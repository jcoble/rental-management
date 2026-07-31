using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Notifications;

public interface IEngineWorkerHeartbeatStore
{
    Task RecordStartAsync(string workerName, DateTime nowUtc, CancellationToken ct = default);

    Task RecordHeartbeatAsync(
        string workerName,
        DateTime nowUtc,
        long processedDelta,
        string metadataJson,
        CancellationToken ct = default);

    Task RecordErrorAsync(
        string workerName,
        DateTime nowUtc,
        string errorMessage,
        CancellationToken ct = default);
}

internal sealed class EngineWorkerHeartbeatStore : IEngineWorkerHeartbeatStore
{
    private readonly RentalCommandDbContext _db;
    private readonly IInternalSetBasedWriteScope _writeScope;

    public EngineWorkerHeartbeatStore(
        RentalCommandDbContext db,
        IInternalSetBasedWriteScope writeScope)
    {
        _db = db;
        _writeScope = writeScope;
    }

    public async Task RecordStartAsync(string workerName, DateTime nowUtc, CancellationToken ct = default)
    {
        using var lease = _writeScope.BeginWrite("EngineWorkerHeartbeats", InternalWriteOperation.Insert);
        await _db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "EngineWorkerHeartbeats"
                ("WorkerName", "LastHeartbeatUtc", "Status", "StartedAtUtc",
                 "ProcessedCount", "CycleCount")
            VALUES
                ({{workerName}}, {{nowUtc}}, {{(int)EngineWorkerStatus.Running}}, {{nowUtc}}, 0, 0)
            ON CONFLICT ("WorkerName") DO UPDATE SET
                "StartedAtUtc" = EXCLUDED."StartedAtUtc",
                "LastHeartbeatUtc" = EXCLUDED."LastHeartbeatUtc",
                "Status" = EXCLUDED."Status",
                "ProcessedCount" = 0,
                "CycleCount" = 0
            """, ct);
    }

    public async Task RecordHeartbeatAsync(
        string workerName,
        DateTime nowUtc,
        long processedDelta,
        string metadataJson,
        CancellationToken ct = default)
    {
        using var lease = _writeScope.BeginWrite("EngineWorkerHeartbeats", InternalWriteOperation.Insert);
        await _db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "EngineWorkerHeartbeats"
                ("WorkerName", "LastHeartbeatUtc", "Status", "StartedAtUtc",
                 "ProcessedCount", "CycleCount", "Metadata")
            VALUES
                ({{workerName}}, {{nowUtc}}, {{(int)EngineWorkerStatus.Running}}, {{nowUtc}},
                 {{processedDelta}}, 1, CAST({{metadataJson}} AS jsonb))
            ON CONFLICT ("WorkerName") DO UPDATE SET
                "LastHeartbeatUtc" = EXCLUDED."LastHeartbeatUtc",
                "Status" = EXCLUDED."Status",
                "ProcessedCount" = "EngineWorkerHeartbeats"."ProcessedCount" + EXCLUDED."ProcessedCount",
                "CycleCount" = "EngineWorkerHeartbeats"."CycleCount" + 1,
                "Metadata" = EXCLUDED."Metadata",
                "LastErrorMessage" = NULL,
                "LastErrorUtc" = NULL
            """, ct);
    }

    public async Task RecordErrorAsync(
        string workerName,
        DateTime nowUtc,
        string errorMessage,
        CancellationToken ct = default)
    {
        using var lease = _writeScope.BeginWrite("EngineWorkerHeartbeats", InternalWriteOperation.Insert);
        await _db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "EngineWorkerHeartbeats"
                ("WorkerName", "LastHeartbeatUtc", "LastErrorMessage", "LastErrorUtc",
                 "Status", "ProcessedCount", "StartedAtUtc", "CycleCount")
            VALUES
                ({{workerName}}, {{nowUtc}}, {{errorMessage}}, {{nowUtc}},
                 {{(int)EngineWorkerStatus.Error}}, 0, {{nowUtc}}, 0)
            ON CONFLICT ("WorkerName") DO UPDATE SET
                "Status" = EXCLUDED."Status",
                "LastErrorMessage" = EXCLUDED."LastErrorMessage",
                "LastErrorUtc" = EXCLUDED."LastErrorUtc"
            """, ct);
    }
}
