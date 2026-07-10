using RentalCommand.Core.Interfaces;
using RentalCommand.Data.Auditing;

namespace RentalCommand.Api.Services;

/// <summary>
/// API compatibility name for the staged semantic audit writer. The implementation only stages;
/// durability is owned by <c>IAtomicUnitOfWork</c>'s final transaction flush.
/// </summary>
public sealed class AuditTrailService : StagedAuditTrailService
{
    // Keep the historical DbContext constructor parameter so existing composition and direct test
    // construction do not require an unrelated caller rewrite. It is deliberately not captured.
    public AuditTrailService(
        RentalCommand.Data.RentalCommandDbContext db,
        IAuditScope scope,
        TimeProvider timeProvider)
        : base(scope, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(db);
    }
}
