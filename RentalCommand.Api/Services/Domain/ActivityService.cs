using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IActivityService"/>
public class ActivityService : IActivityService
{
    private readonly RentalCommandDbContext _db;

    public ActivityService(RentalCommandDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ActivityResponse>> ListAsync(
        int portfolioId, RentalActivityType? type, string? entityType, int? entityId, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.ActivityLogs
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId);

        if (type.HasValue)
        {
            q = q.Where(a => a.Type == type.Value);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var et = entityType.Trim();
            q = q.Where(a => a.EntityType == et);
        }

        if (entityId.HasValue)
        {
            q = q.Where(a => a.EntityId == entityId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(a =>
                (a.Description != null && EF.Functions.ILike(a.Description, $"%{term}%")) ||
                (a.Action != null && EF.Functions.ILike(a.Action, $"%{term}%")) ||
                (a.Actor != null && EF.Functions.ILike(a.Actor, $"%{term}%")));
        }

        // Default to newest-first for an activity feed; CreatedAt is the only meaningful sort field.
        q = query.SortField switch
        {
            "createdat" => query.SortDescending ? q.OrderByDescending(a => a.CreatedAt) : q.OrderBy(a => a.CreatedAt),
            _ => q.OrderByDescending(a => a.CreatedAt),
        };

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return items.Select(ActivityResponse.FromEntity).ToList();
    }
}
