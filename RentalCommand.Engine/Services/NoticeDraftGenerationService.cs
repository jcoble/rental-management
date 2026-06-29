using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <inheritdoc cref="INoticeDraftGenerationService"/>
/// <remarks>
/// Runs inside a fresh DI scope (scoped <see cref="RentalCommandDbContext"/>). Delegates the actual
/// per-portfolio draft composition (and its LLM copy + de-dup idempotency) to the shared
/// <see cref="INoticeDraftService"/> from the Api project — the same service the manual
/// "Generate" button calls — so behaviour is identical whether triggered by the worker or the user.
/// </remarks>
public sealed class NoticeDraftGenerationService : INoticeDraftGenerationService
{
    private readonly RentalCommandDbContext _db;
    private readonly INoticeDraftService _notices;
    private readonly INotificationSettingsService _settings;
    private readonly ILogger<NoticeDraftGenerationService> _logger;

    public NoticeDraftGenerationService(
        RentalCommandDbContext db,
        INoticeDraftService notices,
        INotificationSettingsService settings,
        ILogger<NoticeDraftGenerationService> logger)
    {
        _db = db;
        _notices = notices;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAllAsync(CancellationToken ct = default)
    {
        var portfolioIds = await _db.Portfolios
            .Where(p => p.DeletedAt == null)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var created = 0;
        foreach (var portfolioId in portfolioIds)
        {
            ct.ThrowIfCancellationRequested();

            // NoticeAutopilot is a per-portfolio master gate now.
            var cfg = await _settings.GetRuntimeAsync(portfolioId, ct);
            if (!cfg.EnableNoticeAutopilot)
            {
                _logger.LogDebug("notice autopilot disabled for portfolio {PortfolioId}", portfolioId);
                continue;
            }

            try
            {
                // GenerateAsync is idempotent (it skips notice types that already have an open Draft
                // for the lease), so re-running daily never produces duplicates. The autopilot runs
                // portfolio-wide (no per-tenant/type scoping).
                var result = await _notices.GenerateAsync(portfolioId, ct: ct);
                created += result.CreatedCount;

                // Auto-send: for each freshly-created draft whose type is set to AutoSend AND has an
                // active template, approve+send it now. Gated by NotifyTenants. Load the portfolio's
                // settings + active-template types once (DB-side) — no per-draft queries.
                if (cfg.NotifyTenants && result.Drafts.Count > 0)
                {
                    var settings = await _db.NotificationSettings
                        .AsNoTracking()
                        .FirstOrDefaultAsync(s => s.PortfolioId == portfolioId, ct);

                    var templatedTypes = await _db.NoticeTemplates
                        .AsNoTracking()
                        .Where(t => t.PortfolioId == portfolioId && t.IsActive)
                        .Select(t => t.NoticeType)
                        .ToListAsync(ct);
                    var templated = templatedTypes.ToHashSet();

                    foreach (var draft in result.Drafts)
                    {
                        if (draft.Status != "Draft") continue;
                        if (!AutoSendEnabled(settings, draft.NoticeType)) continue;
                        if (!templated.Contains(draft.NoticeType)) continue;

                        var channels = ChannelsFor(cfg, draft.NoticeType);
                        if (channels.Count == 0) continue;

                        try
                        {
                            await _notices.ApproveAsync(
                                portfolioId,
                                draft.Id,
                                new RentalCommand.Api.DTOs.ApproveNoticeDraftRequest { Channels = channels },
                                ct);
                        }
                        catch (Exception ex)
                        {
                            // A blocked/failed auto-send leaves the draft for manual review.
                            _logger.LogWarning(ex, "Auto-send failed for draft {DraftId} ({Type}); left as draft.", draft.Id, draft.NoticeType);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Isolate one portfolio's failure so the rest of the run still proceeds.
                _logger.LogWarning(ex, "Notice autopilot failed for portfolio {PortfolioId}; continuing.", portfolioId);
            }
        }

        if (created > 0)
            _logger.LogInformation("Notice autopilot created {Count} draft(s) across {Portfolios} portfolio(s).",
                created, portfolioIds.Count);

        return created;
    }

    private static bool AutoSendEnabled(Core.Entities.NotificationSettings? s, string noticeType) => s != null && noticeType switch
    {
        "RentReminder" => s.AutoSendRentReminder,
        "RenewalOffer" => s.AutoSendRenewal,
        "MonthToMonthConversion" => s.AutoSendMonthToMonth,
        "MoveOutReminder" => s.AutoSendMoveOut,
        "LateRentNotice" => s.AutoSendLateRent,
        _ => false,
    };

    private static List<string> ChannelsFor(NotificationsConfig cfg, string noticeType)
    {
        var category = noticeType switch
        {
            "RentReminder" => Core.Enums.NotificationType.RentCharge,
            "LateRentNotice" => Core.Enums.NotificationType.LateFee,
            _ => Core.Enums.NotificationType.LeaseExpiry, // renewal / month-to-month / move-out
        };
        var pref = cfg.ResolveChannels(category);
        var channels = new List<string>();
        if (pref.EnableInApp) channels.Add("Portal");
        if (pref.EnableEmail) channels.Add("Email");
        if (pref.EnableSms) channels.Add("Sms");
        return channels;
    }
}
