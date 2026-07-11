using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Time;
using RentalCommand.Data.Accounting;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Atomically claims a bounded batch of near-expiry accounting connections and refreshes them only
/// after the claim transaction has closed. Its ownership fence is independent from scheduled pulls,
/// so urgent token rotation is not queued behind a long provider import.
/// </summary>
public sealed class AccountingTokenRefreshWorker : EngineWorkerBase
{
    private const int BatchSize = 25;
    private static readonly TimeSpan RefreshHorizon = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(3);
    private readonly string _claimOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:accounting-refresh:{Guid.NewGuid():N}";

    protected override string WorkerName => "AccountingTokenRefreshWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromMinutes(5);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(2);

    public AccountingTokenRefreshWorker(
        IServiceProvider serviceProvider,
        ILogger<AccountingTokenRefreshWorker> logger)
        : base(serviceProvider, logger) { }

    protected override async Task<int> ExecuteCycleAsync(IServiceProvider scoped, CancellationToken ct)
    {
        var tokenService = scoped.GetRequiredService<AccountingTokenService>();
        var claims = scoped.GetRequiredService<IAccountingConnectionClaimStore>();
        var logger = scoped.GetRequiredService<ILogger<AccountingTokenRefreshWorker>>();
        var now = scoped.GetRequiredService<TimeProvider>().UtcNow();

        var batch = await claims.ClaimTokenRefreshAsync(
            _claimOwner, now, now.Add(RefreshHorizon), ClaimLease, BatchSize, ct);
        var refreshed = 0;
        foreach (var claim in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await tokenService.RefreshAsync(claim.Connection, claim.Fence, ct);
                if (result.Outcome == AccountingTokenService.RefreshOutcome.Refreshed)
                {
                    refreshed++;
                }

            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Accounting token refresh failed for connection {ConnectionId}", claim.Connection.Id);
                await claims.MarkTokenRotationRecoveryRequiredAsync(
                    claim.Connection.Id, claim.Fence.ClaimToken,
                    scoped.GetRequiredService<TimeProvider>().UtcNow(), ex.Message, ct);
            }
        }

        return refreshed;
    }
}
