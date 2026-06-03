namespace RentalCommand.Api.DTOs;

public sealed class NotificationSettingsResponse
{
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
