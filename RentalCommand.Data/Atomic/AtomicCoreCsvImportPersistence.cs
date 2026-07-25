using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Data.Atomic;

/// <summary>PostgreSQL-owned Property and Tenant CSV preview/import batches.</summary>
internal sealed class AtomicCoreCsvImportPersistence
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
              AND capability."AuthorizationTargetKind" = 'Property'
              AND capability."Key" = ANY(@capabilities)
        ), authorized_properties AS (
            SELECT DISTINCT property."Id"
            FROM "Properties" property
            JOIN active_assignments assignment ON TRUE
            LEFT JOIN "MembershipRoleAssignmentProperties" selected
              ON selected."MembershipRoleAssignmentId" = assignment."Id"
             AND selected."PortfolioId" = @portfolioId
             AND selected."PropertyId" = property."Id"
            WHERE property."PortfolioId" = @portfolioId
              AND property."DeletedAt" IS NULL
              AND (assignment."ScopeKind" = 'AllProperties'
                   OR (assignment."ScopeKind" = 'SelectedProperties'
                       AND selected."PropertyId" IS NOT NULL))
        ), authorization AS (
            SELECT EXISTS (SELECT 1 FROM active_assignments)
               AND (@allowSelectedProperties
                    OR EXISTS (SELECT 1 FROM active_assignments assignment
                               WHERE assignment."ScopeKind" = 'AllProperties'))
               AS "Authorized"
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
                "RentalStructure" text,
                "UnitNumber" text,
                "Errors" text[])
        ), classified AS (
            SELECT input.*,
                   COALESCE(input."Errors", ARRAY[]::text[]) AS "FinalErrors",
                   false AS "IsDuplicate"
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
                   COALESCE(input."Errors", ARRAY[]::text[]) AS "FinalErrors",
                   false AS "IsDuplicate"
            FROM input
        )
        """;

    private const string ExpenseInputSql = """
        , input AS (
            SELECT *
            FROM jsonb_to_recordset(@rows::jsonb) AS row(
                "RowNumber" integer,
                "PropertyName" text,
                "Category" integer,
                "Description" text,
                "Amount" numeric,
                "IncurredAt" timestamptz,
                "PaidAt" timestamptz,
                "Notes" text,
                "Errors" text[])
        ), resolved AS (
            SELECT input.*, property_match."PropertyId", property_match."MatchCount",
                   row_number() OVER (
                       PARTITION BY property_match."PropertyId", input."Amount", input."IncurredAt",
                                    lower(trim(input."Description"))
                       ORDER BY input."RowNumber") AS "NaturalKeyOrdinal",
                   EXISTS (
                       SELECT 1 FROM "Expenses" existing
                       WHERE existing."PortfolioId" = @portfolioId
                         AND existing."PropertyId" = property_match."PropertyId"
                         AND existing."Amount" = input."Amount"
                         AND existing."IncurredAt" = input."IncurredAt"
                         AND lower(trim(existing."Description")) = lower(trim(input."Description"))
                         AND existing."DeletedAt" IS NULL) AS "AlreadyExists"
            FROM input
            LEFT JOIN LATERAL (
                SELECT min(property."Id") AS "PropertyId", count(*)::integer AS "MatchCount"
                FROM "Properties" property
                WHERE property."PortfolioId" = @portfolioId
                  AND property."DeletedAt" IS NULL
                  AND lower(property."Name") = lower(trim(input."PropertyName"))
                  AND property."Id" IN (SELECT "Id" FROM authorized_properties)
            ) property_match ON TRUE
        ), classified AS (
            SELECT resolved.*,
                   COALESCE(resolved."Errors", ARRAY[]::text[]) || CASE
                       WHEN resolved."MatchCount" = 0 THEN ARRAY['Property was not found in this portfolio.']::text[]
                       WHEN resolved."MatchCount" > 1 THEN ARRAY['Property name is ambiguous.']::text[]
                       ELSE ARRAY[]::text[] END AS "FinalErrors",
                   resolved."AlreadyExists" OR resolved."NaturalKeyOrdinal" > 1 AS "IsDuplicate"
            FROM resolved
        )
        """;

    private const string LoanInputSql = """
        , input AS (
            SELECT *
            FROM jsonb_to_recordset(@rows::jsonb) AS row(
                "RowNumber" integer,
                "PropertyName" text,
                "Lender" text,
                "OriginalAmount" numeric,
                "CurrentBalance" numeric,
                "AnnualInterestRatePct" numeric,
                "TermMonths" integer,
                "StartDate" timestamptz,
                "DayOfMonthDue" integer,
                "MonthlyPrincipalInterest" numeric,
                "MonthlyEscrow" numeric,
                "Errors" text[])
        ), resolved AS (
            SELECT input.*, property_match."PropertyId", property_match."MatchCount",
                   row_number() OVER (
                       PARTITION BY property_match."PropertyId", lower(trim(input."Lender")),
                                    input."OriginalAmount", input."StartDate"
                       ORDER BY input."RowNumber") AS "NaturalKeyOrdinal",
                   EXISTS (
                       SELECT 1 FROM "Loans" existing
                       WHERE existing."PortfolioId" = @portfolioId
                         AND existing."PropertyId" = property_match."PropertyId"
                         AND lower(trim(existing."Lender")) = lower(trim(input."Lender"))
                         AND existing."OriginalAmount" = input."OriginalAmount"
                         AND existing."StartDate" = input."StartDate"
                         AND existing."DeletedAt" IS NULL) AS "AlreadyExists"
            FROM input
            LEFT JOIN LATERAL (
                SELECT min(property."Id") AS "PropertyId", count(*)::integer AS "MatchCount"
                FROM "Properties" property
                WHERE property."PortfolioId" = @portfolioId
                  AND property."DeletedAt" IS NULL
                  AND lower(property."Name") = lower(trim(input."PropertyName"))
                  AND property."Id" IN (SELECT "Id" FROM authorized_properties)
            ) property_match ON TRUE
        ), classified AS (
            SELECT resolved.*,
                   COALESCE(resolved."Errors", ARRAY[]::text[]) || CASE
                       WHEN resolved."MatchCount" = 0 THEN ARRAY['Property was not found in this portfolio.']::text[]
                       WHEN resolved."MatchCount" > 1 THEN ARRAY['Property name is ambiguous.']::text[]
                       ELSE ARRAY[]::text[] END AS "FinalErrors",
                   resolved."AlreadyExists" OR resolved."NaturalKeyOrdinal" > 1 AS "IsDuplicate"
            FROM resolved
        )
        """;

    private const string PreviewSql = """
        , output AS (
            SELECT classified."RowNumber",
                   cardinality(classified."FinalErrors") = 0 AS "Valid",
                   COALESCE(classified."IsDuplicate", false) AS "IsDuplicate",
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
               count(output."RowNumber") FILTER (WHERE output."IsDuplicate")::integer AS "DuplicateRows"
        FROM authorization
        LEFT JOIN output ON TRUE
        GROUP BY authorization."Authorized"
        """;

    private const string PropertyImportSql = """
        , prepared AS (
            SELECT classified.*,
                   nextval(pg_get_serial_sequence('"Properties"', 'Id'))::integer AS "CreatedId",
                   nextval(pg_get_serial_sequence('"Units"', 'Id'))::integer AS "CreatedUnitId"
            FROM classified
            CROSS JOIN authorization
            WHERE authorization."Authorized"
              AND cardinality(classified."FinalErrors") = 0
            ORDER BY classified."RowNumber"
        ), inserted_properties AS (
            INSERT INTO "Properties" (
                "Id", "PortfolioId", "Name", "PropertyType", "RentalStructure", "Status",
                "AddressLine1", "AddressLine2", "City", "State", "PostalCode",
                "AccumulatedDepreciation", "CreatedAt", "UpdatedAt")
            SELECT prepared."CreatedId", @portfolioId,
                   trim(prepared."Name"), prepared."PropertyType", prepared."RentalStructure", 0,
                   trim(prepared."AddressLine1"), nullif(trim(prepared."AddressLine2"), ''),
                   trim(prepared."City"), trim(prepared."State"), trim(prepared."PostalCode"),
                   0, @createdAt, @createdAt
            FROM prepared
            RETURNING "Id"
        ), inserted_ownerships AS (
            INSERT INTO "PropertyOwnerships" (
                "PortfolioId", "PropertyId", "OwnerEntityId", "OwnershipSharePercent",
                "EffectiveFromUtc", "StatementRecipientName", "StatementRecipientEmail", "PayeeName")
            SELECT @portfolioId, prepared."CreatedId", owner."Id", 100.0000,
                   @createdAt, owner."Name", owner."Email", owner."Name"
            FROM prepared
            JOIN inserted_properties ON inserted_properties."Id" = prepared."CreatedId"
            JOIN LATERAL (
                SELECT owner_entity."Id", owner_entity."Name", owner_entity."Email"
                FROM "OwnerEntities" owner_entity
                WHERE owner_entity."PortfolioId" = @portfolioId
                  AND owner_entity."IsPrimary"
                  AND owner_entity."DeletedAt" IS NULL
                ORDER BY owner_entity."Id"
                LIMIT 1
            ) owner ON TRUE
            RETURNING "Id"
        ), inserted_units AS (
            INSERT INTO "Units" (
                "Id", "PortfolioId", "PropertyId", "UnitNumber", "Bedrooms", "Bathrooms",
                "MarketRent", "CreatedAt", "UpdatedAt")
            SELECT prepared."CreatedUnitId", @portfolioId, prepared."CreatedId",
                   trim(prepared."UnitNumber"), 0, 0, 0, @createdAt, @createdAt
            FROM prepared
            JOIN inserted_properties ON inserted_properties."Id" = prepared."CreatedId"
            RETURNING "Id"
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
            LEFT JOIN inserted_units ON inserted_units."Id" = prepared."CreatedUnitId"
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

    private const string ExpenseImportSql = """
        , inserted AS (
            INSERT INTO "Expenses" (
                "PortfolioId", "PropertyId", "Category", "Description", "Status", "Amount",
                "IncurredAt", "PaidAt", "BillableToOwner", "Notes", "CreatedAt", "UpdatedAt")
            SELECT @portfolioId, classified."PropertyId", classified."Category",
                   trim(classified."Description"),
                   CASE WHEN classified."PaidAt" IS NULL THEN 0 ELSE 2 END,
                   classified."Amount", classified."IncurredAt", classified."PaidAt", false,
                   nullif(trim(classified."Notes"), ''), @createdAt, @createdAt
            FROM classified CROSS JOIN authorization
            WHERE authorization."Authorized"
              AND cardinality(classified."FinalErrors") = 0
              AND NOT classified."IsDuplicate"
            ORDER BY classified."RowNumber"
            RETURNING "Id", "PropertyId", "Amount", "IncurredAt", "Description"
        ), output AS (
            SELECT classified."RowNumber",
                   cardinality(classified."FinalErrors") = 0 AS "Valid",
                   classified."IsDuplicate" AS "IsDuplicate",
                   inserted."Id" AS "CreatedId",
                   classified."PropertyId" AS "RelatedId",
                   classified."FinalErrors" AS "Errors"
            FROM classified CROSS JOIN authorization
            LEFT JOIN inserted
              ON inserted."PropertyId" = classified."PropertyId"
             AND inserted."Amount" = classified."Amount"
             AND inserted."IncurredAt" = classified."IncurredAt"
             AND lower(trim(inserted."Description")) = lower(trim(classified."Description"))
             AND classified."NaturalKeyOrdinal" = 1
             AND NOT classified."AlreadyExists"
        )
        SELECT authorization."Authorized",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber"), '[]'::jsonb)::text AS "ResultsJson",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber")
                   FILTER (WHERE output."CreatedId" IS NOT NULL), '[]'::jsonb)::text AS "CreatedRowsJson",
               count(output."RowNumber")::integer AS "TotalRows",
               count(output."RowNumber") FILTER (WHERE output."Valid")::integer AS "ValidRows",
               count(output."CreatedId")::integer AS "CreatedCount",
               count(output."RowNumber") FILTER (WHERE output."IsDuplicate")::integer AS "DuplicateRows"
        FROM authorization LEFT JOIN output ON TRUE
        GROUP BY authorization."Authorized"
        """;

    private const string LoanImportSql = """
        , inserted AS (
            INSERT INTO "Loans" (
                "PortfolioId", "PropertyId", "Lender", "OriginalAmount", "CurrentBalance",
                "AnnualInterestRatePct", "TermMonths", "StartDate", "DayOfMonthDue",
                "MonthlyPrincipalInterest", "MonthlyEscrow", "EscrowCoversTaxes",
                "EscrowCoversInsurance", "Status", "CreatedAt", "UpdatedAt", "WorkerClaimAttemptCount")
            SELECT @portfolioId, classified."PropertyId", trim(classified."Lender"),
                   classified."OriginalAmount",
                   COALESCE(classified."CurrentBalance", classified."OriginalAmount"),
                   classified."AnnualInterestRatePct", classified."TermMonths",
                   classified."StartDate", classified."DayOfMonthDue",
                   classified."MonthlyPrincipalInterest", classified."MonthlyEscrow",
                   false, false, 0, @createdAt, @createdAt, 0
            FROM classified CROSS JOIN authorization
            WHERE authorization."Authorized"
              AND cardinality(classified."FinalErrors") = 0
              AND NOT classified."IsDuplicate"
            ORDER BY classified."RowNumber"
            RETURNING "Id", "PropertyId", "Lender", "OriginalAmount", "StartDate"
        ), output AS (
            SELECT classified."RowNumber",
                   cardinality(classified."FinalErrors") = 0 AS "Valid",
                   classified."IsDuplicate" AS "IsDuplicate",
                   inserted."Id" AS "CreatedId",
                   classified."PropertyId" AS "RelatedId",
                   classified."FinalErrors" AS "Errors"
            FROM classified CROSS JOIN authorization
            LEFT JOIN inserted
              ON inserted."PropertyId" = classified."PropertyId"
             AND lower(trim(inserted."Lender")) = lower(trim(classified."Lender"))
             AND inserted."OriginalAmount" = classified."OriginalAmount"
             AND inserted."StartDate" = classified."StartDate"
             AND classified."NaturalKeyOrdinal" = 1
             AND NOT classified."AlreadyExists"
        )
        SELECT authorization."Authorized",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber"), '[]'::jsonb)::text AS "ResultsJson",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber")
                   FILTER (WHERE output."CreatedId" IS NOT NULL), '[]'::jsonb)::text AS "CreatedRowsJson",
               count(output."RowNumber")::integer AS "TotalRows",
               count(output."RowNumber") FILTER (WHERE output."Valid")::integer AS "ValidRows",
               count(output."CreatedId")::integer AS "CreatedCount",
               count(output."RowNumber") FILTER (WHERE output."IsDuplicate")::integer AS "DuplicateRows"
        FROM authorization LEFT JOIN output ON TRUE
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

        var inputSql = domain switch
        {
            AtomicCoreCsvImportDomain.Property => PropertyInputSql,
            AtomicCoreCsvImportDomain.Tenant => TenantInputSql,
            AtomicCoreCsvImportDomain.Expense => ExpenseInputSql,
            AtomicCoreCsvImportDomain.Loan => LoanInputSql,
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };
        var suffixSql = write
            ? domain switch
            {
                AtomicCoreCsvImportDomain.Property => PropertyImportSql,
                AtomicCoreCsvImportDomain.Tenant => TenantImportSql,
                AtomicCoreCsvImportDomain.Expense => ExpenseImportSql,
                AtomicCoreCsvImportDomain.Loan => LoanImportSql,
                _ => throw new ArgumentOutOfRangeException(nameof(domain)),
            }
            : PreviewSql;
        string[] capabilities = domain switch
        {
            AtomicCoreCsvImportDomain.Property => ["rentals.manage"],
            AtomicCoreCsvImportDomain.Tenant => ["rentals.manage", "leasing.onboarding.manage"],
            AtomicCoreCsvImportDomain.Expense or AtomicCoreCsvImportDomain.Loan => ["money.expenses.manage"],
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };
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
            new("allowSelectedProperties", NpgsqlDbType.Boolean)
            {
                Value = domain is AtomicCoreCsvImportDomain.Expense or AtomicCoreCsvImportDomain.Loan,
            },
        };
        if (write)
            parameters.Add(new NpgsqlParameter("createdAt", NpgsqlDbType.TimestampTz) { Value = effectiveAtUtc });

        using var primaryPermit = write
            ? _scope.BeginInternalRawDml(
                domain switch
                {
                    AtomicCoreCsvImportDomain.Property => "Properties",
                    AtomicCoreCsvImportDomain.Tenant => "Tenants",
                    AtomicCoreCsvImportDomain.Expense => "Expenses",
                    AtomicCoreCsvImportDomain.Loan => "Loans",
                    _ => throw new ArgumentOutOfRangeException(nameof(domain)),
                },
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
