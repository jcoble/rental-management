using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IUnitDashboardService"/>
public class UnitDashboardService : IUnitDashboardService
{
    /// <summary>Cap for each Overview list (recent payments, open WOs, pending docs, upcoming appts).</summary>
    private const int OverviewTake = 5;

    /// <summary>Cap for the persistent timeline rail slice returned on the dashboard.</summary>
    private const int RecentTimelineTake = 15;

    private readonly RentalCommandDbContext _db;
    private readonly AuditDescriber _auditDescriber;
    private readonly AuditDiffBuilder _auditDiff;

    public UnitDashboardService(RentalCommandDbContext db, AuditDescriber auditDescriber, AuditDiffBuilder auditDiff, TimeProvider timeProvider)
    {
        _db = db;
        _auditDescriber = auditDescriber;
        _auditDiff = auditDiff;
        _ = timeProvider;
    }

    public async Task<UnitDashboardResponse?> GetDashboardAsync(int portfolioId, int unitId, CancellationToken ct = default)
    {
        var unitRow = await BuildCanonicalDashboardQuery(portfolioId, unitId).FirstOrDefaultAsync(ct);

        if (unitRow is null)
        {
            return null;
        }

        var now = unitRow.EffectiveNowUtc;
        var currentTenants = new List<UnitTenantSummary>();
        var docs = new List<UnitDocumentSummary>();
        var documentCount = 0;
        var upcomingAppointments = new List<UnitAppointmentSummary>();
        var recentPayments = new List<UnitPaymentSummary>();
        var openWorkOrders = new List<UnitWorkOrderSummary>();

        // All bounded overview lists share one translated UNION ALL statement. Each branch keeps its
        // own SQL predicate/order/take; the loop only demultiplexes already-filtered rows by kind.
        var overviewRows = await BuildUnitDashboardItemsQuery(
            portfolioId,
            unitId,
            unitRow.LeaseManagementId,
            unitRow.TenantAccountId,
            unitRow.BusinessDate,
            now)
            .ToListAsync(ct);

        foreach (var item in overviewRows)
        {
            switch (item.Kind)
            {
                case UnitDashboardItemKind.Tenant:
                    currentTenants.Add(new UnitTenantSummary
                    {
                        Id = item.IntValue1!.Value,
                        Name = item.TextValue1 ?? string.Empty,
                        Email = item.TextValue2,
                        Phone = item.TextValue3,
                    });
                    break;
                case UnitDashboardItemKind.Document:
                    docs.Add(new UnitDocumentSummary
                    {
                        Id = item.IntValue1!.Value,
                        FileName = item.TextValue1 ?? string.Empty,
                        ContentType = item.TextValue2 ?? string.Empty,
                        EntityType = item.TextValue3,
                        EntityId = item.LongValue1,
                        UploadedAt = item.DateValue1!.Value,
                    });
                    break;
                case UnitDashboardItemKind.DocumentCount:
                    documentCount = item.IntValue1 ?? 0;
                    break;
                case UnitDashboardItemKind.Appointment:
                    upcomingAppointments.Add(new UnitAppointmentSummary
                    {
                        Id = item.IntValue1!.Value,
                        Title = item.TextValue1 ?? string.Empty,
                        Type = item.TextValue2 ?? string.Empty,
                        Status = item.TextValue3 ?? string.Empty,
                        ScheduledStart = item.DateValue1!.Value,
                        AssignedTo = item.TextValue4,
                    });
                    break;
                case UnitDashboardItemKind.Payment:
                    recentPayments.Add(new UnitPaymentSummary
                    {
                        Id = item.LongValue1!.Value,
                        TenantAccountId = item.IntValue1!.Value,
                        LeaseManagementId = unitRow.LeaseManagementId!.Value,
                        LeaseAgreementId = item.IntValue2,
                        Type = item.TextValue1 ?? string.Empty,
                        Status = item.TextValue2 ?? string.Empty,
                        Description = item.TextValue3 ?? string.Empty,
                        Amount = item.DecimalValue1!.Value,
                        DueDate = item.DateValue1!.Value,
                        PaidDate = item.DateValue2,
                    });
                    break;
                case UnitDashboardItemKind.WorkOrder:
                    openWorkOrders.Add(new UnitWorkOrderSummary
                    {
                        Id = item.IntValue1!.Value,
                        Title = item.TextValue1 ?? string.Empty,
                        Status = item.TextValue2 ?? string.Empty,
                        Priority = item.TextValue3 ?? string.Empty,
                        RequestedAt = item.DateValue1!.Value,
                    });
                    break;
            }
        }

        var outstanding = unitRow.ReceivableBalance;
        var hasOverdue = unitRow.PastDueAmount > 0m;
        var hasDueSoon = unitRow.NextDueOn is not null;

        var recentMoveOutSignal = unitRow.IsInTurnover || unitRow.IsOutOfService || unitRow.IsOnManagementHold
            || unitRow.OpenWorkOrderCount > 0
            || unitRow.Lifecycle == "AccountingCloseout"
            || unitRow.MoveOutInspectionDone;

        var stage = ResolveCanonicalStage(unitRow, unitRow.HasDraftOrPendingAgreement, unitRow.HasOpenApplication,
            unitRow.HasUpcomingShowing, unitRow.HasUpcomingMoveInAppointment, recentMoveOutSignal);
        var nextBestAction = new UnitNextBestAction
        {
            Label = NextBestActionLabel(stage, outstanding, unitRow.AgreementEndOn, unitRow.BusinessDate),
            Href = NextBestActionHref(stage, unitId, unitRow.Unit.PropertyId, unitRow.CurrentPrimaryTenantId),
        };

        // Header rent state + lease-ends-in.
        var rentState = unitRow.TenantAccountId is null
            ? "NoLease"
            : hasOverdue ? "Overdue"
            : hasDueSoon ? "Due"
            : "Current";

        int? leaseEndsInDays = unitRow.AgreementEndOn is { } end
            ? Math.Max(0, end.DayNumber - unitRow.BusinessDate.DayNumber)
            : null;

        var turnover = BuildTurnoverSummary(unitRow, stage, now);

        return new UnitDashboardResponse
        {
            Unit = unitRow.Unit,
            PropertyName = unitRow.PropertyName,
            LifecycleStage = stage.ToString(),
            NextBestAction = nextBestAction,
            LeaseManagementId = unitRow.LeaseManagementId,
            TenantAccountId = unitRow.TenantAccountId,
            OccupancyPossession = new UnitOccupancyPossessionCondition
            {
                Status = unitRow.IsOccupied
                    ? "Occupied"
                    : unitRow.HasScheduledMoveIn ? "PossessionScheduled" : "Vacant",
                IsOccupied = unitRow.IsOccupied,
                HasScheduledMoveIn = unitRow.HasScheduledMoveIn,
                LeaseManagementId = unitRow.LeaseManagementId,
            },
            MarketingAvailability = new UnitMarketingAvailabilityCondition
            {
                Status = unitRow.IsOccupied || unitRow.HasScheduledMoveIn
                    ? "NotAvailable"
                    : unitRow.IsInTurnover || unitRow.IsOutOfService || unitRow.IsOnManagementHold
                        ? "Unavailable"
                        : "Available",
                IsAvailable = !unitRow.IsOccupied
                    && !unitRow.HasScheduledMoveIn
                    && !unitRow.IsInTurnover
                    && !unitRow.IsOutOfService
                    && !unitRow.IsOnManagementHold,
            },
            TenantAccountCondition = new UnitTenantAccountCondition
            {
                Status = unitRow.TenantAccountId is null
                    ? "NoAccount"
                    : unitRow.PastDueAmount > 0m ? "PastDue" : "Current",
                TenantAccountId = unitRow.TenantAccountId,
                ReceivableBalance = unitRow.ReceivableBalance,
                PastDueAmount = unitRow.PastDueAmount,
            },
            LegalNoticeCondition = new UnitLegalNoticeCondition
            {
                Status = unitRow.OpenNoticeCount > 0
                    ? "NoticeOpen"
                    : unitRow.AgreementId is null ? "NoGoverningAgreement" : "NoNotices",
                AgreementId = unitRow.AgreementId,
                AgreementStatus = unitRow.AgreementStatus,
                OpenNoticeCount = unitRow.OpenNoticeCount,
            },
            MaintenanceTurnover = new UnitMaintenanceTurnoverCondition
            {
                Status = turnover.Status,
                OpenWorkOrderCount = unitRow.OpenWorkOrderCount,
                IsInTurnover = unitRow.IsInTurnover,
                IsOutOfService = unitRow.IsOutOfService,
                IsOnManagementHold = unitRow.IsOnManagementHold,
            },
            Header = new UnitDashboardHeader
            {
                RentState = rentState,
                OutstandingRentBalance = outstanding,
                OpenWorkOrderCount = unitRow.OpenWorkOrderCount,
                LeaseEndsInDays = leaseEndsInDays,
                DocsNeedingReviewCount = documentCount,
                CurrentTenantName = unitRow.CurrentPrimaryTenantName,
            },
            CurrentLease = unitRow.AgreementId is not int agreementId ? null : new UnitLeaseSummary
            {
                Id = agreementId,
                LeaseManagementId = unitRow.LeaseManagementId!.Value,
                TenantAccountId = unitRow.TenantAccountId,
                LeaseNumber = string.IsNullOrWhiteSpace(unitRow.AgreementNumber) ? $"Agreement #{agreementId}" : unitRow.AgreementNumber,
                Status = unitRow.AgreementStatus ?? unitRow.Lifecycle ?? "Preparing",
                StartDate = unitRow.AgreementStartOn!.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                EndDate = (unitRow.AgreementEndOn ?? DateOnly.MaxValue).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                MonthlyRent = unitRow.BaseRentAmount ?? 0m,
                SecurityDeposit = unitRow.SecurityDepositObligation ?? 0m,
            },
            CurrentTenant = currentTenants.FirstOrDefault(),
            CurrentTenants = currentTenants,
            Overview = new UnitDashboardOverview
            {
                RecentPayments = recentPayments,
                OpenWorkOrders = openWorkOrders,
                PendingDocs = docs,
                UpcomingAppointments = upcomingAppointments,
            },
            Turnover = turnover,
            RecentTimeline = await GetTimelineAsync(portfolioId, unitId, 0, RecentTimelineTake, ct),
        };
    }

