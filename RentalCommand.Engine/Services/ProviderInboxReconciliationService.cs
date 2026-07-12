using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Payments;

namespace RentalCommand.Engine.Services;

public sealed class ProviderInboxReconciliationService
{
    internal const int BatchSize = 20;
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly AtomicJsonResultCodec<ReconcileClaimedProviderPaymentEventResult> ResultCodec =
        new("reconcile-claimed-provider-payment-event-result.v1");

    private readonly IProviderInboxClaimStore _claimStore;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProviderInboxReconciliationService> _logger;

    public ProviderInboxReconciliationService(
        IProviderInboxClaimStore claimStore,
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider,
        ILogger<ProviderInboxReconciliationService> logger)
    {
        _claimStore = claimStore;
        _atomic = atomic;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        var claims = await _claimStore.ClaimAsync(owner, LeaseDuration, BatchSize, ct);
        var completed = 0;

        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reconciledAt = _timeProvider.GetUtcNow().UtcDateTime;
                var outcome = await _atomic.ExecuteAsync(
                    new AtomicCommandIdentity(
                        "payments.provider-inbox.reconcile",
                        $"{claim.Id}:{claim.ClaimToken:N}"),
                    new ReconcileClaimedProviderPaymentEventCommand(
                        claim.Id,
                        claim.ClaimOwner,
                        claim.ClaimToken,
                        reconciledAt),
                    ResultCodec,
                    ct);
                if (outcome.Value.Outcome == ReconcileProviderPaymentEventOutcome.Applied)
                {
                    completed++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The lease is deliberately left intact. A different worker can reclaim only after
                // expiry, and its new token fences this worker from later completion.
                _logger.LogError(ex, "Provider inbox reconciliation failed for event {ProviderInboxEventId}.", claim.Id);
            }
        }

        return completed;
    }
}
