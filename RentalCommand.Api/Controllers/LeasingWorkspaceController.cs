using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Purpose-built Leasing Agent read surface. Every endpoint is admitted only while the current
/// canonical experience is Leasing; management membership or overlapping capabilities do not turn
/// these projections into an alternate management API.
/// </summary>
[ApiController]
[Route("api/v1/leasing")]
[Produces("application/json")]
public sealed class LeasingWorkspaceController : ManagementControllerBase
{
    private readonly ILeasingWorkspaceService _workspace;

    public LeasingWorkspaceController(ILeasingWorkspaceService workspace) => _workspace = workspace;

    [HttpGet("today")]
    public async Task<ActionResult<LeasingTodayResponse>> Today(CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        var response = await _workspace.GetTodayAsync(scope, ct);
        return response is null ? Forbid() : Ok(response);
    }

    [HttpGet("pipeline/page")]
    public async Task<ActionResult<LeasingPipelinePageResponse>> Pipeline(
        [FromQuery] ListQuery query, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        return Ok(await _workspace.ListPipelineAsync(scope, query, ct));
    }

    [HttpGet("rentals/page")]
    public async Task<ActionResult<LeasingRentalPageResponse>> Rentals(
        [FromQuery] ListQuery query, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        return Ok(await _workspace.ListRentalsAsync(scope, query, ct));
    }

    [HttpGet("calendar/page")]
    public async Task<ActionResult<LeasingCalendarPageResponse>> Calendar(
        [FromQuery] ListQuery query, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        return Ok(await _workspace.ListCalendarAsync(scope, query, ct));
    }

    [HttpGet("inbox/page")]
    public async Task<ActionResult<LeasingInboxPageResponse>> Inbox(
        [FromQuery] ListQuery query, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        return Ok(await _workspace.ListInboxAsync(scope, query, ct));
    }

    private bool TryGetLeasingScope(out WorkspaceReadScope scope)
    {
        scope = default;
        if (!TryGetActiveAccessContext(out var active) ||
            active.LastAuthorizedExperience != WorkspaceExperience.Leasing ||
            active.WorkspaceMembershipId is null)
        {
            return false;
        }

        scope = new WorkspaceReadScope(
            active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
        return true;
    }
}
