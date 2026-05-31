namespace RentalCommand.Core.Configuration;

public class NotificationsConfig
{
    public const string SectionName = "Notifications";

    // Feature flags — financial automations default OFF (opt-in); reminders default ON.
    public bool EnableRentCharges { get; set; } = false;
    public bool EnableLateFees { get; set; } = false;
    public bool EnableLeaseExpiryReminders { get; set; } = true;

    // Whether to enqueue tenant-facing SMS/email notices from the automations (off until providers set).
    public bool NotifyTenants { get; set; } = false;

    public int RentChargeLeadDays { get; set; } = 5;    // create the scheduled rent payment up to N days before due
    public int LateFeeGraceDays { get; set; } = 5;      // days past due before a late fee applies
    public int LeaseExpiryReminderDays { get; set; } = 60;

    public TwilioOptions Twilio { get; set; } = new();
    public SendGridOptions SendGrid { get; set; } = new();

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

public class SendGridOptions
{
    public string? ApiKey { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromEmail);
}

public class LateFeeCap
{
    public decimal? MaxPercentOfRent { get; set; }  // e.g. 6 means 6% of monthly rent
    public decimal? MaxFlat { get; set; }           // absolute dollar cap
}
