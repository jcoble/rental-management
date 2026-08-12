using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Automation;
using RentalCommand.Data;
using RentalCommand.Data.Automation;

namespace RentalCommand.TestCommon;

/// <summary>SQLite test double; production uses the one-statement PostgreSQL claim store.</summary>
public sealed class TestScheduledAutomationClaimStore(RentalCommandDbContext db) : IScheduledAutomationClaimStore
{
    public async Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimDebtServiceAsync(
        string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var rows = await db.Loans.Where(row => row.Status == LoanStatus.Active && row.TermMonths > 0 &&
                row.StartDate <= todayUtc && (row.WorkerClaimToken == null || row.WorkerClaimExpiresAtUtc <= nowUtc))
            .OrderBy(row => row.StartDate).ThenBy(row => row.Id).Take(batchSize).ToListAsync(ct);
        return await AssignAsync(rows.Select(row => (row.Id, row.PortfolioId, row.WorkerClaimAttemptCount, (Action<Guid>)(token =>
        {
            row.WorkerClaimAttemptCount++;
            row.WorkerClaimOwner = owner; row.WorkerClaimToken = token; row.WorkerClaimExpiresAtUtc = nowUtc.Add(leaseDuration);
        }))), ct);
    }


    public async Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringExpensesAsync(
        string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var rows = await db.RecurringExpenses.Where(row => row.Active && row.NextRunDate <= todayUtc &&
                (row.WorkerClaimToken == null || row.WorkerClaimExpiresAtUtc <= nowUtc))
            .OrderBy(row => row.NextRunDate).ThenBy(row => row.Id).Take(batchSize).ToListAsync(ct);
        return await AssignAsync(rows.Select(row => (row.Id, row.PortfolioId, row.WorkerClaimAttemptCount, (Action<Guid>)(token =>
        {
            row.WorkerClaimAttemptCount++;
            row.WorkerClaimOwner = owner; row.WorkerClaimToken = token; row.WorkerClaimExpiresAtUtc = nowUtc.Add(leaseDuration);
        }))), ct);
    }

    public async Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringMaintenanceAsync(
        string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var rows = await db.RecurringMaintenanceTasks.Where(row => row.IsActive && row.NextDueDate <= todayUtc &&
                row.WorkerClaimQuarantinedAtUtc == null &&
                (row.WorkerClaimToken == null || row.WorkerClaimExpiresAtUtc <= nowUtc) &&
                (!db.AutomationSettings.Any(settings => settings.PortfolioId == row.PortfolioId) ||
                 db.AutomationSettings.Any(settings =>
                     settings.PortfolioId == row.PortfolioId && settings.EnableRecurringMaintenance)))
            .OrderBy(row => row.NextDueDate).ThenBy(row => row.Id).Take(batchSize).ToListAsync(ct);
        return await AssignAsync(rows.Select(row => (row.Id, row.PortfolioId, row.WorkerClaimAttemptCount, (Action<Guid>)(token =>
        {
            row.WorkerClaimAttemptCount++;
            row.WorkerClaimOwner = owner; row.WorkerClaimToken = token; row.WorkerClaimExpiresAtUtc = nowUtc.Add(leaseDuration);
        }))), ct);
    }

    public async Task<bool> RecordRecurringMaintenanceFailureAsync(
        ScheduledAutomationClaim claim,
        DateTime failedAtUtc,
        string failureReason,
        int quarantineAfterAttempts,
        CancellationToken ct = default)
    {
        var task = await db.RecurringMaintenanceTasks
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(row => row.Id == claim.Id && row.WorkerClaimToken == claim.ClaimToken, ct);
        if (task is null) return false;

        task.WorkerClaimLastFailureReason = string.IsNullOrWhiteSpace(failureReason)
            ? "Recurring-maintenance generation failed without a reason."
            : failureReason.Trim()[..Math.Min(
                failureReason.Trim().Length,
                ScheduledAutomationPolicy.RecurringMaintenanceFailureReasonMaxLength)];
        task.WorkerClaimLastFailureAtUtc = failedAtUtc;
        if (claim.AttemptCount >= quarantineAfterAttempts)
            task.WorkerClaimQuarantinedAtUtc = failedAtUtc;
        task.WorkerClaimOwner = null;
        task.WorkerClaimToken = null;
        task.WorkerClaimExpiresAtUtc = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<IReadOnlyList<ScheduledAutomationClaim>> AssignAsync(
        IEnumerable<(int Id, int PortfolioId, int AttemptCount, Action<Guid> Assign)> rows, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var claims = rows.Select(row =>
        {
            row.Assign(token);
            return new ScheduledAutomationClaim(row.Id, row.PortfolioId, token, row.AttemptCount + 1);
        }).ToArray();
        await db.SaveChangesAsync(ct);
        return claims;
    }

}
