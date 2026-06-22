using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Composes the Daily Briefing: a prioritized, plain-English summary of what needs attention
/// today across the portfolio, optionally polished by an LLM.
/// </summary>
public class DailyBriefingService : IDailyBriefingService
{
    private const int MaxBriefingBullets = 25;

    private readonly RentalCommandDbContext _db;
    private readonly ILlmProvider _llm;

    public DailyBriefingService(RentalCommandDbContext db, ILlmProvider llm)
    {
        _db = db;
        _llm = llm;
    }

    public async Task<BriefingResponse> ComposeAsync(int portfolioId, CancellationToken ct = default)
    {
        // TODO: portfolio-timezone handling is a future refinement; using UTC for now.
        var today = DateTime.UtcNow.Date;
        var tomorrow = today.AddDays(1);
        var nextWeekEnd = today.AddDays(8);
        var sixtyDaysOut = today.AddDays(60);
        var criticalOverdueCutoff = today.AddDays(-5);

        // --- Rule 1: Emergency maintenance ---
        var emergencyWorkOrders = _db.WorkOrders
            .AsNoTracking()
            .Where(w =>
                w.PortfolioId == portfolioId &&
                w.Priority == WorkOrderPriority.Emergency &&
                w.Status != WorkOrderStatus.Completed &&
                w.Status != WorkOrderStatus.Cancelled &&
                w.Status != WorkOrderStatus.Archived)
            .Select(w => new BriefingCandidate
            {
                SortOrder = 1,
                SeverityOrder = 0,
                Category = "Maintenance",
                EntityType = "WorkOrder",
                EntityId = w.Id,
                TitleText = w.Title,
                DetailText = w.Description,
                LeaseNumber = null,
                UnitNumber = null,
                Amount = 0m,
                EventDate = w.RequestedAt,
                TypeValue = 0,
            });

        // --- Rule 2: Overdue rent ---
        var overduePayments = _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                (p.Status == PaymentStatus.Scheduled ||
                 p.Status == PaymentStatus.Partial ||
                 p.Status == PaymentStatus.Late) &&
                p.DueDate < today)
            .Select(p => new BriefingCandidate
            {
                SortOrder = 2,
                SeverityOrder = p.DueDate <= criticalOverdueCutoff ? 0 : 1,
                Category = "RentLate",
                EntityType = "Payment",
                EntityId = p.Id,
                TitleText = null,
                DetailText = null,
                LeaseNumber = p.Lease != null ? p.Lease.LeaseNumber : null,
                UnitNumber = null,
                Amount = p.Amount,
                EventDate = p.DueDate,
                TypeValue = 0,
            });

        // --- Rule 3: Rent due today ---
        var rentDueLeases = _db.Leases
            .AsNoTracking()
            .Where(l =>
                l.PortfolioId == portfolioId &&
                l.Status == LeaseStatus.Active &&
                l.RentDueDay == today.Day)
            .Select(l => new BriefingCandidate
            {
                SortOrder = 3,
                SeverityOrder = 2,
                Category = "RentDue",
                EntityType = "Lease",
                EntityId = l.Id,
                TitleText = null,
                DetailText = null,
                LeaseNumber = l.LeaseNumber,
                UnitNumber = l.Unit != null ? l.Unit.UnitNumber : null,
                Amount = l.MonthlyRent,
                EventDate = today,
                TypeValue = 0,
            });

        // --- Rule 4: Appointments today ---
        var todayAppointments = _db.Appointments
            .AsNoTracking()
            .Where(a =>
                a.PortfolioId == portfolioId &&
                (a.Status == AppointmentStatus.Scheduled || a.Status == AppointmentStatus.Confirmed) &&
                a.ScheduledStart >= today && a.ScheduledStart < tomorrow)
            .Select(a => new BriefingCandidate
            {
                SortOrder = 4,
                SeverityOrder = 2,
                Category = "Appointment",
                EntityType = "Appointment",
                EntityId = a.Id,
                TitleText = a.Title,
                DetailText = null,
                LeaseNumber = null,
                UnitNumber = null,
                Amount = 0m,
                TypeValue = (int)a.Type,
                EventDate = a.ScheduledStart,
            });

        // --- Rule 5: Inspections due within 7 days ---
        var upcomingInspections = _db.Inspections
            .AsNoTracking()
            .Where(i =>
                i.PortfolioId == portfolioId &&
                i.Status == InspectionStatus.Scheduled &&
                i.ScheduledFor >= today &&
                i.ScheduledFor < nextWeekEnd)
            .Select(i => new BriefingCandidate
            {
                SortOrder = 5,
                SeverityOrder = i.ScheduledFor < today.AddDays(2) ? 1 : 2,
                Category = "Inspection",
                EntityType = "Inspection",
                EntityId = i.Id,
                TitleText = null,
                DetailText = null,
                LeaseNumber = null,
                UnitNumber = null,
                Amount = 0m,
                TypeValue = (int)i.Type,
                EventDate = i.ScheduledFor,
            });

