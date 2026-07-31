using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Import;
using RentalCommand.Data.Atomic;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Data.Import;

/// <summary>Domain-owned, set-based Unit CSV validation and db.</summary>
public sealed class AtomicUnitImportPersistence : IUnitCsvImportPreviewQuery
{
    private static AtomicAuditScope RequireAuditScope(RentalCommandDbContext db, IAtomicCommandContext context)
    {
        if (context is not AtomicCommandContext owner || !owner.Owns(db))
        {
            throw new AtomicArchitectureException(
                "Atomic helper requires the exact scoped DbContext and active command context.");
        }

        return owner.AuditScope;
    }

    // Both preview and import use this exact resolution, authorization, validation, and
    // duplicate-classification query. The preview suffix contains no DML statement at all.
    private const string ValidationSql = """
        WITH active_assignments AS (
            SELECT DISTINCT assignment."Id", assignment."ScopeKind"
            FROM "AuthSessions" db
            INNER JOIN "WorkspaceAccessContexts" context
              ON context."Id" = db."ActiveAccessContextId"
             AND context."UserId" = db."UserId"
            INNER JOIN "WorkspaceMemberships" membership
              ON membership."AccessContextId" = context."Id"
             AND membership."PortfolioId" = context."PortfolioId"
            INNER JOIN "MembershipRoleAssignments" assignment
              ON assignment."WorkspaceMembershipId" = membership."Id"
             AND assignment."PortfolioId" = membership."PortfolioId"
            INNER JOIN "RoleProfileCapabilities" grant_row
              ON grant_row."RoleProfileId" = assignment."RoleProfileId"
            INNER JOIN "CapabilityDefinitions" capability
              ON capability."Id" = grant_row."CapabilityDefinitionId"
            WHERE db."Id" = @authSessionId
              AND db."UserId" = @actorUserId
              AND db."ActiveAccessContextId" = @accessContextId
              AND db."Status" = 'Active'
              AND db."RevokedAtUtc" IS NULL
              AND db."ExpiresAtUtc" > @effectiveAt
              AND context."Id" = @accessContextId
              AND context."PortfolioId" = @portfolioId
              AND context."AccessRevision" = @accessRevision
              AND context."Status" = 'Active'
              AND context."SuspendedAtUtc" IS NULL
              AND context."RevokedAtUtc" IS NULL
              AND membership."Status" = 'Active'
              AND membership."SuspendedAtUtc" IS NULL
              AND membership."RevokedAtUtc" IS NULL
              AND membership."EffectiveFromUtc" <= @effectiveAt
              AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > @effectiveAt)
              AND assignment."Status" = 'Active'
              AND assignment."SuspendedAtUtc" IS NULL
              AND assignment."RevokedAtUtc" IS NULL
              AND assignment."EffectiveFromUtc" <= @effectiveAt
              AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > @effectiveAt)
              AND capability."Key" = 'rentals.manage'
              AND capability."AuthorizationTargetKind" = 'Property'
        ), authorized_properties AS (
            SELECT DISTINCT property."Id"
            FROM "Properties" property
            INNER JOIN active_assignments assignment ON TRUE
            LEFT JOIN "MembershipRoleAssignmentProperties" selected
              ON selected."MembershipRoleAssignmentId" = assignment."Id"
             AND selected."PortfolioId" = @portfolioId
             AND selected."PropertyId" = property."Id"
            WHERE property."PortfolioId" = @portfolioId
              AND property."DeletedAt" IS NULL
              AND (assignment."ScopeKind" = 'AllProperties'
                   OR (assignment."ScopeKind" = 'SelectedProperties'
                       AND selected."PropertyId" IS NOT NULL))
        ), input AS (
            SELECT *
            FROM jsonb_to_recordset(@rows::jsonb) AS row(
                "RowNumber" integer,
                "PropertyId" integer,
                "PropertyName" text,
                "UnitNumber" text,
                "Bedrooms" numeric,
                "Bathrooms" numeric,
                "MarketRent" numeric,
                "Errors" text[])
        ), resolved AS (
            SELECT input.*,
                   property_match."ResolvedPropertyId",
                   property_match."MatchCount",
                   property_match."UnauthorizedMatchCount",
                   row_number() OVER (
                       PARTITION BY property_match."ResolvedPropertyId", lower(trim(input."UnitNumber"))
                       ORDER BY input."RowNumber") AS "NaturalKeyOrdinal",
                   EXISTS (
                       SELECT 1 FROM "Units" existing
                       WHERE existing."PortfolioId" = @portfolioId
                         AND existing."PropertyId" = property_match."ResolvedPropertyId"
                         AND lower(trim(existing."UnitNumber")) = lower(trim(input."UnitNumber"))
                         AND existing."DeletedAt" IS NULL) AS "AlreadyExists"
            FROM input
            LEFT JOIN LATERAL (
                SELECT min(property."Id") AS "ResolvedPropertyId",
                       count(*)::integer AS "MatchCount",
                       count(*) FILTER (WHERE NOT EXISTS (
                           SELECT 1 FROM authorized_properties authorized
                           WHERE authorized."Id" = property."Id"))::integer AS "UnauthorizedMatchCount"
                FROM "Properties" property
                WHERE property."PortfolioId" = @portfolioId
                  AND property."DeletedAt" IS NULL
                  AND ((input."PropertyId" IS NOT NULL AND property."Id" = input."PropertyId")
                       OR (input."PropertyId" IS NULL
                           AND nullif(trim(input."PropertyName"), '') IS NOT NULL
                           AND lower(property."Name") = lower(trim(input."PropertyName"))))
            ) property_match ON TRUE
        ), access_check AS (
            SELECT EXISTS (SELECT 1 FROM active_assignments)
                AND NOT EXISTS (
                    SELECT 1 FROM resolved
                    WHERE resolved."UnauthorizedMatchCount" > 0)
                AS "Authorized"
        ), classified AS (
            SELECT resolved.*,
                   COALESCE(resolved."Errors", ARRAY[]::text[]) || CASE
                       WHEN resolved."MatchCount" = 0 THEN ARRAY['Property was not found in this portfolio.']::text[]
                       WHEN resolved."MatchCount" > 1 THEN ARRAY['Property name matches more than one property; use propertyId.']::text[]
                       WHEN nullif(trim(resolved."UnitNumber"), '') IS NULL THEN ARRAY['Unit number is required.']::text[]
                       ELSE ARRAY[]::text[]
                   END AS "FinalErrors",
                   resolved."AlreadyExists" OR resolved."NaturalKeyOrdinal" > 1 AS "IsDuplicate"
            FROM resolved
        )
        """;

