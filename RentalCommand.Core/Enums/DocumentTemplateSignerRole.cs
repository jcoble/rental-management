namespace RentalCommand.Core.Enums;

/// <summary>Signer role responsible for a signing field. Non-signing fields use None.</summary>
public enum DocumentTemplateSignerRole
{
    None = 0,
    Tenant = 1,
    Landlord = 2,
    CoTenant = 3,
}

