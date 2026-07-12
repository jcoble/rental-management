using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.Services.Esign;
using RentalCommand.Data.Esign;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Finds a bounded batch of native e-sign requests whose signatures are durable but whose executed
/// document still needs to be attached. Selection, ordering, and paging are translated into one SQL
/// query. Each selected document is then processed in its own scope because rendering and blob storage
/// are external side effects and one broken document must not poison the rest of the batch.
/// </summary>
public sealed class NativeEsignReconciliationService
{
    internal const int BatchSize = 20;
    internal static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);

    private readonly string _claimOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:native-esign:{Guid.NewGuid():N}";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NativeEsignReconciliationService> _logger;

    public NativeEsignReconciliationService(
        IServiceScopeFactory scopeFactory,
        ILogger<NativeEsignReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        IReadOnlyList<NativeEsignExecutionClaim> claims;
        await using (var queryScope = _scopeFactory.CreateAsyncScope())
        {
            var store = queryScope.ServiceProvider.GetRequiredService<INativeEsignExecutionClaimStore>();
            claims = await store.ClaimBatchAsync(
                _claimOwner, ClaimLease, BatchSize, ct);
        }

        var completed = 0;
        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var itemScope = _scopeFactory.CreateAsyncScope();
                var execution = itemScope.ServiceProvider.GetRequiredService<INativeEsignExecutionService>();
                if (await execution.FinalizeClaimedAsync(claim.Id, claim.ClaimToken, ct))
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
                _logger.LogError(
                    ex,
                    "Native e-sign reconciliation failed for request {SignatureRequestId}; it remains pending for retry.",
                    claim.Id);
            }
        }

        return completed;
    }
}
