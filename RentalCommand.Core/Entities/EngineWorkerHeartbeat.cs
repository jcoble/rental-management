using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Persisted heartbeat record for each Engine background worker.
/// Written on every poll cycle; used by the watchdog and health check
/// to detect stuck or dead workers.
/// </summary>
public class EngineWorkerHeartbeat
{
    public int Id { get; set; }
    public required string WorkerName { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
    public string? LastErrorMessage { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public EngineWorkerStatus Status { get; set; }
    public long ProcessedCount { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public string? Metadata { get; set; }
    public long CycleCount { get; set; }
}
