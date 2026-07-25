using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Automation;

public sealed record ApplyClaimedDebtServiceBatchCommand(
    int[] LoanIds,
    Guid ClaimToken,
    DateTime BusinessDateUtc,
    DateTime AppliedAtUtc) : IAtomicCommandData;

public sealed record ApplyClaimedRecurringExpenseBatchCommand(
    int[] RecurringExpenseIds,
    Guid ClaimToken,
    DateTime BusinessDateUtc,
    DateTime AppliedAtUtc) : IAtomicCommandData;

public sealed record ApplyClaimedRecurringMaintenanceBatchCommand(
    int[] RecurringMaintenanceTaskIds,
    Guid ClaimToken,
    DateTime BusinessDateUtc,
    DateTime AppliedAtUtc,
    string BusinessTimeZoneId) : IAtomicCommandData;

public enum ScheduledFinanceApplyOutcome
{
    Applied,
}

public sealed record ApplyScheduledFinanceBatchResult(
    ScheduledFinanceApplyOutcome Outcome,
    int ClaimedScheduleCount,
    int GeneratedRowCount) : IAtomicResultData;
