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

    internal IQueryable<AuthorizedAssignmentRow> BuildFilteredAssignmentQuery(
        WorkspaceReadScope scope, TechnicianAssignmentQuery query, bool conversationsOnly,
        DateTime now)
    {
        var assignments = AuthorizedAssignmentRows(scope, CapabilityKeys.AssignedWorkRead, now);
        if (query.OpenOnly)
            assignments = assignments.Where(row => row.WorkOrder.Status != WorkOrderStatus.Completed &&
                row.WorkOrder.Status != WorkOrderStatus.Cancelled &&
                row.WorkOrder.Status != WorkOrderStatus.Archived);
        if (query.Status is { } status)
            assignments = assignments.Where(row => row.WorkOrder.Status == status);
        if (query.ScheduledFrom is { } from)
        {
            var utc = from.UtcDateTime;
            assignments = assignments.Where(row =>
                row.WorkOrder.ScheduledFor != null && row.WorkOrder.ScheduledFor >= utc);
        }
        if (query.ScheduledTo is { } to)
        {
            var utc = to.UtcDateTime;
            assignments = assignments.Where(row =>
                row.WorkOrder.ScheduledFor != null && row.WorkOrder.ScheduledFor < utc);
        }
        if (conversationsOnly)
            assignments = assignments.Where(row => _db.Conversations.Any(conversation =>
                conversation.PortfolioId == row.WorkOrder.PortfolioId &&
                conversation.WorkOrderId == row.WorkOrder.Id));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            assignments = assignments.Where(row =>
                EF.Functions.ILike(row.WorkOrder.Title, $"%{term}%") ||
                EF.Functions.ILike(row.WorkOrder.Description, $"%{term}%") ||
                EF.Functions.ILike(row.WorkOrder.Category, $"%{term}%") ||
                EF.Functions.ILike(row.AddressLine1, $"%{term}%") ||
                EF.Functions.ILike(row.City, $"%{term}%") ||
                (row.UnitNumber != null && EF.Functions.ILike(row.UnitNumber, $"%{term}%")));
        }

        return assignments;
    }

    internal IQueryable<TechnicianAssignmentReadRow> BuildAssignmentPageQuery(
        WorkspaceReadScope scope, TechnicianAssignmentQuery query, bool conversationsOnly,
        DateTime now)
    {
        var assignments = BuildFilteredAssignmentQuery(scope, query, conversationsOnly, now);
        IOrderedQueryable<AuthorizedAssignmentRow> ordered = query.SortField switch
        {
            "title" => query.SortDescending
                ? assignments.OrderByDescending(row => row.WorkOrder.Title)
                : assignments.OrderBy(row => row.WorkOrder.Title),
            "status" => query.SortDescending
                ? assignments.OrderByDescending(row => row.WorkOrder.Status)
                : assignments.OrderBy(row => row.WorkOrder.Status),
            "scheduledfor" => query.SortDescending
                ? assignments.OrderByDescending(row => row.WorkOrder.ScheduledFor)
                : assignments.OrderBy(row => row.WorkOrder.ScheduledFor),
            "updatedat" => query.SortDescending
                ? assignments.OrderByDescending(row => row.WorkOrder.UpdatedAt)
                : assignments.OrderBy(row => row.WorkOrder.UpdatedAt),
            _ => assignments.OrderBy(row => row.WorkOrder.ScheduledFor == null)
                .ThenBy(row => row.WorkOrder.ScheduledFor)
                .ThenByDescending(row => row.WorkOrder.UpdatedAt),
        };
        return ordered.ThenBy(row => row.WorkOrder.Id)
            .Skip(query.NormalizedSkip).Take(query.NormalizedTake)
            .Select(row => new TechnicianAssignmentReadRow(
                row.WorkOrder.Id, row.WorkOrder.PropertyId, row.WorkOrder.UnitId,
                row.WorkOrder.Title, row.WorkOrder.Category, row.WorkOrder.Status,
                row.AddressLine1, row.AddressLine2, row.City, row.State, row.PostalCode,
                row.UnitNumber,
                row.WorkOrder.ScheduledFor, row.WorkOrder.ScheduledWindowEnd, row.WorkOrder.UpdatedAt,
                _db.Conversations.Where(conversation =>
                    conversation.PortfolioId == row.WorkOrder.PortfolioId &&
                    conversation.WorkOrderId == row.WorkOrder.Id)
                    .Select(conversation => (int?)conversation.TechnicianUnreadCount).FirstOrDefault() ?? 0));
    }

    public async Task<TechnicianAssignmentDetail?> GetAssignmentAsync(
        WorkspaceReadScope scope, int workOrderId, CancellationToken ct)
    {
        var seed = await AuthorizedAssignmentRows(
                scope, CapabilityKeys.AssignedWorkRead, targetWorkOrderId: workOrderId)
            .Select(row => new AssignmentDetailSeed(
                row.WorkOrder.Id, row.WorkOrder.PropertyId, row.WorkOrder.UnitId,
                row.WorkOrder.Title, row.WorkOrder.Description, row.WorkOrder.Category,
                row.WorkOrder.Status, row.WorkOrder.RequestedAt,
                row.WorkOrder.ScheduledFor, row.WorkOrder.ScheduledWindowEnd,
                row.WorkOrder.CompletedAt, row.WorkOrder.UpdatedAt,
                row.AddressLine1, row.AddressLine2, row.City, row.State, row.PostalCode,
                row.UnitNumber, row.WorkOrder.TechnicianAccessInstructions,
                row.WorkOrder.RequesterName, row.WorkOrder.RequesterPhone, row.WorkOrder.RequesterEmail,
                _db.Conversations.Where(conversation =>
                        conversation.PortfolioId == row.WorkOrder.PortfolioId &&
                        conversation.WorkOrderId == row.WorkOrder.Id)
                    .Select(conversation => (int?)conversation.Id).FirstOrDefault()))
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

    private IQueryable<AuthorizedAssignmentRow> AuthorizedAssignmentRows(
        WorkspaceReadScope scope,
        string capability,
        DateTime? now = null,
        int? targetWorkOrderId = null)
    {
        var safeContexts = _db.Database.SqlQuery<AssignedWorkOrderContextRow>($"""
            SELECT assigned_context."WorkOrderId",
                   assigned_context."AddressLine1",
                   assigned_context."AddressLine2",
                   assigned_context."City",
                   assigned_context."State",
                   assigned_context."PostalCode",
                   assigned_context."UnitNumber"
            FROM public.rc_api_assigned_work_order_detail_context(
                {scope.PortfolioId}, {targetWorkOrderId}) AS assigned_context
            """);

        return
            from workOrder in AuthorizedAssignments(scope, capability, now)
            join context in safeContexts on workOrder.Id equals context.WorkOrderId
            select new AuthorizedAssignmentRow
            {
                WorkOrder = workOrder,
                AddressLine1 = context.AddressLine1,
                AddressLine2 = context.AddressLine2,
                City = context.City,
                State = context.State,
                PostalCode = context.PostalCode,
                UnitNumber = context.UnitNumber,
            };
    }

    private static TechnicianAssignmentListItem ToListItem(TechnicianAssignmentReadRow row) => new(
        row.Id, row.PropertyId, row.UnitId, row.Title, row.Category, row.Status,
        Address(row.Address1, row.Address2, row.City, row.State, row.PostalCode), row.Unit,
        row.ScheduledFor, row.ScheduledWindowEnd, row.UpdatedAt, row.UnreadMessageCount);

    private static string Address(string line1, string? line2, string city, string state, string postal) =>
        AddressComposer.Compose(line1, line2, city, state, postal) ?? string.Empty;

    internal sealed record TechnicianAssignmentReadRow(
        int Id, int PropertyId, int? UnitId, string Title, string Category, WorkOrderStatus Status,
        string Address1, string? Address2, string City, string State, string PostalCode, string? Unit,
        DateTime? ScheduledFor, DateTime? ScheduledWindowEnd, DateTime UpdatedAt, int UnreadMessageCount);

    internal sealed class AuthorizedAssignmentRow
    {
        public WorkOrder WorkOrder { get; init; } = null!;
        public string AddressLine1 { get; init; } = string.Empty;
        public string? AddressLine2 { get; init; }
        public string City { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string PostalCode { get; init; } = string.Empty;
        public string? UnitNumber { get; init; }
    }

    private sealed class AssignedWorkOrderContextRow
    {
        public int WorkOrderId { get; init; }
        public string AddressLine1 { get; init; } = string.Empty;
        public string? AddressLine2 { get; init; }
        public string City { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string PostalCode { get; init; } = string.Empty;
        public string? UnitNumber { get; init; }
    }

    private sealed record AssignmentDetailSeed(int Id, int PropertyId, int? UnitId,
        string Title, string Description, string Category,
        WorkOrderStatus Status, DateTime RequestedAt, DateTime? ScheduledFor, DateTime? ScheduledWindowEnd,
        DateTime? CompletedAt, DateTime UpdatedAt, string Address1, string? Address2, string City,
        string State, string PostalCode, string? Unit, string? AccessInstructions, string? ContactName,
        string? ContactPhone, string? ContactEmail, int? ConversationId);
}
