using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
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

    public AccountingService(RentalCommandDbContext db)
    {
        _db = db;
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
        // is the subset of that which is past its due date.
        var now = DateTime.UtcNow;
        var payments = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .Select(p => new { p.Status, p.Amount, p.DueDate })
            .ToListAsync(ct);

        var rollup = new PaymentRollup();
        foreach (var p in payments)
        {
            if (p.Status == PaymentStatus.Paid)
            {
                rollup.Collected += p.Amount;
                continue;
            }

            // Waived/Failed/Refunded are not owed money to collect.
            var owed = p.Status is PaymentStatus.Scheduled or PaymentStatus.Partial or PaymentStatus.Late;
            if (!owed)
            {
                continue;
            }

            rollup.Outstanding += p.Amount;

            if (p.Status == PaymentStatus.Late || p.DueDate < now)
            {
                rollup.Overdue += p.Amount;
                rollup.OverdueCount++;
            }
        }

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

    private static MoneySnapshotResponse BuildMoneySnapshot(
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

        return new MoneySnapshotResponse
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
        var rows = BuildTransactionRows(portfolioId);

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
            var status = query.Status.Trim().ToLower();
            rows = rows.Where(r => r.Status.ToLower() == status);
        }

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            var category = query.Category.Trim().ToLower();
            rows = rows.Where(r => r.Category.ToLower() == category);
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
            var term = query.Search.Trim().ToLower();
            rows = rows.Where(r =>
                r.Description.ToLower().Contains(term) ||
                (r.PropertyName != null && r.PropertyName.ToLower().Contains(term)) ||
                (r.Counterparty != null && r.Counterparty.ToLower().Contains(term)) ||
                (r.Reference != null && r.Reference.ToLower().Contains(term)) ||
                (r.Notes != null && r.Notes.ToLower().Contains(term)));
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
            _ => rows.OrderByDescending(r => r.Date).ThenByDescending(r => r.Id),
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

        return new AccountingTransactionsResponse
        {
            Items = pageRows.Select(r =>
            {
                var item = new AccountingTransactionResponse
                {
                    Kind = r.Kind,
                    Id = r.Id,
                    Date = r.Date,
                    Description = r.Description,
                    Category = r.Category,
                    Status = r.Status,
                    Amount = r.Amount,
                    PropertyId = r.PropertyId,
                    PropertyName = r.PropertyName,
                    Counterparty = r.Counterparty,
                    DetailHref = r.DetailHref,
                };

                if (r.Kind == KindExpense && filesByExpenseId.TryGetValue(r.Id, out var contentType))
                {
                    item.HasReceipt = true;
                    item.ReceiptIsImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                }

                return item;
            }).ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<AccountingReportsResponse> GetReportsAsync(int portfolioId, CancellationToken ct = default)
    {
        var generatedAt = DateTime.UtcNow;

        var paymentRows = await _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .Select(p => new
            {
                p.Id,
                p.Amount,
                p.DueDate,
                p.PaidDate,
                p.PaymentType,
                p.Status,
                p.Method,
                p.ExternalReference,
                PropertyId = (int?)p.Lease!.PropertyId,
                PropertyName = p.Lease!.Property!.Name,
                TenantFirstName = p.Lease!.Tenant!.FirstName,
                TenantLastName = p.Lease!.Tenant!.LastName,
            })
            .ToListAsync(ct);

        var expenseRows = await _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId)
            .Select(e => new
            {
                e.Id,
                e.Amount,
                e.IncurredAt,
                e.PaidAt,
                e.Category,
                e.Description,
                e.Status,
                e.PropertyId,
                PropertyName = e.Property != null ? e.Property.Name : null,
                e.VendorId,
                VendorName = e.Vendor != null ? e.Vendor.Name : null,
            })
            .ToListAsync(ct);

        var bankRows = await _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.MatchStatus != "Removed")
            .Select(t => new
            {
                t.Id,
                t.Amount,
                t.PostedAt,
                t.Description,
                t.MerchantName,
                t.Category,
                t.MatchStatus,
                t.MatchedPaymentId,
                t.MatchedExpenseId,
                InstitutionName = t.BankConnection!.InstitutionName,
                AccountName = t.BankConnection!.AccountName,
            })
            .ToListAsync(ct);

        var ledger = paymentRows
            .Select(p => new LedgerTransactionResponse
            {
                Date = p.PaidDate ?? p.DueDate,
                Type = "Payment",
                Id = p.Id,
                Description = p.PaymentType.ToString(),
                Amount = p.Status == PaymentStatus.Paid ? p.Amount : 0m,
                PropertyId = p.PropertyId,
                PropertyName = p.PropertyName,
                Counterparty = FullName(p.TenantFirstName, p.TenantLastName),
                Category = p.Method,
                Status = p.Status.ToString(),
                SourceHref = $"/accounting/payments/{p.Id}",
            })
            .Concat(expenseRows.Select(e => new LedgerTransactionResponse
            {
                Date = e.PaidAt ?? e.IncurredAt,
                Type = "Expense",
                Id = e.Id,
                Description = e.Description,
                Amount = -e.Amount,
                PropertyId = e.PropertyId,
                PropertyName = e.PropertyName,
                Counterparty = e.VendorName,
                Category = e.Category.ToString(),
                Status = e.Status.ToString(),
                SourceHref = $"/accounting/expenses/{e.Id}",
            }))
            .Concat(bankRows
                .Where(b => b.MatchedPaymentId == null && b.MatchedExpenseId == null)
                .Select(b => new LedgerTransactionResponse
                {
                    Date = b.PostedAt,
                    Type = "Bank",
                    Id = b.Id,
                    Description = b.Description,
                    Amount = b.Amount,
                    PropertyId = null,
                    PropertyName = null,
                    Counterparty = b.MerchantName ?? b.InstitutionName,
                    Category = b.Category ?? (b.Amount >= 0 ? "Deposit" : "Withdrawal"),
                    Status = b.MatchStatus,
                    SourceHref = "/banking",
            }))
            .OrderByDescending(l => l.Date)
            .ThenByDescending(l => l.Id)
            .ToList();

        var paidIncomeByProperty = paymentRows
            .Where(p => p.Status == PaymentStatus.Paid && p.PropertyId.HasValue)
            .GroupBy(p => p.PropertyId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

        var expensesByProperty = expenseRows
            .Where(e => e.PropertyId.HasValue)
            .GroupBy(e => e.PropertyId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));

        var overdueRows = paymentRows
            .Where(p => p.PropertyId.HasValue && IsOwedPayment(p.Status) && (p.Status == PaymentStatus.Late || p.DueDate < generatedAt))
            .GroupBy(p => p.PropertyId!.Value)
            .ToDictionary(g => g.Key, g => new { Total = g.Sum(p => p.Amount), Count = g.Count() });

        var properties = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var propertyReports = properties
            .Select(p =>
            {
                paidIncomeByProperty.TryGetValue(p.Id, out var income);
                expensesByProperty.TryGetValue(p.Id, out var expenses);
                overdueRows.TryGetValue(p.Id, out var overdue);

                return new PropertyFinancialSummaryResponse
                {
                    PropertyId = p.Id,
                    PropertyName = p.Name,
                    Income = income,
                    Expenses = expenses,
                    Net = income - expenses,
                    Overdue = overdue?.Total ?? 0m,
                    OverdueCount = overdue?.Count ?? 0,
                };
            })
            .ToList();

        var scheduleE = expenseRows
            .GroupBy(e => e.Category)
            .OrderByDescending(g => g.Sum(e => e.Amount))
            .Select(g => new ScheduleECategoryTotal
            {
                Category = g.Key,
                CategoryName = g.Key.ToString(),
                Total = g.Sum(e => e.Amount),
                Count = g.Count(),
            })
            .ToList();

        var vendors1099 = await _db.Vendors
            .AsNoTracking()
            .Where(v => v.PortfolioId == portfolioId)
            .OrderBy(v => v.Name)
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
            .ToListAsync(ct);

        var vendorReports = vendors1099
            .Where(v => v.Is1099Eligible || v.TotalPaid > 0)
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

        var totalIncome = paymentRows
            .Where(p => p.Status == PaymentStatus.Paid)
            .Sum(p => p.Amount) +
            bankRows.Where(b => b.Amount > 0 && b.MatchedPaymentId == null).Sum(b => b.Amount);
        var totalExpenses = expenseRows.Sum(e => e.Amount) +
            bankRows.Where(b => b.Amount < 0 && b.MatchedExpenseId == null).Sum(b => Math.Abs(b.Amount));

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

    private static bool IsOwedPayment(PaymentStatus status) =>
        status is PaymentStatus.Scheduled or PaymentStatus.Partial or PaymentStatus.Late;

    private static string FullName(string firstName, string lastName)
    {
        var fullName = $"{firstName} {lastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName) ? "Tenant" : fullName;
    }

    private IQueryable<AccountingTransactionRow> BuildTransactionRows(int portfolioId)
    {
        var payments = _db.Payments
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId)
            .Select(p => new AccountingTransactionRow
            {
                Kind = KindPayment,
                Id = p.Id,
                Date = p.PaidDate ?? p.DueDate,
                Description = p.Notes != null && p.Notes != ""
                    ? p.Notes
                    : p.PaymentType.ToString() + " - " + p.Lease!.Tenant!.FirstName + " " + p.Lease!.Tenant!.LastName,
                Category = p.PaymentType.ToString(),
                Status = p.Status.ToString(),
                Amount = p.Amount,
                PropertyId = p.Lease!.PropertyId,
                PropertyName = p.Lease!.Property!.Name,
                Counterparty = p.Lease!.Tenant!.FirstName + " " + p.Lease!.Tenant!.LastName,
                Reference = p.Lease!.LeaseNumber + " " + (p.Method ?? "") + " " + (p.ExternalReference ?? ""),
                Notes = p.Notes,
                DetailHref = "/accounting/payments/" + p.Id,
            });

        var expenses = _db.Expenses
            .AsNoTracking()
            .Where(e => e.PortfolioId == portfolioId)
            .Select(e => new AccountingTransactionRow
            {
                Kind = KindExpense,
                Id = e.Id,
                Date = e.PaidAt ?? e.IncurredAt,
                Description = e.Description,
                Category = e.Category.ToString(),
                Status = e.Status.ToString(),
                Amount = e.Amount,
                PropertyId = e.PropertyId,
                PropertyName = e.Property != null ? e.Property.Name : null,
                Counterparty = e.Vendor != null ? e.Vendor.Name : null,
                Reference = e.WorkOrder != null ? e.WorkOrder.Title : null,
                Notes = e.Notes,
                DetailHref = "/accounting/expenses/" + e.Id,
            });

        var bankTransactions = _db.BankTransactions
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.MatchStatus != "Removed")
            .Select(t => new AccountingTransactionRow
            {
                Kind = KindBank,
                Id = t.Id,
                Date = t.PostedAt,
                Description = t.Description,
                Category = t.Category ?? (t.Amount >= 0 ? "Deposit" : "Withdrawal"),
                Status = t.MatchStatus,
                Amount = t.Amount,
                PropertyId = null,
                PropertyName = null,
                Counterparty = t.MerchantName ?? t.BankConnection!.InstitutionName,
                Reference = t.BankConnection!.AccountName + " " + t.ProviderTransactionId,
                Notes = t.Notes,
                DetailHref = "/banking",
            });

        return payments.Concat(expenses).Concat(bankTransactions);
    }

    private sealed class AccountingTransactionRow
    {
        public string Kind { get; set; } = string.Empty;
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public int? PropertyId { get; set; }
        public string? PropertyName { get; set; }
        public string? Counterparty { get; set; }
        public string? Reference { get; set; }
        public string? Notes { get; set; }
        public string DetailHref { get; set; } = string.Empty;
    }
}
