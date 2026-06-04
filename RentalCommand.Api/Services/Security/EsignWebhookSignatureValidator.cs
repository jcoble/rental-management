using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Services.Security;

/// <summary>
/// Verifies the signature on inbound e-sign provider webhooks. Dropbox Sign / HelloSign signs each event
/// with <c>event_hash = HMAC-SHA256(api_key/webhook_secret, event_time + event_type)</c> (hex). Without
/// this, the <c>[AllowAnonymous]</c> e-sign webhook is spoofable — anyone could POST a "signed" event to
/// flip a lease to Active. Enforcement is on whenever <c>Esign:WebhookSecret</c> is configured; when it
/// is absent (local dev) verification is skipped but loudly logged so it is never silently off in a real
/// deployment (mirrors <see cref="SmsWebhookSignatureValidator"/>).
/// </summary>
public interface IEsignWebhookSignatureValidator
{
    /// <summary>True when a webhook secret is configured, so signatures must be enforced.</summary>
    bool IsEnforced { get; }

    /// <summary>
    /// Validate the provider's <c>event_hash</c> against HMAC-SHA256 of (<paramref name="eventTime"/> +
    /// <paramref name="eventType"/>) using the configured secret.
    /// </summary>
    bool IsValid(string? eventTime, string? eventType, string? providedHash);
}

public sealed class EsignWebhookSignatureValidator : IEsignWebhookSignatureValidator
{
    private readonly EsignConfig _config;
    private readonly ILogger<EsignWebhookSignatureValidator> _logger;

    public EsignWebhookSignatureValidator(IOptions<EsignConfig> config, ILogger<EsignWebhookSignatureValidator> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public bool IsEnforced => !string.IsNullOrWhiteSpace(_config.WebhookSecret);

    public bool IsValid(string? eventTime, string? eventType, string? providedHash)
    {
        if (!IsEnforced)
        {
            // Caller decides what to do; this guard keeps the method total.
            return true;
        }

        if (string.IsNullOrWhiteSpace(providedHash))
        {
            _logger.LogWarning("E-sign webhook is missing its event_hash; rejecting.");
            return false;
        }

        var data = $"{eventTime}{eventType}";
        var expected = Compute(_config.WebhookSecret!, data);

        var match = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(providedHash));

        if (!match)
        {
            _logger.LogWarning("E-sign webhook signature did not match for event_type {EventType}; rejecting.", eventType);
        }

        return match;
    }

    private static string Compute(string secret, string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
