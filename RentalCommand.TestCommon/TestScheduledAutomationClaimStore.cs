using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Automation;

namespace RentalCommand.TestCommon;

/// <summary>SQLite test double; production uses the one-statement PostgreSQL claim store.</summary>
public sealed class TestScheduledAutomationClaimStore(RentalCommandDbContext db) : IScheduledAutomationClaimStore
{
    public async Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimDebtServiceAsync(
        string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        var rows = await db.Loans.Where(row => row.Status == LoanStatus.Active && row.TermMonths > 0 &&
                row.StartDate <= todayUtc && (row.WorkerClaimToken == null || row.WorkerClaimExpiresAtUtc <= nowUtc))
            .OrderBy(row => row.StartDate).ThenBy(row => row.Id).Take(batchSize).ToListAsync(ct);
        return await AssignAsync(rows.Select(row => (row.Id, row.PortfolioId, (Action<Guid>)(token =>
        {
            row.WorkerClaimOwner = owner; row.WorkerClaimToken = token; row.WorkerClaimExpiresAtUtc = nowUtc.Add(leaseDuration);
        }))), ct);
    }

    public async Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringExpensesAsync(
        string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        var rows = await db.RecurringExpenses.Where(row => row.Active && row.NextRunDate <= todayUtc &&
                (row.WorkerClaimToken == null || row.WorkerClaimExpiresAtUtc <= nowUtc))
            .OrderBy(row => row.NextRunDate).ThenBy(row => row.Id).Take(batchSize).ToListAsync(ct);
        return await AssignAsync(rows.Select(row => (row.Id, row.PortfolioId, (Action<Guid>)(token =>
        {
            row.WorkerClaimOwner = owner; row.WorkerClaimToken = token; row.WorkerClaimExpiresAtUtc = nowUtc.Add(leaseDuration);
        }))), ct);
    }

    public async Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringMaintenanceAsync(
        string owner, DateTime todayUtc, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        var rows = await db.RecurringMaintenanceTasks.Where(row => row.IsActive && row.NextDueDate <= todayUtc &&
                (row.WorkerClaimToken == null || row.WorkerClaimExpiresAtUtc <= nowUtc))
            .OrderBy(row => row.NextDueDate).ThenBy(row => row.Id).Take(batchSize).ToListAsync(ct);
        return await AssignAsync(rows.Select(row => (row.Id, row.PortfolioId, (Action<Guid>)(token =>
        {
            row.WorkerClaimOwner = owner; row.WorkerClaimToken = token; row.WorkerClaimExpiresAtUtc = nowUtc.Add(leaseDuration);
        }))), ct);
    }

    private async Task<IReadOnlyList<ScheduledAutomationClaim>> AssignAsync(
        IEnumerable<(int Id, int PortfolioId, Action<Guid> Assign)> rows, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var claims = rows.Select(row =>
        {
            row.Assign(token);
            return new ScheduledAutomationClaim(row.Id, row.PortfolioId, token);
        }).ToArray();
        await db.SaveChangesAsync(ct);
        return claims;
    }
}
