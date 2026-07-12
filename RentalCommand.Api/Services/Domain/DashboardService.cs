using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

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

    public async Task<DashboardResponse?> GetDashboardAsync(int portfolioId, CancellationToken ct = default)
    {
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
            Occupancy = await BuildOccupancyAsync(portfolioId, ct),
            Accounting = await BuildAccountingAsync(portfolioId, monthStart, nextMonthStart, ct),
            Maintenance = await BuildMaintenanceAsync(portfolioId, ct),
            Leasing = await BuildLeasingAsync(portfolioId, ct),
            RecentActivity = await BuildRecentActivityAsync(portfolioId, ct),
            UpcomingAppointments = await BuildUpcomingAppointmentsAsync(portfolioId, now, ct),
        };
    }

    private async Task<DashboardOccupancy> BuildOccupancyAsync(int portfolioId, CancellationToken ct)
    {
        // The database projection is the single source of occupancy truth. Possession and operational
        // periods determine these counts; mutable Unit.Status and legacy Lease.Status are not consulted.
        var counts = await _db.UnitOccupancyProjections
            .AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId)
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
        int portfolioId, DateTime monthStart, DateTime nextMonthStart, CancellationToken ct)
    {
        var monthStartOn = DateOnly.FromDateTime(monthStart);
        var nextMonthStartOn = DateOnly.FromDateTime(nextMonthStart);

        // One PostgreSQL statement derives the entire tenant-money portion of the dashboard from
        // canonical facts. Current receivable attention is lifecycle-scoped; billed charges and
        // allocations are immutable; partial payment is reflected by the balance views; and cash
        // collected remains historical even after move-out. Security-deposit receipts are excluded
        // through their typed subledger provenance because held deposits are liabilities, not income.
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
                        && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                        && charge.DueOn >= monthStartOn
                        && charge.DueOn < nextMonthStartOn
                    select (decimal?)(charge.OriginalAmount - charge.ReversedAmount)).Sum() ?? 0m,
                PaidThisMonth = _db.TenantLedgerEntries
                    .AsNoTracking()
                    .Where(entry => entry.PortfolioId == portfolio.Id
                        && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                        && entry.EffectiveOn >= monthStartOn
                        && entry.EffectiveOn < nextMonthStartOn
                        && !_db.SecurityDepositEntries.Any(deposit =>
                            deposit.PortfolioId == portfolio.Id
                            && deposit.TenantLedgerEntryId == entry.Id
                            && deposit.EntryType == SecurityDepositEntryType.Receipt))
                    .Sum(entry => (decimal?)entry.Amount) ?? 0m,
            })
            .SingleAsync(ct);

        var bankCash = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId
                && t.MatchStatus != "Removed"
                && t.PostedAt >= monthStart
                && t.PostedAt < nextMonthStart)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                UnmatchedDeposits = g.Sum(t => t.Amount > 0 && t.MatchedTenantLedgerEntryId == null ? t.Amount : 0m),
                UnmatchedWithdrawals = g.Sum(t => t.Amount < 0 && t.MatchedExpenseId == null ? -t.Amount : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var overdue = tenantMoney.Overdue;
        var dueThisMonth = tenantMoney.DueThisMonth;
        var paidThisMonth = tenantMoney.PaidThisMonth + (bankCash?.UnmatchedDeposits ?? 0m);

        // Expenses spent this month (paid date when present, else incurred date), matching the
        // dashboard money snapshot so the summary KPI and the detailed money card cannot diverge.
        var expenseRowsThisMonth = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId
                && (e.PaidAt ?? e.IncurredAt) >= monthStart
                && (e.PaidAt ?? e.IncurredAt) < nextMonthStart)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;
        var expensesThisMonth = expenseRowsThisMonth + (bankCash?.UnmatchedWithdrawals ?? 0m);

        return new DashboardAccounting
        {
            DueThisMonthAmount = dueThisMonth,
            PaidThisMonthAmount = paidThisMonth,
            OverdueAmount = overdue,
            ExpensesThisMonthAmount = expensesThisMonth,
            NetThisMonth = paidThisMonth - expensesThisMonth,
        };
    }

    private async Task<DashboardMaintenance> BuildMaintenanceAsync(int portfolioId, CancellationToken ct)
    {
        // "Open" = any work order that is not Completed/Cancelled/Archived. The three counts (open,
        // emergency-among-open, in-progress-among-open) are computed SQL-side: the open predicate scopes
        // the query, then a single grouped aggregate emits the conditional counts. No work-order rows are
        // pulled into memory.
        var counts = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId
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

    private async Task<DashboardLeasing> BuildLeasingAsync(int portfolioId, CancellationToken ct)
    {
        var byStatusRaw = await _db.LeaseManagementLifecycleProjections
            .AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId)
            .GroupBy(row => row.Lifecycle)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        var byStatus = byStatusRaw.ToDictionary(group => group.Status, group => group.Count);
        var leaseCounts = await _db.LeaseManagementLifecycleProjections
            .AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId)
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

    private async Task<IReadOnlyList<DashboardActivity>> BuildRecentActivityAsync(int portfolioId, CancellationToken ct)
    {
        // The dashboard "recent activity" widget reads the unified audit trail. The humanized
        // description (via AuditDescriber) is the verb+noun; Label names the specific record it touched
        // and EntityId lets the web deep-link to it. Action carries the raw operation name.
        var rows = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId)
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .Take(10)
            .ToListAsync(ct);

        var refs = await ResolveActivityRefsAsync(rows, portfolioId, ct);

        return rows
            .Select(a => new DashboardActivity
            {
                Id = a.Id,
                Type = a.EntityType,
                EntityId = a.EntityId,
                UnitId = refs.GetValueOrDefault((a.EntityType, a.EntityId))?.UnitId,
                Action = a.Operation.ToString(),
                Description = _auditDescriber.Describe(a),
                Label = refs.GetValueOrDefault((a.EntityType, a.EntityId))?.Label,
                Actor = a.ActorLabel ?? (a.UserId.HasValue ? $"User #{a.UserId.Value}" : "system"),
                CreatedAt = a.Timestamp,
            })
            .ToList();
    }

    /// <summary>
    /// Resolves a display label and owning unit for each audit row's touched entity. The dashboard page
    /// has already fetched at most 10 audit rows, and each present type issues exactly ONE batched,
    /// portfolio-scoped, projected query over its distinct ids (set-based — never one query per row).
    /// Types with no cheap DB-side label/context are left unmapped and the caller falls back to the
    /// verb-only description. Every query filters by <paramref name="portfolioId"/> as a cross-tenant guard.
    /// </summary>
    private async Task<Dictionary<(string EntityType, int EntityId), ActivityRefRow>> ResolveActivityRefsAsync(
        IReadOnlyList<AuditLog> rows, int portfolioId, CancellationToken ct)
    {
        var refs = new Dictionary<(string, int), ActivityRefRow>();
        if (rows.Count == 0)
        {
            return refs;
        }

        // Distinct ids per entity type across the bounded page. Each present type drives one IN-query below.
        List<int> Ids(string type)
        {
            var ids = new List<int>();
            foreach (var row in rows)
            {
                if (row.EntityType == type && !ids.Contains(row.EntityId))
                {
                    ids.Add(row.EntityId);
                }
            }

            return ids;
        }

        // Record each (type,id) → label + unit context from one already-projected, set-based query.
        async Task AddAsync(string type, IQueryable<ActivityRefRow> projected)
        {
            foreach (var row in await projected.ToListAsync(ct))
            {
                if (!string.IsNullOrWhiteSpace(row.Label) || row.UnitId is > 0)
                {
                    row.Label = string.IsNullOrWhiteSpace(row.Label) ? null : row.Label.Trim();
                    refs[(type, row.Id)] = row;
                }
            }
        }

        // --- One batched, portfolio-scoped query per present entity type ---

        if (Ids("Tenant") is { Count: > 0 } tenantIds)
        {
            await AddAsync("Tenant", _db.Tenants.AsNoTracking()
                .Where(t => tenantIds.Contains(t.Id) && t.PortfolioId == portfolioId)
                .Select(t => new ActivityRefRow { Id = t.Id, Label = t.FirstName + " " + t.LastName }));
        }

        if (Ids("Property") is { Count: > 0 } propertyIds)
        {
            await AddAsync("Property", _db.Properties.AsNoTracking()
                .Where(p => propertyIds.Contains(p.Id) && p.PortfolioId == portfolioId)
                .Select(p => new ActivityRefRow { Id = p.Id, Label = p.Name }));
        }

        // Units carry their portfolio via the parent property; scope and label through the Property
        // join so it stays a single set-based SQL statement.
        if (Ids("Unit") is { Count: > 0 } unitIds)
        {
            await AddAsync("Unit", _db.Units.AsNoTracking()
                .Where(u => unitIds.Contains(u.Id) && u.Property!.PortfolioId == portfolioId)
                .Select(u => new ActivityRefRow { Id = u.Id, Label = u.Property!.Name + " · Unit " + u.UnitNumber, UnitId = u.Id }));
        }

        if (Ids(nameof(LeaseManagement)) is { Count: > 0 } relationshipIds)
        {
            await AddAsync(nameof(LeaseManagement), _db.LeaseManagements.AsNoTracking()
                .Where(relationship => relationshipIds.Contains(relationship.Id)
                    && relationship.PortfolioId == portfolioId)
                .Select(relationship => new ActivityRefRow
                {
                    Id = relationship.Id,
                    Label = relationship.RelationshipNumber,
                    UnitId = relationship.UnitId,
                }));
        }

        if (Ids(nameof(LeaseAgreement)) is { Count: > 0 } agreementIds)
        {
            await AddAsync(nameof(LeaseAgreement), _db.LeaseAgreements.AsNoTracking()
                .Where(agreement => agreementIds.Contains(agreement.Id)
                    && agreement.PortfolioId == portfolioId)
                .Select(agreement => new ActivityRefRow
                {
                    Id = agreement.Id,
                    Label = agreement.AgreementNumber,
                    UnitId = agreement.LeaseManagement!.UnitId,
                }));
        }

        if (Ids("WorkOrder") is { Count: > 0 } workOrderIds)
        {
            await AddAsync("WorkOrder", _db.WorkOrders.AsNoTracking()
                .Where(w => workOrderIds.Contains(w.Id) && w.PortfolioId == portfolioId)
                .Select(w => new ActivityRefRow { Id = w.Id, Label = w.Title, UnitId = w.UnitId }));
        }

        if (Ids("Expense") is { Count: > 0 } expenseIds)
        {
            await AddAsync("Expense", _db.Expenses.AsNoTracking()
                .Where(e => expenseIds.Contains(e.Id) && e.PortfolioId == portfolioId)
                .Select(e => new ActivityRefRow
                {
                    Id = e.Id,
                    Label = e.Description,
                    UnitId = e.UnitId ?? (e.WorkOrder != null ? e.WorkOrder.UnitId : null),
                }));
        }

        if (Ids("Vendor") is { Count: > 0 } vendorIds)
        {
            await AddAsync("Vendor", _db.Vendors.AsNoTracking()
                .Where(v => vendorIds.Contains(v.Id) && v.PortfolioId == portfolioId)
                .Select(v => new ActivityRefRow { Id = v.Id, Label = v.Name }));
        }

        if (Ids("OwnerEntity") is { Count: > 0 } ownerIds)
        {
            await AddAsync("OwnerEntity", _db.OwnerEntities.AsNoTracking()
                .Where(o => ownerIds.Contains(o.Id) && o.PortfolioId == portfolioId)
                .Select(o => new ActivityRefRow { Id = o.Id, Label = o.Name }));
        }

        if (Ids("Appointment") is { Count: > 0 } appointmentIds)
        {
            await AddAsync("Appointment", _db.Appointments.AsNoTracking()
                .Where(a => appointmentIds.Contains(a.Id) && a.PortfolioId == portfolioId)
                .Select(a => new ActivityRefRow { Id = a.Id, Label = a.Title, UnitId = a.UnitId }));
        }

        // Inspections have no title of their own; name them by the property they belong to.
        if (Ids("Inspection") is { Count: > 0 } inspectionIds)
        {
            await AddAsync("Inspection", _db.Inspections.AsNoTracking()
                .Where(i => inspectionIds.Contains(i.Id) && i.PortfolioId == portfolioId)
                .Select(i => new ActivityRefRow { Id = i.Id, Label = i.Property!.Name, UnitId = i.UnitId }));
        }

        if (Ids("RentalApplication") is { Count: > 0 } applicationIds)
        {
            await AddAsync("RentalApplication", _db.RentalApplications.AsNoTracking()
                .Where(r => applicationIds.Contains(r.Id) && r.PortfolioId == portfolioId)
                .Select(r => new ActivityRefRow { Id = r.Id, Label = r.FirstName + " " + r.LastName, UnitId = r.UnitId }));
        }

        // Tenant-money commands audit the continuous account. Resolve the account number and Unit in one
        // canonical projection so scan-originated receipts and ordinary account mutations share a label.
        if (Ids(nameof(TenantAccount)) is { Count: > 0 } accountIds)
        {
            await AddAsync(nameof(TenantAccount),
                BuildTenantAccountActivityRefsQuery(portfolioId, accountIds));
        }

        if (Ids("SecurityDeposit") is { Count: > 0 } depositIds)
        {
            var found = await (
                    from account in _db.SecurityDepositAccounts.AsNoTracking()
                    join balance in _db.SecurityDepositBalanceProjections.AsNoTracking()
                        on new { account.PortfolioId, SecurityDepositAccountId = account.Id }
                        equals new { balance.PortfolioId, balance.SecurityDepositAccountId }
                    where depositIds.Contains(account.Id) && account.PortfolioId == portfolioId
                    select new { account.Id, Amount = balance.HeldBalance })
                .ToListAsync(ct);
            foreach (var d in found)
            {
                refs[("SecurityDeposit", d.Id)] = new ActivityRefRow
                {
                    Id = d.Id,
                    Label = Money(d.Amount),
                };
            }
        }

        return refs;
    }

    internal IQueryable<ActivityRefRow> BuildTenantAccountActivityRefsQuery(
        int portfolioId, IReadOnlyList<int> accountIds) =>
        _db.TenantAccounts.AsNoTracking()
            .Where(account => accountIds.Contains(account.Id) && account.PortfolioId == portfolioId)
            .Select(account => new ActivityRefRow
            {
                Id = account.Id,
                Label = account.AccountNumber,
                UnitId = account.LeaseManagement!.UnitId,
            });

    private static string Money(decimal amount) =>
        "$" + amount.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>Projection holder for a batched activity lookup (<c>Id</c> + label + owning unit).</summary>
    internal sealed class ActivityRefRow
    {
        public int Id { get; set; }
        public string? Label { get; set; }
        public int? UnitId { get; set; }
    }

    private async Task<IReadOnlyList<DashboardAppointment>> BuildUpcomingAppointmentsAsync(
        int portfolioId, DateTime now, CancellationToken ct)
    {
        var appointments = await _db.Appointments
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId
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
}
