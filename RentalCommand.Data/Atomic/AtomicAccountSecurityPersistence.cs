using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicAccountSecurityPersistence : IAtomicAccountSecurityPersistence
{
    private readonly RentalCommandDbContext _db;

    public AtomicAccountSecurityPersistence(RentalCommandDbContext db) => _db = db;

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
