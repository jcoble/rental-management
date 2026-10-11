using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

/// <summary>Kernel-owned transaction lock implementation for cross-row aggregate decisions.</summary>
internal sealed class AtomicLockingPersistence
{
    private const int NamespaceKey = 0x52434D44; // "RCMD"
    private readonly RentalCommandDbContext _db;

    public AtomicLockingPersistence(RentalCommandDbContext db) => _db = db;

    public async Task AcquireAsync(
        string lockNamespace,
        int aggregateId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockNamespace);
        if (aggregateId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregateId), "An exact aggregate lock is required.");
        }

        if (_db.Database.IsNpgsql())
        {
            var namespaceKey = StableNamespaceKey(lockNamespace);
            // Two-int advisory locks avoid unstable runtime string hashes. The lock is released by the
            // atomic owner's commit/rollback and therefore cannot leak beyond the command attempt.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({namespaceKey}, {aggregateId})",
                ct);
        }
        // SQLite is used only by focused tests and serializes writes at the database level. Production
        // PostgreSQL uses the explicit aggregate lock above; concurrency proof runs against PostgreSQL.
    }

    public async Task AcquireAsync(
        string lockNamespace,
        Guid aggregateId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockNamespace);
        if (aggregateId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregateId), "An exact aggregate lock is required.");
        }

        if (_db.Database.IsNpgsql())
        {
            var namespaceBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lockNamespace));
            var input = new byte[48];
            namespaceBytes.CopyTo(input, 0);
            aggregateId.TryWriteBytes(input.AsSpan(32));
            var digest = SHA256.HashData(input);
            var lockKey = BinaryPrimitives.ReadInt64BigEndian(digest);

            // Hashing the fixed resource and GUID into PostgreSQL's bigint advisory-lock namespace
            // can only introduce harmless over-serialization on a collision; it cannot allow two
            // commands for the same aggregate to use different locks.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({lockKey})",
                ct);
        }
    }

    private static int StableNamespaceKey(string lockNamespace)
    {
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lockNamespace));
        return NamespaceKey ^ BinaryPrimitives.ReadInt32BigEndian(digest);
    }
}
