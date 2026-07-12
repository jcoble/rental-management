using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Leasing;

internal sealed class AtomicLeaseMutationPersistence : IAtomicLeaseMutationPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;

    public AtomicLeaseMutationPersistence(RentalCommandDbContext db, AtomicAuditScope auditScope)
    {
        _db = db;
        _auditScope = auditScope;
    }

    public async Task<AtomicTenantAccessTransitionResult> TransitionTenantAccessAsync(
        int portfolioId,
        int leaseManagementId,
        IReadOnlyList<AtomicTenantAccessTransition> transitions,
        int actorUserId,
        DateTime changedAtUtc,
        string reason,
        CancellationToken ct = default)
    {
        if (transitions.Count == 0)
        {
            return new([], [], []);
        }

        var payload = JsonSerializer.Serialize(transitions.Select(item => new
        {
            source_party_id = item.SourcePartyId,
            replacement_party_id = item.ReplacementPartyId,
            kind = (int)item.Kind,
        }));
        var parameters = new NpgsqlParameter[]
        {
            JsonParameter("transitions", payload),
            Integer("portfolioId", portfolioId),
            Integer("leaseManagementId", leaseManagementId),
            Integer("actorUserId", actorUserId),
            Timestamp("changedAt", changedAtUtc),
            Text("reason", reason.Trim()),
            Integer("retain", (int)AtomicTenantAccessTransitionKind.Retain),
            Integer("revoke", (int)AtomicTenantAccessTransitionKind.Revoke),
            Integer("continue", (int)AtomicTenantAccessTransitionKind.ContinueOnReplacement),
        };

        using var lease = _auditScope.BeginInternalRawDmlBatch(
            new("TenantUserAccesses", AtomicRawDmlOperation.Update),
            new("TenantUserAccesses", AtomicRawDmlOperation.Insert));
        var row = await _db.Database.SqlQueryRaw<AccessTransitionRow>(
                AccessTransitionSql, parameters)
            .SingleAsync(ct);
        if (!row.InputValid)
        {
            throw new InvalidOperationException(
                "Tenant access transition sources or replacements do not belong to the lease relationship.");
        }
        return new(
            DeserializeIds(row.ActiveAccessIdsJson),
            DeserializeIds(row.RevokedAccessIdsJson),
            DeserializeIds(row.CreatedAccessIdsJson));
    }

    public async Task<AtomicReturnPossessionMutationResult> ReturnPossessionAsync(
        int portfolioId,
        int leaseManagementId,
        int unitId,
        DateOnly requiredBusinessDate,
        IReadOnlyList<AtomicReturnPossessionPartyInput> parties,
        IReadOnlyList<AtomicReturnPossessionAccessInput> accesses,
        int actorUserId,
        DateTime changedAtUtc,
        string turnoverReason,
        CancellationToken ct = default)
    {
        var partyPayload = JsonSerializer.Serialize(parties.Select(item => new
        {
            party_id = item.PartyId,
            disposition = (int)item.Disposition,
        }));
        var accessPayload = JsonSerializer.Serialize(accesses.Select(item => new
        {
            access_id = item.AccessId,
            disposition = (int)item.Disposition,
        }));
        var parameters = new NpgsqlParameter[]
        {
            JsonParameter("parties", partyPayload),
            JsonParameter("accesses", accessPayload),
            Integer("portfolioId", portfolioId),
            Integer("leaseManagementId", leaseManagementId),
            Integer("unitId", unitId),
            new("businessDate", NpgsqlDbType.Date) { Value = requiredBusinessDate },
            Integer("actorUserId", actorUserId),
            Timestamp("changedAt", changedAtUtc),
            Text("turnoverReason", turnoverReason.Trim()),
            Integer("endMembership", (int)ReturnPartyDisposition.EndMembership),
            Integer("retainGuarantor", (int)ReturnPartyDisposition.RetainGuarantor),
            Integer("revokeNow", (int)ReturnAccessDisposition.RevokeNow),
            Text("guarantor", "Guarantor"),
            Text("turnover", "Turnover"),
            Integer("returned", (int)ReturnPossessionOutcome.Returned),
            Integer("alreadyReturned", (int)ReturnPossessionOutcome.AlreadyReturned),
            Integer("notGiven", (int)ReturnPossessionOutcome.PossessionNotGiven),
            Integer("invalidParty", (int)ReturnPossessionOutcome.InvalidPartyDisposition),
            Integer("invalidAccess", (int)ReturnPossessionOutcome.InvalidAccessDisposition),
            Integer("turnoverOpen", (int)ReturnPossessionOutcome.TurnoverAlreadyOpen),
        };

        using var lease = _auditScope.BeginInternalRawDmlBatch(
            new("LeaseManagementParties", AtomicRawDmlOperation.Update),
            new("TenantUserAccesses", AtomicRawDmlOperation.Update),
            new("LeaseManagements", AtomicRawDmlOperation.Update),
            new("UnitOperationalPeriods", AtomicRawDmlOperation.Insert));
        var row = await _db.Database.SqlQueryRaw<ReturnPossessionRow>(
                ReturnPossessionSql, parameters)
            .SingleAsync(ct);
        return new(
            (ReturnPossessionOutcome)row.Outcome,
            row.TurnoverPeriodId,
            row.PossessionReturnedAtUtc,
            DeserializeIds(row.EndedPartyIdsJson),
            DeserializeIds(row.RevokedAccessIdsJson));
    }

    private static NpgsqlParameter JsonParameter(string name, string json) =>
        new(name, NpgsqlDbType.Jsonb) { Value = json };

    private static NpgsqlParameter Integer(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };

    private static NpgsqlParameter Timestamp(string name, DateTime value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value };

    private static NpgsqlParameter Text(string name, string value) =>
        new(name, NpgsqlDbType.Text) { Value = value };

    private static int[] DeserializeIds(string json) =>
        JsonSerializer.Deserialize<int[]>(json)
        ?? throw new InvalidOperationException("Lease mutation returned invalid id JSON.");

    private sealed class AccessTransitionRow
    {
        public bool InputValid { get; set; }
        public string ActiveAccessIdsJson { get; set; } = "[]";
        public string RevokedAccessIdsJson { get; set; } = "[]";
        public string CreatedAccessIdsJson { get; set; } = "[]";
    }

    private sealed class ReturnPossessionRow
    {
        public int Outcome { get; set; }
        public int? TurnoverPeriodId { get; set; }
        public DateTime? PossessionReturnedAtUtc { get; set; }
        public string EndedPartyIdsJson { get; set; } = "[]";
        public string RevokedAccessIdsJson { get; set; } = "[]";
    }

    private const string AccessTransitionSql = """
        WITH input AS MATERIALIZED (
            SELECT source_party_id, replacement_party_id, kind
            FROM jsonb_to_recordset(@transitions::jsonb) AS row(
                source_party_id integer,
                replacement_party_id integer,
                kind integer)
        ),
        input_validation AS MATERIALIZED (
            SELECT
                (SELECT count(*) FROM input) = (SELECT count(DISTINCT source_party_id) FROM input)
                AND NOT EXISTS (
                    SELECT 1
                    FROM input
                    LEFT JOIN "LeaseManagementParties" AS source
                      ON source."Id" = input.source_party_id
                     AND source."PortfolioId" = @portfolioId
                     AND source."LeaseManagementId" = @leaseManagementId
                    LEFT JOIN "LeaseManagementParties" AS replacement
                      ON replacement."Id" = input.replacement_party_id
                     AND replacement."PortfolioId" = @portfolioId
                     AND replacement."LeaseManagementId" = @leaseManagementId
                    WHERE input.kind NOT IN (@retain, @revoke, @continue)
                       OR source."Id" IS NULL
                       OR (input.kind = @continue AND replacement."Id" IS NULL)
                       OR (input.kind <> @continue AND input.replacement_party_id IS NOT NULL))
                AS is_valid
        ),
        source_access AS MATERIALIZED (
            SELECT access.*, input.replacement_party_id, input.kind
            FROM input
            INNER JOIN "LeaseManagementParties" AS party
                ON party."Id" = input.source_party_id
               AND party."PortfolioId" = @portfolioId
               AND party."LeaseManagementId" = @leaseManagementId
            INNER JOIN "TenantUserAccesses" AS access
                ON access."LeaseManagementPartyId" = party."Id"
               AND access."PortfolioId" = @portfolioId
               AND access."RevokedAtUtc" IS NULL
            CROSS JOIN input_validation
            WHERE input_validation.is_valid
        ),
        revoked AS (
            UPDATE "TenantUserAccesses" AS access
            SET "RevokedAtUtc" = @changedAt,
                "RevokedByUserId" = @actorUserId,
                "Reason" = @reason
            FROM source_access AS source
            WHERE access."Id" = source."Id"
              AND source.kind <> @retain
            RETURNING access."Id", access."ApplicationUserId", source.replacement_party_id, source.kind
        ),
        continued AS (
            INSERT INTO "TenantUserAccesses"
                ("PublicId", "PortfolioId", "ApplicationUserId", "LeaseManagementPartyId",
                 "GrantedAtUtc", "GrantedByUserId", "Reason")
            SELECT gen_random_uuid(), @portfolioId, revoked."ApplicationUserId",
                   revoked.replacement_party_id, @changedAt, @actorUserId, @reason
            FROM revoked
            INNER JOIN "LeaseManagementParties" AS replacement
                ON replacement."Id" = revoked.replacement_party_id
               AND replacement."PortfolioId" = @portfolioId
               AND replacement."LeaseManagementId" = @leaseManagementId
            WHERE revoked.kind = @continue
            RETURNING "Id"
        )
        SELECT
            (SELECT is_valid FROM input_validation) AS "InputValid",
            COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM source_access), '[]'::jsonb)::text
                AS "ActiveAccessIdsJson",
            COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM revoked), '[]'::jsonb)::text
                AS "RevokedAccessIdsJson",
            COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM continued), '[]'::jsonb)::text
                AS "CreatedAccessIdsJson"
        """;

    private const string ReturnPossessionSql = """
        WITH party_input AS MATERIALIZED (
            SELECT party_id, disposition
            FROM jsonb_to_recordset(@parties::jsonb) AS row(party_id integer, disposition integer)
        ),
        access_input AS MATERIALIZED (
            SELECT access_id, disposition
            FROM jsonb_to_recordset(@accesses::jsonb) AS row(access_id integer, disposition integer)
        ),
        relationship AS MATERIALIZED (
            SELECT relationship.*
            FROM "LeaseManagements" AS relationship
            WHERE relationship."Id" = @leaseManagementId
              AND relationship."PortfolioId" = @portfolioId
              AND relationship."UnitId" = @unitId
            FOR UPDATE
        ),
        current_parties AS MATERIALIZED (
            SELECT party."Id", party."Role"
            FROM "LeaseManagementParties" AS party
            WHERE party."PortfolioId" = @portfolioId
              AND party."LeaseManagementId" = @leaseManagementId
              AND party."EffectiveFrom" <= @businessDate
              AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= @businessDate)
        ),
        current_access AS MATERIALIZED (
            SELECT access."Id"
            FROM "TenantUserAccesses" AS access
            INNER JOIN current_parties AS party ON party."Id" = access."LeaseManagementPartyId"
            WHERE access."PortfolioId" = @portfolioId
              AND access."RevokedAtUtc" IS NULL
        ),
        validation AS MATERIALIZED (
            SELECT
                relationship."PossessionGivenAtUtc" IS NOT NULL AS possession_given,
                relationship."PossessionReturnedAtUtc" IS NOT NULL AS already_returned,
                relationship."PossessionReturnedAtUtc" AS returned_at,
                rc_business_date(@portfolioId) = @businessDate AS business_date_matches,
                EXISTS (
                    SELECT 1 FROM "UnitOperationalPeriods" AS period
                    WHERE period."PortfolioId" = @portfolioId
                      AND period."UnitId" = @unitId
                      AND period."Type" = @turnover
                      AND period."EndedAtUtc" IS NULL) AS turnover_open,
                (SELECT count(*) FROM party_input) = (SELECT count(DISTINCT party_id) FROM party_input)
                  AND NOT EXISTS (
                    SELECT party_id FROM party_input EXCEPT SELECT "Id" FROM current_parties)
                  AND NOT EXISTS (
                    SELECT "Id" FROM current_parties EXCEPT SELECT party_id FROM party_input)
                  AND NOT EXISTS (
                    SELECT 1 FROM party_input AS input
                    INNER JOIN current_parties AS party ON party."Id" = input.party_id
                    WHERE input.disposition NOT IN (@endMembership, @retainGuarantor)
                       OR (input.disposition = @retainGuarantor AND party."Role" <> @guarantor))
                  AS parties_valid,
                (SELECT count(*) FROM access_input) = (SELECT count(DISTINCT access_id) FROM access_input)
                  AND NOT EXISTS (
                    SELECT access_id FROM access_input EXCEPT SELECT "Id" FROM current_access)
                  AND NOT EXISTS (
                    SELECT "Id" FROM current_access EXCEPT SELECT access_id FROM access_input)
                  AND NOT EXISTS (
                    SELECT 1 FROM access_input
                    WHERE disposition NOT IN (@revokeNow, 1))
                  AS accesses_valid
            FROM relationship
        ),
        decision AS MATERIALIZED (
            SELECT *,
                CASE
                    WHEN already_returned THEN @alreadyReturned
                    WHEN NOT possession_given THEN @notGiven
                    WHEN turnover_open THEN @turnoverOpen
                    WHEN NOT business_date_matches OR NOT parties_valid THEN @invalidParty
                    WHEN NOT accesses_valid THEN @invalidAccess
                    ELSE @returned
                END AS outcome
            FROM validation
        ),
        ended_parties AS (
            UPDATE "LeaseManagementParties" AS party
            SET "EffectiveThrough" = @businessDate,
                "ChangeReason" = 'Party membership ended when possession returned.'
            FROM party_input AS input, decision
            WHERE decision.outcome = @returned
              AND input.disposition = @endMembership
              AND party."Id" = input.party_id
              AND party."PortfolioId" = @portfolioId
              AND party."LeaseManagementId" = @leaseManagementId
            RETURNING party."Id"
        ),
        revoked_access AS (
            UPDATE "TenantUserAccesses" AS access
            SET "RevokedAtUtc" = @changedAt,
                "RevokedByUserId" = @actorUserId,
                "Reason" = 'Tenant access revoked when possession returned.'
            FROM access_input AS input, decision
            WHERE decision.outcome = @returned
              AND input.disposition = @revokeNow
              AND access."Id" = input.access_id
              AND access."PortfolioId" = @portfolioId
              AND access."RevokedAtUtc" IS NULL
            RETURNING access."Id"
        ),
        returned_relationship AS (
            UPDATE "LeaseManagements" AS relationship
            SET "PossessionReturnedAtUtc" = @changedAt,
                "UpdatedAtUtc" = @changedAt,
                "RowVersion" = gen_random_uuid()
            FROM decision
            WHERE decision.outcome = @returned
              AND relationship."Id" = @leaseManagementId
              AND relationship."PortfolioId" = @portfolioId
              AND relationship."UnitId" = @unitId
              AND relationship."PossessionReturnedAtUtc" IS NULL
            RETURNING relationship."PropertyId", relationship."PossessionReturnedAtUtc"
        ),
        turnover AS (
            INSERT INTO "UnitOperationalPeriods"
                ("PortfolioId", "PropertyId", "UnitId", "Type", "StartedAtUtc",
                 "SourceLeaseManagementId", "Reason", "CreatedAtUtc", "CreatedByUserId")
            SELECT @portfolioId, returned_relationship."PropertyId", @unitId, @turnover,
                   @changedAt, @leaseManagementId, @turnoverReason, @changedAt, @actorUserId
            FROM returned_relationship
            RETURNING "Id"
        )
        SELECT
            COALESCE((SELECT outcome FROM decision), @invalidParty) AS "Outcome",
            (SELECT "Id" FROM turnover) AS "TurnoverPeriodId",
            COALESCE(
                (SELECT "PossessionReturnedAtUtc" FROM returned_relationship),
                (SELECT returned_at FROM decision)) AS "PossessionReturnedAtUtc",
            COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM ended_parties), '[]'::jsonb)::text
                AS "EndedPartyIdsJson",
            COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM revoked_access), '[]'::jsonb)::text
                AS "RevokedAccessIdsJson"
        """;
}
