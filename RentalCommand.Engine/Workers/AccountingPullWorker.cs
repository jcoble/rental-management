using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Engine.Writes;

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
        var writes = scoped.GetRequiredService<IJobStepWriteExecutor>();
        var logger = scoped.GetRequiredService<ILogger<AccountingPullWorker>>();
        var batch = await claims.ClaimPullAsync(_claimOwner, ClaimLease, BatchSize, ct);
        var processed = 0;
        foreach (var claim in batch)
        {
            ct.ThrowIfCancellationRequested();
            // The claim statement returned the full connection snapshot, so there is no per-row reload.
            // Attach only after the claim transaction is closed; provider calls therefore never run in it.
            db.Attach(claim.Connection);
            try
            {
                var command = await import.PullAsync(claim.Connection, since: null, ct, claim.Fence);
                var identity = new AtomicCommandIdentity(
                    "accounting.pull.apply",
                    $"{claim.Connection.Id}:{claim.Fence.ClaimToken:N}:{command.ProviderBatchIdentity}");
                var handler = new ApplyAccountingPullResultHandler(db);
                var outcome = await writes.ExecuteAsync(identity.IdempotencyKey,
                    AccountingWriteSupport.Write(
                        command, handler.ExecuteAsync, handler.AuthorizeAsync), ct);
                processed += outcome.Value.PaymentsImported + outcome.Value.ExpensesImported;

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
                await claims.MarkPullFailedAsync(
                    claim.Connection.Id, claim.Fence.ClaimToken, TimeSpan.FromMinutes(15), ex.Message, ct);
            }
        }

        return processed;
    }
}
