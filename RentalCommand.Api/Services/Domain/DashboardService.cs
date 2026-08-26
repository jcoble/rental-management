using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
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
        var now = _timeProvider.UtcNow();
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStart = monthStart.AddMonths(1);

        // Portfolio KPIs, expiring agreements, and upcoming appointments share one translated reader
        // statement. Each branch keeps its own authorization, ordering, and cap in SQL; materialization
        // only demultiplexes the tagged rows into the existing response sections.
        var initialRows = await BuildDashboardInitialReadQuery(scope, now).ToListAsync(ct);
        var headerAndKpis = new List<DashboardHeaderKpiReadRow>();
        var expiringSoon = new List<DashboardExpiringLease>();
        var upcomingAppointments = new List<DashboardAppointment>();
        foreach (var row in initialRows)
        {
            switch (row.RowKind)
            {
                case DashboardInitialReadKind.HeaderKpi:
                    headerAndKpis.Add(new DashboardHeaderKpiReadRow
                    {
                        PortfolioId = row.PortfolioId,
                        PortfolioName = row.PortfolioName,
                        ManagementCompanyName = row.ManagementCompanyName,
                        TimeZone = row.TimeZone,
                        PortfolioStatus = row.PortfolioStatus,
                        OccupancyTotal = row.OccupancyTotal,
                        OccupiedUnits = row.OccupiedUnits,
                        ReservedUnits = row.ReservedUnits,
                        MaintenanceOpen = row.MaintenanceOpen,
                        MaintenanceEmergency = row.MaintenanceEmergency,
                        MaintenanceInProgress = row.MaintenanceInProgress,
                        TotalLeases = row.TotalLeases,
                        ActiveLeases = row.ActiveLeases,
                        Lifecycle = row.Lifecycle,
                        LifecycleCount = row.LifecycleCount,
                    });
                    break;
                case DashboardInitialReadKind.ExpiringLease:
                    expiringSoon.Add(new DashboardExpiringLease
                    {
                        Id = row.ExpiringId!.Value,
                        LeaseNumber = string.IsNullOrWhiteSpace(row.ExpiringAgreementNumber)
                            ? $"Agreement #{row.ExpiringId.Value}"
                            : row.ExpiringAgreementNumber,
                        Tenant = row.ExpiringTenant,
                        Property = row.ExpiringProperty ?? string.Empty,
                        Unit = row.ExpiringUnit ?? string.Empty,
                        EndDate = row.ExpiringEndOn!.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                        MonthlyRent = row.ExpiringBaseRentAmount ?? 0m,
                    });
                    break;
                case DashboardInitialReadKind.UpcomingAppointment:
                    upcomingAppointments.Add(new DashboardAppointment
                    {
                        Id = row.AppointmentId!.Value,
                        Title = row.AppointmentTitle ?? string.Empty,
                        Type = row.AppointmentType ?? string.Empty,
                        Status = row.AppointmentStatus ?? string.Empty,
                        ScheduledStart = row.AppointmentScheduledStart!.Value,
                        AssignedTo = row.AppointmentAssignedTo,
                        PropertyId = row.AppointmentPropertyId,
                        UnitId = row.AppointmentUnitId,
                    });
                    break;
            }
        }
        var portfolio = headerAndKpis.FirstOrDefault(row => row.PortfolioName != null);
        if (portfolio == null)
        {
            return null;
        }

        var totalUnits = headerAndKpis
            .Where(row => row.OccupancyTotal.HasValue)
            .Select(row => row.OccupancyTotal!.Value)
            .FirstOrDefault();
        var occupiedUnits = headerAndKpis
            .Where(row => row.OccupiedUnits.HasValue)
            .Select(row => row.OccupiedUnits!.Value)
            .FirstOrDefault();
        var reservedUnits = headerAndKpis
            .Where(row => row.ReservedUnits.HasValue)
            .Select(row => row.ReservedUnits!.Value)
            .FirstOrDefault();
        var maintenance = headerAndKpis.FirstOrDefault(row => row.MaintenanceOpen.HasValue);
        var leasingCounts = headerAndKpis.FirstOrDefault(row => row.TotalLeases.HasValue);
        var byStatus = headerAndKpis
            .Where(row => row.Lifecycle != null)
            .ToDictionary(row => row.Lifecycle!, row => row.LifecycleCount!.Value);
        var vacantUnits = totalUnits - occupiedUnits - reservedUnits;

        return new DashboardResponse
        {
            Portfolio = new DashboardPortfolio
            {
                Id = portfolio.PortfolioId!.Value,
                Name = portfolio.PortfolioName!,
                ManagementCompanyName = string.IsNullOrWhiteSpace(portfolio.ManagementCompanyName)
                    ? "Rental Command"
                    : portfolio.ManagementCompanyName,
                TimeZone = string.IsNullOrWhiteSpace(portfolio.TimeZone)
                    ? "America/New_York"
                    : portfolio.TimeZone,
                Status = ((PortfolioStatus)portfolio.PortfolioStatus!.Value).ToString(),
            },
            Occupancy = new DashboardOccupancy
            {
                TotalUnits = totalUnits,
                OccupiedUnits = occupiedUnits,
                VacantUnits = vacantUnits,
                ReservedUnits = reservedUnits,
                OccupancyRate = totalUnits == 0
                    ? 0
                    : Math.Round((double)occupiedUnits / totalUnits * 100, 1),
            },
            Maintenance = new DashboardMaintenance
            {
                OpenCount = maintenance?.MaintenanceOpen ?? 0,
                EmergencyCount = maintenance?.MaintenanceEmergency ?? 0,
                InProgressCount = maintenance?.MaintenanceInProgress ?? 0,
            },
            Leasing = new DashboardLeasing
            {
                TotalLeases = leasingCounts?.TotalLeases ?? 0,
                ActiveLeases = leasingCounts?.ActiveLeases ?? 0,
                ExpiringSoon = expiringSoon,
                ByStatus = byStatus,
            },
            Accounting = await BuildAccountingAsync(scope, monthStart, nextMonthStart, ct),
            RecentActivity = await BuildRecentActivityAsync(scope, ct),
            UpcomingAppointments = upcomingAppointments,
        };
    }

    private IQueryable<DashboardHeaderKpiReadRow> BuildDashboardHeaderAndKpiQuery(
        WorkspaceReadScope scope)
    {
        var portfolioRows = _db.Portfolios
            .AsNoTracking()
            .Where(portfolio => portfolio.Id == scope.PortfolioId)
            .Select(portfolio => new DashboardHeaderKpiReadRow
            {
                PortfolioId = portfolio.Id,
                PortfolioName = portfolio.Name,
                ManagementCompanyName = portfolio.ManagementCompanyName,
                TimeZone = portfolio.TimeZone,
                PortfolioStatus = (int?)portfolio.Status,
                OccupancyTotal = null,
                OccupiedUnits = null,
                ReservedUnits = null,
                MaintenanceOpen = null,
                MaintenanceEmergency = null,
                MaintenanceInProgress = null,
                TotalLeases = null,
                ActiveLeases = null,
                Lifecycle = null,
                LifecycleCount = null,
            });

        var occupancyProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        // The database projection is the single source of occupancy truth. Possession and operational
        // periods determine these counts; mutable Unit.Status and legacy Lease.Status are not consulted.
        var occupancyRows = _db.UnitOccupancyProjections
            .AsNoTracking()
            .Where(row => row.PortfolioId == scope.PortfolioId &&
                occupancyProperties.Any(property => property.Id == row.PropertyId))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Occupied = g.Count(row => row.IsOccupied),
                Reserved = g.Count(row => !row.IsOccupied && row.HasScheduledMoveIn),
            })
            .Select(row => new DashboardHeaderKpiReadRow
            {
                PortfolioId = scope.PortfolioId,
                PortfolioName = null,
                ManagementCompanyName = null,
                TimeZone = null,
                PortfolioStatus = null,
                OccupancyTotal = row.Total,
                OccupiedUnits = row.Occupied,
                ReservedUnits = row.Reserved,
                MaintenanceOpen = null,
                MaintenanceEmergency = null,
                MaintenanceInProgress = null,
                TotalLeases = null,
                ActiveLeases = null,
                Lifecycle = null,
                LifecycleCount = null,
            });

        var maintenanceProperties = AuthorizedProperties(scope, CapabilityKeys.WorkRead);
        var maintenanceRows = _db.WorkOrders
            .AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == scope.PortfolioId
                && maintenanceProperties.Any(property => property.Id == workOrder.PropertyId)
                && workOrder.Status != WorkOrderStatus.Completed
                && workOrder.Status != WorkOrderStatus.Cancelled
                && workOrder.Status != WorkOrderStatus.Archived)
            .GroupBy(_ => 1)
            .Select(group => new DashboardHeaderKpiReadRow
            {
                PortfolioId = scope.PortfolioId,
                PortfolioName = null,
                ManagementCompanyName = null,
                TimeZone = null,
                PortfolioStatus = null,
                OccupancyTotal = null,
                OccupiedUnits = null,
                ReservedUnits = null,
                MaintenanceOpen = group.Count(),
                MaintenanceEmergency = group.Count(workOrder => workOrder.Priority == WorkOrderPriority.Emergency),
                MaintenanceInProgress = group.Count(workOrder => workOrder.Status == WorkOrderStatus.InProgress),
                TotalLeases = null,
                ActiveLeases = null,
                Lifecycle = null,
                LifecycleCount = null,
            });

        var leasingProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        var lifecycleRows = _db.LeaseManagementLifecycleProjections
            .AsNoTracking()
            .Where(row => row.PortfolioId == scope.PortfolioId &&
                leasingProperties.Any(property => property.Id == row.PropertyId));
        var leasingCounts = lifecycleRows
            .GroupBy(_ => 1)
            .Select(group => new DashboardHeaderKpiReadRow
            {
                PortfolioId = scope.PortfolioId,
                PortfolioName = null,
                ManagementCompanyName = null,
                TimeZone = null,
                PortfolioStatus = null,
                OccupancyTotal = null,
                OccupiedUnits = null,
                ReservedUnits = null,
                MaintenanceOpen = null,
                MaintenanceEmergency = null,
                MaintenanceInProgress = null,
                TotalLeases = group.Count(),
                ActiveLeases = group.Count(row => row.Lifecycle == "Occupied" || row.Lifecycle == "Ending"),
                Lifecycle = null,
                LifecycleCount = null,
            });
        var leasingStatuses = lifecycleRows
            .GroupBy(row => row.Lifecycle)
            .Select(group => new DashboardHeaderKpiReadRow
            {
                PortfolioId = scope.PortfolioId,
                PortfolioName = null,
                ManagementCompanyName = null,
                TimeZone = null,
                PortfolioStatus = null,
                OccupancyTotal = null,
                OccupiedUnits = null,
                ReservedUnits = null,
                MaintenanceOpen = null,
                MaintenanceEmergency = null,
                MaintenanceInProgress = null,
                TotalLeases = null,
                ActiveLeases = null,
                Lifecycle = group.Key,
                LifecycleCount = group.Count(),
            });

        return portfolioRows
            .Concat(occupancyRows)
            .Concat(maintenanceRows)
            .Concat(leasingCounts)
            .Concat(leasingStatuses);
    }

    private IQueryable<DashboardInitialReadRow> BuildDashboardInitialReadQuery(
        WorkspaceReadScope scope,
        DateTime now)
    {
        var headerRows = BuildDashboardHeaderAndKpiQuery(scope)
            .Select(row => new DashboardInitialReadRow
            {
                RowKind = DashboardInitialReadKind.HeaderKpi,
                SortDay = DateOnly.MinValue,
                SortDate = DateTime.UnixEpoch,
                SortId = 0L,
                PortfolioId = row.PortfolioId,
                PortfolioName = row.PortfolioName,
                ManagementCompanyName = row.ManagementCompanyName,
                TimeZone = row.TimeZone,
                PortfolioStatus = row.PortfolioStatus,
                OccupancyTotal = row.OccupancyTotal,
                OccupiedUnits = row.OccupiedUnits,
                ReservedUnits = row.ReservedUnits,
                MaintenanceOpen = row.MaintenanceOpen,
                MaintenanceEmergency = row.MaintenanceEmergency,
                MaintenanceInProgress = row.MaintenanceInProgress,
                TotalLeases = row.TotalLeases,
                ActiveLeases = row.ActiveLeases,
                Lifecycle = row.Lifecycle,
                LifecycleCount = row.LifecycleCount,
                ExpiringId = 0,
                ExpiringAgreementNumber = string.Empty,
                ExpiringTenant = string.Empty,
                ExpiringProperty = string.Empty,
                ExpiringUnit = string.Empty,
                ExpiringEndOn = DateOnly.MinValue,
                ExpiringBaseRentAmount = 0m,
                AppointmentId = 0,
                AppointmentTitle = string.Empty,
                AppointmentType = string.Empty,
                AppointmentStatus = string.Empty,
                AppointmentScheduledStart = DateTime.UnixEpoch,
                AppointmentAssignedTo = string.Empty,
                AppointmentPropertyId = 0,
                AppointmentUnitId = 0,
            });

        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
        var expiringRows =
            from lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            where lifecycle.PortfolioId == scope.PortfolioId
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
            select new DashboardInitialReadRow
            {
                RowKind = DashboardInitialReadKind.ExpiringLease,
                SortDay = agreement.TermEndOn,
                SortDate = DateTime.UnixEpoch,
                SortId = agreement.Id,
                PortfolioId = 0,
                PortfolioName = string.Empty,
                ManagementCompanyName = string.Empty,
                TimeZone = string.Empty,
                PortfolioStatus = 0,
                OccupancyTotal = 0,
                OccupiedUnits = 0,
                ReservedUnits = 0,
                MaintenanceOpen = 0,
                MaintenanceEmergency = 0,
                MaintenanceInProgress = 0,
                TotalLeases = 0,
                ActiveLeases = 0,
                Lifecycle = string.Empty,
                LifecycleCount = 0,
                ExpiringId = agreement.Id,
                ExpiringAgreementNumber = agreement.AgreementNumber,
                ExpiringTenant = lifecycle.CurrentPrimaryTenantName,
                ExpiringProperty = property.Name,
                ExpiringUnit = unit.UnitNumber,
                ExpiringEndOn = agreement.TermEndOn,
                ExpiringBaseRentAmount = agreement.BaseRentAmount,
                AppointmentId = 0,
                AppointmentTitle = string.Empty,
                AppointmentType = string.Empty,
                AppointmentStatus = string.Empty,
                AppointmentScheduledStart = DateTime.UnixEpoch,
                AppointmentAssignedTo = string.Empty,
                AppointmentPropertyId = 0,
                AppointmentUnitId = 0,
            };

        var appointmentRows = _db.Appointments
            .AsNoTracking()
            .Where(appointment => appointment.PortfolioId == scope.PortfolioId
                && appointment.PropertyId != null
                && authorizedProperties.Any(authorized => authorized.Id == appointment.PropertyId.Value)
                && appointment.ScheduledStart >= now
                && appointment.Status != AppointmentStatus.Cancelled)
            .OrderBy(appointment => appointment.ScheduledStart)
            .Take(10)
            .Select(appointment => new DashboardInitialReadRow
            {
                RowKind = DashboardInitialReadKind.UpcomingAppointment,
                SortDay = DateOnly.MinValue,
                SortDate = appointment.ScheduledStart,
                SortId = appointment.Id,
                PortfolioId = 0,
                PortfolioName = string.Empty,
                ManagementCompanyName = string.Empty,
                TimeZone = string.Empty,
                PortfolioStatus = 0,
                OccupancyTotal = 0,
                OccupiedUnits = 0,
                ReservedUnits = 0,
                MaintenanceOpen = 0,
                MaintenanceEmergency = 0,
                MaintenanceInProgress = 0,
                TotalLeases = 0,
                ActiveLeases = 0,
                Lifecycle = string.Empty,
                LifecycleCount = 0,
                ExpiringId = 0,
                ExpiringAgreementNumber = string.Empty,
                ExpiringTenant = string.Empty,
                ExpiringProperty = string.Empty,
                ExpiringUnit = string.Empty,
                ExpiringEndOn = DateOnly.MinValue,
                ExpiringBaseRentAmount = 0m,
                AppointmentId = appointment.Id,
                AppointmentTitle = appointment.Title,
                AppointmentType = appointment.Type.ToString(),
                AppointmentStatus = appointment.Status.ToString(),
                AppointmentScheduledStart = appointment.ScheduledStart,
                AppointmentAssignedTo = appointment.AssignedTo,
                AppointmentPropertyId = appointment.PropertyId,
                AppointmentUnitId = appointment.UnitId,
            });

        return headerRows
            .Concat(expiringRows)
            .Concat(appointmentRows)
            .OrderBy(row => row.RowKind)
            .ThenBy(row => row.SortDay)
            .ThenBy(row => row.SortDate)
            .ThenBy(row => row.SortId);
    }

    private async Task<DashboardAccounting> BuildAccountingAsync(
        WorkspaceReadScope scope,
        DateTime monthStart,
        DateTime nextMonthStart,
        CancellationToken ct)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var monthStartOn = DateOnly.FromDateTime(monthStart);
        var nextMonthStartOn = DateOnly.FromDateTime(nextMonthStart);

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
        var authorizedMoneyProperties = AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead);
        var accountingDebtServiceQuery =
            from payment in LoanPaymentEffectiveQuery.From(_db)
            join loan in _db.Loans.AsNoTracking()
                on new { payment.PortfolioId, payment.LoanId }
                equals new { loan.PortfolioId, LoanId = loan.Id }
            where payment.PortfolioId == scope.PortfolioId
                && payment.Status == LoanPaymentStatus.Paid
                && payment.PaidDate >= monthStart
                && payment.PaidDate < nextMonthStart
                && authorizedMoneyProperties.Any(property => property.Id == loan.PropertyId)
            select payment.TotalAmount;
        var accountingAnchor = FinancialReportProjections.BuildAuthorizedCashFlowAnchor(
            _db,
            scope,
            CapabilityKeys.MoneyBalancesRead,
            utcNow,
            []);

        // Materialize the small authorized active-account boundary before touching immutable
        // ledger facts. The canonical charge arithmetic is unchanged, but the planner can no
        // longer expand the portfolio-wide lifecycle, entry, and allocation relations together.
        var receivablesQuery = BuildDashboardReceivablesQuery(
            scope,
            monthStartOn,
            nextMonthStartOn);

        // One PostgreSQL statement derives both receivable KPIs and cash-flow totals from canonical
        // facts. Billed charges, reversals, and allocations are immutable, and the current
        // possession boundary determines which accounts need attention.
        var accountingTotals = await (
            from _ in accountingAnchor
            from receivables in receivablesQuery
            select new
            {
                Overdue = receivables.Overdue ?? 0m,
                DueThisMonth = receivables.DueThisMonth ?? 0m,
                Income = accountingIncomeQuery.Sum(row => (decimal?)row.Amount) ?? 0m,
                Expense = (accountingExpenseQuery.Sum(expense => (decimal?)expense.Amount) ?? 0m)
                    + (accountingDebtServiceQuery.Sum(amount => (decimal?)amount) ?? 0m),
            })
            .SingleOrDefaultAsync(ct);

        var overdue = accountingTotals?.Overdue ?? 0m;
        var dueThisMonth = accountingTotals?.DueThisMonth ?? 0m;
        var paidThisMonth = accountingTotals?.Income ?? 0m;
        var expensesThisMonth = accountingTotals?.Expense ?? 0m;

        return new DashboardAccounting
        {
            DueThisMonthAmount = dueThisMonth,
            PaidThisMonthAmount = paidThisMonth,
            OverdueAmount = overdue,
            ExpensesThisMonthAmount = expensesThisMonth,
            NetThisMonth = paidThisMonth - expensesThisMonth,
        };
    }

    private IQueryable<DashboardReceivableTotalsReadRow> BuildDashboardReceivablesQuery(
        WorkspaceReadScope scope,
        DateOnly monthStartOn,
        DateOnly nextMonthStartOn)
    {
        const string sql = """
            WITH active_portfolio AS MATERIALIZED (
                SELECT
                    portfolio."Id" AS "PortfolioId",
                    effective_time."NowUtc" AS "NowUtc",
                    (effective_time."NowUtc" AT TIME ZONE
                        COALESCE(NULLIF(clock_state."TimeZoneId", ''), portfolio."TimeZone"))::date
                        AS "BusinessDate"
                FROM "Portfolios" AS portfolio
                LEFT JOIN "SimulationClocks" AS clock_state
                    ON clock_state."Id" = 1
                CROSS JOIN LATERAL (
                    SELECT rc_effective_now_utc(portfolio."Id") AS "NowUtc"
                ) AS effective_time
                WHERE portfolio."Id" = @portfolioId
                  AND portfolio."DeletedAt" IS NULL
            ),
            effective_scopes AS MATERIALIZED (
                SELECT effective_scope."ScopeKind", effective_scope."PropertyId"
                FROM public.rc_api_effective_capability_scopes(
                    @portfolioId,
                    @sessionId,
                    @userId,
                    @accessContextId,
                    @accessRevision,
                    @capabilityKeys,
                    @targetKind) AS effective_scope
            ),
            authorized_properties AS MATERIALIZED (
                SELECT property_row."Id" AS "PropertyId"
                FROM "Properties" AS property_row
                INNER JOIN active_portfolio
                    ON active_portfolio."PortfolioId" = property_row."PortfolioId"
                WHERE property_row."PortfolioId" = @portfolioId
                  AND property_row."DeletedAt" IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM effective_scopes
                      WHERE effective_scopes."ScopeKind" = 'AllProperties'
                         OR (effective_scopes."ScopeKind" = 'SelectedProperties'
                             AND effective_scopes."PropertyId" = property_row."Id")
                  )
            ),
            active_accounts AS MATERIALIZED (
                SELECT
                    account."PortfolioId" AS "PortfolioId",
                    account."Id" AS "TenantAccountId",
                    active_portfolio."BusinessDate" AS "BusinessDate"
                FROM "TenantAccounts" AS account
                INNER JOIN active_portfolio
                    ON active_portfolio."PortfolioId" = account."PortfolioId"
                INNER JOIN "LeaseManagements" AS management
                    ON management."PortfolioId" = account."PortfolioId"
                   AND management."Id" = account."LeaseManagementId"
                INNER JOIN authorized_properties
                    ON authorized_properties."PropertyId" = management."PropertyId"
                WHERE account."PortfolioId" = @portfolioId
                  AND management."CanceledAtUtc" IS NULL
                  AND management."AccountClosedAtUtc" IS NULL
                  AND management."PossessionGivenAtUtc" <= active_portfolio."NowUtc"
                  AND (management."PossessionReturnedAtUtc" IS NULL
                       OR management."PossessionReturnedAtUtc" > active_portfolio."NowUtc")
            ),
            charge_rows AS MATERIALIZED (
                SELECT
                    balance."PortfolioId",
                    balance."TenantAccountId",
                    balance."TenantLedgerEntryId",
                    balance."DueOn",
                    balance."BusinessDate",
                    balance."OriginalAmount",
                    balance."ReversedAmount",
                    balance."NetAllocations"
                FROM active_accounts AS active_account
                INNER JOIN "vw_tenant_charge_balances" AS balance
                    ON balance."PortfolioId" = active_account."PortfolioId"
                   AND balance."TenantAccountId" = active_account."TenantAccountId"
            )
            SELECT
                COALESCE(sum(
                    CASE
                        WHEN charge_row."DueOn" IS NOT NULL
                         AND charge_row."DueOn" < charge_row."BusinessDate"
                         AND charge_row."OriginalAmount"
                             - charge_row."ReversedAmount"
                             - charge_row."NetAllocations" > 0
                        THEN GREATEST(
                            0::numeric,
                            charge_row."OriginalAmount"
                                - charge_row."ReversedAmount"
                                - charge_row."NetAllocations")
                        ELSE 0::numeric
                    END), 0::numeric) AS "Overdue",
                COALESCE(sum(
                    CASE
                        WHEN charge_row."DueOn" >= @monthStartOn
                         AND charge_row."DueOn" < @nextMonthStartOn
                        THEN charge_row."OriginalAmount" - charge_row."ReversedAmount"
                        ELSE 0::numeric
                    END), 0::numeric) AS "DueThisMonth"
            FROM charge_rows AS charge_row
            """;

        return _db.Database.SqlQueryRaw<DashboardReceivableTotalsReadRow>(
            sql,
            new NpgsqlParameter<int>("portfolioId", scope.PortfolioId),
            new NpgsqlParameter<Guid>("sessionId", scope.SessionId),
            new NpgsqlParameter<int>("userId", scope.UserId),
            new NpgsqlParameter<int>("accessContextId", scope.AccessContextId),
            new NpgsqlParameter<long>("accessRevision", scope.AccessRevision),
            new NpgsqlParameter("capabilityKeys", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = new[] { CapabilityKeys.MoneyBalancesRead },
            },
            new NpgsqlParameter<string>(
                "targetKind",
                CapabilityAuthorizationTargetKind.Property.ToString()),
            new NpgsqlParameter("monthStartOn", NpgsqlDbType.Date) { Value = monthStartOn },
            new NpgsqlParameter("nextMonthStartOn", NpgsqlDbType.Date) { Value = nextMonthStartOn });
    }

    private sealed class DashboardHeaderKpiReadRow
    {
        public int? PortfolioId { get; set; }
        public string? PortfolioName { get; set; }
        public string? ManagementCompanyName { get; set; }
        public string? TimeZone { get; set; }
        public int? PortfolioStatus { get; set; }
        public int? OccupancyTotal { get; set; }
        public int? OccupiedUnits { get; set; }
        public int? ReservedUnits { get; set; }
        public int? MaintenanceOpen { get; set; }
        public int? MaintenanceEmergency { get; set; }
        public int? MaintenanceInProgress { get; set; }
        public int? TotalLeases { get; set; }
        public int? ActiveLeases { get; set; }
        public string? Lifecycle { get; set; }
        public int? LifecycleCount { get; set; }
    }

    private enum DashboardInitialReadKind
    {
        HeaderKpi,
        ExpiringLease,
        UpcomingAppointment,
    }

    private sealed class DashboardInitialReadRow
    {
        public DashboardInitialReadKind RowKind { get; init; }
        public DateOnly? SortDay { get; init; }
        public DateTime? SortDate { get; init; }
        public long? SortId { get; init; }
        public int? PortfolioId { get; init; }
        public string? PortfolioName { get; init; }
        public string? ManagementCompanyName { get; init; }
        public string? TimeZone { get; init; }
        public int? PortfolioStatus { get; init; }
        public int? OccupancyTotal { get; init; }
        public int? OccupiedUnits { get; init; }
        public int? ReservedUnits { get; init; }
        public int? MaintenanceOpen { get; init; }
        public int? MaintenanceEmergency { get; init; }
        public int? MaintenanceInProgress { get; init; }
        public int? TotalLeases { get; init; }
        public int? ActiveLeases { get; init; }
        public string? Lifecycle { get; init; }
        public int? LifecycleCount { get; init; }
        public int? ExpiringId { get; init; }
        public string? ExpiringAgreementNumber { get; init; }
        public string? ExpiringTenant { get; init; }
        public string? ExpiringProperty { get; init; }
        public string? ExpiringUnit { get; init; }
        public DateOnly? ExpiringEndOn { get; init; }
        public decimal? ExpiringBaseRentAmount { get; init; }
        public int? AppointmentId { get; init; }
        public string? AppointmentTitle { get; init; }
        public string? AppointmentType { get; init; }
        public string? AppointmentStatus { get; init; }
        public DateTime? AppointmentScheduledStart { get; init; }
        public string? AppointmentAssignedTo { get; init; }
        public int? AppointmentPropertyId { get; init; }
        public int? AppointmentUnitId { get; init; }
    }

    private sealed class DashboardReceivableTotalsReadRow
    {
        public decimal? Overdue { get; init; }
        public decimal? DueThisMonth { get; init; }
    }

    private async Task<IReadOnlyList<DashboardActivity>> BuildRecentActivityAsync(
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        var rows = await BuildRecentActivitySingleStatementQuery(scope).ToListAsync(ct);
        return rows
            .Select(row => new DashboardActivity
            {
                Id = row.Audit.Id,
                Type = row.Audit.EntityType,
                EntityId = row.Audit.EntityId,
                UnitId = row.UnitId,
                LeaseManagementId = row.LeaseManagementId,
                Action = row.Audit.Operation.ToString(),
                Description = _auditDescriber.Describe(row.Audit),
                Label = string.IsNullOrWhiteSpace(row.Label) ? null : row.Label.Trim(),
                Actor = AuditEntryResponse.ResolveActor(row.Audit, row.ResolvedActorName),
                CreatedAt = row.Audit.Timestamp,
            })
            .ToList();
    }

    /// <summary>
    /// Resolves the bounded authorized audit page and its labels/Unit context in one translated SQL
    /// statement. The fact branches are correlated to the page-key subquery, so unrelated rows are
    /// never materialized merely to decorate the feed.
    /// </summary>
    private IQueryable<DashboardActivitySingleStatementReadRow> BuildRecentActivitySingleStatementQuery(
        WorkspaceReadScope scope)
    {
        var auditPage = BuildRecentActivityAuditPageQuery(scope);
        var entityFacts = BuildRecentActivityEntityFactQueryForPage(scope, auditPage);
        return
            from page in auditPage
            join fact in entityFacts
                on new { EntityType = page.Audit.EntityType, EntityId = (long)page.Audit.EntityId }
                equals new { fact.EntityType, fact.EntityId }
                into factJoin
            from fact in factJoin.DefaultIfEmpty()
            select new DashboardActivitySingleStatementReadRow
            {
                Audit = page.Audit,
                ResolvedActorName = page.ResolvedActorName,
                Label = fact == null ? null : fact.Label,
                UnitId = fact == null ? null : fact.UnitId,
                LeaseManagementId = fact == null ? null : fact.LeaseManagementId,
            };
    }

    /// <summary>
    /// Statement 1 for recent activity: authorize, order, and cap the audit relation before the
    /// optional actor join. The outer ordering keeps the response stable after that join.
    /// </summary>
    internal IQueryable<DashboardActivityAuditPageReadRow> BuildRecentActivityAuditPageQuery(
        WorkspaceReadScope scope)
    {
        var portfolioId = scope.PortfolioId;
        var audits = _db.AtomicAuditLogs
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                _timeProvider.GetUtcNow().UtcDateTime)
            .OrderByDescending(audit => audit.Timestamp)
            .ThenByDescending(audit => audit.Id)
            .Take(10);
        var actorUsers = _db.Users
            .AsNoTracking()
            .Where(user => user.WorkspaceAccessContexts.Any(context => context.PortfolioId == portfolioId));

        return from audit in audits
               join user in actorUsers
                   on audit.UserId equals (int?)user.Id
                   into actorJoin
               from user in actorJoin.DefaultIfEmpty()
               orderby audit.Timestamp descending, audit.Id descending
               select new DashboardActivityAuditPageReadRow
               {
                   Audit = audit,
                   ResolvedActorName = audit.ActorLabel != null && audit.ActorLabel != ""
                       ? audit.ActorLabel
                       : user == null
                           ? null
                           : user.DisplayName != null && user.DisplayName != ""
                               ? user.DisplayName
                               : user.Email,
               };
    }

    /// <summary>
    /// Statement 2 for recent activity: resolve labels and Unit context only for entity keys present
    /// in the bounded audit page. Branches absent from the page are not composed into the SQL union.
    /// </summary>
    internal IQueryable<DashboardActivityEntityReadRow> BuildRecentActivityEntityFactQuery(
        WorkspaceReadScope scope,
        IReadOnlyDictionary<string, long[]> keysByType)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.ReportsRead);
        IQueryable<DashboardActivityEntityReadRow>? entityRows = null;

        static void Append(
            ref IQueryable<DashboardActivityEntityReadRow>? target,
            IQueryable<DashboardActivityEntityReadRow> branch) =>
            target = target == null ? branch : target.Concat(branch);

        if (keysByType.TryGetValue(nameof(Property), out var propertyIds) && propertyIds.Length > 0)
        {
            var propertyRows = authorizedProperties
                .Where(property => propertyIds.Contains((long)property.Id))
                .Select(property => new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(Property),
                    EntityId = property.Id,
                    Label = property.Name,
                    UnitId = null,
                    LeaseManagementId = null,
                });
            Append(ref entityRows, propertyRows);
        }

        if (keysByType.TryGetValue(nameof(Unit), out var unitIds) && unitIds.Length > 0)
        {
            var pageUnits = _db.Units
                .AsNoTracking()
                .Where(unit => unit.PortfolioId == portfolioId
                    && unitIds.Contains((long)unit.Id)
                    && authorizedProperties.Any(authorized => authorized.Id == unit.PropertyId));
            var unitRows =
                from unit in pageUnits
                join property in _db.Properties.AsNoTracking()
                    on new { unit.PortfolioId, Id = unit.PropertyId }
                    equals new { property.PortfolioId, property.Id }
                select new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(Unit),
                    EntityId = unit.Id,
                    Label = property.Name + " · Unit " + unit.UnitNumber,
                    UnitId = unit.Id,
                    LeaseManagementId = null,
                };
            Append(ref entityRows, unitRows);
        }

        if (keysByType.TryGetValue(nameof(Tenant), out var tenantIds) && tenantIds.Length > 0)
        {
            var tenantParties =
                from party in _db.LeaseManagementParties.AsNoTracking()
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { party.PortfolioId, Id = party.LeaseManagementId }
                    equals new { management.PortfolioId, management.Id }
                where party.PortfolioId == portfolioId
                    && tenantIds.Contains((long)party.TenantId)
                    && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
                select new
                {
                    party.PortfolioId,
                    party.TenantId,
                    party.EffectiveFrom,
                    party.Id,
                    management.UnitId,
                };
            var latestTenantParties = tenantParties
                .GroupBy(party => new { party.PortfolioId, party.TenantId })
                .Select(group => new
                {
                    group.Key.PortfolioId,
                    group.Key.TenantId,
                    UnitId = EF.Functions.ArrayAgg(
                        group
                            .OrderByDescending(party => party.EffectiveFrom)
                            .ThenByDescending(party => party.Id)
                            .Select(party => (int?)party.UnitId))[0],
                });
            var pageTenants = _db.Tenants
                .AsNoTracking()
                .Where(tenant => tenant.PortfolioId == portfolioId
                    && tenantIds.Contains((long)tenant.Id));
            var tenantRows =
                from tenant in pageTenants
                join latestParty in latestTenantParties
                    on new { tenant.PortfolioId, tenant.Id }
                    equals new { latestParty.PortfolioId, Id = latestParty.TenantId }
                select new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(Tenant),
                    EntityId = tenant.Id,
                    Label = tenant.FirstName + " " + tenant.LastName,
                    UnitId = latestParty.UnitId,
                    LeaseManagementId = null,
                };
            Append(ref entityRows, tenantRows);
        }

        if (keysByType.TryGetValue(nameof(LeaseManagement), out var managementIds) && managementIds.Length > 0)
        {
            var managementRows = _db.LeaseManagements
                .AsNoTracking()
                .Where(management => management.PortfolioId == portfolioId
                    && managementIds.Contains((long)management.Id)
                    && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId))
                .Select(management => new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(LeaseManagement),
                    EntityId = management.Id,
                    Label = management.RelationshipNumber,
                    UnitId = management.UnitId,
                    LeaseManagementId = null,
                });
            Append(ref entityRows, managementRows);
        }

        if (keysByType.TryGetValue(nameof(LeaseAgreement), out var agreementIds) && agreementIds.Length > 0)
        {
            var agreementRows =
                from agreement in _db.LeaseAgreements.AsNoTracking()
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { agreement.PortfolioId, agreement.LeaseManagementId }
                    equals new { management.PortfolioId, LeaseManagementId = management.Id }
                where agreement.PortfolioId == portfolioId
                    && agreementIds.Contains((long)agreement.Id)
                    && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
                select new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(LeaseAgreement),
                    EntityId = agreement.Id,
                    Label = agreement.AgreementNumber,
                    UnitId = management.UnitId,
                    LeaseManagementId = management.Id,
                };
            Append(ref entityRows, agreementRows);
        }

        if (keysByType.TryGetValue(nameof(LeaseAddendum), out var addendumIds) && addendumIds.Length > 0)
        {
            var addendumRows =
                from addendum in _db.LeaseAddenda.AsNoTracking()
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { addendum.PortfolioId, addendum.LeaseManagementId }
                    equals new { management.PortfolioId, LeaseManagementId = management.Id }
                where addendum.PortfolioId == portfolioId
                    && addendumIds.Contains((long)addendum.Id)
                    && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
                select new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(LeaseAddendum),
                    EntityId = addendum.Id,
                    Label = addendum.AddendumNumber,
                    UnitId = management.UnitId,
                    LeaseManagementId = null,
                };
            Append(ref entityRows, addendumRows);
        }

        if (keysByType.TryGetValue(nameof(TenantAccount), out var accountIds) && accountIds.Length > 0)
        {
            var accountRows =
                from account in _db.TenantAccounts.AsNoTracking()
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { account.PortfolioId, account.LeaseManagementId }
                    equals new { management.PortfolioId, LeaseManagementId = management.Id }
                where account.PortfolioId == portfolioId
                    && accountIds.Contains((long)account.Id)
                    && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
                select new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(TenantAccount),
                    EntityId = account.Id,
                    Label = account.AccountNumber,
                    UnitId = management.UnitId,
                    LeaseManagementId = null,
                };
            Append(ref entityRows, accountRows);
        }

        if (keysByType.TryGetValue(nameof(TenantLedgerEntry), out var ledgerEntryIds)
            && ledgerEntryIds.Length > 0)
        {
            var ledgerRows =
                from entry in _db.TenantLedgerEntries.AsNoTracking()
                join account in _db.TenantAccounts.AsNoTracking()
                    on new { entry.PortfolioId, entry.TenantAccountId }
                    equals new { account.PortfolioId, TenantAccountId = account.Id }
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { account.PortfolioId, account.LeaseManagementId }
                    equals new { management.PortfolioId, LeaseManagementId = management.Id }
                where entry.PortfolioId == portfolioId
                    && ledgerEntryIds.Contains(entry.Id)
                    && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
                select new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(TenantLedgerEntry),
                    EntityId = entry.Id,
                    Label = entry.Description,
                    UnitId = management.UnitId,
                    LeaseManagementId = null,
                };
            Append(ref entityRows, ledgerRows);
        }

        if (keysByType.TryGetValue(nameof(SecurityDepositAccount), out var depositAccountIds)
            && depositAccountIds.Length > 0)
        {
            var depositAccountRows = BuildSecurityDepositAccountFactQuery(
                portfolioId,
                depositAccountIds,
                nameof(SecurityDepositAccount),
                authorizedProperties);
            Append(ref entityRows, depositAccountRows);
        }

        if (keysByType.TryGetValue("SecurityDeposit", out var depositAliasIds)
            && depositAliasIds.Length > 0)
        {
            var depositAliasRows = BuildSecurityDepositAccountFactQuery(
                portfolioId,
                depositAliasIds,
                "SecurityDeposit",
                authorizedProperties);
            Append(ref entityRows, depositAliasRows);
        }

        if (keysByType.TryGetValue(nameof(SecurityDepositEntry), out var depositEntryIds)
            && depositEntryIds.Length > 0)
        {
            var depositEntryRows =
                from entry in _db.SecurityDepositEntries.AsNoTracking()
                join deposit in _db.SecurityDepositAccounts.AsNoTracking()
                    on new { entry.PortfolioId, entry.SecurityDepositAccountId }
                    equals new { deposit.PortfolioId, SecurityDepositAccountId = deposit.Id }
                join account in _db.TenantAccounts.AsNoTracking()
                    on new { deposit.PortfolioId, deposit.TenantAccountId }
                    equals new { account.PortfolioId, TenantAccountId = account.Id }
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { account.PortfolioId, account.LeaseManagementId }
                    equals new { management.PortfolioId, LeaseManagementId = management.Id }
                where entry.PortfolioId == portfolioId
                    && depositEntryIds.Contains(entry.Id)
                    && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
                select new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(SecurityDepositEntry),
                    EntityId = entry.Id,
                    Label = entry.Description,
                    UnitId = management.UnitId,
                    LeaseManagementId = null,
                };
            Append(ref entityRows, depositEntryRows);
        }

        if (keysByType.TryGetValue(nameof(WorkOrder), out var workOrderIds) && workOrderIds.Length > 0)
        {
            var workOrderRows = _db.WorkOrders
                .AsNoTracking()
                .Where(workOrder => workOrder.PortfolioId == portfolioId
                    && workOrderIds.Contains((long)workOrder.Id)
                    && authorizedProperties.Any(authorized => authorized.Id == workOrder.PropertyId))
                .Select(workOrder => new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(WorkOrder),
                    EntityId = workOrder.Id,
                    Label = workOrder.Title,
                    UnitId = workOrder.UnitId,
                    LeaseManagementId = null,
                });
            Append(ref entityRows, workOrderRows);
        }

        if (keysByType.TryGetValue(nameof(Expense), out var expenseIds) && expenseIds.Length > 0)
        {
            var expenseRows =
                from expense in _db.Expenses.AsNoTracking()
                join workOrder in _db.WorkOrders.AsNoTracking()
                    on new { expense.PortfolioId, WorkOrderId = expense.WorkOrderId }
                    equals new { workOrder.PortfolioId, WorkOrderId = (int?)workOrder.Id }
                    into workOrderJoin
                from workOrder in workOrderJoin.DefaultIfEmpty()
                where expense.PortfolioId == portfolioId
                    && expenseIds.Contains((long)expense.Id)
                    && ((expense.PropertyId != null
                            && authorizedProperties.Any(authorized => authorized.Id == expense.PropertyId.Value))
                        || (expense.PropertyId == null
                            && workOrder != null
                            && authorizedProperties.Any(authorized => authorized.Id == workOrder.PropertyId)))
                select new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(Expense),
                    EntityId = expense.Id,
                    Label = expense.Description,
                    UnitId = expense.UnitId ?? (workOrder == null ? null : workOrder.UnitId),
                    LeaseManagementId = null,
                };
            Append(ref entityRows, expenseRows);
        }

        if (keysByType.TryGetValue(nameof(Appointment), out var appointmentIds)
            && appointmentIds.Length > 0)
        {
            var appointmentRows = _db.Appointments
                .AsNoTracking()
                .Where(appointment => appointment.PortfolioId == portfolioId
                    && appointmentIds.Contains((long)appointment.Id)
                    && appointment.PropertyId != null
                    && authorizedProperties.Any(authorized => authorized.Id == appointment.PropertyId.Value))
                .Select(appointment => new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(Appointment),
                    EntityId = appointment.Id,
                    Label = appointment.Title,
                    UnitId = appointment.UnitId,
                    LeaseManagementId = null,
                });
            Append(ref entityRows, appointmentRows);
        }

        if (keysByType.TryGetValue(nameof(Inspection), out var inspectionIds) && inspectionIds.Length > 0)
        {
            var pageInspections = _db.Inspections
                .AsNoTracking()
                .Where(inspection => inspection.PortfolioId == portfolioId
                    && inspectionIds.Contains((long)inspection.Id)
                    && authorizedProperties.Any(authorized => authorized.Id == inspection.PropertyId));
            var inspectionRows =
                from inspection in pageInspections
                join property in _db.Properties.AsNoTracking()
                    on new { inspection.PortfolioId, inspection.PropertyId }
                    equals new { property.PortfolioId, PropertyId = property.Id }
                select new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(Inspection),
                    EntityId = inspection.Id,
                    Label = property.Name,
                    UnitId = inspection.UnitId,
                    LeaseManagementId = null,
                };
            Append(ref entityRows, inspectionRows);
        }

        if (keysByType.TryGetValue(nameof(RentalApplication), out var applicationIds)
            && applicationIds.Length > 0)
        {
            var applicationRows = _db.RentalApplications
                .AsNoTracking()
                .Where(application => application.PortfolioId == portfolioId
                    && applicationIds.Contains((long)application.Id)
                    && application.PropertyId != null
                    && authorizedProperties.Any(authorized => authorized.Id == application.PropertyId.Value))
                .Select(application => new DashboardActivityEntityReadRow
                {
                    EntityType = nameof(RentalApplication),
                    EntityId = application.Id,
                    Label = application.FirstName + " " + application.LastName,
                    UnitId = application.UnitId,
                    LeaseManagementId = null,
                });
            Append(ref entityRows, applicationRows);
        }

        return entityRows ?? _db.Database.SqlQuery<DashboardActivityEntityReadRow>(
            $"""
            SELECT ''::text AS "EntityType", 0::bigint AS "EntityId",
                   NULL::text AS "Label", NULL::integer AS "UnitId",
                   NULL::integer AS "LeaseManagementId"
            WHERE FALSE
            """);
    }

    private IQueryable<DashboardActivityEntityReadRow> BuildSecurityDepositAccountFactQuery(
        int portfolioId,
        long[] depositIds,
        string entityType,
        IQueryable<Property> authorizedProperties)
    {
        return
            from deposit in _db.SecurityDepositAccounts.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { deposit.PortfolioId, deposit.TenantAccountId }
                equals new { account.PortfolioId, TenantAccountId = account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, account.LeaseManagementId }
                equals new { management.PortfolioId, LeaseManagementId = management.Id }
            where deposit.PortfolioId == portfolioId
                && depositIds.Contains((long)deposit.Id)
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
            select new DashboardActivityEntityReadRow
            {
                EntityType = entityType,
                EntityId = deposit.Id,
                Label = account.AccountNumber + " deposit",
                UnitId = management.UnitId,
                LeaseManagementId = null,
            };
    }

    private IQueryable<DashboardActivityEntityReadRow> BuildRecentActivityEntityFactQueryForPage(
        WorkspaceReadScope scope,
        IQueryable<DashboardActivityAuditPageReadRow> auditPage)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.ReportsRead);

        IQueryable<long> PageIds(string entityType) => auditPage
            .Where(page => page.Audit.EntityType == entityType)
            .Select(page => (long)page.Audit.EntityId);

        IQueryable<DashboardActivityEntityReadRow>? entityRows = null;
        static void Append(
            ref IQueryable<DashboardActivityEntityReadRow>? target,
            IQueryable<DashboardActivityEntityReadRow> branch) =>
            target = target == null ? branch : target.Concat(branch);

        var propertyIds = PageIds(nameof(Property));
        Append(ref entityRows, authorizedProperties
            .Where(property => propertyIds.Contains(property.Id))
            .Select(property => new DashboardActivityEntityReadRow
            {
                EntityType = nameof(Property),
                EntityId = property.Id,
                Label = property.Name,
                UnitId = null,
                LeaseManagementId = null,
            }));

        var unitIds = PageIds(nameof(Unit));
        var unitRows =
            from unit in _db.Units.AsNoTracking()
            join property in _db.Properties.AsNoTracking()
                on new { unit.PortfolioId, Id = unit.PropertyId }
                equals new { property.PortfolioId, property.Id }
            where unit.PortfolioId == portfolioId
                && unitIds.Contains((long)unit.Id)
                && authorizedProperties.Any(authorized => authorized.Id == unit.PropertyId)
            select new DashboardActivityEntityReadRow
            {
                EntityType = nameof(Unit),
                EntityId = unit.Id,
                Label = property.Name + " · Unit " + unit.UnitNumber,
                UnitId = unit.Id,
                LeaseManagementId = null,
            };
        Append(ref entityRows, unitRows);

        var tenantIds = PageIds(nameof(Tenant));
        var tenantParties =
            from party in _db.LeaseManagementParties.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { party.PortfolioId, Id = party.LeaseManagementId }
                equals new { management.PortfolioId, management.Id }
            where party.PortfolioId == portfolioId
                && tenantIds.Contains((long)party.TenantId)
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
            select new
            {
                party.PortfolioId,
                party.TenantId,
                party.EffectiveFrom,
                party.Id,
                management.UnitId,
            };
        var latestTenantParties = tenantParties
            .GroupBy(party => new { party.PortfolioId, party.TenantId })
            .Select(group => new
            {
                group.Key.PortfolioId,
                group.Key.TenantId,
                UnitId = EF.Functions.ArrayAgg(
                    group
                        .OrderByDescending(party => party.EffectiveFrom)
                        .ThenByDescending(party => party.Id)
                        .Select(party => (int?)party.UnitId))[0],
            });
        var tenantRows =
            from tenant in _db.Tenants.AsNoTracking()
            join latestParty in latestTenantParties
                on new { tenant.PortfolioId, tenant.Id }
                equals new { latestParty.PortfolioId, Id = latestParty.TenantId }
            where tenant.PortfolioId == portfolioId
                && tenantIds.Contains((long)tenant.Id)
            select new DashboardActivityEntityReadRow
            {
                EntityType = nameof(Tenant),
                EntityId = tenant.Id,
                Label = tenant.FirstName + " " + tenant.LastName,
                UnitId = latestParty.UnitId,
                LeaseManagementId = null,
            };
        Append(ref entityRows, tenantRows);

        var managementIds = PageIds(nameof(LeaseManagement));
        Append(ref entityRows, _db.LeaseManagements
            .AsNoTracking()
            .Where(management => management.PortfolioId == portfolioId
                && managementIds.Contains((long)management.Id)
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId))
            .Select(management => new DashboardActivityEntityReadRow
            {
                EntityType = nameof(LeaseManagement),
                EntityId = management.Id,
                Label = management.RelationshipNumber,
                UnitId = management.UnitId,
                LeaseManagementId = null,
            }));

        var agreementIds = PageIds(nameof(LeaseAgreement));
        var agreementRows =
            from agreement in _db.LeaseAgreements.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { agreement.PortfolioId, agreement.LeaseManagementId }
                equals new { management.PortfolioId, LeaseManagementId = management.Id }
            where agreement.PortfolioId == portfolioId
                && agreementIds.Contains((long)agreement.Id)
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
            select new DashboardActivityEntityReadRow
            {
                EntityType = nameof(LeaseAgreement),
                EntityId = agreement.Id,
                Label = agreement.AgreementNumber,
                UnitId = management.UnitId,
                LeaseManagementId = management.Id,
            };
        Append(ref entityRows, agreementRows);

        var addendumIds = PageIds(nameof(LeaseAddendum));
        var addendumRows =
            from addendum in _db.LeaseAddenda.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { addendum.PortfolioId, addendum.LeaseManagementId }
                equals new { management.PortfolioId, LeaseManagementId = management.Id }
            where addendum.PortfolioId == portfolioId
                && addendumIds.Contains((long)addendum.Id)
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
            select new DashboardActivityEntityReadRow
            {
                EntityType = nameof(LeaseAddendum),
                EntityId = addendum.Id,
                Label = addendum.AddendumNumber,
                UnitId = management.UnitId,
                LeaseManagementId = null,
            };
        Append(ref entityRows, addendumRows);

        var accountIds = PageIds(nameof(TenantAccount));
        var accountRows =
            from account in _db.TenantAccounts.AsNoTracking()
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, account.LeaseManagementId }
                equals new { management.PortfolioId, LeaseManagementId = management.Id }
            where account.PortfolioId == portfolioId
                && accountIds.Contains((long)account.Id)
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
            select new DashboardActivityEntityReadRow
            {
                EntityType = nameof(TenantAccount),
                EntityId = account.Id,
                Label = account.AccountNumber,
                UnitId = management.UnitId,
                LeaseManagementId = null,
            };
        Append(ref entityRows, accountRows);

        var ledgerEntryIds = PageIds(nameof(TenantLedgerEntry));
        var ledgerRows =
            from entry in _db.TenantLedgerEntries.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { entry.PortfolioId, entry.TenantAccountId }
                equals new { account.PortfolioId, TenantAccountId = account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, account.LeaseManagementId }
                equals new { management.PortfolioId, LeaseManagementId = management.Id }
            where entry.PortfolioId == portfolioId
                && ledgerEntryIds.Contains(entry.Id)
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
            select new DashboardActivityEntityReadRow
            {
                EntityType = nameof(TenantLedgerEntry),
                EntityId = entry.Id,
                Label = entry.Description,
                UnitId = management.UnitId,
                LeaseManagementId = null,
            };
        Append(ref entityRows, ledgerRows);

        var depositAccountIds = PageIds(nameof(SecurityDepositAccount));
        Append(ref entityRows, BuildSecurityDepositAccountFactQueryForPage(
            portfolioId, depositAccountIds, nameof(SecurityDepositAccount), authorizedProperties));
        var depositAliasIds = PageIds("SecurityDeposit");
        Append(ref entityRows, BuildSecurityDepositAccountFactQueryForPage(
            portfolioId, depositAliasIds, "SecurityDeposit", authorizedProperties));

        var depositEntryIds = PageIds(nameof(SecurityDepositEntry));
        var depositEntryRows =
            from entry in _db.SecurityDepositEntries.AsNoTracking()
            join deposit in _db.SecurityDepositAccounts.AsNoTracking()
                on new { entry.PortfolioId, entry.SecurityDepositAccountId }
                equals new { deposit.PortfolioId, SecurityDepositAccountId = deposit.Id }
            join account in _db.TenantAccounts.AsNoTracking()
                on new { deposit.PortfolioId, deposit.TenantAccountId }
                equals new { account.PortfolioId, TenantAccountId = account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, account.LeaseManagementId }
                equals new { management.PortfolioId, LeaseManagementId = management.Id }
            where entry.PortfolioId == portfolioId
                && depositEntryIds.Contains(entry.Id)
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
            select new DashboardActivityEntityReadRow
            {
                EntityType = nameof(SecurityDepositEntry),
                EntityId = entry.Id,
                Label = entry.Description,
                UnitId = management.UnitId,
                LeaseManagementId = null,
            };
        Append(ref entityRows, depositEntryRows);

        var workOrderIds = PageIds(nameof(WorkOrder));
        Append(ref entityRows, _db.WorkOrders
            .AsNoTracking()
            .Where(workOrder => workOrder.PortfolioId == portfolioId
                && workOrderIds.Contains((long)workOrder.Id)
                && authorizedProperties.Any(authorized => authorized.Id == workOrder.PropertyId))
            .Select(workOrder => new DashboardActivityEntityReadRow
            {
                EntityType = nameof(WorkOrder),
                EntityId = workOrder.Id,
                Label = workOrder.Title,
                UnitId = workOrder.UnitId,
                LeaseManagementId = null,
            }));

        var expenseIds = PageIds(nameof(Expense));
        var expenseRows =
            from expense in _db.Expenses.AsNoTracking()
            join workOrder in _db.WorkOrders.AsNoTracking()
                on new { expense.PortfolioId, WorkOrderId = expense.WorkOrderId }
                equals new { workOrder.PortfolioId, WorkOrderId = (int?)workOrder.Id }
                into workOrderJoin
            from workOrder in workOrderJoin.DefaultIfEmpty()
            where expense.PortfolioId == portfolioId
                && expenseIds.Contains((long)expense.Id)
                && ((expense.PropertyId != null
                        && authorizedProperties.Any(authorized => authorized.Id == expense.PropertyId.Value))
                    || (expense.PropertyId == null
                        && workOrder != null
                        && authorizedProperties.Any(authorized => authorized.Id == workOrder.PropertyId)))
            select new DashboardActivityEntityReadRow
            {
                EntityType = nameof(Expense),
                EntityId = expense.Id,
                Label = expense.Description,
                UnitId = expense.UnitId ?? (workOrder == null ? null : workOrder.UnitId),
                LeaseManagementId = null,
            };
        Append(ref entityRows, expenseRows);

        var appointmentIds = PageIds(nameof(Appointment));
        Append(ref entityRows, _db.Appointments
            .AsNoTracking()
            .Where(appointment => appointment.PortfolioId == portfolioId
                && appointmentIds.Contains((long)appointment.Id)
                && appointment.PropertyId != null
                && authorizedProperties.Any(authorized => authorized.Id == appointment.PropertyId.Value))
            .Select(appointment => new DashboardActivityEntityReadRow
            {
                EntityType = nameof(Appointment),
                EntityId = appointment.Id,
                Label = appointment.Title,
                UnitId = appointment.UnitId,
                LeaseManagementId = null,
            }));

        var inspectionIds = PageIds(nameof(Inspection));
        var pageInspections = _db.Inspections
            .AsNoTracking()
            .Where(inspection => inspection.PortfolioId == portfolioId
                && inspectionIds.Contains((long)inspection.Id)
                && authorizedProperties.Any(authorized => authorized.Id == inspection.PropertyId));
        var inspectionRows =
            from inspection in pageInspections
            join property in _db.Properties.AsNoTracking()
                on new { inspection.PortfolioId, inspection.PropertyId }
                equals new { property.PortfolioId, PropertyId = property.Id }
            select new DashboardActivityEntityReadRow
            {
                EntityType = nameof(Inspection),
                EntityId = inspection.Id,
                Label = property.Name,
                UnitId = inspection.UnitId,
                LeaseManagementId = null,
            };
        Append(ref entityRows, inspectionRows);

        var applicationIds = PageIds(nameof(RentalApplication));
        Append(ref entityRows, _db.RentalApplications
            .AsNoTracking()
            .Where(application => application.PortfolioId == portfolioId
                && applicationIds.Contains((long)application.Id)
                && application.PropertyId != null
                && authorizedProperties.Any(authorized => authorized.Id == application.PropertyId.Value))
            .Select(application => new DashboardActivityEntityReadRow
            {
                EntityType = nameof(RentalApplication),
                EntityId = application.Id,
                Label = application.FirstName + " " + application.LastName,
                UnitId = application.UnitId,
                LeaseManagementId = null,
            }));

        return entityRows ?? _db.Database.SqlQuery<DashboardActivityEntityReadRow>(
            $"""
            SELECT ''::text AS "EntityType", 0::bigint AS "EntityId",
                   NULL::text AS "Label", NULL::integer AS "UnitId",
                   NULL::integer AS "LeaseManagementId"
            WHERE FALSE
            """);
    }

    private IQueryable<DashboardActivityEntityReadRow> BuildSecurityDepositAccountFactQueryForPage(
        int portfolioId,
        IQueryable<long> depositIds,
        string entityType,
        IQueryable<Property> authorizedProperties)
    {
        return
            from deposit in _db.SecurityDepositAccounts.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { deposit.PortfolioId, deposit.TenantAccountId }
                equals new { account.PortfolioId, TenantAccountId = account.Id }
            join management in _db.LeaseManagements.AsNoTracking()
                on new { account.PortfolioId, account.LeaseManagementId }
                equals new { management.PortfolioId, LeaseManagementId = management.Id }
            where deposit.PortfolioId == portfolioId
                && depositIds.Contains((long)deposit.Id)
                && authorizedProperties.Any(authorized => authorized.Id == management.PropertyId)
            select new DashboardActivityEntityReadRow
            {
                EntityType = entityType,
                EntityId = deposit.Id,
                Label = account.AccountNumber + " deposit",
                UnitId = management.UnitId,
                LeaseManagementId = null,
            };
    }

    internal sealed class DashboardActivityAuditPageReadRow
    {
        public AtomicAuditLog Audit { get; set; } = null!;
        public string? ResolvedActorName { get; set; }
    }

    internal sealed class DashboardActivityEntityReadRow
    {
        public string EntityType { get; set; } = string.Empty;
        public long EntityId { get; set; }
        public string? Label { get; set; }
        public int? UnitId { get; set; }
        public int? LeaseManagementId { get; set; }
    }

    private sealed class DashboardActivitySingleStatementReadRow
    {
        public required AtomicAuditLog Audit { get; init; }
        public string? ResolvedActorName { get; init; }
        public string? Label { get; init; }
        public int? UnitId { get; init; }
        public int? LeaseManagementId { get; init; }
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
