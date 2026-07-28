using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Purpose-built Owner projections. Every query begins with the effective Owner relationship view;
/// the caller never supplies an OwnerEntity id and management capabilities never substitute for the
/// relationship join.
/// </summary>
public sealed class OwnerPortalService : IOwnerPortalService
{
    internal const string ApprovalNotificationType = "OwnerApproval";
    internal const string MessageNotificationType = "OwnerMessage";
    internal const string OwnerEntityRelatedType = "OwnerEntity";

    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public OwnerPortalService(RentalCommandDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<OwnerPortalOverviewResponse?> GetOverviewAsync(
        OwnerPortalReadScope scope, CancellationToken ct = default)
    {
        var year = _timeProvider.GetUtcNow().Year;
        var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddYears(1);
        var ownerAccess = EffectiveOwnerAccess(scope);
        var properties = AuthorizedProperties(scope, ownerAccess);
        var ownerItems = OwnerItems(scope, ownerAccess);

        return await _db.WorkspaceAccessContexts
            .AsNoTracking()
            .Where(context =>
                context.Id == scope.AccessContextId &&
                context.UserId == scope.UserId &&
                context.PortfolioId == scope.PortfolioId &&
                context.AccessRevision == scope.AccessRevision &&
                ownerAccess.Any())
            .Select(_ => new OwnerPortalOverviewResponse
            {
                CurrentYear = year,
                PropertyCount = properties.Count(),
                UnitCount = _db.Units.Count(unit =>
                    unit.PortfolioId == scope.PortfolioId &&
                    properties.Any(property => property.Id == unit.PropertyId)),
                DistributedThisYear = _db.OwnerDistributions
                    .Where(distribution =>
                        distribution.PortfolioId == scope.PortfolioId &&
                        distribution.DeletedAt == null &&
                        distribution.Status == OwnerDistributionStatus.Approved &&
                        distribution.Date >= start &&
                        distribution.Date < end &&
                        ownerAccess.Any(access =>
                            access.OwnerEntityId == distribution.OwnerEntityId &&
                            (distribution.PropertyId == null || access.PropertyId == distribution.PropertyId)))
                    .Sum(distribution => (decimal?)distribution.Amount) ?? 0m,
                PendingApprovalCount = ownerItems.Count(notification =>
                    notification.Type == ApprovalNotificationType &&
                    !_db.NotificationReadStates.Any(readState =>
                        readState.PortfolioId == scope.PortfolioId &&
                        readState.NotificationId == notification.Id &&
                        readState.UserId == scope.UserId)),
                UnreadMessageCount = ownerItems.Count(notification =>
                    notification.Type == MessageNotificationType &&
                    !_db.NotificationReadStates.Any(readState =>
                        readState.PortfolioId == scope.PortfolioId &&
                        readState.NotificationId == notification.Id &&
                        readState.UserId == scope.UserId)),
            })
            .SingleOrDefaultAsync(ct);
    }

    public async Task<OwnerPortalPropertyPageResponse> ListPropertiesPageAsync(
        OwnerPortalReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var properties = AuthorizedProperties(scope, EffectiveOwnerAccess(scope));
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            properties = properties.Where(property =>
                EF.Functions.ILike(property.Name, $"%{search}%") ||
                EF.Functions.ILike(property.AddressLine1, $"%{search}%") ||
                (property.AddressLine2 != null && EF.Functions.ILike(property.AddressLine2, $"%{search}%")) ||
                EF.Functions.ILike(property.City, $"%{search}%") ||
                EF.Functions.ILike(property.State, $"%{search}%") ||
                EF.Functions.ILike(property.PostalCode, $"%{search}%"));
        }

        properties = (query.SortField, query.SortDescending) switch
        {
            ("name", true) => properties.OrderByDescending(property => property.Name).ThenByDescending(property => property.Id),
            ("city", false) => properties.OrderBy(property => property.City).ThenBy(property => property.Name),
            ("city", true) => properties.OrderByDescending(property => property.City).ThenByDescending(property => property.Name),
            _ => properties.OrderBy(property => property.Name).ThenBy(property => property.Id),
        };

        var totalCount = await properties.CountAsync(ct);
        var items = await properties
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(property => new OwnerPortalPropertyResponse
            {
                Id = property.Id,
                Name = property.Name,
                PropertyType = property.PropertyType,
                Status = property.Status,
                AddressLine1 = property.AddressLine1,
                AddressLine2 = property.AddressLine2,
                City = property.City,
                State = property.State,
                PostalCode = property.PostalCode,
                UnitCount = property.Units.Count,
            })
            .ToListAsync(ct);

        return new OwnerPortalPropertyPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<OwnerPortalDistributionPageResponse> ListDistributionsPageAsync(
        OwnerPortalReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var distributions = BuildDistributionsQuery(scope, query);

        var totalCount = await distributions.CountAsync(ct);
        var items = await distributions
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(distribution => new OwnerPortalDistributionResponse
            {
                Id = distribution.Id,
                OwnerEntityId = distribution.OwnerEntityId,
                OwnerName = distribution.OwnerEntity!.Name,
                PropertyId = distribution.PropertyId,
                PropertyName = distribution.Property == null ? null : distribution.Property.Name,
                Date = distribution.Date,
                Amount = distribution.Amount,
                Method = distribution.Method,
                Memo = distribution.Memo,
            })
            .ToListAsync(ct);

        return new OwnerPortalDistributionPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    internal IQueryable<OwnerDistribution> BuildDistributionsQuery(
        OwnerPortalReadScope scope, ListQuery query)
    {
        var ownerAccess = EffectiveOwnerAccess(scope);
        var distributions = _db.OwnerDistributions
            .AsNoTracking()
            .Where(distribution =>
                distribution.PortfolioId == scope.PortfolioId &&
                distribution.DeletedAt == null &&
                distribution.Status == OwnerDistributionStatus.Approved &&
                ownerAccess.Any(access =>
                    access.OwnerEntityId == distribution.OwnerEntityId &&
                    (distribution.PropertyId == null || access.PropertyId == distribution.PropertyId)));
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            distributions = distributions.Where(distribution =>
                EF.Functions.ILike(distribution.OwnerEntity!.Name, $"%{search}%") ||
                (distribution.Property != null && EF.Functions.ILike(distribution.Property.Name, $"%{search}%")) ||
                (distribution.Memo != null && EF.Functions.ILike(distribution.Memo, $"%{search}%")));
        }
        if (query.From is not null)
        {
            distributions = distributions.Where(distribution => distribution.Date >= query.From.Value);
        }
        if (query.To is not null)
        {
            var through = query.To.Value.Date.AddDays(1);
            distributions = distributions.Where(distribution => distribution.Date < through);
        }
        distributions = query.SortDescending
            ? distributions.OrderByDescending(distribution => distribution.Date).ThenByDescending(distribution => distribution.Id)
            : distributions.OrderBy(distribution => distribution.Date).ThenBy(distribution => distribution.Id);
        return distributions;
    }

    public Task<OwnerPortalItemPageResponse> ListApprovalsPageAsync(
        OwnerPortalReadScope scope, ListQuery query, CancellationToken ct = default) =>
        ListItemsPageAsync(scope, query, ApprovalNotificationType, ct);

    public Task<OwnerPortalItemPageResponse> ListMessagesPageAsync(
        OwnerPortalReadScope scope, ListQuery query, CancellationToken ct = default) =>
        ListItemsPageAsync(scope, query, MessageNotificationType, ct);

    private async Task<OwnerPortalItemPageResponse> ListItemsPageAsync(
        OwnerPortalReadScope scope,
        ListQuery query,
        string notificationType,
        CancellationToken ct)
    {
        var notifications = BuildOwnerItemsQuery(scope, query, notificationType);

        var totalCount = await notifications.CountAsync(ct);
        var items = await notifications
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(notification => new OwnerPortalItemResponse
            {
                Id = notification.Id,
                Title = notification.Title,
                Message = notification.Message,
                Severity = notification.Severity,
                IsRead = _db.NotificationReadStates.Any(readState =>
                    readState.PortfolioId == scope.PortfolioId &&
                    readState.NotificationId == notification.Id &&
                    readState.UserId == scope.UserId),
                CreatedAt = notification.CreatedAt,
            })
            .ToListAsync(ct);

        return new OwnerPortalItemPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    internal IQueryable<Notification> BuildOwnerItemsQuery(
        OwnerPortalReadScope scope, ListQuery query, string notificationType)
    {
        var notifications = OwnerItems(scope, EffectiveOwnerAccess(scope))
            .Where(notification => notification.Type == notificationType);
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            notifications = notifications.Where(notification =>
                EF.Functions.ILike(notification.Title, $"%{search}%") ||
                EF.Functions.ILike(notification.Message, $"%{search}%"));
        }
        notifications = query.SortDescending
            ? notifications.OrderByDescending(notification => notification.CreatedAt).ThenByDescending(notification => notification.Id)
            : notifications.OrderBy(notification => notification.CreatedAt).ThenBy(notification => notification.Id);
        return notifications;
    }

    private IQueryable<EffectiveOwnerAccessProjection> EffectiveOwnerAccess(OwnerPortalReadScope scope) =>
        _db.EffectiveOwnerAccess
            .AsNoTracking()
            .Where(access =>
                access.AccessContextId == scope.AccessContextId &&
                access.UserId == scope.UserId &&
                access.PortfolioId == scope.PortfolioId &&
                access.AccessRevision == scope.AccessRevision);

    private IQueryable<Property> AuthorizedProperties(
        OwnerPortalReadScope scope,
        IQueryable<EffectiveOwnerAccessProjection> ownerAccess)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return _db.Properties
            .AsNoTracking()
            .Where(property =>
                property.PortfolioId == scope.PortfolioId &&
                property.DeletedAt == null &&
                ownerAccess.Any(access =>
                    access.PropertyId == property.Id &&
                    _db.PropertyOwnerships.Any(ownership =>
                        ownership.PortfolioId == scope.PortfolioId
                        && ownership.PropertyId == property.Id
                        && ownership.OwnerEntityId == access.OwnerEntityId
                        && ownership.EffectiveFromUtc <= now
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now))));
    }

    private IQueryable<Notification> OwnerItems(
        OwnerPortalReadScope scope,
        IQueryable<EffectiveOwnerAccessProjection> ownerAccess) =>
        _db.Notifications
            .AsNoTracking()
            .Where(notification =>
                notification.PortfolioId == scope.PortfolioId &&
                notification.UserId == scope.UserId &&
                notification.RelatedEntityType == OwnerEntityRelatedType &&
                notification.RelatedEntityId != null &&
                ownerAccess.Any(access => access.OwnerEntityId == notification.RelatedEntityId));
}
