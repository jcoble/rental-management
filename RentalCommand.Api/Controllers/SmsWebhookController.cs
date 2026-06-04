using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Security;

namespace RentalCommand.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/sms")]
public class SmsWebhookController : ControllerBase
{
    private readonly ISmsInboundRentConfirmationService _rentConfirmation;
    private readonly ISmsWebhookSignatureValidator _signatureValidator;
    private readonly ILogger<SmsWebhookController> _logger;

    public SmsWebhookController(
        ISmsInboundRentConfirmationService rentConfirmation,
        ISmsWebhookSignatureValidator signatureValidator,
        ILogger<SmsWebhookController> logger)
    {
        _rentConfirmation = rentConfirmation;
        _signatureValidator = signatureValidator;
        _logger = logger;
    }

    [HttpPost("inbound")]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    [Produces("application/xml")]
    public async Task<IActionResult> Inbound(
        [FromForm(Name = "From")] string? from,
        [FromForm(Name = "Body")] string? body,
        CancellationToken ct)
    {
        // This endpoint mutates money (marks rent paid), so verify the provider signature before
        // trusting the form. When no provider auth token is configured (local dev), enforcement is
        // skipped but loudly logged so it is never silently off in a real deployment.
        if (_signatureValidator.IsEnforced)
        {
            if (!_signatureValidator.IsValid(Request))
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }
        }
        else
        {
            _logger.LogWarning(
                "Inbound SMS webhook signature verification is DISABLED (no SignalWire/Twilio auth token configured). " +
                "This endpoint is spoofable until a provider token is set.");
        }

        var result = await _rentConfirmation.HandleAsync(from, body, DateTime.UtcNow, ct);
        return Content(ToMessageResponse(result.ResponseMessage), "application/xml", Encoding.UTF8);
    }

    private static string ToMessageResponse(string message)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Message>{WebUtility.HtmlEncode(message)}</Message></Response>";
}
