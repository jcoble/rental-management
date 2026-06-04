using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>
/// One row of the per-(NotificationType) channel matrix. <see cref="NotificationType"/> round-trips
/// as its STRING name on the wire (e.g. "RentCharge"). Always returned for every known type — when
/// no row is stored the service fills in defaults — so the client renders the full matrix.
/// </summary>
public sealed class NotificationChannelPreferenceDto
{
    public NotificationType NotificationType { get; set; }
    public bool EnableInApp { get; set; } = true;
    public bool EnableEmail { get; set; } = true;
    public bool EnableSms { get; set; }
}

public sealed class NotificationSettingsResponse
{
    public bool EnableRentCharges { get; set; }
    public bool EnableLateFees { get; set; }
    public bool EnableLeaseExpiryReminders { get; set; } = true;
    public bool NotifyTenants { get; set; }
    public int RentChargeLeadDays { get; set; } = 5;
    public int LateFeeGraceDays { get; set; } = 5;
    public int LeaseExpiryReminderDays { get; set; } = 60;
    public bool EnableDailyBriefingMessages { get; set; }
    public int DailyBriefingSendHourLocal { get; set; } = 8;
    public bool DailyBriefingIncludeEmpty { get; set; }
    public string[] DailyBriefingSmsRecipients { get; set; } = [];
    public string[] DailyBriefingEmailRecipients { get; set; } = [];
    public string? SignalWireProjectId { get; set; }
    public bool SignalWireTokenSet { get; set; }
    public string? SignalWireToken { get; set; }
    public string? SignalWireSpaceUrl { get; set; }
    public string? SignalWireFromNumber { get; set; }

    /// <summary>
    /// The full per-(NotificationType) In-app/Email/SMS matrix — one entry per known type, with
    /// stored values where present and defaults otherwise.
    /// </summary>
    public List<NotificationChannelPreferenceDto> ChannelPreferences { get; set; } = [];
}

public sealed class UpdateNotificationSettingsRequest
{
    public bool EnableRentCharges { get; set; }
    public bool EnableLateFees { get; set; }
    public bool EnableLeaseExpiryReminders { get; set; } = true;
    public bool NotifyTenants { get; set; }
    public int RentChargeLeadDays { get; set; } = 5;
    public int LateFeeGraceDays { get; set; } = 5;
    public int LeaseExpiryReminderDays { get; set; } = 60;
    public bool EnableDailyBriefingMessages { get; set; }
    public int DailyBriefingSendHourLocal { get; set; } = 8;
    public bool DailyBriefingIncludeEmpty { get; set; }
    public string[] DailyBriefingSmsRecipients { get; set; } = [];
    public string[] DailyBriefingEmailRecipients { get; set; } = [];
    public string? SignalWireProjectId { get; set; }
    public string? SignalWireToken { get; set; }
    public string? SignalWireSpaceUrl { get; set; }
    public string? SignalWireFromNumber { get; set; }

    /// <summary>
    /// Per-(NotificationType) channel matrix to persist. Unknown types are ignored; omitted types
    /// keep their current stored value (or default if never set).
    /// </summary>
    public List<NotificationChannelPreferenceDto> ChannelPreferences { get; set; } = [];
}
