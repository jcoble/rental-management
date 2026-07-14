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
    private readonly IConversationService _conversations;

    public LeasingWorkspaceController(
        ILeasingWorkspaceService workspace,
        IConversationService conversations)
        => (_workspace, _conversations) = (workspace, conversations);

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

    [HttpGet("rentals/{unitId:int}")]
    public async Task<ActionResult<LeasingRentalDetailResponse>> Rental(int unitId, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        var response = await _workspace.GetRentalAsync(scope, unitId, ct);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpGet("applications/{id:int}")]
    public async Task<ActionResult<LeasingApplicationDetailResponse>> Application(int id, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        var response = await _workspace.GetApplicationAsync(scope, id, ct);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpGet("appointments/{id:int}")]
    public async Task<ActionResult<LeasingAppointmentDetailResponse>> Appointment(int id, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        var response = await _workspace.GetAppointmentAsync(scope, id, ct);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpGet("conversations/{id:int}")]
    public async Task<ActionResult<LeasingConversationDetailResponse>> Conversation(int id, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        var response = await _workspace.GetConversationAsync(scope, id, ct);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpPost("conversations/{id:int}/messages")]
    public async Task<IActionResult> Reply(
        int id, [FromBody] PostMessageRequest request, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        if (!await _workspace.CanAccessConversationAsync(scope, id, ct)) return NotFound();
        var response = await _conversations.PostMessageAuthorizedForCapabilityAsync(
            scope, id, request.Body, request.Channels, request.OperationKey,
            CapabilityKeys.LeasingOnboardingManage, ct);
        return response is null ? NotFound() : NoContent();
    }

    [HttpGet("move-ins/{id:int}")]
    public async Task<ActionResult<LeasingMoveInDetailResponse>> MoveIn(int id, CancellationToken ct)
    {
        if (!TryGetLeasingScope(out var scope)) return Forbid();
        var response = await _workspace.GetMoveInAsync(scope, id, ct);
        return response is null ? NotFound() : Ok(response);
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
