using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

public sealed class DailyBriefingDeliveryService : IDailyBriefingDeliveryService
{
    private const string Purpose = "daily-briefing";
    private readonly RentalCommandDbContext _db;
    private readonly IDailyBriefingService _briefing;
    private readonly INotificationSettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DailyBriefingDeliveryService> _logger;

    public DailyBriefingDeliveryService(
        RentalCommandDbContext db,
        IDailyBriefingService briefing,
        INotificationSettingsService settings,
        TimeProvider timeProvider,
        ILogger<DailyBriefingDeliveryService> logger)
    {
        _db = db;
        _briefing = briefing;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> EnqueueDueAsync(DateTime? utcNow = null, CancellationToken ct = default)
    {
        var now = utcNow ?? _timeProvider.UtcNow();
        var portfolios = await _db.Portfolios
            .Where(p => p.DeletedAt == null)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        var queued = 0;
        foreach (var portfolio in portfolios)
        {
            // Settings (gate, recipients, send-hour, channel matrix) are per-portfolio now.
            var config = await _settings.GetRuntimeAsync(portfolio.Id, ct);
            if (!config.EnableDailyBriefingMessages)
                continue;

            var channels = config.ResolveChannels(NotificationType.DailyBriefing);
            var smsRecipients = channels.EnableSms ? CleanRecipients(config.DailyBriefing.SmsRecipients) : [];
            var emailRecipients = channels.EnableEmail ? CleanRecipients(config.DailyBriefing.EmailRecipients) : [];
            if (smsRecipients.Count == 0 && emailRecipients.Count == 0)
                continue;

            var localNow = ToPortfolioLocalTime(now, portfolio.TimeZone);
            if (localNow.Hour < config.DailyBriefing.SendHourLocal)
                continue;

            var dateKey = localNow.Date.ToString("yyyy-MM-dd");
            var dedupKey = $"{Purpose}:{portfolio.Id}:{dateKey}";
            if (await AlreadyQueuedAsync(dedupKey, ct))
                continue;

            var briefing = await _briefing.ComposeForSystemAutomationAsync(portfolio.Id, ct);
            if (!config.DailyBriefing.IncludeEmptyBriefing &&
                briefing.Bullets.Count == 0 &&
                string.IsNullOrWhiteSpace(briefing.Summary))
            {
                continue;
            }

            var body = ComposeBody(portfolio.Name, dateKey, briefing);
            foreach (var to in smsRecipients)
            {
                _db.OutboxMessages.Add(new OutboxMessage
                {
                    PortfolioId = portfolio.Id,
                    MessageType = "sms",
                    IdempotencyKey = $"{dedupKey}:sms:{to}",
                    Payload = JsonSerializer.Serialize(new
                    {
                        purpose = Purpose,
                        date = dateKey,
                        to,
                        message = body,
                    }),
                    CreatedAtUtc = now,
                    NextAttemptAtUtc = now,
                });
                queued++;
            }

            foreach (var to in emailRecipients)
            {
                _db.OutboxMessages.Add(new OutboxMessage
                {
                    PortfolioId = portfolio.Id,
                    MessageType = "email",
                    IdempotencyKey = $"{dedupKey}:email:{to}",
                    Payload = JsonSerializer.Serialize(new
                    {
                        purpose = Purpose,
                        date = dateKey,
                        to,
                        subject = $"Rental Command briefing for {dateKey}",
                        body,
                    }),
                    CreatedAtUtc = now,
                    NextAttemptAtUtc = now,
                });
                queued++;
            }
        }

        if (queued > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Queued {Count} daily briefing notification(s).", queued);
        }

        return queued;
    }

    // Single indexed lookup on DedupKey — no payload scan, no in-memory JSON parse, bounded regardless
    // of how many historical outbox rows the portfolio has accumulated.
    private async Task<bool> AlreadyQueuedAsync(string dedupKey, CancellationToken ct) =>
        await _db.OutboxMessages.AnyAsync(
            m => m.IdempotencyKey.StartsWith(dedupKey + ":"), ct);

    private static DateTime ToPortfolioLocalTime(DateTime utcNow, string? timeZoneId)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                string.IsNullOrWhiteSpace(timeZoneId) ? "America/New_York" : timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        }
        catch
        {
            return utcNow;
        }
    }

    private static string ComposeBody(string portfolioName, string dateKey, BriefingResponse briefing)
    {
        var sb = new StringBuilder();
        sb.Append("Rental Command");
        if (!string.IsNullOrWhiteSpace(portfolioName))
            sb.Append(" - ").Append(portfolioName);
        sb.Append(" briefing for ").Append(dateKey).Append(':');

        if (!string.IsNullOrWhiteSpace(briefing.Summary))
        {
            sb.Append(' ').Append(briefing.Summary.Trim());
        }

        foreach (var bullet in briefing.Bullets.Take(5))
        {
            sb.AppendLine();
            sb.Append("- ").Append(bullet.Title);
            if (!string.IsNullOrWhiteSpace(bullet.Detail))
                sb.Append(": ").Append(bullet.Detail);
        }

        if (briefing.Bullets.Count > 5)
            sb.AppendLine().Append("- Plus ").Append(briefing.Bullets.Count - 5).Append(" more item(s).");

        return sb.ToString();
    }

    private static List<string> CleanRecipients(IEnumerable<string>? values) =>
        (values ?? [])
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
