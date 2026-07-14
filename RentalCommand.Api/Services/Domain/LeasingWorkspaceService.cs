using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Leasing-only read models. The projections deliberately do not reuse management dashboard DTOs:
/// every source begins with the exact leasing capability and the same effective assignment that
/// covers the record's Property, then remains IQueryable through search, sort, count, and paging.
/// </summary>
public sealed class LeasingWorkspaceService : ILeasingWorkspaceService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _time;

    public LeasingWorkspaceService(RentalCommandDbContext db, TimeProvider time)
        => (_db, _time) = (db, time);

    public Task<LeasingTodayResponse?> GetTodayAsync(
        WorkspaceReadScope scope, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var tomorrow = now.Date.AddDays(1);
        var weekEnd = now.AddDays(7);
        var applications = AuthorizedApplications(scope, now);
        var listingProperties = AuthorizedProperties(scope, CapabilityKeys.LeasingListingsManage, now);
        var showingProperties = AuthorizedProperties(scope, CapabilityKeys.LeasingShowingsManage, now);
        var onboardingProperties = AuthorizedProperties(scope, CapabilityKeys.LeasingOnboardingManage, now);
        var conversations = AuthorizedConversations(scope, now);

        return _db.AuthSessions
            .AsNoTracking()
            .Where(session =>
                session.Id == scope.SessionId &&
                session.UserId == scope.UserId &&
                session.ActiveAccessContextId == scope.AccessContextId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > now &&
                session.ActiveAccessContext != null &&
                session.ActiveAccessContext.AccessRevision == scope.AccessRevision &&
                session.ActiveAccessContext.PortfolioId == scope.PortfolioId)
            .Select(_ => new LeasingTodayResponse
            {
                ApplicationsToReview = applications.Count(application =>
                    application.DeletedAt == null &&
                    (application.Status == ApplicationStatus.Submitted ||
                     application.Status == ApplicationStatus.UnderReview)),
                ListingsNeedingAttention = _db.RentalListings.Count(listing =>
                    listing.PortfolioId == scope.PortfolioId &&
                    listing.DeletedAt == null &&
                    (listing.Status == RentalListingStatus.Draft ||
                     listing.Status == RentalListingStatus.ReadyToPublish ||
                     listing.Publications.Any(publication =>
                         publication.Status == ListingPublicationStatus.Failed ||
                         publication.Status == ListingPublicationStatus.ReconciliationRequired)) &&
                    listingProperties.Any(property => property.Id == listing.PropertyId)),
                ShowingsToday = _db.Appointments.Count(appointment =>
                    appointment.PortfolioId == scope.PortfolioId &&
                    appointment.Type == AppointmentType.Showing &&
                    appointment.Status != AppointmentStatus.Cancelled &&
                    appointment.ScheduledStart >= now.Date &&
                    appointment.ScheduledStart < tomorrow &&
                    appointment.PropertyId != null &&
                    showingProperties.Any(property => property.Id == appointment.PropertyId)),
                UpcomingMoveIns = _db.LeaseManagements.Count(management =>
                    management.PortfolioId == scope.PortfolioId &&
                    management.CanceledAtUtc == null &&
                    management.PossessionGivenAtUtc == null &&
                    management.PlannedPossessionAtUtc >= now &&
                    management.PlannedPossessionAtUtc < weekEnd &&
                    onboardingProperties.Any(property => property.Id == management.PropertyId)),
                UnreadConversations = conversations.Count(conversation =>
                    conversation.LandlordUnreadCount > 0),
            })
            .SingleOrDefaultAsync(ct);
    }

    public async Task<LeasingPipelinePageResponse> ListPipelineAsync(
        WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var applications = AuthorizedApplications(scope, now)
            .Where(application =>
                application.DeletedAt == null &&
                application.Status != ApplicationStatus.Declined &&
                application.Status != ApplicationStatus.Withdrawn)
            .Select(application => new LeasingPipelineItemResponse
            {
                Kind = "Application",
                RecordId = application.Id,
                PropertyId = application.PropertyId,
                UnitId = application.UnitId,
                Title = application.FirstName + " " + application.LastName,
                PropertyName = application.Property == null ? null : application.Property.Name,
                UnitNumber = application.Unit == null ? null : application.Unit.UnitNumber,
                Stage = application.Status == ApplicationStatus.Submitted ? "New application" :
                    application.Status == ApplicationStatus.UnderReview ? "Under review" :
                    application.Status == ApplicationStatus.Approved ? "Approved" : "Application",
                NextActionAtUtc = application.DesiredMoveInDate,
                UpdatedAtUtc = application.UpdatedAt,
            });

        var listingProperties = AuthorizedProperties(scope, CapabilityKeys.LeasingListingsManage, now);
        var listings = _db.RentalListings
            .AsNoTracking()
            .Where(listing =>
                listing.PortfolioId == scope.PortfolioId &&
                listing.DeletedAt == null &&
                listing.Status != RentalListingStatus.Filled &&
                listing.Status != RentalListingStatus.Archived &&
                listingProperties.Any(property => property.Id == listing.PropertyId))
            .Select(listing => new LeasingPipelineItemResponse
            {
                Kind = "Listing",
                RecordId = listing.Id,
                PropertyId = listing.PropertyId,
                UnitId = listing.UnitId,
                Title = listing.Headline,
                PropertyName = listing.Unit!.Property!.Name,
                UnitNumber = listing.Unit.UnitNumber,
                Stage = listing.Status == RentalListingStatus.Draft ? "Draft listing" :
                    listing.Status == RentalListingStatus.ReadyToPublish ? "Ready to publish" :
                    listing.Status == RentalListingStatus.Published ? "Published" : "Paused",
                NextActionAtUtc = listing.AvailableOn,
                UpdatedAtUtc = listing.UpdatedAt,
            });

        var onboardingProperties = AuthorizedProperties(scope, CapabilityKeys.LeasingOnboardingManage, now);
        var moveIns = _db.LeaseManagements
            .AsNoTracking()
            .Where(management =>
                management.PortfolioId == scope.PortfolioId &&
                management.CanceledAtUtc == null &&
                management.PossessionGivenAtUtc == null &&
                management.AccountClosedAtUtc == null &&
                onboardingProperties.Any(property => property.Id == management.PropertyId))
            .Select(management => new LeasingPipelineItemResponse
            {
                Kind = "MoveIn",
                RecordId = management.Id,
                PropertyId = management.PropertyId,
                UnitId = management.UnitId,
                Title = management.Parties
                    .Where(party => party.Role == LeaseManagementPartyRole.PrimaryTenant)
                    .OrderByDescending(party => party.CreatedAtUtc)
                    .Select(party => party.Tenant!.FirstName + " " + party.Tenant.LastName)
                    .FirstOrDefault() ?? management.RelationshipNumber,
                PropertyName = management.Property!.Name,
                UnitNumber = management.Unit!.UnitNumber,
                Stage = management.Agreements.Any(agreement => agreement.FullyExecutedAtUtc != null)
                    ? "Ready for move-in" : "Prepare agreement",
                NextActionAtUtc = management.PlannedPossessionAtUtc,
                UpdatedAtUtc = management.UpdatedAtUtc,
            });

        IQueryable<LeasingPipelineItemResponse> pipeline = applications.Concat(listings).Concat(moveIns);
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            pipeline = pipeline.Where(item =>
                EF.Functions.ILike(item.Title, $"%{search}%") ||
                (item.PropertyName != null && EF.Functions.ILike(item.PropertyName, $"%{search}%")) ||
                (item.UnitNumber != null && EF.Functions.ILike(item.UnitNumber, $"%{search}%")) ||
                EF.Functions.ILike(item.Stage, $"%{search}%"));
        }

        pipeline = query.SortDescending
            ? pipeline.OrderByDescending(item => item.UpdatedAtUtc).ThenByDescending(item => item.RecordId)
            : pipeline.OrderBy(item => item.NextActionAtUtc == null)
                .ThenBy(item => item.NextActionAtUtc)
                .ThenByDescending(item => item.UpdatedAtUtc)
                .ThenBy(item => item.RecordId);

        var totalCount = await pipeline.CountAsync(ct);
        var items = await pipeline.Skip(query.NormalizedSkip).Take(query.NormalizedTake).ToListAsync(ct);
        return new LeasingPipelinePageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<LeasingRentalPageResponse> ListRentalsAsync(
        WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var properties = AuthorizedProperties(scope, CapabilityKeys.LeasingListingsManage, now);
        var units = _db.Units.AsNoTracking().Where(unit =>
            unit.PortfolioId == scope.PortfolioId &&
            unit.DeletedAt == null &&
            unit.Property!.DeletedAt == null &&
            properties.Any(property => property.Id == unit.PropertyId));
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            units = units.Where(unit =>
                EF.Functions.ILike(unit.Property!.Name, $"%{search}%") ||
                EF.Functions.ILike(unit.UnitNumber, $"%{search}%") ||
                EF.Functions.ILike(unit.Property.AddressLine1, $"%{search}%"));
        }

        units = query.SortDescending
            ? units.OrderByDescending(unit => unit.Property!.Name).ThenByDescending(unit => unit.UnitNumber).ThenByDescending(unit => unit.Id)
            : units.OrderBy(unit => unit.Property!.Name).ThenBy(unit => unit.UnitNumber).ThenBy(unit => unit.Id);

        var totalCount = await units.CountAsync(ct);
        var items = await units
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(unit => new LeasingRentalResponse
            {
                PropertyId = unit.PropertyId,
                UnitId = unit.Id,
                PropertyName = unit.Property!.Name,
                UnitNumber = unit.UnitNumber,
                Address = unit.Property.AddressLine1 + ", " + unit.Property.City + ", " + unit.Property.State,
                ListingId = unit.RentalListings
                    .Where(listing => listing.DeletedAt == null)
                    .OrderByDescending(listing => listing.UpdatedAt)
                    .ThenByDescending(listing => listing.Id)
                    .Select(listing => (int?)listing.Id)
                    .FirstOrDefault(),
                ListingStatus = unit.RentalListings
                    .Where(listing => listing.DeletedAt == null)
                    .OrderByDescending(listing => listing.UpdatedAt)
                    .ThenByDescending(listing => listing.Id)
                    .Select(listing => (RentalListingStatus?)listing.Status)
                    .FirstOrDefault(),
                ListingHeadline = unit.RentalListings
                    .Where(listing => listing.DeletedAt == null)
                    .OrderByDescending(listing => listing.UpdatedAt)
                    .ThenByDescending(listing => listing.Id)
                    .Select(listing => listing.Headline)
                    .FirstOrDefault(),
                AskingRent = unit.RentalListings
                    .Where(listing => listing.DeletedAt == null)
                    .OrderByDescending(listing => listing.UpdatedAt)
                    .ThenByDescending(listing => listing.Id)
                    .Select(listing => (decimal?)listing.Rent)
                    .FirstOrDefault(),
                AvailableOn = unit.RentalListings
                    .Where(listing => listing.DeletedAt == null)
                    .OrderByDescending(listing => listing.UpdatedAt)
                    .ThenByDescending(listing => listing.Id)
                    .Select(listing => listing.AvailableOn)
                    .FirstOrDefault(),
                OpenApplicationCount = _db.RentalApplications.Count(application =>
                    application.PortfolioId == scope.PortfolioId &&
                    application.UnitId == unit.Id &&
                    application.DeletedAt == null &&
                    (application.Status == ApplicationStatus.Submitted ||
                     application.Status == ApplicationStatus.UnderReview)),
                NextShowingAtUtc = _db.Appointments
                    .Where(appointment =>
                        appointment.PortfolioId == scope.PortfolioId &&
                        appointment.UnitId == unit.Id &&
                        appointment.Type == AppointmentType.Showing &&
                        appointment.Status != AppointmentStatus.Cancelled &&
                        appointment.ScheduledStart >= now)
                    .OrderBy(appointment => appointment.ScheduledStart)
                    .Select(appointment => (DateTime?)appointment.ScheduledStart)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return new LeasingRentalPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<LeasingCalendarPageResponse> ListCalendarAsync(
        WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var properties = AuthorizedProperties(scope, CapabilityKeys.LeasingShowingsManage, now);
        var allProperties = _db.AuthorizedAllPropertyAssignments(
            scope, CapabilityKeys.LeasingShowingsManage,
            CapabilityAuthorizationTargetKind.Property, now);
        var appointments = _db.Appointments.AsNoTracking().Where(appointment =>
            appointment.PortfolioId == scope.PortfolioId &&
            appointment.Type == AppointmentType.Showing &&
            ((appointment.PropertyId == null && allProperties.Any()) ||
             (appointment.PropertyId != null && properties.Any(property => property.Id == appointment.PropertyId))));
        if (query.From is not null) appointments = appointments.Where(item => item.ScheduledStart >= query.From.Value);
        if (query.To is not null)
        {
            var through = query.To.Value.Date.AddDays(1);
            appointments = appointments.Where(item => item.ScheduledStart < through);
        }
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            appointments = appointments.Where(item =>
                EF.Functions.ILike(item.Title, $"%{search}%") ||
                (item.ProspectName != null && EF.Functions.ILike(item.ProspectName, $"%{search}%")) ||
                (item.Property != null && EF.Functions.ILike(item.Property.Name, $"%{search}%")) ||
                (item.Unit != null && EF.Functions.ILike(item.Unit.UnitNumber, $"%{search}%")));
        }
        appointments = query.SortDescending
            ? appointments.OrderByDescending(item => item.ScheduledStart).ThenByDescending(item => item.Id)
            : appointments.OrderBy(item => item.ScheduledStart).ThenBy(item => item.Id);

        var totalCount = await appointments.CountAsync(ct);
        var items = await appointments.Skip(query.NormalizedSkip).Take(query.NormalizedTake)
            .Select(item => new LeasingCalendarItemResponse
            {
                Id = item.Id,
                PropertyId = item.PropertyId,
                UnitId = item.UnitId,
                RentalApplicationId = item.RentalApplicationId,
                Title = item.Title,
                ProspectName = item.ProspectName,
                PropertyName = item.Property == null ? null : item.Property.Name,
                UnitNumber = item.Unit == null ? null : item.Unit.UnitNumber,
                Type = item.Type,
                Status = item.Status,
                ScheduledStart = item.ScheduledStart,
                ScheduledEnd = item.ScheduledEnd,
            }).ToListAsync(ct);
        return new LeasingCalendarPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<LeasingInboxPageResponse> ListInboxAsync(
        WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default)
    {
        var conversations = AuthorizedConversations(scope, _time.GetUtcNow().UtcDateTime);
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            conversations = conversations.Where(item =>
                EF.Functions.ILike(item.Subject, $"%{search}%") ||
                EF.Functions.ILike(item.Tenant!.FirstName + " " + item.Tenant.LastName, $"%{search}%") ||
                (item.Property != null && EF.Functions.ILike(item.Property.Name, $"%{search}%")) ||
                (item.LastMessagePreview != null && EF.Functions.ILike(item.LastMessagePreview, $"%{search}%")));
        }
        conversations = query.SortDescending
            ? conversations.OrderByDescending(item => item.LastMessageAt).ThenByDescending(item => item.Id)
            : conversations.OrderBy(item => item.LastMessageAt).ThenBy(item => item.Id);

        var totalCount = await conversations.CountAsync(ct);
        var items = await conversations.Skip(query.NormalizedSkip).Take(query.NormalizedTake)
            .Select(item => new LeasingInboxItemResponse
            {
                Id = item.Id,
                TenantName = item.Tenant!.FirstName + " " + item.Tenant.LastName,
                Subject = item.Subject,
                PropertyName = item.Property == null ? null : item.Property.Name,
                LastMessagePreview = item.LastMessagePreview,
                LastMessageAt = item.LastMessageAt,
                UnreadCount = item.LandlordUnreadCount,
            }).ToListAsync(ct);
        return new LeasingInboxPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private IQueryable<RentalApplication> AuthorizedApplications(WorkspaceReadScope scope, DateTime now) =>
        _db.RentalApplications.AsNoTracking().WhereAuthorized(
            _db, scope, [CapabilityKeys.LeasingApplicationsManage], now);

    private IQueryable<Property> AuthorizedProperties(
        WorkspaceReadScope scope, string capability, DateTime now) =>
        _db.Properties.AsNoTracking().WhereAuthorized(_db, scope, capability, now)
            .Where(property => property.DeletedAt == null);

    private IQueryable<Conversation> AuthorizedConversations(WorkspaceReadScope scope, DateTime now) =>
        _db.Conversations.AsNoTracking().WhereAuthorized(
            _db, scope,
            [CapabilityKeys.LeasingOnboardingManage, CapabilityKeys.LeasingApplicationsManage], now);
}