    /// <summary>
    /// Canonical Unit Command Center header query. Occupancy and lifecycle come from database views;
    /// the selected legal artifact, tenant account, balance, and deposit are joined by their canonical
    /// identities in one PostgreSQL statement. No legacy Lease, LeaseTenant, or Unit.Status fact is read.
    /// </summary>
    internal IQueryable<UnitDashboardReadRow> BuildCanonicalDashboardQuery(int portfolioId, int unitId) =>
        from unit in _db.Units.AsNoTracking()
        where unit.PortfolioId == portfolioId && unit.Id == unitId
        join property in _db.Properties.AsNoTracking()
            on new { unit.PortfolioId, Id = unit.PropertyId }
            equals new { property.PortfolioId, property.Id }
        join occupancy in _db.UnitOccupancyProjections.AsNoTracking()
            on new { unit.PortfolioId, UnitId = unit.Id }
            equals new { occupancy.PortfolioId, occupancy.UnitId }
        let selectedRelationshipId = occupancy.CurrentLeaseManagementId ?? occupancy.PlannedLeaseManagementId
        from lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            .Where(row => row.PortfolioId == unit.PortfolioId
                && row.LeaseManagementId == selectedRelationshipId)
            .DefaultIfEmpty()
        let selectedAgreementId = lifecycle.CurrentAgreementId ?? lifecycle.UpcomingAgreementId
        from agreement in _db.LeaseAgreements.AsNoTracking()
            .Where(row => row.PortfolioId == unit.PortfolioId && row.Id == selectedAgreementId)
            .DefaultIfEmpty()
        from agreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
            .Where(row => row.PortfolioId == unit.PortfolioId && row.AgreementId == selectedAgreementId)
            .DefaultIfEmpty()
        from balance in _db.TenantAccountBalanceProjections.AsNoTracking()
            .Where(row => row.PortfolioId == unit.PortfolioId
                && row.TenantAccountId == lifecycle!.TenantAccountId)
            .DefaultIfEmpty()
        from deposit in _db.SecurityDepositBalanceProjections.AsNoTracking()
            .Where(row => row.PortfolioId == unit.PortfolioId
                && row.TenantAccountId == lifecycle!.TenantAccountId)
            .DefaultIfEmpty()
        let openNoticeCount = _db.NoticeDrafts.AsNoTracking().Count(notice =>
            notice.PortfolioId == unit.PortfolioId
            && notice.LeaseManagementId == selectedRelationshipId
            && notice.DismissedAt == null
            && notice.Status != "Sent")
        let openWorkOrderCount = _db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
            .Count(workOrder =>
            workOrder.Status != WorkOrderStatus.Completed
            && workOrder.Status != WorkOrderStatus.Cancelled
            && workOrder.Status != WorkOrderStatus.Archived)
        let hasDraftOrPendingAgreement = _db.LeaseAgreements.AsNoTracking().Any(agreement =>
            agreement.PortfolioId == portfolioId
            && agreement.LeaseManagement!.UnitId == unitId
            && agreement.FullyExecutedAtUtc == null
            && agreement.VoidedAtUtc == null
            && agreement.DraftCanceledAtUtc == null)
        let hasOpenApplication = _db.RentalApplications.AsNoTracking().Any(application =>
            application.UnitId == unitId && application.PortfolioId == portfolioId
            && (application.Status == ApplicationStatus.Submitted
                || application.Status == ApplicationStatus.UnderReview
                || application.Status == ApplicationStatus.Approved)
            && application.ApprovedTenantId == null)
        let hasUpcomingShowing = _db.Appointments.AsNoTracking().Any(appointment =>
            appointment.UnitId == unitId && appointment.PortfolioId == portfolioId
            && appointment.Type == AppointmentType.Showing
            && appointment.ScheduledStart >= occupancy.EffectiveNowUtc
            && appointment.Status != AppointmentStatus.Cancelled)
        let hasUpcomingMoveInAppointment = _db.Appointments.AsNoTracking().Any(appointment =>
            appointment.UnitId == unitId && appointment.PortfolioId == portfolioId
            && appointment.Type == AppointmentType.MoveIn
            && appointment.ScheduledStart >= occupancy.EffectiveNowUtc
            && appointment.Status != AppointmentStatus.Cancelled)
        let moveOutInspectionDone = _db.Inspections.AsNoTracking().Any(inspection =>
            inspection.UnitId == unitId && inspection.PortfolioId == portfolioId
            && inspection.Type == InspectionType.MoveOut
            && (inspection.Status == InspectionStatus.Completed || inspection.Status == InspectionStatus.Reviewed))
        let totalTaskCount = _db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
            .Count()
        let completedTaskCount = _db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
            .Count(workOrder => workOrder.Status == WorkOrderStatus.Completed)
        let estimatedCost = _db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
            .Select(workOrder => (decimal?)workOrder.EstimatedCost).Sum() ?? 0m
        let workOrderActualCost = _db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
            .Select(workOrder => (decimal?)workOrder.ActualCost).Sum() ?? 0m
        let startedAt = _db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
            .Select(workOrder => (DateTime?)workOrder.RequestedAt).Min()
        let targetReadyDate = _db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
            .Where(workOrder => workOrder.Status != WorkOrderStatus.Completed
                && workOrder.Status != WorkOrderStatus.Cancelled
                && workOrder.Status != WorkOrderStatus.Archived)
            .Select(workOrder => (DateTime?)(workOrder.ScheduledWindowEnd ?? workOrder.ScheduledFor))
            .Max()
        let lastWorkOrderActivityAt = _db.WorkOrders.AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
            .Select(workOrder => (DateTime?)(workOrder.CompletedAt ?? workOrder.UpdatedAt))
            .Max()
        let receiptCount = _db.Expenses.AsNoTracking()
            .Where(expense => expense.PortfolioId == portfolioId
                && (expense.UnitId == unitId
                    || (expense.WorkOrderId != null && _db.WorkOrders.AsNoTracking()
                        .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
                        .Select(workOrder => workOrder.Id)
                        .Contains(expense.WorkOrderId.Value))))
            .Count()
        let receiptCost = _db.Expenses.AsNoTracking()
            .Where(expense => expense.PortfolioId == portfolioId
                && expense.Status == ExpenseStatus.Paid
                && (expense.UnitId == unitId
                    || (expense.WorkOrderId != null && _db.WorkOrders.AsNoTracking()
                        .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
                        .Select(workOrder => workOrder.Id)
                        .Contains(expense.WorkOrderId.Value))))
            .Select(expense => (decimal?)expense.Amount).Sum() ?? 0m
        let lastReceiptAt = _db.Expenses.AsNoTracking()
            .Where(expense => expense.PortfolioId == portfolioId
                && (expense.UnitId == unitId
                    || (expense.WorkOrderId != null && _db.WorkOrders.AsNoTracking()
                        .Where(workOrder => workOrder.PortfolioId == portfolioId && workOrder.UnitId == unitId)
                        .Select(workOrder => workOrder.Id)
                        .Contains(expense.WorkOrderId.Value))))
            .Select(expense => (DateTime?)(expense.PaidAt ?? expense.IncurredAt)).Max()
        select new UnitDashboardReadRow
        {
            Unit = new UnitResponse
            {
                Id = unit.Id,
                PropertyId = unit.PropertyId,
                UnitNumber = unit.UnitNumber,
                FloorPlan = unit.FloorPlan,
                PropertyType = property.PropertyType,
                Bedrooms = unit.Bedrooms,
                Bathrooms = unit.Bathrooms,
                SquareFeet = unit.SquareFeet,
                MarketRent = unit.MarketRent,
                Status = occupancy.IsInTurnover || occupancy.IsOutOfService || occupancy.IsOnManagementHold
                    ? DerivedUnitStatus.Offline
                    : occupancy.IsOccupied
                        ? DerivedUnitStatus.Occupied
                        : occupancy.HasScheduledMoveIn
                            ? DerivedUnitStatus.Reserved
                            : DerivedUnitStatus.Vacant,
                Notes = unit.Notes,
                CreatedAt = unit.CreatedAt,
                UpdatedAt = unit.UpdatedAt,
            },
            PropertyName = property.Name,
            EffectiveNowUtc = occupancy.EffectiveNowUtc,
            BusinessDate = (DateOnly?)lifecycle.BusinessDate
                ?? DateOnly.FromDateTime(occupancy.EffectiveNowUtc),
            IsOccupied = occupancy.IsOccupied,
            HasScheduledMoveIn = occupancy.HasScheduledMoveIn,
            IsInTurnover = occupancy.IsInTurnover,
            IsOutOfService = occupancy.IsOutOfService,
            IsOnManagementHold = occupancy.IsOnManagementHold,
            LeaseManagementId = selectedRelationshipId,
            Lifecycle = lifecycle.Lifecycle,
            TenantAccountId = lifecycle.TenantAccountId,
            CurrentPrimaryTenantId = lifecycle.CurrentPrimaryTenantId,
            CurrentPrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
            AgreementId = agreement == null ? null : agreement.Id,
            AgreementNumber = agreement == null ? null : agreement.AgreementNumber,
            AgreementStatus = agreementStatus.AgreementStatus,
            AgreementStartOn = agreement == null ? null : agreement.TermStartOn,
            AgreementEndOn = agreement == null ? null : agreement.TermEndOn,
            BaseRentAmount = agreement == null ? null : agreement.BaseRentAmount,
            SecurityDepositObligation = agreement == null ? null : agreement.SecurityDepositObligation,
            ReceivableBalance = (decimal?)balance.ReceivableBalance ?? 0m,
            PastDueAmount = (decimal?)balance.PastDueAmount ?? 0m,
            NextDueOn = balance.NextDueOn,
            HeldDepositBalance = (decimal?)deposit.HeldBalance ?? 0m,
            OpenNoticeCount = openNoticeCount,
            OpenWorkOrderCount = openWorkOrderCount,
            DocumentsCount = 0,
            HasDraftOrPendingAgreement = hasDraftOrPendingAgreement,
            HasOpenApplication = hasOpenApplication,
            HasUpcomingShowing = hasUpcomingShowing,
            HasUpcomingMoveInAppointment = hasUpcomingMoveInAppointment,
            MoveOutInspectionDone = moveOutInspectionDone,
            TotalTaskCount = totalTaskCount,
            CompletedTaskCount = completedTaskCount,
            EstimatedCost = estimatedCost,
            WorkOrderActualCost = workOrderActualCost,
            StartedAt = startedAt,
            TargetReadyDate = targetReadyDate,
            LastWorkOrderActivityAt = lastWorkOrderActivityAt,
            ReceiptCount = receiptCount,
            ReceiptCost = receiptCost,
            LastReceiptAt = lastReceiptAt,
        };

