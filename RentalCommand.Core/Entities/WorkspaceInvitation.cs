namespace RentalCommand.Core.Entities;

/// <summary>
/// One secure account-activation invitation for a newly created workspace member. Only the token
/// hash is durable; acceptance is transactional with setting the user's first password.
/// </summary>
public sealed class WorkspaceInvitation
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public int WorkspaceMembershipId { get; set; }
    public int InvitedUserId { get; set; }
    public int InvitedByUserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Portfolio? Portfolio { get; set; }
    public WorkspaceMembership? WorkspaceMembership { get; set; }
    public ApplicationUser? InvitedUser { get; set; }
    public ApplicationUser? InvitedByUser { get; set; }
}
