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
                // for the lease), so re-running daily never produces duplicates.
                var result = await _notices.GenerateAsync(portfolioId, ct);
                created += result.CreatedCount;
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
}
