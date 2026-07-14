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
        ResolveAuthoredAsync(
            ResolveActiveOverlaySql,
            [
                Integer("portfolioId", portfolioId),
                Integer("propertyId", propertyId),
                Integer("actorUserId", actorUserId),
                Timestamp("createdAtUtc", createdAtUtc),
            ],
            ct);

    public Task<ResolvedAuthoredDocumentSourceVersion?> ResolveAuthoredTemplateAsync(
        int portfolioId,
        int documentTemplateId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default) =>
        ResolveAuthoredAsync(
            ResolveAuthoredTemplateSql,
            [
                Integer("portfolioId", portfolioId),
                Integer("documentTemplateId", documentTemplateId),
                Integer("actorUserId", actorUserId),
                Timestamp("createdAtUtc", createdAtUtc),
            ],
            ct);

    public async Task<int> ResolveBuiltInAsync(
        int portfolioId,
        string businessKey,
        string rendererKey,
        int rendererVersion,
        string snapshotPayload,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        var parameters = new NpgsqlParameter[]
        {
            Integer("portfolioId", portfolioId),
            Text("businessKey", businessKey),
            Text("rendererKey", rendererKey),
            Integer("rendererVersion", rendererVersion),
            Json("snapshotPayload", snapshotPayload),
            Integer("actorUserId", actorUserId),
            Timestamp("createdAtUtc", createdAtUtc),
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var lease = BeginInsertLeaseIfRequired();
            var row = await _db.Database.SingleTopLevelResultAsync<SourceIdRow>(
                ResolveBuiltInSql, CloneParameters(parameters), ct);
            if (row.DocumentSourceVersionId > 0)
            {
                return row.DocumentSourceVersionId;
            }
        }

        throw new InvalidOperationException(
            $"Built-in legal-document source {rendererKey}/v{rendererVersion} could not be resolved.");
    }

    private async Task<ResolvedAuthoredDocumentSourceVersion?> ResolveAuthoredAsync(
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken ct)
    {
        // A concurrent winner is not visible to the first statement snapshot after ON CONFLICT
        // waits. One bounded replay obtains a new statement snapshot without weakening immutability.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var lease = BeginInsertLeaseIfRequired();
            var row = await _db.Database.SingleOrDefaultTopLevelResultAsync<AuthoredSourceRow>(
                sql, CloneParameters(parameters), ct);
            if (row is not null && row.DocumentSourceVersionId > 0)
            {
                return new(
                    row.DocumentSourceVersionId,
                    row.SnapshotPayload,
                    row.OriginalStoragePath);
            }
        }

        return null;
    }

    private IDisposable? BeginInsertLeaseIfRequired() =>
        _auditScope.IsActive
            ? _auditScope.BeginInternalRawDml(
                "LegalDocumentSourceVersions",
                AtomicRawDmlOperation.Insert)
            : null;

    private static NpgsqlParameter Integer(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };

    private static NpgsqlParameter Text(string name, string value) =>
        new(name, NpgsqlDbType.Text) { Value = value };

    private static NpgsqlParameter Json(string name, string value) =>
        new(name, NpgsqlDbType.Jsonb) { Value = value };

    private static NpgsqlParameter Timestamp(string name, DateTime value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value };

    private static object[] CloneParameters(IEnumerable<NpgsqlParameter> parameters) =>
        parameters.Select(parameter => new NpgsqlParameter(
            parameter.ParameterName,
            parameter.NpgsqlDbType)
        {
            Value = parameter.Value,
        }).ToArray();

    private sealed class SourceIdRow
    {
        public int DocumentSourceVersionId { get; set; }
    }

    private sealed class AuthoredSourceRow
    {
        public int DocumentSourceVersionId { get; set; }
        public string SnapshotPayload { get; set; } = string.Empty;
        public string? OriginalStoragePath { get; set; }
    }

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

    private const string SnapshotPayloadSql = """
        jsonb_build_object(
            'documentTemplateId', template."Id",
            'documentTemplateVersion', template."Version",
            'renderMode', template."RenderMode",
            'originalStoredFileId', template."OriginalStoredFileId",
            'compiledStoredFileId', template."CompiledStoredFileId",
            'draftHtml', template."DraftHtml",
            'fields', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                    'id', field."Id", 'fieldKey', field."FieldKey", 'label', field."Label",
                    'kind', field."Kind", 'signerRole', field."SignerRole",
                    'pageNumber', field."PageNumber", 'xPct', field."XPct", 'yPct', field."YPct",
                    'widthPct', field."WidthPct", 'heightPct', field."HeightPct",
                    'required', field."Required", 'locked', field."Locked",
                    'sortOrder', field."SortOrder", 'defaultText', field."DefaultText")
                    ORDER BY field."SortOrder", field."Id")
                FROM "DocumentTemplateFields" AS field
                WHERE field."DocumentTemplateId" = template."Id"
                  AND field."PortfolioId" = template."PortfolioId"
            ), '[]'::jsonb)
        )
        """;

    private static readonly string ResolveActiveOverlaySql = BuildResolveAuthoredSql("""
        template."PortfolioId" = @portfolioId
        AND template."Kind" = 'Lease'
        AND template."Status" = 'Active'
        AND template."RenderMode" = 'Overlay'
        AND template."ArchivedAtUtc" IS NULL
        AND template."OriginalStoredFileId" IS NOT NULL
        AND template."DefaultForPortfolio"
        AND (template."PropertyId" = @propertyId OR template."PropertyId" IS NULL)
        ORDER BY CASE WHEN template."PropertyId" = @propertyId THEN 1 ELSE 0 END DESC,
                 template."UpdatedAtUtc" DESC,
                 template."Id" DESC
        LIMIT 1
        """);

    private static readonly string ResolveAuthoredTemplateSql = BuildResolveAuthoredSql("""
        template."PortfolioId" = @portfolioId
        AND template."Id" = @documentTemplateId
        AND template."Kind" = 'Lease'
        AND template."Status" = 'Active'
        AND template."ArchivedAtUtc" IS NULL
        LIMIT 1
        """);

    private static string BuildResolveAuthoredSql(string candidatePredicate) => $$"""
        WITH candidate AS MATERIALIZED (
            SELECT template."Id", template."Version", template."RenderMode",
                   'template:' || template."Id"::text || ':v' || template."Version"::text AS business_key,
                   {{SnapshotPayloadSql}} AS snapshot_payload,
                   original_file."FilePath" AS original_storage_path
            FROM "DocumentTemplates" AS template
            LEFT JOIN "StoredFiles" AS original_file
              ON original_file."PortfolioId" = template."PortfolioId"
             AND original_file."Id" = template."OriginalStoredFileId"
             AND original_file."DeletedAt" IS NULL
            WHERE {{candidatePredicate}}
        ), inserted AS (
            INSERT INTO "LegalDocumentSourceVersions"
                ("PublicId", "PortfolioId", "SourceKind", "BusinessKey",
                 "DocumentTemplateId", "DocumentTemplateVersion", "RendererKey", "RendererVersion",
                 "SnapshotPayload", "CreatedAtUtc", "CreatedByUserId")
            SELECT gen_random_uuid(), @portfolioId, 'AuthoredTemplateSnapshot', candidate.business_key,
                   candidate."Id", candidate."Version", lower(candidate."RenderMode"), 1,
                   candidate.snapshot_payload, @createdAtUtc, @actorUserId
            FROM candidate
            ON CONFLICT ("PortfolioId", "BusinessKey") DO NOTHING
            RETURNING "Id", "BusinessKey", "SnapshotPayload"
        ), resolved AS (
            SELECT inserted."Id", inserted."SnapshotPayload", candidate.original_storage_path
            FROM inserted
            JOIN candidate ON candidate.business_key = inserted."BusinessKey"
            UNION ALL
            SELECT source."Id", source."SnapshotPayload", stored_file."FilePath"
            FROM candidate
            JOIN "LegalDocumentSourceVersions" AS source
              ON source."PortfolioId" = @portfolioId
             AND source."BusinessKey" = candidate.business_key
             AND source."SourceKind" = 'AuthoredTemplateSnapshot'
            LEFT JOIN "StoredFiles" AS stored_file
              ON stored_file."PortfolioId" = source."PortfolioId"
             AND stored_file."Id" = NULLIF(source."SnapshotPayload" ->> 'originalStoredFileId', '')::integer
             AND stored_file."DeletedAt" IS NULL
            WHERE NOT EXISTS (SELECT 1 FROM inserted)
        )
        SELECT resolved."Id" AS "DocumentSourceVersionId",
               resolved."SnapshotPayload"::text AS "SnapshotPayload",
               resolved.original_storage_path AS "OriginalStoragePath"
        FROM resolved
        LIMIT 1
        """;

    private const string ResolveBuiltInSql = """
        WITH inserted AS (
            INSERT INTO "LegalDocumentSourceVersions"
                ("PublicId", "PortfolioId", "SourceKind", "BusinessKey",
                 "RendererKey", "RendererVersion", "SnapshotPayload", "CreatedAtUtc", "CreatedByUserId")
            VALUES (gen_random_uuid(), @portfolioId, 'BuiltInRenderer', @businessKey,
                    @rendererKey, @rendererVersion, @snapshotPayload, @createdAtUtc, @actorUserId)
            ON CONFLICT ("PortfolioId", "RendererKey", "RendererVersion")
                WHERE "SourceKind" = 'BuiltInRenderer'
            DO NOTHING
            RETURNING "Id"
        )
        SELECT COALESCE(
            (SELECT "Id" FROM inserted),
            (SELECT source."Id"
             FROM "LegalDocumentSourceVersions" AS source
             WHERE source."PortfolioId" = @portfolioId
               AND source."SourceKind" = 'BuiltInRenderer'
               AND source."RendererKey" = @rendererKey
               AND source."RendererVersion" = @rendererVersion),
            0) AS "DocumentSourceVersionId"
        """;
}
