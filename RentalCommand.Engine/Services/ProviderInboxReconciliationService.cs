using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Payments;
using RentalCommand.Data;
using RentalCommand.Data.Payments;
using RentalCommand.Engine.Writes;

namespace RentalCommand.Engine.Services;

public sealed class ProviderInboxReconciliationService
{
    internal const int BatchSize = 20;
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private readonly IProviderInboxClaimStore _claimStore;
    private readonly RentalCommandDbContext _db;
    private readonly IJobStepWriteExecutor _writes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProviderInboxReconciliationService> _logger;

    public ProviderInboxReconciliationService(
        IProviderInboxClaimStore claimStore,
        RentalCommandDbContext db,
        IJobStepWriteExecutor writes,
        TimeProvider timeProvider,
        ILogger<ProviderInboxReconciliationService> logger)
    {
        _claimStore = claimStore;
        _db = db;
        _writes = writes;
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
                var key = $"{claim.Id}:{claim.ClaimToken:N}";
                var command = new ReconcileClaimedProviderPaymentEventCommand(
                        claim.Id,
                        claim.ClaimOwner,
                        claim.ClaimToken,
                        reconciledAt);
                var outcome = await _writes.ExecuteAsync(key,
                    ProviderPaymentWriteSupport.Write<ReconcileClaimedProviderPaymentEventCommand,
                        ReconcileClaimedProviderPaymentEventResult>(_db,
                            "payments.provider-inbox.reconcile", command), ct);
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
