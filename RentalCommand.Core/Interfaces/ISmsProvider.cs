using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Resolved SMS credentials for one provider. The three credential slots carry different meanings per
/// provider (documented on each <see cref="ISmsProvider"/> implementation), so the storage/UI layer can
/// stay provider-agnostic — it persists three opaque secrets + a from-number and never special-cases a
/// vendor. <see cref="FromNumber"/> is the sender (E.164) for every provider.
/// </summary>
/// <param name="Provider">Which provider these credentials are for.</param>
/// <param name="CredentialA">Slot A (e.g. SignalWire ProjectId / Twilio AccountSid / Telnyx API key / Vonage api_key).</param>
/// <param name="CredentialB">Slot B (e.g. SignalWire Token / Twilio AuthToken / Vonage api_secret). Unused for Telnyx.</param>
/// <param name="CredentialC">Slot C (e.g. SignalWire Space URL). Unused for Twilio/Telnyx/Vonage.</param>
/// <param name="FromNumber">The sender phone number (E.164).</param>
public sealed record SmsCredentials(
    SmsProviderKey Provider,
    string? CredentialA,
    string? CredentialB,
    string? CredentialC,
    string? FromNumber)
{
    /// <summary>True when the slots required by <see cref="Provider"/> are all present.</summary>
    public bool IsComplete => Provider switch
    {
        SmsProviderKey.SignalWire =>
            NotBlank(CredentialA) && NotBlank(CredentialB) && NotBlank(CredentialC) && NotBlank(FromNumber),
        SmsProviderKey.Twilio =>
            NotBlank(CredentialA) && NotBlank(CredentialB) && NotBlank(FromNumber),
        SmsProviderKey.Telnyx =>
            NotBlank(CredentialA) && NotBlank(FromNumber),
        SmsProviderKey.Vonage =>
            NotBlank(CredentialA) && NotBlank(CredentialB) && NotBlank(FromNumber),
        _ => false,
    };

    private static bool NotBlank(string? v) => !string.IsNullOrWhiteSpace(v);
}

/// <summary>
/// One SMS vendor's HTTP transport. Implementations are pluggable: the dispatch channel resolves the
/// portfolio's chosen provider (or the platform-env fallback), looks up the matching
/// <see cref="ISmsProvider"/> by <see cref="Key"/>, and calls <see cref="SendAsync"/>. A provider must
/// throw on a non-success response (the outbox worker turns that into a retry); a misconfigured/missing
/// provider is handled by the caller as a suppression log, never a crash.
/// </summary>
public interface ISmsProvider
{
    /// <summary>The provider this implementation serves.</summary>
    SmsProviderKey Key { get; }

    /// <summary>
    /// Sends one SMS via this provider's HTTP API. <paramref name="toPhoneNumber"/> is already
    /// normalized to E.164. Throws on a non-success HTTP response (with the provider's error body in
    /// the message) so the outbox worker can retry.
    /// </summary>
    Task SendAsync(SmsCredentials credentials, string toPhoneNumber, string message, CancellationToken ct = default);
}
