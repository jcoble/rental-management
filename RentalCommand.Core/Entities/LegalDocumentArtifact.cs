using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>An immutable identity and content hash for one issued or executed legal document.</summary>
public class LegalDocumentArtifact : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int StoredFileId { get; set; }
    public LegalDocumentArtifactKind ArtifactKind { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public StoredFile? StoredFile { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
}
