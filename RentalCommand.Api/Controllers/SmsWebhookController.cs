using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Security;

namespace RentalCommand.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/sms")]
public class SmsWebhookController : ControllerBase
{
    private readonly ISmsInboundRouter _router;
    private readonly ISmsWebhookSignatureValidator _signatureValidator;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SmsWebhookController> _logger;

    public SmsWebhookController(
        ISmsInboundRouter router,
        ISmsWebhookSignatureValidator signatureValidator,
        IHostEnvironment environment,
        ILogger<SmsWebhookController> logger)
    {
        _router = router;
        _signatureValidator = signatureValidator;
        _environment = environment;
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
        // This endpoint mutates money (marks rent paid) and closes work orders, so verify the provider
        // signature before trusting the form. Fail CLOSED outside Development: if no provider auth token is
        // configured we cannot prove the request came from the SMS provider, so reject it (403) rather than
        // act on a potentially forged "From=<tenant>&Body=YES". Skip-with-warning is allowed ONLY in local dev.
        if (!_signatureValidator.IsEnforced)
        {
            if (!_environment.IsDevelopment())
            {
                _logger.LogWarning(
                    "Inbound SMS webhook REJECTED: signature verification is not configured (no SignalWire/Twilio " +
                    "auth token) in a non-Development environment. Refusing to process an unverifiable, spoofable request.");
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            _logger.LogWarning(
                "Inbound SMS webhook signature verification is DISABLED (no SignalWire/Twilio auth token configured). " +
                "This is permitted only in Development; the endpoint is spoofable until a provider token is set.");
        }
        else if (!_signatureValidator.IsValid(Request))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        // Route to vendor-DONE (job completion) or fall back to tenant rent-YES confirmation.
        var responseMessage = await _router.RouteAsync(from, body, DateTime.UtcNow, ct);
        return Content(ToMessageResponse(responseMessage), "application/xml", Encoding.UTF8);
    }

    private static string ToMessageResponse(string message)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Message>{WebUtility.HtmlEncode(message)}</Message></Response>";
}
