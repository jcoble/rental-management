using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Account-wide Sandbox/Live lifecycle for the caller's portfolio. Scope comes from the JWT
/// <c>portfolioId</c> claim — never a request parameter — so a caller can only read or graduate their
/// OWN portfolio (IDOR-safe). "Go Live" is a one-way graduation that wipes the seeded demo data.
/// </summary>
[ApiController]
[Route("api/v1/portfolio")]
[Produces("application/json")]
public class SandboxController : AuthenticatedPortfolioControllerBase
{
    private readonly ISandboxService _sandbox;

    public SandboxController(ISandboxService sandbox)
    {
        _sandbox = sandbox;
    }

    /// <summary>Current Sandbox/Live state of the caller's account (portfolio).</summary>
    [HttpGet("sandbox-state")]
    [ProducesResponseType(typeof(SandboxStateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SandboxStateResponse>> GetState(CancellationToken ct)
    {
        var state = await _sandbox.GetStateAsync(GetPortfolioId(), ct);
        return state is null ? NotFound(new { error = "Portfolio not found" }) : Ok(state);
    }

    /// <summary>
    /// One-way "Go Live": wipes the caller's seeded demo data and flips the account to Live. Idempotent —
    /// calling it on an already-Live account is a no-op that returns the Live state. Returns the new state.
    /// </summary>
    [HttpPost("go-live")]
    [ProducesResponseType(typeof(SandboxStateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SandboxStateResponse>> GoLive(CancellationToken ct)
    {
        var state = await _sandbox.GoLiveAsync(GetPortfolioId(), ct);
        return state is null ? NotFound(new { error = "Portfolio not found" }) : Ok(state);
    }
}
