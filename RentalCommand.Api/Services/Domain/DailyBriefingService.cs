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

        var rawBullets = new List<(int SortOrder, BriefingBullet Bullet)>();

        // --- Rule 1: Emergency maintenance ---
        var emergencyWorkOrders = await _db.WorkOrders
            .AsNoTracking()
            .Where(w =>
                w.PortfolioId == portfolioId &&
                w.Priority == WorkOrderPriority.Emergency &&
                w.Status != WorkOrderStatus.Completed &&
                w.Status != WorkOrderStatus.Cancelled &&
                w.Status != WorkOrderStatus.Archived)
            .ToListAsync(ct);

        foreach (var wo in emergencyWorkOrders)
        {
            rawBullets.Add((1, new BriefingBullet(
                Title: $"Emergency: {wo.Title}",
                Detail: wo.Description.Length > 120 ? wo.Description[..120] + "…" : wo.Description,
                Category: "Maintenance",
                Severity: "critical",
                EntityType: "WorkOrder",
                EntityId: wo.Id)));
        }

        // --- Rule 2: Overdue rent ---
        var overduePayments = await _db.Payments
            .AsNoTracking()
            .Include(p => p.Lease)
            .Where(p =>
                p.PortfolioId == portfolioId &&
                (p.Status == PaymentStatus.Scheduled ||
                 p.Status == PaymentStatus.Partial ||
                 p.Status == PaymentStatus.Late) &&
                p.DueDate < today)
            .ToListAsync(ct);

        foreach (var pmt in overduePayments)
        {
            var daysOverdue = (int)(today - pmt.DueDate.Date).TotalDays;
            var severity = daysOverdue >= 5 ? "critical" : "warning";
            var leaseRef = pmt.Lease?.LeaseNumber ?? $"Lease #{pmt.LeaseId}";
            rawBullets.Add((2, new BriefingBullet(
                Title: $"Rent overdue — {leaseRef}",
                Detail: $"${pmt.Amount:N0} was due {daysOverdue} day{(daysOverdue == 1 ? "" : "s")} ago",
                Category: "RentLate",
                Severity: severity,
                EntityType: "Payment",
                EntityId: pmt.Id)));
        }

        // --- Rule 3: Rent due today ---
        var rentDueLeases = await _db.Leases
            .AsNoTracking()
            .Include(l => l.Unit)
            .Where(l =>
                l.PortfolioId == portfolioId &&
                l.Status == LeaseStatus.Active &&
                l.RentDueDay == today.Day)
            .ToListAsync(ct);

        foreach (var lease in rentDueLeases)
        {
            var unitRef = lease.Unit != null ? $"Unit {lease.Unit.UnitNumber}" : lease.LeaseNumber;
            rawBullets.Add((3, new BriefingBullet(
                Title: $"Rent due today — {unitRef}",
                Detail: $"${lease.MonthlyRent:N0} due",
                Category: "RentDue",
                Severity: "info",
                EntityType: "Lease",
                EntityId: lease.Id)));
        }

        // --- Rule 4: Appointments today ---
        var todayAppointments = await _db.Appointments
            .AsNoTracking()
            .Where(a =>
                a.PortfolioId == portfolioId &&
                (a.Status == AppointmentStatus.Scheduled || a.Status == AppointmentStatus.Confirmed) &&
                a.ScheduledStart >= today && a.ScheduledStart < today.AddDays(1))
            .ToListAsync(ct);

        foreach (var appt in todayAppointments)
        {
            rawBullets.Add((4, new BriefingBullet(
                Title: $"Appointment today: {appt.Title}",
                Detail: $"{appt.Type} at {appt.ScheduledStart:h:mm tt}",
                Category: "Appointment",
                Severity: "info",
                EntityType: "Appointment",
                EntityId: appt.Id)));
        }

        // --- Rule 5: Inspections due within 7 days ---
        var upcomingInspections = await _db.Inspections
            .AsNoTracking()
            .Where(i =>
                i.PortfolioId == portfolioId &&
                i.Status == InspectionStatus.Scheduled &&
                i.ScheduledFor.Date <= today.AddDays(7))
            .ToListAsync(ct);

        foreach (var insp in upcomingInspections)
        {
            var daysUntil = (int)(insp.ScheduledFor.Date - today).TotalDays;
            var severity = daysUntil <= 1 ? "warning" : "info";
            var when = daysUntil == 0 ? "today"
                     : daysUntil == 1 ? "tomorrow"
                     : $"in {daysUntil} days";
            rawBullets.Add((5, new BriefingBullet(
                Title: $"Inspection {when} — {insp.Type}",
                Detail: $"Scheduled for {insp.ScheduledFor:MMM d}",
                Category: "Inspection",
                Severity: severity,
                EntityType: "Inspection",
                EntityId: insp.Id)));
        }

        // --- Rule 6: Leases expiring within 60 days ---
        var expiringLeases = await _db.Leases
            .AsNoTracking()
            .Include(l => l.Unit)
            .Where(l =>
                l.PortfolioId == portfolioId &&
                l.Status == LeaseStatus.Active &&
                l.EndDate > today &&
                l.EndDate <= today.AddDays(60))
            .ToListAsync(ct);

        foreach (var lease in expiringLeases)
        {
            var daysLeft = (int)(lease.EndDate.Date - today).TotalDays;
            var unitRef = lease.Unit != null ? $"Unit {lease.Unit.UnitNumber}" : lease.LeaseNumber;
            rawBullets.Add((6, new BriefingBullet(
                Title: $"Lease expiring — {unitRef}",
                Detail: $"Ends {lease.EndDate:MMM d} ({daysLeft} day{(daysLeft == 1 ? "" : "s")} left)",
                Category: "LeaseExpiring",
                Severity: "warning",
                EntityType: "Lease",
                EntityId: lease.Id)));
        }

        // Sort: critical → warning → info, then by internal rule priority.
        static int SeverityOrder(string s) => s switch { "critical" => 0, "warning" => 1, _ => 2 };

        var sortedBullets = rawBullets
            .OrderBy(x => SeverityOrder(x.Bullet.Severity))
            .ThenBy(x => x.SortOrder)
            .Select(x => x.Bullet)
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
}
