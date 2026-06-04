using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Esign;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Public, no-login native e-signature endpoints. Anonymous: a signer opens a link containing their
/// opaque, single-use, expiring token, reviews the document + ESIGN/UETA disclosure, and signs or
/// declines. The signer (and thus the portfolio scope) is resolved server-side by the token ONLY — a
/// client-supplied id is never trusted, and one token can only ever expose/act on its own signer's
/// document (IDOR-critical). Mirrors the public rental-application flow.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/sign")]
public sealed class SignController : ControllerBase
{
    private readonly INativeSigningService _signing;

    public SignController(INativeSigningService signing)
    {
        _signing = signing;
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/sign/{token} — the signing package (marks Viewed + audits)
    // -------------------------------------------------------------------------

    [HttpGet("{token}")]
    [ProducesResponseType(typeof(SignPackageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<SignPackageResponse>> GetPackage(string token, CancellationToken ct)
    {
        var result = await _signing.GetPackageAsync(token, IpAddress(), UserAgent(), ct);
        return Map(result);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/sign/{token}/document — stream the PDF under review
    // -------------------------------------------------------------------------

    [HttpGet("{token}/document")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> GetDocument(string token, CancellationToken ct)
    {
        var result = await _signing.GetDocumentAsync(token, ct);
        if (result.Outcome != SignTokenOutcome.Ok)
        {
            return Problem(result);
        }

        var (stream, fileName, contentType) = result.Value;
        // Inline so the signer can read it in-browser before signing.
        Response.Headers.ContentDisposition = $"inline; filename=\"{fileName}\"";
        return File(stream, contentType);
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/sign/{token} — apply the signature + consent
    // -------------------------------------------------------------------------

    [HttpPost("{token}")]
    [ProducesResponseType(typeof(SignActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<SignActionResponse>> Sign(
        string token, [FromBody] SubmitSignatureRequest request, CancellationToken ct)
    {
        var result = await _signing.SignAsync(token, request, IpAddress(), UserAgent(), ct);
        return Map(result);
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/sign/{token}/decline — decline to sign
    // -------------------------------------------------------------------------

    [HttpPost("{token}/decline")]
    [ProducesResponseType(typeof(SignActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<SignActionResponse>> Decline(
        string token, [FromBody] DeclineSignatureRequest? request, CancellationToken ct)
    {
        var result = await _signing.DeclineAsync(token, request ?? new DeclineSignatureRequest(), IpAddress(), UserAgent(), ct);
        return Map(result);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private string? IpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? UserAgent()
    {
        var ua = Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(ua) ? null : (ua.Length > 512 ? ua[..512] : ua);
    }

    private ActionResult<T> Map<T>(SignTokenResult<T> result) => result.Outcome switch
    {
        SignTokenOutcome.Ok => Ok(result.Value),
        SignTokenOutcome.NotFound => NotFound(new { error = result.Error }),
        SignTokenOutcome.Expired => StatusCode(StatusCodes.Status410Gone, new { error = result.Error }),
        SignTokenOutcome.Invalid => BadRequest(new { error = result.Error }),
        _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = "Unexpected error." }),
    };

    private IActionResult Problem<T>(SignTokenResult<T> result) => result.Outcome switch
    {
        SignTokenOutcome.NotFound => NotFound(new { error = result.Error }),
        SignTokenOutcome.Expired => StatusCode(StatusCodes.Status410Gone, new { error = result.Error }),
        SignTokenOutcome.Invalid => BadRequest(new { error = result.Error }),
        _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = "Unexpected error." }),
    };
}
