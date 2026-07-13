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

    private readonly RentalCommandDbContext _db;
    private readonly ILlmProvider _llm;
    private readonly TimeProvider _timeProvider;

    public DailyBriefingService(RentalCommandDbContext db, ILlmProvider llm, TimeProvider timeProvider)
    {
        _db = db;
        _llm = llm;
        _timeProvider = timeProvider;
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

    public Task<BriefingResponse> ComposeForSystemAutomationAsync(
        int portfolioId,
        CancellationToken ct = default)
    {
        var portfolioProperties = _db.Properties
            .AsNoTracking()
            .Where(property => property.PortfolioId == portfolioId && property.DeletedAt == null);
        return ComposeCoreAsync(
            portfolioId,
            portfolioProperties,
            portfolioProperties,
            portfolioProperties,
            portfolioProperties,
            portfolioProperties,
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

        // --- Rule 1: Emergency maintenance ---
        var emergencyWorkOrders = _db.WorkOrders
            .AsNoTracking()
            .Where(w =>
                w.PortfolioId == portfolioId &&
                workProperties.Any(property => property.Id == w.PropertyId) &&
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
                UnitId = w.UnitId,
                TitleText = w.Title,
                DetailText = w.Description,
                LeaseNumber = null,
                UnitNumber = null,
                TenantName = null,
                PropertyName = null,
                Amount = 0m,
                EventDateTime = w.RequestedAt,
                EventDateOnly = null,
                TypeValue = 0,
            });

        // --- Rule 2: Overdue rent ---
        var overduePayments =
            from charge in _db.TenantChargeBalanceProjections.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { charge.PortfolioId, charge.TenantAccountId }
                equals new { account.PortfolioId, TenantAccountId = account.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { account.PortfolioId, account.LeaseManagementId }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join unit in _db.Units.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            join property in moneyProperties
                on new { lifecycle.PortfolioId, Id = lifecycle.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.CurrentAgreementId }
                equals new { agreement.PortfolioId, Id = (int?)agreement.Id }
                into agreementRows
            from agreement in agreementRows.DefaultIfEmpty()
            where charge.PortfolioId == portfolioId
                && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                && charge.IsPastDue
                && charge.OpenAmount > 0m
                && charge.DueOn != null
            select new BriefingCandidate
            {
                SortOrder = 2,
                SeverityOrder = charge.DueOn <= criticalOverdueCutoff ? 0 : 1,
                Category = "RentLate",
                EntityType = "TenantAccount",
                EntityId = account.Id,
                UnitId = lifecycle.UnitId,
                TitleText = null,
                DetailText = null,
                LeaseNumber = agreement != null ? agreement.AgreementNumber : account.AccountNumber,
                UnitNumber = unit.UnitNumber,
                TenantName = lifecycle.CurrentPrimaryTenantName,
                PropertyName = property.Name,
                Amount = charge.OpenAmount,
                EventDateTime = null,
                EventDateOnly = charge.DueOn,
                TypeValue = 0,
            };

        // --- Rule 3: Open rent charges due today ---
        // Collapse charge rows in PostgreSQL so one tenant account produces one briefing item even
        // if a corrected agreement caused more than one open rent entry for the same due date.
        var rentDueAmounts = _db.TenantChargeBalanceProjections
            .AsNoTracking()
            .Where(charge =>
                charge.PortfolioId == portfolioId &&
                charge.EntryType == nameof(TenantLedgerEntryType.RentCharge) &&
                charge.DueOn == businessDate &&
                charge.OpenAmount > 0m)
            .GroupBy(charge => new { charge.PortfolioId, charge.TenantAccountId, charge.DueOn })
            .Select(grouped => new
            {
                grouped.Key.PortfolioId,
                grouped.Key.TenantAccountId,
                grouped.Key.DueOn,
                OpenAmount = grouped.Sum(charge => charge.OpenAmount),
            });

        var rentDueAccounts =
            from due in rentDueAmounts
            join account in _db.TenantAccounts.AsNoTracking()
                on new { due.PortfolioId, Id = due.TenantAccountId }
                equals new { account.PortfolioId, account.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { account.PortfolioId, account.LeaseManagementId }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join unit in _db.Units.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            join property in moneyProperties
                on new { lifecycle.PortfolioId, Id = lifecycle.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.CurrentAgreementId }
                equals new { agreement.PortfolioId, Id = (int?)agreement.Id }
                into agreementRows
            from agreement in agreementRows.DefaultIfEmpty()
            where lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending"
            select new BriefingCandidate
            {
                SortOrder = 3,
                SeverityOrder = 2,
                Category = "RentDue",
                EntityType = nameof(TenantAccount),
                EntityId = account.Id,
                UnitId = lifecycle.UnitId,
                TitleText = null,
                DetailText = null,
                LeaseNumber = agreement != null ? agreement.AgreementNumber : account.AccountNumber,
                UnitNumber = unit.UnitNumber,
                TenantName = lifecycle.CurrentPrimaryTenantName,
                PropertyName = property.Name,
                Amount = due.OpenAmount,
                EventDateTime = null,
                EventDateOnly = due.DueOn,
                TypeValue = 0,
            };

        // --- Rule 4: Appointments today ---
        var todayAppointments = _db.Appointments
            .AsNoTracking()
            .Where(a =>
                a.PortfolioId == portfolioId &&
                a.PropertyId != null &&
                ((a.Type == AppointmentType.Showing &&
                  showingProperties.Any(property => property.Id == a.PropertyId)) ||
                 ((a.Type == AppointmentType.MoveIn || a.Type == AppointmentType.MoveOut) &&
                  onboardingProperties.Any(property => property.Id == a.PropertyId)) ||
                 ((a.Type == AppointmentType.Inspection || a.Type == AppointmentType.MaintenanceVisit) &&
                  workProperties.Any(property => property.Id == a.PropertyId)) ||
                 (a.Type == AppointmentType.OwnerMeeting &&
                  rentalProperties.Any(property => property.Id == a.PropertyId))) &&
                (a.Status == AppointmentStatus.Scheduled || a.Status == AppointmentStatus.Confirmed) &&
                a.ScheduledStart >= today && a.ScheduledStart < tomorrow)
            .Select(a => new BriefingCandidate
            {
                SortOrder = 4,
                SeverityOrder = 2,
                Category = "Appointment",
                EntityType = "Appointment",
                EntityId = a.Id,
                UnitId = a.UnitId,
                TitleText = a.Title,
                DetailText = null,
                LeaseNumber = null,
                UnitNumber = null,
                TenantName = null,
                PropertyName = null,
                Amount = 0m,
                TypeValue = (int)a.Type,
                EventDateTime = a.ScheduledStart,
                EventDateOnly = null,
            });

        // --- Rule 5: Inspections due within 7 days ---
        var upcomingInspections = _db.Inspections
            .AsNoTracking()
            .Where(i =>
                i.PortfolioId == portfolioId &&
                workProperties.Any(property => property.Id == i.PropertyId) &&
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
                UnitId = i.UnitId,
                TitleText = null,
                DetailText = null,
                LeaseNumber = null,
                UnitNumber = null,
                TenantName = null,
                PropertyName = null,
                Amount = 0m,
                TypeValue = (int)i.Type,
                EventDateTime = i.ScheduledFor,
                EventDateOnly = null,
            });

        // --- Rule 6: Governing agreements expiring within 60 days ---
        var expiringAgreements =
            from status in _db.LeaseAgreementStatusProjections.AsNoTracking()
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { status.PortfolioId, Id = status.AgreementId }
                equals new { agreement.PortfolioId, agreement.Id }
            join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                on new { status.PortfolioId, status.LeaseManagementId }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            join unit in _db.Units.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            join property in rentalProperties
                on new { lifecycle.PortfolioId, Id = lifecycle.PropertyId }
                equals new { property.PortfolioId, property.Id }
            where status.PortfolioId == portfolioId
                && status.IsGoverning
                && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                && agreement.TermEndOn != null
                && agreement.TermEndOn > businessDate
                && agreement.TermEndOn <= sixtyDaysOut
            select new BriefingCandidate
            {
                SortOrder = 6,
                SeverityOrder = 1,
                Category = "LeaseExpiring",
                EntityType = nameof(LeaseAgreement),
                EntityId = agreement.Id,
                UnitId = lifecycle.UnitId,
                TitleText = null,
                DetailText = null,
                LeaseNumber = agreement.AgreementNumber,
                UnitNumber = unit.UnitNumber,
                TenantName = lifecycle.CurrentPrimaryTenantName,
                PropertyName = property.Name,
                Amount = 0m,
                EventDateTime = null,
                EventDateOnly = agreement.TermEndOn,
                TypeValue = 0,
            };

        var candidates = await emergencyWorkOrders
            .Concat(overduePayments)
            .Concat(rentDueAccounts)
            .Concat(todayAppointments)
            .Concat(upcomingInspections)
            .Concat(expiringAgreements)
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
            GeneratedAt = _timeProvider.UtcNow(),
            Summary = summary,
            LlmEnhanced = llmEnhanced,
            Bullets = sortedBullets,
        };
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
