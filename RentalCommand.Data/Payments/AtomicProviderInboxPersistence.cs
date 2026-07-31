using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Data.Atomic;
using RentalCommand.Core.Entities;

namespace RentalCommand.Data.Payments;

/// <summary>
/// PostgreSQL owns provider-inbox lease validity. The returned entity remains tracked by the
/// atomic context, and FOR UPDATE keeps ownership stable through its final commit or rollback.
/// </summary>
public sealed class AtomicProviderInboxPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;

    private AtomicProviderInboxPersistence(
        RentalCommandDbContext db,
        AtomicAuditScope auditScope)
    {
        _db = db;
        _auditScope = auditScope;
    }


    private static AtomicAuditScope RequireAuditScope(RentalCommandDbContext db, IAtomicCommandContext context)
    {
        if (context is not AtomicCommandContext owner || !owner.Owns(db))
        {
            throw new AtomicArchitectureException(
                "Atomic helper requires the exact scoped DbContext and active command context.");
        }

        return owner.AuditScope;
    }
public static Task<ProviderInboxEvent?> LockOwnedAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        long providerInboxEventId,
        string claimOwner,
        Guid claimToken,
        CancellationToken ct = default)
    {
        var scope = RequireAuditScope(db, context);
        return new AtomicProviderInboxPersistence(db, scope)
            .LockOwnedAsync(providerInboxEventId, claimOwner, claimToken, ct);
    }

    public async Task<ProviderInboxEvent?> LockOwnedAsync(
        long providerInboxEventId,
        string claimOwner,
        Guid claimToken,
        CancellationToken ct = default)
    {
        if (providerInboxEventId <= 0) throw new ArgumentOutOfRangeException(nameof(providerInboxEventId));
        if (string.IsNullOrWhiteSpace(claimOwner)) throw new ArgumentException("Claim owner is required.", nameof(claimOwner));
        if (claimToken == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(claimToken));

        using var guardLease = _auditScope.BeginInternalRawDml(
            "ProviderInboxEvents",
            AtomicRawDmlOperation.Update);
        return await _db.ProviderInboxEvents
            .FromSqlInterpolated($$"""
                SELECT inbox.*
                FROM "ProviderInboxEvents" AS inbox
                WHERE inbox."Id" = {{providerInboxEventId}}
                  AND inbox."ClaimOwner" = {{claimOwner}}
                  AND inbox."ClaimToken" = {{claimToken}}
                  AND inbox."ClaimExpiresAtUtc" > clock_timestamp()
                  AND inbox."ProcessedAtUtc" IS NULL
                  AND inbox."DeadLetteredAtUtc" IS NULL
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(ct);
    }
}
