using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDocumentTemplateFieldCatalog"/>
public sealed class DocumentTemplateFieldCatalog : IDocumentTemplateFieldCatalog
{
    private static readonly IReadOnlyList<DocumentTemplateFieldCatalogItemResponse> LeaseFields =
    [
        new("landlord.name", "Landlord name", DocumentTemplateFieldKind.Text, DocumentTemplateSignerRole.None, false,
            "Name of the landlord or owner entity."),
        new("tenant.fullName", "Tenant full name", DocumentTemplateFieldKind.Text, DocumentTemplateSignerRole.None, false,
            "Primary tenant or lessee name."),
        new("tenant.email", "Tenant email", DocumentTemplateFieldKind.Text, DocumentTemplateSignerRole.None, false,
            "Primary tenant email address."),
        new("property.name", "Property name", DocumentTemplateFieldKind.Text, DocumentTemplateSignerRole.None, false,
            "Building, community, or property name."),
        new("property.address", "Property address", DocumentTemplateFieldKind.Text, DocumentTemplateSignerRole.None, false,
            "Street, city, state, and ZIP for the leased premises."),
        new("unit.number", "Unit number", DocumentTemplateFieldKind.Text, DocumentTemplateSignerRole.None, false,
            "Apartment, suite, or unit identifier."),
        new("lease.startDate", "Lease start date", DocumentTemplateFieldKind.Date, DocumentTemplateSignerRole.None, false,
            "Lease commencement date."),
        new("lease.endDate", "Lease end date", DocumentTemplateFieldKind.Date, DocumentTemplateSignerRole.None, false,
            "Lease expiration date."),
        new("lease.monthlyRent", "Monthly rent", DocumentTemplateFieldKind.Currency, DocumentTemplateSignerRole.None, false,
            "Monthly rent amount."),
        new("lease.securityDeposit", "Security deposit", DocumentTemplateFieldKind.Currency, DocumentTemplateSignerRole.None, false,
            "Security deposit amount."),
        new("lease.lateFeeAmount", "Late fee amount", DocumentTemplateFieldKind.Currency, DocumentTemplateSignerRole.None, false,
            "Late fee amount configured on the lease."),
        new("lease.rentDueDay", "Rent due day", DocumentTemplateFieldKind.Text, DocumentTemplateSignerRole.None, false,
            "Day of month rent is due."),
        new("lease.signature.tenant", "Tenant signature", DocumentTemplateFieldKind.Signature, DocumentTemplateSignerRole.Tenant, true,
            "Required tenant signature field."),
        new("lease.signature.landlord", "Landlord signature", DocumentTemplateFieldKind.Signature, DocumentTemplateSignerRole.Landlord, false,
            "Landlord signature field."),
        new("lease.initials.tenant", "Tenant initials", DocumentTemplateFieldKind.Initial, DocumentTemplateSignerRole.Tenant, false,
            "Tenant initials field."),
        new("lease.dateSigned.tenant", "Tenant date signed", DocumentTemplateFieldKind.DateSigned, DocumentTemplateSignerRole.Tenant, true,
            "Date captured when the tenant signs."),
        new("lease.dateSigned.landlord", "Landlord date signed", DocumentTemplateFieldKind.DateSigned, DocumentTemplateSignerRole.Landlord, false,
            "Date captured when the landlord signs."),
    ];

    public IReadOnlyList<DocumentTemplateFieldCatalogItemResponse> GetCatalog(DocumentTemplateKind kind) =>
        kind switch
        {
            DocumentTemplateKind.Lease => LeaseFields,
            _ => [],
        };

    public DocumentTemplateFieldCatalogItemResponse? Find(DocumentTemplateKind kind, string fieldKey)
    {
        if (string.IsNullOrWhiteSpace(fieldKey))
        {
            return null;
        }

        return GetCatalog(kind).FirstOrDefault(f =>
            f.FieldKey.Equals(fieldKey.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

