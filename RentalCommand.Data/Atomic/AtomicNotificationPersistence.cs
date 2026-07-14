using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicNotificationPersistence : IAtomicNotificationPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicNotificationPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<int> MarkAllReadAsync(
        int portfolioId,
        int userId,
        bool includeStaffOnlyNotifications,
        DateTime readAtUtc,
        CancellationToken ct = default)
    {
        using var lease = _scope.BeginInternalRawDml("Notifications", AtomicRawDmlOperation.Update);
        return await _db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "Notifications"
            SET "IsRead" = TRUE,
                "ReadAt" = {{readAtUtc}}
            WHERE "PortfolioId" = {{portfolioId}}
              AND ("UserId" IS NULL OR "UserId" = {{userId}})
              AND "IsRead" = FALSE
              AND ({{includeStaffOnlyNotifications}} OR "Type" <> 'TenantMessage')
            """, ct);
    }
}
