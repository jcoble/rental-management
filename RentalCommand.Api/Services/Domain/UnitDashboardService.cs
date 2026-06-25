using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IUnitDashboardService"/>
public class UnitDashboardService : IUnitDashboardService
{
    private const string RenewalNoticeType = "RenewalOffer";
    private const string NoticeStatusApproved = "Approved";
    private const string NoticeStatusDraft = "Draft";

    /// <summary>Cap for each Overview list (recent payments, open WOs, pending docs, upcoming appts).</summary>
    private const int OverviewTake = 5;

    /// <summary>Cap for the persistent timeline rail slice returned on the dashboard.</summary>
    private const int RecentTimelineTake = 15;

    private readonly RentalCommandDbContext _db;
    private readonly AuditDescriber _auditDescriber;
    private readonly AuditDiffBuilder _auditDiff;

    public UnitDashboardService(RentalCommandDbContext db, AuditDescriber auditDescriber, AuditDiffBuilder auditDiff)
    {
        _db = db;
        _auditDescriber = auditDescriber;
        _auditDiff = auditDiff;
    }

    public async Task<UnitDashboardResponse?> GetDashboardAsync(int portfolioId, int unitId, CancellationToken ct = default)
    {
        // (1) Unit + property name. Scope is enforced through the owning property (units carry no PortfolioId).
        var unitRow = await _db.Units
            .AsNoTracking()
            .Where(u => u.Id == unitId && u.Property!.PortfolioId == portfolioId)
            .Select(u => new { Unit = u, PropertyName = u.Property!.Name })
            .FirstOrDefaultAsync(ct);

        if (unitRow is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;

        // The unit's lease ids — one indexed select. Small per unit; reused by the rent aggregate,
        // the recent-payments list, and the timeline union (kept DB-side as IN (...) subqueries where possible).
        var leaseIds = await _db.Leases
            .AsNoTracking()
            .Where(l => l.UnitId == unitId && l.PortfolioId == portfolioId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        // (2) Current lease (+ tenant): most-recent Active, else the latest lease of any status.
        var currentLease = await _db.Leases
            .AsNoTracking()
            .Where(l => l.UnitId == unitId && l.PortfolioId == portfolioId)
            .OrderByDescending(l => l.Status == LeaseStatus.Active)
            .ThenByDescending(l => l.StartDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.LeaseNumber,
                l.Status,
                l.StartDate,
                l.EndDate,
                l.MonthlyRent,
                l.SecurityDeposit,
                l.TenantId,
                TenantFirst = l.Tenant != null ? l.Tenant.FirstName : null,
                TenantLast = l.Tenant != null ? l.Tenant.LastName : null,
                TenantEmail = l.Tenant != null ? l.Tenant.Email : null,
                TenantPhone = l.Tenant != null ? l.Tenant.Phone : null,
            })
            .FirstOrDefaultAsync(ct);

        // (3) Rent state — a single grouped conditional SUM over the unit's payments (no materialization).
        // Outstanding = still-owed rows (Scheduled/Partial/Late). Overdue flags whether any owed row is past due.
        decimal outstanding = 0m;
        bool hasOverdue = false;
        bool hasDueSoon = false;
        if (leaseIds.Count > 0)
        {
            var rent = await _db.Payments
                .AsNoTracking()
                .Where(p => p.PortfolioId == portfolioId && leaseIds.Contains(p.LeaseId))
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Outstanding = g.Sum(p =>
                        (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial || p.Status == PaymentStatus.Late)
                            ? p.Amount : 0m),
                    OverdueCount = g.Count(p =>
                        (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial || p.Status == PaymentStatus.Late)
                        && (p.Status == PaymentStatus.Late || p.DueDate < now)),
                    DueSoonCount = g.Count(p =>
                        (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial)
                        && p.DueDate >= now),
                })
                .FirstOrDefaultAsync(ct);

            outstanding = rent?.Outstanding ?? 0m;
            hasOverdue = (rent?.OverdueCount ?? 0) > 0;
            hasDueSoon = (rent?.DueSoonCount ?? 0) > 0;
        }

        // (4) Counts — open work orders + documents on the unit and its children.
        var openWorkOrderCount = await _db.WorkOrders
            .AsNoTracking()
            .CountAsync(w => w.UnitId == unitId && w.PortfolioId == portfolioId
                && w.Status != WorkOrderStatus.Completed
                && w.Status != WorkOrderStatus.Cancelled
                && w.Status != WorkOrderStatus.Archived, ct);

