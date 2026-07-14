using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Relationship-scoped Owner experience. This is deliberately separate from management controllers:
/// an Owner relationship grants no Team membership, management capability, or administrative route.
/// </summary>
[ApiController]
[Route("api/v1/owner")]
[Produces("application/json")]
public sealed class OwnerPortalController : AuthenticatedPortfolioControllerBase
{
    private readonly IOwnerPortalService _portal;
    private readonly IOwnerStatementService _statements;
    private readonly TimeProvider _timeProvider;

    public OwnerPortalController(
        IOwnerPortalService portal,
        IOwnerStatementService statements,
        TimeProvider timeProvider)
    {
        _portal = portal;
        _statements = statements;
        _timeProvider = timeProvider;
    }

    [HttpGet("overview")]
    [ProducesResponseType(typeof(OwnerPortalOverviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OwnerPortalOverviewResponse>> Overview(CancellationToken ct)
    {
        var response = await _portal.GetOverviewAsync(GetOwnerScope(), ct);
        return response is null ? Forbid() : Ok(response);
    }

    [HttpGet("properties/page")]
    public Task<OwnerPortalPropertyPageResponse> PropertiesPage(
        [FromQuery] ListQuery query, CancellationToken ct) =>
        _portal.ListPropertiesPageAsync(GetOwnerScope(), query, ct);

    [HttpGet("statements")]
    public Task<OwnerStatementSummaryPageResponse> Statements(
        [FromQuery] int? year, [FromQuery] ListQuery query, CancellationToken ct) =>
        _statements.ListForOwnerPortalPageAsync(
            GetOwnerScope(), year ?? _timeProvider.GetUtcNow().Year, query, ct);

    [HttpGet("statements/{ownerEntityId:int}")]
    [ProducesResponseType(typeof(OwnerStatementReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerStatementReport>> Statement(
        int ownerEntityId, [FromQuery] int? year, CancellationToken ct)
    {
        var report = await _statements.GetForOwnerPortalAsync(
            GetOwnerScope(), ownerEntityId, year ?? _timeProvider.GetUtcNow().Year, ct);
        return report is null ? NotFound() : Ok(report);
    }

    [HttpGet("distributions/page")]
    public Task<OwnerPortalDistributionPageResponse> DistributionsPage(
        [FromQuery] ListQuery query, CancellationToken ct) =>
        _portal.ListDistributionsPageAsync(GetOwnerScope(), query, ct);

    [HttpGet("approvals/page")]
    public Task<OwnerPortalItemPageResponse> ApprovalsPage(
        [FromQuery] ListQuery query, CancellationToken ct) =>
        _portal.ListApprovalsPageAsync(GetOwnerScope(), query, ct);

    [HttpGet("messages/page")]
    public Task<OwnerPortalItemPageResponse> MessagesPage(
        [FromQuery] ListQuery query, CancellationToken ct) =>
        _portal.ListMessagesPageAsync(GetOwnerScope(), query, ct);

    private OwnerPortalReadScope GetOwnerScope()
    {
        var active = GetActiveAccessContext();
        return new OwnerPortalReadScope(
            active.PortfolioId,
            active.UserId,
            active.AccessContextId,
            active.AccessRevision);
    }
}
