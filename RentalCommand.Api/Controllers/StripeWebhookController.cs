using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Payments;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Receives Stripe webhook events. Anonymous — Stripe authenticates via the signature header.
/// When Stripe keys are absent the endpoint returns 503 (not enabled); it never throws at startup.
/// </summary>
[ApiController]
[Route("api/v1/payments/stripe")]
[AllowAnonymous]
public class StripeWebhookController : ControllerBase
{
    private readonly IStripePaymentService _stripeService;
    private readonly StripeConfig _config;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(
        IStripePaymentService stripeService,
        IOptions<StripeConfig> config,
        ILogger<StripeWebhookController> logger)
    {
        _stripeService = stripeService;
        _config = config.Value;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> HandleWebhook(CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Online payments are not enabled." });
        }

        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(ct);
        var signature = Request.Headers["Stripe-Signature"].ToString();

        if (string.IsNullOrEmpty(signature))
        {
            _logger.LogWarning("Stripe webhook received without Stripe-Signature header");
            return BadRequest(new { error = "Missing Stripe-Signature header" });
        }

        try
        {
            await _stripeService.HandleWebhookEventAsync(json, signature, ct);
            return Ok();
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogError(ex, "Stripe webhook signature verification failed");
            return BadRequest(new { error = "Invalid signature" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing Stripe webhook");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Webhook processing error" });
        }
    }
}
