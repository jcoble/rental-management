namespace RentalCommand.Core.Enums;

/// <summary>
/// Lifecycle of a bulk-scan batch (the "import many lease PDFs at once" on-ramp).
/// <see cref="Processing"/> = files uploaded, drafts still extracting; <see cref="Reviewing"/> =
/// at least one draft is ready for the review queue; <see cref="Completed"/> = every draft in the
/// batch has been confirmed or rejected.
/// </summary>
public enum ScanBatchStatus
{
    Processing,
    Reviewing,
    Completed
}
