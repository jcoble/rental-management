using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

public sealed class DailyBriefingDeliveryService : IDailyBriefingDeliveryService
{
    private const string Purpose = "daily-briefing";
    private readonly RentalCommandDbContext _db;
    private readonly IDailyBriefingService _briefing;
    private readonly NotificationsConfig _config;
    private readonly ILogger<DailyBriefingDeliveryService> _logger;

    public DailyBriefingDeliveryService(
        RentalCommandDbContext db,
        IDailyBriefingService briefing,
        IOptions<NotificationsConfig> config,
        ILogger<DailyBriefingDeliveryService> logger)
    {
        _db = db;
        _briefing = briefing;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<int> EnqueueDueAsync(DateTime? utcNow = null, CancellationToken ct = default)
    {
        if (!_config.EnableDailyBriefingMessages)
            return 0;

        var smsRecipients = CleanRecipients(_config.DailyBriefing.SmsRecipients);
        var emailRecipients = CleanRecipients(_config.DailyBriefing.EmailRecipients);
        if (smsRecipients.Count == 0 && emailRecipients.Count == 0)
            return 0;

        var now = utcNow ?? DateTime.UtcNow;
        var portfolios = await _db.Portfolios
            .Where(p => p.DeletedAt == null)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        var queued = 0;
        foreach (var portfolio in portfolios)
        {
            var localNow = ToPortfolioLocalTime(now, portfolio.TimeZone);
            if (localNow.Hour < _config.DailyBriefing.SendHourLocal)
                continue;

            var dateKey = localNow.Date.ToString("yyyy-MM-dd");
            if (await AlreadyQueuedAsync(portfolio.Id, dateKey, ct))
                continue;

            var briefing = await _briefing.ComposeAsync(portfolio.Id, ct);
            if (!_config.DailyBriefing.IncludeEmptyBriefing &&
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
                    Payload = JsonSerializer.Serialize(new
                    {
                        purpose = Purpose,
                        date = dateKey,
                        to,
                        message = body,
                    }),
                    CreatedAt = now,
                });
                queued++;
            }

            foreach (var to in emailRecipients)
            {
                _db.OutboxMessages.Add(new OutboxMessage
                {
                    PortfolioId = portfolio.Id,
                    MessageType = "email",
                    Payload = JsonSerializer.Serialize(new
                    {
                        purpose = Purpose,
                        date = dateKey,
                        to,
                        subject = $"Rental Command briefing for {dateKey}",
                        body,
                    }),
                    CreatedAt = now,
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

    private async Task<bool> AlreadyQueuedAsync(int portfolioId, string dateKey, CancellationToken ct)
    {
        var purposeNeedle = $"\"purpose\":\"{Purpose}\"";
        var dateNeedle = $"\"date\":\"{dateKey}\"";
        return await _db.OutboxMessages.AnyAsync(m =>
            m.PortfolioId == portfolioId &&
            (m.MessageType == "sms" || m.MessageType == "email") &&
            m.Payload.Contains(purposeNeedle) &&
            m.Payload.Contains(dateNeedle), ct);
    }

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
