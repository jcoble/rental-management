using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Authorization root for one login identity in one workspace. Team membership and future
/// relationship-only grants hang beneath this context without granting one another's authority.
/// </summary>
public sealed class WorkspaceAccessContext
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int PortfolioId { get; set; }
    public WorkspaceAccessContextStatus Status { get; set; } = WorkspaceAccessContextStatus.Active;

    /// <summary>
    /// Monotonic authorization version. Every membership, assignment, scope, responsibility,
    /// relationship, or status mutation must increment this value in the same database command.
    /// </summary>
    public long AccessRevision { get; private set; } = 1;

    public WorkspaceExperience? LastAuthorizedExperience { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    public ApplicationUser? User { get; set; }
    public Portfolio? Portfolio { get; set; }
    public WorkspaceMembership? Membership { get; set; }
    public ICollection<AuthSession> ActiveSessions { get; set; } = new List<AuthSession>();

    /// <summary>
    /// Advances the optimistic-concurrency token exactly once. EF includes the prior revision in the
    /// update predicate, so concurrent authority changes cannot silently overwrite one another.
    /// </summary>
    public void AdvanceRevision(long expectedRevision)
    {
        if (AccessRevision != expectedRevision)
        {
            throw new InvalidOperationException("The workspace access revision changed before this update.");
        }

        AccessRevision = checked(AccessRevision + 1);
    }
}
