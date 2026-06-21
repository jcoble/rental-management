using System.Globalization;
using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly RentalCommandDbContext _db;
    private readonly IOwnerStatementService _ownerStatements;

    public ReportsService(RentalCommandDbContext db, IOwnerStatementService ownerStatements)
    {
        _db = db;
        _ownerStatements = ownerStatements;
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
            LeaseCount = rows.Count,
            TotalMonthlyRent = rows.Sum(r => r.MonthlyRent),
            TotalSecurityDeposit = rows.Sum(r => r.SecurityDeposit),
        };
    }

    // ── Rent Ledger (accrual, per lease over a range) ──────────────────────────────────────────────

    public async Task<RentLedgerResponse> GetRentLedgerAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var (from, to) = ResolveRange(query);
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        // Pull the leases in scope (any non-draft/void lease that can have rent activity), then their
        // payments within the range. A "charge" is the amount owed on a payment's DueDate; a "payment"
        // is the cash received on PaidDate. Tenant-ledger convention: charges add to the balance owed,
        // payments reduce it.
        var leaseQuery = _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId &&
                        l.Status != LeaseStatus.Draft && l.Status != LeaseStatus.Void);

        if (propertyFilter is not null)
            leaseQuery = leaseQuery.Where(l => propertyFilter.Contains(l.PropertyId));

        var leases = await leaseQuery
            .Select(l => new
            {
                l.Id,
                l.LeaseNumber,
                l.PropertyId,
                PropertyName = l.Property!.Name,
                UnitNumber = l.Unit!.UnitNumber,
                TenantName = (l.Tenant!.FirstName + " " + l.Tenant!.LastName).Trim(),
            })
            .ToListAsync(ct);

        var leaseIds = leases.Select(l => l.Id).ToHashSet();

        // Charges: every payment due in the range (regardless of paid status) is a charge accrued on its
        // due date. Payments collected (PaidDate within the range, Paid status) reduce the balance.
        var charges = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                        leaseIds.Contains(p.LeaseId) &&
                        p.Status != PaymentStatus.Waived &&
                        p.Status != PaymentStatus.Failed &&
                        p.Status != PaymentStatus.Refunded &&
                        p.DueDate >= from && p.DueDate <= to)
            .Select(p => new { p.LeaseId, p.Amount, Date = p.DueDate, p.PaymentType })
            .ToListAsync(ct);

        var receipts = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                        leaseIds.Contains(p.LeaseId) &&
                        p.Status == PaymentStatus.Paid &&
                        p.PaidDate != null &&
                        p.PaidDate >= from && p.PaidDate <= to)
            .Select(p => new { p.LeaseId, p.Amount, Date = p.PaidDate!.Value, p.PaymentType })
            .ToListAsync(ct);

        var chargesByLease = charges.GroupBy(c => c.LeaseId).ToDictionary(g => g.Key, g => g.ToList());
        var receiptsByLease = receipts.GroupBy(r => r.LeaseId).ToDictionary(g => g.Key, g => g.ToList());

        var ledgerLeases = new List<RentLedgerLease>(leases.Count);

        foreach (var lease in leases)
        {
            var entries = new List<(DateTime Date, bool IsCharge, decimal Amount, PaymentType Type)>();

            if (chargesByLease.TryGetValue(lease.Id, out var leaseCharges))
                entries.AddRange(leaseCharges.Select(c => (c.Date, true, c.Amount, c.PaymentType)));
            if (receiptsByLease.TryGetValue(lease.Id, out var leaseReceipts))
                entries.AddRange(leaseReceipts.Select(r => (r.Date, false, r.Amount, r.PaymentType)));

            if (entries.Count == 0)
                continue; // no activity in range → omit the lease to keep the ledger tight

            // Charge before payment on the same day so a same-day pay-as-charged nets to zero, not a
            // transient negative balance.
            var ordered = entries
                .OrderBy(e => e.Date)
                .ThenByDescending(e => e.IsCharge)
                .ToList();

            var rows = new List<RentLedgerEntry>(ordered.Count);
            var balance = 0m;
            var totalCharged = 0m;
            var totalPaid = 0m;

            foreach (var e in ordered)
            {
                if (e.IsCharge)
                {
                    balance += e.Amount;
                    totalCharged += e.Amount;
                    rows.Add(new RentLedgerEntry
                    {
                        Date = e.Date,
                        Type = "Charge",
                        Description = $"{e.Type} due",
                        Charge = e.Amount,
                        Payment = 0m,
                        Balance = balance,
                    });
                }
                else
                {
                    balance -= e.Amount;
                    totalPaid += e.Amount;
                    rows.Add(new RentLedgerEntry
                    {
                        Date = e.Date,
                        Type = "Payment",
                        Description = $"{e.Type} payment received",
                        Charge = 0m,
                        Payment = e.Amount,
                        Balance = balance,
                    });
                }
            }

            ledgerLeases.Add(new RentLedgerLease
            {
                LeaseId = lease.Id,
                LeaseNumber = lease.LeaseNumber,
                PropertyId = lease.PropertyId,
                PropertyName = lease.PropertyName,
                UnitNumber = lease.UnitNumber,
                TenantName = lease.TenantName,
                Entries = rows,
                TotalCharged = totalCharged,
                TotalPaid = totalPaid,
                Balance = totalCharged - totalPaid,
            });
        }

        ledgerLeases = ledgerLeases
            .OrderBy(l => l.PropertyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.UnitNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new RentLedgerResponse
        {
            From = from,
            To = to,
            Leases = ledgerLeases,
            TotalCharged = ledgerLeases.Sum(l => l.TotalCharged),
            TotalPaid = ledgerLeases.Sum(l => l.TotalPaid),
            TotalBalance = ledgerLeases.Sum(l => l.Balance),
        };
    }

    // ── Delinquency / Overdue Aging ────────────────────────────────────────────────────────────────

    public async Task<DelinquencyResponse> GetDelinquencyAsync(int portfolioId, ReportRangeQuery query, CancellationToken ct = default)
    {
        var asOf = DateTime.UtcNow;
        var propertyFilter = await ResolvePropertyFilterAsync(portfolioId, query, ct);

        // Outstanding = payments still owed (Scheduled/Partial/Late) whose due date is in the past.
        var owed = _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                        (p.Status == PaymentStatus.Scheduled ||
                         p.Status == PaymentStatus.Partial ||
                         p.Status == PaymentStatus.Late) &&
                        p.DueDate < asOf);

        if (propertyFilter is not null)
            owed = owed.Where(p => propertyFilter.Contains(p.Lease!.PropertyId));

        var overdue = await owed
            .Select(p => new
            {
                p.LeaseId,
                p.Amount,
                p.DueDate,
                p.Lease!.LeaseNumber,
                p.Lease!.PropertyId,
                PropertyName = p.Lease!.Property!.Name,
                UnitNumber = p.Lease!.Unit!.UnitNumber,
                p.Lease!.TenantId,
                TenantFirst = p.Lease!.Tenant!.FirstName,
                TenantLast = p.Lease!.Tenant!.LastName,
            })
            .ToListAsync(ct);

        var rows = overdue
            .GroupBy(p => p.LeaseId)
            .Select(g =>
            {
                var first = g.First();
                var buckets = new DelinquencyBuckets();
                var oldestDays = 0;

                foreach (var p in g)
                {
                    var days = AgeInDays(p.DueDate, asOf);
                    if (days > oldestDays) oldestDays = days;
                    AddToBucket(buckets, days, p.Amount);
                }

                var total = buckets.Current + buckets.Days31To60 + buckets.Days61To90 + buckets.Over90;

                return new DelinquencyRow
                {
                    LeaseId = first.LeaseId,
                    LeaseNumber = first.LeaseNumber,
                    PropertyId = first.PropertyId,
                    PropertyName = first.PropertyName,
                    UnitNumber = first.UnitNumber,
                    TenantId = first.TenantId,
                    TenantName = $"{first.TenantFirst} {first.TenantLast}".Trim(),
                    Buckets = buckets,
                    Total = total,
                    OldestOverdueDays = oldestDays,
                };
            })
            .OrderByDescending(r => r.OldestOverdueDays)
            .ThenByDescending(r => r.Total)
            .ToList();

        var totals = new DelinquencyBuckets
        {
            Current = rows.Sum(r => r.Buckets.Current),
            Days31To60 = rows.Sum(r => r.Buckets.Days31To60),
            Days61To90 = rows.Sum(r => r.Buckets.Days61To90),
            Over90 = rows.Sum(r => r.Buckets.Over90),
        };

        return new DelinquencyResponse
        {
            AsOf = asOf,
            Rows = rows,
            Totals = totals,
            TotalOutstanding = rows.Sum(r => r.Total),
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
            TotalIncome = months.Sum(m => m.Income),
            TotalExpense = months.Sum(m => m.Expense),
            TotalNet = months.Sum(m => m.Net),
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

        var payments = await paymentQuery
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
            })
            .ToListAsync(ct);

        var expenseQuery = _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId);

        if (propertyFilter is not null)
            expenseQuery = expenseQuery.Where(e => e.PropertyId != null && propertyFilter.Contains(e.PropertyId.Value));

        var expenses = await expenseQuery
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
            })
            .ToListAsync(ct);

        var entries = payments
            .Concat(expenses)
            .Where(e => e.Date >= from && e.Date <= to)
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Type)
            .ThenBy(e => e.Id)
            .ToList();

        var running = 0m;
        foreach (var e in entries)
        {
            running += e.Amount;
            e.RunningBalance = running;
        }

        var totalIncome = entries.Where(e => e.Amount > 0).Sum(e => e.Amount);
        var totalExpense = entries.Where(e => e.Amount < 0).Sum(e => -e.Amount);

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

        var inScope = properties.Select(p => p.Id).ToHashSet();

        // Income: paid payments dated in range, keyed to property via lease.
        var incomeRows = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.Status == PaymentStatus.Paid)
            .Select(p => new { PropertyId = p.Lease!.PropertyId, p.Amount, When = p.PaidDate ?? p.DueDate })
            .ToListAsync(ct);

        var incomeByProperty = incomeRows
            .Where(r => inScope.Contains(r.PropertyId) && r.When >= from && r.When <= to)
            .GroupBy(r => r.PropertyId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        // Expense: expenses dated in range, keyed to property (unassigned ones never match a property).
        var expenseRows = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId && e.PropertyId != null)
            .Select(e => new { PropertyId = e.PropertyId!.Value, e.Amount, When = e.PaidAt ?? e.IncurredAt })
            .ToListAsync(ct);

        var expenseByProperty = expenseRows
            .Where(r => inScope.Contains(r.PropertyId) && r.When >= from && r.When <= to)
            .GroupBy(r => r.PropertyId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

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
            TotalIncome = rows.Sum(r => r.Income),
            TotalExpense = rows.Sum(r => r.Expense),
            TotalNet = rows.Sum(r => r.Net),
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

        var totalUnits = occRows.Sum(r => r.TotalUnits);
        var occupiedUnits = occRows.Sum(r => r.OccupiedUnits);

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
            LeaseCount = rows.Count,
            TotalMonthlyRent = rows.Sum(r => r.MonthlyRent),
        };
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

        var holdings = await q
            .OrderBy(h => h.Lease!.Property!.Name)
            .ThenBy(h => h.Lease!.Unit!.UnitNumber)
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
                h.DeductionsJson,
                h.ReturnedAmount,
                h.Status,
                h.HeldAt,
                h.ReturnedAt,
            })
            .ToListAsync(ct);

        var rows = holdings
            .Select(h =>
            {
                var deductions = ParseDeductionsTotal(h.DeductionsJson);
                var returned = h.ReturnedAmount ?? 0m;
                var current = Math.Max(0m, h.Amount - deductions - returned);

                return new SecurityDepositRegisterRow
                {
                    DepositId = h.Id,
                    LeaseId = h.LeaseId,
                    LeaseNumber = h.LeaseNumber,
                    PropertyId = h.PropertyId,
                    PropertyName = h.PropertyName,
                    UnitNumber = h.UnitNumber,
                    TenantName = $"{h.TenantFirst} {h.TenantLast}".Trim(),
                    Held = h.Amount,
                    Deductions = deductions,
                    Returned = returned,
                    CurrentBalance = current,
                    Status = h.Status,
                    StatusName = h.Status.ToString(),
                    HeldAt = h.HeldAt,
                    ReturnedAt = h.ReturnedAt,
                };
            })
            .ToList();

        return new SecurityDepositRegisterResponse
        {
            GeneratedAt = DateTime.UtcNow,
            Rows = rows,
            TotalHeld = rows.Sum(r => r.Held),
            TotalDeductions = rows.Sum(r => r.Deductions),
            TotalReturned = rows.Sum(r => r.Returned),
            TotalCurrentBalance = rows.Sum(r => r.CurrentBalance),
        };
    }

    /// <summary>Sums the deduction amounts in a holding's DeductionsJson; returns 0 on null/blank/malformed JSON.</summary>
    internal static decimal ParseDeductionsTotal(string? deductionsJson)
    {
        if (string.IsNullOrWhiteSpace(deductionsJson))
            return 0m;

        try
        {
            var deductions = JsonSerializer.Deserialize<List<DepositDeduction>>(deductionsJson, JsonOpts);
            return deductions?.Sum(d => d.Amount) ?? 0m;
        }
        catch (JsonException)
        {
            return 0m;
        }
    }

    // ── Vendor 1099 & Payments ─────────────────────────────────────────────────────────────────────

    public async Task<Vendor1099Response> GetVendor1099Async(int portfolioId, int year, CancellationToken ct = default)
    {
        // Total paid per vendor = expenses to that vendor whose PaidAt falls in the year. Mirrors the
        // accounting reports' 1099 logic but scoped to a single tax year (the 1099 reporting period).
        var vendors = await _db.Vendors
            .AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId)
            .OrderBy(v => v.Name)
            .Select(v => new
            {
                v.Id,
                v.Name,
                v.TaxId,
                v.Is1099Eligible,
                v.W9OnFile,
                TotalPaid = v.Expenses
                    .Where(e => e.PaidAt != null && e.PaidAt!.Value.Year == year)
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
            })
            .ToListAsync(ct);

        var rows = vendors
            .Where(v => v.Is1099Eligible || v.TotalPaid > 0m)
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
            .ToList();

        return new Vendor1099Response
        {
            Year = year,
            Rows = rows,
            TotalPaid = rows.Sum(r => r.TotalPaid),
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
            TotalNetToOwners = rows.Sum(r => r.NetToOwner),
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

        var open = rows.Count(r => r.Status is WorkOrderStatus.New or WorkOrderStatus.Scheduled
            or WorkOrderStatus.InProgress or WorkOrderStatus.WaitingParts or WorkOrderStatus.OnHold);
        var completed = rows.Count(r => r.Status == WorkOrderStatus.Completed);

        return new WorkOrderReportResponse
        {
            From = from,
            To = to,
            Rows = rows,
            TotalCount = rows.Count,
            OpenCount = open,
            CompletedCount = completed,
            TotalActualCost = rows.Sum(r => r.ActualCost ?? 0m),
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
