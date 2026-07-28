using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Reporting;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAccountingService"/>
public class AccountingService : IAccountingService
{
    private const int ReportLedgerPreviewTake = 8;
    private const decimal Vendor1099Threshold = 600m;
    private const string KindExpense = "Expense";
    private const string KindPayment = "Payment";
    private const string KindTenantLedger = "TenantLedger";
    private const string KindBank = "Bank";
    private const string KindApplicationFee = "ApplicationFee";

    private readonly RentalCommandDbContext _db;
    private readonly IScheduleEService _scheduleE;
    private readonly IYearEndPacketPdfGenerator _packetPdf;
    private readonly TimeProvider _timeProvider;

    public AccountingService(
        RentalCommandDbContext db,
        IScheduleEService scheduleE,
        IYearEndPacketPdfGenerator packetPdf,
        TimeProvider timeProvider)
    {
        _db = db;
        _scheduleE = scheduleE;
        _packetPdf = packetPdf;
        _timeProvider = timeProvider;
    }

    public Task<AccountingSummaryResponse> GetSummaryAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default) =>
        GetSummaryCoreAsync(scope.PortfolioId, AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead), ct);

    private async Task<AccountingSummaryResponse> GetSummaryCoreAsync(
        int portfolioId,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct)
    {
        // Expense totals grouped by Schedule E category (soft-deleted expenses are excluded by the
        // global query filter). Keep the authorized source reusable so the category breakdown and
        // the headline rollup are the only two database statements required by this page.
        var authorizedExpenses = _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == e.PropertyId));

        var categoryGroups = await authorizedExpenses
            .GroupBy(e => e.Category)
            .Select(g => new
            {
                Category = g.Key,
                Total = g.Sum(e => e.Amount),
                Count = g.Count(),
            })
            .OrderByDescending(g => g.Total)
            .ToListAsync(ct);

        var expensesByCategory = categoryGroups
            .Select(g => new ScheduleECategoryTotal
            {
                Category = g.Category,
                CategoryName = g.Category.ToString(),
                Total = g.Total,
                Count = g.Count,
            })
            .ToList();

        // Payment collection rollup. Collected cash remains historical; operational receivables are
        // limited to possession-backed Occupied/Ending relationships. Agreement expiry by itself does
        // not end possession or erase an amount still owed by a current resident. Each source is
        // aggregated before joining to the one portfolio anchor, so PostgreSQL scans each relation
        // once and returns the complete headline rollup in one statement.
        var expenseRollupQuery = authorizedExpenses
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Key = g.Key,
                TotalExpenses = g.Sum(expense => (decimal?)expense.Amount),
            });

        var collectedRollupQuery = TenantIncomeQuery(portfolioId, authorizedProperties)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Key = g.Key,
                Collected = g.Sum(row => (decimal?)row.Amount),
            });

        var receivablesRollupQuery = CurrentTenantBalanceQuery(portfolioId, authorizedProperties)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Key = g.Key,
                Outstanding = g.Sum(balance => (decimal?)(balance.ReceivableBalance > 0m
                    ? balance.ReceivableBalance
                    : 0m)),
                Overdue = g.Sum(balance => (decimal?)balance.PastDueAmount),
                OverdueCount = g.Sum(balance => (int?)balance.PastDueCount),
            });

        var raw = await (
                from portfolio in _db.Portfolios.AsNoTracking()
                where portfolio.Id == portfolioId
                join expense in expenseRollupQuery on 1 equals expense.Key into expenseRows
                from expense in expenseRows.DefaultIfEmpty()
                join collected in collectedRollupQuery on 1 equals collected.Key into collectedRows
                from collected in collectedRows.DefaultIfEmpty()
                join receivables in receivablesRollupQuery on 1 equals receivables.Key into receivableRows
                from receivables in receivableRows.DefaultIfEmpty()
                select new
                {
                    TotalExpenses = expense.TotalExpenses ?? 0m,
                    Collected = collected.Collected ?? 0m,
                    Outstanding = receivables.Outstanding ?? 0m,
                    Overdue = receivables.Overdue ?? 0m,
                    OverdueCount = receivables.OverdueCount ?? 0,
                })
            .SingleAsync(ct);

        var rollup = new PaymentRollup
        {
            Collected = raw.Collected,
            Outstanding = raw.Outstanding,
            Overdue = raw.Overdue,
            OverdueCount = raw.OverdueCount,
        };

        return new AccountingSummaryResponse
        {
            PortfolioId = portfolioId,
            ExpensesByCategory = expensesByCategory,
            TotalExpenses = raw.TotalExpenses,
            Payments = rollup,
            Snapshot = BuildMoneySnapshot(rollup, raw.TotalExpenses, expensesByCategory),
        };
    }

    public Task<MoneySnapshotResponse> GetSnapshotAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default) =>
        GetSnapshotCoreAsync(scope.PortfolioId, AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead), ct);

    private async Task<MoneySnapshotResponse> GetSnapshotCoreAsync(
        int portfolioId,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct)
    {
        var now = _timeProvider.UtcNow();
        var monthStart = new DateOnly(now.Year, now.Month, 1);
        var last30Start = DateOnly.FromDateTime(now.AddDays(-30));

        // Money in: payments actually collected. "Collected" means Status == Paid AND a real PaidDate
        // (the date the cash landed) — a row marked Paid but lacking a PaidDate is not yet collected and
        // must NOT count, otherwise scheduled/expected rent would inflate money-in by its due date. This
        // matches the DashboardService "PaidThisMonth" KPI (Paid + PaidDate in period) so the two figures
        // can't disagree. Both period figures (month-to-date and trailing 30 days)
        // are computed SQL-side as conditional SUMs in one grouped round-trip per source — no rows are
        // loaded into memory.
        // A Partial payment's collected cash also lands on its PaidDate, so it counts as money-in for the
        // period — contributing its AmountPaid (not its full Amount). Paid contributes the full Amount.
        var paymentsCollected = await TenantIncomeQuery(portfolioId, authorizedProperties)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Mtd = g.Sum(row => row.EffectiveOn >= monthStart
                    ? row.Amount
                    : 0m),
                Last30 = g.Sum(row => row.EffectiveOn >= last30Start
                    ? row.Amount
                    : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var collectedMtd = paymentsCollected?.Mtd ?? 0m;
        var collected30 = paymentsCollected?.Last30 ?? 0m;

        // Money out: authorized-property expenses, using paid date when present and incurred date otherwise.
        var expensesSpent = await _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == e.PropertyId))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Mtd = g.Sum(e => (e.PaidAt ?? e.IncurredAt) >= monthStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) ? e.Amount : 0m),
                Last30 = g.Sum(e => (e.PaidAt ?? e.IncurredAt) >= last30Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) ? e.Amount : 0m),
            })
            .FirstOrDefaultAsync(ct);

        var spentMtd = expensesSpent?.Mtd ?? 0m;
        var spent30 = expensesSpent?.Last30 ?? 0m;
        var debtServiceSpent = await _db.LoanPayments
            .AsNoTracking()
            .Where(payment =>
                payment.PortfolioId == portfolioId &&
                payment.Status == LoanPaymentStatus.Paid &&
                payment.PaidDate != null &&
                payment.Loan != null &&
                authorizedProperties.Any(property => property.Id == payment.Loan.PropertyId))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Mtd = group.Sum(payment => payment.PaidDate >= monthStart.ToDateTime(
                    TimeOnly.MinValue, DateTimeKind.Utc) ? payment.TotalAmount : 0m),
                Last30 = group.Sum(payment => payment.PaidDate >= last30Start.ToDateTime(
                    TimeOnly.MinValue, DateTimeKind.Utc) ? payment.TotalAmount : 0m),
            })
            .FirstOrDefaultAsync(ct);
        spentMtd += debtServiceSpent?.Mtd ?? 0m;
        spent30 += debtServiceSpent?.Last30 ?? 0m;

        // Past due: anyone behind right now (not period-bound). The amount and the distinct-lease count
        // (= tenants behind) come from the SAME per-lease grouped query that powers the "Who's behind"
        // list (GetPastDueAsync), so this KPI can never disagree with the destination row count.
        // Both figures are derived SQL-side; no payment rows are loaded to count.
        var pastDueSummary = await PastDueSummaryQuery(portfolioId, authorizedProperties).FirstOrDefaultAsync(ct);
        var pastDueAmount = pastDueSummary?.TotalPastDueAmount ?? 0m;
        var pastDueCount = pastDueSummary?.TotalCount ?? 0;

        var netMtd = collectedMtd - spentMtd;
        var net30 = collected30 - spent30;

        return new MoneySnapshotResponse
        {
            PortfolioId = portfolioId,
            PeriodLabel = $"{monthStart:MMMM yyyy} (so far)",
            PeriodStart = monthStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
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

    public Task<PastDueResponse> GetPastDueAsync(
        WorkspaceReadScope scope,
        PastDueQuery query,
        CancellationToken ct = default) =>
        GetPastDueCoreAsync(
            scope.PortfolioId,
            AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead),
            query.NormalizedSkip,
            query.NormalizedTake,
            ct);

    private async Task<PastDueResponse> GetPastDueCoreAsync(
        int portfolioId,
        IQueryable<Property> authorizedProperties,
        int skip,
        int take,
        CancellationToken ct)
    {
        var summary = await PastDueSummaryQuery(portfolioId, authorizedProperties).FirstOrDefaultAsync(ct);
        var totalCount = summary?.TotalCount ?? 0;
        var totalPastDueAmount = summary?.TotalPastDueAmount ?? 0m;
        var businessDate = summary?.BusinessDate;

        if (totalCount == 0)
        {
            return new PastDueResponse
            {
                Items = [],
                TotalCount = 0,
                TotalPastDueAmount = 0m,
                BusinessDate = businessDate,
                Skip = skip,
                Take = take,
            };
        }

        var items = await (
                from balance in CurrentTenantBalanceQuery(portfolioId, authorizedProperties)
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { balance.PortfolioId, Id = balance.LeaseManagementId }
                    equals new { management.PortfolioId, management.Id }
                where balance.PortfolioId == portfolioId &&
                    balance.PastDueAmount > 0m
                let oldest = _db.TenantChargeBalanceProjections
                    .Where(charge => charge.PortfolioId == balance.PortfolioId
                        && charge.TenantAccountId == balance.TenantAccountId
                        && charge.IsPastDue)
                    .OrderBy(charge => charge.DueOn)
                    .ThenBy(charge => charge.TenantLedgerEntryId)
                    .Select(charge => new { charge.TenantLedgerEntryId, charge.DueOn })
                    .First()
                orderby oldest.DueOn, oldest.TenantLedgerEntryId
                select new PastDueLeaseResponse
            {
                LeaseManagementId = management.Id,
                TenantAccountId = balance.TenantAccountId,
                CurrentAgreementId = balance.CurrentAgreementId,
                UnitId = management.UnitId,
                TenantName = balance.CurrentPrimaryTenantName,
                TenantPhone = _db.LeaseManagementParties
                    .Where(party => party.PortfolioId == management.PortfolioId
                        && party.LeaseManagementId == management.Id
                        && party.Id == balance.CurrentPrimaryPartyId)
                    .Select(party => party.Tenant!.Phone)
                    .FirstOrDefault(),
                RelationshipNumber = management.RelationshipNumber,
                PropertyName = management.Property!.Name,
                UnitNumber = management.Unit!.UnitNumber,
                PastDueAmount = balance.PastDueAmount,
                OverduePaymentCount = balance.PastDueCount,
                OldestDueOn = oldest.DueOn!.Value,
                OldestLedgerEntryId = oldest.TenantLedgerEntryId,
            })
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return new PastDueResponse
        {
            Items = items,
            TotalCount = totalCount,
            TotalPastDueAmount = totalPastDueAmount,
            BusinessDate = businessDate,
            Skip = skip,
            Take = take,
        };
    }

    /// <summary>
    /// The canonical current past-due relation shared by the snapshot KPI, the "Who's behind" list,
    /// summary rollup, and per-property report. Charge aging comes from the balance view; whether the
    /// relationship remains operational comes from the lifecycle view.
    /// </summary>
    private IQueryable<PastDueSummary> PastDueSummaryQuery(
        int portfolioId,
        IQueryable<Property> authorizedProperties) =>
        CurrentTenantBalanceQuery(portfolioId, authorizedProperties)
            .GroupBy(balance => balance.BusinessDate)
            .Select(g => new PastDueSummary
            {
                BusinessDate = g.Key,
                TotalCount = g.Count(balance => balance.PastDueAmount > 0m),
                TotalPastDueAmount = g.Sum(balance =>
                    balance.PastDueAmount > 0m ? balance.PastDueAmount : 0m),
            });

    /// <summary>
    /// Canonical tenant balances that still belong on current operational money surfaces. Agreement
    /// expiration does not end a resident relationship: open possession remains Occupied (and is
    /// separately exposed as a reconciliation exception when no agreement governs). Returned possession,
    /// cancellation, and accounting closeout are excluded by the lifecycle projection.
    /// </summary>
    private IQueryable<CurrentTenantBalanceRow> CurrentTenantBalanceQuery(
        int portfolioId,
        IQueryable<Property> authorizedProperties) =>
        from balance in _db.TenantAccountBalanceProjections.AsNoTracking()
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { balance.PortfolioId, balance.LeaseManagementId }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        where balance.PortfolioId == portfolioId
            && authorizedProperties.Any(property => property.Id == lifecycle.PropertyId)
            && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
        select new CurrentTenantBalanceRow
        {
            PortfolioId = balance.PortfolioId,
            PropertyId = lifecycle.PropertyId,
            LeaseManagementId = balance.LeaseManagementId,
            TenantAccountId = balance.TenantAccountId,
            BusinessDate = balance.BusinessDate,
            ReceivableBalance = balance.ReceivableBalance,
            PastDueAmount = balance.PastDueAmount,
            PastDueCount = balance.PastDueCount,
            CurrentAgreementId = lifecycle.CurrentAgreementId,
            CurrentPrimaryPartyId = lifecycle.CurrentPrimaryPartyId,
            CurrentPrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
        };

    private sealed class PastDueSummary
    {
        public DateOnly BusinessDate { get; set; }
        public int TotalCount { get; set; }
        public decimal TotalPastDueAmount { get; set; }
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
        var topExpense = expensesByCategory.FirstOrDefault();
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

    private static string Money(decimal value)
    {
        var hasCents = decimal.Remainder(Math.Abs(value), 1m) != 0m;
        return value.ToString(hasCents ? "$#,0.00;$-#,0.00;$0.00" : "$#,0;$-#,0;$0");
    }

    public Task<AccountingTransactionsResponse> GetTransactionsAsync(
        WorkspaceReadScope scope,
        AccountingTransactionsQuery query,
        CancellationToken ct = default) =>
        GetTransactionsCoreAsync(
            scope.PortfolioId,
            query,
            AuthorizedProperties(scope, CapabilityKeys.MoneyBalancesRead),
            ct);

    private async Task<AccountingTransactionsResponse> GetTransactionsCoreAsync(
        int portfolioId,
        AccountingTransactionsQuery query,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct)
    {
        // Build the canonical transaction surface as one translated UNION ALL over tenant-account
        // ledger entries, expenses, bank movements, and the separate application-fee subledger.
        // Posted ledger debits are negative and credits are positive; no mutable Payment status is
        // consulted and security-deposit receipts remain visibly typed rather than misreported as rent.
        IQueryable<AccountingTransactionView> rows = _db.TenantLedgerEntries
            .AsNoTracking()
            .Where(entry =>
                entry.PortfolioId == portfolioId &&
                authorizedProperties.Any(property =>
                    property.Id == entry.TenantAccount!.LeaseManagement!.PropertyId))
            .Select(entry => new AccountingTransactionView
            {
                Kind = entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    ? KindPayment
                    : KindTenantLedger,
                Id = entry.Id,
                PortfolioId = entry.PortfolioId,
                Date = entry.PostedAtUtc,
                CreatedAt = entry.PostedAtUtc,
                UpdatedAt = entry.PostedAtUtc,
                Description = entry.Description,
                Category = entry.EntryType.ToString(),
                Status = entry.Direction == TenantLedgerDirection.Credit ? "Credit" : "Debit",
                Amount = entry.Direction == TenantLedgerDirection.Credit ? entry.Amount : -entry.Amount,
                TenantAccountId = entry.TenantAccountId,
                PropertyId = entry.TenantAccount!.LeaseManagement!.PropertyId,
                UnitId = entry.TenantAccount.LeaseManagement.UnitId,
                PropertyName = entry.TenantAccount.LeaseManagement.Property!.Name,
                Counterparty = _db.LeaseManagementLifecycleProjections
                    .Where(lifecycle => lifecycle.PortfolioId == entry.PortfolioId
                        && lifecycle.LeaseManagementId == entry.TenantAccount.LeaseManagementId)
                    .Select(lifecycle => lifecycle.CurrentPrimaryTenantName)
                    .FirstOrDefault(),
                Reference = entry.TenantAccount.AccountNumber,
                Notes = null,
                HasReceipt = false,
                ReceiptIsImage = false,
                Reconciled = false,
                ClearedBankName = null,
                ClearedAt = null,
            })
            .Concat(_db.Expenses.AsNoTracking()
                .Where(expense =>
                    expense.PortfolioId == portfolioId &&
                    expense.PropertyId != null &&
                    authorizedProperties.Any(property => property.Id == expense.PropertyId))
                .Select(expense => new AccountingTransactionView
                {
                    Kind = KindExpense,
                    Id = expense.Id,
                    PortfolioId = expense.PortfolioId,
                    Date = expense.PaidAt ?? expense.IncurredAt,
                    CreatedAt = expense.CreatedAt,
                    UpdatedAt = expense.UpdatedAt,
                    Description = expense.Description,
                    Category = expense.Category.ToString(),
                    Status = expense.Status.ToString(),
                    Amount = -expense.Amount,
                    TenantAccountId = null,
                    PropertyId = expense.PropertyId,
                    UnitId = expense.UnitId,
                    PropertyName = expense.Property == null ? null : expense.Property.Name,
                    Counterparty = expense.Vendor == null ? null : expense.Vendor.Name,
                    Reference = expense.WorkOrder == null ? null : expense.WorkOrder.Title,
                    Notes = expense.Notes,
                    HasReceipt = _db.StoredFiles.Any(file =>
                        file.PortfolioId == expense.PortfolioId &&
                        file.EntityType == "Expense" &&
                        file.EntityId == expense.Id &&
                        file.DeletedAt == null),
                    ReceiptIsImage = _db.StoredFiles
                        .Where(file =>
                            file.PortfolioId == expense.PortfolioId &&
                            file.EntityType == "Expense" &&
                            file.EntityId == expense.Id &&
                            file.DeletedAt == null)
                        .OrderByDescending(file => file.UploadedAt)
                        .Select(file => file.ContentType.StartsWith("image/"))
                        .FirstOrDefault(),
                    Reconciled = _db.BankTransactions.Any(bank =>
                        bank.PortfolioId == expense.PortfolioId &&
                        bank.MatchStatus == "Matched" &&
                        bank.MatchedExpenseId == expense.Id),
                    ClearedBankName = _db.BankTransactions
                        .Where(bank =>
                            bank.PortfolioId == expense.PortfolioId &&
                            bank.MatchStatus == "Matched" &&
                            bank.MatchedExpenseId == expense.Id)
                        .OrderByDescending(bank => bank.PostedAt)
                        .Select(bank => bank.BankConnection!.InstitutionName)
                        .FirstOrDefault(),
                    ClearedAt = _db.BankTransactions
                        .Where(bank =>
                            bank.PortfolioId == expense.PortfolioId &&
                            bank.MatchStatus == "Matched" &&
                            bank.MatchedExpenseId == expense.Id)
                        .OrderByDescending(bank => bank.PostedAt)
                        .Select(bank => (DateTime?)bank.PostedAt)
                        .FirstOrDefault(),
                }))
            .Concat(_db.ApplicationFinancialEntries
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entry =>
                    entry.PortfolioId == portfolioId &&
                    entry.PropertyId != null &&
                    authorizedProperties.Any(property => property.Id == entry.PropertyId))
                .Select(entry => new AccountingTransactionView
                {
                    Kind = KindApplicationFee,
                    Id = entry.Id,
                    PortfolioId = entry.PortfolioId,
                    Date = entry.OccurredAtUtc,
                    CreatedAt = entry.OccurredAtUtc,
                    UpdatedAt = entry.OccurredAtUtc,
                    Description = entry.Description,
                    Category = entry.EntryType == ApplicationFinancialEntryType.FeeCollection
                        ? "ApplicationFee"
                        : entry.EntryType == ApplicationFinancialEntryType.Refund
                            ? "ApplicationFeeRefund"
                            : "ApplicationFeeAdjustment",
                    Status = "Posted",
                    Amount = entry.Direction == ApplicationFinancialDirection.Increase
                        ? entry.Amount
                        : -entry.Amount,
                    TenantAccountId = null,
                    PropertyId = entry.PropertyId,
                    UnitId = entry.UnitId,
                    PropertyName = entry.Property == null ? null : entry.Property.Name,
                    // Deleted applications remain absent from normal navigations. Preserve the
                    // immutable financial row without re-exposing deleted applicant PII.
                    Counterparty = "Rental application",
                    Reference = entry.ProviderReference ?? entry.SourceReference ?? entry.Method,
                    Notes = null,
                    HasReceipt = false,
                    ReceiptIsImage = false,
                    Reconciled = false,
                    ClearedBankName = null,
                    ClearedAt = null,
                }));

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            var kind = query.Kind.Trim();
            if (kind.Equals(KindExpense, StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(r => r.Kind == KindExpense);
            }
            else if (kind.Equals(KindTenantLedger, StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(r => r.Kind == KindTenantLedger);
            }
            else if (kind.Equals(KindPayment, StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(r => r.Kind == KindPayment);
            }
            else if (kind.Equals(KindBank, StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(r => r.Kind == KindBank);
            }
            else if (kind.Equals(KindApplicationFee, StringComparison.OrdinalIgnoreCase))
            {
                rows = rows.Where(r => r.Kind == KindApplicationFee);
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
                    TenantAccountId = r.TenantAccountId,
                    PropertyId = r.PropertyId,
                    UnitId = r.UnitId,
                    PropertyName = r.PropertyName,
                    Counterparty = r.Counterparty,
                    DetailHref = DetailHrefFor(r.Kind, r.Id, r.TenantAccountId),
                    HasReceipt = r.HasReceipt,
                    ReceiptIsImage = r.ReceiptIsImage,
                    Reconciled = r.Reconciled,
                    ClearedBankName = r.ClearedBankName,
                    ClearedAt = r.ClearedAt,
                };

                return item;
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    /// <summary>The grid row's deep-link, derived from its source kind + id (was a view column).</summary>
    internal static string DetailHrefFor(string kind, long id, int? tenantAccountId) => kind switch
    {
        KindTenantLedger or KindPayment when tenantAccountId.HasValue =>
            $"/tenant-accounts/{tenantAccountId.Value}/entries/{id}",
        KindTenantLedger or KindPayment => throw new InvalidOperationException(
            "A tenant-ledger accounting row must carry TenantAccountId."),
        KindExpense => $"/accounting/expenses/{id}",
        KindApplicationFee => "/applications",
        _ => "/banking",
    };

    public Task<AccountingReportsResponse> GetReportsAsync(
        WorkspaceReadScope scope,
        CancellationToken ct = default) =>
        GetReportsCoreAsync(
            scope.PortfolioId,
            AuthorizedProperties(scope, CapabilityKeys.ReportsRead),
            ct);

    private async Task<AccountingReportsResponse> GetReportsCoreAsync(
        int portfolioId,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct)
    {
        var generatedAt = _timeProvider.UtcNow();

        var ledgerQuery = ReportLedgerQuery(portfolioId, authorizedProperties);
        var ledgerTotalCount = await ledgerQuery.CountAsync(ct);
        var ledgerRows = await ledgerQuery
            .OrderByDescending(l => l.Date)
            .ThenByDescending(l => l.Id)
            .Take(ReportLedgerPreviewTake)
            .ToListAsync(ct);
        var recentLedger = ledgerRows.Select(ToLedgerTransaction).ToList();

        // Aggregate each financial source by property before joining it to the authorized property
        // relation. This produces one translated statement with three derived tables and avoids
        // embedding the complete authorization query inside a correlated tenant-income projection.
        var incomeByProperty = TenantIncomeQuery(portfolioId, authorizedProperties)
            .GroupBy(income => income.PropertyId)
            .Select(group => new
            {
                PropertyId = group.Key,
                Total = group.Sum(income => (decimal?)income.Amount),
            });
        var expensesByProperty = _db.Expenses
            .AsNoTracking()
            .Where(expense =>
                expense.PortfolioId == portfolioId &&
                expense.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == expense.PropertyId))
            .GroupBy(expense => expense.PropertyId!.Value)
            .Select(group => new
            {
                PropertyId = group.Key,
                Total = group.Sum(expense => (decimal?)expense.Amount),
            });
        var pastDueByProperty =
            from balance in CurrentTenantBalanceQuery(portfolioId, authorizedProperties)
            where balance.PastDueAmount > 0m
            group balance by balance.PropertyId
            into balances
            select new
            {
                PropertyId = balances.Key,
                Total = balances.Sum(balance => (decimal?)balance.PastDueAmount),
                Count = balances.Sum(balance => (int?)balance.PastDueCount),
            };

        var propertyRows = await (
                from property in authorizedProperties
                join income in incomeByProperty
                    on property.Id equals income.PropertyId into propertyIncome
                from income in propertyIncome.DefaultIfEmpty()
                join expense in expensesByProperty
                    on property.Id equals expense.PropertyId into propertyExpenses
                from expense in propertyExpenses.DefaultIfEmpty()
                join pastDue in pastDueByProperty
                    on property.Id equals pastDue.PropertyId into propertyPastDue
                from pastDue in propertyPastDue.DefaultIfEmpty()
                orderby property.Name
                select new
                {
                    property.Id,
                    property.Name,
                    Income = income.Total ?? 0m,
                    Expenses = expense.Total ?? 0m,
                    Overdue = pastDue.Total ?? 0m,
                    OverdueCount = pastDue.Count ?? 0,
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

        var scheduleE = await BuildEmbeddedScheduleETotalsAsync(
            portfolioId, generatedAt.Year, authorizedProperties, ct);

        var vendors1099 = await _db.Vendors
            .AsNoTracking()
            .Where(v =>
                v.PortfolioId == portfolioId &&
                v.Expenses.Any(expense =>
                    expense.PropertyId != null &&
                    authorizedProperties.Any(property => property.Id == expense.PropertyId)))
            .Select(v => new
            {
                v.Id,
                v.Name,
                v.Is1099Eligible,
                v.W9OnFile,
                TotalPaid = v.Expenses
                    .Where(e =>
                        (e.Status == ExpenseStatus.Paid || e.PaidAt != null) &&
                        e.PropertyId != null &&
                        authorizedProperties.Any(property => property.Id == e.PropertyId))
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
        var paidPaymentTotal = await TenantIncomeQuery(portfolioId, authorizedProperties)
            .SumAsync(row => (decimal?)row.Amount, ct) ?? 0m;

        var expenseTotal = await _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == e.PropertyId))
            .SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;

        var totalIncome = paidPaymentTotal;
        var totalExpenses = expenseTotal;

        return new AccountingReportsResponse
        {
            PortfolioId = portfolioId,
            GeneratedAt = generatedAt,
            TotalIncome = totalIncome,
            TotalExpenses = totalExpenses,
            NetCashFlow = totalIncome - totalExpenses,
            LedgerTotalCount = ledgerTotalCount,
            RecentLedger = recentLedger,
            Ledger = recentLedger,
            Properties = propertyReports,
            ScheduleE = scheduleE,
            Vendors1099 = vendorReports,
        };
    }

    private async Task<IReadOnlyList<ScheduleECategoryTotal>> BuildEmbeddedScheduleETotalsAsync(
        int portfolioId,
        int year,
        IQueryable<Property> authorizedProperties,
        CancellationToken ct)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEndExclusive = yearStart.AddYears(1);

        var loanPropertyIdsQuery = _db.Loans
            .AsNoTracking()
            .Where(l =>
                l.PortfolioId == portfolioId &&
                authorizedProperties.Any(property => property.Id == l.PropertyId))
            .Select(l => l.PropertyId)
            .Distinct();

        var depreciationQuery = ScheduleEDepreciationQuery.Build(
            _db, portfolioId, year, authorizedProperties);

        var expenseComponents = _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == e.PropertyId) &&
                e.CapitalizedAssetId == null &&
                e.IncurredAt >= yearStart &&
                e.IncurredAt < yearEndExclusive &&
                !(e.Category == ScheduleECategory.MortgageInterest &&
                  e.PropertyId != null &&
                  loanPropertyIdsQuery.Contains(e.PropertyId.Value)) &&
                !(e.Category == ScheduleECategory.Depreciation &&
                  e.PropertyId != null &&
                  depreciationQuery.Any(row => row.PropertyId == e.PropertyId.Value)))
            .Select(e => new
            {
                e.Category,
                Total = e.Amount,
                Count = 1,
            });

        var modeledInterestComponents = _db.LoanPayments
            .AsNoTracking()
            .Where(lp =>
                lp.PortfolioId == portfolioId &&
                lp.Status == LoanPaymentStatus.Paid &&
                lp.PaidDate != null &&
                lp.Loan != null &&
                authorizedProperties.Any(property => property.Id == lp.Loan.PropertyId) &&
                lp.PaidDate >= yearStart &&
                lp.PaidDate < yearEndExclusive)
            .Select(lp => new
            {
                Category = ScheduleECategory.MortgageInterest,
                Total = lp.InterestAmount,
                Count = 1,
            });

        var depreciationComponents = depreciationQuery.Select(row => new
        {
            Category = ScheduleECategory.Depreciation,
            Total = row.Amount,
            Count = 1,
        });

        return await expenseComponents
            .Concat(modeledInterestComponents)
            .Concat(depreciationComponents)
            .GroupBy(component => component.Category)
            .Select(group => new ScheduleECategoryTotal
            {
                Category = group.Key,
                CategoryName = group.Key.ToString(),
                Total = group.Sum(component => component.Total),
                Count = group.Sum(component => component.Count),
            })
            .OrderByDescending(row => row.Total)
            .ToListAsync(ct);
    }

    private IQueryable<AccountingReportLedgerRow> ReportLedgerQuery(
        int portfolioId,
        IQueryable<Property> authorizedProperties)
    {
        var tenantReceipts = TenantIncomeQuery(portfolioId, authorizedProperties)
            .GroupBy(row => new
            {
                row.ReceiptId,
                row.TenantAccountId,
                row.LeaseManagementId,
                row.PostedAtUtc,
                row.Description,
                row.PropertyId,
                row.UnitId,
            })
            .Select(g => new AccountingReportLedgerRow
            {
                Date = g.Key.PostedAtUtc,
                Type = KindTenantLedger,
                Id = g.Key.ReceiptId,
                TenantAccountId = g.Key.TenantAccountId,
                Amount = g.Sum(row => row.Amount),
                PropertyId = g.Key.PropertyId,
                PropertyName = _db.Properties
                    .Where(property => property.PortfolioId == portfolioId && property.Id == g.Key.PropertyId)
                    .Select(property => property.Name)
                    .FirstOrDefault(),
                CounterpartyName = _db.LeaseManagementLifecycleProjections
                    .Where(lifecycle => lifecycle.PortfolioId == portfolioId
                        && lifecycle.LeaseManagementId == g.Key.LeaseManagementId)
                    .Select(lifecycle => lifecycle.CurrentPrimaryTenantName)
                    .FirstOrDefault(),
                Description = g.Key.Description,
                Category = nameof(TenantLedgerEntryType.PaymentReceipt),
                Status = nameof(TenantLedgerDirection.Credit),
                ExpenseCategory = null,
                ExpenseStatus = null,
                OriginalExpenseAmount = 0m,
                ExpenseDate = null,
            });

        var expenses = _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.PropertyId != null &&
                authorizedProperties.Any(property => property.Id == e.PropertyId))
            .Select(e => new AccountingReportLedgerRow
            {
                Date = e.PaidAt ?? e.IncurredAt,
                Type = KindExpense,
                Id = e.Id,
                Description = e.Description,
                Amount = -e.Amount,
                TenantAccountId = null,
                PropertyId = e.PropertyId,
                PropertyName = e.Property != null ? e.Property.Name : null,
                CounterpartyName = e.Vendor != null ? e.Vendor.Name : null,
                ExpenseCategory = e.Category,
                ExpenseStatus = e.Status,
                OriginalExpenseAmount = e.Amount,
                ExpenseDate = e.PaidAt ?? e.IncurredAt,
                Category = null,
                Status = null,
            });

        return tenantReceipts.Concat(expenses);
    }

    private static LedgerTransactionResponse ToLedgerTransaction(AccountingReportLedgerRow row)
    {
        if (row.Type == KindTenantLedger)
        {
            return new LedgerTransactionResponse
            {
                Date = row.Date,
                Type = KindTenantLedger,
                Id = row.Id,
                TenantAccountId = row.TenantAccountId,
                Description = row.Description ?? "Tenant receipt",
                Amount = row.Amount,
                PropertyId = row.PropertyId,
                PropertyName = row.PropertyName,
                Counterparty = row.CounterpartyName,
                Category = row.Category,
                Status = row.Status ?? nameof(TenantLedgerDirection.Credit),
                SourceHref = $"/tenant-accounts/{row.TenantAccountId}/entries/{row.Id}",
                Explanation = $"Tenant receipt posted on {row.Date:MMM d, yyyy}.",
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

    public async Task<byte[]> GetYearEndPacketAsync(
        WorkspaceReadScope scope,
        int year,
        CancellationToken ct = default)
    {
        var data = await GetYearEndPacketDataAsync(scope, year, ct);
        return _packetPdf.Generate(data);
    }

    public Task<YearEndPacketData> GetYearEndPacketDataAsync(
        WorkspaceReadScope scope,
        int year,
        CancellationToken ct = default) =>
        GetYearEndPacketDataCoreAsync(
            scope.PortfolioId,
            year,
            AuthorizedProperties(scope, CapabilityKeys.ReportsRead),
            scope,
            ct);

    private async Task<YearEndPacketData> GetYearEndPacketDataCoreAsync(
        int portfolioId,
        int year,
        IQueryable<Property> authorizedProperties,
        WorkspaceReadScope workspaceScope,
        CancellationToken ct)
    {
        var now = _timeProvider.UtcNow();

        // ── Portfolio header ────────────────────────────────────────────────────────────────────
        var portfolio = await _db.Portfolios
            .AsNoTracking()
            .Where(p =>
                p.Id == portfolioId &&
                authorizedProperties.Any())
            .Select(p => new { p.Name, p.ManagementCompanyName })
            .FirstOrDefaultAsync(ct);

        // ── Schedule E (reused so the packet matches the existing CSV/report exactly) ─────────────
        var scheduleE = await _scheduleE.GetReportAsync(workspaceScope, year, ct: ct);

        // ── Per-property P&L for the year ─────────────────────────────────────────────────────────
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEndExclusive = yearStart.AddYears(1);

        var paidExpenseQuery = FinancialReportProjections.BuildExpenseAllocationProjection(_db, portfolioId)
            .Where(expense =>
                expense.Status == ExpenseStatus.Paid &&
                expense.EffectiveAt >= yearStart &&
                expense.EffectiveAt < yearEndExclusive &&
                ((expense.PropertyId != null &&
                    authorizedProperties.Any(property => property.Id == expense.PropertyId.Value)) ||
                 (expense.PropertyId == null &&
                    BuildAllPropertiesAuthorityQuery(workspaceScope, CapabilityKeys.ReportsRead).Any())));
        var yearStartOn = new DateOnly(year, 1, 1);
        var yearEndOn = yearStartOn.AddYears(1);
        var tenantIncomeForYear = TenantIncomeQuery(portfolioId, authorizedProperties)
            .Where(income => income.EffectiveOn >= yearStartOn && income.EffectiveOn < yearEndOn);

        var propertyFinancialTotalsQuery = tenantIncomeForYear
            .Select(income => new
            {
                income.PropertyId,
                Income = income.Amount,
                Expense = 0m,
                ExpenseCategory = (ScheduleECategory?)null,
            })
            .Concat(paidExpenseQuery
                .Where(expense => expense.PropertyId != null)
                .Select(expense => new
                {
                    PropertyId = expense.PropertyId!.Value,
                    Income = 0m,
                    Expense = expense.Amount,
                    ExpenseCategory = (ScheduleECategory?)expense.Category,
                }))
            .GroupBy(component => component.PropertyId)
            .Select(group => new
            {
                PropertyId = group.Key,
                Income = group.Sum(component => component.Income),
                TotalExpenses = group.Sum(component => component.Expense),
                Advertising = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.Advertising ? component.Expense : 0m),
                AutoTravel = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.AutoTravel ? component.Expense : 0m),
                CleaningMaintenance = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.CleaningMaintenance ? component.Expense : 0m),
                Commissions = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.Commissions ? component.Expense : 0m),
                Insurance = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.Insurance ? component.Expense : 0m),
                LegalProfessional = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.LegalProfessional ? component.Expense : 0m),
                ManagementFees = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.ManagementFees ? component.Expense : 0m),
                MortgageInterest = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.MortgageInterest ? component.Expense : 0m),
                Repairs = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.Repairs ? component.Expense : 0m),
                Supplies = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.Supplies ? component.Expense : 0m),
                Taxes = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.Taxes ? component.Expense : 0m),
                Utilities = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.Utilities ? component.Expense : 0m),
                Depreciation = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.Depreciation ? component.Expense : 0m),
                Other = group.Sum(component =>
                    component.ExpenseCategory == ScheduleECategory.Other ? component.Expense : 0m),
            });

        var properties = await (
                from property in authorizedProperties
                join totals in propertyFinancialTotalsQuery on property.Id equals totals.PropertyId
                orderby property.Name
                select new
                {
                    property.Id,
                    property.Name,
                    totals.Income,
                    totals.TotalExpenses,
                    totals.Advertising,
                    totals.AutoTravel,
                    totals.CleaningMaintenance,
                    totals.Commissions,
                    totals.Insurance,
                    totals.LegalProfessional,
                    totals.ManagementFees,
                    totals.MortgageInterest,
                    totals.Repairs,
                    totals.Supplies,
                    totals.Taxes,
                    totals.Utilities,
                    totals.Depreciation,
                    totals.Other,
                })
            .ToListAsync(ct);

        var propertyPnL = properties
            .Select(property => new YearEndPropertyPnL
            {
                PropertyId = property.Id,
                PropertyName = property.Name,
                Income = property.Income,
                ExpensesByCategory = BuildYearEndExpenseCategories(
                    property.Advertising,
                    property.AutoTravel,
                    property.CleaningMaintenance,
                    property.Commissions,
                    property.Insurance,
                    property.LegalProfessional,
                    property.ManagementFees,
                    property.MortgageInterest,
                    property.Repairs,
                    property.Supplies,
                    property.Taxes,
                    property.Utilities,
                    property.Depreciation,
                    property.Other),
                TotalExpenses = property.TotalExpenses,
                Net = property.Income - property.TotalExpenses,
            })
            .ToList();

        // ── Cash flow by month ────────────────────────────────────────────────────────────────────
        // Derive a guaranteed 12-row calendar relation from the authorized portfolio itself. EF emits
        // one UNION ALL query whose correlated sums fill every month; no dictionary join or per-month
        // query occurs in application code.
        var authorizedPortfolio = _db.Portfolios
            .AsNoTracking()
            .Where(candidate => candidate.Id == portfolioId && authorizedProperties.Any());
        var months = authorizedPortfolio.Select(_ => 1)
            .Concat(authorizedPortfolio.Select(_ => 2))
            .Concat(authorizedPortfolio.Select(_ => 3))
            .Concat(authorizedPortfolio.Select(_ => 4))
            .Concat(authorizedPortfolio.Select(_ => 5))
            .Concat(authorizedPortfolio.Select(_ => 6))
            .Concat(authorizedPortfolio.Select(_ => 7))
            .Concat(authorizedPortfolio.Select(_ => 8))
            .Concat(authorizedPortfolio.Select(_ => 9))
            .Concat(authorizedPortfolio.Select(_ => 10))
            .Concat(authorizedPortfolio.Select(_ => 11))
            .Concat(authorizedPortfolio.Select(_ => 12));
        var cashFlowRows = await months
            .OrderBy(month => month)
            .Select(month => new YearEndCashFlowSqlRow
            {
                Month = month,
                MoneyIn = tenantIncomeForYear
                    .Where(income => income.EffectiveOn.Month == month)
                    .Sum(income => (decimal?)income.Amount) ?? 0m,
                MoneyOut = paidExpenseQuery
                    .Where(expense => expense.EffectiveAt.Month == month)
                    .Sum(expense => (decimal?)expense.Amount) ?? 0m,
                TotalMoneyIn = tenantIncomeForYear.Sum(income => (decimal?)income.Amount) ?? 0m,
                TotalMoneyOut = paidExpenseQuery.Sum(expense => (decimal?)expense.Amount) ?? 0m,
            })
            .ToListAsync(ct);
        var cashFlow = cashFlowRows
            .Select(row => new YearEndCashFlowMonth
            {
                Month = row.Month,
                MonthName = System.Globalization.CultureInfo.InvariantCulture
                    .DateTimeFormat.GetAbbreviatedMonthName(row.Month),
                MoneyIn = row.MoneyIn,
                MoneyOut = row.MoneyOut,
                Net = row.MoneyIn - row.MoneyOut,
            })
            .ToList();
        var cashIn = cashFlowRows.Count == 0 ? 0m : cashFlowRows[0].TotalMoneyIn;
        var cashOut = cashFlowRows.Count == 0 ? 0m : cashFlowRows[0].TotalMoneyOut;

        // ── Rent roll ─────────────────────────────────────────────────────────────────────────────
        var rentRollRows = await (
                from lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                join management in _db.LeaseManagements.AsNoTracking()
                    on new { lifecycle.PortfolioId, Id = lifecycle.LeaseManagementId }
                    equals new { management.PortfolioId, management.Id }
                join occupancy in _db.UnitOccupancyProjections.AsNoTracking()
                    on new { lifecycle.PortfolioId, lifecycle.UnitId }
                    equals new { occupancy.PortfolioId, occupancy.UnitId }
                join agreement in _db.LeaseAgreements.AsNoTracking()
                    on new { management.PortfolioId, LeaseManagementId = management.Id }
                    equals new { agreement.PortfolioId, agreement.LeaseManagementId }
                join status in _db.LeaseAgreementStatusProjections.AsNoTracking()
                    on new { lifecycle.PortfolioId, AgreementId = agreement.Id }
                    equals new { status.PortfolioId, status.AgreementId }
                join balance in _db.TenantAccountBalanceProjections.AsNoTracking()
                    on new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                    equals new { balance.PortfolioId, balance.LeaseManagementId }
                where lifecycle.PortfolioId == portfolioId
                    && authorizedProperties.Any(property => property.Id == management.PropertyId)
                    && occupancy.IsOccupied
                    && occupancy.CurrentLeaseManagementId == lifecycle.LeaseManagementId
                    && (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending")
                    && agreement.FullyExecutedAtUtc != null
                    && agreement.VoidedAtUtc == null
                    && agreement.DraftCanceledAtUtc == null
                    && agreement.GoverningFromOn <= lifecycle.BusinessDate
                    && !_db.LeaseAgreements.Any(candidate =>
                        candidate.PortfolioId == agreement.PortfolioId &&
                        candidate.LeaseManagementId == agreement.LeaseManagementId &&
                        candidate.FullyExecutedAtUtc != null &&
                        candidate.VoidedAtUtc == null &&
                        candidate.DraftCanceledAtUtc == null &&
                        candidate.GoverningFromOn <= lifecycle.BusinessDate &&
                        (candidate.GoverningFromOn > agreement.GoverningFromOn ||
                         (candidate.GoverningFromOn == agreement.GoverningFromOn &&
                          candidate.VersionNumber > agreement.VersionNumber) ||
                         (candidate.GoverningFromOn == agreement.GoverningFromOn &&
                          candidate.VersionNumber == agreement.VersionNumber &&
                          candidate.Id > agreement.Id)))
                orderby management.Property!.Name, management.Unit!.UnitNumber
                select new
                {
                    PropertyName = management.Property!.Name,
                    UnitNumber = management.Unit!.UnitNumber,
                    TenantName = lifecycle.CurrentPrimaryTenantName ?? "Tenant",
                    MonthlyRent = agreement.BaseRentAmount,
                    agreement.TermStartOn,
                    agreement.TermEndOn,
                    status.AgreementStatus,
                    PastDueBalance = balance.PastDueAmount,
                })
            .ToListAsync(ct);

        var rentRoll = rentRollRows
            .Select(l => new YearEndRentRollRow
            {
                PropertyName = l.PropertyName,
                UnitNumber = l.UnitNumber,
                TenantName = l.TenantName,
                MonthlyRent = l.MonthlyRent,
                LeaseStart = l.TermStartOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                LeaseEnd = (l.TermEndOn ?? l.TermStartOn).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                LeaseStatus = l.AgreementStatus,
                PastDueBalance = l.PastDueBalance,
            })
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

    private IQueryable<TenantIncomeRow> TenantIncomeQuery(
        int portfolioId,
        IQueryable<Property> authorizedProperties) =>
        from allocation in _db.TenantLedgerAllocations.AsNoTracking()
        join receipt in _db.TenantLedgerEntries.AsNoTracking()
            on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.CreditEntryId }
            equals new { receipt.PortfolioId, receipt.TenantAccountId, receipt.Id }
        join charge in _db.TenantLedgerEntries.AsNoTracking()
            on new { allocation.PortfolioId, allocation.TenantAccountId, Id = allocation.DebitEntryId }
            equals new { charge.PortfolioId, charge.TenantAccountId, charge.Id }
        join account in _db.TenantAccounts.AsNoTracking()
            on new { allocation.PortfolioId, Id = allocation.TenantAccountId }
            equals new { account.PortfolioId, account.Id }
        join management in _db.LeaseManagements.AsNoTracking()
            on new { account.PortfolioId, Id = account.LeaseManagementId }
            equals new { management.PortfolioId, management.Id }
        where allocation.PortfolioId == portfolioId
            && authorizedProperties.Any(property => property.Id == management.PropertyId)
            && receipt.EntryType == TenantLedgerEntryType.PaymentReceipt
            && charge.EntryType != TenantLedgerEntryType.DepositCharge
        select new TenantIncomeRow
        {
            ReceiptId = receipt.Id,
            TenantAccountId = account.Id,
            LeaseManagementId = management.Id,
            EffectiveOn = receipt.EffectiveOn,
            PostedAtUtc = receipt.PostedAtUtc,
            Description = receipt.Description,
            Amount = allocation.Amount,
            PropertyId = management.PropertyId,
            UnitId = management.UnitId,
        };

    private IQueryable<Property> AuthorizedProperties(
        WorkspaceReadScope scope,
        string capabilityKey) =>
        _db.Properties
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                capabilityKey,
                _timeProvider.UtcNow());

    private IQueryable<int> BuildAllPropertiesAuthorityQuery(
        WorkspaceReadScope scope,
        string capabilityKey) =>
        _db.AuthorizedWorkspaceAssignments(
                scope,
                [capabilityKey],
                CapabilityAuthorizationTargetKind.Property,
                _timeProvider.GetUtcNow().UtcDateTime)
            .Select(_ => 1);

    private static IReadOnlyList<ScheduleECategoryAmount> BuildYearEndExpenseCategories(
        decimal advertising,
        decimal autoTravel,
        decimal cleaningMaintenance,
        decimal commissions,
        decimal insurance,
        decimal legalProfessional,
        decimal managementFees,
        decimal mortgageInterest,
        decimal repairs,
        decimal supplies,
        decimal taxes,
        decimal utilities,
        decimal depreciation,
        decimal other)
    {
        var categories = new List<ScheduleECategoryAmount>(14);
        AddYearEndExpenseCategory(categories, ScheduleECategory.Advertising, advertising);
        AddYearEndExpenseCategory(categories, ScheduleECategory.AutoTravel, autoTravel);
        AddYearEndExpenseCategory(categories, ScheduleECategory.CleaningMaintenance, cleaningMaintenance);
        AddYearEndExpenseCategory(categories, ScheduleECategory.Commissions, commissions);
        AddYearEndExpenseCategory(categories, ScheduleECategory.Insurance, insurance);
        AddYearEndExpenseCategory(categories, ScheduleECategory.LegalProfessional, legalProfessional);
        AddYearEndExpenseCategory(categories, ScheduleECategory.ManagementFees, managementFees);
        AddYearEndExpenseCategory(categories, ScheduleECategory.MortgageInterest, mortgageInterest);
        AddYearEndExpenseCategory(categories, ScheduleECategory.Repairs, repairs);
        AddYearEndExpenseCategory(categories, ScheduleECategory.Supplies, supplies);
        AddYearEndExpenseCategory(categories, ScheduleECategory.Taxes, taxes);
        AddYearEndExpenseCategory(categories, ScheduleECategory.Utilities, utilities);
        AddYearEndExpenseCategory(categories, ScheduleECategory.Depreciation, depreciation);
        AddYearEndExpenseCategory(categories, ScheduleECategory.Other, other);
        return categories;
    }

    private static void AddYearEndExpenseCategory(
        ICollection<ScheduleECategoryAmount> categories,
        ScheduleECategory category,
        decimal amount)
    {
        if (amount != 0m)
        {
            categories.Add(new ScheduleECategoryAmount(category.ToString(), amount));
        }
    }

    private sealed class TenantIncomeRow
    {
        public long ReceiptId { get; set; }
        public int TenantAccountId { get; set; }
        public int LeaseManagementId { get; set; }
        public DateOnly EffectiveOn { get; set; }
        public DateTime PostedAtUtc { get; set; }
        public string Description { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public int PropertyId { get; set; }
        public int UnitId { get; set; }
    }

    private sealed class CurrentTenantBalanceRow
    {
        public int PortfolioId { get; set; }
        public int PropertyId { get; set; }
        public int LeaseManagementId { get; set; }
        public int TenantAccountId { get; set; }
        public DateOnly BusinessDate { get; set; }
        public decimal ReceivableBalance { get; set; }
        public decimal PastDueAmount { get; set; }
        public int PastDueCount { get; set; }
        public int? CurrentAgreementId { get; set; }
        public int? CurrentPrimaryPartyId { get; set; }
        public string? CurrentPrimaryTenantName { get; set; }
    }

    private sealed class YearEndCashFlowSqlRow
    {
        public int Month { get; set; }
        public decimal MoneyIn { get; set; }
        public decimal MoneyOut { get; set; }
        public decimal TotalMoneyIn { get; set; }
        public decimal TotalMoneyOut { get; set; }
    }

    private sealed class AccountingReportLedgerRow
    {
        public DateTime Date { get; set; }
        public string Type { get; set; } = string.Empty;
        public long Id { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public int? TenantAccountId { get; set; }
        public int? PropertyId { get; set; }
        public string? PropertyName { get; set; }
        public string? CounterpartyName { get; set; }
        public string? Category { get; set; }
        public string? Status { get; set; }
        public ScheduleECategory? ExpenseCategory { get; set; }
        public ExpenseStatus? ExpenseStatus { get; set; }
        public decimal OriginalExpenseAmount { get; set; }
        public DateTime? ExpenseDate { get; set; }
    }

}
