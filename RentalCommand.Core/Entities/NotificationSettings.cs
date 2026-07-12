using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class NotificationSettings
{
    public int Id { get; set; }

    /// <summary>
    /// Portfolio this settings row belongs to. The audit flagged the original single global row;
    /// settings are now per-portfolio. Legacy rows are backfilled to the lowest portfolio id.
    /// </summary>
    public int PortfolioId { get; set; }

    /// <summary>
    /// The SMS provider this portfolio brings its own credentials for, stored as the
    /// <see cref="Enums.SmsProviderKey"/> string name ("SignalWire" / "Twilio" / "Telnyx" / "Vonage").
    /// Null / "None" means "no per-portfolio provider configured" — the Engine then falls back to the
    /// platform-level env credentials. Provider-agnostic: the three credential slots below carry
    /// vendor-specific meaning (documented on <c>SmsCredentials</c>) so storage never special-cases a vendor.
    /// </summary>
    public string? SmsProvider { get; set; }

    /// <summary>Credential slot A (e.g. SignalWire ProjectId / Twilio AccountSid / Telnyx API key / Vonage api_key), encrypted.</summary>
    public string? SmsCredentialACipherText { get; set; }

    /// <summary>Credential slot B (e.g. SignalWire Token / Twilio AuthToken / Vonage api_secret), encrypted.</summary>
    public string? SmsCredentialBCipherText { get; set; }

    /// <summary>Credential slot C (e.g. SignalWire Space URL), encrypted.</summary>
    public string? SmsCredentialCCipherText { get; set; }

    /// <summary>The sender phone number (E.164) for the chosen provider, encrypted.</summary>
    public string? SmsFromNumberCipherText { get; set; }

    // Legacy SignalWire-specific columns. Retained for backward-compatible reads of rows saved before
    // the pluggable-provider migration; the migration backfills the generic slots above from these.
    // New writes go to the generic slots only. Do not write these going forward.
    public string? SignalWireProjectIdCipherText { get; set; }
    public string? SignalWireTokenCipherText { get; set; }
    public string? SignalWireSpaceUrlCipherText { get; set; }
    public string? SignalWireFromNumberCipherText { get; set; }
    public bool EnableRentCharges { get; set; }
    public bool EnableLateFees { get; set; }
    public bool EnableLeaseExpiryReminders { get; set; } = true;
    public int RentChargeLeadDays { get; set; } = 5;
    public int LateFeeGraceDays { get; set; } = 5;
    public int LeaseExpiryReminderDays { get; set; } = 60;
    public bool EnableDailyBriefingMessages { get; set; }
    public bool EnableRecurringMaintenance { get; set; } = true;
    public int DailyBriefingSendHourLocal { get; set; } = 8;
    public bool DailyBriefingIncludeEmpty { get; set; }
    public string? DailyBriefingSmsRecipientsCipherText { get; set; }
    public string? DailyBriefingEmailRecipientsCipherText { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
