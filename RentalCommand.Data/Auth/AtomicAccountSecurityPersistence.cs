using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Data.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Auth;

public sealed class AtomicAccountSecurityPersistence
{
    private readonly RentalCommandDbContext _db;

    internal AtomicAccountSecurityPersistence(RentalCommandDbContext db) => _db = db;

    public static Task<AtomicInitialWorkspaceBootstrap> BootstrapInitialWorkspaceAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int userId,
        string portfolioName,
        string managementCompanyName,
        string ownerName,
        string ownerEmail,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        return new AtomicAccountSecurityPersistence(db).BootstrapInitialWorkspaceAsync(
            userId, portfolioName, managementCompanyName, ownerName, ownerEmail, createdAtUtc, ct);
    }

    public static Task<int> DeleteFreshWorkspaceSuppliedNoticeTemplateVersionsAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int userId,
        int portfolioId,
        IReadOnlyList<int> templateVersionIds,
        CancellationToken ct = default)
    {
        return new AtomicAccountSecurityPersistence(db).DeleteFreshWorkspaceSuppliedNoticeTemplateVersionsAsync(
            userId, portfolioId, templateVersionIds, ct);
    }

    public static Task<AtomicWorkspaceInvitationActivation?> ActivateWorkspaceInvitationAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        long invitationId,
        int invitedUserId,
        string tokenHash,
        string passwordHash,
        string newSecurityStamp,
        string newConcurrencyStamp,
        CancellationToken ct = default)
    {
        return new AtomicAccountSecurityPersistence(db).ActivateWorkspaceInvitationAsync(
            invitationId, invitedUserId, tokenHash, passwordHash, newSecurityStamp, newConcurrencyStamp, ct);
    }

    public static Task RevokeActiveSessionsForPasswordResetAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int userId,
        DateTime revokedAtUtc,
        string reason,
        CancellationToken ct = default)
    {
        return new AtomicAccountSecurityPersistence(db).RevokeActiveSessionsForPasswordResetAsync(
            userId, revokedAtUtc, reason, ct);
    }

    public static Task RevokeOtherActiveSessionsForPasswordChangeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int userId,
        Guid preservedAuthSessionId,
        DateTime revokedAtUtc,
        string reason,
        CancellationToken ct = default)
    {
        return new AtomicAccountSecurityPersistence(db).RevokeOtherActiveSessionsForPasswordChangeAsync(
            userId, preservedAuthSessionId, revokedAtUtc, reason, ct);
    }

    public async Task<AtomicInitialWorkspaceBootstrap> BootstrapInitialWorkspaceAsync(
        int userId,
        string portfolioName,
        string managementCompanyName,
        string ownerName,
        string ownerEmail,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        var row = await _db.Database.SqlQuery<InitialWorkspaceBootstrapRow>($"""
                SELECT * FROM rc_bootstrap_initial_workspace(
                    {userId}, {portfolioName}, {managementCompanyName}, {ownerName},
                    {ownerEmail}, {createdAtUtc})
                """)
            .SingleAsync(ct);
        return new AtomicInitialWorkspaceBootstrap(row.PortfolioId, row.AccessContextId);
    }

    public async Task<int> DeleteFreshWorkspaceSuppliedNoticeTemplateVersionsAsync(
        int userId,
        int portfolioId,
        IReadOnlyList<int> templateVersionIds,
        CancellationToken ct = default)
    {
        if (userId <= 0 || portfolioId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(portfolioId));
        }

        if (templateVersionIds.Count != 2 || templateVersionIds.Distinct().Count() != 2)
        {
            throw new ArgumentException(
                "Exactly two distinct fresh-workspace template versions are required.",
                nameof(templateVersionIds));
        }

        var templateIds = templateVersionIds.ToArray();
        return await _db.Database.SqlQuery<int>($"""
                SELECT rc_delete_fresh_workspace_notice_templates(
                    {userId}, {portfolioId}, {templateIds}) AS "Value"
                """)
            .SingleAsync(ct);
    }

    public async Task<AtomicWorkspaceInvitationActivation?> ActivateWorkspaceInvitationAsync(
        long invitationId,
        int invitedUserId,
        string tokenHash,
        string passwordHash,
        string newSecurityStamp,
        string newConcurrencyStamp,
        CancellationToken ct = default)
    {
        if (invitationId <= 0 || invitedUserId <= 0)
        {
            return null;
        }

        var row = await _db.Database.SqlQuery<WorkspaceInvitationActivationRow>($"""
                SELECT * FROM rc_activate_workspace_invitation(
                    {invitationId}, {invitedUserId}, {tokenHash}, {passwordHash},
                    {newSecurityStamp}, {newConcurrencyStamp})
                """)
            .SingleOrDefaultAsync(ct);
        return row is null
            ? null
            : new AtomicWorkspaceInvitationActivation(
                row.PortfolioId,
                row.WorkspaceMembershipId,
                row.AccessContextId,
                row.InvitedUserId,
                row.AcceptedAtUtc);
    }

    public async Task RevokeActiveSessionsForPasswordResetAsync(
        int userId,
        DateTime revokedAtUtc,
        string reason,
        CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await RevokeActiveSessionsAsync(userId, preservedAuthSessionId: null, revokedAtUtc, reason, ct);
    }

    public async Task RevokeOtherActiveSessionsForPasswordChangeAsync(
        int userId,
        Guid preservedAuthSessionId,
        DateTime revokedAtUtc,
        string reason,
        CancellationToken ct = default)
    {
        if (userId <= 0 || preservedAuthSessionId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await RevokeActiveSessionsAsync(userId, preservedAuthSessionId, revokedAtUtc, reason, ct);
    }

    private async Task RevokeActiveSessionsAsync(
        int userId,
        Guid? preservedAuthSessionId,
        DateTime revokedAtUtc,
        string reason,
        CancellationToken ct)
    {
        await _db.AuthSessionRefreshCredentials
            .IgnoreQueryFilters()
            .Where(credential =>
                credential.RevokedAtUtc == null &&
                _db.AuthSessionRefreshTokenFamilies
                    .IgnoreQueryFilters()
                    .Where(family =>
                        family.AuthSession!.UserId == userId &&
                        family.AuthSession.Status == AuthSessionStatus.Active &&
                        family.AuthSession.RevokedAtUtc == null &&
                        (preservedAuthSessionId == null || family.AuthSessionId != preservedAuthSessionId))
                    .Select(family => family.Id)
                    .Contains(credential.RefreshTokenFamilyId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(credential => credential.RevokedAtUtc, revokedAtUtc)
                .SetProperty(credential => credential.RevocationReason, reason), ct);

        await _db.AuthSessionRefreshTokenFamilies
            .IgnoreQueryFilters()
            .Where(family =>
                family.AuthSession!.UserId == userId &&
                family.AuthSession.Status == AuthSessionStatus.Active &&
                family.AuthSession.RevokedAtUtc == null &&
                family.RevokedAtUtc == null &&
                (preservedAuthSessionId == null || family.AuthSessionId != preservedAuthSessionId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(family => family.RevokedAtUtc, revokedAtUtc)
                .SetProperty(family => family.RevocationReason, reason), ct);

        await _db.AuthSessions
            .IgnoreQueryFilters()
            .Where(session =>
                session.UserId == userId &&
                session.Status == AuthSessionStatus.Active &&
                session.RevokedAtUtc == null &&
                (preservedAuthSessionId == null || session.Id != preservedAuthSessionId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.Status, AuthSessionStatus.Revoked)
                .SetProperty(session => session.RevokedAtUtc, revokedAtUtc)
                .SetProperty(session => session.RevocationReason, reason), ct);
    }

    private sealed class InitialWorkspaceBootstrapRow
    {
        public int PortfolioId { get; set; }
        public int AccessContextId { get; set; }
    }

    private sealed class WorkspaceInvitationActivationRow
    {
        public int PortfolioId { get; set; }
        public int WorkspaceMembershipId { get; set; }
        public int AccessContextId { get; set; }
        public int InvitedUserId { get; set; }
        public DateTime AcceptedAtUtc { get; set; }
    }
}
