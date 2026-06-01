using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IMessageService"/>
public class MessageService : IMessageService
{
    private const string EntityType = "PortalMessage";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;

    public MessageService(RentalCommandDbContext db, IDataUpdateService dataUpdate)
    {
        _db = db;
        _dataUpdate = dataUpdate;
    }

    public async Task<IReadOnlyList<MessageResponse>> ListAsync(int portfolioId, string? status, CancellationToken ct = default)
    {
        var q = _db.PortalMessages
            .AsNoTracking()
            .Include(m => m.Property)
            .Include(m => m.Unit)
            .Include(m => m.RecipientTenant)
            .Include(m => m.UserAccount)
                .ThenInclude(ua => ua == null ? null : ua.Tenant)
            .Where(m => m.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<PortalMessageStatus>(status, ignoreCase: true, out var parsedStatus))
        {
            q = q.Where(m => m.Status == parsedStatus);
        }

        var messages = await q
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);

        return messages.Select(ToResponse).ToList();
    }

    public async Task<MessageResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var entity = await _db.PortalMessages
            .AsNoTracking()
            .Include(m => m.Property)
            .Include(m => m.Unit)
            .Include(m => m.RecipientTenant)
            .Include(m => m.UserAccount)
                .ThenInclude(ua => ua == null ? null : ua.Tenant)
            .FirstOrDefaultAsync(m => m.Id == id && m.PortfolioId == portfolioId, ct);

        return entity == null ? null : ToResponse(entity);
    }

    public async Task<MessageResponse?> ReplyAsync(int portfolioId, int id, ReplyMessageRequest request, CancellationToken ct = default)
    {
        var entity = await _db.PortalMessages
            .Include(m => m.Property)
            .Include(m => m.Unit)
            .Include(m => m.RecipientTenant)
            .Include(m => m.UserAccount)
                .ThenInclude(ua => ua == null ? null : ua.Tenant)
            .FirstOrDefaultAsync(m => m.Id == id && m.PortfolioId == portfolioId, ct);

        if (entity == null)
        {
            return null;
        }

        entity.Reply = request.Reply;
        entity.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.Status) &&
            Enum.TryParse<PortalMessageStatus>(request.Status, ignoreCase: true, out var parsedStatus))
        {
            entity.Status = parsedStatus;
        }
        else
        {
            entity.Status = PortalMessageStatus.InProgress;
        }

        await _db.SaveChangesAsync(ct);

        var response = ToResponse(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<MessageResponse?> UpdateStatusAsync(int portfolioId, int id, string status, CancellationToken ct = default)
    {
        if (!Enum.TryParse<PortalMessageStatus>(status, ignoreCase: true, out var parsedStatus))
        {
            throw new ArgumentException($"Invalid status value: '{status}'.");
        }

        var entity = await _db.PortalMessages
            .Include(m => m.Property)
            .Include(m => m.Unit)
            .Include(m => m.RecipientTenant)
            .Include(m => m.UserAccount)
                .ThenInclude(ua => ua == null ? null : ua.Tenant)
            .FirstOrDefaultAsync(m => m.Id == id && m.PortfolioId == portfolioId, ct);

        if (entity == null)
        {
            return null;
        }

        entity.Status = parsedStatus;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        var response = ToResponse(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<MessageResponse?> CreateToTenantAsync(
        int portfolioId, int senderUserId, CreateMessageRequest request, CancellationToken ct = default)
    {
        // Validate the recipient tenant is in this portfolio (and not soft-deleted).
        var tenant = await _db.Tenants
            .FirstOrDefaultAsync(
                t => t.Id == request.TenantId && t.PortfolioId == portfolioId && t.DeletedAt == null, ct);

        if (tenant == null)
        {
            // Not in this portfolio → controller returns 404.
            return null;
        }

        // Normalise the requested channels (de-duplicated, canonical casing).
        var requested = request.Channels
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(NormalizeChannel)
            .Where(c => c != null)
            .Select(c => c!)
            .Distinct()
            .ToList();

        var actualChannels = new List<string>();

        // Author resolution: a landlord authenticates as an ApplicationUser, which is a distinct
        // identity from the portal UserAccount keyed on PortalMessage. There is no reliable FK to
        // resolve one to the other, so landlord-authored messages store UserAccountId = null and
        // carry FromLandlord = true (UserAccountId was made nullable for exactly this case).
        var now = DateTime.UtcNow;
        var entity = new PortalMessage
        {
            PortfolioId = portfolioId,
            UserAccountId = null,
            RecipientTenantId = tenant.Id,
            FromLandlord = true,
            Subject = request.Subject,
            Body = request.Body,
            Status = PortalMessageStatus.Open,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Portal delivery is the in-app row itself — no outbox needed.
        if (requested.Contains("Portal"))
        {
            actualChannels.Add("Portal");
        }

        // Email: only if requested AND the tenant has an address on file. Matches the existing
        // email outbox payload shape exactly: { to, subject, body }.
        if (requested.Contains("Email") && !string.IsNullOrWhiteSpace(tenant.Email))
        {
            _db.OutboxMessages.Add(new OutboxMessage
            {
                PortfolioId = portfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    to = tenant.Email,
                    subject = request.Subject,
                    body = request.Body,
                }),
                CreatedAt = now,
            });
            actualChannels.Add("Email");
        }

        // SMS: only if requested AND the tenant has a phone on file. Matches the existing SMS outbox
        // payload shape exactly: { to, message }.
        if (requested.Contains("Sms") && !string.IsNullOrWhiteSpace(tenant.Phone))
        {
            _db.OutboxMessages.Add(new OutboxMessage
            {
                PortfolioId = portfolioId,
                MessageType = "sms",
                Payload = JsonSerializer.Serialize(new
                {
                    to = tenant.Phone,
                    message = request.Body,
                }),
                CreatedAt = now,
            });
            actualChannels.Add("Sms");
        }

        entity.Channels = string.Join(",", actualChannels);
        _db.PortalMessages.Add(entity);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // RecipientTenant nav is already loaded (we fetched the tenant on this context).
        entity.RecipientTenant = tenant;
        var response = ToResponse(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    /// <summary>Map a case-insensitive channel string to its canonical form, or null if unrecognised.</summary>
    private static string? NormalizeChannel(string channel) => channel.Trim().ToLowerInvariant() switch
    {
        "portal" => "Portal",
        "email" => "Email",
        "sms" => "Sms",
        _ => null,
    };

    private static MessageResponse ToResponse(Core.Entities.PortalMessage m) => new()
    {
        Id = m.Id,
        PortfolioId = m.PortfolioId,
        PropertyId = m.PropertyId,
        PropertyName = m.Property?.Name,
        UnitId = m.UnitId,
        UnitLabel = m.Unit?.UnitNumber,
        UserAccountId = m.UserAccountId,
        SenderName = ResolveSenderName(m),
        FromLandlord = m.FromLandlord,
        Channels = m.Channels,
        RecipientTenantId = m.RecipientTenantId,
        RecipientTenantName = m.RecipientTenant != null
            ? $"{m.RecipientTenant.FirstName} {m.RecipientTenant.LastName}".Trim()
            : null,
        Subject = m.Subject,
        Body = m.Body,
        Status = m.Status.ToString(),
        Reply = m.Reply,
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
    };

    private static string? ResolveSenderName(Core.Entities.PortalMessage m)
    {
        // Landlord-authored messages have no portal UserAccount; surface a stable label.
        if (m.FromLandlord)
        {
            return "Landlord";
        }

        if (m.UserAccount == null)
        {
            return null;
        }

        // Prefer the tenant's full name when the UserAccount is linked to a tenant.
        if (m.UserAccount.Tenant != null)
        {
            var t = m.UserAccount.Tenant;
            return $"{t.FirstName} {t.LastName}".Trim();
        }

        return m.UserAccount.DisplayName;
    }
}
