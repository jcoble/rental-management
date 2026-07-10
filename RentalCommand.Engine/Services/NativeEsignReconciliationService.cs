using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.Services.Esign;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

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
        int[] requestIds;
        await using (var queryScope = _scopeFactory.CreateAsyncScope())
        {
            var db = queryScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            requestIds = await PendingRequestIdsQuery(db).ToArrayAsync(ct);
        }

        var completed = 0;
        foreach (var requestId in requestIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var itemScope = _scopeFactory.CreateAsyncScope();
                var execution = itemScope.ServiceProvider.GetRequiredService<INativeEsignExecutionService>();
                if (await execution.FinalizePendingAsync(requestId, ct))
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
                    requestId);
            }
        }

        return completed;
    }

    internal static IQueryable<int> PendingRequestIdsQuery(RentalCommandDbContext db) =>
        db.SignatureRequests.AsNoTracking()
            .Where(request => request.Status == SignatureRequestStatus.ExecutionPending)
            .OrderBy(request => request.CreatedAtUtc)
            .ThenBy(request => request.Id)
            .Select(request => request.Id)
            .Take(BatchSize);
}
