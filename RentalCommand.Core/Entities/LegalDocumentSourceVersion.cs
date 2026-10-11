using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Immutable, exact source provenance shared by Agreement and Addendum versions. This is the
/// canonical identity of what was rendered or imported; mutable template rows are never legal
/// provenance by themselves.
/// </summary>
public sealed class LegalDocumentSourceVersion : IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public LegalDocumentSourceKind SourceKind { get; set; }
    public string BusinessKey { get; set; } = string.Empty;
    public int? DocumentTemplateId { get; set; }
    public int? DocumentTemplateVersion { get; set; }
    public string? RendererKey { get; set; }
    public int? RendererVersion { get; set; }
    public string SnapshotPayload { get; set; } = string.Empty;
    public int? SourceStoredFileId { get; set; }
    public int? SourceLegalDocumentArtifactId { get; set; }
    public string? SourceContentSha256 { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public DocumentTemplate? DocumentTemplate { get; set; }
    public StoredFile? SourceStoredFile { get; set; }
    public LegalDocumentArtifact? SourceLegalDocumentArtifact { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<LeaseAgreement> Agreements { get; set; } = [];
    public List<LeaseAddendum> Addenda { get; set; } = [];
}
