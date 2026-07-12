using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Time;
using RentalCommand.Data.Simulation;

namespace RentalCommand.Engine.Workers;

/// <summary>
/// Dev-only (<c>Simulation:Enabled</c>) worker that drains the <c>SimWorkerCommand</c> queue: it claims
/// the oldest <c>Pending</c> command, refreshes the sim clock, runs the matching automation service(s)
/// via <see cref="SimWorkerRegistry"/> in the Engine's native admin-RLS + system-actor scope, then writes
/// <c>{created}</c> (or an error) back for the API long-poll. Fast poll (~500ms) so the replay driver gets
/// near-synchronous behavior. Registered only when simulation is active.
/// </summary>
public sealed class SimWorkerCommandWorker : EngineWorkerBase
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(6);
    private readonly string _claimOwner = $"{Environment.MachineName}:{Environment.ProcessId}:simulation:{Guid.NewGuid():N}";
    private readonly ILogger<SimWorkerCommandWorker> _logger;

    protected override string WorkerName => "SimWorkerCommandWorker";
    protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(500);
    protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(5);

    public SimWorkerCommandWorker(IServiceProvider serviceProvider, ILogger<SimWorkerCommandWorker> logger)
        : base(serviceProvider, logger) => _logger = logger;

    protected override Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
        => ProcessOldestPendingAsync(scopedProvider, cancellationToken);

    /// <summary>
    /// Claims and runs the single oldest <c>Pending</c> command (if any), returning the number of commands
    /// processed this cycle (0 or 1). Internal so integration tests can drive it directly against a real
    /// Postgres — the test harness does not boot the Engine host.
    /// </summary>
    internal async Task<int> ProcessOldestPendingAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var claimStore = scopedProvider.GetRequiredService<ISimWorkerCommandClaimStore>();
        var command = await claimStore.ClaimOldestAsync(_claimOwner, ClaimLease, cancellationToken);
        if (command is null)
            return 0;

        // Ensure the automation services see the latest (driver-frozen) sim clock before running.
        var clockState = scopedProvider.GetService<IClockStateProvider>();
        if (clockState is not null)
            await clockState.RefreshAsync(cancellationToken);

        var registry = scopedProvider.GetRequiredService<SimWorkerRegistry>();

        try
        {
            var created = await registry.RunAsync(command.WorkerKey, scopedProvider, cancellationToken);
            var resultJson = JsonSerializer.Serialize(new { created });
            var completedRealUtc = TimeProvider.System.GetUtcNow().UtcDateTime; // real stamp, hoisted for ExecuteUpdate

            var finalized = await claimStore.MarkDoneAsync(
                command.Id, command.ClaimOwner, command.ClaimToken,
                resultJson, completedRealUtc, cancellationToken);
            if (finalized == 0)
            {
                _logger.LogWarning(
                    "Discarded stale simulation success for command {CommandId}; its claim lease was lost",
                    command.Id);
                return 0;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = ex.Message;
            var completedRealUtc = TimeProvider.System.GetUtcNow().UtcDateTime;

            var finalized = await claimStore.MarkErrorAsync(
                command.Id, command.ClaimOwner, command.ClaimToken,
                error, completedRealUtc, cancellationToken);
            if (finalized == 0)
            {
                _logger.LogWarning(
                    "Discarded stale simulation failure for command {CommandId}; its claim lease was lost",
                    command.Id);
                return 0;
            }
        }

        return 1;
    }
}
