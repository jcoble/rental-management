using Lifecycle.Data;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class ActivityEndpoints
{
    public static WebApplication MapActivityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/portfolios/{portfolioId:int}/activity", async (
            int portfolioId,
            int take,
            LifecycleDbContext db) =>
        {
            var safeTake = take is <= 0 or > 200 ? 50 : take;

            var items = await db.ActivityLogs
                .Where(a => a.PortfolioId == portfolioId)
                .OrderByDescending(a => a.CreatedAt)
                .Take(safeTake)
                .ToListAsync();

            return Results.Ok(items.Select(a => new
            {
                a.Id,
                a.PortfolioId,
                Type = a.Type.ToString(),
                a.EntityType,
                a.EntityId,
                a.Action,
                a.Description,
                a.Actor,
                a.CreatedAt
            }));
        });

        return app;
    }
}
