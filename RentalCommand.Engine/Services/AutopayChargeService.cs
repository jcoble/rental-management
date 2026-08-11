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

namespace RentalCommand.Engine.Services;

/// <inheritdoc cref="IAutopayChargeService"/>
public sealed class AutopayChargeService : IAutopayChargeService
{
    private const int BatchSize = 100;
    private readonly RentalCommandDbContext _db;
    private readonly StripeConfig _config;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomicUnitOfWork;
    private readonly ILogger<AutopayChargeService> _logger;

    private static readonly AtomicJsonResultCodec<PrepareProviderPaymentCreateResult> PrepareCodec =
        new("prepare-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<FinalizeProviderPaymentCreateResult> FinalizeCodec =
        new("finalize-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<FailProviderPaymentCreateResult> FailCodec =
        new("fail-provider-payment-create-result.v1");

    public AutopayChargeService(
        RentalCommandDbContext db,
        IOptions<StripeConfig> config,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomicUnitOfWork,
        ILogger<AutopayChargeService> logger)
    {
        _db = db;
        _config = config.Value;
        _timeProvider = timeProvider;
        _atomicUnitOfWork = atomicUnitOfWork;
        _logger = logger;
    }

    public async Task<int> ChargeDueAsync(CancellationToken ct = default)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Stripe is not configured — autopay charging is a no-op");
            return 0;
        }

        var now = _timeProvider.UtcNow();
        // Eligibility, open-balance filtering, duplicate suppression, ordering, and paging remain
        // in this one translated SQL statement. The bounded materialized rows are remote-work inputs.
        var candidates = await BuildCandidateQuery(_db).ToListAsync(ct);
        return await ChargeCandidatesAsync(candidates, now, ct);
    }

    internal static IQueryable<AutopayChargeCandidate> BuildCandidateQuery(RentalCommandDbContext db) =>
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
                && !db.TenantPaymentAttempts.Any(paymentAttempt =>
                    paymentAttempt.Provider == "stripe"
                    && paymentAttempt.ChargeLedgerEntryId == charge.Id
                    && (paymentAttempt.State == TenantPaymentAttemptState.Submitted
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
            var prepared = await _atomicUnitOfWork.ExecuteAsync(
                new AtomicCommandIdentity("payments.provider-create.prepare", idempotencyKey),
                new PrepareProviderPaymentCreateCommand(candidate.PortfolioId,
                    candidate.TenantAccountId, candidate.ChargeLedgerEntryId,
                    candidate.ActorUserId, TenantId: null, candidate.EnrollmentId,
                    "stripe", idempotencyKey, "USD", now),
                PrepareCodec,
                ct);
            if (prepared.Value.Outcome != PrepareProviderPaymentCreateOutcome.Prepared
                || string.IsNullOrWhiteSpace(prepared.Value.ProviderCustomerId)
                || string.IsNullOrWhiteSpace(prepared.Value.ProviderPaymentMethodId))
                continue;

            try
            {
                var intent = await new PaymentIntentService().CreateAsync(new PaymentIntentCreateOptions
                {
                    Amount = (long)(prepared.Value.Amount * 100m),
                    Currency = prepared.Value.Currency.ToLowerInvariant(),
                    Customer = prepared.Value.ProviderCustomerId,
                    PaymentMethod = prepared.Value.ProviderPaymentMethodId,
                    Confirm = true,
                    OffSession = true,
                    Metadata = new Dictionary<string, string>
                    {
                        ["portfolioId"] = candidate.PortfolioId.ToString(),
                        ["tenantAccountId"] = candidate.TenantAccountId.ToString(),
                        ["chargeLedgerEntryId"] = candidate.ChargeLedgerEntryId.ToString(),
                        ["paymentAttemptId"] = prepared.Value.PaymentAttemptId.ToString(),
                        ["autopay"] = "1",
                    },
                }, new RequestOptions
                {
                    ApiKey = _config.SecretKey,
                    IdempotencyKey = idempotencyKey,
                }, ct);

                var state = intent.Status switch
                {
                    "succeeded" => TenantPaymentAttemptState.Succeeded,
                    "canceled" => TenantPaymentAttemptState.Canceled,
                    _ => TenantPaymentAttemptState.Submitted,
                };
                await _atomicUnitOfWork.ExecuteAsync(
                    new AtomicCommandIdentity("payments.provider-create.finalize", idempotencyKey),
                    new FinalizeProviderPaymentCreateCommand(candidate.PortfolioId,
                        candidate.TenantAccountId, prepared.Value.PaymentAttemptId, "stripe",
                        idempotencyKey, intent.Id, state, null, _timeProvider.UtcNow()),
                    FinalizeCodec,
                    ct);
                charged++;
                _logger.LogInformation(
                    "Autopay submitted tenant account {TenantAccountId} charge {ChargeId} through PaymentIntent {IntentId} ({Status})",
                    candidate.TenantAccountId, candidate.ChargeLedgerEntryId, intent.Id, intent.Status);
            }
            catch (StripeException ex)
            {
                await _atomicUnitOfWork.ExecuteAsync(
                    new AtomicCommandIdentity("payments.provider-create.fail", idempotencyKey),
                    new FailProviderPaymentCreateCommand(candidate.PortfolioId,
                        candidate.TenantAccountId, prepared.Value.PaymentAttemptId, "stripe",
                        idempotencyKey, ex.StripeError?.Code, ex.Message, _timeProvider.UtcNow()),
                    FailCodec,
                    ct);
                _logger.LogWarning(ex,
                    "Autopay provider call failed for tenant account {TenantAccountId} charge {ChargeId}",
                    candidate.TenantAccountId, candidate.ChargeLedgerEntryId);
            }
        }

        if (charged > 0)
            _logger.LogInformation("AutopayChargeService initiated {Count} canonical charge(s).", charged);
        return charged;
    }

    internal static string BuildIdempotencyKey(long chargeLedgerEntryId, int attemptNonce) =>
        $"autopay:tenant-charge:{chargeLedgerEntryId}:attempt:{attemptNonce}";
}
