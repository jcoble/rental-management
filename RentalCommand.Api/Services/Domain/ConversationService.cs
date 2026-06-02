using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IConversationService"/>
public class ConversationService : IConversationService
{
    private const string EntityType = "Conversation";
    private const int PreviewMaxLength = 280;

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public ConversationService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    // ===========================================================================================
    // Landlord
    // ===========================================================================================

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(int portfolioId, CancellationToken ct = default)
    {
        var rows = await _db.Conversations
            .AsNoTracking()
            .Include(c => c.Tenant)
            .Include(c => c.Property)
            .Where(c => c.PortfolioId == portfolioId)
            .OrderByDescending(c => c.LastMessageAt)
            .ToListAsync(ct);

        return rows.Select(c => ToSummary(c, c.LandlordUnreadCount)).ToList();
    }

    public async Task<ConversationDetail?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.Conversations
            .Include(c => c.Tenant)
            .Include(c => c.Property)
            .Include(c => c.Messages)
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

        return ToDetail(entity, entity.LandlordUnreadCount);
    }

    public async Task<ConversationDetail?> StartAsync(
        int portfolioId, int tenantId, string subject, string body, List<string> channels, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.PortfolioId == portfolioId && t.DeletedAt == null, ct);

        if (tenant == null)
        {
            return null; // → controller 404
        }

        var now = DateTime.UtcNow;
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
        await tx.CommitAsync(ct);

        conversation.Tenant = tenant;
        var detail = ToDetail(conversation, conversation.LandlordUnreadCount);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, conversation.Id, detail, ct);
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

        var now = DateTime.UtcNow;

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

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var detail = ToDetail(conversation, conversation.LandlordUnreadCount);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, conversation.Id, detail, ct);
        return detail;
    }

    // ===========================================================================================
    // Tenant
    // ===========================================================================================

    public async Task<IReadOnlyList<ConversationSummary>> ListForTenantAsync(
        int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var rows = await _db.Conversations
            .AsNoTracking()
            .Include(c => c.Tenant)
            .Include(c => c.Property)
            .Where(c => c.PortfolioId == portfolioId && c.TenantId == tenantId)
            .OrderByDescending(c => c.LastMessageAt)
            .ToListAsync(ct);

        return rows.Select(c => ToSummary(c, c.TenantUnreadCount)).ToList();
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

        return ToDetail(entity, entity.TenantUnreadCount);
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

        var now = DateTime.UtcNow;
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
        await tx.CommitAsync(ct);

        conversation.Tenant = tenant;
        var detail = ToDetail(conversation, conversation.TenantUnreadCount);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, conversation.Id, detail, ct);
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

        var now = DateTime.UtcNow;

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

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var detail = ToDetail(conversation, conversation.TenantUnreadCount);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, conversation.Id, detail, ct);
        return detail;
    }

    // ===========================================================================================
    // Helpers
    // ===========================================================================================

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
                CreatedAt = now,
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
                CreatedAt = now,
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

    private static string? Preview(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        return body.Length <= PreviewMaxLength ? body : body[..PreviewMaxLength];
    }

    private static ConversationSummary ToSummary(Conversation c, int viewerUnread) => new()
    {
        Id = c.Id,
        TenantId = c.TenantId,
        TenantName = c.Tenant != null ? $"{c.Tenant.FirstName} {c.Tenant.LastName}".Trim() : string.Empty,
        Subject = c.Subject,
        PropertyName = c.Property?.Name,
        LastMessagePreview = c.LastMessagePreview,
        LastMessageAt = c.LastMessageAt,
        UnreadCount = viewerUnread,
        MessageCount = c.Messages.Count,
    };

    private static ConversationDetail ToDetail(Conversation c, int viewerUnread) => new()
    {
        Id = c.Id,
        TenantId = c.TenantId,
        TenantName = c.Tenant != null ? $"{c.Tenant.FirstName} {c.Tenant.LastName}".Trim() : string.Empty,
        Subject = c.Subject,
        PropertyName = c.Property?.Name,
        LastMessagePreview = c.LastMessagePreview,
        LastMessageAt = c.LastMessageAt,
        UnreadCount = viewerUnread,
        MessageCount = c.Messages.Count,
        Messages = c.Messages
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new ConversationMessageDto
            {
                Id = m.Id,
                SenderRole = m.SenderRole.ToString(),
                Body = m.Body,
                Channels = m.Channels,
                CreatedAt = m.CreatedAt,
            })
            .ToList(),
    };
}
