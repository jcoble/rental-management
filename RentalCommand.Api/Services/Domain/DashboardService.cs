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
        var now = _timeProvider.UtcNow();
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStart = monthStart.AddMonths(1);

        // The portfolio header and all count-only KPI blocks share one translated reader statement.
        // Each source remains grouped in SQL; only the final DTO assembly happens after materialization.
        var headerAndKpis = await BuildDashboardHeaderAndKpiQuery(scope).ToListAsync(ct);
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
                ExpiringSoon = await BuildExpiringLeasesAsync(scope, ct),
                ByStatus = byStatus,
            },
            Accounting = await BuildAccountingAsync(scope, monthStart, nextMonthStart, ct),
            RecentActivity = await BuildRecentActivityAsync(scope, ct),
            UpcomingAppointments = await BuildUpcomingAppointmentsAsync(scope, now, ct),
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

        // One PostgreSQL statement derives both receivable KPIs and cash-flow totals from canonical
        // facts. Billed charges and allocations are immutable; partial payment is reflected by the
        // balance views; and lifecycle scope determines which current accounts need attention.
        var accountingTotals = await accountingAnchor
            .Select(_ => new
            {
                Overdue = (
                    from balance in _db.TenantAccountBalanceProjections.AsNoTracking()
                    join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                        on new { balance.PortfolioId, balance.LeaseManagementId }
                        equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                    where balance.PortfolioId == portfolioId
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
                    where charge.PortfolioId == portfolioId
                        && authorizedProperties.Any(property => property.Id == lifecycle.PropertyId)
                        && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                        && charge.DueOn >= monthStartOn
                        && charge.DueOn < nextMonthStartOn
                    select (decimal?)(charge.OriginalAmount - charge.ReversedAmount)).Sum() ?? 0m,
                Income = accountingIncomeQuery.Sum(row => (decimal?)row.Amount) ?? 0m,
                Expense = accountingExpenseQuery.Sum(expense => (decimal?)expense.Amount) ?? 0m,
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

    private async Task<IReadOnlyList<DashboardExpiringLease>> BuildExpiringLeasesAsync(
        WorkspaceReadScope scope,
        CancellationToken ct)
    {
        var portfolioId = scope.PortfolioId;
        var authorizedProperties = AuthorizedProperties(scope, CapabilityKeys.RentalsRead);
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

        return expiring;
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
        // Statement 1 fixes the authorized activity boundary before entity resolution. Extracting
        // keys from this at-most-ten-row page is bounded orchestration, not business aggregation.
        var auditPage = await BuildRecentActivityAuditPageQuery(scope).ToListAsync(ct);
        var keySetsByType = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var row in auditPage)
        {
            if (!keySetsByType.TryGetValue(row.Audit.EntityType, out var entityIds))
            {
                entityIds = [];
                keySetsByType.Add(row.Audit.EntityType, entityIds);
            }

            entityIds.Add(row.Audit.EntityId);
        }

        var keysByType = new Dictionary<string, long[]>(keySetsByType.Count, StringComparer.Ordinal);
        foreach (var (entityType, entityIds) in keySetsByType)
        {
            keysByType.Add(entityType, [.. entityIds]);
        }

        // Statement 2 contains only branches represented on the page, with each base relation
        // constrained by its page keys before label and Unit-context joins are composed.
        var entityFacts = await BuildRecentActivityEntityFactQuery(scope, keysByType).ToListAsync(ct);
        var entityFactsByKey = new Dictionary<(string EntityType, long EntityId), DashboardActivityEntityReadRow>();
        foreach (var fact in entityFacts)
        {
            entityFactsByKey.TryAdd((fact.EntityType, fact.EntityId), fact);
        }

        return auditPage
            .Select(row =>
            {
                entityFactsByKey.TryGetValue((row.Audit.EntityType, row.Audit.EntityId), out var fact);
                return new DashboardActivity
                {
                    Id = row.Audit.Id,
                    Type = row.Audit.EntityType,
                    EntityId = row.Audit.EntityId,
                    UnitId = fact?.UnitId,
                    Action = row.Audit.Operation.ToString(),
                    Description = _auditDescriber.Describe(row.Audit),
                    Label = string.IsNullOrWhiteSpace(fact?.Label) ? null : fact!.Label!.Trim(),
                    Actor = AuditEntryResponse.ResolveActor(row.Audit, row.ResolvedActorName),
                    CreatedAt = row.Audit.Timestamp,
                };
            })
            .ToList();
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
            .WhereAuthorizedForReports(
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
                });
            Append(ref entityRows, applicationRows);
        }

        return entityRows ?? _db.Database.SqlQuery<DashboardActivityEntityReadRow>(
            $"""
            SELECT ''::text AS "EntityType", 0::bigint AS "EntityId",
                   NULL::text AS "Label", NULL::integer AS "UnitId"
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
