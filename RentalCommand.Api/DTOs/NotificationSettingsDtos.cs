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

    /// <summary>Deliver this event as a push notification to registered mobile devices.</summary>
    public bool EnablePush { get; set; } = true;
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

    public bool AutoSendRentReminder { get; set; }
    public bool AutoSendLateRent { get; set; }

    /// <summary>Single mutually-exclusive lease-end auto-send action (string enum name on the wire).</summary>
    public LeaseEndAutoAction LeaseEndAutoAction { get; set; }

    /// <summary>
    /// The portfolio's chosen SMS provider as its <c>SmsProviderKey</c> string name
    /// ("None" / "SignalWire" / "Twilio" / "Telnyx" / "Vonage"). "None" = fall back to platform env creds.
    /// </summary>
    public string SmsProvider { get; set; } = "None";

    /// <summary>The sender phone number (E.164) — readable so the landlord can confirm it.</summary>
    public string? SmsFromNumber { get; set; }

    // Credential slots are secrets: we return only whether each is set, never the value. Slot meaning
    // is provider-specific (see SmsCredentials): A = ProjectId/AccountSid/ApiKey, B = Token/AuthToken/ApiSecret,
    // C = SpaceUrl.
    public bool SmsCredentialASet { get; set; }
    public bool SmsCredentialBSet { get; set; }
    public bool SmsCredentialCSet { get; set; }

    /// <summary>
    /// The full per-(NotificationType) In-app/Email/SMS matrix — one entry per known type, with
    /// stored values where present and defaults otherwise.
    /// </summary>
    public List<NotificationChannelPreferenceDto> ChannelPreferences { get; set; } = [];
}

/// <summary>
/// Request to send a one-off test SMS to verify a provider's credentials. When a credential slot is
/// left null/blank the currently-saved value for the portfolio is used (so the landlord can re-test an
/// already-saved provider without re-typing secrets).
/// </summary>
public sealed class TestSmsRequest
{
    /// <summary>Provider to test (<c>SmsProviderKey</c> string name).</summary>
    public string SmsProvider { get; set; } = "None";

    /// <summary>Where to send the verification text (the landlord's own phone).</summary>
    public string ToPhoneNumber { get; set; } = string.Empty;

    public string? SmsFromNumber { get; set; }
    public string? SmsCredentialA { get; set; }
    public string? SmsCredentialB { get; set; }
    public string? SmsCredentialC { get; set; }
}

/// <summary>Result of a test-SMS attempt: <see cref="Success"/> plus a human-readable <see cref="Message"/>.</summary>
public sealed class TestSmsResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
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

    public bool AutoSendRentReminder { get; set; }
    public bool AutoSendLateRent { get; set; }

    /// <summary>Single mutually-exclusive lease-end auto-send action (string enum name on the wire).</summary>
    public LeaseEndAutoAction LeaseEndAutoAction { get; set; }

    /// <summary>Chosen SMS provider (<c>SmsProviderKey</c> string name). Unknown/blank → "None".</summary>
    public string SmsProvider { get; set; } = "None";

    /// <summary>Sender phone number (E.164). Empty/null clears it.</summary>
    public string? SmsFromNumber { get; set; }

    // Credential slots: write-only secrets. null/omitted = keep the currently-saved value; empty string
    // = clear. A = ProjectId/AccountSid/ApiKey, B = Token/AuthToken/ApiSecret, C = SpaceUrl.
    public string? SmsCredentialA { get; set; }
    public string? SmsCredentialB { get; set; }
    public string? SmsCredentialC { get; set; }

    /// <summary>
    /// Per-(NotificationType) channel matrix to persist. Unknown types are ignored; omitted types
    /// keep their current stored value (or default if never set).
    /// </summary>
    public List<NotificationChannelPreferenceDto> ChannelPreferences { get; set; } = [];
}
