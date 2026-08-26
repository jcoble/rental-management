using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Conversations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Conversations;
using RentalCommand.Api.Writes;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IConversationService"/>
public class ConversationService : IConversationService
{
    private const string EntityType = "Conversation";
    private static readonly string[] ConversationReadCapabilities = [CapabilityKeys.RentalsRead];
    private static readonly string[] ConversationWriteCapabilities =
        [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingOnboardingManage];

    private readonly RentalCommandDbContext _db;
    private readonly IRealtimeInvalidationQueue _realtimeQueue;
    private readonly IFairHousingReviewService _fairHousing;
    private readonly ILogger<ConversationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestWriteExecutor _writes;

    public ConversationService(
        RentalCommandDbContext db,
        IRealtimeInvalidationQueue realtimeQueue,
        IFairHousingReviewService fairHousing,
        ILogger<ConversationService> logger,
        TimeProvider timeProvider,
        IRequestWriteExecutor writes)
    {
        _db = db;
        _realtimeQueue = realtimeQueue;
        _fairHousing = fairHousing;
        _logger = logger;
        _timeProvider = timeProvider;
        _writes = writes;
    }

    // ===========================================================================================
    // Landlord
    // ===========================================================================================

    public async Task<IReadOnlyList<ConversationSummary>> ListAuthorizedAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default)
    {
        var page = await ListPageAuthorizedAsync(scope, new ConversationListQuery(), ct);
        return page.Items;
    }

    public Task<ConversationListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope,
        ConversationListQuery query,
        CancellationToken ct = default)
    {
        var conversations = _db.Conversations
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                ConversationReadCapabilities,
                _timeProvider.UtcNow());
        return ListPageFromQueryAsync(
            conversations, query, tenantViewer: false, ct: ct);
    }

    private static async Task<ConversationListResponse> ListPageFromQueryAsync(
        IQueryable<Conversation> conversations,
        ConversationListQuery query,
        bool tenantViewer,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            conversations = conversations.Where(c =>
                EF.Functions.ILike(c.Subject, $"%{term}%") ||
                (c.LastMessagePreview != null &&
                    EF.Functions.ILike(c.LastMessagePreview, $"%{term}%")) ||
                (c.Tenant != null &&
                    (EF.Functions.ILike(c.Tenant.FirstName, $"%{term}%") ||
                     EF.Functions.ILike(c.Tenant.LastName, $"%{term}%") ||
                     EF.Functions.ILike(c.Tenant.FirstName + " " + c.Tenant.LastName, $"%{term}%"))) ||
                (c.Property != null && EF.Functions.ILike(c.Property.Name, $"%{term}%")));
        }

        if (query.UnreadOnly == true)
        {
            conversations = tenantViewer
                ? conversations.Where(c => c.TenantUnreadCount > 0)
                : conversations.Where(c => c.LandlordUnreadCount > 0);
        }

        var summaries = ProjectSummaries(conversations, tenantViewer);

        var totalCount = await summaries.CountAsync(ct);

        IOrderedQueryable<ConversationSummary> ordered = query.SortField switch
        {
            "tenantname" => query.SortDescending ? summaries.OrderByDescending(c => c.TenantName) : summaries.OrderBy(c => c.TenantName),
            "subject" => query.SortDescending ? summaries.OrderByDescending(c => c.Subject) : summaries.OrderBy(c => c.Subject),
            "unreadcount" => query.SortDescending ? summaries.OrderByDescending(c => c.UnreadCount) : summaries.OrderBy(c => c.UnreadCount),
            "messagecount" => query.SortDescending ? summaries.OrderByDescending(c => c.MessageCount) : summaries.OrderBy(c => c.MessageCount),
            "lastmessageat" => query.SortDescending ? summaries.OrderByDescending(c => c.LastMessageAt) : summaries.OrderBy(c => c.LastMessageAt),
            _ => summaries.OrderByDescending(c => c.LastMessageAt),
        };

        var items = await ordered
            .ThenBy(c => c.Id)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        return new ConversationListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<int> GetUnreadCountAuthorizedAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default) =>
        await _db.Conversations
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                ConversationReadCapabilities,
                _timeProvider.UtcNow())
            .SumAsync(c => (int?)c.LandlordUnreadCount, ct) ?? 0;

    public async Task<ConversationDetail?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var found = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == id)
            .WhereAuthorized(
                _db,
                scope,
                ConversationReadCapabilities,
                _timeProvider.UtcNow())
            .AnyAsync(ct);
        return found
            ? await LoadDetailAsync(scope.PortfolioId, id, tenantId: null, tenantViewer: false, ct)
            : null;
    }

    public async Task<bool> MarkReadAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.LandlordConversationRead, id, string.Empty,
            operationKey, new AtomicConversationReadRequest(0));
        var outcome = await ExecuteNotificationAsync(command, ct);
        return outcome.Value.Found;
    }

    private async Task<ConversationDetail?> LoadDetailAsync(
        int portfolioId,
        int id,
        int? tenantId,
        bool tenantViewer,
        CancellationToken ct = default)
    {
        var query = _db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == id && c.PortfolioId == portfolioId);

        if (tenantId.HasValue)
        {
            query = query.Where(c => c.TenantId == tenantId.Value);
        }

        return await ProjectDetails(query, tenantViewer).FirstOrDefaultAsync(ct);
    }

    private static IQueryable<ConversationSummary> ProjectSummaries(
        IQueryable<Conversation> query,
        bool tenantViewer)
    {
        return query.Select(c => new ConversationSummary
        {
            Id = c.Id,
            TenantId = c.TenantId,
            TenantName = c.Tenant != null ? (c.Tenant.FirstName + " " + c.Tenant.LastName).Trim() : string.Empty,
            CounterpartyName = tenantViewer
                ? c.Portfolio != null && c.Portfolio.ManagementCompanyName.Trim() != string.Empty
                    ? c.Portfolio.ManagementCompanyName.Trim()
                    : "Property management"
                : c.Tenant != null
                    ? (c.Tenant.FirstName + " " + c.Tenant.LastName).Trim()
                    : string.Empty,
            Subject = c.Subject,
            PropertyName = c.Property != null ? c.Property.Name : null,
            LastMessagePreview = c.LastMessagePreview,
            LastMessageAt = c.LastMessageAt,
            UnreadCount = tenantViewer ? c.TenantUnreadCount : c.LandlordUnreadCount,
            MessageCount = c.Messages.Count(),
        });
    }

    private static IQueryable<ConversationDetail> ProjectDetails(
        IQueryable<Conversation> query,
        bool tenantViewer)
    {
        return query.Select(c => new ConversationDetail
        {
            Id = c.Id,
            TenantId = c.TenantId,
            TenantName = c.Tenant != null ? (c.Tenant.FirstName + " " + c.Tenant.LastName).Trim() : string.Empty,
            CounterpartyName = tenantViewer
                ? c.Portfolio != null && c.Portfolio.ManagementCompanyName.Trim() != string.Empty
                    ? c.Portfolio.ManagementCompanyName.Trim()
                    : "Property management"
                : c.Tenant != null
                    ? (c.Tenant.FirstName + " " + c.Tenant.LastName).Trim()
                    : string.Empty,
            Subject = c.Subject,
            PropertyName = c.Property != null ? c.Property.Name : null,
            LastMessagePreview = c.LastMessagePreview,
            LastMessageAt = c.LastMessageAt,
            UnreadCount = tenantViewer ? c.TenantUnreadCount : c.LandlordUnreadCount,
            MessageCount = c.Messages.Count(),
            Messages = c.Messages
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.Id)
                .Select(m => new ConversationMessageDto
                {
                    Id = m.Id,
                    SenderRole = m.SenderRole == ConversationSenderRole.Landlord ? "Landlord" :
                        m.SenderRole == ConversationSenderRole.Technician ? "Technician" : "Tenant",
                    Body = m.Body,
                    Channels = m.Channels,
                    CreatedAt = m.CreatedAt,
                })
                .ToList(),
        });
    }

    public async Task<ConversationDetail?> StartAsync(
        int portfolioId, int tenantId, string subject, string body, List<string> channels,
        string operationKey, bool acknowledgedFairHousingReview = false, CancellationToken ct = default)
    {
        return await StartCoreAsync(
            portfolioId,
            tenantId,
            propertyId: null,
            subject,
            body,
            channels,
            operationKey,
            acknowledgedFairHousingReview,
            managementAccess: null,
            ct: ct);
    }

    public async Task<ConversationDetail?> StartAuthorizedAsync(
        WorkspaceReadScope scope,
        int tenantId,
        int? propertyId,
        string subject,
        string body,
        List<string> channels,
        string operationKey,
        bool acknowledgedFairHousingReview = false,
        CancellationToken ct = default)
    {
        var resolution = await ResolveAuthorizedConversationPropertyAsync(
            scope, tenantId, propertyId, ct);
        if (!resolution.Allowed)
        {
            return null;
        }

        return await StartCoreAsync(
            scope.PortfolioId,
            tenantId,
            resolution.PropertyId,
            subject,
            body,
            channels,
            operationKey,
            acknowledgedFairHousingReview,
            new ConversationManagementAccess(
                scope.SessionId,
                scope.UserId,
                scope.AccessContextId,
                scope.AccessRevision),
            ct);
    }

    private async Task<ConversationDetail?> StartCoreAsync(
        int portfolioId,
        int tenantId,
        int? propertyId,
        string subject,
        string body,
        List<string> channels,
        string operationKey,
        bool acknowledgedFairHousingReview,
        ConversationManagementAccess? managementAccess,
        CancellationToken ct)
    {
        var identity = new AtomicCommandIdentity(
            "conversation.start",
            ScopedOperationKey(portfolioId, tenantId, operationKey, propertyId));
        var completedReceiptExists = await _db.AtomicCommandReceipts
            .AsNoTracking()
            .AnyAsync(receipt => receipt.CommandType == identity.CommandType
                && receipt.IdempotencyKey == identity.IdempotencyKey, ct);

        var tenantExists = completedReceiptExists
            || await _db.Tenants.AsNoTracking()
                .AnyAsync(t => t.Id == tenantId && t.PortfolioId == portfolioId && t.DeletedAt == null, ct);

        if (!tenantExists)
        {
            return null; // → controller 404
        }

        // Fair Housing gate: screen the outgoing copy before it leaves the building. Throws a
        // FairHousingBlockedException (→ 422) when flagged and not acknowledged; otherwise (clean,
        // acknowledged-override, or review-unavailable) falls through and the send proceeds.
        if (!completedReceiptExists)
        {
            await EnforceFairHousingGateAsync(
                portfolioId, tenantId, conversationId: null, subject, body, acknowledgedFairHousingReview, ct);
        }

        return await ExecuteLandlordSendAsync(
            identity,
            new SendConversationMessageCommand(
                portfolioId, null, tenantId, subject, body, ConversationSenderRole.Landlord,
                channels, _timeProvider.UtcNow(), propertyId, managementAccess),
            ct);
    }

    private async Task<ConversationPropertyResolution> ResolveAuthorizedConversationPropertyAsync(
        WorkspaceReadScope scope,
        int tenantId,
        int? requestedPropertyId,
        CancellationToken ct)
    {
        var utcNow = _timeProvider.UtcNow();
        var authorizedProperties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(_db, scope, ConversationWriteCapabilities, utcNow);
        var currentPropertyIds = _db.LeaseManagementParties
            .AsNoTracking()
            .Where(party =>
                party.PortfolioId == scope.PortfolioId &&
                party.TenantId == tenantId &&
                party.Role != LeaseManagementPartyRole.Guarantor &&
                party.LeaseManagement != null &&
                party.LeaseManagement.CanceledAtUtc == null &&
                party.LeaseManagement.PossessionReturnedAtUtc == null &&
                _db.LeaseManagementLifecycleProjections.Any(lifecycle =>
                    lifecycle.PortfolioId == scope.PortfolioId &&
                    lifecycle.LeaseManagementId == party.LeaseManagementId &&
                    party.EffectiveFrom <= lifecycle.BusinessDate &&
                    (party.EffectiveThrough == null ||
                     party.EffectiveThrough >= lifecycle.BusinessDate)))
            .Select(party => party.LeaseManagement!.PropertyId)
            .Distinct()
            .Where(propertyId => authorizedProperties.Any(property => property.Id == propertyId));

        if (requestedPropertyId is > 0)
        {
            var allowed = await currentPropertyIds.AnyAsync(
                propertyId => propertyId == requestedPropertyId.Value,
                ct);
            return new ConversationPropertyResolution(allowed, allowed ? requestedPropertyId : null);
        }

        var currentProperty = await currentPropertyIds
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                PropertyId = group.Min(),
            })
            .FirstOrDefaultAsync(ct);

        if (currentProperty?.Count == 1)
        {
            return new ConversationPropertyResolution(true, currentProperty.PropertyId);
        }

        if (currentProperty is { Count: > 1 })
        {
            throw new DomainValidationException(
                "Select which property this conversation is about because the tenant has more than one current rental relationship.");
        }

        var canUseUnattachedTenant = await _db.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId && tenant.PortfolioId == scope.PortfolioId)
            .Where(tenant => _db.AuthorizedAllPropertyAssignments(
                scope,
                ConversationWriteCapabilities,
                CapabilityAuthorizationTargetKind.Property,
                utcNow).Any())
            .AnyAsync(ct);

        return new ConversationPropertyResolution(canUseUnattachedTenant, PropertyId: null);
    }

    private sealed record ConversationPropertyResolution(bool Allowed, int? PropertyId);

    public async Task<ConversationDetail?> PostMessageAsync(
        int portfolioId, int id, string body, List<string> channels, string operationKey,
        CancellationToken ct = default)
    {
        return await ExecuteLandlordSendAsync(
            new AtomicCommandIdentity(
                "conversation.post-message",
                ScopedOperationKey(portfolioId, id, operationKey)),
            new SendConversationMessageCommand(
                portfolioId, id, 0, string.Empty, body, ConversationSenderRole.Landlord,
                channels, _timeProvider.UtcNow()),
            ct);
    }

    public Task<ConversationDetail?> PostMessageAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        string body,
        List<string> channels,
        string operationKey,
        CancellationToken ct = default) =>
        PostMessageAuthorizedCoreAsync(
            scope, id, body, channels, operationKey, requiredCapabilityKey: null, ct: ct);

    public Task<ConversationDetail?> PostMessageAuthorizedForCapabilityAsync(
        WorkspaceReadScope scope,
        int id,
        string body,
        List<string> channels,
        string operationKey,
        string requiredCapabilityKey,
        CancellationToken ct = default)
    {
        if (!ConversationWriteCapabilities.Contains(requiredCapabilityKey, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredCapabilityKey), requiredCapabilityKey, "Unsupported conversation capability.");
        }

        return PostMessageAuthorizedCoreAsync(
            scope, id, body, channels, operationKey, requiredCapabilityKey, ct);
    }

    private async Task<ConversationDetail?> PostMessageAuthorizedCoreAsync(
        WorkspaceReadScope scope,
        int id,
        string body,
        List<string> channels,
        string operationKey,
        string? requiredCapabilityKey,
        CancellationToken ct)
    {
        return await ExecuteLandlordSendAsync(
            new AtomicCommandIdentity(
                "conversation.post-message",
                ScopedOperationKey(scope.PortfolioId, id, operationKey)),
            new SendConversationMessageCommand(
                scope.PortfolioId, id, 0, string.Empty, body, ConversationSenderRole.Landlord,
                channels, _timeProvider.UtcNow(), PropertyId: null,
                ManagementAccess: new ConversationManagementAccess(
                    scope.SessionId,
                    scope.UserId,
                    scope.AccessContextId,
                    scope.AccessRevision,
                    requiredCapabilityKey)),
            ct);
    }

    private static string ScopedOperationKey(
        int portfolioId,
        int targetId,
        string operationKey,
        int? propertyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey.Trim())))
            .ToLowerInvariant();
        return $"conversation:{portfolioId}:{targetId}:{propertyId?.ToString() ?? "none"}:{hash}";
    }

    private async Task<ConversationDetail?> ExecuteLandlordSendAsync(
        AtomicCommandIdentity identity,
        SendConversationMessageCommand command,
        CancellationToken ct)
    {
        var outcome = await _writes.ExecuteExactAsync(
            identity.IdempotencyKey,
            ConversationWriteSupport.Write(identity.CommandType, _db, command), ct);
        if (outcome.Value.Outcome == SendConversationMessageOutcome.NotFound)
        {
            return null;
        }

        var detail = await LoadDetailAsync(
            command.PortfolioId, outcome.Value.ConversationId, tenantId: null, tenantViewer: false, ct);
        if (detail is null)
        {
            return null;
        }

        // Remote broadcasts happen only after the atomic command has committed (or replayed its receipt).
        EnqueueConversationUpdates(command.PortfolioId, detail, outcome.Value.NotificationIds);

        return detail;
    }

    // ===========================================================================================
    // Tenant
    // ===========================================================================================

    public async Task<IReadOnlyList<ConversationSummary>> ListForTenantAsync(
        int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var page = await ListPageForTenantAsync(
            portfolioId, tenantId, new ConversationListQuery(), ct);
        return page.Items;
    }

    public Task<ConversationListResponse> ListPageForTenantAsync(
        int portfolioId,
        int tenantId,
        ConversationListQuery query,
        CancellationToken ct = default)
    {
        var conversations = _db.Conversations
            .AsNoTracking()
            .Where(c => c.PortfolioId == portfolioId && c.TenantId == tenantId);
        return ListPageFromQueryAsync(
            conversations, query, tenantViewer: true, ct: ct);
    }

    public async Task<ConversationDetail?> GetForTenantAsync(
        int portfolioId, int tenantId, int id, CancellationToken ct = default)
        => await LoadDetailAsync(portfolioId, id, tenantId, tenantViewer: true, ct);

    public async Task<bool> MarkReadForTenantAsync(
        WorkspaceReadScope scope,
        int tenantId,
        int id,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicNotificationMutation.Command(scope,
            AtomicNotificationMutationDomain.TenantConversationRead, id, string.Empty,
            operationKey, new AtomicConversationReadRequest(tenantId));
        var outcome = await ExecuteNotificationAsync(command, ct);
        return outcome.Value.Found;
    }

    private Task<AtomicCommandOutcome<AtomicNotificationMutationResult>> ExecuteNotificationAsync(
        AtomicNotificationMutationCommand command,
        CancellationToken ct) =>
        _writes.ExecuteExactAsync(
            AtomicNotificationMutation.Identity(command).IdempotencyKey,
            AtomicNotificationMutation.Write(_db, command), ct);

    public async Task<ConversationDetail?> TenantStartAsync(
        int portfolioId, int tenantId, string subject, string body, string operationKey,
        CancellationToken ct = default)
    {
        var propertyId = await ResolveTenantConversationPropertyAsync(portfolioId, tenantId, ct);
        return await ExecuteTenantSendAsync(
            new AtomicCommandIdentity(
                "conversation.tenant-start",
                ScopedOperationKey(portfolioId, tenantId, operationKey, propertyId)),
            new SendConversationMessageCommand(
                portfolioId, null, tenantId, subject, body, ConversationSenderRole.Tenant,
                [], _timeProvider.UtcNow(), propertyId),
            tenantId,
            ct);
    }

    private async Task<int?> ResolveTenantConversationPropertyAsync(
        int portfolioId,
        int tenantId,
        CancellationToken ct)
    {
        var currentProperty = await _db.LeaseManagementParties
            .AsNoTracking()
            .Where(party =>
                party.PortfolioId == portfolioId &&
                party.TenantId == tenantId &&
                party.Role != LeaseManagementPartyRole.Guarantor &&
                party.LeaseManagement != null &&
                party.LeaseManagement.CanceledAtUtc == null &&
                party.LeaseManagement.PossessionReturnedAtUtc == null &&
                _db.LeaseManagementLifecycleProjections.Any(lifecycle =>
                    lifecycle.PortfolioId == portfolioId &&
                    lifecycle.LeaseManagementId == party.LeaseManagementId &&
                    party.EffectiveFrom <= lifecycle.BusinessDate &&
                    (party.EffectiveThrough == null ||
                     party.EffectiveThrough >= lifecycle.BusinessDate)))
            .Select(party => party.LeaseManagement!.PropertyId)
            .Distinct()
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                PropertyId = group.Min(),
            })
            .FirstOrDefaultAsync(ct);

        return currentProperty?.Count == 1 ? currentProperty.PropertyId : null;
    }

    public async Task<ConversationDetail?> TenantPostAsync(
        int portfolioId, int tenantId, int id, string body, string operationKey,
        CancellationToken ct = default)
    {
        return await ExecuteTenantSendAsync(
            new AtomicCommandIdentity(
                "conversation.tenant-post-message",
                ScopedOperationKey(portfolioId, id, operationKey)),
            new SendConversationMessageCommand(
                portfolioId, id, tenantId, string.Empty, body, ConversationSenderRole.Tenant,
                [], _timeProvider.UtcNow()),
            tenantId,
            ct);
    }

    private async Task<ConversationDetail?> ExecuteTenantSendAsync(
        AtomicCommandIdentity identity,
        SendConversationMessageCommand command,
        int tenantId,
        CancellationToken ct)
    {
        var outcome = await _writes.ExecuteExactAsync(
            identity.IdempotencyKey,
            ConversationWriteSupport.Write(identity.CommandType, _db, command), ct);
        if (outcome.Value.Outcome == SendConversationMessageOutcome.NotFound) return null;

        var detail = await LoadDetailAsync(
            command.PortfolioId, outcome.Value.ConversationId, tenantId, tenantViewer: true, ct);
        if (detail is null) return null;

        EnqueueConversationUpdates(command.PortfolioId, detail, outcome.Value.NotificationIds);

        return detail;
    }

    private void EnqueueConversationUpdates(
        int portfolioId,
        ConversationDetail detail,
        IReadOnlyList<int> notificationIds)
    {
        var updates = new List<EntityUpdateBroadcast>(notificationIds.Count + 1)
        {
            new(portfolioId, EntityType, detail.Id, detail),
        };
        updates.AddRange(notificationIds.Select(notificationId =>
            new EntityUpdateBroadcast(
                portfolioId,
                "Notification",
                notificationId,
                new SavedContextNotificationRealtimeHint())));
        _realtimeQueue.EnqueueEntityUpdates(updates);
    }

    // ===========================================================================================
    // Helpers
    // ===========================================================================================

    /// <summary>
    /// Runs the Fair Housing review on the outgoing <paramref name="subject"/>+<paramref name="body"/>
    /// and enforces the send gate:
    /// <list type="bullet">
    ///   <item>Flagged + NOT acknowledged → throws <see cref="FairHousingBlockedException"/> (→ 422).</item>
    ///   <item>Flagged + acknowledged → logs a Warning (who/where + the flagged phrases) and proceeds.</item>
    ///   <item>Compliant → proceeds silently.</item>
    ///   <item>Review unavailable/errored (<c>Reviewed == false</c>) → fail-open: logs and proceeds, so an
    ///         AI outage never blocks legitimate landlord mail (mirrors AiController's tolerance).</item>
    /// </list>
    /// One review call per send — no looping.
    /// </summary>
    private async Task EnforceFairHousingGateAsync(
        int portfolioId, int tenantId, int? conversationId,
        string subject, string body, bool acknowledged, CancellationToken ct)
    {
        // Review the full outgoing copy (subject + body) the tenant will actually see.
        var text = string.IsNullOrWhiteSpace(subject) ? body : $"{subject}\n\n{body}";
        var review = await _fairHousing.ReviewAsync(text, ct);

        // Fail-open: an unavailable or errored review (no AI key, provider hiccup, unparseable output)
        // must not block the send. ReviewAsync already swallows provider errors into Reviewed=false.
        if (!review.Reviewed)
        {
            _logger.LogInformation(
                "Fair Housing review unavailable for send (portfolio {PortfolioId}, tenant {TenantId}, conversation {ConversationId}); proceeding without screening.",
                portfolioId, tenantId, conversationId);
            return;
        }

        if (review.Compliant)
        {
            return; // Clean — nothing to gate.
        }

        var concerns = review.Issues
            .Select(i => new FairHousingConcern(i.Phrase, i.Concern))
            .ToList();

        if (!acknowledged)
        {
            // Block: surface the concerns to the human (controller maps to 422).
            throw new FairHousingBlockedException(concerns);
        }

        // Acknowledged override: a human consciously chose to send flagged copy. Record it.
        _logger.LogWarning(
            "Fair Housing-flagged message SENT via acknowledged override (portfolio {PortfolioId}, tenant {TenantId}, conversation {ConversationId}). Flagged phrases: {Phrases}",
            portfolioId, tenantId, conversationId,
            string.Join(" | ", concerns.Select(c => c.Phrase)));
    }

}
