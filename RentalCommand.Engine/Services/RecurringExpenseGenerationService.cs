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
        var created = 0;

        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();
            created += await ProcessClaimAsync(claim, today, ct);
        }

        if (created > 0)
            _logger.LogInformation(
                "RecurringExpenseGenerationService created {Count} expense(s) from recurring templates.", created);

        return created;
    }

    private async Task<int> ProcessClaimAsync(
        ScheduledAutomationClaim claim, DateTime today, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var ownedTemplate = _db.Database.IsNpgsql()
                ? _db.RecurringExpenses.FromSqlInterpolated($"""
                    SELECT * FROM "RecurringExpenses"
                    WHERE "Id" = {claim.Id} AND "WorkerClaimToken" = {claim.ClaimToken}
                    FOR UPDATE
                    """)
                : _db.RecurringExpenses.Where(row => row.Id == claim.Id && row.WorkerClaimToken == claim.ClaimToken);
            var template = await ownedTemplate.SingleOrDefaultAsync(ct);
            if (template is null)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }

            if (!template.Active || template.NextRunDate > today)
            {
                ClearClaim(template);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return 0;
            }

            var now = _timeProvider.UtcNow();
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
            {
                ClearClaim(template);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return 0;
            }

            // The expense inserts and the NextRunDate advance must commit atomically: a crash between
            // separate saves could create expenses without advancing the schedule, double-billing the
            // cost next cycle. One transaction; the (active, due) re-scan retries the whole unit.
            _db.Expenses.AddRange(newExpenses);
            template.NextRunDate = runDate;
            template.UpdatedAt = now;
            ClearClaim(template);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return newExpenses.Count;
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
                "Failed recurring-expense claim for template {TemplateId}; lease will expire for retry",
                claim.Id);
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
