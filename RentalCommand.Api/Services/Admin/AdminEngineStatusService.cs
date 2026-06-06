using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Admin;

// =====================================================================
// Interface
// =====================================================================

/// <summary>
/// Reads the Engine's persisted worker heartbeats (written by
/// <c>RentalCommand.Engine.Services.EngineStatusReporter</c>) and derives an at-a-glance
/// health picture for the admin Engine Health page. The Engine has no HTTP port, so the
/// database heartbeat table is the contract between the two processes — this service is the
/// API-side reader of that table. Also surfaces the active LLM provider/model (read from
/// <c>Assistant:*</c> config) so stale provider configuration is visible at a glance.
/// </summary>
public interface IAdminEngineStatusService
{
    Task<EngineStatusResponse> GetEngineStatusAsync(CancellationToken ct = default);
}

// =====================================================================
// DTOs
// =====================================================================

public record EngineStatusResponse(
    string OverallStatus,
    EngineInstanceDto? Instance,
    List<WorkerStatusDto> Workers,
    LlmProviderDto Llm
);

public record WorkerStatusDto(
    string WorkerName,
    string Status,
    string HealthState,
    DateTime LastHeartbeatUtc,
    DateTime StartedAtUtc,
    long ProcessedCount,
    long CycleCount,
    string? LastErrorMessage,
    DateTime? LastErrorUtc,
    double HeartbeatSecondsAgo,
    double? ErrorSecondsAgo
);

public record EngineInstanceDto(
    int? Pid,
    string? Hostname,
    string? Version,
    bool AdvisoryLockHeld,
    bool LockContested,
    double UptimeSeconds
);

/// <summary>The active LLM provider + model the Engine/API will use for scan extraction.</summary>
public record LlmProviderDto(
    string Provider,
    string ModelId
);

// =====================================================================
// Implementation
// =====================================================================

public class AdminEngineStatusService : IAdminEngineStatusService
{
    private readonly RentalCommandDbContext _db;
    private readonly AssistantConfig _assistant;

    public AdminEngineStatusService(RentalCommandDbContext db, IOptions<AssistantConfig> assistant)
    {
        _db = db;
        _assistant = assistant.Value;
    }

    public async Task<EngineStatusResponse> GetEngineStatusAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Only surface heartbeats for workers we know about (filters out renamed/retired rows).
        var workers = (await _db.EngineWorkerHeartbeats
                .AsNoTracking()
                .ToListAsync(ct))
            .Where(h => WorkerHealthThresholds.IsKnownWorker(h.WorkerName))
            .OrderBy(h => h.WorkerName)
            .ToList();

        var workerDtos = workers.Select(w => new WorkerStatusDto(
            w.WorkerName,
            w.Status.ToString(),
            ComputeWorkerHealthState(w, now),
            w.LastHeartbeatUtc,
            w.StartedAtUtc,
            w.ProcessedCount,
            w.CycleCount,
            w.LastErrorMessage,
            w.LastErrorUtc,
            (now - w.LastHeartbeatUtc).TotalSeconds,
            w.LastErrorUtc.HasValue ? (now - w.LastErrorUtc.Value).TotalSeconds : null
        )).ToList();

        var overallStatus = DetermineOverallStatus(workers, now);
        var instance = ParseInstanceInfo(workers, now);

        var llm = new LlmProviderDto(
            string.IsNullOrWhiteSpace(_assistant.Provider) ? "openai" : _assistant.Provider,
            _assistant.ModelId
        );

        return new EngineStatusResponse(overallStatus, instance, workerDtos, llm);
    }

    // -----------------------------------------------------------------
    // Health computation (mirrors EngineWorkerHealthCheck escalation,
    // expressed as Healthy / Degraded / Stale / Down for the UI).
    // -----------------------------------------------------------------

    private static string ComputeWorkerHealthState(EngineWorkerHeartbeat worker, DateTime now)
    {
        var staleness = (now - worker.LastHeartbeatUtc).TotalSeconds;
        var t = WorkerHealthThresholds.GetThresholds(worker.WorkerName);

        // A self-reported Error is at least Degraded even if the heartbeat is fresh.
        if (staleness > t.Down) return "Down";
        if (staleness > t.Degraded) return "Stale";
        if (worker.Status == EngineWorkerStatus.Error) return "Degraded";
        if (staleness > t.Healthy) return "Degraded";
        return "Healthy";
    }

    private static string DetermineOverallStatus(List<EngineWorkerHeartbeat> workers, DateTime now)
    {
        if (workers.Count == 0)
            return "Down";

        // Overall health = worst individual worker health.
        var states = workers.Select(w => ComputeWorkerHealthState(w, now)).ToList();
        if (states.Any(s => s == "Down")) return "Down";
        if (states.Any(s => s == "Stale")) return "Stale";
        if (states.Any(s => s == "Degraded")) return "Degraded";
        return "Healthy";
    }

    private static EngineInstanceDto? ParseInstanceInfo(List<EngineWorkerHeartbeat> workers, DateTime now)
    {
        if (workers.Count == 0)
            return null;

        var earliestStart = workers.Min(w => w.StartedAtUtc);
        var uptimeSeconds = (now - earliestStart).TotalSeconds;

        // The most recently updated heartbeat carries the freshest instance metadata.
        var metadataJson = workers
            .Where(w => !string.IsNullOrEmpty(w.Metadata))
            .OrderByDescending(w => w.LastHeartbeatUtc)
            .Select(w => w.Metadata)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(metadataJson))
            return new EngineInstanceDto(null, null, null, false, false, uptimeSeconds);

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            return new EngineInstanceDto(
                root.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var pidVal) ? pidVal : null,
                root.TryGetProperty("hostname", out var host) ? host.GetString() : null,
                root.TryGetProperty("version", out var ver) ? ver.GetString() : null,
                root.TryGetProperty("advisoryLockHeld", out var held) && held.ValueKind == JsonValueKind.True,
                root.TryGetProperty("lockContested", out var contested) && contested.ValueKind == JsonValueKind.True,
                uptimeSeconds
            );
        }
        catch
        {
            return new EngineInstanceDto(null, null, null, false, false, uptimeSeconds);
        }
    }
}
