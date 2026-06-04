using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Security;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Receives e-sign provider (Dropbox Sign / HelloSign) webhook events. Anonymous — the provider
/// authenticates via the event's HMAC signature (<c>event_hash</c>), verified against
/// <c>Esign:WebhookSecret</c>. On a "signed/completed" event we match the lease by the provider envelope
/// id (never a client parameter), store the signed PDF, mark the lease signed, and flip a PendingSignature
/// lease to Active. When the secret is configured, unsigned/forged events are rejected with 403.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/esign")]
public sealed class EsignWebhookController : ControllerBase
{
    // Dropbox Sign delivers the event as a multipart/form field named "json" and expects this exact
    // string echoed back in the response body to confirm the callback is wired up.
    private const string CallbackAck = "Hello API Event Received";

    private readonly ILeaseEsignService _esign;
    private readonly IEsignWebhookSignatureValidator _signatureValidator;
    private readonly EsignConfig _config;
    private readonly ILogger<EsignWebhookController> _logger;

    public EsignWebhookController(
        ILeaseEsignService esign,
        IEsignWebhookSignatureValidator signatureValidator,
        IOptions<EsignConfig> config,
        ILogger<EsignWebhookController> logger)
    {
        _esign = esign;
        _signatureValidator = signatureValidator;
        _config = config.Value;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        var rawJson = await ReadEventJsonAsync(ct);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return BadRequest(new { error = "Missing event payload" });
        }

        EsignEventEnvelope? envelope;
        try
        {
            envelope = ParseEvent(rawJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "E-sign webhook payload was not valid JSON.");
            return BadRequest(new { error = "Invalid event payload" });
        }

        if (envelope is null)
        {
            return BadRequest(new { error = "Invalid event payload" });
        }

        // This endpoint flips lease state, so verify the provider signature before trusting the payload.
        // When no webhook secret is configured (local dev), enforcement is skipped but loudly logged so it
        // is never silently off in a real deployment (mirrors the SMS webhook).
        if (_signatureValidator.IsEnforced)
        {
            if (!_signatureValidator.IsValid(envelope.EventTime, envelope.EventType, envelope.EventHash))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Invalid signature" });
            }
        }
        else
        {
            _logger.LogWarning(
                "E-sign webhook signature verification is DISABLED (no Esign:WebhookSecret configured). " +
                "This endpoint is spoofable until a secret is set.");
        }

        // Match the lease by the provider envelope id. Unknown envelopes are a no-op (still 200, ack the
        // callback) so the provider does not retry forever for events we don't track.
        if (!string.IsNullOrWhiteSpace(envelope.EnvelopeId))
        {
            switch (NormalizeEventType(envelope.EventType))
            {
                case EsignEventKind.Signed:
                    await _esign.HandleSignedEventAsync(envelope.EnvelopeId!, ct);
                    break;
                case EsignEventKind.Declined:
                    await _esign.HandleDeclinedEventAsync(envelope.EnvelopeId!, ct);
                    break;
                default:
                    _logger.LogDebug("E-sign webhook event {EventType} received but not handled.", envelope.EventType);
                    break;
            }
        }

        // Dropbox Sign requires this literal ack string in the body to consider the callback verified.
        return Content(CallbackAck);
    }

    private async Task<string?> ReadEventJsonAsync(CancellationToken ct)
    {
        // Dropbox Sign posts multipart/form-data with a "json" field; accept a raw JSON body too for
        // flexibility / testing.
        if (Request.HasFormContentType && Request.Form.TryGetValue("json", out var jsonField))
        {
            return jsonField.ToString();
        }

        using var reader = new StreamReader(Request.Body);
        return await reader.ReadToEndAsync(ct);
    }

    private static EsignEventEnvelope? ParseEvent(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("event", out var ev))
        {
            return null;
        }

        string? eventType = ev.TryGetProperty("event_type", out var et) ? et.GetString() : null;
        string? eventTime = ev.TryGetProperty("event_time", out var ti) ? ti.GetString() : null;
        string? eventHash = ev.TryGetProperty("event_hash", out var eh) ? eh.GetString() : null;

        // The signed signature_request_id lives under "signature_request".
        string? envelopeId = null;
        if (root.TryGetProperty("signature_request", out var sr)
            && sr.TryGetProperty("signature_request_id", out var srId))
        {
            envelopeId = srId.GetString();
        }

        return new EsignEventEnvelope(eventType, eventTime, eventHash, envelopeId);
    }

    private static EsignEventKind NormalizeEventType(string? eventType) => eventType switch
    {
        // "all_signed" is the terminal completion event; "signed" fires per-signer. Treat both as signed —
        // the service is idempotent and only advances state once.
        "signature_request_all_signed" or "signature_request_signed" => EsignEventKind.Signed,
        "signature_request_declined" => EsignEventKind.Declined,
        _ => EsignEventKind.Other,
    };

    private enum EsignEventKind { Signed, Declined, Other }

    private sealed record EsignEventEnvelope(string? EventType, string? EventTime, string? EventHash, string? EnvelopeId);
}
