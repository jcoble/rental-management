namespace RentalCommand.Core.Enums;

/// <summary>
/// Lifecycle status of a tenant <see cref="Entities.ScreeningResult"/>: the screening was requested,
/// the provider returned a completed report, or the request failed. Serialized as the string name
/// app-wide. When the provider is not configured (gated), the request is recorded as
/// <see cref="Failed"/> with a clear "not configured" note — never a false "completed/passed".
/// </summary>
public enum ScreeningStatus
{
    Requested,
    Completed,
    Failed
}
