using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// Exact database-owned workspace-experience mutation. PostgreSQL revalidates the canonical
/// session/revision and current experience projection in the same statement that updates the
/// display preference.
/// </summary>
internal sealed class AtomicWorkspaceExperiencePersistence : IAtomicWorkspaceExperiencePersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicWorkspaceExperiencePersistence(
        RentalCommandDbContext db,
        AtomicAuditScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<bool> SelectAsync(
        WorkspaceReadScope scope,
        WorkspaceExperience experience,
        CancellationToken ct = default)
    {
        var experienceName = experience.ToString();
        using var lease = _scope.BeginInternalRawDml(
            "WorkspaceAccessContexts", AtomicRawDmlOperation.Update);
        return await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "WorkspaceAccessContexts" AS context
               SET "LastAuthorizedExperience" = {experienceName},
                   "UpdatedAtUtc" = clock_timestamp()
             WHERE context."Id" = {scope.AccessContextId}
               AND context."UserId" = {scope.UserId}
               AND context."PortfolioId" = {scope.PortfolioId}
               AND context."AccessRevision" = {scope.AccessRevision}
               AND context."Status" = 'Active'
               AND context."SuspendedAtUtc" IS NULL
               AND context."RevokedAtUtc" IS NULL
               AND EXISTS (
                   SELECT 1
                     FROM "AuthSessions" AS session
                    WHERE session."Id" = {scope.SessionId}
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
                          {experienceName}))
            """, ct) == 1;
    }
}
