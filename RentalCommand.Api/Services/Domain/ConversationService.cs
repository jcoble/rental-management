using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IConversationService"/>
public class ConversationService : IConversationService
{
    private const string EntityType = "Conversation";
    private const int PreviewMaxLength = 280;

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IFairHousingReviewService _fairHousing;
    private readonly ILogger<ConversationService> _logger;
    private readonly TimeProvider _timeProvider;

    public ConversationService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IFairHousingReviewService fairHousing,
        ILogger<ConversationService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _fairHousing = fairHousing;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    // ===========================================================================================
    // Landlord
    // ===========================================================================================

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(int portfolioId, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, new ListQuery(), ct);
        return page.Items;
    }

    public async Task<ConversationListResponse> ListPageAsync(int portfolioId, ListQuery query, CancellationToken ct = default)
    {
        var summaries = ProjectSummaries(
            _db.Conversations
                .AsNoTracking()
                .Where(c => c.PortfolioId == portfolioId),
            tenantViewer: false);

        summaries = query.SortField switch
        {
            "tenantname" => query.SortDescending ? summaries.OrderByDescending(c => c.TenantName) : summaries.OrderBy(c => c.TenantName),
            "subject" => query.SortDescending ? summaries.OrderByDescending(c => c.Subject) : summaries.OrderBy(c => c.Subject),
            "unreadcount" => query.SortDescending ? summaries.OrderByDescending(c => c.UnreadCount) : summaries.OrderBy(c => c.UnreadCount),
            "messagecount" => query.SortDescending ? summaries.OrderByDescending(c => c.MessageCount) : summaries.OrderBy(c => c.MessageCount),
            "lastmessageat" => query.SortDescending ? summaries.OrderByDescending(c => c.LastMessageAt) : summaries.OrderBy(c => c.LastMessageAt),
            _ => summaries.OrderByDescending(c => c.LastMessageAt),
        };

        var totalCount = await summaries.CountAsync(ct);

        var items = await summaries
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

    public async Task<int> GetUnreadCountAsync(int portfolioId, CancellationToken ct = default) =>
        await _db.Conversations
            .AsNoTracking()
            .Where(c => c.PortfolioId == portfolioId)
            .SumAsync(c => (int?)c.LandlordUnreadCount, ct) ?? 0;

    public async Task<ConversationDetail?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == id && c.PortfolioId == portfolioId, ct);

        if (entity == null)
        {
            return null;
        }

        // Mark read for the landlord.
        if (entity.LandlordUnreadCount != 0)
        {
            entity.LandlordUnreadCount = 0;
            await _db.SaveChangesAsync(ct);
        }

        return await LoadDetailAsync(portfolioId, id, tenantId: null, tenantViewer: false, ct);
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
                    SenderRole = m.SenderRole == ConversationSenderRole.Landlord ? "Landlord" : "Tenant",
                    Body = m.Body,
                    Channels = m.Channels,
                    CreatedAt = m.CreatedAt,
                })
                .ToList(),
        });
    }

    public async Task<ConversationDetail?> StartAsync(
        int portfolioId, int tenantId, string subject, string body, List<string> channels,
        bool acknowledgedFairHousingReview = false, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.PortfolioId == portfolioId && t.DeletedAt == null, ct);

        if (tenant == null)
        {
            return null; // → controller 404
        }

        // Fair Housing gate: screen the outgoing copy before it leaves the building. Throws a
        // FairHousingBlockedException (→ 422) when flagged and not acknowledged; otherwise (clean,
        // acknowledged-override, or review-unavailable) falls through and the send proceeds.
        await EnforceFairHousingGateAsync(
            portfolioId, tenantId, conversationId: null, subject, body, acknowledgedFairHousingReview, ct);

        var now = _timeProvider.UtcNow();
        var preview = Preview(body);

        var conversation = new Conversation
        {
            PortfolioId = portfolioId,
            TenantId = tenant.Id,
            Subject = subject,
            StartedByLandlord = true,
            CreatedAt = now,
            LastMessageAt = now,
            LastMessagePreview = preview,
            LandlordUnreadCount = 0,
            TenantUnreadCount = 1,
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var actualChannels = FanOut(portfolioId, tenant, subject, body, channels, now);

        var message = new ConversationMessage
        {
            SenderRole = ConversationSenderRole.Landlord,
            Body = body,
            Channels = actualChannels,
            CreatedAt = now,
        };
        conversation.Messages.Add(message);

        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(ct);

        var tenantNotification = await CreateLandlordMessageNotificationAsync(
            portfolioId, tenant.Id, conversation, subject, body, actualChannels, now, ct);
        if (tenantNotification is not null)
        {
            _db.Notifications.Add(tenantNotification);
            await _db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        conversation.Tenant = tenant;
        var detail = await LoadDetailAsync(portfolioId, conversation.Id, tenantId: null, tenantViewer: false, ct);
        if (detail is null)
        {
            return null;
        }
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, conversation.Id, detail, ct);
        if (tenantNotification is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId, "Notification", tenantNotification.Id, NotificationResponse.FromEntity(tenantNotification), ct);
        }
        return detail;
    }

    public async Task<ConversationDetail?> PostMessageAsync(
        int portfolioId, int id, string body, List<string> channels, CancellationToken ct = default)
    {
        var conversation = await _db.Conversations
            .Include(c => c.Tenant)
            .Include(c => c.Property)
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id && c.PortfolioId == portfolioId, ct);

        if (conversation == null)
        {
            return null;
        }

        var now = _timeProvider.UtcNow();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var actualChannels = conversation.Tenant != null
            ? FanOut(portfolioId, conversation.Tenant, conversation.Subject, body, channels, now)
            : string.Empty;

        conversation.Messages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            SenderRole = ConversationSenderRole.Landlord,
            Body = body,
            Channels = actualChannels,
            CreatedAt = now,
        });
        conversation.LastMessageAt = now;
        conversation.LastMessagePreview = Preview(body);
        conversation.TenantUnreadCount += 1;
        var tenantNotification = conversation.Tenant is not null
            ? await CreateLandlordMessageNotificationAsync(
                portfolioId, conversation.Tenant.Id, conversation, conversation.Subject, body, actualChannels, now, ct)
            : null;
        if (tenantNotification is not null)
        {
            _db.Notifications.Add(tenantNotification);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var detail = await LoadDetailAsync(portfolioId, conversation.Id, tenantId: null, tenantViewer: false, ct);
        if (detail is null)
        {
            return null;
        }
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, conversation.Id, detail, ct);
        if (tenantNotification is not null)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(
                portfolioId, "Notification", tenantNotification.Id, NotificationResponse.FromEntity(tenantNotification), ct);
        }
        return detail;
    }

    // ===========================================================================================
    // Tenant
    // ===========================================================================================

    public async Task<IReadOnlyList<ConversationSummary>> ListForTenantAsync(
        int portfolioId, int tenantId, CancellationToken ct = default)
    {
        return await ProjectSummaries(
                _db.Conversations
                    .AsNoTracking()
                    .Where(c => c.PortfolioId == portfolioId && c.TenantId == tenantId),
                tenantViewer: true)
            .OrderByDescending(c => c.LastMessageAt)
            .ToListAsync(ct);
    }

    public async Task<ConversationDetail?> GetForTenantAsync(
        int portfolioId, int tenantId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Conversations
            .Include(c => c.Tenant)
            .Include(c => c.Property)
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id && c.PortfolioId == portfolioId && c.TenantId == tenantId, ct);

        if (entity == null)
        {
            return null;
        }

        if (entity.TenantUnreadCount != 0)
        {
            entity.TenantUnreadCount = 0;
            await _db.SaveChangesAsync(ct);
        }

        return await LoadDetailAsync(portfolioId, id, tenantId, tenantViewer: true, ct);
    }

    public async Task<ConversationDetail?> TenantStartAsync(
        int portfolioId, int tenantId, string subject, string body, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.PortfolioId == portfolioId && t.DeletedAt == null, ct);

        if (tenant == null)
        {
            return null;
        }

        var now = _timeProvider.UtcNow();
        var conversation = new Conversation
        {
            PortfolioId = portfolioId,
            TenantId = tenant.Id,
            Subject = subject,
            StartedByLandlord = false,
            CreatedAt = now,
            LastMessageAt = now,
            LastMessagePreview = Preview(body),
            LandlordUnreadCount = 1,
            TenantUnreadCount = 0,
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        conversation.Messages.Add(new ConversationMessage
        {
            SenderRole = ConversationSenderRole.Tenant,
            Body = body,
            Channels = null, // tenant messages are in-app only
            CreatedAt = now,
        });

        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(ct);

        var notifications = await CreateTenantMessageNotificationsAsync(portfolioId, conversation, tenant, now, ct);
        _db.Notifications.AddRange(notifications);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        conversation.Tenant = tenant;
        var detail = await LoadDetailAsync(portfolioId, conversation.Id, tenantId, tenantViewer: true, ct);
        if (detail is null)
        {
            return null;
        }
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, conversation.Id, detail, ct);
        foreach (var notification in notifications)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, "Notification", notification.Id, NotificationResponse.FromEntity(notification), ct);
        }
        return detail;
    }

    public async Task<ConversationDetail?> TenantPostAsync(
        int portfolioId, int tenantId, int id, string body, CancellationToken ct = default)
    {
        var conversation = await _db.Conversations
            .Include(c => c.Tenant)
            .Include(c => c.Property)
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id && c.PortfolioId == portfolioId && c.TenantId == tenantId, ct);

        if (conversation == null)
        {
            return null;
        }

        var now = _timeProvider.UtcNow();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        conversation.Messages.Add(new ConversationMessage
        {
            ConversationId = conversation.Id,
            SenderRole = ConversationSenderRole.Tenant,
            Body = body,
            Channels = null,
            CreatedAt = now,
        });
        conversation.LastMessageAt = now;
        conversation.LastMessagePreview = Preview(body);
        conversation.LandlordUnreadCount += 1;
        var notifications = await CreateTenantMessageNotificationsAsync(portfolioId, conversation, conversation.Tenant, now, ct);
        _db.Notifications.AddRange(notifications);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var detail = await LoadDetailAsync(portfolioId, conversation.Id, tenantId, tenantViewer: true, ct);
        if (detail is null)
        {
            return null;
        }
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, conversation.Id, detail, ct);
        foreach (var notification in notifications)
        {
            await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, "Notification", notification.Id, NotificationResponse.FromEntity(notification), ct);
        }
        return detail;
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

    /// <summary>
    /// Queues email/SMS outbox rows for the requested channels the tenant has contact info for and
    /// returns the comma-separated list of channels actually used (always includes "Portal" when
    /// requested, since the in-app record IS the portal delivery). Mirrors the existing outbox payload
    /// shapes exactly: email = { to, subject, body }, sms = { to, message }.
    /// </summary>
    private string FanOut(
        int portfolioId, Tenant tenant, string subject, string body, List<string> channels, DateTime now)
    {
        var requested = (channels ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(NormalizeChannel)
            .Where(c => c != null)
            .Select(c => c!)
            .Distinct()
            .ToList();

        var actual = new List<string>();

        if (requested.Contains("Portal"))
        {
            actual.Add("Portal");
        }

        if (requested.Contains("Email") && !string.IsNullOrWhiteSpace(tenant.Email))
        {
            _db.OutboxMessages.Add(new OutboxMessage
            {
                PortfolioId = portfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new { to = tenant.Email, subject, body }),
                IdempotencyKey = $"conversation:{portfolioId}:{tenant.Id}:email:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(subject + "\n" + body)))}",
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
            actual.Add("Email");
        }

        if (requested.Contains("Sms") && !string.IsNullOrWhiteSpace(tenant.Phone))
        {
            _db.OutboxMessages.Add(new OutboxMessage
            {
                PortfolioId = portfolioId,
                MessageType = "sms",
                Payload = JsonSerializer.Serialize(new { to = tenant.Phone, message = body }),
                IdempotencyKey = $"conversation:{portfolioId}:{tenant.Id}:sms:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)))}",
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
            actual.Add("Sms");
        }

        return string.Join(",", actual);
    }

    private static string? NormalizeChannel(string channel) => channel.Trim().ToLowerInvariant() switch
    {
        "portal" => "Portal",
        "email" => "Email",
        "sms" => "Sms",
        _ => null,
    };

    private async Task<IReadOnlyList<Notification>> CreateTenantMessageNotificationsAsync(
        int portfolioId,
        Conversation conversation,
        Tenant? tenant,
        DateTime now,
        CancellationToken ct)
    {
        var tenantName = tenant != null ? $"{tenant.FirstName} {tenant.LastName}".Trim() : "Tenant";
        if (string.IsNullOrWhiteSpace(tenantName)) tenantName = "Tenant";

        var staffUserIds = await StaffUserIdsAsync(portfolioId, ct);

        return staffUserIds.Select(userId => new Notification
            {
                PortfolioId = portfolioId,
                UserId = userId,
                Type = "TenantMessage",
                Title = $"New message from {tenantName}",
                Message = conversation.LastMessagePreview ?? conversation.Subject,
                Severity = "Info",
                ActionUrl = $"/messages?conversationId={conversation.Id}",
                RelatedEntityType = "Conversation",
                RelatedEntityId = conversation.Id,
                CreatedAt = now,
            })
            .ToList();
    }

    private async Task<IReadOnlyList<int>> StaffUserIdsAsync(int portfolioId, CancellationToken ct)
    {
        var staffRoles = new[] { nameof(UserRole.Admin), nameof(UserRole.Manager), nameof(UserRole.Agent) };

        return await (
                from user in _db.Users.AsNoTracking()
                join userRole in _db.UserRoles.AsNoTracking() on user.Id equals userRole.UserId
                join role in _db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where user.PortfolioId == portfolioId && role.Name != null && staffRoles.Contains(role.Name)
                select user.Id)
            .Distinct()
            .ToListAsync(ct);
    }

    private async Task<Notification?> CreateLandlordMessageNotificationAsync(
        int portfolioId,
        int tenantId,
        Conversation conversation,
        string subject,
        string body,
        string actualChannels,
        DateTime now,
        CancellationToken ct)
    {
        if (!actualChannels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("Portal", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var tenantUserId = await _db.Users
            .AsNoTracking()
            .Where(u => u.PortfolioId == portfolioId && u.TenantId == tenantId)
            .OrderBy(u => u.Id)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(ct);

        if (tenantUserId is null)
        {
            return null;
        }

        var preview = Preview(body) ?? subject;

        return new Notification
        {
            PortfolioId = portfolioId,
            UserId = tenantUserId,
            Type = "TenantNotice",
            Title = subject,
            Message = preview,
            Severity = "Info",
            ActionUrl = $"/portal/messages?conversation={conversation.Id}",
            RelatedEntityType = "Conversation",
            RelatedEntityId = conversation.Id,
            CreatedAt = now,
        };
    }

    private static string? Preview(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        return body.Length <= PreviewMaxLength ? body : body[..PreviewMaxLength];
    }

}
