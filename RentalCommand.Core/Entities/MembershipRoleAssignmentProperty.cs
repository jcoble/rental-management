namespace RentalCommand.Core.Entities;

/// <summary>Selected-property scope row. Composite foreign keys prevent cross-workspace linkage.</summary>
public sealed class MembershipRoleAssignmentProperty
{
    public int MembershipRoleAssignmentId { get; set; }
    public int PropertyId { get; set; }
    public int PortfolioId { get; set; }

    public MembershipRoleAssignment? MembershipRoleAssignment { get; set; }
    public Property? Property { get; set; }
}
