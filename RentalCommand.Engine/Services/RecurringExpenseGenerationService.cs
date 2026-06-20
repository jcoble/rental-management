using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Materializes due recurring-expense templates into <see cref="Expense"/> rows (spec §8), mirroring
/// <c>RecurringMaintenanceService</c>'s idempotent pattern. Idempotency is by schedule advancement:
/// each due period creates one expense and rolls NextRunDate forward, all in one transaction, so a
/// re-run (or a crash mid-cycle) never double-creates a period. Missed periods are caught up (each
/// becomes its own dated expense) so deductions are never understated; the catch-up is capped to
/// avoid flooding the books from a long-dormant template.
///
/// Runs inside a fresh DI scope (scoped <see cref="RentalCommandDbContext"/>) under the Engine's
/// single-instance advisory lock.
/// </summary>
public sealed class RecurringExpenseGenerationService : IRecurringExpenseGenerationService
{
    private const string DefaultTimeZoneId = "America/New_York";

    /// <summary>Max periods materialized for one template in a single run (backlog guard).</summary>
    private const int MaxCatchUpPeriods = 36;

    private readonly RentalCommandDbContext _db;
    private readonly TimeZoneInfo _businessTimeZone;
    private readonly ILogger<RecurringExpenseGenerationService> _logger;

    public RecurringExpenseGenerationService(
        RentalCommandDbContext db,
        IConfiguration configuration,
        ILogger<RecurringExpenseGenerationService> logger)
    {
        _db = db;
        _logger = logger;

        var tzId = configuration["App:TimeZone"];
        if (string.IsNullOrWhiteSpace(tzId))
            tzId = DefaultTimeZoneId;
        _businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        // Business "today" in the landlord's local zone (drives the due comparison). NextRunDate is a
        // timestamptz column, so the comparison value must be UTC-Kind or Npgsql rejects the parameter.
        var today = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _businessTimeZone).Date,
            DateTimeKind.Utc);

        var templates = await _db.RecurringExpenses
            .Where(t => t.Active && t.NextRunDate <= today)
            .ToListAsync(ct);

        var created = 0;

        foreach (var template in templates)
        {
            ct.ThrowIfCancellationRequested();

            var now = DateTime.UtcNow;
            var newExpenses = new List<Expense>();

            // Materialize one expense per due period, advancing the run date each time. Cap the catch-up
            // so a template that has been dormant for years can't flood the ledger in one cycle.
            var runDate = template.NextRunDate;
            var periods = 0;
            while (runDate <= today && periods < MaxCatchUpPeriods)
            {
                newExpenses.Add(new Expense
                {
                    PortfolioId = template.PortfolioId,
                    PropertyId = template.PropertyId,
                    Category = template.Category,
                    Description = template.Description,
                    Status = ExpenseStatus.Pending,
                    Amount = template.Amount,
                    IncurredAt = runDate,
                    Notes = template.Notes,
                    CreatedAt = now,
                    UpdatedAt = now,
                });

                runDate = Advance(runDate, template.Frequency);
                periods++;
            }

            // If still behind after the cap, jump the schedule forward to the next not-yet-due period so
            // we don't re-process the same backlog every cycle.
            while (runDate <= today)
                runDate = Advance(runDate, template.Frequency);

            if (newExpenses.Count == 0)
                continue;

            // The expense inserts and the NextRunDate advance must commit atomically: a crash between
            // separate saves could create expenses without advancing the schedule, double-billing the
            // cost next cycle. One transaction; the (active, due) re-scan retries the whole unit.
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                _db.Expenses.AddRange(newExpenses);
                template.NextRunDate = runDate;
                template.UpdatedAt = now;

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                created += newExpenses.Count;
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                _logger.LogWarning(
                    ex,
                    "Failed to materialize recurring expense template {TemplateId} (portfolio {PortfolioId}); rolled back, will retry",
                    template.Id, template.PortfolioId);

                foreach (var e in newExpenses)
                    _db.Entry(e).State = EntityState.Detached;
                _db.Entry(template).State = EntityState.Unchanged;
                continue;
            }
        }

        if (created > 0)
            _logger.LogInformation(
                "RecurringExpenseGenerationService created {Count} expense(s) from recurring templates.", created);

        return created;
    }

    /// <summary>Advance a run date by one period (Monthly = +1mo, Quarterly = +3mo, Annual = +1yr).</summary>
    private static DateTime Advance(DateTime date, RecurringExpenseFrequency frequency) => frequency switch
    {
        RecurringExpenseFrequency.Monthly => date.AddMonths(1),
        RecurringExpenseFrequency.Quarterly => date.AddMonths(3),
        RecurringExpenseFrequency.Annual => date.AddYears(1),
        _ => date.AddMonths(1),
    };
}
