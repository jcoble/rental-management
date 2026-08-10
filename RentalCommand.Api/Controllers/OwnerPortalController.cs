using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Owners;

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
    private readonly IAtomicUnitOfWork _atomic;

    private static readonly AtomicJsonResultCodec<OwnerPortalCommandResult> ApprovalCodec =
        new("owner-portal.approval-decision.v1");
    private static readonly AtomicJsonResultCodec<OwnerPortalCommandResult> ReplyCodec =
        new("owner-portal.message-reply.v1");

    public OwnerPortalController(
        IOwnerPortalService portal,
        IOwnerStatementService statements,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic)
    {
        _portal = portal;
        _statements = statements;
        _timeProvider = timeProvider;
        _atomic = atomic;
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

    [HttpPost("approvals/{notificationId:int}/decision")]
    public Task<IActionResult> DecideApproval(
        int notificationId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] DecideOwnerApprovalRequest request,
        CancellationToken ct) => ExecuteOwnerCommand(
        notificationId,
        idempotencyKey,
        "owner-portal.approval-decision",
        ApprovalCodec,
        (active, digest) => new DecideOwnerApprovalCommand(
            active.PortfolioId,
            active.UserId,
            active.SessionId,
            active.AccessContextId,
            active.AccessRevision,
            notificationId,
            request.Decision,
            request.Note,
            digest),
        ct);

    [HttpPost("messages/{notificationId:int}/replies")]
    public Task<IActionResult> ReplyToMessage(
        int notificationId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ReplyToOwnerMessageRequest request,
        CancellationToken ct) => ExecuteOwnerCommand(
        notificationId,
        idempotencyKey,
        "owner-portal.message-reply",
        ReplyCodec,
        (active, digest) => new ReplyToOwnerMessageCommand(
            active.PortfolioId,
            active.UserId,
            active.SessionId,
            active.AccessContextId,
            active.AccessRevision,
            notificationId,
            request.Body,
            digest),
        ct);

    private async Task<IActionResult> ExecuteOwnerCommand<TCommand>(
        int notificationId,
        string? idempotencyKey,
        string commandType,
        AtomicJsonResultCodec<OwnerPortalCommandResult> codec,
        Func<RentalCommand.Core.Authorization.ActiveAccessContext, string, TCommand> createCommand,
        CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData
    {
        if (!TryValidateIdempotencyKey(idempotencyKey, out var key))
            return BadRequest(new { error = "A request key is required and cannot exceed 128 characters." });
        if (!TryGetActiveAccessContext(out var active)) return Forbid();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
            .ToLowerInvariant();
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    commandType,
                    $"{active.PortfolioId}:{notificationId}:{digest}"),
                createCommand(active, digest),
                codec,
                ct);
            return Ok(new OwnerPortalCommandResponse(
                outcome.Value.SourceNotificationId,
                outcome.Value.OwnerEntityId,
                outcome.Value.StaffNotificationIds,
                outcome.Value.RecordedAtUtc,
                outcome.Disposition == AtomicCommandDisposition.Replayed));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

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
