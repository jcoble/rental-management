using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Leasing;

internal sealed class LegalDocumentSourceVersionResolver : ILegalDocumentSourceVersionResolver
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;

    public LegalDocumentSourceVersionResolver(
        RentalCommandDbContext db,
        AtomicAuditScope auditScope)
    {
        _db = db;
        _auditScope = auditScope;
    }

    public async Task<ResolvedExactLegalDocumentSourceVersion?> ResolveExactAsync(
        int portfolioId,
        int documentSourceVersionId,
        CancellationToken ct = default)
    {
        var row = await _db.Database.SingleOrDefaultTopLevelResultAsync<ExactSourceRow>(
            ResolveExactSql,
            [Integer("portfolioId", portfolioId), Integer("documentSourceVersionId", documentSourceVersionId)],
            ct);
        return row is null
            ? null
            : new ResolvedExactLegalDocumentSourceVersion(
                row.DocumentSourceVersionId,
                Enum.Parse<LegalDocumentSourceKind>(row.SourceKind),
                row.BusinessKey,
                row.RendererKey,
                row.RendererVersion,
                row.SnapshotPayload,
                row.SourceStoredFileId,
                row.SourceLegalDocumentArtifactId,
                row.SourceContentSha256,
                row.SourceArtifactContentSha256,
                row.SourceStoragePath);
    }

    public Task<ResolvedAuthoredDocumentSourceVersion?> ResolveActiveOverlayAsync(
        int portfolioId,
        int propertyId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default) =>
        AtomicLeaseMutationPersistence.ResolveActiveOverlayForRendererAsync(
            _db, _auditScope, portfolioId, propertyId, actorUserId, createdAtUtc, ct);

    public Task<ResolvedAuthoredDocumentSourceVersion?> ResolveAuthoredTemplateAsync(
        int portfolioId,
        int documentTemplateId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default) =>
        AtomicLeaseMutationPersistence.ResolveAuthoredTemplateForRendererAsync(
            _db, _auditScope, portfolioId, documentTemplateId, actorUserId, createdAtUtc, ct);

    public Task<int> ResolveBuiltInAsync(
        int portfolioId,
        string businessKey,
        string rendererKey,
        int rendererVersion,
        string snapshotPayload,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default) =>
        AtomicLeaseMutationPersistence.ResolveBuiltInForRendererAsync(
            _db, _auditScope, portfolioId, businessKey, rendererKey, rendererVersion,
            snapshotPayload, actorUserId, createdAtUtc, ct);

    private static NpgsqlParameter Integer(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };

    private sealed class ExactSourceRow
    {
        public int DocumentSourceVersionId { get; set; }
        public string SourceKind { get; set; } = string.Empty;
        public string BusinessKey { get; set; } = string.Empty;
        public string? RendererKey { get; set; }
        public int? RendererVersion { get; set; }
        public string SnapshotPayload { get; set; } = string.Empty;
        public int? SourceStoredFileId { get; set; }
        public int? SourceLegalDocumentArtifactId { get; set; }
        public string? SourceContentSha256 { get; set; }
        public string? SourceArtifactContentSha256 { get; set; }
        public string? SourceStoragePath { get; set; }
    }

    private const string ResolveExactSql = """
        SELECT source."Id" AS "DocumentSourceVersionId",
               source."SourceKind" AS "SourceKind",
               source."BusinessKey" AS "BusinessKey",
               source."RendererKey" AS "RendererKey",
               source."RendererVersion" AS "RendererVersion",
               source."SnapshotPayload"::text AS "SnapshotPayload",
               source."SourceStoredFileId" AS "SourceStoredFileId",
               source."SourceLegalDocumentArtifactId" AS "SourceLegalDocumentArtifactId",
               source."SourceContentSha256" AS "SourceContentSha256",
               source_artifact."ContentSha256" AS "SourceArtifactContentSha256",
               COALESCE(source_file."FilePath", artifact_file."FilePath",
                        authored_file."FilePath", compiled_file."FilePath")
                   AS "SourceStoragePath"
        FROM "LegalDocumentSourceVersions" AS source
        LEFT JOIN "StoredFiles" AS source_file
          ON source_file."PortfolioId" = source."PortfolioId"
         AND source_file."Id" = source."SourceStoredFileId"
         AND source_file."DeletedAt" IS NULL
        LEFT JOIN "LegalDocumentArtifacts" AS source_artifact
          ON source_artifact."PortfolioId" = source."PortfolioId"
         AND source_artifact."Id" = source."SourceLegalDocumentArtifactId"
        LEFT JOIN "StoredFiles" AS artifact_file
          ON artifact_file."PortfolioId" = source_artifact."PortfolioId"
         AND artifact_file."Id" = source_artifact."StoredFileId"
         AND artifact_file."DeletedAt" IS NULL
        LEFT JOIN "StoredFiles" AS authored_file
          ON authored_file."PortfolioId" = source."PortfolioId"
         AND authored_file."Id" = NULLIF(source."SnapshotPayload" ->> 'originalStoredFileId', '')::integer
         AND authored_file."DeletedAt" IS NULL
        LEFT JOIN "StoredFiles" AS compiled_file
          ON compiled_file."PortfolioId" = source."PortfolioId"
         AND compiled_file."Id" = NULLIF(source."SnapshotPayload" ->> 'compiledStoredFileId', '')::integer
         AND compiled_file."DeletedAt" IS NULL
        WHERE source."PortfolioId" = @portfolioId
          AND source."Id" = @documentSourceVersionId
        LIMIT 1
        """;

}
