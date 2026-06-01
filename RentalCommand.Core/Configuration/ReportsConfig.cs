namespace RentalCommand.Core.Configuration;

/// <summary>
/// Configuration for automated report delivery. Section name: "Reports".
/// All features default OFF — nothing sends unless explicitly enabled.
/// </summary>
public class ReportsConfig
{
    public const string SectionName = "Reports";

    /// <summary>
    /// When true, the <c>ScheduledOwnerStatementWorker</c> emails every owner that has an email
    /// address on the day-of-month specified by <see cref="StatementDayOfMonth"/>.
    /// Defaults to <c>false</c> — no email is ever sent without opting in.
    /// </summary>
    public bool EmailOwnerStatementsMonthly { get; set; } = false;

    /// <summary>
    /// UTC day-of-month (1–28) on which monthly owner statements are emailed.
    /// Clamped to 28 to avoid issues in February. Defaults to 1 (first of the month).
    /// </summary>
    public int StatementDayOfMonth { get; set; } = 1;
}
