using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>Immutable seeded action key used by policies and DB-side authorization queries.</summary>
public sealed class CapabilityDefinition
{
    private CapabilityDefinition() { }

    public int Id { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public CapabilityAuthorizationTargetKind AuthorizationTargetKind { get; private set; }

    public ICollection<RoleProfileCapability> RoleProfiles { get; private set; } =
        new List<RoleProfileCapability>();
}
