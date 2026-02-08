using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Lifecycle.Data.Enums;

namespace Lifecycle.Api;

public static class ActivityHelper
{
    public static async Task LogActivity(
        LifecycleDbContext db,
        int portfolioId,
        RentalActivityType type,
        string entityType,
        int? entityId,
        string action,
        string description,
        string actor = "system")
    {
        db.ActivityLogs.Add(new ActivityLog
        {
            PortfolioId = portfolioId,
            Type = type,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Description = description,
            Actor = actor,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }
}
