using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Keeps security-deposit read authorization inside the caller's translated SQL. A deposit is
/// visible when the active property-scoped assignment can manage deposits or read leasing deposits.
/// </summary>
internal static class TenantAccountDepositAuthorization
{
    internal static IQueryable<Property> AuthorizedProperties(
        RentalCommandDbContext db,
        WorkspaceReadScope scope,
        DateTime utcNow)
    {
        var properties = db.Properties.AsNoTracking();
        return properties
            .WhereAuthorized(db, scope, CapabilityKeys.MoneyDepositsManage, utcNow)
            .Union(properties.WhereAuthorized(
                db, scope, CapabilityKeys.LeasingDepositsRead, utcNow));
    }
}
