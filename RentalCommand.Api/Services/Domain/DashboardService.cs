using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Reporting;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDashboardService"/>
public class DashboardService : IDashboardService
{
    private readonly RentalCommandDbContext _db;
    private readonly AuditDescriber _auditDescriber;
    private readonly TimeProvider _timeProvider;

    public DashboardService(RentalCommandDbContext db, AuditDescriber auditDescriber, TimeProvider timeProvider)
    {
        _db = db;
        _auditDescriber = auditDescriber;
        _timeProvider = timeProvider;
    }

    public async Task<DashboardResponse?> GetDashboardAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var portfolio = await _db.Portfolios
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);
        if (portfolio == null)
        {
            return null;
        }

        var now = _timeProvider.UtcNow();
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStart = monthStart.AddMonths(1);

        return new DashboardResponse
        {
            Portfolio = new DashboardPortfolio
            {
                Id = portfolio.Id,
                Name = portfolio.Name,
                ManagementCompanyName = string.IsNullOrWhiteSpace(portfolio.ManagementCompanyName)
                    ? "Rental Command"
                    : portfolio.ManagementCompanyName,
                TimeZone = string.IsNullOrWhiteSpace(portfolio.TimeZone)
                    ? "America/New_York"
                    : portfolio.TimeZone,
                Status = portfolio.Status.ToString(),
            },
            Occupancy = await BuildOccupancyAsync(scope, ct),
            Accounting = await BuildAccountingAsync(scope, monthStart, nextMonthStart, ct),
            Maintenance = await BuildMaintenanceAsync(scope, ct),
            Leasing = await BuildLeasingAsync(scope, ct),
            RecentActivity = await BuildRecentActivityAsync(scope, ct),
            UpcomingAppointments = await BuildUpcomingAppointmentsAsync(scope, now, ct),
        };
    }

    private async Task<DashboardOccupancy> BuildOccupancyAsync(
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        // The database projection is the single source of occupancy truth. Possession and operational
        // periods determine these counts; mutable Unit.Status and legacy Lease.Status are not consulted.
        var counts = await _db.UnitOccupancyProjections
            .AsNoTracking()
            .Where(row => row.PortfolioId == scope.PortfolioId &&
                authorizedProperties.Any(property => property.Id == row.PropertyId))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Occupied = g.Count(row => row.IsOccupied),
                Reserved = g.Count(row => !row.IsOccupied && row.HasScheduledMoveIn),
            })
            .FirstOrDefaultAsync(ct);

        var totalUnits = counts?.Total ?? 0;
        var occupiedUnits = counts?.Occupied ?? 0;
        var reservedUnits = counts?.Reserved ?? 0;
        var vacantUnits = totalUnits - occupiedUnits - reservedUnits;

        var occupancyRate = totalUnits == 0
            ? 0
            : Math.Round((double)occupiedUnits / totalUnits * 100, 1);

        return new DashboardOccupancy
        {
            TotalUnits = totalUnits,
            OccupiedUnits = occupiedUnits,
            VacantUnits = vacantUnits,
            ReservedUnits = reservedUnits,
            OccupancyRate = occupancyRate,
        };
    }

    private async Task<DashboardAccounting> BuildAccountingAsync(
        WorkspaceReadScope scope,
        DateTime monthStart,
        DateTime nextMonthStart,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead);
        var monthStartOn = DateOnly.FromDateTime(monthStart);
        var nextMonthStartOn = DateOnly.FromDateTime(nextMonthStart);

        // One PostgreSQL statement derives the current receivable portion of the dashboard from
        // canonical facts. Billed charges and allocations are immutable; partial payment is reflected
        // by the balance views; and lifecycle scope determines which current accounts need attention.
        var tenantMoney = await _db.Portfolios
            .AsNoTracking()
            .Where(portfolio => portfolio.Id == portfolioId)
            .Select(portfolio => new
            {
                Overdue = (
                    from balance in _db.TenantAccountBalanceProjections.AsNoTracking()
                    join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                        on new { balance.PortfolioId, balance.LeaseManagementId }
                        equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                    where balance.PortfolioId == portfolio.Id
                        && authorizedProperties.Any(property => property.Id == lifecycle.PropertyId)
                        && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                    select (decimal?)balance.PastDueAmount).Sum() ?? 0m,
                DueThisMonth = (
                    from charge in _db.TenantChargeBalanceProjections.AsNoTracking()
                    join account in _db.TenantAccounts.AsNoTracking()
                        on new { charge.PortfolioId, charge.TenantAccountId }
                        equals new { account.PortfolioId, TenantAccountId = account.Id }
                    join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                        on new { account.PortfolioId, account.LeaseManagementId }
                        equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                    where charge.PortfolioId == portfolio.Id
                        && authorizedProperties.Any(property => property.Id == lifecycle.PropertyId)
                        && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                        && charge.DueOn >= monthStartOn
                        && charge.DueOn < nextMonthStartOn
                    select (decimal?)(charge.OriginalAmount - charge.ReversedAmount)).Sum() ?? 0m,
            })
            .SingleAsync(ct);

        var cashFlowTo = nextMonthStart.AddTicks(-1);
        var accountingIncomeQuery = FinancialReportProjections.BuildAuthorizedCashFlowIncomeProjection(
            _db,
            scope,
            CapabilityKeys.MoneyBalancesRead,
            utcNow,
            monthStart,
            cashFlowTo,
            []);
        var accountingExpenseQuery = FinancialReportProjections.BuildAuthorizedCashFlowExpenseProjection(
            _db,
            scope,
            CapabilityKeys.MoneyBalancesRead,
            utcNow,
            monthStart,
            cashFlowTo,
            []);
        var accountingAnchor = FinancialReportProjections.BuildAuthorizedCashFlowAnchor(
            _db,
            scope,
            CapabilityKeys.MoneyBalancesRead,
            utcNow,
            []);
        var cashFlowTotals = await accountingAnchor
            .Select(_ => new
            {
                Income = accountingIncomeQuery.Sum(row => (decimal?)row.Amount) ?? 0m,
                Expense = accountingExpenseQuery.Sum(expense => (decimal?)expense.Amount) ?? 0m,
            })
            .SingleOrDefaultAsync(ct);

        var overdue = tenantMoney.Overdue;
        var dueThisMonth = tenantMoney.DueThisMonth;
        var paidThisMonth = cashFlowTotals?.Income ?? 0m;
        var expensesThisMonth = cashFlowTotals?.Expense ?? 0m;

        return new DashboardAccounting
        {
            DueThisMonthAmount = dueThisMonth,
            PaidThisMonthAmount = paidThisMonth,
            OverdueAmount = overdue,
            ExpensesThisMonthAmount = expensesThisMonth,
            NetThisMonth = paidThisMonth - expensesThisMonth,
        };
    }

    private async Task<DashboardMaintenance> BuildMaintenanceAsync(
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.WorkRead);
        // "Open" = any work order that is not Completed/Cancelled/Archived. The three counts (open,
        // emergency-among-open, in-progress-among-open) are computed SQL-side: the open predicate scopes
        // the query, then a single grouped aggregate emits the conditional counts. No work-order rows are
        // pulled into memory.
        var counts = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == scope.PortfolioId
                && authorizedProperties.Any(property => property.Id == w.PropertyId)
                && w.Status != WorkOrderStatus.Completed
                && w.Status != WorkOrderStatus.Cancelled
                && w.Status != WorkOrderStatus.Archived)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Open = g.Count(),
                Emergency = g.Count(w => w.Priority == WorkOrderPriority.Emergency),
                InProgress = g.Count(w => w.Status == WorkOrderStatus.InProgress),
            })
            .FirstOrDefaultAsync(ct);

        return new DashboardMaintenance
        {
            OpenCount = counts?.Open ?? 0,
            EmergencyCount = counts?.Emergency ?? 0,
            InProgressCount = counts?.InProgress ?? 0,
        };
    }

    private async Task<DashboardLeasing> BuildLeasingAsync(
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        var byStatusRaw = await _db.LeaseManagementLifecycleProjections
            .AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId &&
                authorizedProperties.Any(property => property.Id == row.PropertyId))
            .GroupBy(row => row.Lifecycle)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        var byStatus = byStatusRaw.ToDictionary(group => group.Status, group => group.Count);
        var leaseCounts = await _db.LeaseManagementLifecycleProjections
            .AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId &&
                authorizedProperties.Any(property => property.Id == row.PropertyId))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Total = group.Count(),
                Active = group.Count(row => row.Lifecycle == "Occupied" || row.Lifecycle == "Ending"),
            })
            .SingleOrDefaultAsync(ct);

        var totalLeases = leaseCounts?.Total ?? 0;
        var activeLeases = leaseCounts?.Active ?? 0;

        var expiringRows = await (
            from lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            where lifecycle.PortfolioId == portfolioId
                && authorizedProperties.Any(authorized => authorized.Id == lifecycle.PropertyId)
                && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                && lifecycle.CurrentAgreementId != null
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.CurrentAgreementId }
                equals new { agreement.PortfolioId, Id = (int?)agreement.Id }
            join property in _db.Properties.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.PropertyId }
                equals new { property.PortfolioId, property.Id }
            join unit in _db.Units.AsNoTracking()
                on new { lifecycle.PortfolioId, Id = lifecycle.UnitId }
                equals new { unit.PortfolioId, unit.Id }
            where agreement.TermEndOn != null
                && agreement.TermEndOn >= lifecycle.BusinessDate
                && agreement.TermEndOn <= lifecycle.BusinessDate.AddDays(60)
            orderby agreement.TermEndOn, agreement.Id
            select new ExpiringAgreementReadRow
            {
                Id = agreement.Id,
                AgreementNumber = agreement.AgreementNumber,
                Tenant = lifecycle.CurrentPrimaryTenantName,
                Property = property.Name,
                Unit = unit.UnitNumber,
                EndOn = agreement.TermEndOn.GetValueOrDefault(),
                BaseRentAmount = agreement.BaseRentAmount,
            }).ToListAsync(ct);

        var expiring = expiringRows.Select(row => new DashboardExpiringLease
        {
            Id = row.Id,
            LeaseNumber = string.IsNullOrWhiteSpace(row.AgreementNumber)
                ? $"Agreement #{row.Id}"
                : row.AgreementNumber,
            Tenant = row.Tenant,
            Property = row.Property,
            Unit = row.Unit,
            EndDate = row.EndOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            MonthlyRent = row.BaseRentAmount,
        }).ToList();

        return new DashboardLeasing
        {
            TotalLeases = totalLeases,
            ActiveLeases = activeLeases,
            ExpiringSoon = expiring,
            ByStatus = byStatus,
        };
    }

    private sealed class ExpiringAgreementReadRow
    {
        public int Id { get; init; }
        public string AgreementNumber { get; init; } = string.Empty;
        public string? Tenant { get; init; }
        public string Property { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public DateOnly EndOn { get; init; }
        public decimal BaseRentAmount { get; init; }
    }

    private async Task<IReadOnlyList<DashboardActivity>> BuildRecentActivityAsync(
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        // Authorization, supported entity-path filtering, stable ordering, the 10-row limit, actor
        // resolution, label resolution, and Unit context are all part of this one translated reader
        // statement. Only enum/string presentation remains after materialization.
        var rows = await BuildRecentActivityProjectionQuery(scope).ToListAsync(ct);

        return rows
            .Select(row => new DashboardActivity
            {
                Id = row.Audit.Id,
                Type = row.Audit.EntityType,
                EntityId = row.Audit.EntityId,
                UnitId = row.UnitId,
                Action = row.Audit.Operation.ToString(),
                Description = _auditDescriber.Describe(row.Audit),
                Label = string.IsNullOrWhiteSpace(row.Label) ? null : row.Label.Trim(),
                Actor = AuditEntryResponse.ResolveActor(row.Audit, row.ResolvedActorName),
                CreatedAt = row.Audit.Timestamp,
            })
            .ToList();
    }

    /// <summary>
    /// One SQL projection for the scoped recent-activity widget. Exposed internally so translation and
    /// SQL-shape tests can prove that authorization happens before ordering/paging and that no follow-up
    /// label query exists.
    /// </summary>
    internal IQueryable<DashboardActivityReadRow> BuildRecentActivityProjectionQuery(WorkspaceReadScope scope)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.ReportsRead);

        return _db.AtomicAuditLogs
            .AsNoTracking()
            .WhereAuthorizedForReports(
                _db,
                scope,
                _timeProvider.GetUtcNow().UtcDateTime)
            .OrderByDescending(audit => audit.Timestamp)
            .ThenByDescending(audit => audit.Id)
            .Take(10)
            .Select(audit => new DashboardActivityReadRow
            {
                Audit = audit,
                ResolvedActorName = audit.ActorLabel != null && audit.ActorLabel != ""
                    ? audit.ActorLabel
                    : audit.UserId != null
                        ? _db.Users
                            .Where(user => user.Id == audit.UserId.Value &&
                                user.WorkspaceAccessContexts.Any(context =>
                                    context.PortfolioId == portfolioId))
                            .Select(user => user.DisplayName != null && user.DisplayName != ""
                                ? user.DisplayName
                                : user.Email)
                            .FirstOrDefault()
                        : null,
                Label = audit.EntityType == nameof(Property)
                    ? _db.Properties.Where(property =>
                            property.PortfolioId == portfolioId && property.Id == audit.EntityId)
                        .Select(property => property.Name).FirstOrDefault()
                    : audit.EntityType == nameof(Unit)
                        ? _db.Units.Where(unit =>
                                unit.PortfolioId == portfolioId && unit.Id == audit.EntityId)
                            .Select(unit => unit.Property!.Name + " · Unit " + unit.UnitNumber).FirstOrDefault()
                        : audit.EntityType == nameof(Tenant)
                            ? _db.Tenants.Where(tenant =>
                                    tenant.PortfolioId == portfolioId && tenant.Id == audit.EntityId)
                                .Select(tenant => tenant.FirstName + " " + tenant.LastName).FirstOrDefault()
                            : audit.EntityType == nameof(LeaseManagement)
                                ? _db.LeaseManagements.Where(management =>
                                        management.PortfolioId == portfolioId && management.Id == audit.EntityId)
                                    .Select(management => management.RelationshipNumber).FirstOrDefault()
                                : audit.EntityType == nameof(LeaseAgreement)
                                    ? _db.LeaseAgreements.Where(agreement =>
                                            agreement.PortfolioId == portfolioId && agreement.Id == audit.EntityId)
                                        .Select(agreement => agreement.AgreementNumber).FirstOrDefault()
                                    : audit.EntityType == nameof(LeaseAddendum)
                                        ? _db.LeaseAddenda.Where(addendum =>
                                                addendum.PortfolioId == portfolioId && addendum.Id == audit.EntityId)
                                            .Select(addendum => addendum.AddendumNumber).FirstOrDefault()
                                        : audit.EntityType == nameof(TenantAccount)
                                            ? _db.TenantAccounts.Where(account =>
                                                    account.PortfolioId == portfolioId && account.Id == audit.EntityId)
                                                .Select(account => account.AccountNumber).FirstOrDefault()
                                            : audit.EntityType == nameof(TenantLedgerEntry)
                                                ? _db.TenantLedgerEntries.Where(entry =>
                                                        entry.PortfolioId == portfolioId && entry.Id == audit.EntityId)
                                                    .Select(entry => entry.Description).FirstOrDefault()
                                                : audit.EntityType == nameof(SecurityDepositAccount) || audit.EntityType == "SecurityDeposit"
                                                    ? _db.SecurityDepositAccounts.Where(deposit =>
                                                            deposit.PortfolioId == portfolioId && deposit.Id == audit.EntityId)
                                                        .Select(deposit => deposit.TenantAccount!.AccountNumber + " deposit")
                                                        .FirstOrDefault()
                                                    : audit.EntityType == nameof(SecurityDepositEntry)
                                                        ? _db.SecurityDepositEntries.Where(entry =>
                                                                entry.PortfolioId == portfolioId && entry.Id == audit.EntityId)
                                                            .Select(entry => entry.Description).FirstOrDefault()
                                                        : audit.EntityType == nameof(WorkOrder)
                                                            ? _db.WorkOrders.Where(workOrder =>
                                                                    workOrder.PortfolioId == portfolioId && workOrder.Id == audit.EntityId)
                                                                .Select(workOrder => workOrder.Title).FirstOrDefault()
                                                            : audit.EntityType == nameof(Expense)
                                                                ? _db.Expenses.Where(expense =>
                                                                        expense.PortfolioId == portfolioId && expense.Id == audit.EntityId)
                                                                    .Select(expense => expense.Description).FirstOrDefault()
                                                                : audit.EntityType == nameof(Appointment)
                                                                    ? _db.Appointments.Where(appointment =>
                                                                            appointment.PortfolioId == portfolioId && appointment.Id == audit.EntityId)
                                                                        .Select(appointment => appointment.Title).FirstOrDefault()
                                                                    : audit.EntityType == nameof(Inspection)
                                                                        ? _db.Inspections.Where(inspection =>
                                                                                inspection.PortfolioId == portfolioId && inspection.Id == audit.EntityId)
                                                                            .Select(inspection => inspection.Property!.Name).FirstOrDefault()
                                                                        : audit.EntityType == nameof(RentalApplication)
                                                                            ? _db.RentalApplications.Where(application =>
                                                                                    application.PortfolioId == portfolioId && application.Id == audit.EntityId)
                                                                                .Select(application => application.FirstName + " " + application.LastName)
                                                                                .FirstOrDefault()
                                                                            : null,
                UnitId = audit.EntityType == nameof(Unit)
                    ? (int?)audit.EntityId
                    : audit.EntityType == nameof(Tenant)
                        ? _db.LeaseManagementParties
                            .Where(party => party.PortfolioId == portfolioId &&
                                party.TenantId == audit.EntityId &&
                                authorizedProperties.Any(authorized =>
                                    authorized.Id == party.LeaseManagement!.PropertyId))
                            .OrderByDescending(party => party.EffectiveFrom)
                            .Select(party => (int?)party.LeaseManagement!.UnitId)
                            .FirstOrDefault()
                        : audit.EntityType == nameof(LeaseManagement)
                            ? _db.LeaseManagements.Where(management =>
                                    management.PortfolioId == portfolioId && management.Id == audit.EntityId)
                                .Select(management => (int?)management.UnitId).FirstOrDefault()
                            : audit.EntityType == nameof(LeaseAgreement)
                                ? _db.LeaseAgreements.Where(agreement =>
                                        agreement.PortfolioId == portfolioId && agreement.Id == audit.EntityId)
                                    .Select(agreement => (int?)agreement.LeaseManagement!.UnitId).FirstOrDefault()
                                : audit.EntityType == nameof(LeaseAddendum)
                                    ? _db.LeaseAddenda.Where(addendum =>
                                            addendum.PortfolioId == portfolioId && addendum.Id == audit.EntityId)
                                        .Select(addendum => (int?)addendum.LeaseManagement!.UnitId).FirstOrDefault()
                                    : audit.EntityType == nameof(TenantAccount)
                                        ? _db.TenantAccounts.Where(account =>
                                                account.PortfolioId == portfolioId && account.Id == audit.EntityId)
                                            .Select(account => (int?)account.LeaseManagement!.UnitId).FirstOrDefault()
                                        : audit.EntityType == nameof(TenantLedgerEntry)
                                            ? _db.TenantLedgerEntries.Where(entry =>
                                                    entry.PortfolioId == portfolioId && entry.Id == audit.EntityId)
                                                .Select(entry => (int?)entry.TenantAccount!.LeaseManagement!.UnitId).FirstOrDefault()
                                            : audit.EntityType == nameof(SecurityDepositAccount) || audit.EntityType == "SecurityDeposit"
                                                ? _db.SecurityDepositAccounts.Where(deposit =>
                                                        deposit.PortfolioId == portfolioId && deposit.Id == audit.EntityId)
                                                    .Select(deposit => (int?)deposit.TenantAccount!.LeaseManagement!.UnitId).FirstOrDefault()
                                                : audit.EntityType == nameof(SecurityDepositEntry)
                                                    ? _db.SecurityDepositEntries.Where(entry =>
                                                            entry.PortfolioId == portfolioId && entry.Id == audit.EntityId)
                                                        .Select(entry => (int?)entry.SecurityDepositAccount!.TenantAccount!.LeaseManagement!.UnitId)
                                                        .FirstOrDefault()
                                                    : audit.EntityType == nameof(WorkOrder)
                                                        ? _db.WorkOrders.Where(workOrder =>
                                                                workOrder.PortfolioId == portfolioId && workOrder.Id == audit.EntityId)
                                                            .Select(workOrder => workOrder.UnitId).FirstOrDefault()
                                                        : audit.EntityType == nameof(Expense)
                                                            ? _db.Expenses.Where(expense =>
                                                                    expense.PortfolioId == portfolioId && expense.Id == audit.EntityId)
                                                                .Select(expense => expense.UnitId ??
                                                                    (expense.WorkOrder != null ? expense.WorkOrder.UnitId : null))
                                                                .FirstOrDefault()
                                                            : audit.EntityType == nameof(Appointment)
                                                                ? _db.Appointments.Where(appointment =>
                                                                        appointment.PortfolioId == portfolioId && appointment.Id == audit.EntityId)
                                                                    .Select(appointment => appointment.UnitId).FirstOrDefault()
                                                                : audit.EntityType == nameof(Inspection)
                                                                    ? _db.Inspections.Where(inspection =>
                                                                            inspection.PortfolioId == portfolioId && inspection.Id == audit.EntityId)
                                                                        .Select(inspection => inspection.UnitId).FirstOrDefault()
                                                                    : audit.EntityType == nameof(RentalApplication)
                                                                        ? _db.RentalApplications.Where(application =>
                                                                                application.PortfolioId == portfolioId && application.Id == audit.EntityId)
                                                                            .Select(application => (int?)application.UnitId).FirstOrDefault()
                                                                        : null,
            });
    }

    internal sealed class DashboardActivityReadRow
    {
        public AtomicAuditLog Audit { get; set; } = null!;
        public string? ResolvedActorName { get; set; }
        public string? Label { get; set; }
        public int? UnitId { get; set; }
    }

    private async Task<IReadOnlyList<DashboardAppointment>> BuildUpcomingAppointmentsAsync(
        WorkspaceReadScope scope,
        DateTime now,
        CancellationToken ct)
    {
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        var appointments = await _db.Appointments
            .AsNoTracking()
            .Where(a => a.PortfolioId == scope.PortfolioId
                && a.PropertyId != null
                && authorizedProperties.Any(property => property.Id == a.PropertyId.Value)
                && a.ScheduledStart >= now
                && a.Status != AppointmentStatus.Cancelled)
            .OrderBy(a => a.ScheduledStart)
            .Take(10)
            .Select(a => new
            {
                a.Id,
                a.Title,
                a.Type,
                a.Status,
                a.ScheduledStart,
                a.AssignedTo,
                a.PropertyId,
                a.UnitId,
            })
            .ToListAsync(ct);

        return appointments
            .Select(a => new DashboardAppointment
            {
                Id = a.Id,
                Title = a.Title,
                Type = a.Type.ToString(),
                Status = a.Status.ToString(),
                ScheduledStart = a.ScheduledStart,
                AssignedTo = a.AssignedTo,
                PropertyId = a.PropertyId,
                UnitId = a.UnitId,
            })
            .ToList();
    }

    private IQueryable<Property> AuthorizedProperties(
        WorkspaceReadScope scope,
        string capabilityKey) =>
        _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                capabilityKey,
                _timeProvider.GetUtcNow().UtcDateTime);
}
