using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Claims a bounded batch of connected, pull-enabled accounting connections, then performs provider
/// I/O after the atomic PostgreSQL claim statement has closed.
/// </summary>
public sealed class AccountingPullWorker : EngineWorkerBase
{
    private const int BatchSize = 10;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(12);
    private readonly string _claimOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:accounting-pull:{Guid.NewGuid():N}";

    protected override string WorkerName => "AccountingPullWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromMinutes(15);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(10);

    public AccountingPullWorker(IServiceProvider serviceProvider, ILogger<AccountingPullWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var db = scoped.GetRequiredService<RentalCommandDbContext>();
        var import = scoped.GetRequiredService<AccountingImportService>();
        var claims = scoped.GetRequiredService<IAccountingConnectionClaimStore>();
        var logger = scoped.GetRequiredService<ILogger<AccountingPullWorker>>();
        var now = scoped.GetRequiredService<TimeProvider>().UtcNow();

        var batch = await claims.ClaimPullAsync(_claimOwner, now, ClaimLease, BatchSize, ct);
        var processed = 0;
        foreach (var claim in batch)
        {
            ct.ThrowIfCancellationRequested();
            // The claim statement returned the full connection snapshot, so there is no per-row reload.
            // Attach only after the claim transaction is closed; provider calls therefore never run in it.
            db.Attach(claim.Connection);
            try
            {
                var summary = await import.ImportAsync(claim.Connection, since: null, ct, claim.Fence);
                processed += summary.PaymentsImported + summary.ExpensesImported;

                db.ChangeTracker.Clear();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Accounting pull failed for connection {ConnectionId}", claim.Connection.Id);
                db.ChangeTracker.Clear();
                var failedAt = scoped.GetRequiredService<TimeProvider>().UtcNow();
                await claims.MarkPullFailedAsync(
                    claim.Connection.Id, claim.Fence.ClaimToken, failedAt, failedAt.AddMinutes(15), ex.Message, ct);
            }
        }

        return processed;
    }
}
