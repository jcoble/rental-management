using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Payments;

namespace RentalCommand.Engine.Services;

/// <inheritdoc cref="IAutopayChargeService"/>
public sealed class AutopayChargeService : IAutopayChargeService
{
    private const int BatchSize = 100;
    private readonly RentalCommandDbContext _db;
    private readonly StripeConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly IWriteExecutor _writes;
    private readonly IAutopayProviderClient _provider;
    private readonly ILogger<AutopayChargeService> _logger;

    public AutopayChargeService(
        RentalCommandDbContext db,
        IOptions<StripeConfig> config,
        TimeProvider timeProvider,
        IWriteExecutor writes,
        ILogger<AutopayChargeService> logger,
        IAutopayProviderClient? provider = null)
    {
        _db = db;
        _config = config.Value;
        _timeProvider = timeProvider;
        _writes = writes;
        _logger = logger;
        _provider = provider ?? new StripeAutopayProviderClient(config);
    }

    public async Task<int> ChargeDueAsync(CancellationToken ct = default)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — autopay charging is a no-op");
            return 0;
        }

        var now = _timeProvider.UtcNow();
        var deferredChargeIds = await ReconcileUnresolvedAsync(now, ct);
        // Eligibility, open-balance filtering, duplicate suppression, ordering, and paging remain
        // in this one translated SQL statement. The bounded materialized rows are remote-work inputs.
        var candidates = await BuildCandidateQuery(_db, deferredChargeIds).ToListAsync(ct);
        return await ChargeCandidatesAsync(candidates, now, ct);
    }

    internal static IQueryable<AutopayChargeCandidate> BuildCandidateQuery(
        RentalCommandDbContext db,
        IReadOnlyCollection<long>? excludedChargeIds = null) =>
        (from balance in db.TenantChargeBalanceProjections.AsNoTracking()
            join charge in db.TenantLedgerEntries.AsNoTracking()
                on new { Id = balance.TenantLedgerEntryId, balance.PortfolioId, balance.TenantAccountId }
                equals new { charge.Id, charge.PortfolioId, charge.TenantAccountId }
            join account in db.TenantAccounts.AsNoTracking()
                on new { Id = charge.TenantAccountId, charge.PortfolioId }
                equals new { account.Id, account.PortfolioId }
            join enrollment in db.TenantAutopayEnrollments.AsNoTracking()
                on new { Id = account.Id, account.PortfolioId }
                equals new { Id = enrollment.TenantAccountId, enrollment.PortfolioId }
            where balance.OpenAmount > 0m
                && charge.EntryType == TenantLedgerEntryType.RentCharge
                && charge.Direction == TenantLedgerDirection.Debit
                && charge.DueOn <= balance.BusinessDate
                && account.ClosedAtUtc == null
                && enrollment.CanceledAtUtc == null
                && enrollment.Provider == "stripe"
                && (excludedChargeIds == null || !excludedChargeIds.Contains(charge.Id))
                && !db.TenantPaymentAttempts.Any(paymentAttempt =>
                    paymentAttempt.Provider == "stripe"
                    && paymentAttempt.ChargeLedgerEntryId == charge.Id
                    && (paymentAttempt.State == TenantPaymentAttemptState.Submitted
                        || paymentAttempt.State == TenantPaymentAttemptState.Prepared
                        || paymentAttempt.State == TenantPaymentAttemptState.Succeeded))
            orderby charge.DueOn, charge.TenantAccountId, charge.Id
         select new AutopayChargeCandidate(
                charge.PortfolioId,
                charge.TenantAccountId,
                charge.Id,
                enrollment.Id,
                enrollment.CreatedByUserId,
                AttemptNonce: db.TenantPaymentAttempts.Count(paymentAttempt =>
                    paymentAttempt.Provider == "stripe"
                    && paymentAttempt.ChargeLedgerEntryId == charge.Id))).Take(BatchSize);

    internal sealed record AutopayChargeCandidate(
        int PortfolioId,
        int TenantAccountId,
        long ChargeLedgerEntryId,
        int EnrollmentId,
        int ActorUserId,
        int AttemptNonce);

    private async Task<int> ChargeCandidatesAsync(
        IReadOnlyList<AutopayChargeCandidate> candidates, DateTime now, CancellationToken ct)
    {
        var charged = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var idempotencyKey = BuildIdempotencyKey(
                candidate.ChargeLedgerEntryId, candidate.AttemptNonce);
            var command = new PrepareProviderPaymentCreateCommand(candidate.PortfolioId,
                    candidate.TenantAccountId, candidate.ChargeLedgerEntryId,
                    candidate.ActorUserId, TenantId: null, candidate.EnrollmentId,
                    "stripe", idempotencyKey, "USD", now);
            var prepared = await _writes.ExecuteAsync(idempotencyKey,
                ProviderPaymentWriteSupport.Write<PrepareProviderPaymentCreateCommand,
                    PrepareProviderPaymentCreateResult>(_db, "payments.provider-create.prepare", command), ct);
            if (prepared.Value.Outcome != PrepareProviderPaymentCreateOutcome.Prepared
                || string.IsNullOrWhiteSpace(prepared.Value.ProviderCustomerId)
                || string.IsNullOrWhiteSpace(prepared.Value.ProviderPaymentMethodId))
                continue;
            if (await ChargePreparedAttemptAsync(prepared.Value.PaymentAttemptId,
                    candidate.PortfolioId, candidate.TenantAccountId, prepared.Value.ProviderCustomerId!,
                    prepared.Value.ProviderPaymentMethodId!, now, ct))
                charged++;
        }

        if (charged > 0)
            _logger.LogInformation("AutopayChargeService initiated {Count} canonical charge(s).", charged);
        return charged;
    }

    internal static string BuildIdempotencyKey(long chargeLedgerEntryId, int attemptNonce) =>
        $"autopay:tenant-charge:{chargeLedgerEntryId}:attempt:{attemptNonce}";

    private async Task<IReadOnlySet<long>> ReconcileUnresolvedAsync(DateTime now, CancellationToken ct)
    {
        var deferredChargeIds = new HashSet<long>();
        var unresolved = await (
            from attempt in _db.TenantPaymentAttempts.AsNoTracking()
            join enrollment in _db.TenantAutopayEnrollments.AsNoTracking()
                on new { attempt.TenantAccountId, attempt.PortfolioId }
                equals new { TenantAccountId = enrollment.TenantAccountId, enrollment.PortfolioId }
            where attempt.Provider == "stripe"
                && attempt.AttemptType == TenantPaymentAttemptType.Charge
                && (attempt.State == TenantPaymentAttemptState.Prepared
                    || attempt.State == TenantPaymentAttemptState.Submitted)
                && enrollment.Provider == "stripe"
                && enrollment.CanceledAtUtc == null
                && enrollment.ProviderCustomerId != null
                && enrollment.ProviderPaymentMethodId != null
            select new AutopayUnresolvedAttempt(attempt,
                enrollment.ProviderCustomerId!, enrollment.ProviderPaymentMethodId!))
            .Take(BatchSize).ToListAsync(ct);

        foreach (var item in unresolved)
        {
            ct.ThrowIfCancellationRequested();
            var providerPayment = await _provider.ReconcileAsync(item.Attempt, ct);
            if (providerPayment is not null)
            {
                deferredChargeIds.Add(item.Attempt.ChargeLedgerEntryId!.Value);
                var submitted = await SubmitAttemptAsync(item.Attempt, now, ct);
                await FinalizeAttemptAsync(item.Attempt, providerPayment, submitted.Value.ProviderFenceToken, ct);
                continue;
            }

            if (item.Attempt.PreparedAtUtc <= now.AddHours(-24))
            {
                var command = new FailProviderPaymentCreateCommand(item.Attempt.PortfolioId,
                        item.Attempt.TenantAccountId, item.Attempt.Id, item.Attempt.Provider,
                        item.Attempt.IdempotencyKey, "PROVIDER_RECONCILE_EXPIRED",
                        "Provider reconciliation found no accepted payment after 24 hours.", now,
                        item.Attempt.ProviderFenceToken);
                var fail = await _writes.ExecuteAsync(item.Attempt.IdempotencyKey,
                    ProviderPaymentWriteSupport.Write<FailProviderPaymentCreateCommand,
                        FailProviderPaymentCreateResult>(_db, "payments.provider-create.fail", command), ct);
                _logger.LogWarning("Autopay attempt {AttemptId} expired without provider reconciliation ({State}).",
                    item.Attempt.Id, fail.Value.State);
                deferredChargeIds.Add(item.Attempt.ChargeLedgerEntryId!.Value);
                continue;
            }

            // A Prepared attempt has never crossed the provider boundary. Reuse its exact key;
            // it must be submitted before the first create, never replaced by a count-derived key.
            if (item.Attempt.State == TenantPaymentAttemptState.Prepared)
                await ChargePreparedAttemptAsync(item.Attempt.Id, item.Attempt.PortfolioId,
                    item.Attempt.TenantAccountId, item.ProviderCustomerId, item.ProviderPaymentMethodId,
                    now, ct);
        }

        return deferredChargeIds;
    }

    private async Task<bool> ChargePreparedAttemptAsync(
        long attemptId, int portfolioId, int tenantAccountId, string customerId,
        string paymentMethodId, DateTime now, CancellationToken ct)
    {
        var attempt = await _db.TenantPaymentAttempts.AsNoTracking()
            .SingleAsync(row => row.Id == attemptId, ct);
        var submitted = await SubmitAttemptAsync(attempt, now, ct);
        if (submitted.Value.Outcome != SubmitProviderPaymentCreateOutcome.Submitted)
            return false;

        attempt = await _db.TenantPaymentAttempts.AsNoTracking()
            .SingleAsync(row => row.Id == attemptId, ct);
        try
        {
            var payment = await _provider.CreateAsync(attempt, customerId, paymentMethodId, ct);
            await FinalizeAttemptAsync(attempt, payment, submitted.Value.ProviderFenceToken, ct);
            _logger.LogInformation(
                "Autopay submitted tenant account {TenantAccountId} charge {ChargeId} through provider object {ProviderPaymentId} ({Status})",
                tenantAccountId, attempt.ChargeLedgerEntryId, payment.ProviderPaymentId, payment.Status);
            return true;
        }
        catch (StripeException ex) when (IsDefinitiveProviderFailure(ex))
        {
            var command = new FailProviderPaymentCreateCommand(portfolioId, tenantAccountId, attempt.Id,
                    "stripe", attempt.IdempotencyKey, ex.StripeError?.Code, ex.Message,
                    _timeProvider.UtcNow(), submitted.Value.ProviderFenceToken);
            await _writes.ExecuteAsync(attempt.IdempotencyKey,
                ProviderPaymentWriteSupport.Write<FailProviderPaymentCreateCommand,
                    FailProviderPaymentCreateResult>(_db, "payments.provider-create.fail", command), ct);
            _logger.LogWarning(ex, "Autopay provider definitively declined attempt {AttemptId}", attempt.Id);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unknown/transport failure is ambiguous. Leave Submitted fenced so the next sweep
            // reconciles by metadata before any new create; never mark it Failed optimistically.
            _logger.LogWarning(ex, "Autopay provider outcome is ambiguous for attempt {AttemptId}; reconciliation will retry.", attempt.Id);
            return false;
        }
    }

    private async Task<AtomicCommandOutcome<SubmitProviderPaymentCreateResult>> SubmitAttemptAsync(
        TenantPaymentAttempt attempt, DateTime now, CancellationToken ct)
    {
        var command = new SubmitProviderPaymentCreateCommand(attempt.PortfolioId,
            attempt.TenantAccountId, attempt.Id, attempt.Provider, attempt.IdempotencyKey, now);
        return await _writes.ExecuteAsync(attempt.IdempotencyKey,
            ProviderPaymentWriteSupport.Write<SubmitProviderPaymentCreateCommand,
                SubmitProviderPaymentCreateResult>(_db, "payments.provider-create.submit", command), ct);
    }

    private async Task FinalizeAttemptAsync(
        TenantPaymentAttempt attempt, AutopayProviderPayment payment, Guid? fence, CancellationToken ct)
    {
        var state = payment.Status switch
        {
            "succeeded" => TenantPaymentAttemptState.Succeeded,
            "canceled" => TenantPaymentAttemptState.Canceled,
            _ => TenantPaymentAttemptState.Submitted,
        };
        var command = new FinalizeProviderPaymentCreateCommand(attempt.PortfolioId, attempt.TenantAccountId,
                attempt.Id, attempt.Provider, attempt.IdempotencyKey, payment.ProviderPaymentId,
                state, null, _timeProvider.UtcNow(), fence);
        await _writes.ExecuteAsync(attempt.IdempotencyKey,
            ProviderPaymentWriteSupport.Write<FinalizeProviderPaymentCreateCommand,
                FinalizeProviderPaymentCreateResult>(_db, "payments.provider-create.finalize", command), ct);
    }

    private static bool IsDefinitiveProviderFailure(StripeException ex) =>
        ex.StripeError?.Type is "card_error" or "invalid_request_error"
        && ex.StripeError?.Code is not "idempotency_key_in_use";

    private sealed record AutopayUnresolvedAttempt(
        TenantPaymentAttempt Attempt, string ProviderCustomerId, string ProviderPaymentMethodId);
}
