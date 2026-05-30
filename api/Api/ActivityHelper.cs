using RentalCommand.Data;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api;

public static class ActivityHelper
{
    public static async Task LogActivity(
        RentalCommandDbContext db,
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
