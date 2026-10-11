namespace RentalCommand.Core.Automation;

/// <summary>Safety limits shared by the scheduled-automation claim and retry paths.</summary>
public static class ScheduledAutomationPolicy
{
    /// <summary>
    /// A maintenance task is quarantined after three consecutive claimed failures. A successful
    /// generation resets the consecutive claim-attempt counter; a manual task edit clears quarantine.
    /// </summary>
    public const int RecurringMaintenanceQuarantineAfterAttempts = 3;

    /// <summary>Maximum persisted failure-reason length exposed by the automation status query.</summary>
    public const int RecurringMaintenanceFailureReasonMaxLength = 2000;
}