    private const string PreviewSuffixSql = """
        , output AS (
            SELECT classified."RowNumber",
                   cardinality(classified."FinalErrors") = 0 AS "Valid",
                   classified."IsDuplicate",
                   NULL::integer AS "CreatedId",
                   classified."ResolvedPropertyId" AS "PropertyId",
                   trim(classified."UnitNumber") AS "UnitNumber",
                   classified."Bedrooms", classified."Bathrooms", classified."MarketRent",
                   classified."FinalErrors" AS "Errors"
            FROM classified
        )
        SELECT access_check."Authorized",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber"), '[]'::jsonb)::text AS "ResultsJson",
               '[]'::jsonb::text AS "CreatedRowsJson",
               count(output."RowNumber")::integer AS "TotalRows",
               count(output."RowNumber") FILTER (WHERE output."Valid")::integer AS "ValidRows",
               0::integer AS "CreatedCount",
               count(output."RowNumber") FILTER (WHERE output."IsDuplicate")::integer AS "DuplicateRows"
        FROM access_check
        LEFT JOIN output ON TRUE
        GROUP BY access_check."Authorized"
        """;

    private const string ImportSuffixSql = """
        , inserted AS (
            INSERT INTO "Units" (
                "PortfolioId", "PropertyId", "UnitNumber", "Bedrooms", "Bathrooms",
                "MarketRent", "CreatedAt", "UpdatedAt")
            SELECT @portfolioId, classified."ResolvedPropertyId", trim(classified."UnitNumber"),
                   classified."Bedrooms", classified."Bathrooms", classified."MarketRent",
                   @createdAt, @createdAt
            FROM classified
            CROSS JOIN access_check
            WHERE access_check."Authorized"
              AND cardinality(classified."FinalErrors") = 0
              AND NOT classified."IsDuplicate"
            ORDER BY classified."RowNumber"
            ON CONFLICT ("PropertyId", (lower(trim("UnitNumber")))) WHERE "DeletedAt" IS NULL DO NOTHING
            RETURNING "Id", "PropertyId", "UnitNumber"
        ), output AS (
            SELECT classified."RowNumber",
                   cardinality(classified."FinalErrors") = 0 AS "Valid",
                   (cardinality(classified."FinalErrors") = 0 AND classified."IsDuplicate")
                       OR (access_check."Authorized"
                           AND cardinality(classified."FinalErrors") = 0
                           AND inserted."Id" IS NULL) AS "IsDuplicate",
                   inserted."Id" AS "CreatedId",
                   classified."ResolvedPropertyId" AS "PropertyId",
                   trim(classified."UnitNumber") AS "UnitNumber",
                   classified."Bedrooms", classified."Bathrooms", classified."MarketRent",
                   classified."FinalErrors" AS "Errors"
            FROM classified
            CROSS JOIN access_check
            LEFT JOIN inserted
              ON inserted."PropertyId" = classified."ResolvedPropertyId"
             AND lower(trim(inserted."UnitNumber")) = lower(trim(classified."UnitNumber"))
             AND cardinality(classified."FinalErrors") = 0
             AND NOT classified."AlreadyExists"
             AND classified."NaturalKeyOrdinal" = 1
        )
        SELECT access_check."Authorized",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber"), '[]'::jsonb)::text AS "ResultsJson",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber")
                   FILTER (WHERE output."CreatedId" IS NOT NULL), '[]'::jsonb)::text AS "CreatedRowsJson",
               count(output."RowNumber")::integer AS "TotalRows",
               count(output."RowNumber") FILTER (WHERE output."Valid")::integer AS "ValidRows",
               count(output."CreatedId")::integer AS "CreatedCount",
               count(output."RowNumber") FILTER (WHERE output."IsDuplicate")::integer AS "DuplicateRows"
        FROM access_check
        LEFT JOIN output ON TRUE
        GROUP BY access_check."Authorized"
        """;

