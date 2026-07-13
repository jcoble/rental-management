using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Data;

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
    private readonly IAccessEnvelopeQuery _accessEnvelopes;

    public WorkspaceExperienceController(
        RentalCommandDbContext db,
        IAccessEnvelopeQuery accessEnvelopes)
    {
        _db = db;
        _accessEnvelopes = accessEnvelopes;
    }

    [HttpPost("select")]
    public async Task<ActionResult<AccessEnvelope>> Select(
        [FromBody] SelectWorkspaceExperienceRequest request,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Experience))
        {
            return BadRequest(new { error = "A valid workspace experience is required." });
        }

        var active = GetActiveAccessContext();
        var experience = request.Experience.ToString();

        // One PostgreSQL statement both revalidates the current canonical session/revision and
        // proves the requested experience exists in the effective access-envelope projection.
        // Authority changes advance AccessRevision, so a concurrent revocation/scope change makes
        // this update affect zero rows instead of persisting a stale selection.
        var updated = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "WorkspaceAccessContexts" AS context
               SET "LastAuthorizedExperience" = {experience},
                   "UpdatedAtUtc" = clock_timestamp()
             WHERE context."Id" = {active.AccessContextId}
               AND context."UserId" = {active.UserId}
               AND context."PortfolioId" = {active.PortfolioId}
               AND context."AccessRevision" = {active.AccessRevision}
               AND context."Status" = 'Active'
               AND context."SuspendedAtUtc" IS NULL
               AND context."RevokedAtUtc" IS NULL
               AND EXISTS (
                   SELECT 1
                     FROM "AuthSessions" AS session
                    WHERE session."Id" = {active.SessionId}
                      AND session."UserId" = context."UserId"
                      AND session."ActiveAccessContextId" = context."Id"
                      AND session."Status" = 'Active'
                      AND session."RevokedAtUtc" IS NULL
                      AND session."ExpiresAtUtc" > clock_timestamp())
               AND EXISTS (
                   SELECT 1
                     FROM "vw_access_envelopes" AS envelope
                    WHERE envelope."AccessContextId" = context."Id"
                      AND envelope."UserId" = context."UserId"
                      AND envelope."PortfolioId" = context."PortfolioId"
                      AND jsonb_exists(
                          envelope."EnvelopeJson"::jsonb -> 'availableExperiences',
                          {experience}))
            """, ct);

        if (updated != 1)
        {
            return Forbid();
        }

        var envelope = await _accessEnvelopes.GetAsync(active.UserId, active.AccessContextId, ct);
        return envelope is null ? Forbid() : Ok(envelope);
    }
}
