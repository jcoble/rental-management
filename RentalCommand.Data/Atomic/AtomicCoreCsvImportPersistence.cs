using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Data.Atomic;

/// <summary>PostgreSQL-owned Property and Tenant CSV preview/import batches.</summary>
public sealed class AtomicCoreCsvImportPersistence
    : IAtomicCoreCsvImportPersistence, ICoreCsvImportPreviewQuery
{
    private const string AuthorizationSql = """
        WITH active_assignments AS (
            SELECT DISTINCT assignment."Id", assignment."ScopeKind"
            FROM "AuthSessions" session
            INNER JOIN "WorkspaceAccessContexts" context
              ON context."Id" = session."ActiveAccessContextId"
             AND context."UserId" = session."UserId"
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
            WHERE session."Id" = @authSessionId
              AND session."UserId" = @actorUserId
              AND session."ActiveAccessContextId" = @accessContextId
              AND session."Status" = 'Active'
              AND session."RevokedAtUtc" IS NULL
              AND session."ExpiresAtUtc" > @effectiveAt
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
              AND assignment."ScopeKind" = 'AllProperties'
              AND capability."AuthorizationTargetKind" = 'Property'
              AND capability."Key" = ANY(@capabilities)
        ), authorization AS (
            SELECT EXISTS (SELECT 1 FROM active_assignments) AS "Authorized"
        )
        """;

    private const string PropertyInputSql = """
        , input AS (
            SELECT *
            FROM jsonb_to_recordset(@rows::jsonb) AS row(
                "RowNumber" integer,
                "Name" text,
                "AddressLine1" text,
                "AddressLine2" text,
                "City" text,
                "State" text,
                "PostalCode" text,
                "PropertyType" integer,
                "Errors" text[])
        ), classified AS (
            SELECT input.*,
                   COALESCE(input."Errors", ARRAY[]::text[]) AS "FinalErrors"
            FROM input
        )
        """;

    private const string TenantInputSql = """
        , input AS (
            SELECT *
            FROM jsonb_to_recordset(@rows::jsonb) AS row(
                "RowNumber" integer,
                "FirstName" text,
                "LastName" text,
                "Email" text,
                "Phone" text,
                "Errors" text[])
        ), classified AS (
            SELECT input.*,
                   COALESCE(input."Errors", ARRAY[]::text[]) AS "FinalErrors"
            FROM input
        )
        """;

    private const string PreviewSql = """
        , output AS (
            SELECT classified."RowNumber",
                   cardinality(classified."FinalErrors") = 0 AS "Valid",
                   false AS "IsDuplicate",
                   NULL::integer AS "CreatedId",
                   NULL::integer AS "RelatedId",
                   classified."FinalErrors" AS "Errors"
            FROM classified
        )
        SELECT authorization."Authorized",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber"), '[]'::jsonb)::text AS "ResultsJson",
               '[]'::jsonb::text AS "CreatedRowsJson",
               count(output."RowNumber")::integer AS "TotalRows",
               count(output."RowNumber") FILTER (WHERE output."Valid")::integer AS "ValidRows",
               0::integer AS "CreatedCount",
               0::integer AS "DuplicateRows"
        FROM authorization
        LEFT JOIN output ON TRUE
        GROUP BY authorization."Authorized"
        """;

    private const string PropertyImportSql = """
        , prepared AS (
            SELECT classified.*,
                   nextval(pg_get_serial_sequence('"Properties"', 'Id'))::integer AS "CreatedId"
            FROM classified
            CROSS JOIN authorization
            WHERE authorization."Authorized"
              AND cardinality(classified."FinalErrors") = 0
            ORDER BY classified."RowNumber"
        ), inserted_properties AS (
            INSERT INTO "Properties" (
                "Id", "PortfolioId", "OwnerEntityId", "Name", "PropertyType", "Status",
                "AddressLine1", "AddressLine2", "City", "State", "PostalCode",
                "AccumulatedDepreciation", "CreatedAt", "UpdatedAt")
            SELECT prepared."CreatedId", @portfolioId,
                   (SELECT owner."Id" FROM "OwnerEntities" owner
                    WHERE owner."PortfolioId" = @portfolioId
                      AND owner."IsPrimary" AND owner."DeletedAt" IS NULL
                    ORDER BY owner."Id" LIMIT 1),
                   trim(prepared."Name"), prepared."PropertyType", 0,
                   trim(prepared."AddressLine1"), nullif(trim(prepared."AddressLine2"), ''),
                   trim(prepared."City"), trim(prepared."State"), trim(prepared."PostalCode"),
                   0, @createdAt, @createdAt
            FROM prepared
            RETURNING "Id"
        ), prepared_units AS (
            SELECT prepared."CreatedId" AS "PropertyId",
                   nextval(pg_get_serial_sequence('"Units"', 'Id'))::integer AS "UnitId",
                   left(trim(prepared."Name"), 50) AS "UnitNumber"
            FROM prepared
            WHERE prepared."PropertyType" IN (0, 2, 3)
        ), inserted_units AS (
            INSERT INTO "Units" (
                "Id", "PortfolioId", "PropertyId", "UnitNumber", "Bedrooms", "Bathrooms",
                "MarketRent", "CreatedAt", "UpdatedAt")
            SELECT prepared_units."UnitId", @portfolioId, prepared_units."PropertyId",
                   prepared_units."UnitNumber", 0, 0, 0, @createdAt, @createdAt
            FROM prepared_units
            RETURNING "Id", "PropertyId"
        ), output AS (
            SELECT classified."RowNumber",
                   cardinality(classified."FinalErrors") = 0 AS "Valid",
                   false AS "IsDuplicate",
                   inserted_properties."Id" AS "CreatedId",
                   inserted_units."Id" AS "RelatedId",
                   classified."FinalErrors" AS "Errors"
            FROM classified
            CROSS JOIN authorization
            LEFT JOIN prepared ON prepared."RowNumber" = classified."RowNumber"
            LEFT JOIN inserted_properties ON inserted_properties."Id" = prepared."CreatedId"
            LEFT JOIN inserted_units ON inserted_units."PropertyId" = inserted_properties."Id"
        )
        SELECT authorization."Authorized",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber"), '[]'::jsonb)::text AS "ResultsJson",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber")
                   FILTER (WHERE output."CreatedId" IS NOT NULL), '[]'::jsonb)::text AS "CreatedRowsJson",
               count(output."RowNumber")::integer AS "TotalRows",
               count(output."RowNumber") FILTER (WHERE output."Valid")::integer AS "ValidRows",
               count(output."CreatedId")::integer AS "CreatedCount",
               0::integer AS "DuplicateRows"
        FROM authorization
        LEFT JOIN output ON TRUE
        GROUP BY authorization."Authorized"
        """;

    private const string TenantImportSql = """
        , prepared AS (
            SELECT classified.*,
                   nextval(pg_get_serial_sequence('"Tenants"', 'Id'))::integer AS "CreatedId"
            FROM classified
            CROSS JOIN authorization
            WHERE authorization."Authorized"
              AND cardinality(classified."FinalErrors") = 0
            ORDER BY classified."RowNumber"
        ), inserted AS (
            INSERT INTO "Tenants" (
                "Id", "PortfolioId", "FirstName", "LastName", "Email", "Phone",
                "CreatedAt", "UpdatedAt")
            SELECT prepared."CreatedId", @portfolioId, trim(prepared."FirstName"),
                   trim(prepared."LastName"), nullif(trim(prepared."Email"), ''),
                   nullif(trim(prepared."Phone"), ''), @createdAt, @createdAt
            FROM prepared
            RETURNING "Id"
        ), output AS (
            SELECT classified."RowNumber",
                   cardinality(classified."FinalErrors") = 0 AS "Valid",
                   false AS "IsDuplicate",
                   inserted."Id" AS "CreatedId",
                   NULL::integer AS "RelatedId",
                   classified."FinalErrors" AS "Errors"
            FROM classified
            CROSS JOIN authorization
            LEFT JOIN prepared ON prepared."RowNumber" = classified."RowNumber"
            LEFT JOIN inserted ON inserted."Id" = prepared."CreatedId"
        )
        SELECT authorization."Authorized",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber"), '[]'::jsonb)::text AS "ResultsJson",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber")
                   FILTER (WHERE output."CreatedId" IS NOT NULL), '[]'::jsonb)::text AS "CreatedRowsJson",
               count(output."RowNumber")::integer AS "TotalRows",
               count(output."RowNumber") FILTER (WHERE output."Valid")::integer AS "ValidRows",
               count(output."CreatedId")::integer AS "CreatedCount",
               0::integer AS "DuplicateRows"
        FROM authorization
        LEFT JOIN output ON TRUE
        GROUP BY authorization."Authorized"
        """;

    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicCoreCsvImportPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
        => (_db, _scope) = (db, scope);

    public Task<AtomicCoreCsvImportBatchResult> ImportAsync(
        WorkspaceReadScope scope,
        AtomicCoreCsvImportDomain domain,
        string rowsJson,
        DateTime createdAtUtc,
        CancellationToken ct = default) =>
        ExecuteAsync(scope, domain, rowsJson, createdAtUtc, write: true, ct);

    public async Task<AtomicCoreCsvImportBatchResult> PreviewAsync(
        WorkspaceReadScope scope,
        AtomicCoreCsvImportDomain domain,
        string rowsJson,
        CancellationToken ct = default)
    {
        var now = await _db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(ct);
        return await ExecuteAsync(scope, domain, rowsJson, now, write: false, ct);
    }

    private async Task<AtomicCoreCsvImportBatchResult> ExecuteAsync(
        WorkspaceReadScope scope,
        AtomicCoreCsvImportDomain domain,
        string rowsJson,
        DateTime effectiveAtUtc,
        bool write,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scope.PortfolioId);
        if (string.IsNullOrWhiteSpace(rowsJson)) throw new ArgumentException("CSV rows are required.");

        var inputSql = domain == AtomicCoreCsvImportDomain.Property ? PropertyInputSql : TenantInputSql;
        var suffixSql = write
            ? domain == AtomicCoreCsvImportDomain.Property ? PropertyImportSql : TenantImportSql
            : PreviewSql;
        var capabilities = domain == AtomicCoreCsvImportDomain.Property
            ? new[] { "rentals.manage" }
            : new[] { "rentals.manage", "leasing.onboarding.manage" };
        var parameters = new List<NpgsqlParameter>
        {
            new("rows", NpgsqlDbType.Jsonb) { Value = rowsJson },
            new("portfolioId", NpgsqlDbType.Integer) { Value = scope.PortfolioId },
            new("actorUserId", NpgsqlDbType.Integer) { Value = scope.UserId },
            new("authSessionId", NpgsqlDbType.Uuid) { Value = scope.SessionId },
            new("accessContextId", NpgsqlDbType.Integer) { Value = scope.AccessContextId },
            new("accessRevision", NpgsqlDbType.Bigint) { Value = scope.AccessRevision },
            new("effectiveAt", NpgsqlDbType.TimestampTz) { Value = effectiveAtUtc },
            new("capabilities", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = capabilities },
        };
        if (write)
            parameters.Add(new NpgsqlParameter("createdAt", NpgsqlDbType.TimestampTz) { Value = effectiveAtUtc });

        using var primaryPermit = write
            ? _scope.BeginInternalRawDml(
                domain == AtomicCoreCsvImportDomain.Property ? "Properties" : "Tenants",
                AtomicRawDmlOperation.Insert)
            : null;
        using var unitPermit = write && domain == AtomicCoreCsvImportDomain.Property
            ? _scope.BeginInternalRawDml("Units", AtomicRawDmlOperation.Insert)
            : null;
        var result = await _db.Database.SingleTopLevelResultAsync<ImportResultRow>(
            AuthorizationSql + inputSql + suffixSql, parameters.ToArray(), ct);
        return new AtomicCoreCsvImportBatchResult(
            result.Authorized,
            JsonSerializer.Deserialize<AtomicCoreCsvImportRowResult[]>(result.ResultsJson) ?? [],
            JsonSerializer.Deserialize<AtomicCoreCsvImportRowResult[]>(result.CreatedRowsJson) ?? [],
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
