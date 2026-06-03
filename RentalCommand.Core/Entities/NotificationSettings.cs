namespace RentalCommand.Core.Entities;

public class NotificationSettings
{
    public int Id { get; set; }
    public string? SignalWireProjectIdCipherText { get; set; }
    public string? SignalWireTokenCipherText { get; set; }
    public string? SignalWireSpaceUrlCipherText { get; set; }
    public string? SignalWireFromNumberCipherText { get; set; }
    public bool EnableDailyBriefingMessages { get; set; }
    public int DailyBriefingSendHourLocal { get; set; } = 8;
    public bool DailyBriefingIncludeEmpty { get; set; }
    public string? DailyBriefingSmsRecipientsCipherText { get; set; }
    public string? DailyBriefingEmailRecipientsCipherText { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
