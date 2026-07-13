using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Leasing;

/// <summary>
/// Exact immutable template snapshot selected and persisted by one database statement.
/// Rendering must use this payload rather than re-reading mutable template rows.
/// </summary>
public sealed record ResolvedAuthoredDocumentSourceVersion(
    int DocumentSourceVersionId,
    string SnapshotPayload,
    string? OriginalStoragePath);

public sealed record ResolvedExactLegalDocumentSourceVersion(
    int DocumentSourceVersionId,
    LegalDocumentSourceKind SourceKind,
    string BusinessKey,
    string? RendererKey,
    int? RendererVersion,
    string SnapshotPayload,
    int? SourceStoredFileId,
    int? SourceLegalDocumentArtifactId,
    string? SourceContentSha256,
    string? SourceArtifactContentSha256,
    string? SourceStoragePath);

public interface ILegalDocumentSourceVersionResolver
{
    Task<ResolvedExactLegalDocumentSourceVersion?> ResolveExactAsync(
        int portfolioId,
        int documentSourceVersionId,
        CancellationToken ct = default);

    Task<ResolvedAuthoredDocumentSourceVersion?> ResolveActiveOverlayAsync(
        int portfolioId,
        int propertyId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default);

    Task<ResolvedAuthoredDocumentSourceVersion?> ResolveAuthoredTemplateAsync(
        int portfolioId,
        int documentTemplateId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default);

    Task<int> ResolveBuiltInAsync(
        int portfolioId,
        string businessKey,
        string rendererKey,
        int rendererVersion,
        string snapshotPayload,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default);
}