    private static UnitDashboardStage ResolveCanonicalStage(
        UnitDashboardReadRow row,
        bool hasDraftOrPendingAgreement,
        bool hasOpenApplication,
        bool hasUpcomingShowing,
        bool hasUpcomingMoveInAppointment,
        bool recentMoveOutSignal)
    {
        if (row.IsInTurnover || row.IsOutOfService || row.IsOnManagementHold)
        {
            return UnitDashboardStage.Turnover;
        }

        if (row.IsOccupied && row.Lifecycle == "Ending")
        {
            return UnitDashboardStage.MoveOut;
        }

        if (row.IsOccupied)
        {
            return row.AgreementEndOn is { } end
                && end >= row.BusinessDate
                && end <= row.BusinessDate.AddDays(90)
                ? UnitDashboardStage.Renewal
                : UnitDashboardStage.Active;
        }

        if ((row.HasScheduledMoveIn || hasUpcomingMoveInAppointment)
            && row.AgreementStatus is "Active" or "Upcoming")
        {
            return UnitDashboardStage.MoveIn;
        }

        if (hasDraftOrPendingAgreement || row.HasScheduledMoveIn || row.Lifecycle is "Upcoming" or "Preparing")
        {
            return UnitDashboardStage.Lease;
        }

        if (hasOpenApplication)
        {
            return UnitDashboardStage.Applicant;
        }

        if (recentMoveOutSignal)
        {
            return UnitDashboardStage.Turnover;
        }

        return hasUpcomingShowing ? UnitDashboardStage.Listed : UnitDashboardStage.Ready;
    }

    private static string NextBestActionLabel(
        UnitDashboardStage stage,
        decimal outstanding,
        DateOnly? agreementEndOn,
        DateOnly businessDate) => stage switch
        {
            UnitDashboardStage.Ready => "List this unit",
            UnitDashboardStage.Listed => "Review applicants / schedule showing",
            UnitDashboardStage.Applicant => "Screen & decide on the applicant",
            UnitDashboardStage.Lease => "Finish and send the agreement",
            UnitDashboardStage.MoveIn => "Confirm possession / collect deposit",
            UnitDashboardStage.Active when outstanding > 0m =>
                $"Collect {outstanding.ToString("$#,##0.00", CultureInfo.InvariantCulture)}",
            UnitDashboardStage.Active => "Rent on track",
            UnitDashboardStage.Renewal when agreementEndOn is { } end =>
                $"Prepare renewal — agreement ends in {Math.Max(0, end.DayNumber - businessDate.DayNumber)} days",
            UnitDashboardStage.Renewal => "Prepare renewal",
            UnitDashboardStage.MoveOut => "Schedule move-out inspection",
            UnitDashboardStage.Turnover => "Track make-ready / mark rent-ready",
            _ => "Open unit",
        };

