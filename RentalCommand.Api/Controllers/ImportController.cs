using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Import;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CSV / bulk import for a migrating landlord. Upload a spreadsheet of tenants / properties / units
/// and create them in bulk, with a dry-run preview (per-row validation + errors) before committing.
/// All routes are scoped to the caller's server-validated workspace context — there is no
/// <c>{portfolioId}</c> route parameter and a client can never name another portfolio.
/// </summary>
[ApiController]
[Route("api/v1/import")]
[Produces("application/json")]
public class ImportController : ManagementControllerBase
{
    private readonly ICsvImportService _import;

    public ImportController(ICsvImportService import)
    {
        _import = import;
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/import/{entityType}?dryRun=true|false
    //   - multipart/form-data with a `file` field, OR
    //   - a raw text/csv request body
    // -------------------------------------------------------------------------

    /// <summary>
    /// Imports a CSV of <paramref name="entityType"/> (Tenant / Property / Unit). With
    /// <c>dryRun=true</c> (the default) every row is validated and nothing is created; with
    /// <c>dryRun=false</c> the valid rows are created and invalid rows are skipped (errors still
    /// reported). Accepts either a multipart <c>file</c> or a raw <c>text/csv</c> body.
    /// </summary>
    [HttpPost("{entityType}")]
    [ProducesResponseType(typeof(CsvImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CsvImportResult>> Import(
        string entityType,
        [FromQuery] bool dryRun,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        IFormFile? file,
        CancellationToken ct)
    {
        // Resolve the CSV stream: prefer the multipart file, fall back to the raw request body.
        Stream? csv = null;
        if (file is { Length: > 0 })
        {
            csv = file.OpenReadStream();
        }
        else if (Request.ContentLength is > 0 && !Request.HasFormContentType)
        {
            csv = Request.Body;
        }

        if (csv is null)
        {
            return BadRequest(new { error = "A non-empty CSV is required — upload a multipart 'file' or send a text/csv body." });
        }

        try
        {
            CsvImportCommandContext? commandContext = null;
            if (!dryRun && IsPaymentType(entityType))
            {
                var normalizedKey = idempotencyKey?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
                    return BadRequest(new { error = "A valid Idempotency-Key is required for payment imports (maximum 200 characters)." });
                if (!TryGetActiveAccessContext(out var active))
                    return Forbid();

                commandContext = new CsvImportCommandContext(
                    active.UserId, active.SessionId, active.AccessContextId, active.AccessRevision,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))
                        .ToLowerInvariant());
            }

            var result = await _import.ImportAsync(
                GetPortfolioId(), entityType, csv, dryRun, commandContext, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (CsvFormatException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static bool IsPaymentType(string entityType) =>
        entityType.Trim().Equals("payment", StringComparison.OrdinalIgnoreCase)
        || entityType.Trim().Equals("payments", StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // GET /api/v1/import/{entityType}/template
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the CSV header row for an entity type so the web can offer a downloadable, ready-to-fill
    /// template. Served as <c>text/csv</c> with a download filename.
    /// </summary>
    [HttpGet("{entityType}/template")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Template(string entityType)
    {
        try
        {
            var header = _import.GetTemplate(entityType);
            var bytes = System.Text.Encoding.UTF8.GetBytes(header + "\n");
            return File(bytes, "text/csv", $"{entityType.ToLowerInvariant()}-import-template.csv");
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
