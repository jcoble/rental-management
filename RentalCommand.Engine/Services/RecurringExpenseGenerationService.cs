using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Automation;

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
    /// <summary>Max periods materialized for one template in a single run (backlog guard).</summary>
    private const int MaxCatchUpPeriods = 36;

    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAppTimeZoneProvider _tz;
    private readonly IScheduledAutomationClaimStore _claims;
    private readonly ILogger<RecurringExpenseGenerationService> _logger;

    public RecurringExpenseGenerationService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IAppTimeZoneProvider tz,
        IScheduledAutomationClaimStore claims,
        ILogger<RecurringExpenseGenerationService> logger)
    {
        _db = db;
        _timeProvider = timeProvider;
        _tz = tz;
        _claims = claims;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        // Business "today" in the landlord's local zone (drives the due comparison). NextRunDate is a
        // timestamptz column, so the comparison value must be UTC-Kind or Npgsql rejects the parameter.
        var today = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.UtcNow(), _tz.BusinessTimeZone).Date,
            DateTimeKind.Utc);

        var claims = await _claims.ClaimRecurringExpensesAsync(
            $"{Environment.MachineName}:recurring-expense", today, _timeProvider.UtcNow(),
            TimeSpan.FromMinutes(3), 25, ct);
        var created = await ProcessClaimsAsync(claims, today, ct);

        if (created > 0)
            _logger.LogInformation(
                "RecurringExpenseGenerationService created {Count} expense(s) from recurring templates.", created);

        return created;
    }

    private async Task<int> ProcessClaimsAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims, DateTime today, CancellationToken ct)
    {
        if (claims.Count == 0) return 0;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var templates = await _claims.LockOwnedRecurringExpensesAsync(claims, today, ct);
            var created = 0;

            foreach (var template in templates)
            {
                ct.ThrowIfCancellationRequested();

                var now = _timeProvider.UtcNow();
                var newExpenses = new List<Expense>();

                // Materialize one expense per due period. Cap catch-up so a long-dormant template
                // cannot flood the ledger in one cycle.
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

                // If still behind after the cap, jump to the next not-yet-due period so this same
                // backlog is not reprocessed every cycle.
                while (runDate <= today)
                    runDate = Advance(runDate, template.Frequency);

                ClearClaim(template);
                if (newExpenses.Count == 0) continue;

                // Inserts and NextRunDate advances commit atomically for the entire claimed batch.
                _db.Expenses.AddRange(newExpenses);
                template.NextRunDate = runDate;
                template.UpdatedAt = now;
                created += newExpenses.Count;
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return created;
        }
        catch (OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            _logger.LogWarning(
                ex,
                "Failed recurring-expense batch; leases will expire for retry");
            return 0;
        }
    }

    private static void ClearClaim(RecurringExpense template)
    {
        template.WorkerClaimOwner = null;
        template.WorkerClaimToken = null;
        template.WorkerClaimExpiresAtUtc = null;
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
