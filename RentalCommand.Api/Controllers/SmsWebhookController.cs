using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/sms")]
public class SmsWebhookController : ControllerBase
{
    private readonly ISmsInboundRentConfirmationService _rentConfirmation;

    public SmsWebhookController(ISmsInboundRentConfirmationService rentConfirmation)
    {
        _rentConfirmation = rentConfirmation;
    }

    [HttpPost("inbound")]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    [Produces("application/xml")]
    public async Task<IActionResult> Inbound(
        [FromForm(Name = "From")] string? from,
        [FromForm(Name = "Body")] string? body,
        CancellationToken ct)
    {
        var result = await _rentConfirmation.HandleAsync(from, body, DateTime.UtcNow, ct);
        return Content(ToMessageResponse(result.ResponseMessage), "application/xml", Encoding.UTF8);
    }

    private static string ToMessageResponse(string message)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Message>{WebUtility.HtmlEncode(message)}</Message></Response>";
}
