using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Data.Atomic;

public sealed class AtomicPaymentCsvImportPersistence
    : IAtomicPaymentCsvImportPersistence, IPaymentCsvImportPreviewQuery
{
    private const string ValidationSql = """
        WITH active_assignments AS (
            SELECT DISTINCT assignment."Id", assignment."ScopeKind"
            FROM "AuthSessions" session
            JOIN "WorkspaceAccessContexts" context ON context."Id" = session."ActiveAccessContextId"
            JOIN "WorkspaceMemberships" membership
              ON membership."AccessContextId" = context."Id" AND membership."PortfolioId" = context."PortfolioId"
            JOIN "MembershipRoleAssignments" assignment
              ON assignment."WorkspaceMembershipId" = membership."Id" AND assignment."PortfolioId" = membership."PortfolioId"
            JOIN "RoleProfileCapabilities" grant_row ON grant_row."RoleProfileId" = assignment."RoleProfileId"
            JOIN "CapabilityDefinitions" capability ON capability."Id" = grant_row."CapabilityDefinitionId"
            WHERE session."Id" = @authSessionId AND session."UserId" = @actorUserId
              AND session."ActiveAccessContextId" = @accessContextId AND session."Status" = 'Active'
              AND session."RevokedAtUtc" IS NULL AND session."ExpiresAtUtc" > @effectiveAt
              AND context."UserId" = @actorUserId
              AND context."PortfolioId" = @portfolioId AND context."AccessRevision" = @accessRevision
              AND context."Status" = 'Active' AND context."SuspendedAtUtc" IS NULL AND context."RevokedAtUtc" IS NULL
              AND membership."Status" = 'Active' AND membership."SuspendedAtUtc" IS NULL
              AND membership."RevokedAtUtc" IS NULL AND membership."EffectiveFromUtc" <= @effectiveAt
              AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > @effectiveAt)
              AND assignment."Status" = 'Active' AND assignment."SuspendedAtUtc" IS NULL
              AND assignment."RevokedAtUtc" IS NULL AND assignment."EffectiveFromUtc" <= @effectiveAt
              AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > @effectiveAt)
              AND capability."Key" = 'money.payments.manage'
              AND capability."AuthorizationTargetKind" = 'Property'
        ), authorized_properties AS (
            SELECT DISTINCT property."Id"
            FROM "Properties" property JOIN active_assignments assignment ON TRUE
            LEFT JOIN "MembershipRoleAssignmentProperties" selected
              ON selected."MembershipRoleAssignmentId" = assignment."Id"
             AND selected."PortfolioId" = @portfolioId AND selected."PropertyId" = property."Id"
            WHERE property."PortfolioId" = @portfolioId AND property."DeletedAt" IS NULL
              AND (assignment."ScopeKind" = 'AllProperties'
                   OR (assignment."ScopeKind" = 'SelectedProperties' AND selected."PropertyId" IS NOT NULL))
        ), input AS (
            SELECT * FROM jsonb_to_recordset(@rows::jsonb) AS row(
                "RowNumber" integer, "RelationshipNumber" text, "PropertyName" text,
                "UnitNumber" text, "Amount" numeric, "PaidOn" date, "Method" text,
                "ExternalReference" text, "Description" text, "DeliveryKey" text,
                "Errors" text[])
        ), resolved AS (
            SELECT input.*, account_match."TenantAccountId", account_match."PropertyId",
                   account_match."MatchCount", account_match."UnauthorizedMatchCount",
                   account_match."Currency",
                   row_number() OVER (
                     PARTITION BY account_match."TenantAccountId", input."Amount", input."PaidOn",
                                  lower(COALESCE(trim(input."ExternalReference"), ''))
                     ORDER BY input."RowNumber") AS "NaturalKeyOrdinal",
                   row_number() OVER (
                     PARTITION BY lower(COALESCE(trim(input."ExternalReference"), ''))
                     ORDER BY input."RowNumber") AS "ExternalReferenceOrdinal",
                   EXISTS (
                     SELECT 1 FROM "TenantPaymentAttempts" provider_attempt
                     WHERE provider_attempt."Provider" = 'manual'
                       AND nullif(trim(input."ExternalReference"), '') IS NOT NULL
                       AND lower(trim(provider_attempt."ProviderObjectId")) =
                           lower(trim(input."ExternalReference"))) AS "ProviderReferenceExists",
                   EXISTS (
                     SELECT 1 FROM "TenantLedgerEntries" existing
                     LEFT JOIN "TenantPaymentAttempts" attempt ON attempt."Id" = existing."ProviderPaymentAttemptId"
                     WHERE existing."PortfolioId" = @portfolioId
                       AND existing."TenantAccountId" = account_match."TenantAccountId"
                       AND existing."EntryType" = 'PaymentReceipt' AND existing."Amount" = input."Amount"
                       AND existing."EffectiveOn" = input."PaidOn"
                       AND lower(COALESCE(trim(attempt."ProviderObjectId"), '')) =
                           lower(COALESCE(trim(input."ExternalReference"), ''))) AS "AlreadyExists"
            FROM input
            LEFT JOIN LATERAL (
                SELECT min(account."Id") AS "TenantAccountId", min(management."PropertyId") AS "PropertyId",
                       min(account."Currency") AS "Currency", count(*)::integer AS "MatchCount",
                       count(*) FILTER (WHERE authorized."Id" IS NULL)::integer AS "UnauthorizedMatchCount"
                FROM "TenantAccounts" account
                JOIN "LeaseManagements" management
                  ON management."Id" = account."LeaseManagementId" AND management."PortfolioId" = account."PortfolioId"
                JOIN "Properties" property
                  ON property."Id" = management."PropertyId" AND property."PortfolioId" = management."PortfolioId"
                JOIN "Units" unit ON unit."Id" = management."UnitId" AND unit."PortfolioId" = management."PortfolioId"
                LEFT JOIN authorized_properties authorized ON authorized."Id" = management."PropertyId"
                LEFT JOIN "vw_unit_occupancy" occupancy
                  ON occupancy."PortfolioId" = management."PortfolioId"
                 AND occupancy."UnitId" = management."UnitId"
                 AND occupancy."CurrentLeaseManagementId" = management."Id"
                WHERE account."PortfolioId" = @portfolioId AND account."ClosedAtUtc" IS NULL
                  AND ((nullif(trim(input."RelationshipNumber"), '') IS NOT NULL
                        AND lower(management."RelationshipNumber") = lower(trim(input."RelationshipNumber")))
                    OR (nullif(trim(input."RelationshipNumber"), '') IS NULL
                        AND occupancy."CurrentLeaseManagementId" IS NOT NULL
                        AND lower(property."Name") = lower(trim(input."PropertyName"))
                        AND (nullif(trim(input."UnitNumber"), '') IS NULL
                             OR lower(unit."UnitNumber") = lower(trim(input."UnitNumber")))))
            ) account_match ON TRUE
        ), authorization AS (
            SELECT EXISTS (SELECT 1 FROM active_assignments)
               AND NOT EXISTS (SELECT 1 FROM resolved WHERE resolved."UnauthorizedMatchCount" > 0)
               AS "Authorized"
        ), classified AS (
            SELECT resolved.*,
                   COALESCE(resolved."Errors", ARRAY[]::text[])
                     || CASE
                          WHEN resolved."MatchCount" = 0 THEN ARRAY['Tenant account was not found.']::text[]
                          WHEN resolved."MatchCount" > 1 THEN ARRAY['Tenant account reference is ambiguous.']::text[]
                          ELSE ARRAY[]::text[] END
                     || CASE
                          WHEN resolved."ProviderReferenceExists" AND NOT resolved."AlreadyExists"
                            THEN ARRAY['External reference is already attached to another payment.']::text[]
                          WHEN nullif(trim(resolved."ExternalReference"), '') IS NOT NULL
                               AND resolved."ExternalReferenceOrdinal" > 1
                               AND resolved."NaturalKeyOrdinal" = 1
                            THEN ARRAY['External reference is used by another payment row in this file.']::text[]
                          ELSE ARRAY[]::text[] END AS "FinalErrors",
                   resolved."AlreadyExists" OR resolved."NaturalKeyOrdinal" > 1 AS "IsDuplicate"
            FROM resolved
        )
        """;

    private const string PreviewSql = """
        , output AS (
          SELECT "RowNumber", cardinality("FinalErrors") = 0 AS "Valid", "IsDuplicate",
                 NULL::bigint AS "CreatedId", "TenantAccountId", "FinalErrors" AS "Errors"
          FROM classified)
        SELECT authorization."Authorized",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber"), '[]'::jsonb)::text AS "ResultsJson",
               '[]'::jsonb::text AS "CreatedRowsJson", count(output."RowNumber")::integer AS "TotalRows",
               count(output."RowNumber") FILTER (WHERE output."Valid")::integer AS "ValidRows",
               0::integer AS "CreatedCount",
               count(output."RowNumber") FILTER (WHERE output."IsDuplicate")::integer AS "DuplicateRows"
        FROM authorization LEFT JOIN output ON TRUE GROUP BY authorization."Authorized"
        """;

    private const string ImportSql = """
        , prepared AS (
          SELECT classified.*,
                 nextval(pg_get_serial_sequence('"TenantPaymentAttempts"', 'Id'))::bigint AS "AttemptId",
                 nextval(pg_get_serial_sequence('"TenantLedgerEntries"', 'Id'))::bigint AS "LedgerId",
                 'csv-receipt:' || md5(
                   "TenantAccountId"::text || '|' || "Amount"::text || '|' || "PaidOn"::text || '|'
                   || lower(COALESCE(trim("ExternalReference"), ''))) AS "BusinessKey"
          FROM classified CROSS JOIN authorization
          WHERE authorization."Authorized" AND cardinality(classified."FinalErrors") = 0
            AND NOT classified."IsDuplicate" ORDER BY classified."RowNumber"
        ), attempts AS (
          INSERT INTO "TenantPaymentAttempts" (
            "Id", "PortfolioId", "TenantAccountId", "Provider", "ProviderObjectId", "IdempotencyKey",
            "AttemptType", "State", "Amount", "Currency", "PaymentMethodSummary",
            "PreparedAtUtc", "SubmittedAtUtc", "SettledAtUtc", "UpdatedAtUtc", "AttemptCount", "CreatedByUserId")
          SELECT "AttemptId", @portfolioId, "TenantAccountId", 'manual', nullif(trim("ExternalReference"), ''),
                 "DeliveryKey", 'Charge', 'Succeeded', "Amount", "Currency", trim("Method"),
                 @createdAt, @createdAt, @createdAt, @createdAt, 1, @actorUserId
          FROM prepared RETURNING "Id")
        , ledger AS (
          INSERT INTO "TenantLedgerEntries" (
            "Id", "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount", "Currency",
            "EffectiveOn", "PostedAtUtc", "Description", "BusinessKey", "ProviderPaymentAttemptId", "CreatedByUserId")
          SELECT "LedgerId", @portfolioId, "TenantAccountId", 'PaymentReceipt', 'Credit', "Amount", "Currency",
                 "PaidOn", @createdAt, trim("Description"), "BusinessKey", "AttemptId", @actorUserId
          FROM prepared
          JOIN attempts ON attempts."Id" = prepared."AttemptId"
          RETURNING "Id", "TenantAccountId")
        , output AS (
          SELECT classified."RowNumber", cardinality(classified."FinalErrors") = 0 AS "Valid",
                 classified."IsDuplicate", ledger."Id" AS "CreatedId", classified."TenantAccountId",
                 classified."FinalErrors" AS "Errors"
          FROM classified CROSS JOIN authorization
          LEFT JOIN prepared ON prepared."RowNumber" = classified."RowNumber"
          LEFT JOIN ledger ON ledger."Id" = prepared."LedgerId")
        SELECT authorization."Authorized",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber"), '[]'::jsonb)::text AS "ResultsJson",
               COALESCE(jsonb_agg(to_jsonb(output) ORDER BY output."RowNumber")
                 FILTER (WHERE output."CreatedId" IS NOT NULL), '[]'::jsonb)::text AS "CreatedRowsJson",
               count(output."RowNumber")::integer AS "TotalRows",
               count(output."RowNumber") FILTER (WHERE output."Valid")::integer AS "ValidRows",
               count(output."CreatedId")::integer AS "CreatedCount",
               count(output."RowNumber") FILTER (WHERE output."IsDuplicate")::integer AS "DuplicateRows"
        FROM authorization LEFT JOIN output ON TRUE GROUP BY authorization."Authorized"
        """;

    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;
    public AtomicPaymentCsvImportPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
        => (_db, _scope) = (db, scope);

    public Task<AtomicPaymentCsvImportBatchResult> ImportAsync(
        WorkspaceReadScope scope, string rowsJson, DateTime createdAtUtc, CancellationToken ct = default) =>
        ExecuteAsync(scope, rowsJson, createdAtUtc, true, ct);

    public async Task<AtomicPaymentCsvImportBatchResult> PreviewAsync(
        WorkspaceReadScope scope, string rowsJson, CancellationToken ct = default)
    {
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        return await ExecuteAsync(scope, rowsJson, now, false, ct);
    }

    private async Task<AtomicPaymentCsvImportBatchResult> ExecuteAsync(
        WorkspaceReadScope scope, string rowsJson, DateTime at, bool write, CancellationToken ct)
    {
        var parameters = new List<NpgsqlParameter>
        {
            new("rows", NpgsqlDbType.Jsonb) { Value = rowsJson },
            new("portfolioId", NpgsqlDbType.Integer) { Value = scope.PortfolioId },
            new("actorUserId", NpgsqlDbType.Integer) { Value = scope.UserId },
            new("authSessionId", NpgsqlDbType.Uuid) { Value = scope.SessionId },
            new("accessContextId", NpgsqlDbType.Integer) { Value = scope.AccessContextId },
            new("accessRevision", NpgsqlDbType.Bigint) { Value = scope.AccessRevision },
            new("effectiveAt", NpgsqlDbType.TimestampTz) { Value = at },
        };
        if (write) parameters.Add(new("createdAt", NpgsqlDbType.TimestampTz) { Value = at });
        using var permits = write ? _scope.BeginInternalRawDmlBatch(
            new("TenantPaymentAttempts", AtomicRawDmlOperation.Insert),
            new("TenantLedgerEntries", AtomicRawDmlOperation.Insert)) : null;
        var result = await _db.Database.SingleTopLevelResultAsync<ResultRow>(
            ValidationSql + (write ? ImportSql : PreviewSql), parameters.ToArray(), ct);
        return new(result.Authorized,
            JsonSerializer.Deserialize<AtomicPaymentCsvImportRowResult[]>(result.ResultsJson) ?? [],
            JsonSerializer.Deserialize<AtomicPaymentCsvImportRowResult[]>(result.CreatedRowsJson) ?? [],
            result.TotalRows, result.ValidRows, result.CreatedCount, result.DuplicateRows);
    }

    private sealed class ResultRow
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