    private static UnitTurnoverSummary BuildTurnoverSummary(
        UnitDashboardReadRow row,
        UnitDashboardStage lifecycleStage,
        DateTime now)
    {
        var lastActivityAt = MaxDate(row.LastWorkOrderActivityAt, row.LastReceiptAt);
        return new UnitTurnoverSummary
        {
            Status = TurnoverStatus(lifecycleStage, row.TotalTaskCount, row.OpenWorkOrderCount),
            TotalTaskCount = row.TotalTaskCount,
            OpenTaskCount = row.OpenWorkOrderCount,
            CompletedTaskCount = row.CompletedTaskCount,
            ReceiptCount = row.ReceiptCount,
            EstimatedCost = row.EstimatedCost,
            ActualCost = row.WorkOrderActualCost + row.ReceiptCost,
            StartedAt = row.StartedAt,
            TargetReadyDate = row.TargetReadyDate,
            LastActivityAt = lastActivityAt,
            DaysInTurnover = row.StartedAt is null
                ? null
                : Math.Max(0, (int)Math.Ceiling(((row.OpenWorkOrderCount > 0 ? now : lastActivityAt ?? now) - row.StartedAt.Value).TotalDays)),
        };
    }

    public async Task<IReadOnlyList<AuditEntryResponse>> GetTimelineAsync(
        int portfolioId, int unitId, int skip, int take, CancellationToken ct = default)
    {
        // Keep the IDOR guard as a scalar EXISTS inside the paged audit statement. This preserves the
        // public timeline boundary without spending a separate round trip before the history query.
        skip = skip < 0 ? 0 : skip;
        take = take <= 0 ? RecentTimelineTake : Math.Min(take, 200);

        // Keep child scopes as IQueryable subqueries so the paged AtomicAuditLogs query does the filtering in
        // SQL. Do not materialize the child ids first; a heavily used unit can have unbounded payments,
        // work orders, inspections, appointments, and expenses.
        var relationshipIds = _db.LeaseManagements.AsNoTracking()
            .Where(relationship => relationship.UnitId == unitId && relationship.PortfolioId == portfolioId)
            .Select(relationship => relationship.Id);

        var agreementIds = _db.LeaseAgreements.AsNoTracking()
            .Where(agreement => agreement.PortfolioId == portfolioId
                && relationshipIds.Contains(agreement.LeaseManagementId))
            .Select(agreement => agreement.Id);

        var accountIds = _db.TenantAccounts.AsNoTracking()
            .Where(account => account.PortfolioId == portfolioId
                && relationshipIds.Contains(account.LeaseManagementId))
            .Select(account => account.Id);

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

        // These writer scopes stay as correlated IQueryable subqueries. Direct unit ids are preferred;
        // where a writer has no unit column, follow only a canonical relationship, application, listing,
        // inspection, or work-order path that is already scoped above.
        var recurringTenantChargeIds = _db.RecurringTenantCharges.AsNoTracking()
            .Where(charge => charge.PortfolioId == portfolioId
                && (charge.UnitId == unitId
                    || (charge.UnitId == null && accountIds.Contains(charge.TenantAccountId))
                    || (charge.UnitId == null && agreementIds.Contains(charge.LeaseAgreementId))))
            .Select(charge => charge.Id);

        var recurringExpenseIds = _db.RecurringExpenses.AsNoTracking()
            .Where(expense => expense.PortfolioId == portfolioId && expense.UnitId == unitId)
            .Select(expense => expense.Id);

        var capitalAssetIds = _db.CapitalAssets.AsNoTracking()
            .Where(asset => asset.PortfolioId == portfolioId
                && (asset.UnitId == unitId
                    || (asset.UnitId == null
                        && asset.SourceExpenseId != null
                        && expenseIds.Contains(asset.SourceExpenseId.Value))))
            .Select(asset => asset.Id);

        var applicationIds = _db.RentalApplications.AsNoTracking()
            .Where(application => application.PortfolioId == portfolioId
                && (application.UnitId == unitId
                    || (application.UnitId == null
                        && application.PreparedLeaseManagementId != null
                        && relationshipIds.Contains(application.PreparedLeaseManagementId.Value))))
            .Select(application => application.Id);

        var applicantScreeningIds = _db.ApplicantScreenings.AsNoTracking()
            .Where(screening => screening.PortfolioId == portfolioId
                && applicationIds.Contains(screening.ApplicationId))
            .Select(screening => screening.Id);

        var rentalListingIds = _db.RentalListings.AsNoTracking()
            .Where(listing => listing.PortfolioId == portfolioId && listing.UnitId == unitId)
            .Select(listing => listing.Id);

        var listingPublicationIds = _db.ListingPublications.AsNoTracking()
            .Where(publication => publication.PortfolioId == portfolioId
                && rentalListingIds.Contains(publication.RentalListingId))
            .Select(publication => publication.Id);

        var listingPhotoIds = _db.ListingPhotos.AsNoTracking()
            .Where(photo => photo.PortfolioId == portfolioId
                && rentalListingIds.Contains(photo.RentalListingId))
            .Select(photo => photo.Id);

        var workOrderStatusEventIds = _db.WorkOrderStatusEvents.AsNoTracking()
            .Where(statusEvent => statusEvent.PortfolioId == portfolioId
                && workOrderIds.Contains(statusEvent.WorkOrderId))
            .Select(statusEvent => statusEvent.Id);

        var evictionCaseIds = _db.EvictionCases.AsNoTracking()
            .Where(eviction => eviction.PortfolioId == portfolioId && eviction.UnitId == unitId)
            .Select(eviction => eviction.Id);

        var evictionCaseEventIds = _db.EvictionCaseEvents.AsNoTracking()
            .Where(statusEvent => statusEvent.PortfolioId == portfolioId
                && evictionCaseIds.Contains(statusEvent.EvictionCaseId))
            .Select(statusEvent => statusEvent.Id);

        var tenantIds = _db.LeaseManagementParties.AsNoTracking()
            .Where(party => party.PortfolioId == portfolioId
                && relationshipIds.Contains(party.LeaseManagementId))
            .Select(party => party.TenantId);

        var partyIds = _db.LeaseManagementParties.AsNoTracking()
            .Where(party => party.PortfolioId == portfolioId
                && relationshipIds.Contains(party.LeaseManagementId))
            .Select(party => party.Id);

        var tenantUserAccessIds = _db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.PortfolioId == portfolioId
                && partyIds.Contains(access.LeaseManagementPartyId))
            .Select(access => access.Id);

        var operationalPeriodIds = _db.UnitOperationalPeriods.AsNoTracking()
            .Where(period => period.PortfolioId == portfolioId && period.UnitId == unitId)
            .Select(period => period.Id);

