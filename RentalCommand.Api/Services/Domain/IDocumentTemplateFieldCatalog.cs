using RentalCommand.Api.DTOs;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

/// <summary>Server-owned merge/signing field catalog for reusable document templates.</summary>
public interface IDocumentTemplateFieldCatalog
{
    IReadOnlyList<DocumentTemplateFieldCatalogItemResponse> GetCatalog(DocumentTemplateKind kind);
    DocumentTemplateFieldCatalogItemResponse? Find(DocumentTemplateKind kind, string fieldKey);
}

