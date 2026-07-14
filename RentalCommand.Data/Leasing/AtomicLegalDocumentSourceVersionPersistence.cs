using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Leasing;

internal sealed partial class AtomicLeaseMutationPersistence
{
    private const int ConcurrentSourceResolutionAttempts = 2;

    public async Task<AtomicLegalDocumentSourceVersionResult> ResolveAuthoredDocumentSourceVersionAsync(
        int portfolioId,
        int propertyId,
        int leaseManagementId,
        int documentTemplateId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        var parameters = new NpgsqlParameter[]
        {
            Integer("portfolioId", portfolioId),
            Integer("propertyId", propertyId),
            Integer("leaseManagementId", leaseManagementId),
            Integer("documentTemplateId", documentTemplateId),
            Integer("actorUserId", actorUserId),
            Timestamp("createdAtUtc", createdAtUtc),
        };
        using var lease = _auditScope.BeginInternalRawDmlBatch(
            new AtomicRawDmlTarget("LegalDocumentSourceVersions", AtomicRawDmlOperation.Insert));
        return await ResolveLegalDocumentSourceVersionAsync(ResolveAuthoredSql, parameters, ct);
    }

    public async Task<AtomicLegalDocumentSourceVersionResult> ResolveImportedDocumentSourceVersionAsync(
        int portfolioId,
        int sourceStoredFileId,
        int sourceLegalDocumentArtifactId,
        string sourceContentSha256,
        string? sourceLabel,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        var parameters = new NpgsqlParameter[]
        {
            Integer("portfolioId", portfolioId),
            Integer("sourceStoredFileId", sourceStoredFileId),
            Integer("sourceLegalDocumentArtifactId", sourceLegalDocumentArtifactId),
            Text("sourceContentSha256", sourceContentSha256),
            NullableText("sourceLabel", sourceLabel),
            Integer("actorUserId", actorUserId),
            Timestamp("createdAtUtc", createdAtUtc),
        };
        using var lease = _auditScope.BeginInternalRawDmlBatch(
            new AtomicRawDmlTarget("LegalDocumentSourceVersions", AtomicRawDmlOperation.Insert));
        return await ResolveLegalDocumentSourceVersionAsync(ResolveImportedSql, parameters, ct);
    }

    private async Task<AtomicLegalDocumentSourceVersionResult> ResolveLegalDocumentSourceVersionAsync(
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken ct)
    {
        // Under READ COMMITTED, ON CONFLICT can wait for a concurrent insert that is not visible to
        // the statement snapshot used by the fallback SELECT. Re-executing once stays inside the
        // atomic command's existing transaction while obtaining a fresh statement snapshot.
        for (var attempt = 0; attempt < ConcurrentSourceResolutionAttempts; attempt++)
        {
            var row = await _db.Database.SingleTopLevelResultAsync<LegalDocumentSourceVersionRow>(
                sql, CloneSourceResolutionParameters(parameters), ct);
            if (row.DocumentSourceVersionId > 0)
            {
                return new(true, row.DocumentSourceVersionId);
            }
        }

        return new(false, 0);
    }

    private static object[] CloneSourceResolutionParameters(
        IEnumerable<NpgsqlParameter> parameters) =>
        parameters.Select(parameter => new NpgsqlParameter(
            parameter.ParameterName,
            parameter.NpgsqlDbType)
        {
            Value = parameter.Value,
        }).ToArray();

    private sealed class LegalDocumentSourceVersionRow
    {
        public int DocumentSourceVersionId { get; set; }
    }

    private const string ResolveAuthoredSql = """
        WITH candidate AS (
            SELECT template.*,
                   'template:' || template."Id"::text || ':v' || template."Version"::text AS business_key,
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
                   ) AS snapshot_payload
            FROM "DocumentTemplates" AS template
            WHERE template."PortfolioId" = @portfolioId
              AND template."Id" = @documentTemplateId
              AND template."Kind" = 'Lease'
              AND template."Status" = 'Active'
              AND template."ArchivedAtUtc" IS NULL
              AND (template."PropertyId" IS NULL OR template."PropertyId" = COALESCE(
                    NULLIF(@propertyId, 0),
                    (SELECT relationship."PropertyId" FROM "LeaseManagements" AS relationship
                     WHERE relationship."PortfolioId" = @portfolioId
                       AND relationship."Id" = @leaseManagementId)))
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
            RETURNING "Id"
        )
        SELECT COALESCE(
            (SELECT "Id" FROM inserted),
            (SELECT source."Id"
             FROM "LegalDocumentSourceVersions" AS source
             JOIN candidate ON candidate.business_key = source."BusinessKey"
             WHERE source."PortfolioId" = @portfolioId
               AND source."SourceKind" = 'AuthoredTemplateSnapshot'),
            0) AS "DocumentSourceVersionId"
        """;

    private const string ResolveImportedSql = """
        WITH candidate AS (
            SELECT 'imported:' || @sourceStoredFileId::text || ':' || @sourceContentSha256 AS business_key
            FROM "StoredFiles" AS stored_file
            JOIN "LegalDocumentArtifacts" AS artifact
              ON artifact."PortfolioId" = stored_file."PortfolioId"
             AND artifact."StoredFileId" = stored_file."Id"
             AND artifact."Id" = @sourceLegalDocumentArtifactId
             AND artifact."ContentSha256" = @sourceContentSha256
            WHERE stored_file."PortfolioId" = @portfolioId
              AND stored_file."Id" = @sourceStoredFileId
              AND stored_file."DeletedAt" IS NULL
        ), inserted AS (
            INSERT INTO "LegalDocumentSourceVersions"
                ("PublicId", "PortfolioId", "SourceKind", "BusinessKey", "SnapshotPayload",
                 "SourceStoredFileId", "SourceLegalDocumentArtifactId", "SourceContentSha256",
                 "CreatedAtUtc", "CreatedByUserId")
            SELECT gen_random_uuid(), @portfolioId, 'ImportedExternalDocument', candidate.business_key,
                   jsonb_build_object('sourceLabel', @sourceLabel, 'storedFileId', @sourceStoredFileId,
                       'legalDocumentArtifactId', @sourceLegalDocumentArtifactId,
                       'contentSha256', @sourceContentSha256),
                   @sourceStoredFileId, @sourceLegalDocumentArtifactId, @sourceContentSha256,
                   @createdAtUtc, @actorUserId
            FROM candidate
            ON CONFLICT ("PortfolioId", "BusinessKey") DO NOTHING
            RETURNING "Id"
        )
        SELECT COALESCE(
            (SELECT "Id" FROM inserted),
            (SELECT source."Id"
             FROM "LegalDocumentSourceVersions" AS source
             JOIN candidate ON candidate.business_key = source."BusinessKey"
             WHERE source."PortfolioId" = @portfolioId
               AND source."SourceKind" = 'ImportedExternalDocument'),
            0) AS "DocumentSourceVersionId"
        """;
}
