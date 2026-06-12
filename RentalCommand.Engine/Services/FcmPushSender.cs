using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Engine.Services;

/// <summary>
/// <see cref="IPushSender"/> over Firebase Cloud Messaging (HTTP v1) via the FirebaseAdmin SDK.
/// Placeholder-safe: when <see cref="PushConfig.Enabled"/> is false (no project id / service-account
/// credential) every send is a suppression log and returns <see cref="PushSendResult.Suppressed"/> —
/// the rest of the notification rail (in-app/email/SMS) is unaffected.
///
/// Registered as a singleton: the FirebaseAdmin <see cref="FirebaseApp"/> is a process-global, so we
/// initialise it once, lazily, behind a lock the first time a send is attempted with a valid config.
/// </summary>
public sealed class FcmPushSender : IPushSender
{
    private const string AppName = "rental-command-fcm";

    private readonly PushConfig _cfg;
    private readonly ILogger<FcmPushSender> _logger;
    private readonly object _initLock = new();

    private FirebaseApp? _app;
    private bool _initFailed;

    public FcmPushSender(IOptions<PushConfig> options, ILogger<FcmPushSender> logger)
    {
        _cfg = options.Value;
        _logger = logger;
    }

    public async Task<PushSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data,
        CancellationToken ct = default)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation(
                "[push suppressed — no push provider configured] title={Title}", title);
            return PushSendResult.Suppressed;
        }

        var app = EnsureApp();
        if (app is null)
        {
            return PushSendResult.Suppressed;
        }

        var message = new Message
        {
            Token = deviceToken,
            Notification = new Notification { Title = title, Body = body },
            // String-only data map: the mobile client routes on data["actionUrl"] / data["type"].
            Data = data?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification { ClickAction = "FLUTTER_NOTIFICATION_CLICK" },
            },
            Apns = new ApnsConfig
            {
                Aps = new Aps { Sound = "default", ContentAvailable = true },
            },
        };

        try
        {
            var id = await FirebaseMessaging.GetMessaging(app).SendAsync(message, ct);
            _logger.LogInformation("[push sent] token=…{TokenTail} id={Id}", Tail(deviceToken), id);
            return PushSendResult.Ok();
        }
        catch (FirebaseMessagingException ex) when (
            ex.MessagingErrorCode == MessagingErrorCode.Unregistered
            || ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument)
        {
            // The token is dead (app uninstalled / token rotated) — signal the caller to prune it.
            _logger.LogInformation(
                "[push token invalid — prune] token=…{TokenTail} code={Code}",
                Tail(deviceToken), ex.MessagingErrorCode);
            return PushSendResult.Invalid();
        }
        // Any other exception (network, 5xx, auth) propagates so the outbox worker retries.
    }

    private FirebaseApp? EnsureApp()
    {
        if (_app is not null) return _app;
        if (_initFailed) return null;

        lock (_initLock)
        {
            if (_app is not null) return _app;
            if (_initFailed) return null;

            try
            {
                var credential = LoadCredential();
                _app = FirebaseApp.Create(
                    new AppOptions
                    {
                        Credential = credential,
                        ProjectId = _cfg.FirebaseProjectId,
                    },
                    AppName);
                _logger.LogInformation(
                    "[push] FirebaseApp initialised for project {ProjectId}", _cfg.FirebaseProjectId);
                return _app;
            }
            catch (Exception ex)
            {
                // Bad credential / project — fail soft (suppress) rather than crash the worker, but
                // remember so we don't re-attempt every message.
                _initFailed = true;
                _logger.LogError(ex, "[push] FirebaseApp initialisation failed — push suppressed.");
                return null;
            }
        }
    }

    private GoogleCredential LoadCredential()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.ServiceAccountJson))
        {
            return GoogleCredential.FromJson(_cfg.ServiceAccountJson);
        }
        return GoogleCredential.FromFile(_cfg.ServiceAccountJsonPath!);
    }

    private static string Tail(string token) =>
        token.Length <= 6 ? token : token[^6..];
}
