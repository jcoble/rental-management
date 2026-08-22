using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Persists the caller's selected work area inside the current canonical workspace context. This is
/// a display preference, not an authority mutation, so it does not advance the access revision.
/// The database-backed access-envelope view is the source of truth for which experiences are
/// currently available; a client cannot select an experience merely by naming it.
/// </summary>
[ApiController]
[Route("api/v1/auth/experience")]
[Produces("application/json")]
public sealed class WorkspaceExperienceController : AuthenticatedPortfolioControllerBase
{
    private readonly RentalCommandDbContext _db;
    private readonly IRequestWriteExecutor _writes;
    private readonly IAccessEnvelopeQuery _accessEnvelopes;
    private readonly TimeProvider _timeProvider;

    public WorkspaceExperienceController(
        RentalCommandDbContext db,
        IRequestWriteExecutor writes,
        IAccessEnvelopeQuery accessEnvelopes,
        TimeProvider timeProvider)
    {
        _db = db;
        _writes = writes;
        _accessEnvelopes = accessEnvelopes;
        _timeProvider = timeProvider;
    }

    [HttpPost("select")]
    public async Task<ActionResult<AccessEnvelope>> Select(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] SelectWorkspaceExperienceRequest request,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Experience))
        {
            return BadRequest(new { error = "A valid workspace experience is required." });
        }

        var normalizedKey = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
        {
            return BadRequest(new
            {
                error = "A request key is required and cannot exceed 200 characters.",
            });
        }

        var active = GetActiveAccessContext();
        var keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))
            .ToLowerInvariant();
        var command = new SelectWorkspaceExperienceCommand(
            active.PortfolioId,
            active.UserId,
            active.SessionId,
            active.AccessContextId,
            active.AccessRevision,
            request.Experience);
        try
        {
            await _writes.ExecuteExactAsync(
                $"{active.PortfolioId}:{active.AccessContextId}:{keyDigest}",
                SelectWorkspaceExperienceHandler.Write(_db, command), ct);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }

        var envelope = await _accessEnvelopes.GetAsync(
            active.SessionId,
            active.UserId,
            active.AccessContextId,
            active.AccessRevision,
            _timeProvider.GetUtcNow().UtcDateTime,
            ct);
        return envelope is null ? Forbid() : Ok(envelope);
    }
}
