using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for tenants within the caller's portfolio. Scope comes from the JWT <c>portfolioId</c> claim;
/// list supports <c>?skip&amp;take&amp;search&amp;sort</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/tenants")]
[Produces("application/json")]
public class TenantController : ManagementControllerBase
{
    private readonly ITenantService _service;
    private readonly ITenantPortalProvisioningService _portalProvisioning;

    public TenantController(ITenantService service, ITenantPortalProvisioningService portalProvisioning)
    {
        _service = service;
        _portalProvisioning = portalProvisioning;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TenantResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TenantResponse>>> List([FromQuery] ListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(TenantListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantListResponse>> ListPage([FromQuery] TenantListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAsync(GetPortfolioId(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Tenant not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<TenantResponse>> Create([FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantResponse>> Update(int id, [FromBody] UpdateTenantRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Tenant not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Tenant not found" });
    }

    /// <summary>
    /// Grants the tenant a portal login on demand (creates an Identity account scoped to this portfolio,
    /// or reports that one already exists). The tenant signs in with their email and the shared tenant
    /// password. Requires the tenant to have an email. Portfolio-scoped via the JWT claim (IDOR guard).
    /// </summary>
    [HttpPost("{id:int}/portal-access")]
    [ProducesResponseType(typeof(GrantPortalAccessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GrantPortalAccessResponse>> GrantPortalAccess(int id, CancellationToken ct)
    {
        var result = await _portalProvisioning.EnsurePortalAccountForTenantAsync(id, GetPortfolioId(), ct);

        return result.Status switch
        {
            PortalAccountStatus.TenantNotFound => NotFound(new { error = "Tenant not found" }),
            PortalAccountStatus.NoEmail => BadRequest(new
            {
                error = "This tenant has no email address. Add an email before granting portal access."
            }),
            PortalAccountStatus.Failed => BadRequest(new
            {
                error = result.Error ?? "Could not grant portal access."
            }),
            _ => Ok(new GrantPortalAccessResponse
            {
                Status = result.Status.ToString(),
                AlreadyExisted = result.Status == PortalAccountStatus.AlreadyExisted,
                Email = result.Email,
            }),
        };
    }
}
