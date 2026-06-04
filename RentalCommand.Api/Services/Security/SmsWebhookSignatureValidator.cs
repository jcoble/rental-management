using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Services.Security;

/// <summary>
/// Validates the signature on inbound SMS provider webhooks (Twilio and the SignalWire LaML
/// "Compatibility" API, which use an identical scheme): base64(HMAC-SHA1(authToken, requestUrl +
/// each POST param sorted by name as name+value)). Without this, the <c>[AllowAnonymous]</c> SMS
/// webhooks are spoofable — anyone could POST <c>From=&lt;tenant phone&gt;&amp;Body=YES</c> to mark
/// rent paid. The token is the same secret used to send SMS, so verification is "free" once a
/// provider is configured.
/// </summary>
public interface ISmsWebhookSignatureValidator
{
    /// <summary>True when at least one provider auth token is configured, so signatures must be enforced.</summary>
    bool IsEnforced { get; }

    /// <summary>Validates the <c>X-Twilio-Signature</c> header against the request URL + sorted form params.</summary>
    bool IsValid(HttpRequest request);
}

public sealed class SmsWebhookSignatureValidator : ISmsWebhookSignatureValidator
{
    private const string SignatureHeader = "X-Twilio-Signature";

    private readonly NotificationsConfig _config;
    private readonly ILogger<SmsWebhookSignatureValidator> _logger;

    public SmsWebhookSignatureValidator(IOptions<NotificationsConfig> config, ILogger<SmsWebhookSignatureValidator> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    // SignalWire signs with its API token; Twilio with its AuthToken. Accept either so the same
    // webhook works whichever provider is configured (both use the same HMAC-SHA1 algorithm).
    private IEnumerable<string> AuthTokens()
    {
        if (!string.IsNullOrWhiteSpace(_config.SignalWire.Token))
        {
            yield return _config.SignalWire.Token!;
        }

        if (!string.IsNullOrWhiteSpace(_config.Twilio.AuthToken))
        {
            yield return _config.Twilio.AuthToken!;
        }
    }

    public bool IsEnforced => AuthTokens().Any();

    public bool IsValid(HttpRequest request)
    {
        var provided = request.Headers[SignatureHeader].ToString();
        if (string.IsNullOrEmpty(provided))
        {
            _logger.LogWarning("Inbound SMS webhook is missing the {Header} header; rejecting.", SignatureHeader);
            return false;
        }

        var url = BuildSignedUrl(request);

        // Twilio/SignalWire signature base string: the full URL, then every POST parameter sorted
        // by name (ordinal), each appended as name immediately followed by its value, no separators.
        var builder = new StringBuilder(url);
        if (request.HasFormContentType)
        {
            foreach (var key in request.Form.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                builder.Append(key);
                builder.Append(request.Form[key].ToString());
            }
        }

        var data = builder.ToString();
        var providedBytes = Encoding.UTF8.GetBytes(provided);

        foreach (var token in AuthTokens())
        {
            var expected = Compute(token, data);
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), providedBytes))
            {
                return true;
            }
        }

        _logger.LogWarning("Inbound SMS webhook signature did not match for {Url}; rejecting.", url);
        return false;
    }

    private string BuildSignedUrl(HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(_config.PublicWebhookBaseUrl))
        {
            var baseUrl = _config.PublicWebhookBaseUrl!.TrimEnd('/');
            return $"{baseUrl}{request.PathBase}{request.Path}{request.QueryString}";
        }

        return $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
    }

    private static string Compute(string authToken, string data)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash);
    }
}
