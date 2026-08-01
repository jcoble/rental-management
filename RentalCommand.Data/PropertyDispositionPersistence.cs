using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Leasing;

/// <summary>
/// Owns the destructive, set-based property-disposition cutover. The single PostgreSQL statement
/// is the transaction boundary: either the disposition and every canonical operational closure
/// commit together, or none of them do. Legal Agreement and Addendum rows are intentionally absent.
/// </summary>
public static partial class AtomicLeaseMutationPersistence
{
    public static async Task<AtomicPropertyDispositionMutationResult?> CreatePropertyDispositionAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int propertyId,
        DateTime closedOnDate,
        decimal salePrice,
        decimal sellingCosts,
        string? buyerName,
        string? memo,
        int actorUserId,
        DateTime changedAtUtc,
        DateOnly businessDate,
        CancellationToken ct = default)
    {
        var parameters = new NpgsqlParameter[]
        {
            Integer("portfolioId", portfolioId),
            Integer("propertyId", propertyId),
            Timestamp("closedOnDate", closedOnDate),
            Numeric("salePrice", salePrice),
            Numeric("sellingCosts", sellingCosts),
            NullableText("buyerName", buyerName),
            NullableText("memo", memo),
            Integer("actorUserId", actorUserId),
            Timestamp("changedAtUtc", changedAtUtc),
            Date("businessDate", businessDate),
            Integer("inactivePropertyStatus", (int)PropertyStatus.Inactive),
            Integer("managementHoldType", (int)UnitOperationalPeriodType.ManagementHold),
        };

        var auditScope = RequireAuditScope(db, context);
        using var lease = auditScope.BeginInternalRawDmlBatch(
            new("PropertyDispositions", AtomicRawDmlOperation.Insert),
            new("Properties", AtomicRawDmlOperation.Update),
            new("LeaseManagements", AtomicRawDmlOperation.Update),
            new("LeaseManagementParties", AtomicRawDmlOperation.Update),
            new("TenantUserAccesses", AtomicRawDmlOperation.Update),
            new("WorkspaceAccessContexts", AtomicRawDmlOperation.Update),
            new("TenantAutopayEnrollments", AtomicRawDmlOperation.Update),
            new("TenantAccounts", AtomicRawDmlOperation.Update),
            new("UnitOperationalPeriods", AtomicRawDmlOperation.Insert),
            new("Units", AtomicRawDmlOperation.Update),
            new("CapitalAssets", AtomicRawDmlOperation.Update));
        var row = await db.Database.SingleOrDefaultTopLevelResultAsync<PropertyDispositionMutationRow>(
            CreateSql, parameters, ct);
        return row is null ? null : new AtomicPropertyDispositionMutationResult(
            row.DispositionId,
            DeserializeIds(row.LeaseManagementIdsJson),
            DeserializeIds(row.TenantAccountIdsJson),
            DeserializeIds(row.AutopayEnrollmentIdsJson),
            DeserializeIds(row.PartyIdsJson),
            DeserializeIds(row.RevokedAccessIdsJson),
            DeserializeIds(row.AccessContextIdsJson),
            DeserializeIds(row.ManagementHoldIdsJson),
            DeserializeIds(row.UnitIdsJson),
            DeserializeIds(row.CapitalAssetIdsJson));
    }

    private static NpgsqlParameter Numeric(string name, decimal value) =>
        new(name, NpgsqlDbType.Numeric) { Value = value };

    private const string CreateSql = """
        WITH target_property AS MATERIALIZED (
            SELECT property."Id", property."PortfolioId"
            FROM "Properties" AS property
            WHERE property."Id" = @propertyId
              AND property."PortfolioId" = @portfolioId
              AND property."DeletedAt" IS NULL
            FOR UPDATE
        ),
        inserted_disposition AS (
            INSERT INTO "PropertyDispositions"
                ("PortfolioId", "PropertyId", "ClosedOnDate", "SalePrice", "SellingCosts",
                 "BuyerName", "Memo", "CreatedAt", "UpdatedAt")
            SELECT property."PortfolioId", property."Id", @closedOnDate, @salePrice, @sellingCosts,
                   @buyerName, @memo, @changedAtUtc, @changedAtUtc
            FROM target_property AS property
            WHERE NOT EXISTS (
                SELECT 1
                FROM "PropertyDispositions" AS existing
                WHERE existing."PortfolioId" = property."PortfolioId"
                  AND existing."PropertyId" = property."Id"
                  AND existing."DeletedAt" IS NULL)
            ON CONFLICT DO NOTHING
            RETURNING "Id", "PortfolioId", "PropertyId"
        ),
        updated_property AS (
            UPDATE "Properties" AS property
            SET "Status" = @inactivePropertyStatus,
                "UpdatedAt" = @changedAtUtc
            FROM inserted_disposition AS disposition
            WHERE property."Id" = disposition."PropertyId"
              AND property."PortfolioId" = disposition."PortfolioId"
            RETURNING property."Id"
        ),
        target_relationships AS MATERIALIZED (
            SELECT management."Id"
            FROM "LeaseManagements" AS management
            JOIN inserted_disposition AS disposition
              ON disposition."PortfolioId" = management."PortfolioId"
             AND disposition."PropertyId" = management."PropertyId"
            WHERE (management."PossessionGivenAtUtc" IS NOT NULL
                   AND management."PossessionReturnedAtUtc" IS NULL)
               OR (management."PossessionGivenAtUtc" IS NULL
                   AND management."CanceledAtUtc" IS NULL)
               OR management."AccountClosedAtUtc" IS NULL
               OR EXISTS (
                    SELECT 1
                    FROM "TenantAccounts" AS account
                    WHERE account."PortfolioId" = management."PortfolioId"
                      AND account."LeaseManagementId" = management."Id"
                      AND account."ClosedAtUtc" IS NULL)
        ),
        updated_relationships AS (
            UPDATE "LeaseManagements" AS management
            SET "PossessionReturnedAtUtc" = CASE
                    WHEN management."PossessionGivenAtUtc" IS NOT NULL
                         AND management."PossessionReturnedAtUtc" IS NULL
                      THEN GREATEST(management."PossessionGivenAtUtc", @changedAtUtc)
                    ELSE management."PossessionReturnedAtUtc"
                END,
                "CanceledAtUtc" = CASE
                    WHEN management."PossessionGivenAtUtc" IS NULL
                         AND management."CanceledAtUtc" IS NULL
                      THEN @changedAtUtc
                    ELSE management."CanceledAtUtc"
                END,
                "CancellationReasonCode" = CASE
                    WHEN management."PossessionGivenAtUtc" IS NULL
                         AND management."CanceledAtUtc" IS NULL
                      THEN 'PropertyDisposed'
                    ELSE management."CancellationReasonCode"
                END,
                "CancellationNote" = CASE
                    WHEN management."PossessionGivenAtUtc" IS NULL
                         AND management."CanceledAtUtc" IS NULL
                      THEN 'Planned relationship canceled because the property was disposed.'
                    ELSE management."CancellationNote"
                END,
                "AccountClosedAtUtc" = COALESCE(
                    management."AccountClosedAtUtc",
                    GREATEST(management."PossessionGivenAtUtc", management."PossessionReturnedAtUtc",
                             management."CanceledAtUtc", @changedAtUtc)),
                "UpdatedAtUtc" = @changedAtUtc,
                "RowVersion" = gen_random_uuid()
            FROM target_relationships AS target
            WHERE management."Id" = target."Id"
              AND management."PortfolioId" = @portfolioId
            RETURNING management."Id", management."UnitId"
        ),
        updated_parties AS (
            UPDATE "LeaseManagementParties" AS party
            SET "EffectiveThrough" = GREATEST(party."EffectiveFrom", @businessDate),
                "ChangeReason" = 'Party membership ended because the property was disposed.'
            FROM target_relationships AS target
            WHERE party."PortfolioId" = @portfolioId
              AND party."LeaseManagementId" = target."Id"
              AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" > @businessDate)
            RETURNING party."Id"
        ),
        revoked_accesses AS (
            UPDATE "TenantUserAccesses" AS access
            SET "RevokedAtUtc" = GREATEST(access."GrantedAtUtc", @changedAtUtc),
                "RevokedByUserId" = @actorUserId,
                "Reason" = 'Tenant access revoked because the property was disposed.'
            FROM "LeaseManagementParties" AS party
            JOIN target_relationships AS target ON target."Id" = party."LeaseManagementId"
            WHERE access."PortfolioId" = @portfolioId
              AND access."LeaseManagementPartyId" = party."Id"
              AND access."RevokedAtUtc" IS NULL
            RETURNING access."Id", access."AccessContextId"
        ),
        revised_access_contexts AS (
            UPDATE "WorkspaceAccessContexts" AS context
            SET "AccessRevision" = context."AccessRevision" + 1,
                "UpdatedAtUtc" = @changedAtUtc
            WHERE context."Id" IN (
                SELECT DISTINCT access."AccessContextId"
                FROM revoked_accesses AS access)
              AND context."PortfolioId" = @portfolioId
            RETURNING context."Id"
        ),
        target_accounts AS MATERIALIZED (
            SELECT account."Id",
                   GREATEST(account."OpenedAtUtc", management."PossessionGivenAtUtc",
                            management."PossessionReturnedAtUtc", management."CanceledAtUtc",
                            @changedAtUtc) AS "ClosedAtUtc"
            FROM "TenantAccounts" AS account
            JOIN "LeaseManagements" AS management
              ON management."Id" = account."LeaseManagementId"
             AND management."PortfolioId" = account."PortfolioId"
            JOIN inserted_disposition AS disposition
              ON disposition."PortfolioId" = management."PortfolioId"
             AND disposition."PropertyId" = management."PropertyId"
            WHERE account."ClosedAtUtc" IS NULL
        ),
        canceled_autopay AS (
            UPDATE "TenantAutopayEnrollments" AS enrollment
            SET "CanceledAtUtc" = GREATEST(enrollment."EnrolledAtUtc", @changedAtUtc),
                "CancelReason" = 'Autopay canceled because the property was disposed.'
            FROM target_accounts AS account
            WHERE enrollment."PortfolioId" = @portfolioId
              AND enrollment."TenantAccountId" = account."Id"
              AND enrollment."CanceledAtUtc" IS NULL
            RETURNING enrollment."Id"
        ),
        closed_accounts AS (
            UPDATE "TenantAccounts" AS account
            SET "ClosedAtUtc" = target."ClosedAtUtc",
                "CloseReasonCode" = 'PropertyDisposed',
                "CloseNote" = 'Tenant account closed because the property was disposed.'
            FROM target_accounts AS target
            WHERE account."Id" = target."Id"
              AND account."PortfolioId" = @portfolioId
            RETURNING account."Id", account."LeaseManagementId"
        ),
        opened_holds AS (
            INSERT INTO "UnitOperationalPeriods"
                ("PortfolioId", "PropertyId", "UnitId", "Type", "StartedAtUtc",
                 "Reason", "CreatedAtUtc", "CreatedByUserId")
            SELECT unit."PortfolioId", unit."PropertyId", unit."Id", @managementHoldType,
                   @changedAtUtc, 'Property disposed; unit is no longer operational.',
                   @changedAtUtc, @actorUserId
            FROM "Units" AS unit
            JOIN inserted_disposition AS disposition
              ON disposition."PortfolioId" = unit."PortfolioId"
             AND disposition."PropertyId" = unit."PropertyId"
            WHERE unit."DeletedAt" IS NULL
              AND NOT EXISTS (
                SELECT 1
                FROM "UnitOperationalPeriods" AS period
                WHERE period."PortfolioId" = unit."PortfolioId"
                  AND period."PropertyId" = unit."PropertyId"
                  AND period."UnitId" = unit."Id"
                  AND period."Type" = @managementHoldType
                  AND period."EndedAtUtc" IS NULL)
            RETURNING "Id", "UnitId"
        ),
        updated_units AS (
            UPDATE "Units" AS unit
            SET "UpdatedAt" = @changedAtUtc
            FROM inserted_disposition AS disposition
            WHERE unit."PortfolioId" = disposition."PortfolioId"
              AND unit."PropertyId" = disposition."PropertyId"
              AND unit."DeletedAt" IS NULL
            RETURNING unit."Id"
        ),
        disposed_assets AS (
            UPDATE "CapitalAssets" AS asset
            SET "DisposedOnDate" = @closedOnDate,
                "UpdatedAt" = @changedAtUtc
            FROM inserted_disposition AS disposition
            WHERE asset."PortfolioId" = disposition."PortfolioId"
              AND asset."PropertyId" = disposition."PropertyId"
              AND asset."DisposedOnDate" IS NULL
              AND asset."DeletedAt" IS NULL
            RETURNING asset."Id"
        )
        SELECT disposition."Id" AS "DispositionId",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM updated_relationships), '[]')::text AS "LeaseManagementIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM closed_accounts), '[]')::text AS "TenantAccountIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM canceled_autopay), '[]')::text AS "AutopayEnrollmentIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM updated_parties), '[]')::text AS "PartyIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM revoked_accesses), '[]')::text AS "RevokedAccessIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM revised_access_contexts), '[]')::text AS "AccessContextIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM opened_holds), '[]')::text AS "ManagementHoldIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM updated_units), '[]')::text AS "UnitIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM disposed_assets), '[]')::text AS "CapitalAssetIdsJson"
        FROM inserted_disposition AS disposition
        """;
}

internal sealed class PropertyDispositionMutationRow
{
    public int DispositionId { get; set; }
    public string LeaseManagementIdsJson { get; set; } = "[]";
    public string TenantAccountIdsJson { get; set; } = "[]";
    public string AutopayEnrollmentIdsJson { get; set; } = "[]";
    public string PartyIdsJson { get; set; } = "[]";
    public string RevokedAccessIdsJson { get; set; } = "[]";
    public string AccessContextIdsJson { get; set; } = "[]";
    public string ManagementHoldIdsJson { get; set; } = "[]";
    public string UnitIdsJson { get; set; } = "[]";
    public string CapitalAssetIdsJson { get; set; } = "[]";
}
