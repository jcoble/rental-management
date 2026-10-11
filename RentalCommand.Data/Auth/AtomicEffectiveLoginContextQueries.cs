using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;

namespace RentalCommand.Data.Auth;

public static class AtomicEffectiveLoginContextQueries
{
    public static async Task<AtomicEffectiveLoginContext?> ReadAsync(
        RentalCommandDbContext db,
        int userId,
        int selectedAccessContextId,
        DateTime effectiveAtUtc,
        CancellationToken ct = default)
    {
        if (userId <= 0 || selectedAccessContextId <= 0)
        {
            return null;
        }

        return await db.Database.SqlQuery<AtomicEffectiveLoginContext>($$"""
            SELECT option."AccessContextId", option."PortfolioId",
                   option."AccessRevision", option."TotalEffectiveContexts"
            FROM rc_list_effective_access_contexts({{userId}}, {{effectiveAtUtc}}) option
            WHERE option."AccessContextId" = {{selectedAccessContextId}}
            """).SingleOrDefaultAsync(ct);
    }

    public static async Task<AtomicEffectiveLoginContext?> ReadRootAsync(
        RentalCommandDbContext db,
        int userId,
        DateTime effectiveAtUtc,
        CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            return null;
        }

        return await db.Database.SqlQuery<AtomicEffectiveLoginContext>($$"""
            SELECT option."AccessContextId", option."PortfolioId",
                   option."AccessRevision", option."TotalEffectiveContexts"
            FROM rc_list_effective_access_contexts({{userId}}, {{effectiveAtUtc}}) option
            ORDER BY option."AccessContextId"
            LIMIT 1
            """).SingleOrDefaultAsync(ct);
    }

    public static async Task<AtomicEffectiveLoginContext?> EstablishPreAuthenticatedScopeAsync(
        RentalCommandDbContext db,
        Guid authSessionId,
        int userId,
        int selectedAccessContextId,
        DateTime effectiveAtUtc,
        CancellationToken ct = default)
    {
        if (authSessionId == Guid.Empty || userId <= 0 || selectedAccessContextId <= 0)
        {
            return null;
        }

        return await db.Database.SqlQuery<AtomicEffectiveLoginContext>($$"""
            SELECT option."AccessContextId", option."PortfolioId",
                   option."AccessRevision", option."TotalEffectiveContexts",
                   concat(
                       set_config('app.current_portfolio_id', option."PortfolioId"::text, true),
                       set_config('app.auth_session_id', session."Id"::text, true),
                       set_config('app.current_user_id', session."UserId"::text, true),
                       set_config('app.current_access_context_id', option."AccessContextId"::text, true),
                       set_config('app.access_revision', option."AccessRevision"::text, true)) AS "ScopeActivation"
            FROM rc_list_effective_access_contexts({{userId}}, {{effectiveAtUtc}}) option
            JOIN "AuthSessions" session
              ON session."Id" = {{authSessionId}}
             AND session."UserId" = {{userId}}
             AND session."ActiveAccessContextId" = option."AccessContextId"
             AND session."Status" = 'Active'
             AND session."RevokedAtUtc" IS NULL
            AND session."ExpiresAtUtc" > {{effectiveAtUtc}}
            WHERE option."AccessContextId" = {{selectedAccessContextId}}
            """).SingleOrDefaultAsync(ct);
    }
}