        // The unit's document set: files attached to the unit OR any of its children, computed DB-side as
        // a set of (EntityType, EntityId) predicates against subqueries (one query, IN (...) per child type).
        var docs = await BuildUnitDocumentsQuery(portfolioId, unitId, leaseIds)
            .OrderByDescending(f => f.UploadedAt)
            .Select(f => new UnitDocumentSummary
            {
                Id = f.Id,
                FileName = f.FileName,
                ContentType = f.ContentType,
                EntityType = f.EntityType,
                EntityId = f.EntityId,
                UploadedAt = f.UploadedAt,
            })
            .Take(OverviewTake)
            .ToListAsync(ct);

        var docsCount = await BuildUnitDocumentsQuery(portfolioId, unitId, leaseIds).CountAsync(ct);

        // (5) Upcoming appointments for the unit.
        var upcomingAppointments = await _db.Appointments
            .AsNoTracking()
            .Where(a => a.UnitId == unitId && a.PortfolioId == portfolioId
                && a.ScheduledStart >= now
                && a.Status != AppointmentStatus.Cancelled)
            .OrderBy(a => a.ScheduledStart)
            .Take(OverviewTake)
            .Select(a => new UnitAppointmentSummary
            {
                Id = a.Id,
                Title = a.Title,
                Type = a.Type.ToString(),
                Status = a.Status.ToString(),
                ScheduledStart = a.ScheduledStart,
                AssignedTo = a.AssignedTo,
            })
            .ToListAsync(ct);

        // Overview: recent payments (newest by due date) + open work orders (newest requested first).
        var recentPayments = leaseIds.Count == 0
            ? new List<UnitPaymentSummary>()
            : await _db.Payments
                .AsNoTracking()
                .Where(p => p.PortfolioId == portfolioId && leaseIds.Contains(p.LeaseId))
                .OrderByDescending(p => p.DueDate)
                .ThenByDescending(p => p.Id)
                .Take(OverviewTake)
                .Select(p => new UnitPaymentSummary
                {
                    Id = p.Id,
                    LeaseId = p.LeaseId,
                    Type = p.PaymentType.ToString(),
                    Status = p.Status.ToString(),
                    Amount = p.Amount,
                    DueDate = p.DueDate,
                    PaidDate = p.PaidDate,
                })
                .ToListAsync(ct);

