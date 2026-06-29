using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public class NoticeTemplateService : INoticeTemplateService
{
    private static readonly string[] SupportedTypes =
        ["RentReminder", "RenewalOffer", "MonthToMonthConversion", "MoveOutReminder", "LateRentNotice"];

    private readonly RentalCommandDbContext _db;

    public NoticeTemplateService(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<NoticeTemplateResponse>> ListAsync(int portfolioId, CancellationToken ct = default)
    {
        var saved = await _db.NoticeTemplates
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.IsActive)
            .ToDictionaryAsync(t => t.NoticeType, ct);

        return SupportedTypes
            .Select(type => saved.TryGetValue(type, out var t) ? ToResponse(type, t) : EmptyResponse(type))
            .ToList();
    }

    public async Task<NoticeTemplateResponse> GetAsync(int portfolioId, string noticeType, CancellationToken ct = default)
    {
        EnsureSupported(noticeType);
        var t = await _db.NoticeTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PortfolioId == portfolioId && x.NoticeType == noticeType && x.IsActive, ct);
        return t == null ? EmptyResponse(noticeType) : ToResponse(noticeType, t);
    }

    public async Task<NoticeTemplateResponse> UpsertAsync(int portfolioId, string noticeType, UpsertNoticeTemplateRequest request, CancellationToken ct = default)
    {
        EnsureSupported(noticeType);
        var existing = await _db.NoticeTemplates
            .FirstOrDefaultAsync(x => x.PortfolioId == portfolioId && x.NoticeType == noticeType && x.IsActive, ct);

        var now = DateTime.UtcNow;
        if (existing == null)
        {
            existing = new NoticeTemplate
            {
                PortfolioId = portfolioId,
                NoticeType = noticeType,
                Subject = (request.Subject ?? "").Trim(),
                Body = (request.Body ?? "").Trim(),
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.NoticeTemplates.Add(existing);
        }
        else
        {
            existing.Subject = (request.Subject ?? "").Trim();
            existing.Body = (request.Body ?? "").Trim();
            existing.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return ToResponse(noticeType, existing);
    }

    private static void EnsureSupported(string noticeType)
    {
        if (!SupportedTypes.Contains(noticeType))
            throw new ArgumentException($"Unknown notice type '{noticeType}'.", nameof(noticeType));
    }

    private static NoticeTemplateResponse EmptyResponse(string type) => new()
    {
        NoticeType = type,
        Subject = "",
        Body = "",
        HasTemplate = false,
        AvailableFields = NoticeMergeFields.ForType(type),
        UpdatedAt = null,
    };

    private static NoticeTemplateResponse ToResponse(string type, NoticeTemplate t) => new()
    {
        NoticeType = type,
        Subject = t.Subject,
        Body = t.Body,
        HasTemplate = true,
        AvailableFields = NoticeMergeFields.ForType(type),
        UpdatedAt = t.UpdatedAt,
    };
}
