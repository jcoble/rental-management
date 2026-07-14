using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for tenants within the caller's portfolio. Scope comes from the server-validated workspace context;
/// list supports <c>?skip&amp;take&amp;search&amp;sort</c>. Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/tenants")]
[Produces("application/json")]
public class TenantController : ManagementControllerBase
{
    private readonly ITenantService _service;
    private readonly ITenantPortalProvisioningService _portalProvisioning;
    private readonly IAuthEmailSender _authEmailSender;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SeedSettings _seedSettings;

    public TenantController(
        ITenantService service,
        ITenantPortalProvisioningService portalProvisioning,
        IAuthEmailSender authEmailSender,
        UserManager<ApplicationUser> userManager,
        IOptions<SeedSettings> seedSettings)
    {
        _service = service;
        _portalProvisioning = portalProvisioning;
        _authEmailSender = authEmailSender;
        _userManager = userManager;
        _seedSettings = seedSettings.Value;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TenantResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TenantResponse>>> List([FromQuery] TenantListQuery query, CancellationToken ct)
    {
        var items = await _service.ListAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(items);
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(TenantListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantListResponse>> ListPage([FromQuery] TenantListQuery query, CancellationToken ct)
    {
        var page = await _service.ListPageAuthorizedAsync(GetWorkspaceReadScope(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAuthorizedAsync(GetWorkspaceReadScope(), id, ct);
        return item == null ? NotFound(new { error = "Tenant not found" }) : Ok(item);
    }

    [HttpPost]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<TenantResponse>> Create([FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAuthorizedAsync(GetWorkspaceReadScope(), request, ct);
        if (created is null)
        {
            return Forbid();
        }

        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantResponse>> Update(int id, [FromBody] UpdateTenantRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAuthorizedAsync(GetWorkspaceReadScope(), id, request, ct);
        return updated == null ? NotFound(new { error = "Tenant not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAuthorizedAsync(GetWorkspaceReadScope(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Tenant not found" });
    }

    // Resident login grants are deliberately absent here. They are relationship-scoped and are
    // created/revoked only by LeaseManagementController's atomic party-access commands.
}
