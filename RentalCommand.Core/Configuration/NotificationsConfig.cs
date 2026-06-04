namespace RentalCommand.Core.Configuration;

public class NotificationsConfig
{
    public const string SectionName = "Notifications";

    // Feature flags — financial automations default OFF (opt-in); reminders default ON.
    public bool EnableRentCharges { get; set; } = false;
    public bool EnableLateFees { get; set; } = false;
    public bool EnableLeaseExpiryReminders { get; set; } = true;

    // Lease Lifecycle Autopilot: proactively DRAFT renewal offers, escalating late-rent notices, and
    // move-out reminders for one-tap approval. Defaults ON because drafts are never auto-sent — the
    // landlord still approves each one (and picks channels) before anything leaves the building.
    public bool EnableNoticeAutopilot { get; set; } = true;

    // Whether to enqueue tenant-facing SMS/email notices from the automations (off until providers set).
    public bool NotifyTenants { get; set; } = false;

    // Proactive owner-facing daily briefing delivery. Defaults off until recipients are configured.
    public bool EnableDailyBriefingMessages { get; set; } = false;
    public DailyBriefingOptions DailyBriefing { get; set; } = new();

    public int RentChargeLeadDays { get; set; } = 5;    // create the scheduled rent payment up to N days before due
    public int LateFeeGraceDays { get; set; } = 5;      // days past due before a late fee applies
    public int LeaseExpiryReminderDays { get; set; } = 60;

    public TwilioOptions Twilio { get; set; } = new();
    public SignalWireOptions SignalWire { get; set; } = new();
    public SendGridOptions SendGrid { get; set; } = new();

    /// <summary>
    /// Public base URL the SMS provider was configured to call (e.g. <c>https://app.example.com</c>),
    /// used to recompute the webhook signature when the API sits behind a reverse proxy that rewrites
    /// scheme/host. When null the signature is checked against the request's own absolute URI.
    /// </summary>
    public string? PublicWebhookBaseUrl { get; set; }

    // Keyed by 2-letter US state (e.g. "CA"); caps the late fee. Missing state = no cap (use lease amount).
    public Dictionary<string, LateFeeCap> StateLateFeeCaps { get; set; } = new();
}

public class TwilioOptions
{
    public string? AccountSid { get; set; }
    public string? AuthToken { get; set; }
    public string? FromNumber { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(AccountSid) && !string.IsNullOrWhiteSpace(AuthToken) && !string.IsNullOrWhiteSpace(FromNumber);
}

/// <summary>
/// SignalWire SMS via its Twilio-compatible "Compatibility" (LaML) API — a cheaper drop-in for
/// Twilio. Basic auth uses ProjectId as username and the API token as password; messages POST to
/// <c>https://{SpaceUrl}/api/laml/2010-04-01/Accounts/{ProjectId}/Messages.json</c>.
/// </summary>
public class SignalWireOptions
{
    public string? ProjectId { get; set; }
    public string? Token { get; set; }
    /// <summary>Space host, e.g. <c>your-space.signalwire.com</c> (scheme optional).</summary>
    public string? SpaceUrl { get; set; }
    public string? FromNumber { get; set; }
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(Token)
        && !string.IsNullOrWhiteSpace(SpaceUrl) && !string.IsNullOrWhiteSpace(FromNumber);
}

public class SendGridOptions
{
    public string? ApiKey { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromEmail);
}

public class DailyBriefingOptions
{
    public int SendHourLocal { get; set; } = 8;
    public string[] SmsRecipients { get; set; } = [];
    public string[] EmailRecipients { get; set; } = [];
    public bool IncludeEmptyBriefing { get; set; } = false;
}

public class LateFeeCap
{
    public decimal? MaxPercentOfRent { get; set; }  // e.g. 6 means 6% of monthly rent
    public decimal? MaxFlat { get; set; }           // absolute dollar cap
}
