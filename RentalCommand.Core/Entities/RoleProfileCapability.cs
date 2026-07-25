namespace RentalCommand.Core.Entities;

/// <summary>Immutable seeded many-to-many link between one job preset and one action key.</summary>
public sealed class RoleProfileCapability
{
    private RoleProfileCapability() { }

    public int RoleProfileId { get; private set; }
    public int CapabilityDefinitionId { get; private set; }

    public RoleProfile? RoleProfile { get; private set; }
    public CapabilityDefinition? CapabilityDefinition { get; private set; }
}
