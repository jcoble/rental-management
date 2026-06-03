namespace RentalCommand.Core.Entities;

public class NotificationSettings
{
    public int Id { get; set; }
    public string? SignalWireProjectIdCipherText { get; set; }
    public string? SignalWireTokenCipherText { get; set; }
    public string? SignalWireSpaceUrlCipherText { get; set; }
    public string? SignalWireFromNumberCipherText { get; set; }
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
    public string? DailyBriefingSmsRecipientsCipherText { get; set; }
    public string? DailyBriefingEmailRecipientsCipherText { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
