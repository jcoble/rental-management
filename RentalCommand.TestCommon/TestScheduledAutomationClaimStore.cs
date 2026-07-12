using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
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

    public async Task<IReadOnlyList<RecurringMaintenanceTask>> LockOwnedRecurringMaintenanceAsync(
        IReadOnlyList<ScheduledAutomationClaim> claims, DateTime todayUtc, CancellationToken ct = default)
    {
        var (ids, token) = BatchIdentity(claims);
        return await db.RecurringMaintenanceTasks.Where(row => ids.Contains(row.Id) &&
            row.WorkerClaimToken == token && row.IsActive && row.NextDueDate <= todayUtc &&
            (!db.NotificationSettings.Any(settings => settings.PortfolioId == row.PortfolioId) ||
             db.NotificationSettings.Any(settings =>
                 settings.PortfolioId == row.PortfolioId && settings.EnableRecurringMaintenance)))
            .ToListAsync(ct);
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
                (row.WorkerClaimToken == null || row.WorkerClaimExpiresAtUtc <= nowUtc) &&
                (!db.NotificationSettings.Any(settings => settings.PortfolioId == row.PortfolioId) ||
                 db.NotificationSettings.Any(settings =>
                     settings.PortfolioId == row.PortfolioId && settings.EnableRecurringMaintenance)))
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

    private static (int[] Ids, Guid Token) BatchIdentity(IReadOnlyList<ScheduledAutomationClaim> claims)
    {
        if (claims.Count == 0) return ([], Guid.Empty);
        return (claims.Select(claim => claim.Id).ToArray(), claims[0].ClaimToken);
    }
}
