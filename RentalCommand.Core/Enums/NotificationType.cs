namespace RentalCommand.Core.Enums;

/// <summary>
/// The automation/notification events the landlord controls per-channel (In-app / Email / SMS).
/// Each value maps to an existing automation or proactive message. Serialized as its STRING name
/// on the wire (see <c>JsonStringEnumConverter</c> registration), so renames are breaking.
/// </summary>
public enum NotificationType
{
    /// <summary>A scheduled rent charge was generated for a lease (<c>RentChargeService</c>).</summary>
    RentCharge,

    /// <summary>A late fee was assessed on an overdue rent payment (<c>LateFeeService</c>).</summary>
    LateFee,

    /// <summary>An owner-facing reminder that a lease is approaching expiry (<c>LeaseExpiryReminderService</c>).</summary>
    LeaseExpiry,

    /// <summary>A confirmation that a tenant's rent payment was recorded.</summary>
    RentConfirmation,

    /// <summary>The Lease Lifecycle Autopilot drafted a renewal/notice/move-out (<c>NoticeDraftGenerationService</c>).</summary>
    NoticeAutopilot,

    /// <summary>The proactive owner-facing daily briefing (<c>DailyBriefingDeliveryService</c>).</summary>
    DailyBriefing,
}
