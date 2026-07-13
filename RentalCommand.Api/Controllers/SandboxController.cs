using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Account-wide Sandbox/Live lifecycle for the caller's workspace. Scope comes from the
/// server-validated canonical access context — never a request parameter — so a caller can only read
/// or graduate their own workspace. "Go Live" is a one-way graduation that wipes seeded demo data.
/// </summary>
[ApiController]
[Route("api/v1/portfolio")]
[Produces("application/json")]
public class SandboxController : ManagementControllerBase
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
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    [ProducesResponseType(typeof(SandboxStateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SandboxStateResponse>> GoLive(CancellationToken ct)
    {
        var state = await _sandbox.GoLiveAsync(GetPortfolioId(), ct);
        return state is null ? NotFound(new { error = "Portfolio not found" }) : Ok(state);
    }

    /// <summary>
    /// Records the first-login Sandbox-vs-Live decision for the caller's own account.
    /// <c>{ "mode": "sandbox" }</c> seeds the demo portfolio; <c>{ "mode": "live" }</c> keeps an empty real
    /// portfolio. Idempotent — once a choice is recorded, repeat calls return the current state without
    /// re-seeding or wiping. Returns the resulting sandbox state.
    /// </summary>
    [HttpPost("onboarding-choice")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.AccountDestructiveActions)]
    [ProducesResponseType(typeof(SandboxStateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SandboxStateResponse>> OnboardingChoice(
        [FromBody] OnboardingChoiceRequest request, CancellationToken ct)
    {
        var mode = request.Mode?.Trim().ToLowerInvariant();
        OnboardingChoice choice = mode switch
        {
            "sandbox" => Services.Domain.OnboardingChoice.Sandbox,
            "live" => Services.Domain.OnboardingChoice.Live,
            _ => Services.Domain.OnboardingChoice.Pending,
        };

        if (choice == Services.Domain.OnboardingChoice.Pending)
        {
            return BadRequest(new { error = "mode must be 'sandbox' or 'live'" });
        }

        var state = await _sandbox.ApplyOnboardingChoiceAsync(GetPortfolioId(), choice, ct);
        return state is null ? NotFound(new { error = "Portfolio not found" }) : Ok(state);
    }
}
