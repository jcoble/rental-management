using Lifecycle.Data;
using Lifecycle.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifecycle.Api;

public static class OwnerEndpoints
{
    public static WebApplication MapOwnerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/owners");

        group.MapGet("/", async (int portfolioId, LifecycleDbContext db) =>
        {
            var owners = await db.Owners
                .Where(o => o.PortfolioId == portfolioId)
                .Include(o => o.Properties)
                .OrderBy(o => o.Name)
                .ToListAsync();

            return Results.Ok(owners.Select(o => new
            {
                o.Id,
                o.PortfolioId,
                o.Name,
                o.Email,
                o.Phone,
                o.MailingAddress,
                o.Notes,
                PropertyCount = o.Properties.Count,
                o.CreatedAt,
                o.UpdatedAt
            }));
        });

        group.MapPost("/", async (CreateOwnerRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var now = DateTime.UtcNow;
            var owner = new Owner
            {
                PortfolioId = req.PortfolioId,
                Name = req.Name,
                Email = req.Email,
                Phone = req.Phone,
                MailingAddress = req.MailingAddress,
                Notes = req.Notes,
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Owners.Add(owner);
            await db.SaveChangesAsync();
            await sse.BroadcastAsync("owner:created", new { owner.Id, owner.PortfolioId });

            return Results.Created($"/api/owners/{owner.Id}", owner);
        });

        group.MapPatch("/{id:int}", async (int id, UpdateOwnerRequest req, LifecycleDbContext db, SseService sse) =>
        {
            var owner = await db.Owners.FindAsync(id);
            if (owner is null) return Results.NotFound();

            if (req.Name is not null) owner.Name = req.Name;
            if (req.Email is not null) owner.Email = req.Email;
            if (req.Phone is not null) owner.Phone = req.Phone;
            if (req.MailingAddress is not null) owner.MailingAddress = req.MailingAddress;
            if (req.Notes is not null) owner.Notes = req.Notes;
            owner.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            await sse.BroadcastAsync("owner:updated", new { owner.Id, owner.PortfolioId });
            return Results.Ok(owner);
        });

        group.MapDelete("/{id:int}", async (int id, LifecycleDbContext db) =>
        {
            var owner = await db.Owners.FindAsync(id);
            if (owner is null) return Results.NotFound();
            db.Owners.Remove(owner);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateOwnerRequest(
    int PortfolioId,
    string Name,
    string? Email = null,
    string? Phone = null,
    string? MailingAddress = null,
    string? Notes = null);

public record UpdateOwnerRequest(
    string? Name = null,
    string? Email = null,
    string? Phone = null,
    string? MailingAddress = null,
    string? Notes = null);