        var openWorkOrders = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.UnitId == unitId && w.PortfolioId == portfolioId
                && w.Status != WorkOrderStatus.Completed
                && w.Status != WorkOrderStatus.Cancelled
                && w.Status != WorkOrderStatus.Archived)
            .OrderByDescending(w => w.RequestedAt)
            .ThenByDescending(w => w.Id)
            .Take(OverviewTake)
            .Select(w => new UnitWorkOrderSummary
            {
                Id = w.Id,
                Title = w.Title,
                Status = w.Status.ToString(),
                Priority = w.Priority.ToString(),
                RequestedAt = w.RequestedAt,
            })
            .ToListAsync(ct);

        // Stage inputs — a few cheap EXISTS checks plus the values already fetched above. The resolver is
        // a pure function over these; it issues no queries and does not loop rows.
        var hasDraftOrPendingLease = currentLease is not { Status: LeaseStatus.Active }
            && await _db.Leases.AsNoTracking().AnyAsync(l => l.UnitId == unitId && l.PortfolioId == portfolioId
                && (l.Status == LeaseStatus.Draft || l.Status == LeaseStatus.PendingSignature), ct);

        var hasOpenApplication = await _db.RentalApplications.AsNoTracking().AnyAsync(a =>
            a.UnitId == unitId && a.PortfolioId == portfolioId
            && (a.Status == ApplicationStatus.Submitted || a.Status == ApplicationStatus.UnderReview || a.Status == ApplicationStatus.Approved)
            && a.ApprovedTenantId == null, ct);

        var hasUpcomingShowing = await _db.Appointments.AsNoTracking().AnyAsync(a =>
            a.UnitId == unitId && a.PortfolioId == portfolioId
            && a.Type == AppointmentType.Showing
            && a.ScheduledStart >= now
            && a.Status != AppointmentStatus.Cancelled, ct);

        var hasUpcomingMoveInAppt = await _db.Appointments.AsNoTracking().AnyAsync(a =>
            a.UnitId == unitId && a.PortfolioId == portfolioId
            && a.Type == AppointmentType.MoveIn
            && a.ScheduledStart >= now
            && a.Status != AppointmentStatus.Cancelled, ct);

        // Make-ready signal: Unit offline, OR open work orders, OR last lease ended recently, OR a move-out
        // inspection completed. Each is an indexed existence check.
        var recentlyEndedLease = await _db.Leases.AsNoTracking().AnyAsync(l =>
            l.UnitId == unitId && l.PortfolioId == portfolioId
            && (l.Status == LeaseStatus.Expired || l.Status == LeaseStatus.Terminated)
            && (l.MoveOutDate != null ? l.MoveOutDate >= now.AddDays(-60) : l.EndDate >= now.AddDays(-60)), ct);

        var moveOutInspectionDone = await _db.Inspections.AsNoTracking().AnyAsync(i =>
            i.UnitId == unitId && i.PortfolioId == portfolioId
            && i.Type == InspectionType.MoveOut
            && (i.Status == InspectionStatus.Completed || i.Status == InspectionStatus.Reviewed), ct);

        var recentMoveOutSignal = unitRow.Unit.Status == UnitStatus.Offline
            || openWorkOrderCount > 0
            || recentlyEndedLease
            || moveOutInspectionDone;

        var leaseSnapshot = currentLease is null
            ? null
            : new LeaseSnapshot(currentLease.Status, currentLease.StartDate, currentLease.EndDate);

        var stageInputs = new UnitStageInputs(
            unitRow.Unit.Status,
            leaseSnapshot,
            hasDraftOrPendingLease,
            hasOpenApplication,
            hasUpcomingShowing,
            hasUpcomingMoveInAppt,
            recentMoveOutSignal,
            outstanding);

        var (stage, nextActionLabel) = UnitLifecycleStageResolver.Resolve(stageInputs, now);
        var nextBestAction = await ResolveNextBestActionAsync(
            portfolioId,
            unitId,
            unitRow.Unit.PropertyId,
            stage,
            nextActionLabel,
            currentLease?.Id,
            currentLease?.TenantId,
            ct);

        // Header rent state + lease-ends-in.
        var isActiveLease = currentLease is { Status: LeaseStatus.Active };
        var rentState = currentLease is null
            ? "NoLease"
            : hasOverdue ? "Overdue"
            : hasDueSoon ? "Due"
            : "Current";

        int? leaseEndsInDays = isActiveLease
            ? Math.Max(0, (int)Math.Ceiling((currentLease!.EndDate - now).TotalDays))
            : null;

        var tenantName = currentLease?.TenantFirst is null && currentLease?.TenantLast is null
            ? null
            : $"{currentLease!.TenantFirst} {currentLease.TenantLast}".Trim();

        return new UnitDashboardResponse
        {
            Unit = UnitResponse.FromEntity(unitRow.Unit),
            PropertyName = unitRow.PropertyName,
            LifecycleStage = stage.ToString(),
            NextBestAction = nextBestAction,
            Header = new UnitDashboardHeader
            {
                RentState = rentState,
                OutstandingRentBalance = outstanding,
                OpenWorkOrderCount = openWorkOrderCount,
                LeaseEndsInDays = leaseEndsInDays,
                DocsNeedingReviewCount = docsCount,
                CurrentTenantName = tenantName,
            },
            CurrentLease = currentLease is null ? null : new UnitLeaseSummary
            {
                Id = currentLease.Id,
                LeaseNumber = string.IsNullOrWhiteSpace(currentLease.LeaseNumber) ? $"Lease #{currentLease.Id}" : currentLease.LeaseNumber,
                Status = currentLease.Status.ToString(),
                StartDate = currentLease.StartDate,
                EndDate = currentLease.EndDate,
                MonthlyRent = currentLease.MonthlyRent,
                SecurityDeposit = currentLease.SecurityDeposit,
            },
            CurrentTenant = currentLease?.TenantId is null ? null : new UnitTenantSummary
            {
                Id = currentLease.TenantId,
                Name = tenantName ?? string.Empty,
                Email = currentLease.TenantEmail,
                Phone = currentLease.TenantPhone,
            },
            Overview = new UnitDashboardOverview
            {
                RecentPayments = recentPayments,
                OpenWorkOrders = openWorkOrders,
                PendingDocs = docs,
                UpcomingAppointments = upcomingAppointments,
            },
            RecentTimeline = await GetTimelineAsync(portfolioId, unitId, 0, RecentTimelineTake, ct),
        };
    }

    public async Task<IReadOnlyList<AuditEntryResponse>> GetTimelineAsync(
        int portfolioId, int unitId, int skip, int take, CancellationToken ct = default)
    {
        // Confirm the unit is in the caller's portfolio before unioning its history (IDOR guard).
        var inScope = await _db.Units
            .AsNoTracking()
            .AnyAsync(u => u.Id == unitId && u.Property!.PortfolioId == portfolioId, ct);
        if (!inScope)
        {
            return Array.Empty<AuditEntryResponse>();
        }

        skip = skip < 0 ? 0 : skip;
        take = take <= 0 ? RecentTimelineTake : Math.Min(take, 200);

        // Keep child scopes as IQueryable subqueries so the paged AuditLogs query does the filtering in
        // SQL. Do not materialize the child ids first; a heavily used unit can have unbounded payments,
        // work orders, inspections, appointments, and expenses.
        var leaseIds = _db.Leases.AsNoTracking()
            .Where(l => l.UnitId == unitId && l.PortfolioId == portfolioId)
            .Select(l => l.Id);

        var paymentIds = _db.Payments.AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && leaseIds.Contains(p.LeaseId))
            .Select(p => p.Id);

        var workOrderIds = _db.WorkOrders.AsNoTracking()
            .Where(w => w.UnitId == unitId && w.PortfolioId == portfolioId)
            .Select(w => w.Id);

        var inspectionIds = _db.Inspections.AsNoTracking()
            .Where(i => i.UnitId == unitId && i.PortfolioId == portfolioId)
            .Select(i => i.Id);

        var appointmentIds = _db.Appointments.AsNoTracking()
            .Where(a => a.UnitId == unitId && a.PortfolioId == portfolioId)
            .Select(a => a.Id);

        var expenseIds = _db.Expenses.AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId
                && (e.UnitId == unitId || (e.WorkOrderId != null && workOrderIds.Contains(e.WorkOrderId.Value))))
            .Select(e => e.Id);

        // ONE audit query with translated OR/IN subqueries, newest first, paged.
        var rows = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId && (
                (a.EntityType == "Unit" && a.EntityId == unitId) ||
                (a.EntityType == "Lease" && leaseIds.Contains(a.EntityId)) ||
                (a.EntityType == "Payment" && paymentIds.Contains(a.EntityId)) ||
                (a.EntityType == "WorkOrder" && workOrderIds.Contains(a.EntityId)) ||
                (a.EntityType == "Inspection" && inspectionIds.Contains(a.EntityId)) ||
                (a.EntityType == "Appointment" && appointmentIds.Contains(a.EntityId)) ||
                (a.EntityType == "Expense" && expenseIds.Contains(a.EntityId))))
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        // Resolve user display names for any user-attributed rows in one batched query (no N+1).
        var userNames = await ResolveActorNamesAsync(portfolioId, rows, ct);

        return rows
            .Select(a => AuditEntryResponse.FromEntity(a, _auditDescriber, _auditDiff, userNames))
            .ToList();
    }

    /// <summary>
    /// The set of <see cref="StoredFile"/> rows attached to the unit and its children, as a single
    /// <see cref="IQueryable"/> (EntityType/EntityId predicates against indexed child-id subqueries).
    /// Reused for both the capped Overview list and the header count so the shape stays identical.
    /// </summary>
    private IQueryable<StoredFile> BuildUnitDocumentsQuery(int portfolioId, int unitId, IReadOnlyList<int> leaseIds)
    {
        // Child-id subqueries stay in the database (translated to IN (SELECT ...)); the lease-id list is the
        // only materialized set (small per unit) and is reused across the page.
        var workOrderIds = _db.WorkOrders.Where(w => w.UnitId == unitId && w.PortfolioId == portfolioId).Select(w => w.Id);
        var inspectionIds = _db.Inspections.Where(i => i.UnitId == unitId && i.PortfolioId == portfolioId).Select(i => i.Id);
        var paymentIds = _db.Payments.Where(p => p.PortfolioId == portfolioId && leaseIds.Contains(p.LeaseId)).Select(p => p.Id);
        var expenseIds = _db.Expenses
            .Where(e => e.PortfolioId == portfolioId
                && (e.UnitId == unitId || (e.WorkOrderId != null && workOrderIds.Contains(e.WorkOrderId.Value))))
            .Select(e => e.Id);

        return _db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == portfolioId && f.EntityId != null && (
                (f.EntityType == "Unit" && f.EntityId == unitId) ||
                (f.EntityType == "Lease" && leaseIds.Contains(f.EntityId!.Value)) ||
                (f.EntityType == "Payment" && paymentIds.Contains(f.EntityId!.Value)) ||
                (f.EntityType == "Expense" && expenseIds.Contains(f.EntityId!.Value)) ||
                (f.EntityType == "WorkOrder" && workOrderIds.Contains(f.EntityId!.Value)) ||
                (f.EntityType == "Inspection" && inspectionIds.Contains(f.EntityId!.Value))));
    }

    private async Task<UnitNextBestAction> ResolveNextBestActionAsync(
        int portfolioId,
        int unitId,
        int propertyId,
        UnitLifecycleStage stage,
        string defaultLabel,
        int? currentLeaseId,
        int? currentTenantId,
        CancellationToken ct)
    {
        var defaultHref = NextBestActionHref(stage, unitId, propertyId, currentTenantId);
        if (stage != UnitLifecycleStage.Renewal
            || currentLeaseId is not int leaseId
            || currentTenantId is not int tenantId)
        {
            return new UnitNextBestAction { Label = defaultLabel, Href = defaultHref };
        }

        var renewalNotice = await _db.NoticeDrafts
            .AsNoTracking()
            .Where(d => d.PortfolioId == portfolioId
                && d.LeaseId == leaseId
                && d.TenantId == tenantId
                && d.NoticeType == RenewalNoticeType
                && (d.Status == NoticeStatusApproved || d.Status == NoticeStatusDraft))
            .OrderByDescending(d => d.Status == NoticeStatusApproved)
            .ThenByDescending(d => d.ApprovedAt ?? d.UpdatedAt)
            .ThenByDescending(d => d.Id)
            .Select(d => new
            {
                d.Status,
                d.ConversationId,
            })
            .FirstOrDefaultAsync(ct);

        return renewalNotice?.Status switch
        {
            NoticeStatusApproved when renewalNotice.ConversationId is int conversationId => new UnitNextBestAction
            {
                Label = "Renewal sent - open conversation",
                Href = $"/messages?conversation={conversationId}",
            },
            NoticeStatusApproved => new UnitNextBestAction
            {
                Label = "Renewal sent",
                Href = $"/units/{unitId}?tab=lease",
            },
            NoticeStatusDraft => new UnitNextBestAction
            {
                Label = "Review renewal draft",
                Href = defaultHref,
            },
            _ => new UnitNextBestAction { Label = defaultLabel, Href = defaultHref },
        };
    }

    /// <summary>Batched lookup of display names for the user-attributed audit rows (mirrors AuditQueryService).</summary>
    private async Task<IReadOnlyDictionary<int, string>> ResolveActorNamesAsync(
        int portfolioId, IReadOnlyList<AuditLog> rows, CancellationToken ct)
    {
        var ids = rows
            .Where(r => string.IsNullOrWhiteSpace(r.ActorLabel) && r.UserId.HasValue)
            .Select(r => r.UserId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var resolved = await _db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.PortfolioId == portfolioId)
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .ToListAsync(ct);

        return resolved.ToDictionary(
            u => u.Id,
            u => !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName! : (u.Email ?? string.Empty));
    }

    /// <summary>Deep link for a stage's next-best-action: the relevant unit tab (drawer flows attach there).</summary>
    private static string NextBestActionHref(UnitLifecycleStage stage, int unitId, int propertyId, int? tenantId) => stage switch
    {
        UnitLifecycleStage.Ready => $"/applications?action=list-unit&propertyId={propertyId}&unitId={unitId}",
        UnitLifecycleStage.Listed => $"/units/{unitId}?tab=overview",
        UnitLifecycleStage.Applicant => $"/units/{unitId}?tab=overview",
        UnitLifecycleStage.Lease => $"/units/{unitId}?tab=lease",
        UnitLifecycleStage.MoveIn => $"/units/{unitId}?tab=lease",
        UnitLifecycleStage.Active => $"/units/{unitId}?tab=rent",
        UnitLifecycleStage.Renewal when tenantId is int id => $"/tenants/{id}?action=create-notice&noticeType=RenewalOffer",
        UnitLifecycleStage.Renewal => $"/units/{unitId}?tab=lease",
        UnitLifecycleStage.MoveOut => $"/units/{unitId}?tab=maintenance",
        UnitLifecycleStage.Turnover => $"/units/{unitId}?tab=maintenance",
        _ => $"/units/{unitId}",
    };
}
