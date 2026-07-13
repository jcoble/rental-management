using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Platform-operator forensic view of the caller's selected portfolio audit trail. Same filters as
/// <see cref="AuditController"/> (<c>?operation&amp;entityType&amp;entityId&amp;skip&amp;take&amp;search&amp;sort</c>),
/// but the rows include the actor IP address and raw old→new JSON that the landlord-facing
/// <c>/api/v1/audit</c> intentionally withholds. Portfolio-scoped (the cross-tenant IDOR guard from the
/// base) <b>and</b> gated by the database-backed platform-operator allowlist. Workspace administrators
/// do not receive this raw forensic surface merely because they manage their Team. The data already
/// lives on the row, so there is no migration. Read-only — the trail is append-only.
/// </summary>
[ApiController]
[Route("api/v1/admin/audit")]
[Authorize(Policy = PlatformAdminPolicy.Name)]
[Produces("application/json")]
public class AdminAuditController : AuthenticatedPortfolioControllerBase
{
    private readonly IAuditQueryService _service;
    private readonly TimeProvider _timeProvider;

    public AdminAuditController(IAuditQueryService service, TimeProvider timeProvider)
    {
        _service = service;
        _timeProvider = timeProvider;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AdminAuditEntryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AdminAuditEntryResponse>>> List(
        [FromQuery] ListQuery query,
        [FromQuery] AuditLogOperation? operation,
        [FromQuery] string? entityType,
        [FromQuery] int? entityId,
        CancellationToken ct)
    {
        var items = await _service.ListForensicAsync(GetPortfolioId(), operation, entityType, entityId, query, ct);
        return Ok(items);
    }

    /// <summary>
    /// Streams the currently-filtered forensic audit trail as CSV. Honors the same
    /// <c>?search&amp;operation&amp;entityType&amp;entityId</c> filters as the list (paging is ignored —
    /// the export is the whole filtered set). Rows are pulled from Postgres and written to the response
    /// body one at a time, so an unbounded result set is never buffered in API memory. Admin-only, same
    /// portfolio scope as the page.
    /// </summary>
    [HttpGet("export")]
    [Produces("text/csv")]
    public async Task ExportCsv(
        [FromQuery] ListQuery query,
        [FromQuery] AuditLogOperation? operation,
        [FromQuery] string? entityType,
        [FromQuery] int? entityId,
        CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();
        var stamp = _timeProvider.UtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"audit-{stamp}.csv\"";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false));

        await writer.WriteLineAsync(
            "Timestamp,Action,Description,Actor,UserId,EntityType,EntityId,IpAddress,ChangeReason");

        await foreach (var row in _service.StreamForensicAsync(portfolioId, operation, entityType, entityId, query, ct))
        {
            var line = string.Join(',',
                Csv(row.Timestamp.ToString("o", CultureInfo.InvariantCulture)),
                Csv(row.OperationName),
                Csv(row.Description),
                Csv(row.Actor),
                Csv(row.UserId?.ToString(CultureInfo.InvariantCulture)),
                Csv(row.EntityType),
                Csv(row.EntityId.ToString(CultureInfo.InvariantCulture)),
                Csv(row.IpAddress),
                Csv(row.ChangeReason));

            await writer.WriteLineAsync(line);
        }

        await writer.FlushAsync(ct);
    }

    /// <summary>RFC 4180 CSV field escaping: quote when the value holds a comma, quote, or newline.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
