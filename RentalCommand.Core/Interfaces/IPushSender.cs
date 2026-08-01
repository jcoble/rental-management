namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Sends a single push notification to one device token (FCM/APNs via FCM HTTP v1). Implementations
/// must be fail-soft when no push provider credential is configured (log + no-op) so the rest of the
/// notification rail is unaffected before Firebase is set up.
/// </summary>
public interface IPushSender
{
    /// <summary>
    /// Delivers a push to one registered device. <paramref name="data"/> may carry a serialized,
    /// access-bound <c>navigationIntent</c>. It never carries a URL or route string.
    /// </summary>
    /// <returns>
    /// True if the message was accepted by the provider. False on a fail-soft suppression
    /// (no provider configured). Throws on a transient provider error so the outbox retries.
    /// A permanently-invalid token (404/UNREGISTERED) returns false so the caller can prune it.
    /// </returns>
    Task<PushSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data,
        RentalCommand.Core.Outbox.NotificationDeliveryContext delivery,
        CancellationToken ct = default);
}

/// <summary>Outcome of one push send. <see cref="TokenInvalid"/> signals the token should be pruned.</summary>
public sealed record PushSendResult(bool Sent, bool TokenInvalid, string? ProviderMessageId)
{
    public static readonly PushSendResult Suppressed = new(false, false, null);
    public static PushSendResult Ok(string providerMessageId) => new(true, false, providerMessageId);
    public static PushSendResult Invalid() => new(false, true, null);
}
