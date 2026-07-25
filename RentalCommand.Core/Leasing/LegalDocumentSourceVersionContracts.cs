using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Leasing;

/// <summary>
/// Stable identity and immutable provenance for Rental Command's supplied lease renderer.
/// Workspaces may use this source immediately without first creating a mutable template row.
/// </summary>
public static class BuiltInLeaseAgreementSource
{
    public const string BusinessKey = "built-in:lease-agreement:v1";
    public const string RendererKey = "rental-command-built-in-lease-agreement";
    public const int RendererVersion = 1;
    public const string SnapshotPayload =
        "{\"provenance\":\"RentalCommandSupplied\",\"rendererKey\":\"rental-command-built-in-lease-agreement\",\"rendererVersion\":1,\"termsContract\":\"lease-agreement-render-data-v1\"}";
}

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
