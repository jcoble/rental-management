using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;

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
    private static readonly AtomicJsonResultCodec<SelectWorkspaceExperienceResult> ResultCodec =
        new("workspace-experience-select-result:v1");

    private readonly IAtomicUnitOfWork _atomic;
    private readonly IAccessEnvelopeQuery _accessEnvelopes;
    private readonly TimeProvider _timeProvider;

    public WorkspaceExperienceController(
        IAtomicUnitOfWork atomic,
        IAccessEnvelopeQuery accessEnvelopes,
        TimeProvider timeProvider)
    {
        _atomic = atomic;
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
                error = "A valid Idempotency-Key is required (maximum 200 characters).",
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
            await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "workspace-experience.select",
                    $"{active.PortfolioId}:{active.AccessContextId}:{keyDigest}"),
                command,
                ResultCodec,
                ct);
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
