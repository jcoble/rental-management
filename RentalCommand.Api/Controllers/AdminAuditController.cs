using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Admin-only forensic view of the caller's portfolio audit trail. Same filters as
/// <see cref="AuditController"/> (<c>?operation&amp;entityType&amp;entityId&amp;skip&amp;take&amp;search&amp;sort</c>),
/// but the rows include the actor IP address and raw old→new JSON that the landlord-facing
/// <c>/api/v1/audit</c> intentionally withholds. Portfolio-scoped (the cross-tenant IDOR guard from the
/// base) <b>and</b> gated behind the Admin role; the data already lives on the row, so there is no
/// migration. Read-only — the trail is append-only.
/// </summary>
[ApiController]
[Route("api/v1/admin/audit")]
[Authorize(Roles = "Admin")]
[Produces("application/json")]
public class AdminAuditController : AuthenticatedPortfolioControllerBase
{
    private readonly IAuditQueryService _service;

    public AdminAuditController(IAuditQueryService service)
    {
        _service = service;
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
}
