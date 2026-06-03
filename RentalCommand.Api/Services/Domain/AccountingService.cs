using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IAccountingService"/>
public class AccountingService : IAccountingService
{
    private const decimal Vendor1099Threshold = 600m;

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

        var totalExpenses = categoryGroups.Sum(g => g.Total);

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

        return new AccountingSummaryResponse
        {
            PortfolioId = portfolioId,
            ExpensesByCategory = expensesByCategory,
            TotalExpenses = totalExpenses,
            Payments = rollup,
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
            .Sum(p => p.Amount);
        var totalExpenses = expenseRows.Sum(e => e.Amount);

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
}
