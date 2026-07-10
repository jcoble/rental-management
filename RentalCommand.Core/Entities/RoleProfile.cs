using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>Immutable seeded job preset. Authorization uses capabilities, never the display name.</summary>
public sealed class RoleProfile
{
    private RoleProfile() { }

    public int Id { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public WorkspaceExperience DefaultExperience { get; private set; }
    public MembershipRoleAssignmentScopeKind DefaultScopeKind { get; private set; }

    public ICollection<RoleProfileCapability> Capabilities { get; private set; } =
        new List<RoleProfileCapability>();
    public ICollection<MembershipRoleAssignment> Assignments { get; private set; } =
        new List<MembershipRoleAssignment>();
}
