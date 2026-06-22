using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAccountingService"/>
public class AccountingService : IAccountingService
{
    private const decimal Vendor1099Threshold = 600m;
    private const string KindExpense = "Expense";
    private const string KindPayment = "Payment";
    private const string KindBank = "Bank";

    private readonly RentalCommandDbContext _db;
    private readonly IScheduleEService _scheduleE;
    private readonly IYearEndPacketPdfGenerator _packetPdf;

    public AccountingService(
        RentalCommandDbContext db,
        IScheduleEService scheduleE,
        IYearEndPacketPdfGenerator packetPdf)
    {
        _db = db;
        _scheduleE = scheduleE;
        _packetPdf = packetPdf;
    }

    public async Task<AccountingSummaryResponse> GetSummaryAsync(int portfolioId, CancellationToken ct = default)
    {
        // Expense totals grouped by Schedule E category (soft-deleted expenses are excluded by the
        // global query filter).
        var categoryGroups = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId)
            .GroupBy(e => e.Category)
            .Select(g => new
            {
                Category = g.Key,
                Total = g.Sum(e => e.Amount),
                Count = g.Count(),
            })
            .ToListAsync(ct);

        var expensesByCategory = categoryGroups
            .OrderByDescending(g => g.Total)
            .Select(g => new ScheduleECategoryTotal
            {
                Category = g.Category,
                CategoryName = g.Category.ToString(),
                Total = g.Total,
                Count = g.Count,
            })
            .ToList();

        var unmatchedBankWithdrawals = await _db.BankTransactions
            .AsNoTracking()
            .Where(t =>
                t.PortfolioId == portfolioId &&
                t.MatchStatus != "Removed" &&
                t.Amount < 0 &&
                t.MatchedExpenseId == null)
            .SumAsync(t => (decimal?)-t.Amount, ct) ?? 0m;

        var totalExpenses = categoryGroups.Sum(g => g.Total) + unmatchedBankWithdrawals;

