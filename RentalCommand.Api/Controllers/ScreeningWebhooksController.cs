using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Provider-neutral callback ingress for the one screening adapter installed for the Rental
/// Command platform. Landlords never configure provider credentials. The adapter authenticates
/// and normalizes the callback before domain persistence sees it.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/webhooks/screening/{providerKey}")]
public sealed class ScreeningWebhooksController : ControllerBase
{
    private const int MaxCallbackBytes = 256 * 1024;
    private readonly IScreeningProvider _provider;
    private readonly IScreeningService _screening;
    private readonly ILogger<ScreeningWebhooksController> _logger;

    public ScreeningWebhooksController(
        IScreeningProvider provider,
        IScreeningService screening,
        ILogger<ScreeningWebhooksController> logger)
    {
        _provider = provider;
        _screening = screening;
        _logger = logger;
    }

    [HttpPost]
    [Consumes("application/json", "application/*+json")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Receive(string providerKey, CancellationToken ct)
    {
        var descriptor = _provider.Descriptor;
        if (!descriptor.IsConfigured
            || !descriptor.Capabilities.SupportsStatusWebhooks
            || !string.Equals(providerKey, descriptor.Key, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (Request.ContentLength > MaxCallbackBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        var body = await ReadBodyAsync(Request.Body, ct);
        if (body == null)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (body.Length == 0)
            return BadRequest(new { error = "empty_callback" });

        var headers = Request.Headers.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var verification = await _provider.VerifyDeliveryAsync(
            new ScreeningProviderCallback(
                descriptor.Key,
                body,
                Request.ContentType,
                headers),
            ct);

        if (!verification.IsAuthentic)
        {
            _logger.LogWarning(
                "Rejected unauthenticated screening callback for provider {ProviderKey} ({ErrorCode}).",
                descriptor.Key,
                verification.ErrorCode ?? "signature_invalid");
            return Unauthorized(new { error = verification.ErrorCode ?? "signature_invalid" });
        }

        if (verification.Delivery == null)
            return BadRequest(new { error = verification.ErrorCode ?? "callback_invalid" });
        if (!string.Equals(verification.Delivery.ProviderKey, descriptor.Key, StringComparison.Ordinal))
            return BadRequest(new { error = "provider_key_mismatch" });

        var applied = await _screening.ApplyProviderDeliveryAsync(verification.Delivery, ct);
        if (applied == null)
            return NotFound(new { error = "screening_reference_not_found" });

        return Accepted(new
        {
            received = true,
            screeningId = applied.Id,
            status = applied.Status,
        });
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream source, CancellationToken ct)
    {
        await using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                return destination.ToArray();
            if (destination.Length + read > MaxCallbackBytes)
                return null;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
