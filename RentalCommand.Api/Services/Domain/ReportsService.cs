using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IReportsService"/>
public class ReportsService : IReportsService
{
    /// <summary>IRS 1099-NEC reporting threshold per payee per year.</summary>
    private const decimal Vendor1099Threshold = 600m;

    private const int DefaultExpirationWindowDays = 90;

    private readonly RentalCommandDbContext _db;
    private readonly IOwnerStatementService _ownerStatements;
    private readonly IScheduleEService _scheduleE;

    public ReportsService(RentalCommandDbContext db, IOwnerStatementService ownerStatements, IScheduleEService scheduleE)
    {
        _db = db;
        _ownerStatements = ownerStatements;
        _scheduleE = scheduleE;
    }

    // ── Catalog ──────────────────────────────────────────────────────────────────────────────────

    public ReportsCatalogResponse GetCatalog()
    {
        return new ReportsCatalogResponse
        {
            Categories =
            [
                new ReportCategoryGroup
                {
                    Key = "accounting",
                    Title = "Accounting",
                    Reports =
                    [
                        new ReportCatalogEntry
                        {
                            Key = "income-expense-statement",
                            Title = "Income / Expense Statement (P&L)",
                            Description = "Profit & loss with monthly columns over the selected range.",
                            Endpoint = "/api/v1/reports/cash-flow",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "property-pnl-summary",
                            Title = "Property P&L Summary",
                            Description = "Income, expense, and net per property for the range.",
                            Endpoint = "/api/v1/reports/property-pnl",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "general-ledger",
                            Title = "General Ledger / Account Transactions",
                            Description = "Every payment and expense in date order with a running balance.",
                            Endpoint = "/api/v1/reports/general-ledger",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyId],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "cash-flow",
                            Title = "Cash Flow",
                            Description = "Money in vs. money out by month, with net.",
                            Endpoint = "/api/v1/reports/cash-flow",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "schedule-e",
                            Title = "Schedule E",
                            Description = "Per-property rental income and deductible expenses by IRS category.",
                            Endpoint = "/api/v1/accounting/schedule-e",
                            Params = [ReportParamKeys.Year],
                            External = true,
                        },
                        new ReportCatalogEntry
                        {
                            Key = "year-end-packet",
                            Title = "Year-End Packet",
                            Description = "The clean-books PDF you hand your accountant (Schedule E + P&L + cash flow + rent roll).",
                            Endpoint = "/api/v1/accounting/year-end-packet",
                            Params = [ReportParamKeys.Year],
                            External = true,
                        },
                    ],
                },
                new ReportCategoryGroup
                {
                    Key = "rent-payments",
                    Title = "Rent & Payments",
                    Reports =
                    [
                        new ReportCatalogEntry
                        {
                            Key = "rent-roll",
                            Title = "Rent Roll",
                            Description = "Current snapshot of every active lease: tenant, rent, deposit, term, status.",
                            Endpoint = "/api/v1/reports/rent-roll",
                            Params = [ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "rent-ledger",
                            Title = "Rent Ledger",
                            Description = "Per lease over the range: charges due vs. payments received, running balance.",
                            Endpoint = "/api/v1/reports/rent-ledger",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "delinquency",
                            Title = "Delinquency / Overdue Aging",
                            Description = "Outstanding balances aged into 0-30 / 31-60 / 61-90 / 90+ buckets.",
                            Endpoint = "/api/v1/reports/delinquency",
                            Params = [ReportParamKeys.PropertyIds],
                        },
                    ],
                },
                new ReportCategoryGroup
                {
                    Key = "owners",
                    Title = "Owners",
                    Reports =
                    [
                        new ReportCatalogEntry
                        {
                            Key = "owner-statement",
                            Title = "Owner Statement",
                            Description = "Full per-owner statement: income, expenses, management fee, net distribution.",
                            Endpoint = "/api/v1/accounting/owner-statement",
                            Params = [ReportParamKeys.OwnerId, ReportParamKeys.Year],
                            External = true,
                        },
                        new ReportCatalogEntry
                        {
                            Key = "owner-distributions",
                            Title = "Owner Distributions",
                            Description = "Net distribution per owner for the year, across all owners.",
                            Endpoint = "/api/v1/reports/owner-distributions",
                            Params = [ReportParamKeys.Year],
                        },
                    ],
                },
                new ReportCategoryGroup
                {
                    Key = "operations",
                    Title = "Operations",
                    Reports =
                    [
                        new ReportCatalogEntry
                        {
                            Key = "occupancy",
                            Title = "Occupancy / Vacancy",
                            Description = "Units total / occupied / vacant and occupancy % per property.",
                            Endpoint = "/api/v1/reports/occupancy",
                            Params = [ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "lease-expirations",
                            Title = "Lease Expirations / Renewals Due",
                            Description = "Leases ending within the next N days (default 90).",
                            Endpoint = "/api/v1/reports/lease-expirations",
                            Params = [ReportParamKeys.Days, ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "security-deposit-register",
                            Title = "Security Deposit Register",
                            Description = "Held / deductions / returned / current balance per lease.",
                            Endpoint = "/api/v1/reports/security-deposits",
                            Params = [ReportParamKeys.PropertyIds],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "vendor-1099",
                            Title = "Vendor 1099 & Payments",
                            Description = "Per 1099-eligible vendor: total paid this year, W-9 on file, needs review.",
                            Endpoint = "/api/v1/reports/vendor-1099",
                            Params = [ReportParamKeys.Year],
                        },
                        new ReportCatalogEntry
                        {
                            Key = "work-orders",
                            Title = "Work Orders / Maintenance",
                            Description = "Work orders requested in the range, with status counts and cost rollup.",
                            Endpoint = "/api/v1/reports/work-orders",
                            Params = [ReportParamKeys.From, ReportParamKeys.To, ReportParamKeys.PropertyIds],
                        },
                    ],
                },
            ],
        };
    }

    // ── Rent Roll ────────────────────────────────────────────────────────────────────────────────

    public async Task<RentRollResponse> GetRentRollAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        var q = _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId &&
                        (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven));

        if (propertyFilter is not null)
            q = q.Where(l => propertyFilter.Contains(l.PropertyId));

        var totals = await q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                LeaseCount = g.Count(),
                TotalMonthlyRent = g.Sum(l => l.MonthlyRent),
                TotalSecurityDeposit = g.Sum(l => l.SecurityDeposit),
            })
            .SingleOrDefaultAsync(ct);

        var rows = await q
            .OrderBy(l => l.Property!.Name)
            .ThenBy(l => l.Unit!.UnitNumber)
            .Select(l => new RentRollRow
            {
                LeaseId = l.Id,
                LeaseNumber = l.LeaseNumber,
                PropertyId = l.PropertyId,
                PropertyName = l.Property!.Name,
                UnitId = l.UnitId,
                UnitNumber = l.Unit!.UnitNumber,
                TenantId = l.TenantId,
                TenantName = (l.Tenant!.FirstName + " " + l.Tenant!.LastName).Trim(),
                MonthlyRent = l.MonthlyRent,
                SecurityDeposit = l.SecurityDeposit,
                StartDate = l.StartDate,
                EndDate = l.EndDate,
                Status = l.Status,
                StatusName = l.Status.ToString(),
            })
            .ToListAsync(ct);

        return new RentRollResponse
        {
            GeneratedAt = DateTime.UtcNow,
            Rows = rows,
            LeaseCount = totals?.LeaseCount ?? 0,
            TotalMonthlyRent = totals?.TotalMonthlyRent ?? 0m,
            TotalSecurityDeposit = totals?.TotalSecurityDeposit ?? 0m,
        };
    }

    // ── Rent Ledger (accrual, per lease over a range) ──────────────────────────────────────────────

    public async Task<RentLedgerResponse> GetRentLedgerAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query);
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        const int chargeEntryKind = 0;
        const int paymentEntryKind = 1;

        var paymentQuery = _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                        p.Lease!.Status != LeaseStatus.Draft &&
                        p.Lease.Status != LeaseStatus.Void);

        if (propertyFilter is not null)
            paymentQuery = paymentQuery.Where(p => propertyFilter.Contains(p.Lease!.PropertyId));

        // Charges: every payment due in the range (regardless of paid status) is a charge accrued on its
        // due date. Payments collected (PaidDate within the range, Paid status) reduce the balance.
        var chargeEntries = paymentQuery
            .Where(p =>
                        p.Status != PaymentStatus.Waived &&
                        p.Status != PaymentStatus.Failed &&
                        p.Status != PaymentStatus.Refunded &&
                        p.DueDate >= from && p.DueDate <= to)
            .Select(p => new RentLedgerQueryRow
            {
                LeaseId = p.LeaseId,
                LeaseNumber = p.Lease!.LeaseNumber,
                PropertyId = p.Lease.PropertyId,
                PropertyName = p.Lease.Property!.Name,
                UnitNumber = p.Lease.Unit!.UnitNumber,
                TenantName = (p.Lease.Tenant!.FirstName + " " + p.Lease.Tenant.LastName).Trim(),
                PaymentId = p.Id,
                Date = p.DueDate,
                EntryKind = chargeEntryKind,
                Amount = p.Amount,
                PaymentType = p.PaymentType,
            });

        var receiptEntries = paymentQuery
            .Where(p =>
                        p.Status == PaymentStatus.Paid &&
                        p.PaidDate != null &&
                        p.PaidDate >= from && p.PaidDate <= to)
            .Select(p => new RentLedgerQueryRow
            {
                LeaseId = p.LeaseId,
                LeaseNumber = p.Lease!.LeaseNumber,
                PropertyId = p.Lease.PropertyId,
                PropertyName = p.Lease.Property!.Name,
                UnitNumber = p.Lease.Unit!.UnitNumber,
                TenantName = (p.Lease.Tenant!.FirstName + " " + p.Lease.Tenant.LastName).Trim(),
                PaymentId = p.Id,
                Date = p.PaidDate!.Value,
                EntryKind = paymentEntryKind,
                Amount = p.Amount,
                PaymentType = p.PaymentType,
            });

        var activityQuery = chargeEntries.Concat(receiptEntries);

        var activityRows = await activityQuery
            .Select(r => new RentLedgerQueryRow
            {
                LeaseId = r.LeaseId,
                LeaseNumber = r.LeaseNumber,
                PropertyId = r.PropertyId,
                PropertyName = r.PropertyName,
                UnitNumber = r.UnitNumber,
                TenantName = r.TenantName,
                PaymentId = r.PaymentId,
                Date = r.Date,
                EntryKind = r.EntryKind,
                Amount = r.Amount,
                PaymentType = r.PaymentType,
                RunningBalance = activityQuery
                    .Where(x => x.LeaseId == r.LeaseId &&
                                (x.Date < r.Date ||
                                 (x.Date == r.Date && x.EntryKind < r.EntryKind) ||
                                 (x.Date == r.Date && x.EntryKind == r.EntryKind && x.PaymentId <= r.PaymentId)))
                    .Sum(x => (decimal?)(x.EntryKind == chargeEntryKind ? x.Amount : -x.Amount)) ?? 0m,
            })
            .OrderBy(r => r.PropertyName.ToLower())
            .ThenBy(r => r.PropertyName)
            .ThenBy(r => r.UnitNumber.ToLower())
            .ThenBy(r => r.UnitNumber)
            .ThenBy(r => r.LeaseId)
            .ThenBy(r => r.Date)
            .ThenBy(r => r.EntryKind)
            .ThenBy(r => r.PaymentId)
            .ToListAsync(ct);

        var totalsByLease = await activityQuery
            .GroupBy(r => r.LeaseId)
            .Select(g => new
            {
                LeaseId = g.Key,
                TotalCharged = g.Sum(r => r.EntryKind == chargeEntryKind ? r.Amount : 0m),
                TotalPaid = g.Sum(r => r.EntryKind == paymentEntryKind ? r.Amount : 0m),
            })
            .ToDictionaryAsync(r => r.LeaseId, r => new { r.TotalCharged, r.TotalPaid }, ct);

        var portfolioTotals = await activityQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalCharged = g.Sum(r => r.EntryKind == chargeEntryKind ? r.Amount : 0m),
                TotalPaid = g.Sum(r => r.EntryKind == paymentEntryKind ? r.Amount : 0m),
            })
            .SingleOrDefaultAsync(ct);

        var ledgerLeases = new List<RentLedgerLease>();
        RentLedgerLease? currentLease = null;
        List<RentLedgerEntry>? currentEntries = null;

        foreach (var e in activityRows)
        {
            if (currentLease is null || currentLease.LeaseId != e.LeaseId)
            {
                currentEntries = [];
                var totals = totalsByLease[e.LeaseId];
                currentLease = new RentLedgerLease
                {
                    LeaseId = e.LeaseId,
                    LeaseNumber = e.LeaseNumber,
                    PropertyId = e.PropertyId,
                    PropertyName = e.PropertyName,
                    UnitNumber = e.UnitNumber,
                    TenantName = e.TenantName,
                    Entries = currentEntries,
                    TotalCharged = totals.TotalCharged,
                    TotalPaid = totals.TotalPaid,
                    Balance = totals.TotalCharged - totals.TotalPaid,
                };
                ledgerLeases.Add(currentLease);
            }

            currentEntries!.Add(e.EntryKind == chargeEntryKind
                ? new RentLedgerEntry
                {
                    Date = e.Date,
                    Type = "Charge",
                    Description = $"{e.PaymentType} due",
                    Charge = e.Amount,
                    Payment = 0m,
                    Balance = e.RunningBalance,
                }
                : new RentLedgerEntry
                {
                    Date = e.Date,
                    Type = "Payment",
                    Description = $"{e.PaymentType} payment received",
                    Charge = 0m,
                    Payment = e.Amount,
                    Balance = e.RunningBalance,
                });
        }

        return new RentLedgerResponse
        {
            From = from,
            To = to,
            Leases = ledgerLeases,
            TotalCharged = portfolioTotals?.TotalCharged ?? 0m,
            TotalPaid = portfolioTotals?.TotalPaid ?? 0m,
            TotalBalance = (portfolioTotals?.TotalCharged ?? 0m) - (portfolioTotals?.TotalPaid ?? 0m),
        };
    }

    // ── Delinquency / Overdue Aging ────────────────────────────────────────────────────────────────

    public async Task<DelinquencyResponse> GetDelinquencyAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var asOf = DateTime.UtcNow;
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        // Owed-and-overdue, using the SAME predicate as AccountingService.GetPastDueAsync / the Money
        // pages so the delinquency report and those pages agree: still owed (Scheduled/Partial/Late) AND
        // either explicitly Late or with a DueDate before now. (Previously this used `DueDate < asOf`
        // only, which dropped a Late payment whose DueDate hadn't passed — diverging from the Money pages.)
        var owed = _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                        (p.Status == PaymentStatus.Scheduled ||
                         p.Status == PaymentStatus.Partial ||
                         p.Status == PaymentStatus.Late) &&
                        (p.Status == PaymentStatus.Late || p.DueDate < asOf));

        if (propertyFilter is not null)
            owed = owed.Where(p => propertyFilter.Contains(p.Lease!.PropertyId));

        // Aging by DueDate, computed SQL-side: each bucket is a conditional SUM gated on the DueDate's
        // age via fixed cutoff dates, and the owed amount is Partial-aware (a Partial owes only its
        // unpaid remainder; Scheduled/Late owe in full). One grouped round-trip per behind lease — no
        // payment rows are pulled back to bucket/total in memory. Cutoffs mirror AddToBucket's age
        // boundaries (0-30 / 31-60 / 61-90 / 90+); a Late payment whose DueDate is in the future ages to
        // 0 days (DueDate >= current cutoff) and lands in Current, matching AgeInDays' floor-at-0.
        var current = asOf.AddDays(-30);   // DueDate >= this  → 0-30 days
        var d60 = asOf.AddDays(-60);       // [d60, current)   → 31-60
        var d90 = asOf.AddDays(-90);       // [d90, d60)       → 61-90; < d90 → 90+

        var grouped = await owed
            .GroupBy(p => new
            {
                p.LeaseId,
                p.Lease!.LeaseNumber,
                p.Lease!.PropertyId,
                PropertyName = p.Lease!.Property!.Name,
                UnitNumber = p.Lease!.Unit!.UnitNumber,
                p.Lease!.TenantId,
                TenantFirst = p.Lease!.Tenant!.FirstName,
                TenantLast = p.Lease!.Tenant!.LastName,
            })
            .Select(g => new
            {
                g.Key,
                Current = g.Sum(p => p.DueDate >= current
                    ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount) : 0m),
                Days31To60 = g.Sum(p => p.DueDate < current && p.DueDate >= d60
                    ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount) : 0m),
                Days61To90 = g.Sum(p => p.DueDate < d60 && p.DueDate >= d90
                    ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount) : 0m),
                Over90 = g.Sum(p => p.DueDate < d90
                    ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount) : 0m),
                Total = g.Sum(p => p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount),
                OldestDueDate = g.Min(p => p.DueDate),
            })
            .OrderBy(g => g.OldestDueDate)
            .ThenByDescending(g => g.Total)
            .ToListAsync(ct);

        var totals = await owed
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Current = g.Sum(p => p.DueDate >= current
                    ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount) : 0m),
                Days31To60 = g.Sum(p => p.DueDate < current && p.DueDate >= d60
                    ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount) : 0m),
                Days61To90 = g.Sum(p => p.DueDate < d60 && p.DueDate >= d90
                    ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount) : 0m),
                Over90 = g.Sum(p => p.DueDate < d90
                    ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount) : 0m),
                TotalOutstanding = g.Sum(p => p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount),
            })
            .SingleOrDefaultAsync(ct);

        var rows = grouped
            .Select(g =>
            {
                var buckets = new DelinquencyBuckets
                {
                    Current = g.Current,
                    Days31To60 = g.Days31To60,
                    Days61To90 = g.Days61To90,
                    Over90 = g.Over90,
                };
                return new DelinquencyRow
                {
                    LeaseId = g.Key.LeaseId,
                    LeaseNumber = g.Key.LeaseNumber,
                    PropertyId = g.Key.PropertyId,
                    PropertyName = g.Key.PropertyName,
                    UnitNumber = g.Key.UnitNumber,
                    TenantId = g.Key.TenantId,
                    TenantName = $"{g.Key.TenantFirst} {g.Key.TenantLast}".Trim(),
                    Buckets = buckets,
                    Total = g.Total,
                    // Oldest age from the single per-lease Min(DueDate) — derived from an aggregate, not
                    // by scanning rows.
                    OldestOverdueDays = AgeInDays(g.OldestDueDate, asOf),
                };
            })
            .ToList();

        var totalBuckets = new DelinquencyBuckets
        {
            Current = totals?.Current ?? 0m,
            Days31To60 = totals?.Days31To60 ?? 0m,
            Days61To90 = totals?.Days61To90 ?? 0m,
            Over90 = totals?.Over90 ?? 0m,
        };

        return new DelinquencyResponse
        {
            AsOf = asOf,
            Rows = rows,
            Totals = totalBuckets,
            TotalOutstanding = totals?.TotalOutstanding ?? 0m,
        };
    }

    /// <summary>Whole days between a past due date and the as-of instant (floored at 0).</summary>
    internal static int AgeInDays(DateTime dueDate, DateTime asOf)
    {
        var days = (int)Math.Floor((asOf - dueDate).TotalDays);
        return days < 0 ? 0 : days;
    }

    /// <summary>Places an amount into the aging bucket for its age in days (0-30 / 31-60 / 61-90 / 90+).</summary>
    internal static void AddToBucket(DelinquencyBuckets buckets, int days, decimal amount)
    {
        if (days <= 30) buckets.Current += amount;
        else if (days <= 60) buckets.Days31To60 += amount;
        else if (days <= 90) buckets.Days61To90 += amount;
        else buckets.Over90 += amount;
    }

    // ── Cash Flow (income vs expense by month) ─────────────────────────────────────────────────────

    public async Task<CashFlowResponse> GetCashFlowAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query);
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        // Money in: income is ACTUAL CASH RECEIVED — Rent + LateFee that is Paid (full Amount) or
        // Partial (the collected AmountPaid). Security deposits are a liability, never income (§7/§18).
        // Cash is dated on PaidDate (falling back to DueDate). The range filter + monthly GROUP BY + SUM
        // all run SQL-side (this query was previously materialized + grouped in memory — a hard-rule
        // violation that §18 required rewriting; it is now one EF-translated aggregate).
        var incomeQuery = _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Partial) &&
                (p.PaymentType == PaymentType.Rent || p.PaymentType == PaymentType.LateFee) &&
                (p.PaidDate ?? p.DueDate) >= from && (p.PaidDate ?? p.DueDate) <= to);

        if (propertyFilter is not null)
            incomeQuery = incomeQuery.Where(p => propertyFilter.Contains(p.Lease!.PropertyId));

        var incomeByMonth = (await incomeQuery
            .GroupBy(p => new { (p.PaidDate ?? p.DueDate).Year, (p.PaidDate ?? p.DueDate).Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Total = g.Sum(p => p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount),
            })
            .ToListAsync(ct))
            .ToDictionary(r => (r.Year, r.Month), r => r.Total);

        // Money out: expenses, cash dated on PaidAt (falling back to IncurredAt). A property filter only
        // matches expenses tied to a property; unassigned expenses are excluded when a filter is set.
        // Range filter + monthly GROUP BY + SUM all run SQL-side.
        var expenseQuery = _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                (e.PaidAt ?? e.IncurredAt) >= from && (e.PaidAt ?? e.IncurredAt) <= to);

        if (propertyFilter is not null)
            expenseQuery = expenseQuery.Where(e => e.PropertyId != null && propertyFilter.Contains(e.PropertyId.Value));

        var expenseByMonth = (await expenseQuery
            .GroupBy(e => new { (e.PaidAt ?? e.IncurredAt).Year, (e.PaidAt ?? e.IncurredAt).Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Total = g.Sum(e => e.Amount) })
            .ToListAsync(ct))
            .ToDictionary(r => (r.Year, r.Month), r => r.Total);

        var totalIncome = await incomeQuery
            .SumAsync(p => (decimal?)(p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount), ct) ?? 0m;

        var totalExpense = await expenseQuery
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var months = EnumerateMonths(from, to)
            .Select(m =>
            {
                var income = incomeByMonth.GetValueOrDefault((m.Year, m.Month), 0m);
                var expense = expenseByMonth.GetValueOrDefault((m.Year, m.Month), 0m);
                return new CashFlowMonth
                {
                    Year = m.Year,
                    Month = m.Month,
                    MonthKey = $"{m.Year:D4}-{m.Month:D2}",
                    Label = $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(m.Month)} {m.Year}",
                    Income = income,
                    Expense = expense,
                    Net = income - expense,
                };
            })
            .ToList();

        return new CashFlowResponse
        {
            From = from,
            To = to,
            Months = months,
            TotalIncome = totalIncome,
            TotalExpense = totalExpense,
            TotalNet = totalIncome - totalExpense,
        };
    }

    /// <summary>Yields the (year, month) of every calendar month the [from, to] range touches, inclusive.</summary>
    internal static IEnumerable<(int Year, int Month)> EnumerateMonths(DateTime from, DateTime to)
    {
        var cursor = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(to.Year, to.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        while (cursor <= end)
        {
            yield return (cursor.Year, cursor.Month);
            cursor = cursor.AddMonths(1);
        }
    }

    // ── True cash flow (rent − opex − debt service), escrow-aware, DB-side (§9/§18) ─────────────────

    public async Task<CashFlowSummaryResponse> GetTrueCashFlowAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query);
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        var propertyQuery = _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId);
        if (propertyFilter is not null)
            propertyQuery = propertyQuery.Where(p => propertyFilter.Contains(p.Id));

        var rowQuery = propertyQuery
            .Select(p => new
            {
                p.Id,
                p.Name,
                Income = _db.Payments
                    .Where(pay =>
                        pay.PortfolioId == portfolioId &&
                        (pay.Status == PaymentStatus.Paid || pay.Status == PaymentStatus.Partial) &&
                        (pay.PaymentType == PaymentType.Rent || pay.PaymentType == PaymentType.LateFee) &&
                        pay.Lease != null &&
                        pay.Lease.PropertyId == p.Id &&
                        (pay.PaidDate ?? pay.DueDate) >= from && (pay.PaidDate ?? pay.DueDate) <= to)
                    .Sum(pay => (decimal?)(pay.Status == PaymentStatus.Partial ? (pay.AmountPaid ?? 0m) : pay.Amount)) ?? 0m,
                OperatingExpenses = _db.Expenses
                    .Where(e =>
                        e.PortfolioId == portfolioId &&
                        e.PropertyId == p.Id &&
                        (e.PaidAt ?? e.IncurredAt) >= from && (e.PaidAt ?? e.IncurredAt) <= to &&
                        !_db.Loans.Any(l =>
                            l.PropertyId == p.Id &&
                            l.Status == LoanStatus.Active &&
                            ((e.Category == ScheduleECategory.Taxes && l.EscrowCoversTaxes) ||
                             (e.Category == ScheduleECategory.Insurance && l.EscrowCoversInsurance))))
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
                DebtService = _db.LoanPayments
                    .Where(lp =>
                        lp.PortfolioId == portfolioId &&
                        lp.Loan != null &&
                        lp.Loan.PropertyId == p.Id &&
                        lp.DueDate >= from && lp.DueDate <= to)
                    .Sum(lp => (decimal?)lp.TotalAmount) ?? 0m,
            })
            .Where(r => r.Income != 0m || r.OperatingExpenses != 0m || r.DebtService != 0m);

        var rows = await rowQuery
            .OrderBy(r => r.Name)
            .Select(r => new PropertyCashFlow
            {
                PropertyId = r.Id,
                PropertyName = r.Name,
                Income = r.Income,
                OperatingExpenses = r.OperatingExpenses,
                Noi = r.Income - r.OperatingExpenses,
                DebtService = r.DebtService,
                CashFlow = r.Income - r.OperatingExpenses - r.DebtService,
            })
            .ToListAsync(ct);

        var totals = await rowQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalIncome = g.Sum(r => r.Income),
                TotalOperatingExpenses = g.Sum(r => r.OperatingExpenses),
                TotalDebtService = g.Sum(r => r.DebtService),
            })
            .SingleOrDefaultAsync(ct);

        var totalIncome = totals?.TotalIncome ?? 0m;
        var totalOperatingExpenses = totals?.TotalOperatingExpenses ?? 0m;
        var totalDebtService = totals?.TotalDebtService ?? 0m;
        var totalNoi = totalIncome - totalOperatingExpenses;

        return new CashFlowSummaryResponse
        {
            From = from,
            To = to,
            Properties = rows,
            TotalIncome = totalIncome,
            TotalOperatingExpenses = totalOperatingExpenses,
            TotalNoi = totalNoi,
            TotalDebtService = totalDebtService,
            TotalCashFlow = totalNoi - totalDebtService,
        };
    }

    // ── Year-end view: cash flow vs taxable income + rent roll (§11/§18) ────────────────────────────

    public async Task<YearEndViewResponse> GetYearEndAsync(int portfolioId, int year, int? propertyId = null, CancellationToken ct = default)
    {
        var yearRange = new ReportRangeQuery
        {
            From = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(year, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            PropertyIds = propertyId.HasValue ? [propertyId.Value] : null,
        };

        // Block 1: true cash flow (rent − opex − debt service, escrow-aware, no depreciation).
        var cashFlow = await GetTrueCashFlowAsync(portfolioId, yearRange, ct);

        // Block 2: taxable income / Schedule E (interest + depreciation in, principal + deposits out).
        var scheduleE = await _scheduleE.GetReportAsync(portfolioId, year, propertyId, ct);

        // Block 3: rent roll — current leases with their past-due balance (DB-side, no N+1).
        var rentRoll = await BuildRentRollAsync(portfolioId, propertyId, ct);

        // §18 "see your accountant" caveats — surfaced so the owner never trusts a number the model
        // does not compute. Conditional on what the data suggests, so they are actionable not noise.
        var notes = new List<string>();

        if (scheduleE.Properties.Any(p => p.DepreciationIsFirstYearEstimate))
            notes.Add("A property's first-year depreciation uses the IRS mid-month estimate — confirm the placed-in-service convention with your accountant.");

        if (scheduleE.NetIncome < 0m)
            notes.Add("Taxable income is negative — this is before the passive-loss limitation (Form 8582 / the $25k allowance); your deductible loss may be limited. See your accountant.");

        // Large amounts booked to Repairs may actually be capital improvements (which must be
        // depreciated, not expensed). Flag when any single property's repairs look large.
        var hasLargeRepairs = scheduleE.Properties
            .SelectMany(p => p.ExpensesByCategory)
            .Any(c => c.Category == ScheduleECategory.Repairs.ToString() && c.Amount >= 2_500m);
        if (hasLargeRepairs)
            notes.Add("Large amounts booked to Repairs may be capital improvements (IRS $2,500 de-minimis) that must be depreciated, not expensed. Review with your accountant.");

        notes.Add("Mid-year purchase or sale of a property (disposition: sale-year depreciation, gain/loss, and §1250 recapture) is NOT computed here.");
        notes.Add("Owner-occupied / mixed-use properties are not allocated — expenses and depreciation assume 100% rental use.");

        return new YearEndViewResponse
        {
            Year = year,
            CashFlow = cashFlow,
            ScheduleE = scheduleE,
            RentRoll = rentRoll,
            AccountantNotes = notes,
        };
    }

    /// <summary>
    /// Rent roll: current leases (active or under notice) with each lease's past-due balance, computed
    /// DB-side (the owed-and-overdue sum is grouped in SQL; no rows are pulled back to total in memory).
    /// </summary>
    private async Task<IReadOnlyList<YearEndRentRollRow>> BuildRentRollAsync(int portfolioId, int? propertyId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var leaseQuery = _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId &&
                        (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven));
        if (propertyId.HasValue)
            leaseQuery = leaseQuery.Where(l => l.PropertyId == propertyId.Value);

        var leases = await leaseQuery
            .OrderBy(l => l.Property!.Name)
            .ThenBy(l => l.Unit!.UnitNumber)
            .Select(l => new
            {
                l.Id,
                PropertyName = l.Property!.Name,
                UnitNumber = l.Unit!.UnitNumber,
                TenantFirstName = l.Tenant!.FirstName,
                TenantLastName = l.Tenant!.LastName,
                l.MonthlyRent,
                l.StartDate,
                l.EndDate,
                l.Status,
            })
            .ToListAsync(ct);

        if (leases.Count == 0)
            return [];

        // Past-due balance per lease: owed (Scheduled/Partial/Late) and overdue (Late, or due in the
        // past). A Partial owes only its unpaid remainder (Amount − AmountPaid). Grouped + summed SQL-side.
        var pastDueQuery = _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                        p.Lease != null &&
                        (p.Lease.Status == LeaseStatus.Active || p.Lease.Status == LeaseStatus.NoticeGiven) &&
                        (p.Status == PaymentStatus.Scheduled ||
                         p.Status == PaymentStatus.Partial ||
                         p.Status == PaymentStatus.Late) &&
                        (p.Status == PaymentStatus.Late || p.DueDate < now));
        if (propertyId.HasValue)
            pastDueQuery = pastDueQuery.Where(p => p.Lease!.PropertyId == propertyId.Value);

        var pastDueByLease = (await pastDueQuery
            .GroupBy(p => p.LeaseId)
            .Select(g => new
            {
                LeaseId = g.Key,
                Total = g.Sum(p => p.Status == PaymentStatus.Partial ? (p.Amount - (p.AmountPaid ?? 0m)) : p.Amount),
            })
            .ToListAsync(ct))
            .ToDictionary(g => g.LeaseId, g => g.Total);

        return leases
            .Select(l => new YearEndRentRollRow
            {
                PropertyName = l.PropertyName,
                UnitNumber = l.UnitNumber,
                TenantName = string.IsNullOrWhiteSpace($"{l.TenantFirstName} {l.TenantLastName}".Trim())
                    ? "Tenant"
                    : $"{l.TenantFirstName} {l.TenantLastName}".Trim(),
                MonthlyRent = l.MonthlyRent,
                LeaseStart = l.StartDate,
                LeaseEnd = l.EndDate,
                LeaseStatus = l.Status.ToString(),
                PastDueBalance = pastDueByLease.GetValueOrDefault(l.Id, 0m),
            })
            .ToList();
    }

    // ── General Ledger (running balance) ───────────────────────────────────────────────────────────

    public async Task<GeneralLedgerResponse> GetGeneralLedgerAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query);
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        // Income: paid payments dated on PaidDate (fall back to DueDate). Expense: expenses dated on
        // PaidAt (fall back to IncurredAt). Income is positive, expense negative; running balance is the
        // cumulative net.
        var paymentQuery = _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.Status == PaymentStatus.Paid);

        if (propertyFilter is not null)
            paymentQuery = paymentQuery.Where(p => propertyFilter.Contains(p.Lease!.PropertyId));

        var paymentEntries = paymentQuery
            .Select(p => new GeneralLedgerEntry
            {
                Date = p.PaidDate ?? p.DueDate,
                Type = "Payment",
                Id = p.Id,
                Description = p.PaymentType.ToString() + " — " + p.Lease!.Tenant!.FirstName + " " + p.Lease!.Tenant!.LastName,
                Category = p.PaymentType.ToString(),
                PropertyId = p.Lease!.PropertyId,
                PropertyName = p.Lease!.Property!.Name,
                Counterparty = p.Lease!.Tenant!.FirstName + " " + p.Lease!.Tenant!.LastName,
                Amount = p.Amount,
            });

        var expenseQuery = _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId);

        if (propertyFilter is not null)
            expenseQuery = expenseQuery.Where(e => e.PropertyId != null && propertyFilter.Contains(e.PropertyId.Value));

        var expenseEntries = expenseQuery
            .Select(e => new GeneralLedgerEntry
            {
                Date = e.PaidAt ?? e.IncurredAt,
                Type = "Expense",
                Id = e.Id,
                Description = e.Description,
                Category = e.Category.ToString(),
                PropertyId = e.PropertyId,
                PropertyName = e.Property != null ? e.Property.Name : null,
                Counterparty = e.Vendor != null ? e.Vendor.Name : null,
                Amount = -e.Amount,
            });

        var ledgerQuery = paymentEntries
            .Concat(expenseEntries)
            .Where(e => e.Date >= from && e.Date <= to);

        var entries = await ledgerQuery
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Type)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var totals = await ledgerQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalIncome = g.Sum(e => e.Amount > 0m ? e.Amount : 0m),
                TotalExpense = g.Sum(e => e.Amount < 0m ? -e.Amount : 0m),
            })
            .SingleOrDefaultAsync(ct);

        var running = 0m;
        foreach (var e in entries)
        {
            running += e.Amount;
            e.RunningBalance = running;
        }

        var totalIncome = totals?.TotalIncome ?? 0m;
        var totalExpense = totals?.TotalExpense ?? 0m;

        return new GeneralLedgerResponse
        {
            From = from,
            To = to,
            Entries = entries,
            TotalIncome = totalIncome,
            TotalExpense = totalExpense,
            ClosingBalance = totalIncome - totalExpense,
        };
    }

    // ── Property P&L Summary ───────────────────────────────────────────────────────────────────────

    public async Task<PropertyProfitAndLossResponse> GetPropertyProfitAndLossAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query);
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        var propertyQuery = _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId);

        if (propertyFilter is not null)
            propertyQuery = propertyQuery.Where(p => propertyFilter.Contains(p.Id));

        var properties = await propertyQuery
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var inScope = properties.Select(p => p.Id).ToArray();

        // Income: paid payments dated in range, keyed to property via lease.
        var incomeByProperty = inScope.Length == 0
            ? new Dictionary<int, decimal>()
            : (await _db.Payments
                .AsNoTracking()
                .Where(p => p.PortfolioId == portfolioId && p.Status == PaymentStatus.Paid)
                .Where(p => inScope.Contains(p.Lease!.PropertyId))
                .Where(p => (p.PaidDate ?? p.DueDate) >= from && (p.PaidDate ?? p.DueDate) <= to)
                .GroupBy(p => p.Lease!.PropertyId)
                .Select(g => new { PropertyId = g.Key, Total = g.Sum(p => p.Amount) })
                .ToListAsync(ct))
            .ToDictionary(r => r.PropertyId, r => r.Total);

        // Expense: expenses dated in range, keyed to property (unassigned ones never match a property).
        var expenseByProperty = inScope.Length == 0
            ? new Dictionary<int, decimal>()
            : (await _db.Expenses
                .AsNoTracking()
                .Where(e => e.PortfolioId == portfolioId && e.PropertyId != null)
                .Where(e => inScope.Contains(e.PropertyId!.Value))
                .Where(e => (e.PaidAt ?? e.IncurredAt) >= from && (e.PaidAt ?? e.IncurredAt) <= to)
                .GroupBy(e => e.PropertyId!.Value)
                .Select(g => new { PropertyId = g.Key, Total = g.Sum(e => e.Amount) })
                .ToListAsync(ct))
            .ToDictionary(r => r.PropertyId, r => r.Total);

        var totalIncome = inScope.Length == 0
            ? 0m
            : await _db.Payments
                .AsNoTracking()
                .Where(p => p.PortfolioId == portfolioId && p.Status == PaymentStatus.Paid)
                .Where(p => inScope.Contains(p.Lease!.PropertyId))
                .Where(p => (p.PaidDate ?? p.DueDate) >= from && (p.PaidDate ?? p.DueDate) <= to)
                .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        var totalExpense = inScope.Length == 0
            ? 0m
            : await _db.Expenses
                .AsNoTracking()
                .Where(e => e.PortfolioId == portfolioId && e.PropertyId != null)
                .Where(e => inScope.Contains(e.PropertyId!.Value))
                .Where(e => (e.PaidAt ?? e.IncurredAt) >= from && (e.PaidAt ?? e.IncurredAt) <= to)
                .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var rows = properties
            .Select(p =>
            {
                var income = incomeByProperty.GetValueOrDefault(p.Id, 0m);
                var expense = expenseByProperty.GetValueOrDefault(p.Id, 0m);
                return new PropertyProfitAndLossRow
                {
                    PropertyId = p.Id,
                    PropertyName = p.Name,
                    Income = income,
                    Expense = expense,
                    Net = income - expense,
                };
            })
            .ToList();

        return new PropertyProfitAndLossResponse
        {
            From = from,
            To = to,
            Rows = rows,
            TotalIncome = totalIncome,
            TotalExpense = totalExpense,
            TotalNet = totalIncome - totalExpense,
        };
    }

    // ── Occupancy / Vacancy ────────────────────────────────────────────────────────────────────────

    public async Task<OccupancyResponse> GetOccupancyAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        var propertyQuery = _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId);

        if (propertyFilter is not null)
            propertyQuery = propertyQuery.Where(p => propertyFilter.Contains(p.Id));

        // Per-property unit counts. "Occupied" = Unit.Status is Occupied; everything else (Vacant,
        // Reserved, Offline) counts as not-occupied for the vacancy split. Soft-deleted units are
        // excluded by the global query filter.
        var rows = await propertyQuery
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                TotalUnits = p.Units.Count,
                OccupiedUnits = p.Units.Count(u => u.Status == UnitStatus.Occupied),
            })
            .ToListAsync(ct);

        var occRows = rows
            .Select(p => new OccupancyRow
            {
                PropertyId = p.Id,
                PropertyName = p.Name,
                TotalUnits = p.TotalUnits,
                OccupiedUnits = p.OccupiedUnits,
                VacantUnits = p.TotalUnits - p.OccupiedUnits,
                OccupancyPercent = Percent(p.OccupiedUnits, p.TotalUnits),
            })
            .ToList();

        var totals = await propertyQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalUnits = g.Sum(p => p.Units.Count),
                OccupiedUnits = g.Sum(p => p.Units.Count(u => u.Status == UnitStatus.Occupied)),
            })
            .SingleOrDefaultAsync(ct);

        var totalUnits = totals?.TotalUnits ?? 0;
        var occupiedUnits = totals?.OccupiedUnits ?? 0;

        return new OccupancyResponse
        {
            GeneratedAt = DateTime.UtcNow,
            Rows = occRows,
            TotalUnits = totalUnits,
            OccupiedUnits = occupiedUnits,
            VacantUnits = totalUnits - occupiedUnits,
            OccupancyPercent = Percent(occupiedUnits, totalUnits),
        };
    }

    /// <summary>Occupancy percentage 0–100, rounded to 1 decimal. Returns 0 when there are no units.</summary>
    internal static decimal Percent(int occupied, int total) =>
        total == 0 ? 0m : Math.Round(occupied * 100m / total, 1);

    // ── Lease Expirations / Renewals Due ───────────────────────────────────────────────────────────

    public async Task<LeaseExpirationsResponse> GetLeaseExpirationsAsync(int portfolioId, ReportRangeQuery query, int days, CancellationToken ct = default)
    {
        var window = days > 0 ? days : DefaultExpirationWindowDays;
        var asOf = DateTime.UtcNow;
        var cutoff = asOf.AddDays(window);
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        var q = _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId &&
                        (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven) &&
                        l.EndDate <= cutoff);

        if (propertyFilter is not null)
            q = q.Where(l => propertyFilter.Contains(l.PropertyId));

        var summary = await q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                LeaseCount = g.Count(),
                TotalMonthlyRent = g.Sum(l => l.MonthlyRent),
            })
            .FirstOrDefaultAsync(ct);

        var leases = await q
            .OrderBy(l => l.EndDate)
            .Select(l => new
            {
                l.Id,
                l.LeaseNumber,
                l.PropertyId,
                PropertyName = l.Property!.Name,
                UnitNumber = l.Unit!.UnitNumber,
                l.TenantId,
                TenantFirst = l.Tenant!.FirstName,
                TenantLast = l.Tenant!.LastName,
                l.MonthlyRent,
                l.EndDate,
                l.Status,
            })
            .ToListAsync(ct);

        var rows = leases
            .Select(l => new LeaseExpirationRow
            {
                LeaseId = l.Id,
                LeaseNumber = l.LeaseNumber,
                PropertyId = l.PropertyId,
                PropertyName = l.PropertyName,
                UnitNumber = l.UnitNumber,
                TenantId = l.TenantId,
                TenantName = $"{l.TenantFirst} {l.TenantLast}".Trim(),
                MonthlyRent = l.MonthlyRent,
                EndDate = l.EndDate,
                DaysUntilExpiry = (int)Math.Ceiling((l.EndDate - asOf).TotalDays),
                Status = l.Status,
                StatusName = l.Status.ToString(),
            })
            .ToList();

        return new LeaseExpirationsResponse
        {
            AsOf = asOf,
            WindowDays = window,
            Rows = rows,
            LeaseCount = summary?.LeaseCount ?? 0,
            TotalMonthlyRent = summary?.TotalMonthlyRent ?? 0m,
        };
    }

    private sealed class RentLedgerQueryRow
    {
        public int LeaseId { get; set; }
        public string LeaseNumber { get; set; } = string.Empty;
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public string UnitNumber { get; set; } = string.Empty;
        public string TenantName { get; set; } = string.Empty;
        public int PaymentId { get; set; }
        public DateTime Date { get; set; }
        public int EntryKind { get; set; }
        public decimal Amount { get; set; }
        public PaymentType PaymentType { get; set; }
        public decimal RunningBalance { get; set; }
    }

    // ── Security Deposit Register ──────────────────────────────────────────────────────────────────

    public async Task<SecurityDepositRegisterResponse> GetSecurityDepositRegisterAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        var q = _db.SecurityDepositHoldings
            .AsNoTracking()
            .Where(h => h.PortfolioId == portfolioId);

        if (propertyFilter is not null)
            q = q.Where(h => propertyFilter.Contains(h.Lease!.PropertyId));

        var rowsQuery = q
            .Select(h => new
            {
                h.Id,
                h.LeaseId,
                LeaseNumber = h.Lease!.LeaseNumber,
                h.Lease!.PropertyId,
                PropertyName = h.Lease!.Property!.Name,
                UnitNumber = h.Lease!.Unit!.UnitNumber,
                TenantFirst = h.Lease!.Tenant!.FirstName,
                TenantLast = h.Lease!.Tenant!.LastName,
                h.Amount,
                h.DeductionsTotal,
                h.ReturnedAmount,
                h.Status,
                h.HeldAt,
                h.ReturnedAt,
                CurrentBalance = h.Amount - h.DeductionsTotal - (h.ReturnedAmount ?? 0m) < 0m
                    ? 0m
                    : h.Amount - h.DeductionsTotal - (h.ReturnedAmount ?? 0m),
            });

        var rows = await rowsQuery
            .OrderBy(h => h.PropertyName)
            .ThenBy(h => h.UnitNumber)
            .Select(h => new SecurityDepositRegisterRow
            {
                DepositId = h.Id,
                LeaseId = h.LeaseId,
                LeaseNumber = h.LeaseNumber,
                PropertyId = h.PropertyId,
                PropertyName = h.PropertyName,
                UnitNumber = h.UnitNumber,
                TenantName = (h.TenantFirst + " " + h.TenantLast).Trim(),
                Held = h.Amount,
                Deductions = h.DeductionsTotal,
                Returned = h.ReturnedAmount ?? 0m,
                CurrentBalance = h.CurrentBalance,
                Status = h.Status,
                StatusName = FormatSecurityDepositStatus(h.Status),
                HeldAt = h.HeldAt,
                ReturnedAt = h.ReturnedAt,
            })
            .ToListAsync(ct);

        var totals = await rowsQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalHeld = g.Sum(x => x.Amount),
                TotalDeductions = g.Sum(x => x.DeductionsTotal),
                TotalReturned = g.Sum(x => x.ReturnedAmount ?? 0m),
                TotalCurrentBalance = g.Sum(x => x.CurrentBalance),
            })
            .FirstOrDefaultAsync(ct);

        return new SecurityDepositRegisterResponse
        {
            GeneratedAt = DateTime.UtcNow,
            Rows = rows,
            TotalHeld = totals?.TotalHeld ?? 0m,
            TotalDeductions = totals?.TotalDeductions ?? 0m,
            TotalReturned = totals?.TotalReturned ?? 0m,
            TotalCurrentBalance = totals?.TotalCurrentBalance ?? 0m,
        };
    }

    private static string FormatSecurityDepositStatus(SecurityDepositStatus status) => status switch
    {
        SecurityDepositStatus.PartiallyReturned => "Partially Returned",
        _ => status.ToString(),
    };

    // ── Vendor 1099 & Payments ─────────────────────────────────────────────────────────────────────

    public async Task<Vendor1099Response> GetVendor1099Async(int portfolioId, int year, CancellationToken ct = default)
    {
        // Total paid per vendor = expenses to that vendor whose PaidAt falls in the year. Mirrors the
        // accounting reports' 1099 logic but scoped to a single tax year (the 1099 reporting period).
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd = yearStart.AddYears(1);

        var vendorRowsQuery = _db.Vendors
            .AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId &&
                        (v.Is1099Eligible || v.Expenses.Any(e => e.PaidAt >= yearStart && e.PaidAt < yearEnd)))
            .OrderBy(v => v.Name)
            .Select(v => new
            {
                v.Id,
                v.Name,
                v.TaxId,
                v.Is1099Eligible,
                v.W9OnFile,
                TotalPaid = v.Expenses
                    .Where(e => e.PaidAt >= yearStart && e.PaidAt < yearEnd)
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
            });

        var rows = await vendorRowsQuery
            .Select(v => new Vendor1099Row
            {
                VendorId = v.Id,
                VendorName = v.Name,
                TaxId = v.TaxId,
                TotalPaid = v.TotalPaid,
                Is1099Eligible = v.Is1099Eligible,
                W9OnFile = v.W9OnFile,
                NeedsW9 = v.Is1099Eligible && !v.W9OnFile,
                Needs1099Review = v.Is1099Eligible && v.TotalPaid >= Vendor1099Threshold,
            })
            .ToListAsync(ct);

        var totalPaid = await vendorRowsQuery
            .GroupBy(_ => 1)
            .Select(g => g.Sum(v => v.TotalPaid))
            .FirstOrDefaultAsync(ct);

        return new Vendor1099Response
        {
            Year = year,
            Rows = rows,
            TotalPaid = totalPaid,
            Threshold = Vendor1099Threshold,
        };
    }

    // ── Owner Distributions ────────────────────────────────────────────────────────────────────────

    public async Task<OwnerDistributionsResponse> GetOwnerDistributionsAsync(int portfolioId, int year, CancellationToken ct = default)
    {
        // Reuse OwnerStatementService's net-per-owner computation so distributions reconcile exactly with
        // the per-owner statement.
        var summaries = await _ownerStatements.ListOwnersWithNetAsync(portfolioId, year, ct);

        var rows = summaries
            .Select(s => new OwnerDistributionRow
            {
                OwnerId = s.OwnerId,
                OwnerName = s.OwnerName,
                NetToOwner = s.NetToOwner,
            })
            .ToList();

        return new OwnerDistributionsResponse
        {
            Year = year,
            Rows = rows,
            TotalNetToOwners = await _ownerStatements.GetTotalNetToOwnersAsync(portfolioId, year, ct),
        };
    }

    // ── Work Orders / Maintenance ──────────────────────────────────────────────────────────────────

    public async Task<WorkOrderReportResponse> GetWorkOrdersAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query);
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        var q = _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId &&
                        w.RequestedAt >= from && w.RequestedAt <= to);

        if (propertyFilter is not null)
            q = q.Where(w => propertyFilter.Contains(w.PropertyId));

        var rows = await q
            .OrderByDescending(w => w.RequestedAt)
            .Select(w => new WorkOrderReportRow
            {
                WorkOrderId = w.Id,
                PropertyId = w.PropertyId,
                PropertyName = w.Property!.Name,
                UnitNumber = w.Unit != null ? w.Unit.UnitNumber : null,
                Title = w.Title,
                Category = w.Category,
                Priority = w.Priority,
                PriorityName = w.Priority.ToString(),
                Status = w.Status,
                StatusName = w.Status.ToString(),
                VendorName = w.Vendor != null ? w.Vendor.Name : null,
                RequestedAt = w.RequestedAt,
                CompletedAt = w.CompletedAt,
                ActualCost = w.ActualCost,
            })
            .ToListAsync(ct);

        var summary = await q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalCount = g.Count(),
                OpenCount = g.Count(w =>
                    w.Status == WorkOrderStatus.New ||
                    w.Status == WorkOrderStatus.Scheduled ||
                    w.Status == WorkOrderStatus.InProgress ||
                    w.Status == WorkOrderStatus.WaitingParts ||
                    w.Status == WorkOrderStatus.OnHold),
                CompletedCount = g.Count(w => w.Status == WorkOrderStatus.Completed),
                TotalActualCost = g.Sum(w => w.ActualCost) ?? 0m,
            })
            .SingleOrDefaultAsync(ct);

        return new WorkOrderReportResponse
        {
            From = from,
            To = to,
            Rows = rows,
            TotalCount = summary?.TotalCount ?? 0,
            OpenCount = summary?.OpenCount ?? 0,
            CompletedCount = summary?.CompletedCount ?? 0,
            TotalActualCost = summary?.TotalActualCost ?? 0m,
        };
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the effective set of property ids to filter on, validated to be in-portfolio (IDOR guard),
    /// or <c>null</c> when no property filter was supplied (meaning "all properties in the portfolio").
    /// Accepts either a single <c>propertyId</c> or a <c>propertyIds</c> list; out-of-portfolio ids are
    /// silently dropped so a caller can never read another tenant's data by id.
    /// </summary>
    private async Task<HashSet<int>?> ResolvePropertyFilterAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct)
    {
        var requested = new HashSet<int>();
        if (query.PropertyId.HasValue)
            requested.Add(query.PropertyId.Value);
        if (query.PropertyIds is { Count: > 0 })
            foreach (var id in query.PropertyIds)
                requested.Add(id);

        if (requested.Count == 0)
            return null;

        // Keep only ids that actually belong to this portfolio.
        var valid = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && requested.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct);

        // If every requested id was out-of-portfolio, return an empty set so the report yields no rows
        // (rather than null, which would silently widen the scope to the whole portfolio).
        return valid.ToHashSet();
    }

    /// <summary>
    /// Resolves the [from, to] window for a range report, coercing both ends to UTC for Npgsql. Missing
    /// bounds default to a sensible window: <c>from</c> → start of the current year, <c>to</c> → now.
    /// The <c>to</c> end is inclusive (extended to end-of-day) so a same-day "from == to" still matches.
    /// </summary>
    internal static (DateTime From, DateTime To) ResolveRange(ReportRangeQuery query)
    {
        var now = DateTime.UtcNow;
        var from = (query.From?.ToUtc()) ?? new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toRaw = (query.To?.ToUtc()) ?? now;

        // Treat an explicit `to` as inclusive of the whole day when it is a midnight date.
        var to = query.To.HasValue && toRaw.TimeOfDay == TimeSpan.Zero
            ? toRaw.AddDays(1).AddTicks(-1)
            : toRaw;

        if (to < from)
            (from, to) = (to, from);

        return (from, to);
    }
}
