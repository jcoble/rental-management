using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Durable ownership/admission for a blob before external storage I/O. Operation identity and the
/// canonical request fingerprint are deliberately separate: the identity locates a retry, while the
/// fingerprint rejects reuse for changed content or metadata.
/// </summary>
public sealed class PendingFileUpload : IPortfolioScoped
{
    public Guid Id { get; set; }
    public int PortfolioId { get; set; }
    public int ActorScopeId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string OperationKeyHash { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public PendingFileUploadState State { get; set; }
    public int? StoredFileId { get; set; }
    public string? CleanupClaimOwner { get; set; }
    public Guid? CleanupClaimToken { get; set; }
    public DateTime? CleanupClaimExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public StoredFile? StoredFile { get; set; }
}
