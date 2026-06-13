namespace RentalCommand.Core.Enums;

/// <summary>
/// The SMS providers a landlord can bring their own account for. Serialized as its STRING name on
/// the wire (see the app-wide <c>JsonStringEnumConverter</c>), so renames are breaking. Each maps to
/// one <c>ISmsProvider</c> implementation in the Engine.
/// </summary>
public enum SmsProviderKey
{
    /// <summary>
    /// No provider chosen for this portfolio. The dispatch resolver falls back to the platform-level
    /// env credentials (SignalWire preferred, Twilio fallback) so existing single-landlord installs
    /// keep working with zero per-portfolio config.
    /// </summary>
    None = 0,

    /// <summary>SignalWire — Twilio-compatible LaML/Compatibility API (cheaper drop-in).</summary>
    SignalWire = 1,

    /// <summary>Twilio — the original LaML/Compatibility API.</summary>
    Twilio = 2,

    /// <summary>Telnyx — REST JSON Messaging API with a bearer API key.</summary>
    Telnyx = 3,

    /// <summary>Vonage (Nexmo) — REST form API keyed by api_key + api_secret.</summary>
    Vonage = 4,
}
