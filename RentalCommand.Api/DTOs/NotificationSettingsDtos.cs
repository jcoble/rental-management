namespace RentalCommand.Api.DTOs;

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
}
