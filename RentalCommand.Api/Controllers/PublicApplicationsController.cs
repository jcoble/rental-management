using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Public, no-login rental-application endpoints. Anonymous: an applicant opens a shared link
/// containing the portfolio's opaque token, optionally autofills from a photo of their ID / pay stub,
/// and submits. The portfolio is resolved server-side by the token ONLY — a client-supplied
/// portfolioId is never trusted (IDOR-critical).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/public/applications")]
[Produces("application/json")]
public sealed class PublicApplicationsController : ControllerBase
{
    // Maps the LLM extraction field names (snake_case, from IdExtractionSchema) to the application
    // form field names (camelCase) the web/Flutter client binds to.
    private static readonly (string Source, string Target)[] PrefillMap =
    {
        ("first_name", "firstName"),
        ("last_name", "lastName"),
        ("date_of_birth", "dateOfBirth"),
        ("address", "currentAddress"),
        ("employer", "employer"),
        ("monthly_income", "monthlyIncome"),
        // id_last4 is deliberately NOT mapped into the prefill — the full ID number is never
        // surfaced or stored; only the model's last-4 hint exists and we drop it here.
    };

    private readonly IApplicationService _applications;
    private readonly ILlmProvider _llm;
    private readonly UploadSettings _uploadSettings;
    private readonly ILogger<PublicApplicationsController> _logger;

    public PublicApplicationsController(
        IApplicationService applications,
        ILlmProvider llm,
        IOptions<UploadSettings> uploadSettings,
        ILogger<PublicApplicationsController> logger)
    {
        _applications = applications;
        _llm = llm;
        _uploadSettings = uploadSettings.Value;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/public/applications/{token}  — form display info
    // -------------------------------------------------------------------------

    [HttpGet("{token}")]
    [ProducesResponseType(typeof(PublicApplicationFormInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicApplicationFormInfo>> GetForm(string token, CancellationToken ct)
    {
        var info = await _applications.GetPublicFormInfoAsync(token, ct);
        return info is null
            ? NotFound(new { error = "This application link is invalid or no longer active." })
            : Ok(info);
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/public/applications/{token}  — submit an application
    // -------------------------------------------------------------------------

    [HttpPost("{token}")]
    [ProducesResponseType(typeof(SubmitApplicationResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubmitApplicationResult>> Submit(
        string token, [FromBody] SubmitApplicationRequest request, CancellationToken ct)
    {
        // FCRA: consent is mandatory to submit. (DataAnnotations can't enforce "must be true" on a
        // bool, so check it explicitly.)
        if (!request.ConsentGiven)
            return BadRequest(new { error = "You must consent to a background/credit check to submit an application." });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _applications.SubmitAsync(token, request, ip, ct);

        return result is null
            ? NotFound(new { error = "This application link is invalid or no longer active." })
            : StatusCode(StatusCodes.Status201Created, result);
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/public/applications/{token}/scan-id  — autofill from a photo
    // -------------------------------------------------------------------------

    [HttpPost("{token}/scan-id")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ScanIdResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ScanIdResult>> ScanId(
        string token, IFormFile file, CancellationToken ct)
    {
        // The token must still resolve, so a random caller can't burn LLM tokens against this endpoint.
        var formInfo = await _applications.GetPublicFormInfoAsync(token, ct);
        if (formInfo is null)
            return NotFound(new { error = "This application link is invalid or no longer active." });

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty image is required." });

        var contentType = file.ContentType?.Trim() ?? string.Empty;
        var fileName = Path.GetFileName(file.FileName ?? string.Empty);

        // Read the bytes (in memory only — the ID image is never stored server-side).
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Reuse the shared upload safety checks (size, MIME/extension allowlist, magic-byte sniff).
        var header = bytes.Length >= 16 ? bytes[..16] : bytes;
        var (isValid, error) = FileUploadValidator.ValidateScanUpload(
            fileName, contentType, bytes.LongLength, _uploadSettings, header);
        if (!isValid)
            return BadRequest(new { error });

        // Gated extraction: with no LLM key the provider returns all-empty/zero-confidence fields, so
        // we report Extracted=false and the applicant types manually.
        ExtractedFields extracted;
        try
        {
            extracted = await _llm.ExtractAsync(
                bytes, contentType, IdExtractionSchema.Instructions, IdExtractionSchema.Fields, ct: ct);
        }
        catch (Exception ex)
        {
            // Autofill is best-effort; a provider hiccup must not block manual entry.
            _logger.LogWarning(ex, "Photo-ID extraction failed; returning empty prefill for manual entry.");
            return Ok(new ScanIdResult { Extracted = false });
        }

        var result = new ScanIdResult();
        foreach (var (source, target) in PrefillMap)
        {
            if (extracted.Fields.TryGetValue(source, out var field)
                && !string.IsNullOrWhiteSpace(field.Value)
                && field.Confidence > 0m)
            {
                result.Fields[target] = new PrefillField { Value = field.Value, Confidence = field.Confidence };
            }
        }

        result.Extracted = result.Fields.Count > 0;
        return Ok(result);
    }
}
