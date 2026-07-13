namespace RentalCommand.Core.Configuration;

public class NotificationsConfig
{
    public const string SectionName = "Notifications";

    // Lease Lifecycle Autopilot: proactively DRAFT renewal offers, escalating late-rent notices, and
    // move-out reminders for one-tap approval. Defaults ON because drafts are never auto-sent — the
    // landlord still approves each one (and picks channels) before anything leaves the building.
    public bool EnableNoticeAutopilot { get; set; } = true;

    public TwilioOptions Twilio { get; set; } = new();
    public SignalWireOptions SignalWire { get; set; } = new();
    public TelnyxOptions Telnyx { get; set; } = new();
    public VonageOptions Vonage { get; set; } = new();
    public SendGridOptions SendGrid { get; set; } = new();

    /// <summary>SMTP email transport. Lets email be sent over a domain-authenticated SMTP provider
    /// or IP-authorized relay. Selected via <see cref="Email"/>.Transport == "Smtp".</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Chooses which email transport <c>SendEmailAsync</c> uses (SendGrid vs SMTP).</summary>
    public EmailTransportOptions Email { get; set; } = new();

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

/// <summary>
/// Telnyx SMS via its REST Messaging API (<c>https://api.telnyx.com/v2/messages</c>). Auth is a
/// bearer API key (slot A); messages POST a JSON body of <c>{from,to,text}</c>. No separate token
/// or space — only the API key + from-number are required.
/// </summary>
public class TelnyxOptions
{
    /// <summary>Telnyx API key (bearer). Maps to credential slot A.</summary>
    public string? ApiKey { get; set; }
    public string? FromNumber { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromNumber);
}

/// <summary>
/// Vonage (formerly Nexmo) SMS via its REST API (<c>https://rest.nexmo.com/sms/json</c>). Auth is an
/// <c>api_key</c> (slot A) + <c>api_secret</c> (slot B) form-posted alongside <c>from/to/text</c>.
/// </summary>
public class VonageOptions
{
    /// <summary>Vonage api_key. Maps to credential slot A.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Vonage api_secret. Maps to credential slot B.</summary>
    public string? ApiSecret { get; set; }
    public string? FromNumber { get; set; }
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret)
        && !string.IsNullOrWhiteSpace(FromNumber);
}

public class SendGridOptions
{
    public string? ApiKey { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromEmail);
}

/// <summary>
/// SMTP email transport. Supports authenticated mailbox SMTP and IP-authorized relay
/// (<c>smtp-relay.gmail.com:587</c>) so mail can be sent from the domain-authenticated provider.
/// </summary>
public class SmtpOptions
{
    public string? Host { get; set; }
    /// <summary>465 = implicit SSL/TLS on connect. 587 = STARTTLS.</summary>
    public int Port { get; set; } = 465;
    /// <summary>True → SSL-on-connect (port 465). False → STARTTLS (port 587).</summary>
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
    /// <summary>Socket/operation timeout (seconds) so a dead relay can't stall the outbox dispatcher.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);

    /// <summary>
    /// Configured when host is present and either credentials exist or a relay sender address is set.
    /// If Username/Password are both blank, the SMTP client connects without AUTH.
    /// </summary>
    public bool Enabled =>
        !string.IsNullOrWhiteSpace(Host)
        && (HasCredentials
            || (string.IsNullOrWhiteSpace(Username)
                && string.IsNullOrWhiteSpace(Password)
                && !string.IsNullOrWhiteSpace(FromEmail)));
}

/// <summary>
/// Selects which email transport <c>SendEmailAsync</c> uses. Defaults to "SendGrid" so existing
/// behaviour is unchanged; set Transport = "Smtp" (with <see cref="NotificationsConfig.Smtp"/>
/// configured) to send over SMTP instead.
/// </summary>
public class EmailTransportOptions
{
    public const string SendGrid = "SendGrid";
    public const string Smtp = "Smtp";

    /// <summary>"SendGrid" (default) | "Smtp".</summary>
    public string Transport { get; set; } = SendGrid;

    public bool UseSmtp => string.Equals(Transport, Smtp, StringComparison.OrdinalIgnoreCase);
}

public class LateFeeCap
{
    public decimal? MaxPercentOfRent { get; set; }  // e.g. 6 means 6% of monthly rent
    public decimal? MaxFlat { get; set; }           // absolute dollar cap
}
