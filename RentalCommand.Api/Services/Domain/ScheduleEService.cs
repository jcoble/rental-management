using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IScheduleEService"/>
public class ScheduleEService : IScheduleEService
{
    private const int UnassignedPropertyId = 0;
    private const string UnassignedPropertyName = "Unassigned";

    private readonly RentalCommandDbContext _db;

    public ScheduleEService(RentalCommandDbContext db)
    {
        _db = db;
    }

    public async Task<ScheduleEReport> GetReportAsync(int portfolioId, int year, CancellationToken ct = default)
    {
        // ── Income ──────────────────────────────────────────────────────────────────────────────
        // Qualifying rent payments: PaymentType == Rent, Status == Paid, PaidDate in the target year.
        // Grouped by PropertyId via Payment → Lease → Property.
        var incomeRows = await _db.Payments
            .AsNoTracking()
            .Where(p =>
                p.PortfolioId == portfolioId &&
                p.PaymentType == PaymentType.Rent &&
                p.Status == PaymentStatus.Paid &&
                p.PaidDate != null &&
                p.PaidDate.Value.Year == year)
            .Select(p => new
            {
                PropertyId = (int?)p.Lease!.PropertyId,
                p.Amount,
            })
            .ToListAsync(ct);

        // Build income map: propertyId → total
        var incomeByProperty = incomeRows
            .GroupBy(r => r.PropertyId ?? UnassignedPropertyId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        // ── Expenses ─────────────────────────────────────────────────────────────────────────────
        // Expenses soft-deleted records are excluded by the global query filter on DbContext.
        var expenseRows = await _db.Expenses
            .AsNoTracking()
            .Where(e =>
                e.PortfolioId == portfolioId &&
                e.IncurredAt.Year == year)
            .Select(e => new
            {
                PropertyId = e.PropertyId ?? UnassignedPropertyId,
                e.Category,
                e.Amount,
            })
            .ToListAsync(ct);

        // Build expense map: propertyId → category → total
        var expensesByProperty = expenseRows
            .GroupBy(e => e.PropertyId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(e => e.Category)
                       .ToDictionary(cg => cg.Key, cg => cg.Sum(e => e.Amount)));

        // ── Property names ────────────────────────────────────────────────────────────────────────
        // Collect all property ids that appear in income or expenses (excluding the synthetic 0).
        var realPropertyIds = incomeByProperty.Keys
            .Concat(expensesByProperty.Keys)
            .Where(id => id != UnassignedPropertyId)
            .Distinct()
            .ToHashSet();

        var propertyNames = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && realPropertyIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var nameMap = propertyNames.ToDictionary(p => p.Id, p => p.Name);

        // ── Assemble per-property reports ─────────────────────────────────────────────────────────
        var allPropertyIds = realPropertyIds
            .Concat(incomeByProperty.ContainsKey(UnassignedPropertyId) || expensesByProperty.ContainsKey(UnassignedPropertyId)
                ? [UnassignedPropertyId]
                : Array.Empty<int>())
            .Distinct()
            .ToList();

        var reports = new List<ScheduleEPropertyReport>(allPropertyIds.Count);

        foreach (var propertyId in allPropertyIds)
        {
            var propertyName = propertyId == UnassignedPropertyId
                ? UnassignedPropertyName
                : nameMap.GetValueOrDefault(propertyId, $"Property {propertyId}");

            var income = incomeByProperty.GetValueOrDefault(propertyId, 0m);
            var catMap = expensesByProperty.GetValueOrDefault(propertyId);

            // Build category list in enum-declared order; omit zero amounts.
            var categories = new List<ScheduleECategoryAmount>();
            if (catMap != null)
            {
                foreach (ScheduleECategory cat in Enum.GetValues<ScheduleECategory>())
                {
                    if (catMap.TryGetValue(cat, out var amount) && amount != 0m)
                    {
                        categories.Add(new ScheduleECategoryAmount(cat.ToString(), amount));
                    }
                }
            }

            var propertyTotalExpenses = categories.Sum(c => c.Amount);

            reports.Add(new ScheduleEPropertyReport(
                PropertyId: propertyId,
                PropertyName: propertyName,
                RentalIncome: income,
                ExpensesByCategory: categories,
                TotalExpenses: propertyTotalExpenses,
                NetIncome: income - propertyTotalExpenses));
        }

        // Sort properties by name; "Unassigned" naturally sorts last if names are real.
        reports.Sort((a, b) => string.Compare(a.PropertyName, b.PropertyName, StringComparison.OrdinalIgnoreCase));

        var totalIncome = reports.Sum(r => r.RentalIncome);
        var totalExpenses = reports.Sum(r => r.TotalExpenses);

        return new ScheduleEReport
        {
            Year = year,
            Properties = reports,
            TotalRentalIncome = totalIncome,
            TotalExpenses = totalExpenses,
            NetIncome = totalIncome - totalExpenses,
        };
    }
}
