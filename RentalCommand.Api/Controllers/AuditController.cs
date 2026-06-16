using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Read-only access to the caller's portfolio audit trail (append-only
/// <see cref="Core.Entities.AuditLog"/>). Scope comes from the JWT <c>portfolioId</c> claim; the
/// list supports <c>?operation&amp;entityType&amp;entityId&amp;skip&amp;take&amp;search&amp;sort</c>
/// and defaults to newest-first. There are no create/update/delete operations.
/// </summary>
[ApiController]
[Route("api/v1/audit")]
[Produces("application/json")]
public class AuditController : ManagementControllerBase
{
    private readonly IAuditQueryService _service;

    public AuditController(IAuditQueryService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AuditEntryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuditEntryResponse>>> List(
        [FromQuery] ListQuery query,
        [FromQuery] AuditLogOperation? operation,
        [FromQuery] string? entityType,
        [FromQuery] int? entityId,
        CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), operation, entityType, entityId, query, ct);
        return Ok(items);
    }
}
