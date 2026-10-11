using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Composes the Daily Briefing: a prioritized, plain-English summary of what needs attention
/// today across the portfolio, optionally polished by an LLM.
/// </summary>
public class DailyBriefingService : IDailyBriefingService
{
    private const int MaxBriefingBullets = 25;
    private static readonly TimeSpan DefaultLlmPolishTimeout = TimeSpan.FromSeconds(5);

    private readonly RentalCommandDbContext _db;
    private readonly ILlmProvider _llm;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _llmPolishTimeout;

    public DailyBriefingService(RentalCommandDbContext db, ILlmProvider llm, TimeProvider timeProvider)
        : this(db, llm, timeProvider, DefaultLlmPolishTimeout)
    {
    }

    internal DailyBriefingService(
        RentalCommandDbContext db,
        ILlmProvider llm,
        TimeProvider timeProvider,
        TimeSpan llmPolishTimeout)
    {
        _db = db;
        _llm = llm;
        _timeProvider = timeProvider;
        _llmPolishTimeout = llmPolishTimeout;
    }

    public Task<BriefingResponse> ComposeAsync(WorkspaceReadScope scope, CancellationToken ct = default)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        return ComposeCoreAsync(
            scope.PortfolioId,
            AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead, utcNow),
            AuthorizedProperties(scope, CapabilityKeys.RentalsRead, utcNow),
            AuthorizedProperties(scope, CapabilityKeys.WorkRead, utcNow),
            AuthorizedProperties(scope, CapabilityKeys.LeasingShowingsManage, utcNow),
            AuthorizedProperties(scope, CapabilityKeys.LeasingOnboardingManage, utcNow),
            ct);
    }

    private async Task<BriefingResponse> ComposeCoreAsync(
        int portfolioId,
        IQueryable<Property> moneyProperties,
        IQueryable<Property> rentalProperties,
        IQueryable<Property> workProperties,
        IQueryable<Property> showingProperties,
        IQueryable<Property> onboardingProperties,
        CancellationToken ct)
    {
        // TODO: portfolio-timezone handling is a future refinement; using UTC for now.
        var today = _timeProvider.UtcNow().Date;
        var tomorrow = today.AddDays(1);
        var nextWeekEnd = today.AddDays(8);
        var businessDate = DateOnly.FromDateTime(today);
        var sixtyDaysOut = businessDate.AddDays(60);
        var criticalOverdueCutoff = DateOnly.FromDateTime(today.AddDays(-5));

        // One canonical DB view supplies the authoritative source facts for both the on-demand
        // Today page and scheduled Morning Briefing. Date-window decisions remain parameterized
        // here so simulation/business time is never baked into the view definition.
        var candidates = await _db.MorningBriefingCandidateProjections
            .AsNoTracking()
            .Where(candidate => candidate.PortfolioId == portfolioId)
            .Where(candidate =>
                (candidate.RequiredCapability == CapabilityKeys.MoneyBalancesRead
                    && moneyProperties.Any(property => property.Id == candidate.PropertyId))
                || (candidate.RequiredCapability == CapabilityKeys.RentalsRead
                    && rentalProperties.Any(property => property.Id == candidate.PropertyId))
                || (candidate.RequiredCapability == CapabilityKeys.WorkRead
                    && workProperties.Any(property => property.Id == candidate.PropertyId))
                || (candidate.RequiredCapability == CapabilityKeys.LeasingShowingsManage
                    && showingProperties.Any(property => property.Id == candidate.PropertyId))
                || (candidate.RequiredCapability == CapabilityKeys.LeasingOnboardingManage
                    && onboardingProperties.Any(property => property.Id == candidate.PropertyId)))
            .Where(candidate =>
                candidate.Category == "Maintenance"
                || (candidate.Category == "RentLate" && candidate.EventDateOnly < businessDate)
                || (candidate.Category == "RentDue" && candidate.EventDateOnly == businessDate)
                || (candidate.Category == "Appointment"
                    && candidate.EventDateTime >= today && candidate.EventDateTime < tomorrow)
                || (candidate.Category == "Inspection"
                    && candidate.EventDateTime >= today && candidate.EventDateTime < nextWeekEnd)
                || (candidate.Category == "LeaseExpiring"
                    && candidate.EventDateOnly > businessDate && candidate.EventDateOnly <= sixtyDaysOut))
            .Select(candidate => new BriefingCandidate
            {
                SortOrder = candidate.SortOrder,
                SeverityOrder = candidate.Category == "RentLate" && candidate.EventDateOnly <= criticalOverdueCutoff
                    ? 0
                    : candidate.Category == "Inspection" && candidate.EventDateTime < today.AddDays(2)
                        ? 1
                        : candidate.SeverityOrder,
                Category = candidate.Category,
                EntityType = candidate.EntityType,
                EntityId = candidate.EntityId,
                UnitId = candidate.UnitId,
                TitleText = candidate.TitleText,
                DetailText = candidate.DetailText,
                LeaseNumber = candidate.LeaseNumber,
                UnitNumber = candidate.UnitNumber,
                TenantName = candidate.TenantName,
                PropertyName = candidate.PropertyName,
                Amount = candidate.Amount,
                EventDateTime = candidate.EventDateTime,
                EventDateOnly = candidate.EventDateOnly,
                TypeValue = candidate.TypeValue,
            })
            .OrderBy(c => c.SeverityOrder)
            .ThenBy(c => c.SortOrder)
            // SortOrder uniquely identifies a source category, so each ordered category has
            // exactly one populated event column. Keeping date-only and timestamp values typed
            // avoids a client DateOnly -> DateTime conversion before the SQL UNION.
            .ThenBy(c => c.EventDateOnly)
            .ThenBy(c => c.EventDateTime)
            .ThenBy(c => c.EntityId)
            .Take(MaxBriefingBullets)
            .ToListAsync(ct);

        var sortedBullets = candidates
            .Select(c => ToBullet(c, today))
            .ToList();

        var (summary, llmEnhanced) = await TryPolishSummaryAsync(sortedBullets, ct);

        return new BriefingResponse
        {
            Date = today,
            GeneratedAt = _timeProvider.UtcNow(),
            Summary = summary,
            LlmEnhanced = llmEnhanced,
            Bullets = sortedBullets,
        };
    }

    internal async Task<(string? Summary, bool Enhanced)> TryPolishSummaryAsync(
        IReadOnlyList<BriefingBullet> bullets,
        CancellationToken ct)
    {
        if (bullets.Count == 0) return (null, false);

        try
        {
            var bulletList = string.Join(
                "\n",
                bullets.Select(b => $"- [{b.Severity.ToUpper()}] {b.Title}: {b.Detail}"));
            var prompt = $"You are a friendly assistant for a busy landlord. In 2–4 short sentences, summarize today's priorities from these items. Plain English, no jargon. Items:\n{bulletList}";
            using var polishCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            polishCts.CancelAfter(_llmPolishTimeout);
            var llmResult = await _llm
                .ChatAsync(prompt, polishCts.Token)
                .WaitAsync(_llmPolishTimeout, ct);

            return string.IsNullOrWhiteSpace(llmResult)
                ? (null, false)
                : (llmResult.Trim(), true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Optional AI wording timed out or failed. Return the factual rules-only briefing
            // immediately instead of holding the Dashboard open on a remote dependency.
            return (null, false);
        }
    }

    private IQueryable<Property> AuthorizedProperties(
        WorkspaceReadScope scope,
        string capabilityKey,
        DateTime utcNow) =>
        _db.Properties
            .AsNoTracking()
            .Where(property => property.DeletedAt == null)
            .WhereAuthorized(_db, scope, capabilityKey, utcNow);

    private static BriefingBullet ToBullet(BriefingCandidate candidate, DateTime today)
    {
        var severity = SeverityLabel(candidate.SeverityOrder);
        var eventDate = candidate.EventDateTime
            ?? candidate.EventDateOnly?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            ?? throw new InvalidOperationException("Briefing candidate is missing its event date.");
        return candidate.Category switch
        {
            "Maintenance" => new BriefingBullet(
                Title: $"Emergency: {candidate.TitleText}",
                Detail: Truncate(candidate.DetailText ?? string.Empty, 120),
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId,
                UnitId: candidate.UnitId),

            "RentLate" => new BriefingBullet(
                Title: $"Rent overdue — {RentAttentionRef(candidate)}",
                Detail: $"${candidate.Amount:N0} was due {DaysBetween(today, eventDate)} day{(DaysBetween(today, eventDate) == 1 ? "" : "s")} ago",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId,
                UnitId: candidate.UnitId),

            "RentDue" => new BriefingBullet(
                Title: $"Rent due today — {UnitOrLeaseRef(candidate)}",
                Detail: $"${candidate.Amount:N0} due",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId,
                UnitId: candidate.UnitId),

            "Appointment" => new BriefingBullet(
                Title: $"Appointment today: {candidate.TitleText}",
                Detail: $"{(AppointmentType)candidate.TypeValue} at {eventDate:h:mm tt}",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId,
                UnitId: candidate.UnitId),

            "Inspection" => new BriefingBullet(
                Title: $"Inspection {RelativeWhen(eventDate, today)} — {(InspectionType)candidate.TypeValue}",
                Detail: $"Scheduled for {eventDate:MMM d}",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId,
                UnitId: candidate.UnitId),

            "LeaseExpiring" => new BriefingBullet(
                Title: $"Lease expiring — {UnitOrLeaseRef(candidate)}",
                Detail: $"Ends {eventDate:MMM d} ({DaysUntil(eventDate, today)} day{(DaysUntil(eventDate, today) == 1 ? "" : "s")} left)",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId,
                UnitId: candidate.UnitId),

            _ => new BriefingBullet(
                Title: candidate.TitleText ?? candidate.Category,
                Detail: candidate.DetailText ?? string.Empty,
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId,
                UnitId: candidate.UnitId),
        };
    }

    private static string SeverityLabel(int order) => order switch
    {
        0 => "critical",
        1 => "warning",
        _ => "info",
    };

    private static string Truncate(string value, int maxLength)
        => value.Length > maxLength ? value[..maxLength] + "…" : value;

    private static string LeaseRef(BriefingCandidate candidate)
        => !string.IsNullOrWhiteSpace(candidate.LeaseNumber)
            ? candidate.LeaseNumber!
            : $"Lease #{candidate.EntityId}";

    private static string UnitOrLeaseRef(BriefingCandidate candidate)
        => !string.IsNullOrWhiteSpace(candidate.UnitNumber)
            ? $"Unit {candidate.UnitNumber}"
            : LeaseRef(candidate);

    private static string RentAttentionRef(BriefingCandidate candidate)
    {
        var location = LocationRef(candidate);
        if (!string.IsNullOrWhiteSpace(candidate.TenantName) && !string.IsNullOrWhiteSpace(location))
        {
            return $"{candidate.TenantName} - {location}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.TenantName))
        {
            return candidate.TenantName!;
        }

        return !string.IsNullOrWhiteSpace(location)
            ? location
            : LeaseRef(candidate);
    }

    private static string? LocationRef(BriefingCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.PropertyName) && !string.IsNullOrWhiteSpace(candidate.UnitNumber))
        {
            return $"{candidate.PropertyName}, Unit {candidate.UnitNumber}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.UnitNumber))
        {
            return $"Unit {candidate.UnitNumber}";
        }

        return string.IsNullOrWhiteSpace(candidate.PropertyName) ? null : candidate.PropertyName;
    }

    private static int DaysBetween(DateTime later, DateTime earlier)
        => (int)(later.Date - earlier.Date).TotalDays;

    private static int DaysUntil(DateTime later, DateTime today)
        => (int)(later.Date - today.Date).TotalDays;

    private static string RelativeWhen(DateTime date, DateTime today)
    {
        var daysUntil = DaysUntil(date, today);
        return daysUntil == 0 ? "today"
            : daysUntil == 1 ? "tomorrow"
            : $"in {daysUntil} days";
    }

    private sealed class BriefingCandidate
    {
        public int SortOrder { get; set; }
        public int SeverityOrder { get; set; }
        public string Category { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public int EntityId { get; set; }
        public int? UnitId { get; set; }
        public string? TitleText { get; set; }
        public string? DetailText { get; set; }
        public string? LeaseNumber { get; set; }
        public string? UnitNumber { get; set; }
        public string? TenantName { get; set; }
        public string? PropertyName { get; set; }
        public decimal Amount { get; set; }
        public DateTime? EventDateTime { get; set; }
        public DateOnly? EventDateOnly { get; set; }
        public int TypeValue { get; set; }
    }
}
