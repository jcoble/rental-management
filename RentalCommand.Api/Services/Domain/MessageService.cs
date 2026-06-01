using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
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

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

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
        Subject = m.Subject,
        Body = m.Body,
        Status = m.Status.ToString(),
        Reply = m.Reply,
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
    };

    private static string? ResolveSenderName(Core.Entities.PortalMessage m)
    {
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
