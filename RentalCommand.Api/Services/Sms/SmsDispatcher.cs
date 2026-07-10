using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Api.Services.Sms;

/// <summary>
/// Resolves the effective SMS provider + credentials for a portfolio and dispatches through the
/// matching <see cref="ISmsProvider"/>. Resolution order:
/// <list type="number">
///   <item>The portfolio's own configured provider (BYO creds), if complete.</item>
///   <item>The platform-level env credentials (SignalWire preferred, then Twilio, Telnyx, Vonage).</item>
///   <item>Nothing configured → suppression log (fail-soft, never throws).</item>
/// </list>
/// A non-success response from a *configured* provider DOES throw so the outbox worker retries.
/// </summary>
public sealed class SmsDispatcher : ISmsDispatcher
{
    private readonly INotificationSettingsService _settings;
    private readonly NotificationsConfig _cfg;
    private readonly IReadOnlyDictionary<SmsProviderKey, ISmsProvider> _providers;
    private readonly ILogger<SmsDispatcher> _logger;

    public SmsDispatcher(
        INotificationSettingsService settings,
        IOptions<NotificationsConfig> options,
        IEnumerable<ISmsProvider> providers,
        ILogger<SmsDispatcher> logger)
    {
        _settings = settings;
        _cfg = options.Value;
        // Last registration wins per key (none collide today). Fail-closed lookup below.
        _providers = providers.ToDictionary(p => p.Key, p => p);
        _logger = logger;
    }

    public async Task<NotificationDeliveryReceipt> SendAsync(
        int? portfolioId,
        string toPhoneNumber,
        string message,
        NotificationDeliveryContext delivery,
        CancellationToken ct = default)
    {
        var normalizedTo = NormalizeSmsNumber(toPhoneNumber);

        var creds = await ResolveAsync(portfolioId, ct);
        if (creds is null)
        {
            _logger.LogInformation(
                "[SMS suppressed — no SMS provider configured] portfolio {PortfolioId} to {To}: {Message}",
                portfolioId, normalizedTo, message);
            throw new NotificationDeliverySuppressedException(
                "SMS delivery is not configured. No external provider accepted this message.");
        }

        if (!_providers.TryGetValue(creds.Provider, out var provider))
        {
            // A provider was selected but no implementation is registered — config bug, not a crash.
            _logger.LogError(
                "[SMS suppressed — provider {Provider} has no registered implementation] portfolio {PortfolioId} to {To}.",
                creds.Provider, portfolioId, normalizedTo);
            throw new NotificationDeliverySuppressedException(
                $"SMS provider {creds.Provider} is configured but has no registered implementation.");
        }

        var receipt = await provider.SendAsync(creds, normalizedTo, message, delivery, ct);
        _logger.LogInformation(
            "[SMS sent via {Provider}] portfolio {PortfolioId} To={To}",
            creds.Provider, portfolioId, normalizedTo);
        return new NotificationDeliveryReceipt(creds.Provider.ToString(), receipt.ProviderMessageId);
    }

    /// <summary>Portfolio creds first, then platform-env fallback. Null = nothing usable configured.</summary>
    private async Task<SmsCredentials?> ResolveAsync(int? portfolioId, CancellationToken ct)
    {
        if (portfolioId is int pid)
        {
            var perPortfolio = await _settings.GetSmsCredentialsAsync(pid, ct);
            if (perPortfolio is not null)
                return perPortfolio;
        }

        return ResolvePlatformFallback();
    }

    /// <summary>Maps platform env config to <see cref="SmsCredentials"/>, preferring SignalWire.</summary>
    private SmsCredentials? ResolvePlatformFallback()
    {
        if (_cfg.SignalWire.Enabled)
            return new SmsCredentials(SmsProviderKey.SignalWire, _cfg.SignalWire.ProjectId, _cfg.SignalWire.Token, _cfg.SignalWire.SpaceUrl, _cfg.SignalWire.FromNumber);
        if (_cfg.Twilio.Enabled)
            return new SmsCredentials(SmsProviderKey.Twilio, _cfg.Twilio.AccountSid, _cfg.Twilio.AuthToken, null, _cfg.Twilio.FromNumber);
        if (_cfg.Telnyx.Enabled)
            return new SmsCredentials(SmsProviderKey.Telnyx, _cfg.Telnyx.ApiKey, null, null, _cfg.Telnyx.FromNumber);
        if (_cfg.Vonage.Enabled)
            return new SmsCredentials(SmsProviderKey.Vonage, _cfg.Vonage.ApiKey, _cfg.Vonage.ApiSecret, null, _cfg.Vonage.FromNumber);
        return null;
    }

    /// <summary>Best-effort E.164 normalization (US default), matching the prior channel behaviour.</summary>
    public static string NormalizeSmsNumber(string input)
    {
        var trimmed = input.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());

        if (digits.Length == 10)
            return "+1" + digits;
        if (digits.Length == 11 && digits.StartsWith("1", StringComparison.Ordinal))
            return "+" + digits;
        if (trimmed.StartsWith("+", StringComparison.Ordinal) && digits.Length > 0)
            return "+" + digits;
        return trimmed;
    }
}
