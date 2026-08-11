using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Owns durable reconciliation after a browser or webhook disappears. Candidate selection is one
/// translated, ordered, bounded query; every Stripe call happens before a separate atomic command.
/// </summary>
public sealed class InteractivePaymentReconciliationService
{
    private static readonly AtomicJsonResultCodec<SubmitProviderPaymentCreateResult> SubmitCodec =
        new("submit-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<FinalizeProviderPaymentCreateResult> FinalizeCodec =
        new("finalize-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<FailProviderPaymentCreateResult> FailCodec =
        new("fail-provider-payment-create-result.v1");
    private static readonly AtomicJsonResultCodec<ScheduleProviderPaymentReconciliationResult>
        ScheduleCodec = new("schedule-provider-payment-reconciliation-result.v1");

    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IInteractivePaymentProviderClient _provider;
    private readonly TimeProvider _timeProvider;
    private readonly InteractivePaymentReconciliationOptions _options;
    private readonly ILogger<InteractivePaymentReconciliationService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public InteractivePaymentReconciliationService(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IInteractivePaymentProviderClient provider,
        TimeProvider timeProvider,
        IOptions<InteractivePaymentReconciliationOptions> options,
        ILogger<InteractivePaymentReconciliationService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _atomic = atomic;
        _provider = provider;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();
        var batchSize = Math.Max(1, _options.BatchSize);

        // Keep filtering, ordering, and paging in this one SQL-translated query. No candidate
        // materialization occurs before the unresolved-work predicates are applied.
        var candidates = await _db.TenantPaymentAttempts.AsNoTracking()
            .Where(attempt =>
                attempt.AttemptType == TenantPaymentAttemptType.Charge
                || attempt.AttemptType == TenantPaymentAttemptType.Verification)
            .Where(attempt => attempt.State == TenantPaymentAttemptState.Prepared
                || attempt.State == TenantPaymentAttemptState.Submitted)
            .Where(attempt => attempt.NextAttemptAtUtc == null
                || attempt.NextAttemptAtUtc <= now)
            .Where(attempt => attempt.ClaimToken == null
                || attempt.ClaimExpiresAtUtc == null
                || attempt.ClaimExpiresAtUtc <= now)
            .OrderBy(attempt => attempt.NextAttemptAtUtc ?? attempt.PreparedAtUtc)
            .ThenBy(attempt => attempt.PreparedAtUtc)
            .ThenBy(attempt => attempt.Id)
            .Take(batchSize)
            .Select(attempt => new Candidate(
                attempt.Id,
                attempt.PortfolioId,
                attempt.TenantAccountId,
                attempt.Provider,
                attempt.IdempotencyKey,
                attempt.AttemptType,
                attempt.State,
                attempt.Amount,
                attempt.Currency,
                attempt.ChargeLedgerEntryId,
                attempt.ProviderObjectId,
                attempt.ProviderFenceToken,
                attempt.PreparedAtUtc))
            .ToListAsync(ct);

        var processed = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await ReconcileCandidateInFreshScopeAsync(candidate, now, ct))
                    processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Interactive payment reconciliation could not create an isolated scope for attempt {PaymentAttemptId}; continuing the batch.",
                    candidate.PaymentAttemptId);
            }
        }

        return processed;
    }

    private async Task<bool> ReconcileCandidateInFreshScopeAsync(
        Candidate candidate, DateTime now, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var isolated = new InteractivePaymentReconciliationService(
            scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(),
            scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IInteractivePaymentProviderClient>(),
            _timeProvider,
            Options.Create(_options),
            _logger,
            _scopeFactory);
        return await isolated.ReconcileCandidateWithRecoveryAsync(candidate, now, ct);
    }

    private async Task<bool> ReconcileCandidateWithRecoveryAsync(
        Candidate candidate, DateTime now, CancellationToken ct)
    {
        try
        {
            await ReconcileCandidateAsync(candidate, now, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderReconciliationFailureException ex)
        {
            var providerFailure = ex.InnerException as InteractiveProviderException;
            _logger.LogWarning(ex,
                "Interactive payment provider reconciliation failed for attempt {PaymentAttemptId}; retaining its fence.",
                ex.Candidate.PaymentAttemptId);
            await TryScheduleRetryAsync(
                ex.Candidate,
                now,
                providerFailure is null
                    ? "PROVIDER_RECONCILE_RETRY"
                    : "PROVIDER_RECONCILE_TRANSPORT",
                ex.InnerException?.Message ?? ex.Message,
                ct);
            return false;
        }
        catch (InteractiveProviderException ex)
        {
            _logger.LogWarning(ex,
                "Interactive payment provider reconciliation failed for attempt {PaymentAttemptId}; retaining its fence.",
                candidate.PaymentAttemptId);
            await TryScheduleRetryAsync(candidate, now, "PROVIDER_RECONCILE_TRANSPORT", ex.Message, ct);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Interactive payment reconciliation failed for attempt {PaymentAttemptId}; retaining its fence.",
                candidate.PaymentAttemptId);
            await TryScheduleRetryAsync(candidate, now, "PROVIDER_RECONCILE_RETRY", ex.Message, ct);
            return false;
        }
    }

    private async Task TryScheduleRetryAsync(
        Candidate candidate,
        DateTime now,
        string failureCode,
        string failureReason,
        CancellationToken ct)
    {
        try
        {
            await ScheduleRetryAsync(candidate, now, failureCode, failureReason, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Interactive payment retry scheduling failed for attempt {PaymentAttemptId}; the candidate remains eligible for a later cycle.",
                candidate.PaymentAttemptId);
        }
    }

    private async Task ReconcileCandidateAsync(Candidate candidate, DateTime now, CancellationToken ct)
    {
        var current = candidate;
        if (current.State == TenantPaymentAttemptState.Submitted
            && current.ProviderFenceToken is null)
        {
            current = await EnsureSubmittedFenceAsync(current, now, ct);
            if (current.State != TenantPaymentAttemptState.Submitted)
                return;
        }

        // This remote call is deliberately outside every atomic command transaction.
        InteractiveProviderObject? provider;
        try
        {
            provider = await _provider.ReconcileAsync(ToProviderAttempt(current), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Preserve the repaired fence when a legacy Submitted row fails at the provider
            // boundary; the original candidate may still carry a null legacy fence.
            throw new ProviderReconciliationFailureException(current, ex);
        }
        if (provider is null)
        {
            await ScheduleRetryAsync(current, now, "PROVIDER_RECONCILE_UNKNOWN",
                "Provider reconciliation returned no definitive result.", ct);
            return;
        }

        if (provider.ConfirmedNoProviderObject)
        {
            if (now - candidate.PreparedAtUtc >= _options.Expiration)
            {
                await ExpireWithoutProviderObjectAsync(current, now, ct);
            }
            else
            {
                await ScheduleRetryAsync(current, now, "PROVIDER_RECONCILE_NO_OBJECT",
                    "Provider confirmed that no object exists before the expiry policy.", ct);
            }

            return;
        }

        var providerObjectId = ProviderObjectId(provider);
        if (string.IsNullOrWhiteSpace(providerObjectId))
        {
            await ScheduleRetryAsync(current, now, "PROVIDER_RECONCILE_NO_ID",
                "Provider reconciliation returned an object without a durable identity.", ct);
            return;
        }

        var state = MapProviderState(provider.Status);
        if (current.State == TenantPaymentAttemptState.Prepared)
        {
            var submitted = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("payments.provider-create.submit", current.IdempotencyKey),
                new SubmitProviderPaymentCreateCommand(
                    current.PortfolioId, current.TenantAccountId, current.PaymentAttemptId,
                    current.Provider, current.IdempotencyKey, now),
                SubmitCodec, ct);
            var result = submitted.Value;
            if (result.Outcome is SubmitProviderPaymentCreateOutcome.Canceled
                or SubmitProviderPaymentCreateOutcome.Failed
                or SubmitProviderPaymentCreateOutcome.Succeeded
                or SubmitProviderPaymentCreateOutcome.NotFound)
                return;
            current = current with
            {
                State = result.State,
                ProviderFenceToken = result.ProviderFenceToken,
                ProviderObjectId = result.ProviderPaymentId ?? current.ProviderObjectId,
            };
            if (current.State != TenantPaymentAttemptState.Submitted)
                return;
        }

        await _atomic.ExecuteAsync(
            new AtomicCommandIdentity($"payments.provider-create.finalize:{state}",
                current.IdempotencyKey),
            new FinalizeProviderPaymentCreateCommand(
                current.PortfolioId, current.TenantAccountId, current.PaymentAttemptId,
                current.Provider, current.IdempotencyKey, providerObjectId, state, null, now,
                current.ProviderFenceToken),
            FinalizeCodec, ct);

        if (state == TenantPaymentAttemptState.Submitted)
        {
            await ScheduleRetryAsync(current, now, "PROVIDER_RECONCILE_OPEN",
                "Provider object remains open or requires further action; durable fence retained.", ct);
        }
    }

    private async Task<Candidate> EnsureSubmittedFenceAsync(
        Candidate candidate, DateTime now, CancellationToken ct)
    {
        var ensured = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-reconciliation.ensure-fence",
                candidate.IdempotencyKey),
            new SubmitProviderPaymentCreateCommand(
                candidate.PortfolioId, candidate.TenantAccountId, candidate.PaymentAttemptId,
                candidate.Provider, candidate.IdempotencyKey, now),
            SubmitCodec, ct);
        return candidate with
        {
            State = ensured.Value.State,
            ProviderFenceToken = ensured.Value.ProviderFenceToken,
            ProviderObjectId = ensured.Value.ProviderPaymentId ?? candidate.ProviderObjectId,
        };
    }

    private async Task ExpireWithoutProviderObjectAsync(
        Candidate candidate, DateTime now, CancellationToken ct)
    {
        await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-reconciliation.expire",
                $"{candidate.PaymentAttemptId}:{candidate.IdempotencyKey}"),
            new FailProviderPaymentCreateCommand(
                candidate.PortfolioId, candidate.TenantAccountId, candidate.PaymentAttemptId,
                candidate.Provider, candidate.IdempotencyKey, "PROVIDER_RECONCILE_EXPIRED",
                "Provider reconciliation found no accepted payment after the expiry policy.",
                now, candidate.ProviderFenceToken),
            FailCodec, ct);
    }

    private async Task ScheduleRetryAsync(
        Candidate candidate,
        DateTime now,
        string failureCode,
        string failureReason,
        CancellationToken ct)
    {
        var delay = _options.RetryDelay > TimeSpan.Zero
            ? _options.RetryDelay
            : TimeSpan.FromMilliseconds(1);
        var nextAttemptAtUtc = now + delay;
        await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("payments.provider-reconciliation.schedule",
                $"{candidate.PaymentAttemptId}:{failureCode}:{nextAttemptAtUtc.Ticks}"),
            new ScheduleProviderPaymentReconciliationCommand(
                candidate.PortfolioId, candidate.TenantAccountId, candidate.PaymentAttemptId,
                candidate.Provider, candidate.IdempotencyKey, candidate.ProviderFenceToken,
                nextAttemptAtUtc, failureCode, Truncate(failureReason, 2000), now),
            ScheduleCodec, ct);
    }

    private static InteractiveProviderAttempt ToProviderAttempt(Candidate candidate) =>
        new(candidate.PaymentAttemptId, candidate.Provider, candidate.IdempotencyKey,
            candidate.AttemptType, candidate.ProviderObjectId, candidate.PortfolioId,
            candidate.TenantAccountId, candidate.Amount, candidate.Currency);

    private static string ProviderObjectId(InteractiveProviderObject provider) =>
        provider.ProviderPaymentId ?? provider.PaymentIntentId ?? provider.CheckoutSessionId ?? string.Empty;

    private static TenantPaymentAttemptState MapProviderState(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "succeeded" or "paid" or "complete" => TenantPaymentAttemptState.Succeeded,
            "canceled" or "cancelled" or "expired" => TenantPaymentAttemptState.Canceled,
            "failed" or "payment_failed" or "setup_failed" => TenantPaymentAttemptState.Failed,
            _ => TenantPaymentAttemptState.Submitted,
        };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record Candidate(
        long PaymentAttemptId,
        int PortfolioId,
        int TenantAccountId,
        string Provider,
        string IdempotencyKey,
        TenantPaymentAttemptType AttemptType,
        TenantPaymentAttemptState State,
        decimal Amount,
        string Currency,
        long? ChargeLedgerEntryId,
        string? ProviderObjectId,
        Guid? ProviderFenceToken,
        DateTime PreparedAtUtc);

    private sealed class ProviderReconciliationFailureException(
        Candidate candidate, Exception innerException)
        : Exception(innerException.Message, innerException)
    {
        public Candidate Candidate { get; } = candidate;
    }
}
