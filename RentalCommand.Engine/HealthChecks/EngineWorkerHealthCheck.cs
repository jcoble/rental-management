using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Engine.HealthChecks;

/// <summary>
/// Reports the aggregate health of all Engine workers based on their persisted
/// heartbeat records. Used by <c>AddHealthChecks().AddCheck&lt;...&gt;()</c> in Program.cs.
///
/// The Engine has no HTTP port, so this health check is consumed by the internal
/// watchdog/alerting — the primary observable signal is the watchdog WARNING logs.
/// </summary>
public class EngineWorkerHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _serviceProvider;

    public EngineWorkerHealthCheck(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();

            // Filter to known workers DB-side (WHERE WorkerName IN (...)) rather than loading every
            // heartbeat row and filtering in memory.
            var knownWorkerNames = WorkerHealthThresholds.KnownWorkerNames;
            var knownWorkerHeartbeats = await dbContext.EngineWorkerHeartbeats
                .Where(hb => knownWorkerNames.Contains(hb.WorkerName))
                .ToListAsync(cancellationToken);

            if (knownWorkerHeartbeats.Count == 0)
                return HealthCheckResult.Unhealthy("No known engine worker heartbeats found.");

            var now = DateTime.UtcNow;
            var worstStatus = HealthStatus.Healthy;
            var descriptions = new List<string>();

            foreach (var hb in knownWorkerHeartbeats)
            {
                var age = (now - hb.LastHeartbeatUtc).TotalSeconds;
                var t = WorkerHealthThresholds.GetThresholds(hb.WorkerName);

                if (hb.Status == EngineWorkerStatus.Error || age > t.Down)
                {
                    worstStatus = HealthStatus.Unhealthy;
                    descriptions.Add($"{hb.WorkerName}: DOWN ({age:F0}s ago)");
                }
                else if (age > t.Degraded)
                {
                    if (worstStatus != HealthStatus.Unhealthy)
                        worstStatus = HealthStatus.Degraded;
                    descriptions.Add($"{hb.WorkerName}: degraded ({age:F0}s ago)");
                }
            }

            var desc = descriptions.Count > 0
                ? string.Join("; ", descriptions)
                : "All workers healthy";

            return worstStatus switch
            {
                HealthStatus.Unhealthy => HealthCheckResult.Unhealthy(desc),
                HealthStatus.Degraded  => HealthCheckResult.Degraded(desc),
                _                      => HealthCheckResult.Healthy(desc)
            };
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Health check failed", ex);
        }
    }
}
