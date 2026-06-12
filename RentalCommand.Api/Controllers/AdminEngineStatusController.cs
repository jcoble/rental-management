using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Services.Admin;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Admin-only, system-wide Engine health endpoint backing the admin "Engine Health" page.
/// Reads the worker heartbeat table the Engine writes (the Engine has no HTTP port, so the
/// DB heartbeat is the contract between the two processes) and surfaces per-worker liveness,
/// the running Engine instance, and the active LLM provider/model.
///
/// Not portfolio-scoped — engine health is infrastructure, not tenant data — so this inherits
/// plain <see cref="ControllerBase"/> with an Admin role gate rather than the portfolio base.
/// </summary>
[ApiController]
[Route("api/v1/admin/engine-status")]
// Platform-operator surface — gated by the PlatformAdmin email allowlist (F6 / TSK-212),
// NOT the landlord Admin role. Ordinary portfolio admins must not see Engine internals.
[Authorize(Policy = "PlatformAdmin")]
[Produces("application/json")]
public class AdminEngineStatusController : ControllerBase
{
    private readonly IAdminEngineStatusService _service;

    public AdminEngineStatusController(IAdminEngineStatusService service)
    {
        _service = service;
    }

    /// <summary>Aggregate Engine health: overall status, per-worker heartbeats, instance, LLM config.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(EngineStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EngineStatusResponse>> GetEngineStatus(CancellationToken ct)
    {
        var result = await _service.GetEngineStatusAsync(ct);
        return Ok(result);
    }
}