        // --- Rule 6: Leases expiring within 60 days ---
        var expiringLeases = _db.Leases
            .AsNoTracking()
            .Where(l =>
                l.PortfolioId == portfolioId &&
                l.Status == LeaseStatus.Active &&
                l.EndDate > today &&
                l.EndDate <= sixtyDaysOut)
            .Select(l => new BriefingCandidate
            {
                SortOrder = 6,
                SeverityOrder = 1,
                Category = "LeaseExpiring",
                EntityType = "Lease",
                EntityId = l.Id,
                TitleText = null,
                DetailText = null,
                LeaseNumber = l.LeaseNumber,
                UnitNumber = l.Unit != null ? l.Unit.UnitNumber : null,
                Amount = 0m,
                EventDate = l.EndDate,
                TypeValue = 0,
            });

        var candidates = await emergencyWorkOrders
            .Concat(overduePayments)
            .Concat(rentDueLeases)
            .Concat(todayAppointments)
            .Concat(upcomingInspections)
            .Concat(expiringLeases)
            .OrderBy(c => c.SeverityOrder)
            .ThenBy(c => c.SortOrder)
            .ThenBy(c => c.EventDate)
            .ThenBy(c => c.EntityId)
            .Take(MaxBriefingBullets)
            .ToListAsync(ct);

        var sortedBullets = candidates
            .Select(c => ToBullet(c, today))
            .ToList();

        // LLM polish — skip when there is nothing to summarize.
        string? summary = null;
        var llmEnhanced = false;

        if (sortedBullets.Count > 0)
        {
            try
            {
                var bulletList = string.Join("\n", sortedBullets.Select(b => $"- [{b.Severity.ToUpper()}] {b.Title}: {b.Detail}"));
                var prompt = $"You are a friendly assistant for a busy landlord. In 2–4 short sentences, summarize today's priorities from these items. Plain English, no jargon. Items:\n{bulletList}";
                var llmResult = await _llm.ChatAsync(prompt, ct);

                if (!string.IsNullOrWhiteSpace(llmResult))
                {
                    summary = llmResult.Trim();
                    llmEnhanced = true;
                }
            }
            catch
            {
                // LLM unavailable — fall back to rules-only output, no error thrown.
            }
        }

        return new BriefingResponse
        {
            Date = today,
            GeneratedAt = DateTime.UtcNow,
            Summary = summary,
            LlmEnhanced = llmEnhanced,
            Bullets = sortedBullets,
        };
    }

    private static BriefingBullet ToBullet(BriefingCandidate candidate, DateTime today)
    {
        var severity = SeverityLabel(candidate.SeverityOrder);
        return candidate.Category switch
        {
            "Maintenance" => new BriefingBullet(
                Title: $"Emergency: {candidate.TitleText}",
                Detail: Truncate(candidate.DetailText ?? string.Empty, 120),
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId),

            "RentLate" => new BriefingBullet(
                Title: $"Rent overdue — {LeaseRef(candidate)}",
                Detail: $"${candidate.Amount:N0} was due {DaysBetween(today, candidate.EventDate)} day{(DaysBetween(today, candidate.EventDate) == 1 ? "" : "s")} ago",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId),

            "RentDue" => new BriefingBullet(
                Title: $"Rent due today — {UnitOrLeaseRef(candidate)}",
                Detail: $"${candidate.Amount:N0} due",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId),

            "Appointment" => new BriefingBullet(
                Title: $"Appointment today: {candidate.TitleText}",
                Detail: $"{(AppointmentType)candidate.TypeValue} at {candidate.EventDate:h:mm tt}",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId),

            "Inspection" => new BriefingBullet(
                Title: $"Inspection {RelativeWhen(candidate.EventDate, today)} — {(InspectionType)candidate.TypeValue}",
                Detail: $"Scheduled for {candidate.EventDate:MMM d}",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId),

            "LeaseExpiring" => new BriefingBullet(
                Title: $"Lease expiring — {UnitOrLeaseRef(candidate)}",
                Detail: $"Ends {candidate.EventDate:MMM d} ({DaysUntil(candidate.EventDate, today)} day{(DaysUntil(candidate.EventDate, today) == 1 ? "" : "s")} left)",
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId),

            _ => new BriefingBullet(
                Title: candidate.TitleText ?? candidate.Category,
                Detail: candidate.DetailText ?? string.Empty,
                Category: candidate.Category,
                Severity: severity,
                EntityType: candidate.EntityType,
                EntityId: candidate.EntityId),
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
        public string? TitleText { get; set; }
        public string? DetailText { get; set; }
        public string? LeaseNumber { get; set; }
        public string? UnitNumber { get; set; }
        public decimal Amount { get; set; }
        public DateTime EventDate { get; set; }
        public int TypeValue { get; set; }
    }
}
