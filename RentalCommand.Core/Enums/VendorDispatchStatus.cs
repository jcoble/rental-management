namespace RentalCommand.Core.Enums;

/// <summary>
/// Lifecycle of a single vendor dispatch (the app texting a vendor a job and waiting for the
/// "DONE" reply that closes it). Serialized as the string name app-wide.
/// </summary>
public enum VendorDispatchStatus
{
    /// <summary>The job SMS was enqueued to the vendor; awaiting a reply.</summary>
    Dispatched,

    /// <summary>The vendor acknowledged the job (reserved for a future "ON IT" reply).</summary>
    Acknowledged,

    /// <summary>The vendor replied DONE; the work order was closed off this dispatch.</summary>
    Completed,

    /// <summary>The dispatch was cancelled before completion (e.g. reassigned).</summary>
    Cancelled,
}