        var ledgerEntryIds = _db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && accountIds.Contains(entry.TenantAccountId))
            .Select(entry => entry.Id);

        var inspectionItemIds = _db.InspectionItems.AsNoTracking()
            .Where(item => item.PortfolioId == portfolioId
                && inspectionIds.Contains(item.InspectionId))
            .Select(item => item.Id);

        var vendorDispatchIds = _db.VendorDispatches.AsNoTracking()
            .Where(dispatch => dispatch.PortfolioId == portfolioId
                && workOrderIds.Contains(dispatch.WorkOrderId))
            .Select(dispatch => dispatch.Id);

        var scanDraftIds = _db.ScanDrafts.AsNoTracking()
            .Where(draft => draft.PortfolioId == portfolioId
                && (draft.CaptureUnitId == unitId
                    || (draft.CaptureUnitId == null && (
                        (draft.CaptureLeaseManagementId != null
                            && relationshipIds.Contains(draft.CaptureLeaseManagementId.Value))
                        || (draft.CaptureLeaseAgreementId != null
                            && agreementIds.Contains(draft.CaptureLeaseAgreementId.Value))
                        || (draft.CaptureTenantAccountId != null
                            && accountIds.Contains(draft.CaptureTenantAccountId.Value))
                        || (draft.CaptureTenantLedgerEntryId != null
                            && ledgerEntryIds.Contains(draft.CaptureTenantLedgerEntryId.Value))
                        || (draft.CaptureWorkOrderId != null
                            && workOrderIds.Contains(draft.CaptureWorkOrderId.Value))
                        || (draft.CaptureApplicationId != null
                            && applicationIds.Contains(draft.CaptureApplicationId.Value))
                        || (draft.CaptureRentalListingId != null
                            && rentalListingIds.Contains(draft.CaptureRentalListingId.Value))))))
            .Select(draft => draft.Id);

        var paymentAttemptIds = _db.TenantPaymentAttempts.AsNoTracking()
            .Where(attempt => attempt.PortfolioId == portfolioId
                && accountIds.Contains(attempt.TenantAccountId))
            .Select(attempt => attempt.Id);

        var accountConditionPeriodIds = _db.TenantAccountConditionPeriods.AsNoTracking()
            .Where(period => period.PortfolioId == portfolioId
                && accountIds.Contains(period.TenantAccountId))
            .Select(period => period.Id);

        var autopayEnrollmentIds = _db.TenantAutopayEnrollments.AsNoTracking()
            .Where(enrollment => enrollment.PortfolioId == portfolioId
                && accountIds.Contains(enrollment.TenantAccountId))
            .Select(enrollment => enrollment.Id);

        var depositAccountIds = _db.SecurityDepositAccounts.AsNoTracking()
            .Where(account => account.PortfolioId == portfolioId
                && accountIds.Contains(account.TenantAccountId))
            .Select(account => account.Id);

        var depositEntryIds = _db.SecurityDepositEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && depositAccountIds.Contains(entry.SecurityDepositAccountId))
            .Select(entry => entry.Id);

        var addendumIds = _db.LeaseAddenda.AsNoTracking()
            .Where(addendum => addendum.PortfolioId == portfolioId
                && relationshipIds.Contains(addendum.LeaseManagementId))
            .Select(addendum => addendum.Id);

        var addendumSignerIds = _db.LeaseAddendumSigners.AsNoTracking()
            .Where(signer => signer.PortfolioId == portfolioId
                && addendumIds.Contains(signer.LeaseAddendumId))
            .Select(signer => signer.Id);

        var addendumFinancialEffectIds = _db.LeaseAddendumFinancialEffects.AsNoTracking()
            .Where(effect => effect.PortfolioId == portfolioId
                && addendumIds.Contains(effect.LeaseAddendumId))
            .Select(effect => effect.Id);

        var renewalDecisionIds = _db.LeaseRenewalAddendumDecisions.AsNoTracking()
            .Where(decision => decision.PortfolioId == portfolioId
                && relationshipIds.Contains(decision.LeaseManagementId))
            .Select(decision => decision.Id);

        var agreementSignerIds = _db.LeaseAgreementSigners.AsNoTracking()
            .Where(signer => signer.PortfolioId == portfolioId
                && agreementIds.Contains(signer.LeaseAgreementId))
            .Select(signer => signer.Id);

        var legalArtifactIds = _db.LegalDocumentArtifacts.AsNoTracking()
            .Where(artifact => artifact.PortfolioId == portfolioId
                && (_db.LeaseAgreements.Any(agreement => agreement.PortfolioId == portfolioId
                        && (agreement.IssuedArtifactId == artifact.Id || agreement.ExecutedArtifactId == artifact.Id)
                        && relationshipIds.Contains(agreement.LeaseManagementId))
                    || _db.LeaseAddenda.Any(addendum => addendum.PortfolioId == portfolioId
                        && (addendum.IssuedArtifactId == artifact.Id || addendum.ExecutedArtifactId == artifact.Id)
                        && relationshipIds.Contains(addendum.LeaseManagementId))))
            .Select(artifact => artifact.Id);

        var legalArtifactFileIds = _db.LegalDocumentArtifacts.AsNoTracking()
            .Where(artifact => legalArtifactIds.Contains(artifact.Id))
            .Select(artifact => artifact.StoredFileId);

        var storedFileIds = _db.StoredFiles.AsNoTracking()
            .Where(file => file.PortfolioId == portfolioId && (
                legalArtifactFileIds.Contains(file.Id)
                || (file.EntityId != null && (
                    (file.EntityType == "Unit" && file.EntityId == unitId)
                    || (file.EntityType == nameof(LeaseAgreement) && agreementIds.Contains((int)file.EntityId.Value))
                    || (file.EntityType == nameof(LeaseAddendum) && addendumIds.Contains((int)file.EntityId.Value))
                    || (file.EntityType == nameof(LegalDocumentArtifact) && legalArtifactIds.Contains((int)file.EntityId.Value))
                    || (file.EntityType == nameof(TenantAccount) && accountIds.Contains((int)file.EntityId.Value))
                    || (file.EntityType == nameof(TenantLedgerEntry) && ledgerEntryIds.Contains(file.EntityId.Value))
                    || (file.EntityType == nameof(SecurityDepositAccount) && depositAccountIds.Contains((int)file.EntityId.Value))
                    || (file.EntityType == nameof(SecurityDepositEntry) && depositEntryIds.Contains(file.EntityId.Value))
                    || (file.EntityType == "Expense" && expenseIds.Contains((int)file.EntityId.Value))
                    || (file.EntityType == "WorkOrder" && workOrderIds.Contains((int)file.EntityId.Value))
                    || (file.EntityType == "Inspection" && inspectionIds.Contains((int)file.EntityId.Value))))))
            .Select(file => file.Id);

        var signatureRequestIds = _db.SignatureRequests.AsNoTracking()
            .Where(request => request.PortfolioId == portfolioId
                && ((request.LeaseAgreementId != null && agreementIds.Contains(request.LeaseAgreementId.Value))
                    || (request.LeaseAddendumId != null && addendumIds.Contains(request.LeaseAddendumId.Value))))
            .Select(request => request.Id);

        var signatureSignerIds = _db.SignatureSigners.AsNoTracking()
            .Where(signer => signer.PortfolioId == portfolioId
                && signatureRequestIds.Contains(signer.SignatureRequestId))
            .Select(signer => signer.Id);

        var noticeDraftIds = _db.NoticeDrafts.AsNoTracking()
            .Where(draft => draft.PortfolioId == portfolioId
                && relationshipIds.Contains(draft.LeaseManagementId))
            .Select(draft => draft.Id);

        var renderedNoticeIds = _db.RenderedNotices.AsNoTracking()
            .Where(rendered => rendered.PortfolioId == portfolioId
                && relationshipIds.Contains(rendered.LeaseManagementId))
            .Select(rendered => rendered.Id);

        var noticeDeliveryEvidenceIds = _db.NoticeDeliveryEvidence.AsNoTracking()
            .Where(evidence => evidence.PortfolioId == portfolioId
                && renderedNoticeIds.Contains(evidence.RenderedNoticeId))
            .Select(evidence => evidence.Id);

        var noticeWorkItemIds = _db.TenantNoticeWorkItems.AsNoTracking()
            .Where(item => item.PortfolioId == portfolioId
                && relationshipIds.Contains(item.LeaseManagementId))
            .Select(item => item.Id);

        var conversationIds = _db.Conversations.AsNoTracking()
            .Where(conversation => conversation.PortfolioId == portfolioId
                && conversation.WorkOrderId != null
                && workOrderIds.Contains(conversation.WorkOrderId.Value))
            .Select(conversation => conversation.Id);

        var conversationMessageIds = _db.ConversationMessages.AsNoTracking()
            .Where(message => conversationIds.Contains(message.ConversationId))
            .Select(message => message.Id);

        var notificationIds = _db.Notifications.AsNoTracking()
            .Where(notification => notification.PortfolioId == portfolioId
                && notification.RelatedEntityId != null
                && ((notification.RelatedEntityType == nameof(Conversation)
                        && conversationIds.Contains(notification.RelatedEntityId.Value))
                    || (notification.RelatedEntityType == nameof(ConversationMessage)
                        && conversationMessageIds.Contains(notification.RelatedEntityId.Value))
                    || (notification.RelatedEntityType == nameof(NoticeDraft)
                        && noticeDraftIds.Contains(notification.RelatedEntityId.Value))
                    || (notification.RelatedEntityType == nameof(TenantLedgerEntry)
                        && ledgerEntryIds.Contains(notification.RelatedEntityId.Value))
                    || (notification.RelatedEntityType == nameof(SecurityDepositEntry)
                        && depositEntryIds.Contains(notification.RelatedEntityId.Value))
                    || (notification.NavigationResourceKind == nameof(Conversation)
                        && notification.NavigationResourceId != null
                        && conversationIds.Contains(notification.NavigationResourceId.Value))
                    || (notification.NavigationResourceKind == nameof(NoticeDraft)
                        && notification.NavigationResourceId != null
                        && noticeDraftIds.Contains(notification.NavigationResourceId.Value))
                    || (notification.NavigationResourceKind == nameof(TenantLedgerEntry)
                        && notification.NavigationResourceId != null
                        && ledgerEntryIds.Contains(notification.NavigationResourceId.Value))
                    || (notification.NavigationParentResourceKind == nameof(TenantAccount)
                        && notification.NavigationParentResourceId != null
                        && accountIds.Contains(notification.NavigationParentResourceId!.Value))))
            .Select(notification => notification.Id);

        // ONE audit query with translated OR/IN subqueries, newest first, paged.
        var rows = await _db.AtomicAuditLogs
            .AsNoTracking()
            .Where(a => _db.Units.AsNoTracking().Any(u =>
                    u.Id == unitId && u.Property!.PortfolioId == portfolioId)
                && a.PortfolioId == portfolioId && (
                (a.EntityType == "Unit" && a.EntityId == unitId) ||
                (a.EntityType == nameof(LeaseManagement) && relationshipIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(LeaseAgreement) && agreementIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(TenantAccount) && accountIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(Tenant) && tenantIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(TenantLedgerEntry) && ledgerEntryIds.Contains((long)a.EntityId)) ||
                (a.EntityType == nameof(SecurityDepositAccount) && depositAccountIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(SecurityDepositEntry) && depositEntryIds.Contains((long)a.EntityId)) ||
                (a.EntityType == nameof(TenantPaymentAttempt) && paymentAttemptIds.Contains((long)a.EntityId)) ||
                (a.EntityType == nameof(TenantAccountConditionPeriod) && accountConditionPeriodIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(TenantAutopayEnrollment) && autopayEnrollmentIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(LeaseAgreementSigner) && agreementSignerIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(LeaseAddendum) && addendumIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(LeaseAddendumSigner) && addendumSignerIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(LeaseAddendumFinancialEffect) && addendumFinancialEffectIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(LeaseRenewalAddendumDecision) && renewalDecisionIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(StoredFile) && storedFileIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(LegalDocumentArtifact) && legalArtifactIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(SignatureRequest) && signatureRequestIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(SignatureSigner) && signatureSignerIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(NoticeDraft) && noticeDraftIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(RenderedNotice) && renderedNoticeIds.Contains((long)a.EntityId)) ||
                (a.EntityType == nameof(NoticeDeliveryEvidence) && noticeDeliveryEvidenceIds.Contains((long)a.EntityId)) ||
                (a.EntityType == nameof(TenantNoticeWorkItem) && noticeWorkItemIds.Contains((long)a.EntityId)) ||
                (a.EntityType == nameof(Conversation) && conversationIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(ConversationMessage) && conversationMessageIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(Notification) && notificationIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(LeaseManagementParty) && partyIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(TenantUserAccess) && tenantUserAccessIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(UnitOperationalPeriod) && operationalPeriodIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(EvictionCase) && evictionCaseIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(EvictionCaseEvent) && evictionCaseEventIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(WorkOrderStatusEvent) && workOrderStatusEventIds.Contains(a.EntityId)) ||
                (a.EntityType == "WorkOrder" && workOrderIds.Contains(a.EntityId)) ||
                (a.EntityType == "Inspection" && inspectionIds.Contains(a.EntityId)) ||
                (a.EntityType == "Appointment" && appointmentIds.Contains(a.EntityId)) ||
                (a.EntityType == "Expense" && expenseIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(RecurringTenantCharge) && recurringTenantChargeIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(RecurringExpense) && recurringExpenseIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(CapitalAsset) && capitalAssetIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(RentalApplication) && applicationIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(ApplicantScreening) && applicantScreeningIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(RentalListing) && rentalListingIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(ListingPublication) && listingPublicationIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(ListingPhoto) && listingPhotoIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(InspectionItem) && inspectionItemIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(VendorDispatch) && vendorDispatchIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(ScanDraft) && scanDraftIds.Contains(a.EntityId))))
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .Skip(skip)
            .Take(take)
            .Select(a => new TimelineReadRow
            {
                Audit = a,
                ActorName = a.ActorLabel != null && a.ActorLabel != ""
                    ? a.ActorLabel
                    : a.UserId != null
                        ? _db.Users
                            .Where(user => user.Id == a.UserId.Value
                                && user.WorkspaceAccessContexts.Any(context =>
                                    context.PortfolioId == portfolioId))
                            .Select(user => user.DisplayName != null && user.DisplayName != ""
                                ? user.DisplayName
                                : user.Email)
                            .FirstOrDefault()
                        : null,
            })
            .ToListAsync(ct);

        return rows
            .Select(row =>
            {
                var response = AuditEntryResponse.FromEntity(row.Audit, _auditDescriber, _auditDiff, unitId: unitId);
                if (!string.IsNullOrWhiteSpace(row.ActorName))
                {
                    response.Actor = row.ActorName!;
                }

                return response;
            })
            .ToList();
    }

    /// <summary>
    /// All bounded Unit Command Center overview lists as one server-side UNION ALL statement. Every
    /// branch retains its own authorization predicate, ordering, and cap where the legacy response was
    /// bounded; the current-tenant branch remains complete. The response layer only demultiplexes the
    /// tagged rows into the existing DTO lists.
    /// </summary>
    private IQueryable<UnitDashboardItemReadRow> BuildUnitDashboardItemsQuery(
        int portfolioId,
        int unitId,
        int? leaseManagementId,
        int? tenantAccountId,
        DateOnly businessDate,
        DateTime now)
    {
        var tenantRows = _db.LeaseManagementParties
            .AsNoTracking()
            .Where(party => party.PortfolioId == portfolioId
                && party.LeaseManagementId == leaseManagementId
                && party.EffectiveFrom <= businessDate
                && (party.EffectiveThrough == null || party.EffectiveThrough >= businessDate)
                && party.Role != LeaseManagementPartyRole.Guarantor)
            .OrderBy(party => party.Role == LeaseManagementPartyRole.PrimaryTenant ? 0
                : party.Role == LeaseManagementPartyRole.CoTenant ? 1
                : party.Role == LeaseManagementPartyRole.Occupant ? 2
                : 3)
            .ThenBy(party => party.Id)
            .Select(party => new UnitDashboardItemReadRow
            {
                Kind = UnitDashboardItemKind.Tenant,
                IntValue1 = party.TenantId,
                IntValue2 = 0,
                LongValue1 = 0L,
                TextValue1 = (party.Tenant!.FirstName + " " + party.Tenant.LastName).Trim(),
                TextValue2 = party.Tenant.Email,
                TextValue3 = party.Tenant.Phone,
                TextValue4 = string.Empty,
                DateValue1 = DateTime.UnixEpoch,
                DateValue2 = DateTime.UnixEpoch,
                DecimalValue1 = 0m,
                SortRole = party.Role == LeaseManagementPartyRole.PrimaryTenant ? 0
                    : party.Role == LeaseManagementPartyRole.CoTenant ? 1
                    : party.Role == LeaseManagementPartyRole.Occupant ? 2
                    : 3,
                SortIdAscending = party.Id,
                SortIdDescending = 0L,
                SortDateDescending = DateTime.UnixEpoch,
                SortDateAscending = DateTime.UnixEpoch,
            });

        var documentRows = BuildUnitDocumentsQuery(portfolioId, unitId)
            .OrderByDescending(file => file.UploadedAt)
            .Take(OverviewTake)
            .Select(file => new UnitDashboardItemReadRow
            {
                Kind = UnitDashboardItemKind.Document,
                IntValue1 = file.Id,
                IntValue2 = 0,
                LongValue1 = file.EntityId,
                TextValue1 = file.FileName,
                TextValue2 = file.ContentType,
                TextValue3 = file.EntityType,
                TextValue4 = string.Empty,
                DateValue1 = file.UploadedAt,
                DateValue2 = DateTime.UnixEpoch,
                DecimalValue1 = 0m,
                SortRole = 0,
                SortIdAscending = 0L,
                SortIdDescending = 0L,
                SortDateDescending = file.UploadedAt,
                SortDateAscending = DateTime.UnixEpoch,
            });

        var documentCountRows = BuildUnitDocumentsQuery(portfolioId, unitId)
            .GroupBy(_ => 1)
            .Select(group => new UnitDashboardItemReadRow
            {
                Kind = UnitDashboardItemKind.DocumentCount,
                IntValue1 = group.Count(),
                IntValue2 = 0,
                LongValue1 = 0L,
                TextValue1 = string.Empty,
                TextValue2 = string.Empty,
                TextValue3 = string.Empty,
                TextValue4 = string.Empty,
                DateValue1 = DateTime.UnixEpoch,
                DateValue2 = DateTime.UnixEpoch,
                DecimalValue1 = 0m,
                SortRole = 0,
                SortIdAscending = 0L,
                SortIdDescending = 0L,
                SortDateDescending = DateTime.UnixEpoch,
                SortDateAscending = DateTime.UnixEpoch,
            });

        var appointmentRows = _db.Appointments
            .AsNoTracking()
            .Where(appointment => appointment.UnitId == unitId
                && appointment.PortfolioId == portfolioId
                && appointment.ScheduledStart >= now
                && appointment.Status != AppointmentStatus.Cancelled)
            .OrderBy(appointment => appointment.ScheduledStart)
            .Take(OverviewTake)
            .Select(appointment => new UnitDashboardItemReadRow
            {
                Kind = UnitDashboardItemKind.Appointment,
                IntValue1 = appointment.Id,
                IntValue2 = 0,
                LongValue1 = 0L,
                TextValue1 = appointment.Title,
                TextValue2 = appointment.Type.ToString(),
                TextValue3 = appointment.Status.ToString(),
                TextValue4 = appointment.AssignedTo,
                DateValue1 = appointment.ScheduledStart,
                DateValue2 = DateTime.UnixEpoch,
                DecimalValue1 = 0m,
                SortRole = 0,
                SortIdAscending = 0L,
                SortIdDescending = 0L,
                SortDateDescending = DateTime.UnixEpoch,
                SortDateAscending = appointment.ScheduledStart,
            });

        var paymentRows = _db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => tenantAccountId != null
                && entry.PortfolioId == portfolioId
                && entry.TenantAccountId == tenantAccountId
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt)
            .OrderByDescending(entry => entry.PostedAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(OverviewTake)
            .Select(entry => new UnitDashboardItemReadRow
            {
                Kind = UnitDashboardItemKind.Payment,
                LongValue1 = entry.Id,
                IntValue1 = entry.TenantAccountId,
                IntValue2 = entry.LeaseAgreementId,
                TextValue1 = entry.EntryType.ToString(),
                TextValue2 = "Posted",
                TextValue3 = entry.Description,
                DecimalValue1 = entry.Amount,
                DateValue1 = entry.PostedAtUtc,
                DateValue2 = entry.PostedAtUtc,
                TextValue4 = string.Empty,
                SortRole = 0,
                SortIdAscending = 0L,
                SortIdDescending = entry.Id,
                SortDateDescending = entry.PostedAtUtc,
                SortDateAscending = DateTime.UnixEpoch,
            });

        var workOrderRows = _db.WorkOrders
            .AsNoTracking()
            .Where(workOrder => workOrder.UnitId == unitId
                && workOrder.PortfolioId == portfolioId
                && workOrder.Status != WorkOrderStatus.Completed
                && workOrder.Status != WorkOrderStatus.Cancelled
                && workOrder.Status != WorkOrderStatus.Archived)
            .OrderByDescending(workOrder => workOrder.RequestedAt)
            .ThenByDescending(workOrder => workOrder.Id)
            .Take(OverviewTake)
            .Select(workOrder => new UnitDashboardItemReadRow
            {
                Kind = UnitDashboardItemKind.WorkOrder,
                IntValue1 = workOrder.Id,
                IntValue2 = 0,
                LongValue1 = 0L,
                TextValue1 = workOrder.Title,
                TextValue2 = workOrder.Status.ToString(),
                TextValue3 = workOrder.Priority.ToString(),
                TextValue4 = string.Empty,
                DateValue1 = workOrder.RequestedAt,
                DateValue2 = DateTime.UnixEpoch,
                DecimalValue1 = 0m,
                SortRole = 0,
                SortIdAscending = 0L,
                SortIdDescending = workOrder.Id,
                SortDateDescending = workOrder.RequestedAt,
                SortDateAscending = DateTime.UnixEpoch,
            });

        return tenantRows
            .Concat(documentRows)
            .Concat(documentCountRows)
            .Concat(appointmentRows)
            .Concat(paymentRows)
            .Concat(workOrderRows)
            .OrderBy(row => row.Kind)
            .ThenBy(row => row.SortRole)
            .ThenBy(row => row.SortIdAscending)
            .ThenByDescending(row => row.SortDateDescending)
            .ThenBy(row => row.SortDateAscending)
            .ThenByDescending(row => row.SortIdDescending);
    }

    /// <summary>
    /// The set of <see cref="StoredFile"/> rows attached to the unit and its children, as a single
    /// <see cref="IQueryable"/> (EntityType/EntityId predicates against indexed child-id subqueries).
    /// Reused for both the capped Overview list and the header count so the shape stays identical.
    /// </summary>
    private IQueryable<StoredFile> BuildUnitDocumentsQuery(int portfolioId, int unitId)
    {
        var relationshipIds = _db.LeaseManagements
            .Where(management => management.UnitId == unitId && management.PortfolioId == portfolioId)
            .Select(management => management.Id);
        var agreementIds = _db.LeaseAgreements
            .Where(agreement => agreement.PortfolioId == portfolioId
                && relationshipIds.Contains(agreement.LeaseManagementId))
            .Select(agreement => (long)agreement.Id);
        var accountIds = _db.TenantAccounts
            .Where(account => account.PortfolioId == portfolioId
                && relationshipIds.Contains(account.LeaseManagementId))
            .Select(account => (long)account.Id);
        var ledgerEntryIds = _db.TenantLedgerEntries
            .Where(entry => entry.PortfolioId == portfolioId
                && accountIds.Contains((long)entry.TenantAccountId))
            .Select(entry => entry.Id);
        var depositAccountIds = _db.SecurityDepositAccounts
            .Where(account => account.PortfolioId == portfolioId
                && accountIds.Contains((long)account.TenantAccountId))
            .Select(account => (long)account.Id);
        var workOrderIds = _db.WorkOrders.Where(w => w.UnitId == unitId && w.PortfolioId == portfolioId).Select(w => (long)w.Id);
        var inspectionIds = _db.Inspections.Where(i => i.UnitId == unitId && i.PortfolioId == portfolioId).Select(i => (long)i.Id);
        var expenseIds = _db.Expenses
            .Where(e => e.PortfolioId == portfolioId
                && (e.UnitId == unitId || (e.WorkOrderId != null && workOrderIds.Contains((long)e.WorkOrderId.Value))))
            .Select(e => (long)e.Id);

        var legalArtifactFileIds = _db.LegalDocumentArtifacts
            .Where(artifact => artifact.PortfolioId == portfolioId
                && _db.LeaseAgreements.Any(agreement => agreement.PortfolioId == portfolioId
                    && (agreement.IssuedArtifactId == artifact.Id || agreement.ExecutedArtifactId == artifact.Id)
                    && agreement.LeaseManagement!.UnitId == unitId))
            .Select(artifact => artifact.StoredFileId);

        var legalArtifactIds = _db.LegalDocumentArtifacts
            .Where(artifact => artifact.PortfolioId == portfolioId
                && _db.LeaseAgreements.Any(agreement => agreement.PortfolioId == portfolioId
                    && (agreement.IssuedArtifactId == artifact.Id || agreement.ExecutedArtifactId == artifact.Id)
                    && relationshipIds.Contains(agreement.LeaseManagementId)))
            .Select(artifact => (long)artifact.Id);

        var ledgerSourceFileIds = _db.TenantLedgerEntries
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.SourceStoredFileId != null
                && entry.TenantAccount!.LeaseManagement!.UnitId == unitId)
            .Select(entry => entry.SourceStoredFileId!.Value);

        return _db.StoredFiles
            .AsNoTracking()
            .Where(file => file.PortfolioId == portfolioId && (
                legalArtifactFileIds.Contains(file.Id)
                || ledgerSourceFileIds.Contains(file.Id)
                || (file.EntityId != null && (
                    (file.EntityType == "Unit" && file.EntityId == unitId)
                    || (file.EntityType == nameof(LeaseAgreement) && agreementIds.Contains(file.EntityId.Value))
                    || (file.EntityType == nameof(LegalDocumentArtifact) && legalArtifactIds.Contains(file.EntityId.Value))
                    || (file.EntityType == nameof(TenantAccount) && accountIds.Contains(file.EntityId.Value))
                    || (file.EntityType == nameof(TenantLedgerEntry) && ledgerEntryIds.Contains(file.EntityId.Value))
                    || (file.EntityType == nameof(SecurityDepositAccount) && depositAccountIds.Contains(file.EntityId.Value))
                    || (file.EntityType == "Expense" && expenseIds.Contains(file.EntityId.Value))
                    || (file.EntityType == "WorkOrder" && workOrderIds.Contains(file.EntityId.Value))
                    || (file.EntityType == "Inspection" && inspectionIds.Contains(file.EntityId.Value))))));
    }

    /// <summary>Deep link for a stage's next-best-action: the relevant unit tab (drawer flows attach there).</summary>
    private static string NextBestActionHref(UnitDashboardStage stage, int unitId, int propertyId, int? tenantId) => stage switch
    {
        UnitDashboardStage.Ready => $"/units/{unitId}?tab=leasing&view=listing",
        UnitDashboardStage.Listed => $"/units/{unitId}?tab=summary",
        UnitDashboardStage.Applicant => $"/units/{unitId}?tab=summary",
        UnitDashboardStage.Lease => $"/units/{unitId}?tab=tenant-lease&view=agreements",
        UnitDashboardStage.MoveIn => $"/units/{unitId}?tab=tenant-lease&view=agreements&action=confirm-move-in",
        UnitDashboardStage.Active => $"/units/{unitId}?tab=money&ledger=rent",
        UnitDashboardStage.Renewal when tenantId is int id => $"/tenants/{id}?action=create-notice&noticeType=lease-renewal-offer",
        UnitDashboardStage.Renewal => $"/units/{unitId}?tab=tenant-lease&view=agreements",
        UnitDashboardStage.MoveOut => $"/units/{unitId}?tab=maintenance&view=turnover",
        UnitDashboardStage.Turnover => $"/units/{unitId}?tab=maintenance&view=turnover",
        _ => $"/units/{unitId}",
    };

    private static string TurnoverStatus(UnitDashboardStage lifecycleStage, int totalTasks, int openTasks)
    {
        if (openTasks > 0)
        {
            return lifecycleStage == UnitDashboardStage.MoveOut ? "MoveOut" : "InProgress";
        }

        if (totalTasks > 0)
        {
            return "RentReady";
        }

        return lifecycleStage is UnitDashboardStage.MoveOut or UnitDashboardStage.Turnover
            ? "AwaitingVacancy"
            : "NotStarted";
    }

    /// <summary>
    /// Presentation stages for the unit command center. Canonical occupancy and agreement lifecycle
    /// remain sourced from the database projections; these values only select dashboard copy and routes.
    /// </summary>
    private enum UnitDashboardStage
    {
        Ready,
        Listed,
        Applicant,
        Lease,
        MoveIn,
        Active,
        Renewal,
        MoveOut,
        Turnover,
    }

    private static DateTime? MaxDate(DateTime? first, DateTime? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first >= second ? first : second;
    }

    internal sealed class UnitDashboardReadRow
    {
        public required UnitResponse Unit { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public DateTime EffectiveNowUtc { get; init; }
        public DateOnly BusinessDate { get; init; }
        public bool IsOccupied { get; init; }
        public bool HasScheduledMoveIn { get; init; }
        public bool IsInTurnover { get; init; }
        public bool IsOutOfService { get; init; }
        public bool IsOnManagementHold { get; init; }
        public int? LeaseManagementId { get; init; }
        public string? Lifecycle { get; init; }
        public int? TenantAccountId { get; init; }
        public int? CurrentPrimaryTenantId { get; init; }
        public string? CurrentPrimaryTenantName { get; init; }
        public int? AgreementId { get; init; }
        public string? AgreementNumber { get; init; }
        public string? AgreementStatus { get; init; }
        public DateOnly? AgreementStartOn { get; init; }
        public DateOnly? AgreementEndOn { get; init; }
        public decimal? BaseRentAmount { get; init; }
        public decimal? SecurityDepositObligation { get; init; }
        public decimal ReceivableBalance { get; init; }
        public decimal PastDueAmount { get; init; }
        public DateOnly? NextDueOn { get; init; }
        public decimal HeldDepositBalance { get; init; }
        public int OpenNoticeCount { get; init; }
        public int OpenWorkOrderCount { get; init; }
        public int DocumentsCount { get; init; }
        public bool HasDraftOrPendingAgreement { get; init; }
        public bool HasOpenApplication { get; init; }
        public bool HasUpcomingShowing { get; init; }
        public bool HasUpcomingMoveInAppointment { get; init; }
        public bool MoveOutInspectionDone { get; init; }
        public int TotalTaskCount { get; init; }
        public int CompletedTaskCount { get; init; }
        public int ReceiptCount { get; init; }
        public decimal EstimatedCost { get; init; }
        public decimal WorkOrderActualCost { get; init; }
        public decimal ReceiptCost { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? TargetReadyDate { get; init; }
        public DateTime? LastWorkOrderActivityAt { get; init; }
        public DateTime? LastReceiptAt { get; init; }
    }

    private enum UnitDashboardItemKind
    {
        Tenant,
        Document,
        DocumentCount,
        Appointment,
        Payment,
        WorkOrder,
    }

    private sealed class UnitDashboardItemReadRow
    {
        public UnitDashboardItemKind Kind { get; init; }
        public int? IntValue1 { get; init; }
        public int? IntValue2 { get; init; }
        public long? LongValue1 { get; init; }
        public string? TextValue1 { get; init; }
        public string? TextValue2 { get; init; }
        public string? TextValue3 { get; init; }
        public string? TextValue4 { get; init; }
        public DateTime? DateValue1 { get; init; }
        public DateTime? DateValue2 { get; init; }
        public decimal? DecimalValue1 { get; init; }
        public int SortRole { get; init; }
        public long SortIdAscending { get; init; }
        public long SortIdDescending { get; init; }
        public DateTime SortDateDescending { get; init; }
        public DateTime SortDateAscending { get; init; }
    }

    private sealed class TimelineReadRow
    {
        public required AtomicAuditLog Audit { get; init; }
        public string? ActorName { get; init; }
    }
}