    private static readonly string PreviewSql = ValidationSql + PreviewSuffixSql;
    private static readonly string ImportSql = ValidationSql + ImportSuffixSql;

    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope? _scope;

    private AtomicUnitImportPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
        => (_db, _scope) = (db, scope);

    public AtomicUnitImportPersistence(RentalCommandDbContext db) => _db = db;

    public static Task<AtomicUnitImportBatchResult> ImportAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        WorkspaceReadScope scope,
        IReadOnlyList<AtomicUnitImportRow> rows,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        var auditScope = RequireAuditScope(db, context);
        return new AtomicUnitImportPersistence(db, auditScope)
            .ImportAsync(scope, rows, createdAtUtc, ct);
    }

    public Task<AtomicUnitImportBatchResult> ImportAsync(
        WorkspaceReadScope scope,
        IReadOnlyList<AtomicUnitImportRow> rows,
        DateTime createdAtUtc,
        CancellationToken ct = default)
        => ExecuteAsync(scope, rows, createdAtUtc, write: true, ct);

    public async Task<AtomicUnitImportBatchResult> PreviewAsync(
        WorkspaceReadScope scope,
        IReadOnlyList<AtomicUnitImportRow> rows,
        CancellationToken ct = default)
    {
        var now = await _db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(ct);
        return await ExecuteAsync(scope, rows, now, write: false, ct);
    }

    private async Task<AtomicUnitImportBatchResult> ExecuteAsync(
        WorkspaceReadScope scope,
        IReadOnlyList<AtomicUnitImportRow> rows,
        DateTime effectiveAtUtc,
        bool write,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scope.PortfolioId);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return new AtomicUnitImportBatchResult(true, [], [], 0, 0, 0, 0);

        var payload = JsonSerializer.Serialize(rows);
        using var mutation = write
            ? _scope!.BeginInternalRawDml("Units", AtomicRawDmlOperation.Insert)
            : null;
        var parameters = new List<NpgsqlParameter>
        {
            new("rows", NpgsqlDbType.Jsonb) { Value = payload },
            new("portfolioId", NpgsqlDbType.Integer) { Value = scope.PortfolioId },
            new("actorUserId", NpgsqlDbType.Integer) { Value = scope.UserId },
            new("authSessionId", NpgsqlDbType.Uuid) { Value = scope.SessionId },
            new("accessContextId", NpgsqlDbType.Integer) { Value = scope.AccessContextId },
            new("accessRevision", NpgsqlDbType.Bigint) { Value = scope.AccessRevision },
            new("effectiveAt", NpgsqlDbType.TimestampTz) { Value = effectiveAtUtc },
        };
        if (write)
            parameters.Add(new NpgsqlParameter("createdAt", NpgsqlDbType.TimestampTz) { Value = effectiveAtUtc });

        var result = await _db.Database.SingleTopLevelResultAsync<ImportResultRow>(
            write ? ImportSql : PreviewSql, parameters.ToArray(), ct);
        return new AtomicUnitImportBatchResult(
            result.Authorized,
            JsonSerializer.Deserialize<AtomicUnitImportRowResult[]>(result.ResultsJson) ?? [],
            JsonSerializer.Deserialize<AtomicUnitImportRowResult[]>(result.CreatedRowsJson) ?? [],
            result.TotalRows,
            result.ValidRows,
            result.CreatedCount,
            result.DuplicateRows);
    }

    private sealed class ImportResultRow
    {
        public bool Authorized { get; set; }
        public string ResultsJson { get; set; } = "[]";
        public string CreatedRowsJson { get; set; } = "[]";
        public int TotalRows { get; set; }
        public int ValidRows { get; set; }
        public int CreatedCount { get; set; }
        public int DuplicateRows { get; set; }
    }
}
