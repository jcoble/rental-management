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
        var currentTenants = unitRow.LeaseManagementId is not int leaseManagementId
            ? new List<UnitTenantSummary>()
            : await _db.LeaseManagementParties
                .AsNoTracking()
                .Where(party => party.PortfolioId == portfolioId
                    && party.LeaseManagementId == leaseManagementId
                    && party.EffectiveFrom <= unitRow.BusinessDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= unitRow.BusinessDate)
                    && party.Role != LeaseManagementPartyRole.Guarantor)
                .OrderBy(party => party.Role == LeaseManagementPartyRole.PrimaryTenant ? 0
                    : party.Role == LeaseManagementPartyRole.CoTenant ? 1
                    : party.Role == LeaseManagementPartyRole.Occupant ? 2
                    : 3)
                .ThenBy(party => party.Id)
                .Select(party => new UnitTenantSummary
                {
                    Id = party.TenantId,
                    Name = (party.Tenant!.FirstName + " " + party.Tenant.LastName).Trim(),
                    Email = party.Tenant.Email,
                    Phone = party.Tenant.Phone,
                })
                .ToListAsync(ct);

        var outstanding = unitRow.ReceivableBalance;
        var hasOverdue = unitRow.PastDueAmount > 0m;
        var hasDueSoon = unitRow.NextDueOn is not null;

        // (4) Counts — open work orders + documents on the unit and its children.
        var openWorkOrderCount = await _db.WorkOrders
            .AsNoTracking()
            .CountAsync(w => w.UnitId == unitId && w.PortfolioId == portfolioId
                && w.Status != WorkOrderStatus.Completed
                && w.Status != WorkOrderStatus.Cancelled
                && w.Status != WorkOrderStatus.Archived, ct);

        // The unit's document set: files attached to the unit OR any of its children, computed DB-side as
        // a set of (EntityType, EntityId) predicates against subqueries (one query, IN (...) per child type).
        var docs = await BuildUnitDocumentsQuery(portfolioId, unitId)
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

        var docsCount = await BuildUnitDocumentsQuery(portfolioId, unitId).CountAsync(ct);

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

        // Posted payment receipts use the same immutable ledger-entry ids as the global Tenant Account.
        List<RecentPaymentReadRow> recentPaymentRows = unitRow.TenantAccountId is not int tenantAccountId
            ? []
            : await _db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.TenantAccountId == tenantAccountId
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt)
            .OrderByDescending(entry => entry.PostedAtUtc)
            .ThenByDescending(entry => entry.Id)
            .Take(OverviewTake)
            .Select(entry => new RecentPaymentReadRow
            {
                Id = entry.Id,
                TenantAccountId = entry.TenantAccountId,
                LeaseAgreementId = entry.LeaseAgreementId,
                Type = entry.EntryType.ToString(),
                Amount = entry.Amount,
                EffectiveOn = entry.EffectiveOn,
                PostedAtUtc = entry.PostedAtUtc,
            })
            .ToListAsync(ct);

        var recentPayments = recentPaymentRows.Select(entry => new UnitPaymentSummary
        {
            Id = entry.Id,
            TenantAccountId = entry.TenantAccountId,
            LeaseManagementId = unitRow.LeaseManagementId!.Value,
            LeaseAgreementId = entry.LeaseAgreementId,
            Type = entry.Type,
            Status = "Posted",
            Amount = entry.Amount,
            DueDate = entry.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            PaidDate = entry.PostedAtUtc,
        }).ToList();

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
        var hasDraftOrPendingLease = await _db.LeaseAgreements.AsNoTracking().AnyAsync(agreement =>
            agreement.PortfolioId == portfolioId
            && agreement.LeaseManagement!.UnitId == unitId
            && agreement.FullyExecutedAtUtc == null
            && agreement.VoidedAtUtc == null
            && agreement.DraftCanceledAtUtc == null, ct);

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

        var moveOutInspectionDone = await _db.Inspections.AsNoTracking().AnyAsync(i =>
            i.UnitId == unitId && i.PortfolioId == portfolioId
            && i.Type == InspectionType.MoveOut
            && (i.Status == InspectionStatus.Completed || i.Status == InspectionStatus.Reviewed), ct);

        var recentMoveOutSignal = unitRow.IsInTurnover || unitRow.IsOutOfService || unitRow.IsOnManagementHold
            || openWorkOrderCount > 0
            || unitRow.Lifecycle == "AccountingCloseout"
            || moveOutInspectionDone;

        var stage = ResolveCanonicalStage(unitRow, hasDraftOrPendingLease, hasOpenApplication,
            hasUpcomingShowing, hasUpcomingMoveInAppt, recentMoveOutSignal);
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

        return new UnitDashboardResponse
        {
            Unit = unitRow.Unit,
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
            Turnover = await BuildTurnoverSummaryAsync(portfolioId, unitId, stage, now, ct),
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
        select new UnitDashboardReadRow
        {
            Unit = new UnitResponse
            {
                Id = unit.Id,
                PropertyId = unit.PropertyId,
                UnitNumber = unit.UnitNumber,
                FloorPlan = unit.FloorPlan,
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
        };

    private static UnitLifecycleStage ResolveCanonicalStage(
        UnitDashboardReadRow row,
        bool hasDraftOrPendingAgreement,
        bool hasOpenApplication,
        bool hasUpcomingShowing,
        bool hasUpcomingMoveInAppointment,
        bool recentMoveOutSignal)
    {
        if (row.IsInTurnover || row.IsOutOfService || row.IsOnManagementHold)
        {
            return UnitLifecycleStage.Turnover;
        }

        if (row.IsOccupied && row.Lifecycle == "Ending")
        {
            return UnitLifecycleStage.MoveOut;
        }

        if (row.IsOccupied)
        {
            return row.AgreementEndOn is { } end
                && end >= row.BusinessDate
                && end <= row.BusinessDate.AddDays(90)
                ? UnitLifecycleStage.Renewal
                : UnitLifecycleStage.Active;
        }

        if ((row.HasScheduledMoveIn || hasUpcomingMoveInAppointment)
            && row.AgreementStatus is "Active" or "Upcoming")
        {
            return UnitLifecycleStage.MoveIn;
        }

        if (hasDraftOrPendingAgreement || row.HasScheduledMoveIn || row.Lifecycle is "Upcoming" or "Preparing")
        {
            return UnitLifecycleStage.Lease;
        }

        if (hasOpenApplication)
        {
            return UnitLifecycleStage.Applicant;
        }

        if (recentMoveOutSignal)
        {
            return UnitLifecycleStage.Turnover;
        }

        return hasUpcomingShowing ? UnitLifecycleStage.Listed : UnitLifecycleStage.Ready;
    }

    private static string NextBestActionLabel(
        UnitLifecycleStage stage,
        decimal outstanding,
        DateOnly? agreementEndOn,
        DateOnly businessDate) => stage switch
        {
            UnitLifecycleStage.Ready => "List this unit",
            UnitLifecycleStage.Listed => "Review applicants / schedule showing",
            UnitLifecycleStage.Applicant => "Screen & decide on the applicant",
            UnitLifecycleStage.Lease => "Finish and send the agreement",
            UnitLifecycleStage.MoveIn => "Confirm possession / collect deposit",
            UnitLifecycleStage.Active when outstanding > 0m => $"Collect {outstanding:C}",
            UnitLifecycleStage.Active => "Rent on track",
            UnitLifecycleStage.Renewal when agreementEndOn is { } end =>
                $"Prepare renewal — agreement ends in {Math.Max(0, end.DayNumber - businessDate.DayNumber)} days",
            UnitLifecycleStage.Renewal => "Prepare renewal",
            UnitLifecycleStage.MoveOut => "Schedule move-out inspection",
            UnitLifecycleStage.Turnover => "Track make-ready / mark rent-ready",
            _ => "Open unit",
        };

    private async Task<UnitTurnoverSummary> BuildTurnoverSummaryAsync(
        int portfolioId,
        int unitId,
        UnitLifecycleStage lifecycleStage,
        DateTime now,
        CancellationToken ct)
    {
        var workOrderIds = _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId && w.UnitId == unitId)
            .Select(w => w.Id);

        var workOrderAggregate = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId && w.UnitId == unitId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalTaskCount = g.Count(),
                OpenTaskCount = g.Count(w =>
                    w.Status != WorkOrderStatus.Completed
                    && w.Status != WorkOrderStatus.Cancelled
                    && w.Status != WorkOrderStatus.Archived),
                CompletedTaskCount = g.Count(w => w.Status == WorkOrderStatus.Completed),
                EstimatedCost = g.Sum(w => w.EstimatedCost ?? 0m),
                WorkOrderActualCost = g.Sum(w => w.ActualCost ?? 0m),
                StartedAt = g.Min(w => (DateTime?)w.RequestedAt),
                TargetReadyDate = g.Max(w =>
                    w.Status != WorkOrderStatus.Completed
                    && w.Status != WorkOrderStatus.Cancelled
                    && w.Status != WorkOrderStatus.Archived
                        ? (w.ScheduledWindowEnd ?? w.ScheduledFor)
                        : null),
                LastWorkOrderActivityAt = g.Max(w => (DateTime?)(w.CompletedAt ?? w.UpdatedAt)),
            })
            .FirstOrDefaultAsync(ct);

        var expenseAggregate = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId
                && (e.UnitId == unitId || (e.WorkOrderId != null && workOrderIds.Contains(e.WorkOrderId.Value))))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ReceiptCount = g.Count(),
                ReceiptCost = g.Sum(e => e.Amount),
                LastReceiptAt = g.Max(e => (DateTime?)(e.PaidAt ?? e.IncurredAt)),
            })
            .FirstOrDefaultAsync(ct);

        var totalTasks = workOrderAggregate?.TotalTaskCount ?? 0;
        var openTasks = workOrderAggregate?.OpenTaskCount ?? 0;
        var completedTasks = workOrderAggregate?.CompletedTaskCount ?? 0;
        var startedAt = workOrderAggregate?.StartedAt;
        var lastActivityAt = MaxDate(workOrderAggregate?.LastWorkOrderActivityAt, expenseAggregate?.LastReceiptAt);

        return new UnitTurnoverSummary
        {
            Status = TurnoverStatus(lifecycleStage, totalTasks, openTasks),
            TotalTaskCount = totalTasks,
            OpenTaskCount = openTasks,
            CompletedTaskCount = completedTasks,
            ReceiptCount = expenseAggregate?.ReceiptCount ?? 0,
            EstimatedCost = workOrderAggregate?.EstimatedCost ?? 0m,
            ActualCost = (workOrderAggregate?.WorkOrderActualCost ?? 0m) + (expenseAggregate?.ReceiptCost ?? 0m),
            StartedAt = startedAt,
            TargetReadyDate = workOrderAggregate?.TargetReadyDate,
            LastActivityAt = lastActivityAt,
            DaysInTurnover = startedAt is null
                ? null
                : Math.Max(0, (int)Math.Ceiling(((openTasks > 0 ? now : lastActivityAt ?? now) - startedAt.Value).TotalDays)),
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

        // ONE audit query with translated OR/IN subqueries, newest first, paged.
        var rows = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId && (
                (a.EntityType == "Unit" && a.EntityId == unitId) ||
                (a.EntityType == nameof(LeaseManagement) && relationshipIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(LeaseAgreement) && agreementIds.Contains(a.EntityId)) ||
                (a.EntityType == nameof(TenantAccount) && accountIds.Contains(a.EntityId)) ||
                (a.EntityType == "WorkOrder" && workOrderIds.Contains(a.EntityId)) ||
                (a.EntityType == "Inspection" && inspectionIds.Contains(a.EntityId)) ||
                (a.EntityType == "Appointment" && appointmentIds.Contains(a.EntityId)) ||
                (a.EntityType == "Expense" && expenseIds.Contains(a.EntityId))))
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .Skip(skip)
            .Take(take)
            .Select(a => new TimelineReadRow
            {
                Audit = a,
                ActorName = a.ActorLabel != null && a.ActorLabel != ""
                    ? a.ActorLabel
                    : a.User != null && a.User.DisplayName != null && a.User.DisplayName != ""
                        ? a.User.DisplayName
                        : a.User != null ? a.User.Email : null,
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
    private static string NextBestActionHref(UnitLifecycleStage stage, int unitId, int propertyId, int? tenantId) => stage switch
    {
        UnitLifecycleStage.Ready => $"/units/{unitId}?tab=listing",
        UnitLifecycleStage.Listed => $"/units/{unitId}?tab=overview",
        UnitLifecycleStage.Applicant => $"/units/{unitId}?tab=overview",
        UnitLifecycleStage.Lease => $"/units/{unitId}?tab=lease",
        UnitLifecycleStage.MoveIn => $"/units/{unitId}?tab=lease&action=confirm-move-in",
        UnitLifecycleStage.Active => $"/units/{unitId}?tab=ledger&ledger=rent",
        UnitLifecycleStage.Renewal when tenantId is int id => $"/tenants/{id}?action=create-notice&noticeType=lease-renewal-offer",
        UnitLifecycleStage.Renewal => $"/units/{unitId}?tab=lease",
        UnitLifecycleStage.MoveOut => $"/units/{unitId}?tab=turnover",
        UnitLifecycleStage.Turnover => $"/units/{unitId}?tab=turnover",
        _ => $"/units/{unitId}",
    };

    private static string TurnoverStatus(UnitLifecycleStage lifecycleStage, int totalTasks, int openTasks)
    {
        if (openTasks > 0)
        {
            return lifecycleStage == UnitLifecycleStage.MoveOut ? "MoveOut" : "InProgress";
        }

        if (totalTasks > 0)
        {
            return "RentReady";
        }

        return lifecycleStage is UnitLifecycleStage.MoveOut or UnitLifecycleStage.Turnover
            ? "AwaitingVacancy"
            : "NotStarted";
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
    }

    private sealed class RecentPaymentReadRow
    {
        public long Id { get; init; }
        public int TenantAccountId { get; init; }
        public int? LeaseAgreementId { get; init; }
        public string Type { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public DateOnly EffectiveOn { get; init; }
        public DateTime PostedAtUtc { get; init; }
    }

    private sealed class TimelineReadRow
    {
        public required AuditLog Audit { get; init; }
        public string? ActorName { get; init; }
    }
}
