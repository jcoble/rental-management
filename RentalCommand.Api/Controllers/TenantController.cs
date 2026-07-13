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

    /// <summary>
    /// Turns the tenant's portal access on or off (the staff toggle). Enabling ensures a login exists
    /// (provisioning one scoped to this portfolio if needed) and clears any lock; disabling locks the
    /// login so the tenant can't sign in. Portfolio-scoped via the JWT claim (IDOR guard). Returns the
    /// resulting <c>portalAccess</c> state.
    /// </summary>
    [HttpPost("{id:int}/portal-access")]
    [ProducesResponseType(typeof(PortalAccessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalAccessResponse>> SetPortalAccess(
        int id, [FromBody] SetPortalAccessRequest request, CancellationToken ct)
    {
        var result = await _portalProvisioning.SetPortalAccessAuthorizedAsync(
            GetWorkspaceReadScope(), id, request.Enabled, ct);

        return result.Outcome switch
        {
            SetPortalAccessOutcome.TenantNotFound => NotFound(new { error = "Tenant not found" }),
            SetPortalAccessOutcome.NoEmail => BadRequest(new
            {
                error = "This tenant has no email address. Add an email before enabling portal access."
            }),
            SetPortalAccessOutcome.Failed => BadRequest(new
            {
                error = result.Error ?? "Could not update portal access."
            }),
            _ => Ok(new PortalAccessResponse
            {
                PortalAccess = result.Access.ToString().ToLowerInvariant(),
                Email = result.Email,
            }),
        };
    }

    /// <summary>
    /// Sends (or resends) the tenant their resident-portal invite email. Ensures their portal login
    /// exists, then emails their sign-in email + the shared temporary password and a login link. This
    /// is the ONLY place an invite email goes out (tenant creation provisions silently). Staff-only,
    /// portfolio-scoped via the JWT claim (IDOR guard). Requires the tenant to have an email.
    /// </summary>
    [HttpPost("{id:int}/portal-invite")]
    [ProducesResponseType(typeof(PortalInviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortalInviteResponse>> SendPortalInvite(int id, CancellationToken ct)
    {
        var result = await _portalProvisioning.EnsurePortalAccountForTenantAuthorizedAsync(
            GetWorkspaceReadScope(), id, ct);

        switch (result.Status)
        {
            case PortalAccountStatus.TenantNotFound:
                return NotFound(new { error = "Tenant not found" });
            case PortalAccountStatus.NoEmail:
                return BadRequest(new
                {
                    error = "This tenant has no email address. Add an email before sending a portal invite."
                });
            case PortalAccountStatus.Failed:
                return BadRequest(new { error = result.Error ?? "Could not send the portal invite." });
        }

        // Account exists (Created or AlreadyExisted) — load the Identity user so we can email them.
        var user = result.Email is null ? null : await _userManager.FindByEmailAsync(result.Email);
        if (user == null)
        {
            return BadRequest(new { error = "Could not send the portal invite." });
        }

        await _authEmailSender.SendTenantPortalInviteAsync(user, _seedSettings.TenantPassword, ct);

        return Ok(new PortalInviteResponse
        {
            Email = result.Email,
            AlreadyExisted = result.Status == PortalAccountStatus.AlreadyExisted,
        });
    }
}
