using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Operations;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/technician")]
public sealed class TechnicianController : AuthenticatedPortfolioControllerBase
{
    private static readonly AtomicJsonResultCodec<RecordTechnicianWorkEntryResult> EntryCodec =
        new("technician-work-entry.v1");
    private static readonly AtomicJsonResultCodec<SendTechnicianAssignmentMessageResult> MessageCodec =
        new("technician-assignment-message.v1");
    private static readonly AtomicJsonResultCodec<MarkTechnicianAssignmentConversationReadResult> ReadCodec =
        new("technician-assignment-conversation-read.v1");
    private readonly ITechnicianExperienceService _service;
    private readonly IAtomicUnitOfWork _atomic;

    public TechnicianController(ITechnicianExperienceService service, IAtomicUnitOfWork atomic)
    {
        _service = service;
        _atomic = atomic;
    }

    [HttpGet("assignments")]
    public async Task<ActionResult<TechnicianAssignmentPage>> List(
        [FromQuery] TechnicianAssignmentQuery query, CancellationToken ct)
    {
        if (!TryScope(out var scope)) return Forbid();
        return Ok(await _service.ListAssignmentsAsync(scope, query, conversationsOnly: false, ct));
    }

    [HttpGet("schedule")]
    public async Task<ActionResult<TechnicianAssignmentPage>> Schedule(
        [FromQuery] TechnicianAssignmentQuery query, CancellationToken ct)
    {
        if (!TryScope(out var scope)) return Forbid();
        return Ok(await _service.ListAssignmentsAsync(scope, query, conversationsOnly: false, ct));
    }

    [HttpGet("inbox")]
    public async Task<ActionResult<TechnicianAssignmentPage>> Inbox(
        [FromQuery] TechnicianAssignmentQuery query, CancellationToken ct)
    {
        if (!TryScope(out var scope)) return Forbid();
        return Ok(await _service.ListAssignmentsAsync(scope, query, conversationsOnly: true, ct));
    }

    [HttpGet("assignments/{workOrderId:int}")]
    public async Task<ActionResult<TechnicianAssignmentDetail>> Detail(int workOrderId, CancellationToken ct)
    {
        if (!TryScope(out var scope)) return Forbid();
        var detail = await _service.GetAssignmentAsync(scope, workOrderId, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost("assignments/{workOrderId:int}/entries")]
    public async Task<IActionResult> RecordEntry(int workOrderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RecordTechnicianWorkEntryRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(out var envelope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var key)) return BadRequest();
        var digest = Digest(key);
        var command = new RecordTechnicianWorkEntryCommand(envelope.PortfolioId, envelope.UserId,
            envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision, workOrderId,
            request.Kind, request.Note, request.Quantity, request.Unit, request.PhotoFileId,
            request.OccurredAt?.UtcDateTime ?? default, digest);
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity(
                "technician-work-entry.record", $"{envelope.PortfolioId}:{workOrderId}:{digest}"),
                command, EntryCodec, ct);
            return Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPost("assignments/{workOrderId:int}/messages")]
    public async Task<IActionResult> SendMessage(int workOrderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] TechnicianConversationMessageRequest request, CancellationToken ct)
    {
        if (!TryEnvelope(out var envelope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var key)) return BadRequest();
        var digest = Digest(key);
        var command = new SendTechnicianAssignmentMessageCommand(envelope.PortfolioId, envelope.UserId,
            envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision, workOrderId,
            request.Body, digest);
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity(
                "technician-assignment-message.send", $"{envelope.PortfolioId}:{workOrderId}:{digest}"),
                command, MessageCodec, ct);
            return Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    [HttpPost("assignments/{workOrderId:int}/conversation/read")]
    public async Task<IActionResult> MarkConversationRead(int workOrderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!TryEnvelope(out var envelope)) return Forbid();
        if (!TryValidateIdempotencyKey(idempotencyKey, out var key)) return BadRequest();
        var digest = Digest(key);
        var command = new MarkTechnicianAssignmentConversationReadCommand(envelope.PortfolioId,
            envelope.UserId, envelope.SessionId, envelope.AccessContextId, envelope.AccessRevision,
            workOrderId, digest);
        try
        {
            var outcome = await _atomic.ExecuteAsync(new AtomicCommandIdentity(
                "technician-assignment-conversation.read",
                $"{envelope.PortfolioId}:{workOrderId}:{digest}"), command, ReadCodec, ct);
            return Ok(new { outcome.Value, replayed = outcome.Disposition == AtomicCommandDisposition.Replayed });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }

    private bool TryScope(out RentalCommand.Core.Authorization.WorkspaceReadScope scope)
    {
        scope = default;
        if (!TryGetActiveAccessContext(out var active)) return false;
        scope = new(active.PortfolioId, active.UserId, active.SessionId, active.AccessContextId, active.AccessRevision);
        return true;
    }

    private bool TryEnvelope(out RentalCommand.Core.Authorization.ActiveAccessContext envelope) =>
        TryGetActiveAccessContext(out envelope);

    private static string Digest(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
