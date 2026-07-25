using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

/// <summary>Kernel-owned transaction lock implementation for cross-row aggregate decisions.</summary>
internal sealed class AtomicLockingPersistence : IAtomicLockingPersistence
{
    private const int NamespaceKey = 0x52434D44; // "RCMD"
    private readonly RentalCommandDbContext _db;

    public AtomicLockingPersistence(RentalCommandDbContext db) => _db = db;

    public async Task AcquireAsync(
        AtomicLockResource resource,
        int aggregateId,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(resource) || aggregateId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregateId), "An exact aggregate lock is required.");
        }

        if (_db.Database.IsNpgsql())
        {
            // Two-int advisory locks avoid unstable runtime string hashes. The lock is released by the
            // atomic owner's commit/rollback and therefore cannot leak beyond the command attempt.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({NamespaceKey + (int)resource}, {aggregateId})",
                ct);
        }
        // SQLite is used only by focused tests and serializes writes at the database level. Production
        // PostgreSQL uses the explicit aggregate lock above; concurrency proof runs against PostgreSQL.
    }

    public async Task AcquireAsync(
        AtomicLockResource resource,
        Guid aggregateId,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(resource) || aggregateId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregateId), "An exact aggregate lock is required.");
        }

        if (_db.Database.IsNpgsql())
        {
            var input = new byte[20];
            BinaryPrimitives.WriteInt32BigEndian(input, (int)resource);
            aggregateId.TryWriteBytes(input.AsSpan(4));
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
}
