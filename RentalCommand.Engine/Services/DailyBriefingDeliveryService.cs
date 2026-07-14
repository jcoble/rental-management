using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Enqueues the landlord/team morning briefing through one receipt-backed command. PostgreSQL
/// resolves due workspaces, current routed recipients, preferences, destinations, and each
/// recipient's current authorized action list in one set-based query.
/// </summary>
public sealed class DailyBriefingDeliveryService : IDailyBriefingDeliveryService
{
    private static readonly AtomicJsonResultCodec<EnqueueMorningBriefingsResult> ResultCodec =
        new("notifications.morning-briefing.enqueue.v1");

    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DailyBriefingDeliveryService> _logger;

    public DailyBriefingDeliveryService(
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider,
        ILogger<DailyBriefingDeliveryService> logger)
    {
        _atomic = atomic;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> EnqueueDueAsync(DateTime? utcNow = null, CancellationToken ct = default)
    {
        var requestedUtc = DateTime.SpecifyKind(
            utcNow ?? _timeProvider.GetUtcNow().UtcDateTime,
            DateTimeKind.Utc);
        var evaluationUtc = new DateTime(
            requestedUtc.Year,
            requestedUtc.Month,
            requestedUtc.Day,
            requestedUtc.Hour,
            0,
            0,
            DateTimeKind.Utc);
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "notifications.morning-briefing.enqueue",
                $"utc-hour:{evaluationUtc:yyyyMMddHH}"),
            new EnqueueMorningBriefingsCommand(evaluationUtc),
            ResultCodec,
            ct);

        if (outcome.Value.QueuedCount > 0)
        {
            _logger.LogInformation(
                "Queued {Count} morning briefing delivery or deliveries ({Disposition}).",
                outcome.Value.QueuedCount,
                outcome.Disposition);
        }

        return outcome.Value.QueuedCount;
    }

    internal static string DestinationHash(string destination) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination.Trim())))
            .ToLowerInvariant()[..24];

    internal static string ComposeBody(
        string portfolioName,
        string localDate,
        IReadOnlyList<BriefingDigestItem> items)
    {
        var body = new StringBuilder()
            .Append("Rental Command - ")
            .Append(portfolioName)
            .Append(" briefing for ")
            .Append(localDate)
            .Append(':');

        if (items.Count == 0)
        {
            return body.Append(" Nothing currently needs your attention.").ToString();
        }

        foreach (var item in items)
        {
            var (title, detail) = FormatItem(item, DateOnly.Parse(localDate));
            body.AppendLine().Append("- ").Append(title);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                body.Append(": ").Append(detail);
            }
        }

        return body.ToString();
    }

    private static (string Title, string Detail) FormatItem(BriefingDigestItem item, DateOnly today)
    {
        var date = item.EventDateOnly ?? (item.EventDateTime is DateTime timestamp
            ? DateOnly.FromDateTime(timestamp)
            : today);
        var rental = string.IsNullOrWhiteSpace(item.TenantName)
            ? $"{item.PropertyName}, Unit {item.UnitNumber}"
            : $"{item.TenantName} - {item.PropertyName}, Unit {item.UnitNumber}";
        return item.Category switch
        {
            "Maintenance" => ($"Emergency: {item.TitleText}", item.DetailText ?? string.Empty),
            "RentLate" => ($"Rent overdue - {rental}",
                $"${item.Amount:N0} was due {today.DayNumber - date.DayNumber} day(s) ago"),
            "RentDue" => ($"Rent due today - {rental}", $"${item.Amount:N0} due"),
            "Appointment" => ($"Appointment today: {item.TitleText}",
                item.EventDateTime is DateTime at
                    ? $"{(AppointmentType)item.TypeValue} at {at:h:mm tt}"
                    : ((AppointmentType)item.TypeValue).ToString()),
            "Inspection" => ($"Inspection - {(InspectionType)item.TypeValue}", $"Scheduled for {date:MMM d}"),
            "LeaseExpiring" => ($"Lease expiring - {rental}",
                $"Ends {date:MMM d} ({date.DayNumber - today.DayNumber} day(s) left)"),
            _ => (item.TitleText ?? item.Category, item.DetailText ?? string.Empty),
        };
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

public sealed record EnqueueMorningBriefingsCommand(DateTime EvaluationUtc) : IAtomicCommandData;

public sealed record EnqueueMorningBriefingsResult(int QueuedCount) : IAtomicResultData;

public sealed class EnqueueMorningBriefingsHandler
    : IAtomicCommandHandler<EnqueueMorningBriefingsCommand, EnqueueMorningBriefingsResult>
{
    private const string Purpose = "morning-briefing";

    public async Task<EnqueueMorningBriefingsResult> HandleAsync(
        EnqueueMorningBriefingsCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.EvaluationUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Morning briefing evaluation time must be UTC.");
        }

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var digests = await attempt.Notifications.ReadDueMorningBriefingsAsync(command.EvaluationUtc, ct);
        var queued = 0;

        foreach (var digest in digests)
        {
            var items = JsonSerializer.Deserialize<List<BriefingDigestItem>>(
                digest.ItemsJson,
                DailyBriefingDeliveryService.JsonOptions) ?? [];
            var body = DailyBriefingDeliveryService.ComposeBody(
                digest.PortfolioName,
                digest.LocalDate,
                items);
            var subject = $"Rental Command morning briefing for {digest.LocalDate}";

            if (digest.EnableEmail && !string.IsNullOrWhiteSpace(digest.Email))
            {
                StageOutbox(attempt, digest, "email", digest.Email,
                    new { to = digest.Email, subject, body }, now);
                queued++;
            }

            if (digest.EnableSms && !string.IsNullOrWhiteSpace(digest.PhoneNumber))
            {
                StageOutbox(attempt, digest, "sms", digest.PhoneNumber,
                    new { to = digest.PhoneNumber, message = body }, now);
                queued++;
            }

            if (digest.EnablePush)
            {
                var tokens = JsonSerializer.Deserialize<List<string>>(digest.DeviceTokensJson) ?? [];
                foreach (var token in tokens)
                {
                    StageOutbox(attempt, digest, "push", token, new
                    {
                        deviceToken = token,
                        title = subject,
                        body,
                        actionUrl = "/today",
                        type = "MorningBriefing",
                    }, now);
                    queued++;
                }
            }
        }

        return new EnqueueMorningBriefingsResult(queued);
    }

    private static void StageOutbox(
        IAtomicWriteAttempt attempt,
        AtomicMorningBriefingDigest digest,
        string channel,
        string destination,
        object payload,
        DateTime now)
    {
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = digest.PortfolioId,
            MessageType = channel,
            Payload = JsonSerializer.Serialize(payload),
            IdempotencyKey = $"{Purpose}:{digest.PortfolioId}:{digest.UserId}:{digest.LocalDate}:{channel}:" +
                             DailyBriefingDeliveryService.DestinationHash(destination),
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }
}

public sealed record BriefingDigestItem(
    string Category,
    int SeverityOrder,
    string EntityType,
    int EntityId,
    int? UnitId,
    string? TitleText,
    string? DetailText,
    string? LeaseNumber,
    string? UnitNumber,
    string? TenantName,
    string? PropertyName,
    decimal Amount,
    DateTime? EventDateTime,
    DateOnly? EventDateOnly,
    int TypeValue);
