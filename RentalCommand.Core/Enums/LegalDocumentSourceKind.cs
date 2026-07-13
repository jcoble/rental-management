namespace RentalCommand.Core.Enums;

/// <summary>The truthful origin of one immutable legal-document source version.</summary>
public enum LegalDocumentSourceKind
{
    AuthoredTemplateSnapshot,
    BuiltInRenderer,
    ImportedExternalDocument,
}
