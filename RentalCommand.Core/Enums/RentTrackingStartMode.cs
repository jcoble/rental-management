namespace RentalCommand.Core.Enums;

/// <summary>How an existing lease begins posting rent into its tenant account.</summary>
public enum RentTrackingStartMode
{
    BackfillFromLeaseStart,
    ForwardOnly,
    CustomCutoffDate,
}
