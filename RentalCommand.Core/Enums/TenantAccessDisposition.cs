namespace RentalCommand.Core.Enums;

/// <summary>Explicit treatment of portal grants when an effective party membership ends.</summary>
public enum TenantAccessDisposition
{
    RevokeImmediately,
    RetainHistoricalReadOnly,
    ContinueOnReplacementMembership,
}