        // Payment collection rollup. "Outstanding" is anything not yet collected/written off; "overdue"
        // is the subset of that which is past its due date. All four figures are computed SQL-side as
        // conditional SUM/COUNT aggregates in a single grouped round-trip — no payment rows are loaded.
        var now = DateTime.UtcNow;
        var rollupRaw = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                // Collected: Paid contributes its full Amount; a Partial contributes only what's been
                // paid so far (AmountPaid). Both summed SQL-side.
                Collected = g.Sum(p =>
                    p.Status == PaymentStatus.Paid ? p.Amount
                    : p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m)
                    : 0m),
                // Owed = Scheduled/Partial/Late (Waived/Failed/Refunded are not money to collect). A
                // Partial only owes its unpaid remainder (Amount − AmountPaid); Scheduled/Late owe in full.
                Outstanding = g.Sum(p =>
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Late) ? p.Amount
                    : p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m)
                    : 0m),
                Overdue = g.Sum(p =>
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial || p.Status == PaymentStatus.Late)
                    && (p.Status == PaymentStatus.Late || p.DueDate < now)
                        ? (p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount)
                        : 0m),
                OverdueCount = g.Count(p =>
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial || p.Status == PaymentStatus.Late)
                    && (p.Status == PaymentStatus.Late || p.DueDate < now)),
            })
            .FirstOrDefaultAsync(ct);

        var rollup = new PaymentRollup
        {
            Collected = rollupRaw?.Collected ?? 0m,
            Outstanding = rollupRaw?.Outstanding ?? 0m,
            Overdue = rollupRaw?.Overdue ?? 0m,
            OverdueCount = rollupRaw?.OverdueCount ?? 0,
        };

        var unmatchedBankDeposits = await _db.BankTransactions
            .AsNoTracking()
            .Where(t =>
                t.PortfolioId == portfolioId &&
                t.MatchStatus != "Removed" &&
                t.Amount > 0 &&
                t.MatchedPaymentId == null)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;
        rollup.Collected += unmatchedBankDeposits;

        return new AccountingSummaryResponse
        {
            PortfolioId = portfolioId,
            ExpensesByCategory = expensesByCategory,
            TotalExpenses = totalExpenses,
            Payments = rollup,
            Snapshot = BuildMoneySnapshot(rollup, totalExpenses, expensesByCategory),
        };
    }

    public async Task<MoneySnapshotResponse> GetSnapshotAsync(int portfolioId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var last30Start = now.AddDays(-30);

        // Money in: payments actually collected. "Collected" means Status == Paid AND a real PaidDate
        // (the date the cash landed) — a row marked Paid but lacking a PaidDate is not yet collected and
        // must NOT count, otherwise scheduled/expected rent would inflate money-in by its due date. This
        // matches the DashboardService "PaidThisMonth" KPI (Paid + PaidDate in period) so the two figures
        // can't disagree. Bank deposits not yet matched to a payment also count as money in, so the
        // snapshot reflects real cash movement. Both period figures (month-to-date and trailing 30 days)
        // are computed SQL-side as conditional SUMs in one grouped round-trip per source — no rows are
        // loaded into memory.
        // A Partial payment's collected cash also lands on its PaidDate, so it counts as money-in for the
        // period — contributing its AmountPaid (not its full Amount). Paid contributes the full Amount.
        var paymentsCollected = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId
                && (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Partial)
                && p.PaidDate != null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Mtd = g.Sum(p => p.PaidDate >= monthStart
                    ? (p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount) : 0m),
                Last30 = g.Sum(p => p.PaidDate >= last30Start
                    ? (p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount) : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var depositsCollected = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.MatchStatus != "Removed" &&
                        t.Amount > 0 && t.MatchedPaymentId == null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Mtd = g.Sum(t => t.PostedAt >= monthStart ? t.Amount : 0m),
                Last30 = g.Sum(t => t.PostedAt >= last30Start ? t.Amount : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var collectedMtd = (paymentsCollected?.Mtd ?? 0m) + (depositsCollected?.Mtd ?? 0m);
        var collected30 = (paymentsCollected?.Last30 ?? 0m) + (depositsCollected?.Last30 ?? 0m);

        // Money out: expenses (paid date when present, else incurred date) plus unmatched bank
        // withdrawals — same approach as the summary, kept period-scoped.
        var expensesSpent = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Mtd = g.Sum(e => (e.PaidAt ?? e.IncurredAt) >= monthStart ? e.Amount : 0m),
                Last30 = g.Sum(e => (e.PaidAt ?? e.IncurredAt) >= last30Start ? e.Amount : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var withdrawalsSpent = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.MatchStatus != "Removed" &&
                        t.Amount < 0 && t.MatchedExpenseId == null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Mtd = g.Sum(t => t.PostedAt >= monthStart ? -t.Amount : 0m),
                Last30 = g.Sum(t => t.PostedAt >= last30Start ? -t.Amount : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var spentMtd = (expensesSpent?.Mtd ?? 0m) + (withdrawalsSpent?.Mtd ?? 0m);
        var spent30 = (expensesSpent?.Last30 ?? 0m) + (withdrawalsSpent?.Last30 ?? 0m);

        // Past due: anyone behind right now (not period-bound). The amount and the distinct-lease count
        // (= tenants behind) come from the SAME per-lease grouped query that powers the "Who's behind"
        // list (GetPastDueAsync), so this KPI can never disagree with the destination row count.
        // Both figures are derived SQL-side; no payment rows are loaded to count.
        var pastDueGroups = await PastDueByLeaseQuery(portfolioId, now).ToListAsync(ct);
        var pastDueAmount = pastDueGroups.Sum(g => g.PastDueAmount);
        var pastDueCount = pastDueGroups.Count;

        var netMtd = collectedMtd - spentMtd;
        var net30 = collected30 - spent30;

        return new MoneySnapshotResponse
        {
            PortfolioId = portfolioId,
            PeriodLabel = $"{monthStart:MMMM yyyy} (so far)",
            PeriodStart = monthStart,
            PeriodEnd = now,
            Collected = collectedMtd,
            Spent = spentMtd,
            Net = netMtd,
            PastDueAmount = pastDueAmount,
            PastDueCount = pastDueCount,
            CollectedLast30Days = collected30,
            SpentLast30Days = spent30,
            NetLast30Days = net30,
            Explanations = BuildSnapshotExplanations(collectedMtd, spentMtd, netMtd, pastDueAmount, pastDueCount),
        };
    }

    public async Task<PastDueResponse> GetPastDueAsync(int portfolioId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // One grouped round-trip: per behind lease, sum the past-due amount, count its past-due
        // payments, and find the oldest past-due due date — the single source of truth for "behind".
        var groups = await PastDueByLeaseQuery(portfolioId, now)
            .OrderBy(g => g.OldestDueDate)
            .ToListAsync(ct);

        if (groups.Count == 0)
        {
            return new PastDueResponse { Items = [], TotalCount = 0, TotalPastDueAmount = 0m };
        }

        var leaseIds = groups.Select(g => g.LeaseId).ToList();

        // Second round-trip: the id of each lease's oldest past-due payment (for the row deep-link).
        var oldestPaymentIds = await PastDuePaymentsQuery(portfolioId, now)
            .Where(p => leaseIds.Contains(p.LeaseId))
            .GroupBy(p => p.LeaseId)
            .Select(g => new
            {
                LeaseId = g.Key,
                // Oldest by due date, then by id for a stable pick when due dates tie.
                OldestPaymentId = g.OrderBy(p => p.DueDate).ThenBy(p => p.Id).Select(p => p.Id).First(),
            })
            .ToDictionaryAsync(x => x.LeaseId, x => x.OldestPaymentId, ct);

        // Third round-trip: lease/tenant/property/unit labels for the rows.
        var leaseMeta = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId && leaseIds.Contains(l.Id))
            .Select(l => new
            {
                l.Id,
                l.LeaseNumber,
                TenantName = l.Tenant == null ? null : (l.Tenant.FirstName + " " + l.Tenant.LastName),
                TenantPhone = l.Tenant == null ? null : l.Tenant.Phone,
                PropertyName = l.Property == null ? null : l.Property.Name,
                UnitNumber = l.Unit == null ? null : l.Unit.UnitNumber,
            })
            .ToDictionaryAsync(x => x.Id, ct);

        var items = groups
            .Select(g =>
            {
                leaseMeta.TryGetValue(g.LeaseId, out var meta);
                return new PastDueLeaseResponse
                {
                    LeaseId = g.LeaseId,
                    TenantName = string.IsNullOrWhiteSpace(meta?.TenantName) ? null : meta!.TenantName!.Trim(),
                    TenantPhone = meta?.TenantPhone,
                    LeaseNumber = meta?.LeaseNumber,
                    PropertyName = meta?.PropertyName,
                    UnitNumber = meta?.UnitNumber,
                    PastDueAmount = g.PastDueAmount,
                    OverduePaymentCount = g.OverduePaymentCount,
                    OldestDueDate = g.OldestDueDate,
                    OldestPaymentId = oldestPaymentIds.GetValueOrDefault(g.LeaseId),
                };
            })
            .ToList();

        return new PastDueResponse
        {
            Items = items,
            TotalCount = items.Count,
            TotalPastDueAmount = items.Sum(i => i.PastDueAmount),
        };
    }

    /// <summary>
    /// The canonical "is past due" payment predicate, shared by the snapshot KPI, the "Who's behind"
    /// list, and the per-property overdue rollup so they never diverge: a payment is past due when it
    /// is still owed (Scheduled/Partial/Late) AND is either explicitly Late or has a DueDate before
    /// <paramref name="now"/>. Defined once here as the single source of truth.
    /// </summary>
    private IQueryable<Payment> PastDuePaymentsQuery(int portfolioId, DateTime now) => _db.Payments
        .AsNoTracking()
        .Where(p => p.PortfolioId == portfolioId &&
                    (p.Status == PaymentStatus.Scheduled || p.Status == PaymentStatus.Partial || p.Status == PaymentStatus.Late) &&
                    (p.Status == PaymentStatus.Late || p.DueDate < now));

    /// <summary>
    /// The past-due payments rolled up per lease (one group = one behind tenant). The distinct-lease
    /// count of this query is the KPI's "tenants behind", its summed amount is the KPI's past-due
    /// amount, and its rows are the "Who's behind" list — all from one definition, computed SQL-side.
    /// </summary>
    private IQueryable<PastDueLeaseGroup> PastDueByLeaseQuery(int portfolioId, DateTime now) =>
        PastDuePaymentsQuery(portfolioId, now)
            .GroupBy(p => p.LeaseId)
            .Select(g => new PastDueLeaseGroup
            {
                LeaseId = g.Key,
                // A Partial past-due payment only owes its unpaid remainder (Amount − AmountPaid);
                // Scheduled/Late owe in full. Summed SQL-side.
                PastDueAmount = g.Sum(p =>
                    p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount),
                OverduePaymentCount = g.Count(),
                OldestDueDate = g.Min(p => p.DueDate),
            });

    private sealed class PastDueLeaseGroup
    {
        public int LeaseId { get; set; }
        public decimal PastDueAmount { get; set; }
        public int OverduePaymentCount { get; set; }
        public DateTime OldestDueDate { get; set; }
    }

    private static MoneySnapshotExplanations BuildSnapshotExplanations(
        decimal collected, decimal spent, decimal net, decimal pastDueAmount, int pastDueCount)
    {
        var netExplanation = net >= 0
            ? $"You're keeping {Money(net)} this month after {Money(spent)} of expenses."
            : $"You spent {Money(-net)} more than you collected this month, after {Money(spent)} of expenses.";

        var pastDueExplanation = pastDueCount == 0
            ? "Everyone is caught up — no rentals are behind right now."
            : $"{pastDueCount} rental{(pastDueCount == 1 ? " is" : "s are")} behind, owing {Money(pastDueAmount)} in total.";

        return new MoneySnapshotExplanations
        {
            Collected = $"You collected {Money(collected)} in rent and other payments this month.",
            Spent = $"You spent {Money(spent)} on expenses this month.",
            Net = netExplanation,
            PastDue = pastDueExplanation,
        };
    }

    private static MoneySnapshotCardResponse BuildMoneySnapshot(
        PaymentRollup rollup,
        decimal totalExpenses,
        IReadOnlyList<ScheduleECategoryTotal> expensesByCategory)
    {
        var netCollectedAfterExpenses = rollup.Collected - totalExpenses;
        var topExpense = expensesByCategory.OrderByDescending(c => c.Total).FirstOrDefault();
        var title = rollup.Overdue > 0
            ? "Overdue rent needs attention"
            : rollup.Outstanding > 0
                ? "Rent is not fully collected yet"
                : "Books are current";

        var bullets = new List<string>
        {
            $"{Money(rollup.Collected)} collected against {Money(totalExpenses)} in expenses.",
            $"{Money(netCollectedAfterExpenses)} net collected after expenses.",
        };

        if (rollup.Overdue > 0)
        {
            bullets.Add($"{Money(rollup.Overdue)} is overdue across {rollup.OverdueCount} payment{(rollup.OverdueCount == 1 ? "" : "s")}.");
        }
        else if (rollup.Outstanding > 0)
        {
            bullets.Add($"{Money(rollup.Outstanding)} is still scheduled or partially outstanding.");
        }
        else
        {
            bullets.Add("No overdue rent is currently showing in accounting.");
        }

        if (topExpense != null)
        {
            bullets.Add($"{topExpense.CategoryName} is the largest expense bucket at {Money(topExpense.Total)}.");
        }

        return new MoneySnapshotCardResponse
        {
            Title = title,
            Summary = rollup.Overdue > 0
                ? $"Follow up on overdue rent first, then review the largest expense bucket before owner reporting."
                : $"Cash collection is {Money(rollup.Collected)} with {Money(totalExpenses)} in recorded expenses.",
            Bullets = bullets,
        };
    }

    private static string Money(decimal value) => value.ToString("$#,0.##;$-#,0.##;$0");

    public async Task<AccountingTransactionsResponse> GetTransactionsAsync(
        int portfolioId,
        AccountingTransactionsQuery query,
        CancellationToken ct = default)
    {
        // Source the unified ledger from the vw_accounting_transactions Postgres view (keyless entity)
        // so the whole filter → sort → page runs as ONE SQL statement against the DB, rather than
        // materializing Payments/Expenses/BankTransactions and merging in memory. RLS + soft-delete
        // are enforced inside the view; we still apply the app-layer portfolio scope here.
        IQueryable<AccountingTransactionView> rows = _db.AccountingTransactionViews
            .AsNoTracking()
            .Where(r => r.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            var kind = query.Kind.Trim();
            if (kind.Equals(KindExpense, StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(r => r.Kind == KindExpense);
            }
            else if (kind.Equals(KindPayment, StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(r => r.Kind == KindPayment);
            }
            else if (kind.Equals(KindBank, StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(r => r.Kind == KindBank);
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            // Case-insensitive exact match DB-side via ILIKE (no wildcards). Postgres-native; the
            // grid always runs against Postgres.
            var status = query.Status.Trim();
            rows = rows.Where(r => EF.Functions.ILike(r.Status, status));
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = query.Category.Trim();
            rows = rows.Where(r => EF.Functions.ILike(r.Category, category));
        }

        if (query.PropertyId.HasValue)
        {
            rows = rows.Where(r => r.PropertyId == query.PropertyId.Value);
        }

        if (query.From.HasValue)
        {
            var from = query.From.Value.ToUtc();
            rows = rows.Where(r => r.Date >= from);
        }

        if (query.To.HasValue)
        {
            var to = query.To.Value.ToUtc();
            rows = rows.Where(r => r.Date < to.AddDays(1));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // DB-side case-insensitive contains via Postgres ILIKE %term% (Npgsql translates to the
            // native ILIKE operator) over the view's text columns — the same convention the other
            // domain services use. The grid is Postgres-only, so no provider-portability compromise.
            var like = $"%{query.Search.Trim()}%";
            rows = rows.Where(r =>
                EF.Functions.ILike(r.Description, like) ||
                (r.PropertyName != null && EF.Functions.ILike(r.PropertyName, like)) ||
                (r.Counterparty != null && EF.Functions.ILike(r.Counterparty, like)) ||
                (r.Reference != null && EF.Functions.ILike(r.Reference, like)) ||
                (r.Notes != null && EF.Functions.ILike(r.Notes, like)));
        }

        rows = query.SortField switch
        {
            "amount" => query.SortDescending
                ? rows.OrderByDescending(r => r.Amount).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.Amount).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id),
            "description" => query.SortDescending
                ? rows.OrderByDescending(r => r.Description).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.Description).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id),
            "category" => query.SortDescending
                ? rows.OrderByDescending(r => r.Category).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.Category).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id),
            "status" => query.SortDescending
                ? rows.OrderByDescending(r => r.Status).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.Status).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id),
            "kind" => query.SortDescending
                ? rows.OrderByDescending(r => r.Kind).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.Kind).ThenByDescending(r => r.Date).ThenByDescending(r => r.Id),
            "date" => query.SortDescending
                ? rows.OrderByDescending(r => r.Date).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.Date).ThenBy(r => r.Id),
            "createdat" => query.SortDescending
                ? rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id),
            "updatedat" => query.SortDescending
                ? rows.OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.UpdatedAt).ThenBy(r => r.Id),
            // Default: newest-entered first, so a just-scanned item lands at the top of the ledger
            // even when its transaction date is wrong/old.
            _ => rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id),
        };

        var totalCount = await rows.CountAsync(ct);
        var pageRows = await rows
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);

        var expenseIds = pageRows
            .Where(r => r.Kind == KindExpense)
            .Select(r => r.Id)
            .ToList();

        var filesByExpenseId = await _db.StoredFiles
            .AsNoTracking()
            .Where(f =>
                f.PortfolioId == portfolioId &&
                f.EntityType == "Expense" &&
                f.EntityId != null &&
                expenseIds.Contains(f.EntityId.Value) &&
                f.DeletedAt == null)
            .GroupBy(f => f.EntityId!.Value)
            .Select(g => new { EntityId = g.Key, ContentType = g.OrderByDescending(f => f.UploadedAt).First().ContentType })
            .ToDictionaryAsync(x => x.EntityId, x => x.ContentType, ct);

        var reconciliation = await BuildReconciliationAsync(portfolioId, pageRows, ct);

        return new AccountingTransactionsResponse
        {
            Items = pageRows.Select(r =>
            {
                var item = new AccountingTransactionResponse
                {
                    Kind = r.Kind,
                    Id = r.Id,
                    Date = r.Date,
                    CreatedAt = r.CreatedAt,
                    UpdatedAt = r.UpdatedAt,
                    Description = r.Description,
                    Category = r.Category,
                    Status = r.Status,
                    Amount = r.Amount,
                    PropertyId = r.PropertyId,
                    PropertyName = r.PropertyName,
                    Counterparty = r.Counterparty,
                    DetailHref = DetailHrefFor(r.Kind, r.Id),
                };

                if (r.Kind == KindExpense && filesByExpenseId.TryGetValue(r.Id, out var contentType))
                {
                    item.HasReceipt = true;
                    item.ReceiptIsImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                }

                if ((r.Kind == KindPayment || r.Kind == KindExpense) &&
                    reconciliation.TryGetValue((r.Kind, r.Id), out var recon))
                {
                    item.Reconciled = recon.Reconciled;
                    item.ClearedBankName = recon.ClearedBankName;
                    item.ClearedAt = recon.ClearedAt;
                    item.SuggestedBankMatch = recon.Suggested;
                }

                return item;
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    /// <summary>The grid row's deep-link, derived from its source kind + id (was a view column).</summary>
    private static string DetailHrefFor(string kind, int id) => kind switch
    {
        KindPayment => $"/accounting/payments/{id}",
        KindExpense => $"/accounting/expenses/{id}",
        _ => "/banking",
    };

    /// <summary>
    /// For the Payment/Expense rows on the current page, look up their bank-reconciliation state in a
    /// single pair of queries (no N+1): a CONFIRMED match (a Matched bank line linked to the row) wins
    /// and yields "✓ Cleared · {bank} · {date}"; otherwise a high-confidence still-unmatched bank line
    /// is surfaced as a one-tap "Match?" suggestion. Nothing is auto-matched here.
    /// </summary>
    private async Task<Dictionary<(string Kind, int Id), ReconciliationState>> BuildReconciliationAsync(
        int portfolioId,
        IReadOnlyList<AccountingTransactionView> pageRows,
        CancellationToken ct)
    {
        var result = new Dictionary<(string, int), ReconciliationState>();

        var paymentRows = pageRows.Where(r => r.Kind == KindPayment).ToList();
        var expenseRows = pageRows.Where(r => r.Kind == KindExpense).ToList();
        if (paymentRows.Count == 0 && expenseRows.Count == 0) return result;

        var paymentIds = paymentRows.Select(r => r.Id).ToHashSet();
        var expenseIds = expenseRows.Select(r => r.Id).ToHashSet();

        // 1) Confirmed (Matched) bank lines that link to a row on this page → "cleared".
        var cleared = await _db.BankTransactions
            .AsNoTracking()
            .Where(t =>
                t.PortfolioId == portfolioId &&
                t.MatchStatus == "Matched" &&
                ((t.MatchedPaymentId != null && paymentIds.Contains(t.MatchedPaymentId.Value)) ||
                 (t.MatchedExpenseId != null && expenseIds.Contains(t.MatchedExpenseId.Value))))
            .Select(t => new
            {
                t.MatchedPaymentId,
                t.MatchedExpenseId,
                t.PostedAt,
                InstitutionName = t.BankConnection!.InstitutionName,
            })
            .ToListAsync(ct);

        foreach (var c in cleared)
        {
            if (c.MatchedPaymentId is int pid && paymentIds.Contains(pid))
            {
                result[(KindPayment, pid)] = new ReconciliationState
                {
                    Reconciled = true,
                    ClearedBankName = c.InstitutionName,
                    ClearedAt = c.PostedAt,
                };
            }
            else if (c.MatchedExpenseId is int eid && expenseIds.Contains(eid))
            {
                result[(KindExpense, eid)] = new ReconciliationState
                {
                    Reconciled = true,
                    ClearedBankName = c.InstitutionName,
                    ClearedAt = c.PostedAt,
                };
            }
        }

        // 2) For rows not already cleared, suggest a still-unmatched bank line. Bound the bank candidate
        // lookup by the page rows' amount and date hard gates in SQL, then apply the shared name/date
        // scoring in memory.
        var openPaymentRows = paymentRows
            .Where(p => !result.ContainsKey((KindPayment, p.Id)))
            .ToList();
        var openExpenseRows = expenseRows
            .Where(e => !result.ContainsKey((KindExpense, e.Id)))
            .ToList();

        if (openPaymentRows.Count == 0 && openExpenseRows.Count == 0) return result;

        var unmatchedQuery = _db.BankTransactions
            .AsNoTracking()
            .Where(t =>
                t.PortfolioId == portfolioId &&
                t.MatchStatus == "Unmatched" &&
                t.MatchedPaymentId == null &&
                t.MatchedExpenseId == null);

        var unmatched = new List<BankSuggestionCandidate>();

        if (openPaymentRows.Count > 0)
        {
            var minAmount = openPaymentRows.Min(p => p.Amount) - 0.01m;
            var maxAmount = openPaymentRows.Max(p => p.Amount) + 0.01m;
            var minDate = openPaymentRows.Min(p => p.Date.Date).AddDays(-14);
            var maxDateExclusive = openPaymentRows.Max(p => p.Date.Date).AddDays(15);

            unmatched.AddRange(await unmatchedQuery
                .Where(t =>
                    t.Amount > 0m &&
                    t.Amount >= minAmount &&
                    t.Amount <= maxAmount &&
                    t.PostedAt >= minDate &&
                    t.PostedAt < maxDateExclusive)
                .Select(t => new BankSuggestionCandidate
                {
                    Id = t.Id,
                    PostedAt = t.PostedAt,
                    Amount = t.Amount,
                    MerchantName = t.MerchantName,
                    Description = t.Description,
                    InstitutionName = t.BankConnection!.InstitutionName,
                })
                .ToListAsync(ct));
        }

        if (openExpenseRows.Count > 0)
        {
            var minAmount = openExpenseRows.Min(e => -e.Amount) - 0.01m;
            var maxAmount = openExpenseRows.Max(e => -e.Amount) + 0.01m;
            var minDate = openExpenseRows.Min(e => e.Date.Date).AddDays(-14);
            var maxDateExclusive = openExpenseRows.Max(e => e.Date.Date).AddDays(15);

            unmatched.AddRange(await unmatchedQuery
                .Where(t =>
                    t.Amount < 0m &&
                    t.Amount >= minAmount &&
                    t.Amount <= maxAmount &&
                    t.PostedAt >= minDate &&
                    t.PostedAt < maxDateExclusive)
                .Select(t => new BankSuggestionCandidate
                {
                    Id = t.Id,
                    PostedAt = t.PostedAt,
                    Amount = t.Amount,
                    MerchantName = t.MerchantName,
                    Description = t.Description,
                    InstitutionName = t.BankConnection!.InstitutionName,
                })
                .ToListAsync(ct));
        }

        if (unmatched.Count == 0) return result;

        var deposits = unmatched.Where(c => c.Amount > 0).ToList();   // suggest against payments (income)
        var withdrawals = unmatched.Where(c => c.Amount < 0).ToList(); // suggest against expenses

        foreach (var p in openPaymentRows)
        {
            var suggestion = BestBankSuggestion(deposits, p.Amount, p.Date, p.Counterparty);
            if (suggestion != null)
                result[(KindPayment, p.Id)] = new ReconciliationState { Suggested = suggestion };
        }

        foreach (var e in openExpenseRows)
        {
            // Expense amounts are stored positive; the bank withdrawal is negative.
            var suggestion = BestBankSuggestion(withdrawals, -e.Amount, e.Date, e.Counterparty);
            if (suggestion != null)
                result[(KindExpense, e.Id)] = new ReconciliationState { Suggested = suggestion };
        }

        return result;
    }

    /// <summary>
    /// Pick the best open bank line for a Payment/Expense row: amount must match within a cent (hard
    /// gate); date proximity sets the base score and a counterparty-name signal raises it. Returns the
    /// top candidate above a confidence floor, or null. Mirrors the banking match engine so the inline
    /// "Match?" chip agrees with the banking review queue.
    /// </summary>
    private static SuggestedBankMatchResponse? BestBankSuggestion(
        IReadOnlyList<BankSuggestionCandidate> candidates,
        decimal targetSignedAmount,
        DateTime anchor,
        string? counterparty)
    {
        SuggestedBankMatchResponse? best = null;
        decimal bestScore = 0m;

        foreach (var c in candidates)
        {
            if (Math.Abs(c.Amount - targetSignedAmount) > 0.01m) continue;

            var nameMatch = NameMatchStrength(c.MerchantName, c.Description, counterparty);
            var days = Math.Abs((c.PostedAt.Date - anchor.Date).Days);
            var maxDays = nameMatch >= 0.6m ? 14 : 7;
            if (days > maxDays) continue;

            var dateScore = days switch
            {
                0 => 0.80m,
                <= 2 => 0.72m,
                <= 4 => 0.62m,
                <= 7 => 0.52m,
                _ => 0.42m,
            };
            var score = Math.Min(dateScore + nameMatch * 0.20m, 0.99m);

            if (score > bestScore)
            {
                bestScore = score;
                best = new SuggestedBankMatchResponse
                {
                    BankTransactionId = c.Id,
                    Name = string.IsNullOrWhiteSpace(c.MerchantName) ? c.InstitutionName : c.MerchantName!,
                    Amount = c.Amount,
                    Date = c.PostedAt,
                    Confidence = score,
                };
            }
        }

        return best;
    }

    // ── Name-aware matching helpers (kept in lockstep with BankingService's match engine) ──────────
    private static decimal NameMatchStrength(string? bankMerchant, string? bankDescription, params string?[] candidateNames)
    {
        var bankText = NormalizeName($"{bankMerchant} {bankDescription}");
        if (bankText.Length == 0) return 0m;

        var bankTokens = SignificantTokens(bankText);
        if (bankTokens.Count == 0) return 0m;

        var best = 0m;
        foreach (var candidate in candidateNames)
        {
            var normalized = NormalizeName(candidate);
            if (normalized.Length == 0) continue;

            if (bankText.Contains(normalized, StringComparison.Ordinal) ||
                normalized.Contains(bankText, StringComparison.Ordinal))
            {
                return 1m;
            }

            var candidateTokens = SignificantTokens(normalized);
            if (candidateTokens.Count == 0) continue;

            var shared = candidateTokens.Count(t => bankTokens.Contains(t));
            if (shared == 0) continue;

            var fraction = (decimal)shared / candidateTokens.Count;
            if (fraction > best) best = fraction;
        }

        return best;
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (char.IsWhiteSpace(ch)) sb.Append(' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static readonly HashSet<string> NameStopWords = new(StringComparer.Ordinal)
    {
        "ach", "the", "and", "llc", "inc", "co", "payment", "pmt", "deposit", "debit", "credit",
        "transfer", "xfer", "online", "pos", "purchase", "rent", "from", "for", "ref", "id",
    };

    private static HashSet<string> SignificantTokens(string normalized) =>
        normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && !NameStopWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);

    private sealed class ReconciliationState
    {
        public bool Reconciled { get; set; }
        public string? ClearedBankName { get; set; }
        public DateTime? ClearedAt { get; set; }
        public SuggestedBankMatchResponse? Suggested { get; set; }
    }

    private sealed class BankSuggestionCandidate
    {
        public int Id { get; set; }
        public DateTime PostedAt { get; set; }
        public decimal Amount { get; set; }
        public string? MerchantName { get; set; }
        public string Description { get; set; } = string.Empty;
        public string InstitutionName { get; set; } = string.Empty;
    }

    public async Task<AccountingReportsResponse> GetReportsAsync(int portfolioId, CancellationToken ct = default)
    {
        var generatedAt = DateTime.UtcNow;

        var ledgerRows = await ReportLedgerQuery(portfolioId)
            .OrderByDescending(l => l.Date)
            .ThenByDescending(l => l.Id)
            .ToListAsync(ct);
        var ledger = ledgerRows.Select(ToLedgerTransaction).ToList();

        // Per-property rollups stay in SQL as correlated aggregates. Only final DTO formatting and
        // the simple Net arithmetic happen after materialization.
        var propertyRows = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                Income = _db.Payments
                    .Where(pay => pay.PortfolioId == portfolioId &&
                                  pay.Lease != null &&
                                  pay.Lease.PropertyId == p.Id &&
                                  (pay.Status == PaymentStatus.Paid || pay.Status == PaymentStatus.Partial))
                    .Sum(pay => (decimal?)(pay.Status == PaymentStatus.Partial
                        ? (pay.AmountPaid ?? 0m)
                        : pay.Amount)) ?? 0m,
                Expenses = _db.Expenses
                    .Where(e => e.PortfolioId == portfolioId && e.PropertyId == p.Id)
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
                Overdue = _db.Payments
                    .Where(pay => pay.PortfolioId == portfolioId &&
                                  pay.Lease != null &&
                                  pay.Lease.PropertyId == p.Id &&
                                  (pay.Status == PaymentStatus.Scheduled || pay.Status == PaymentStatus.Partial || pay.Status == PaymentStatus.Late) &&
                                  (pay.Status == PaymentStatus.Late || pay.DueDate < generatedAt))
                    .Sum(pay => (decimal?)(pay.Status == PaymentStatus.Partial
                        ? pay.Amount - (pay.AmountPaid ?? 0m)
                        : pay.Amount)) ?? 0m,
                OverdueCount = _db.Payments.Count(pay =>
                    pay.PortfolioId == portfolioId &&
                    pay.Lease != null &&
                    pay.Lease.PropertyId == p.Id &&
                    (pay.Status == PaymentStatus.Scheduled || pay.Status == PaymentStatus.Partial || pay.Status == PaymentStatus.Late) &&
                    (pay.Status == PaymentStatus.Late || pay.DueDate < generatedAt)),
            })
            .ToListAsync(ct);

        var propertyReports = propertyRows
            .Select(p =>
            {
                return new PropertyFinancialSummaryResponse
                {
                    PropertyId = p.Id,
                    PropertyName = p.Name,
                    Income = p.Income,
                    Expenses = p.Expenses,
                    Net = p.Income - p.Expenses,
                    Overdue = p.Overdue,
                    OverdueCount = p.OverdueCount,
                };
            })
            .ToList();

        // Schedule E category rollup, grouped, summed, and ordered SQL-side. Enum-name formatting happens
        // in memory on the already-aggregated (one row per category) result.
        var scheduleE = (await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId)
            .GroupBy(e => e.Category)
            .Select(g => new { Category = g.Key, Total = g.Sum(e => e.Amount), Count = g.Count() })
            .OrderByDescending(g => g.Total)
            .ToListAsync(ct))
            .Select(g => new ScheduleECategoryTotal
            {
                Category = g.Category,
                CategoryName = g.Category.ToString(),
                Total = g.Total,
                Count = g.Count,
            })
            .ToList();

        var vendors1099 = await _db.Vendors
            .AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId)
            .Select(v => new
            {
                v.Id,
                v.Name,
                v.Is1099Eligible,
                v.W9OnFile,
                TotalPaid = v.Expenses
                    .Where(e => e.Status == ExpenseStatus.Paid || e.PaidAt != null)
                    .Sum(e => (decimal?)e.Amount) ?? 0m,
            })
            .Where(v => v.Is1099Eligible || v.TotalPaid > 0m)
            .OrderBy(v => v.Name)
            .ToListAsync(ct);

        var vendorReports = vendors1099
            .Select(v => new Vendor1099SummaryResponse
            {
                VendorId = v.Id,
                VendorName = v.Name,
                TotalPaid = v.TotalPaid,
                Is1099Eligible = v.Is1099Eligible,
                W9OnFile = v.W9OnFile,
                NeedsW9 = v.Is1099Eligible && !v.W9OnFile,
                Needs1099Review = v.Is1099Eligible && v.TotalPaid >= Vendor1099Threshold,
            })
            .ToList();

        // Portfolio totals computed SQL-side (SUM aggregates), not by re-summing the materialized
        // ledger rows in memory.
        // Collected income: Paid contributes its full Amount, Partial contributes its AmountPaid.
        var paidPaymentTotal = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                        (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Partial))
            .SumAsync(p => (decimal?)(p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount), ct) ?? 0m;

        var unmatchedDepositTotal = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.MatchStatus != "Removed" &&
                        t.Amount > 0 && t.MatchedPaymentId == null)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m;

        var expenseTotal = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId)
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var unmatchedWithdrawalTotal = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.MatchStatus != "Removed" &&
                        t.Amount < 0 && t.MatchedExpenseId == null)
            .SumAsync(t => (decimal?)-t.Amount, ct) ?? 0m;

        var totalIncome = paidPaymentTotal + unmatchedDepositTotal;
        var totalExpenses = expenseTotal + unmatchedWithdrawalTotal;

        return new AccountingReportsResponse
        {
            PortfolioId = portfolioId,
            GeneratedAt = generatedAt,
            TotalIncome = totalIncome,
            TotalExpenses = totalExpenses,
            NetCashFlow = totalIncome - totalExpenses,
            Ledger = ledger,
            Properties = propertyReports,
            ScheduleE = scheduleE,
            Vendors1099 = vendorReports,
        };
    }

    private IQueryable<AccountingReportLedgerRow> ReportLedgerQuery(int portfolioId)
    {
        var payments = _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .Select(p => new AccountingReportLedgerRow
            {
                Date = p.PaidDate ?? p.DueDate,
                Type = KindPayment,
                Id = p.Id,
                Amount = p.Status == PaymentStatus.Paid ? p.Amount
                    : p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m)
                    : 0m,
                PropertyId = (int?)p.Lease!.PropertyId,
                PropertyName = p.Lease!.Property!.Name,
                CounterpartyFirstName = p.Lease!.Tenant!.FirstName,
                CounterpartyLastName = p.Lease!.Tenant!.LastName,
                PaymentType = p.PaymentType,
                PaymentStatus = p.Status,
                OriginalPaymentAmount = p.Amount,
                PaymentDueDate = p.DueDate,
                PaymentPaidDate = p.PaidDate,
                PaymentMethod = p.Method,
                Description = null,
                CounterpartyName = null,
                Category = null,
                Status = null,
                ExpenseCategory = null,
                ExpenseStatus = null,
                OriginalExpenseAmount = 0m,
                ExpenseDate = null,
            });

        var expenses = _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId)
            .Select(e => new AccountingReportLedgerRow
            {
                Date = e.PaidAt ?? e.IncurredAt,
                Type = KindExpense,
                Id = e.Id,
                Description = e.Description,
                Amount = -e.Amount,
                PropertyId = e.PropertyId,
                PropertyName = e.Property != null ? e.Property.Name : null,
                CounterpartyName = e.Vendor != null ? e.Vendor.Name : null,
                ExpenseCategory = e.Category,
                ExpenseStatus = e.Status,
                OriginalExpenseAmount = e.Amount,
                ExpenseDate = e.PaidAt ?? e.IncurredAt,
                PaymentType = null,
                PaymentStatus = null,
                OriginalPaymentAmount = 0m,
                PaymentDueDate = null,
                PaymentPaidDate = null,
                PaymentMethod = null,
                CounterpartyFirstName = null,
                CounterpartyLastName = null,
                Category = null,
                Status = null,
            });

        var bankTransactions = _db.BankTransactions
            .AsNoTracking()
            .Where(t =>
                t.PortfolioId == portfolioId &&
                t.MatchStatus != "Removed" &&
                t.MatchedPaymentId == null &&
                t.MatchedExpenseId == null)
            .Select(t => new AccountingReportLedgerRow
            {
                Date = t.PostedAt,
                Type = KindBank,
                Id = t.Id,
                Description = t.Description,
                Amount = t.Amount,
                PropertyId = null,
                PropertyName = null,
                CounterpartyName = t.MerchantName ?? t.BankConnection!.InstitutionName,
                Category = t.Category ?? (t.Amount >= 0m ? "Deposit" : "Withdrawal"),
                Status = t.MatchStatus,
                PaymentType = null,
                PaymentStatus = null,
                OriginalPaymentAmount = 0m,
                PaymentDueDate = null,
                PaymentPaidDate = null,
                PaymentMethod = null,
                CounterpartyFirstName = null,
                CounterpartyLastName = null,
                ExpenseCategory = null,
                ExpenseStatus = null,
                OriginalExpenseAmount = 0m,
                ExpenseDate = null,
            });

        return payments.Concat(expenses).Concat(bankTransactions);
    }

    private static LedgerTransactionResponse ToLedgerTransaction(AccountingReportLedgerRow row)
    {
        if (row.Type == KindPayment)
        {
            var paymentType = row.PaymentType.GetValueOrDefault();
            var paymentStatus = row.PaymentStatus.GetValueOrDefault();
            return new LedgerTransactionResponse
            {
                Date = row.Date,
                Type = KindPayment,
                Id = row.Id,
                Description = paymentType.ToString(),
                Amount = row.Amount,
                PropertyId = row.PropertyId,
                PropertyName = row.PropertyName,
                Counterparty = FullName(row.CounterpartyFirstName ?? string.Empty, row.CounterpartyLastName ?? string.Empty),
                Category = row.PaymentMethod,
                Status = paymentStatus.ToString(),
                SourceHref = $"/accounting/payments/{row.Id}",
                Explanation = LedgerExplanation.ForPayment(
                    paymentType,
                    paymentStatus,
                    row.OriginalPaymentAmount,
                    row.PaymentDueDate ?? row.Date,
                    row.PaymentPaidDate,
                    row.PaymentMethod),
            };
        }

        if (row.Type == KindExpense)
        {
            var expenseCategory = row.ExpenseCategory.GetValueOrDefault();
            var expenseStatus = row.ExpenseStatus.GetValueOrDefault();
            return new LedgerTransactionResponse
            {
                Date = row.Date,
                Type = KindExpense,
                Id = row.Id,
                Description = row.Description ?? string.Empty,
                Amount = row.Amount,
                PropertyId = row.PropertyId,
                PropertyName = row.PropertyName,
                Counterparty = row.CounterpartyName,
                Category = expenseCategory.ToString(),
                Status = expenseStatus.ToString(),
                SourceHref = $"/accounting/expenses/{row.Id}",
                Explanation = LedgerExplanation.ForExpense(
                    expenseCategory,
                    expenseStatus,
                    row.OriginalExpenseAmount,
                    row.ExpenseDate ?? row.Date,
                    row.CounterpartyName,
                    row.Description ?? string.Empty),
            };
        }

        return new LedgerTransactionResponse
        {
            Date = row.Date,
            Type = KindBank,
            Id = row.Id,
            Description = row.Description ?? string.Empty,
            Amount = row.Amount,
            PropertyId = null,
            PropertyName = null,
            Counterparty = row.CounterpartyName,
            Category = row.Category,
            Status = row.Status ?? string.Empty,
            SourceHref = "/banking",
            Explanation = LedgerExplanation.ForBank(row.Amount, row.Date, row.CounterpartyName),
        };
    }

    public async Task<byte[]> GetYearEndPacketAsync(int portfolioId, int year, CancellationToken ct = default)
    {
        var data = await GetYearEndPacketDataAsync(portfolioId, year, ct);
        return _packetPdf.Generate(data);
    }

    public async Task<YearEndPacketData> GetYearEndPacketDataAsync(
        int portfolioId, int year, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // ── Portfolio header ────────────────────────────────────────────────────────────────────
        var portfolio = await _db.Portfolios
            .AsNoTracking()
            .Where(p => p.Id == portfolioId)
            .Select(p => new { p.Name, p.ManagementCompanyName })
            .FirstOrDefaultAsync(ct);

        // ── Schedule E (reused so the packet matches the existing CSV/report exactly) ─────────────
        var scheduleE = await _scheduleE.GetReportAsync(portfolioId, year, ct);

        // ── Per-property P&L for the year ─────────────────────────────────────────────────────────
        // Income: Rent cash received (Paid full Amount + Partial AmountPaid) whose PaidDate falls in the
        // year, keyed by property via Lease — matching the Schedule E rental-income definition so the
        // packet reconciles with it. Grouped + summed SQL-side (one row per property), never by grouping
        // materialized rows.
        var incomeByProperty = (await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.PaymentType == PaymentType.Rent &&
                (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Partial) &&
                p.PaidDate != null &&
                p.PaidDate.Value.Year == year &&
                p.Lease != null)
            .GroupBy(p => p.Lease!.PropertyId)
            .Select(g => new { PropertyId = g.Key, Total = g.Sum(p =>
                p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount) })
            .ToListAsync(ct))
            .ToDictionary(g => g.PropertyId, g => g.Total);

        // Expenses for the year, keyed by property + Schedule E category — grouped on both keys
        // SQL-side. Cash basis (matching the cash-basis income above and the packet's monthly cash-flow
        // block below): only Paid expenses count, dated by PaidAt (falling back to IncurredAt) — the same
        // COALESCE(PaidAt, IncurredAt) convention used elsewhere. The result (one row per
        // property/category pair) is reshaped into the nested map in memory, but no SUM is computed in
        // memory.
        var expenseCategoryTotals = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId && e.Status == ExpenseStatus.Paid && (e.PaidAt ?? e.IncurredAt).Year == year && e.PropertyId != null)
            .GroupBy(e => new { PropertyId = e.PropertyId!.Value, e.Category })
            .Select(g => new { g.Key.PropertyId, g.Key.Category, Total = g.Sum(e => e.Amount) })
            .ToListAsync(ct);

        var expensesByProperty = expenseCategoryTotals
            .GroupBy(r => r.PropertyId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(r => r.Category, r => r.Total));

        var properties = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var propertyPnL = new List<YearEndPropertyPnL>(properties.Count);
        foreach (var prop in properties)
        {
            var income = incomeByProperty.GetValueOrDefault(prop.Id, 0m);
            var catMap = expensesByProperty.GetValueOrDefault(prop.Id);

            var categories = new List<ScheduleECategoryAmount>();
            if (catMap != null)
            {
                // Enum-declared order, zero amounts omitted — same shape as ScheduleEService.
                foreach (ScheduleECategory cat in Enum.GetValues<ScheduleECategory>())
                {
                    if (catMap.TryGetValue(cat, out var amount) && amount != 0m)
                        categories.Add(new ScheduleECategoryAmount(cat.ToString(), amount));
                }
            }

            var totalExpenses = categories.Sum(c => c.Amount);

            // Skip properties with no activity this year to keep the packet tight.
            if (income == 0m && totalExpenses == 0m)
                continue;

            propertyPnL.Add(new YearEndPropertyPnL
            {
                PropertyId = prop.Id,
                PropertyName = prop.Name,
                Income = income,
                ExpensesByCategory = categories,
                TotalExpenses = totalExpenses,
                Net = income - totalExpenses,
            });
        }

        // ── Cash flow by month ────────────────────────────────────────────────────────────────────
        // Money in: paid payments (cash landed on PaidDate, falling back to DueDate) in the year.
        // Grouped by calendar month and summed SQL-side, with the year filter pushed into the query
        // so the whole payment history is never loaded just to bucket one year in memory.
        var moneyInByMonth = (await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId &&
                        (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Partial) &&
                        (p.PaidDate ?? p.DueDate).Year == year)
            .GroupBy(p => (p.PaidDate ?? p.DueDate).Month)
            // Paid contributes full Amount; Partial contributes the collected AmountPaid.
            .Select(g => new { Month = g.Key, Total = g.Sum(p =>
                p.Status == PaymentStatus.Partial ? (p.AmountPaid ?? 0m) : p.Amount) })
            .ToListAsync(ct))
            .ToDictionary(g => g.Month, g => g.Total);

        // Money out: expenses paid (PaidAt, falling back to IncurredAt) in the year — same SQL-side
        // monthly grouping.
        var moneyOutByMonth = (await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId && (e.PaidAt ?? e.IncurredAt).Year == year)
            .GroupBy(e => (e.PaidAt ?? e.IncurredAt).Month)
            .Select(g => new { Month = g.Key, Total = g.Sum(e => e.Amount) })
            .ToListAsync(ct))
            .ToDictionary(g => g.Month, g => g.Total);

        var cashFlow = new List<YearEndCashFlowMonth>(12);
        for (var month = 1; month <= 12; month++)
        {
            var moneyIn = moneyInByMonth.GetValueOrDefault(month, 0m);
            var moneyOut = moneyOutByMonth.GetValueOrDefault(month, 0m);

            cashFlow.Add(new YearEndCashFlowMonth
            {
                Month = month,
                MonthName = System.Globalization.CultureInfo.InvariantCulture
                    .DateTimeFormat.GetAbbreviatedMonthName(month),
                MoneyIn = moneyIn,
                MoneyOut = moneyOut,
                Net = moneyIn - moneyOut,
            });
        }

        var cashIn = cashFlow.Sum(m => m.MoneyIn);
        var cashOut = cashFlow.Sum(m => m.MoneyOut);

        // ── Rent roll ─────────────────────────────────────────────────────────────────────────────
        // Current leases (active or under notice). Past-due balance is owed rent/charges due in the past.
        var leases = await _db.Leases
            .AsNoTracking()
            .Where(l => l.PortfolioId == portfolioId &&
                        (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven))
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

        var leaseIds = leases.Select(l => l.Id).ToHashSet();

        // Past-due balance per lease: the full owed-and-overdue predicate (including the `DueDate < now`
        // / Late check) is applied in the WHERE and the sum is grouped SQL-side — no rows are pulled
        // back to filter and total in memory.
        var pastDueByLease = (await _db.Payments
                .AsNoTracking()
                .Where(p => p.PortfolioId == portfolioId &&
                            leaseIds.Contains(p.LeaseId) &&
                            (p.Status == PaymentStatus.Scheduled ||
                             p.Status == PaymentStatus.Partial ||
                             p.Status == PaymentStatus.Late) &&
                            (p.Status == PaymentStatus.Late || p.DueDate < now))
                .GroupBy(p => p.LeaseId)
                // A Partial owes only its unpaid remainder; Scheduled/Late owe in full.
                .Select(g => new { LeaseId = g.Key, Total = g.Sum(p =>
                    p.Status == PaymentStatus.Partial ? p.Amount - (p.AmountPaid ?? 0m) : p.Amount) })
                .ToListAsync(ct))
            .ToDictionary(g => g.LeaseId, g => g.Total);

        var rentRoll = leases
            .Select(l => new YearEndRentRollRow
            {
                PropertyName = l.PropertyName,
                UnitNumber = l.UnitNumber,
                TenantName = FullName(l.TenantFirstName, l.TenantLastName),
                MonthlyRent = l.MonthlyRent,
                LeaseStart = l.StartDate,
                LeaseEnd = l.EndDate,
                LeaseStatus = l.Status.ToString(),
                PastDueBalance = pastDueByLease.GetValueOrDefault(l.Id, 0m),
            })
            .OrderBy(r => r.PropertyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.UnitNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new YearEndPacketData
        {
            Year = year,
            PortfolioName = portfolio?.Name ?? string.Empty,
            ManagementCompanyName = portfolio?.ManagementCompanyName ?? string.Empty,
            GeneratedAt = now,
            ScheduleE = scheduleE,
            Properties = propertyPnL,
            CashFlow = cashFlow,
            CashFlowMoneyIn = cashIn,
            CashFlowMoneyOut = cashOut,
            CashFlowNet = cashIn - cashOut,
            RentRoll = rentRoll,
        };
    }

    private static bool IsOwedPayment(PaymentStatus status) =>
        status is PaymentStatus.Scheduled or PaymentStatus.Partial or PaymentStatus.Late;

    private sealed class AccountingReportLedgerRow
    {
        public DateTime Date { get; set; }
        public string Type { get; set; } = string.Empty;
        public int Id { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public int? PropertyId { get; set; }
        public string? PropertyName { get; set; }
        public string? CounterpartyName { get; set; }
        public string? CounterpartyFirstName { get; set; }
        public string? CounterpartyLastName { get; set; }
        public string? Category { get; set; }
        public string? Status { get; set; }
        public PaymentType? PaymentType { get; set; }
        public PaymentStatus? PaymentStatus { get; set; }
        public decimal OriginalPaymentAmount { get; set; }
        public DateTime? PaymentDueDate { get; set; }
        public DateTime? PaymentPaidDate { get; set; }
        public string? PaymentMethod { get; set; }
        public ScheduleECategory? ExpenseCategory { get; set; }
        public ExpenseStatus? ExpenseStatus { get; set; }
        public decimal OriginalExpenseAmount { get; set; }
        public DateTime? ExpenseDate { get; set; }
    }

    private static string FullName(string firstName, string lastName)
    {
        var fullName = $"{firstName} {lastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName) ? "Tenant" : fullName;
    }

}
