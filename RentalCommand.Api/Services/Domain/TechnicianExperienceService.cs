using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface ITechnicianExperienceService
{
    Task<TechnicianAssignmentPage> ListAssignmentsAsync(
        WorkspaceReadScope scope, TechnicianAssignmentQuery query, bool conversationsOnly,
        CancellationToken ct);
    Task<TechnicianAssignmentDetail?> GetAssignmentAsync(
        WorkspaceReadScope scope, int workOrderId, CancellationToken ct);
}

/// <summary>
/// Dedicated assigned-work read model. It never projects a management WorkOrder DTO and every query
/// starts with the canonical assigned-work authorization predicate before search, sort, or paging.
/// </summary>
public sealed class TechnicianExperienceService : ITechnicianExperienceService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public TechnicianExperienceService(RentalCommandDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<TechnicianAssignmentPage> ListAssignmentsAsync(
        WorkspaceReadScope scope, TechnicianAssignmentQuery query, bool conversationsOnly,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var assignments = BuildFilteredAssignmentQuery(scope, query, conversationsOnly, now);
        var total = await assignments.CountAsync(ct);
        var rows = await BuildAssignmentPageQuery(scope, query, conversationsOnly, now).ToListAsync(ct);

        return new TechnicianAssignmentPage(rows.Select(ToListItem).ToList(), total,
            query.NormalizedSkip, query.NormalizedTake);
    }

    internal IQueryable<WorkOrder> BuildFilteredAssignmentQuery(
        WorkspaceReadScope scope, TechnicianAssignmentQuery query, bool conversationsOnly,
        DateTime now)
    {
        var assignments = AuthorizedAssignments(scope, CapabilityKeys.AssignedWorkRead, now);
        if (query.OpenOnly)
            assignments = assignments.Where(work => work.Status != WorkOrderStatus.Completed &&
                work.Status != WorkOrderStatus.Cancelled && work.Status != WorkOrderStatus.Archived);
        if (query.Status is { } status) assignments = assignments.Where(work => work.Status == status);
        if (query.ScheduledFrom is { } from)
        {
            var utc = from.UtcDateTime;
            assignments = assignments.Where(work => work.ScheduledFor != null && work.ScheduledFor >= utc);
        }
        if (query.ScheduledTo is { } to)
        {
            var utc = to.UtcDateTime;
            assignments = assignments.Where(work => work.ScheduledFor != null && work.ScheduledFor < utc);
        }
        if (conversationsOnly)
            assignments = assignments.Where(work => _db.Conversations.Any(conversation =>
                conversation.PortfolioId == work.PortfolioId && conversation.WorkOrderId == work.Id));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            assignments = assignments.Where(work =>
                EF.Functions.ILike(work.Title, $"%{term}%") ||
                EF.Functions.ILike(work.Description, $"%{term}%") ||
                EF.Functions.ILike(work.Category, $"%{term}%") ||
                EF.Functions.ILike(work.Property!.AddressLine1, $"%{term}%") ||
                EF.Functions.ILike(work.Property.City, $"%{term}%") ||
                (work.Unit != null && EF.Functions.ILike(work.Unit.UnitNumber, $"%{term}%")));
        }

        return assignments;
    }

    internal IQueryable<TechnicianAssignmentReadRow> BuildAssignmentPageQuery(
        WorkspaceReadScope scope, TechnicianAssignmentQuery query, bool conversationsOnly,
        DateTime now)
    {
        var assignments = BuildFilteredAssignmentQuery(scope, query, conversationsOnly, now);
        IOrderedQueryable<WorkOrder> ordered = query.SortField switch
        {
            "title" => query.SortDescending ? assignments.OrderByDescending(work => work.Title) : assignments.OrderBy(work => work.Title),
            "status" => query.SortDescending ? assignments.OrderByDescending(work => work.Status) : assignments.OrderBy(work => work.Status),
            "scheduledfor" => query.SortDescending ? assignments.OrderByDescending(work => work.ScheduledFor) : assignments.OrderBy(work => work.ScheduledFor),
            "updatedat" => query.SortDescending ? assignments.OrderByDescending(work => work.UpdatedAt) : assignments.OrderBy(work => work.UpdatedAt),
            _ => assignments.OrderBy(work => work.ScheduledFor == null).ThenBy(work => work.ScheduledFor).ThenByDescending(work => work.UpdatedAt),
        };
        return ordered.ThenBy(work => work.Id)
            .Skip(query.NormalizedSkip).Take(query.NormalizedTake)
            .Select(work => new TechnicianAssignmentReadRow(
                work.Id, work.PropertyId, work.UnitId, work.Title, work.Category, work.Status,
                work.Property!.AddressLine1, work.Property.AddressLine2, work.Property.City,
                work.Property.State, work.Property.PostalCode,
                work.Unit != null ? work.Unit.UnitNumber : null,
                work.ScheduledFor, work.ScheduledWindowEnd, work.UpdatedAt,
                _db.Conversations.Where(conversation => conversation.PortfolioId == work.PortfolioId &&
                    conversation.WorkOrderId == work.Id)
                    .Select(conversation => (int?)conversation.TechnicianUnreadCount).FirstOrDefault() ?? 0));
    }

    public async Task<TechnicianAssignmentDetail?> GetAssignmentAsync(
        WorkspaceReadScope scope, int workOrderId, CancellationToken ct)
    {
        var seed = await AuthorizedAssignments(scope, CapabilityKeys.AssignedWorkRead)
            .Where(work => work.Id == workOrderId)
            .Select(work => new AssignmentDetailSeed(
                work.Id, work.PropertyId, work.UnitId, work.Title, work.Description, work.Category, work.Status, work.RequestedAt,
                work.ScheduledFor, work.ScheduledWindowEnd, work.CompletedAt, work.UpdatedAt,
                work.Property!.AddressLine1, work.Property.AddressLine2, work.Property.City,
                work.Property.State, work.Property.PostalCode,
                work.Unit != null ? work.Unit.UnitNumber : null,
                work.TechnicianAccessInstructions,
                work.Tenant != null ? (work.Tenant.FirstName + " " + work.Tenant.LastName).Trim() : null,
                work.Tenant != null ? work.Tenant.Phone : null,
                work.Tenant != null ? work.Tenant.Email : null,
                _db.Conversations.Where(conversation => conversation.PortfolioId == work.PortfolioId &&
                    conversation.WorkOrderId == work.Id).Select(conversation => (int?)conversation.Id).FirstOrDefault()))
            .SingleOrDefaultAsync(ct);
        if (seed is null) return null;

        var entries = await _db.TechnicianWorkEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == scope.PortfolioId && entry.WorkOrderId == workOrderId &&
                AuthorizedAssignments(scope, CapabilityKeys.AssignedWorkRead).Any(work => work.Id == entry.WorkOrderId))
            .OrderByDescending(entry => entry.OccurredAtUtc).ThenByDescending(entry => entry.Id)
            .Select(entry => new TechnicianWorkEntryDto(entry.Id, entry.Kind, entry.Note, entry.Quantity,
                entry.Unit, entry.StoredFileId, entry.OccurredAtUtc, entry.CreatedAtUtc))
            .ToListAsync(ct);

        var messages = await _db.ConversationMessages.AsNoTracking()
            .Where(message => message.Conversation!.PortfolioId == scope.PortfolioId &&
                message.Conversation.WorkOrderId == workOrderId &&
                AuthorizedAssignments(scope, CapabilityKeys.AssignedWorkConverse)
                    .Any(work => work.Id == message.Conversation.WorkOrderId))
            .OrderBy(message => message.CreatedAt).ThenBy(message => message.Id)
            .Select(message => new TechnicianConversationMessageDto(
                message.Id,
                message.SenderRole == ConversationSenderRole.Technician ? "You" :
                    message.SenderRole == ConversationSenderRole.Tenant ? "Tenant" : "Office",
                message.Body,
                message.CreatedAt))
            .ToListAsync(ct);

        var timeline = await _db.WorkOrderStatusEvents.AsNoTracking()
            .Where(statusEvent => statusEvent.PortfolioId == scope.PortfolioId &&
                statusEvent.WorkOrderId == workOrderId &&
                AuthorizedAssignments(scope, CapabilityKeys.AssignedWorkRead)
                    .Any(work => work.Id == statusEvent.WorkOrderId))
            .OrderByDescending(statusEvent => statusEvent.CreatedAtUtc)
            .ThenByDescending(statusEvent => statusEvent.Id)
            .Select(statusEvent => new TechnicianTimelineItem(
                statusEvent.Id, statusEvent.FromStatus, statusEvent.ToStatus, statusEvent.Note,
                statusEvent.ChangedByLabel, statusEvent.CreatedAtUtc))
            .ToListAsync(ct);

        return new TechnicianAssignmentDetail(seed.Id, seed.PropertyId, seed.UnitId,
            seed.Title, seed.Description, seed.Category,
            seed.Status, seed.RequestedAt, seed.ScheduledFor, seed.ScheduledWindowEnd, seed.CompletedAt,
            seed.UpdatedAt, Address(seed.Address1, seed.Address2, seed.City, seed.State, seed.PostalCode),
            seed.Unit, seed.AccessInstructions, seed.ContactName, seed.ContactPhone, seed.ContactEmail,
            seed.ConversationId, timeline, entries, messages);
    }

    private IQueryable<WorkOrder> AuthorizedAssignments(
        WorkspaceReadScope scope, string capability, DateTime? now = null) =>
        _db.WorkOrders.AsNoTracking().WhereAuthorized(
            _db, scope, [capability], now ?? _timeProvider.GetUtcNow().UtcDateTime);

    private static TechnicianAssignmentListItem ToListItem(TechnicianAssignmentReadRow row) => new(
        row.Id, row.PropertyId, row.UnitId, row.Title, row.Category, row.Status,
        Address(row.Address1, row.Address2, row.City, row.State, row.PostalCode), row.Unit,
        row.ScheduledFor, row.ScheduledWindowEnd, row.UpdatedAt, row.UnreadMessageCount);

    private static string Address(string line1, string? line2, string city, string state, string postal) =>
        string.Join(", ", new[] { line1, line2, city, $"{state} {postal}" }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    internal sealed record TechnicianAssignmentReadRow(
        int Id, int PropertyId, int? UnitId, string Title, string Category, WorkOrderStatus Status,
        string Address1, string? Address2, string City, string State, string PostalCode, string? Unit,
        DateTime? ScheduledFor, DateTime? ScheduledWindowEnd, DateTime UpdatedAt, int UnreadMessageCount);

    private sealed record AssignmentDetailSeed(int Id, int PropertyId, int? UnitId,
        string Title, string Description, string Category,
        WorkOrderStatus Status, DateTime RequestedAt, DateTime? ScheduledFor, DateTime? ScheduledWindowEnd,
        DateTime? CompletedAt, DateTime UpdatedAt, string Address1, string? Address2, string City,
        string State, string PostalCode, string? Unit, string? AccessInstructions, string? ContactName,
        string? ContactPhone, string? ContactEmail, int? ConversationId);
}
