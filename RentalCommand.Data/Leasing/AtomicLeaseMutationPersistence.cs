using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Leasing;

internal sealed partial class AtomicLeaseMutationPersistence : IAtomicLeaseMutationPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;

    public AtomicLeaseMutationPersistence(RentalCommandDbContext db, AtomicAuditScope auditScope)
    {
        _db = db;
        _auditScope = auditScope;
    }

    public async Task<AtomicAgreementDraftSignerReplacementResult> ReplaceAgreementDraftSignersAsync(
        int portfolioId,
        int leaseManagementId,
        int leaseAgreementId,
        int requiredDraftRevision,
        IReadOnlyList<AtomicAgreementDraftSignerInput> signers,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(signers.Select(signer => new
        {
            lease_management_party_id = signer.LeaseManagementPartyId,
            tenant_id = signer.TenantId,
            signer_role = signer.SignerRole,
            name_snapshot = signer.NameSnapshot,
            email_snapshot = signer.EmailSnapshot,
            signing_order = signer.SigningOrder,
            is_required = signer.IsRequired,
        }));
        var parameters = new NpgsqlParameter[]
        {
            JsonParameter("signers", payload),
            Integer("portfolioId", portfolioId),
            Integer("leaseManagementId", leaseManagementId),
            Integer("leaseAgreementId", leaseAgreementId),
            Integer("requiredDraftRevision", requiredDraftRevision),
        };
        using var lease = _auditScope.BeginInternalRawDmlBatch(
            new("LeaseAgreementSigners", AtomicRawDmlOperation.Delete),
            new("LeaseAgreementSigners", AtomicRawDmlOperation.Insert));
        var row = await _db.Database.SqlQueryRaw<AgreementSignerReplacementRow>(
                ReplaceAgreementDraftSignersSql, parameters)
            .SingleAsync(ct);
        if (!row.Eligible)
        {
            throw new InvalidOperationException(
                "Agreement draft changed or became immutable before signer replacement completed.");
        }
        return new(DeserializeIds(row.DeletedSignerIdsJson), DeserializeIds(row.CreatedSignerIdsJson));
    }

    public async Task<IReadOnlyList<int>> CopyAgreementDraftSignersAsync(
        int portfolioId,
        int leaseManagementId,
        int sourceAgreementId,
        int successorAgreementId,
        CancellationToken ct = default)
    {
        var parameters = new NpgsqlParameter[]
        {
            Integer("portfolioId", portfolioId),
            Integer("leaseManagementId", leaseManagementId),
            Integer("sourceAgreementId", sourceAgreementId),
            Integer("successorAgreementId", successorAgreementId),
        };
        using var lease = _auditScope.BeginInternalRawDml(
            "LeaseAgreementSigners", AtomicRawDmlOperation.Insert);
        var row = await _db.Database.SqlQueryRaw<AgreementSignerCopyRow>(
                CopyAgreementDraftSignersSql, parameters)
            .SingleAsync(ct);
        if (!row.Eligible)
        {
            throw new InvalidOperationException(
                "Source or successor Agreement changed before signer copy completed.");
        }
        return DeserializeIds(row.CreatedSignerIdsJson);
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

    public async Task<AtomicCancelPlannedRelationshipMutationResult> CancelPlannedRelationshipAsync(
        int portfolioId,
        int leaseManagementId,
        IReadOnlyList<AtomicCancelPlannedAccessInput> accesses,
        int actorUserId,
        DateTime changedAtUtc,
        string cancellationReasonCode,
        string? cancellationNote,
        string draftCancellationReason,
        CancellationToken ct = default)
    {
        var accessPayload = JsonSerializer.Serialize(accesses.Select(item => new
        {
            access_id = item.AccessId,
            disposition = (int)item.Disposition,
        }));
        var parameters = new NpgsqlParameter[]
        {
            JsonParameter("accesses", accessPayload),
            Integer("portfolioId", portfolioId),
            Integer("leaseManagementId", leaseManagementId),
            Integer("actorUserId", actorUserId),
            Timestamp("changedAt", changedAtUtc),
            Text("reasonCode", cancellationReasonCode.Trim()),
            NullableText("cancellationNote", cancellationNote?.Trim()),
            Text("draftReason", draftCancellationReason.Trim()),
            Text("accountCloseReason", "PlannedRelationshipCanceled"),
            Text("accountCloseNote", "Planned lease relationship canceled before possession."),
            Text("accessReason", "Tenant access revoked because the planned lease relationship was canceled."),
            Integer("revokeNow", (int)CancelPlannedAccessDisposition.RevokeNow),
            Integer("retain", (int)CancelPlannedAccessDisposition.Retain),
            Integer("canceled", (int)CancelPlannedRelationshipOutcome.Canceled),
            Integer("alreadyCanceled", (int)CancelPlannedRelationshipOutcome.AlreadyCanceled),
            Integer("possessionGiven", (int)CancelPlannedRelationshipOutcome.PossessionAlreadyGiven),
            Integer("issuedArtifacts", (int)CancelPlannedRelationshipOutcome.IssuedArtifactsRequireResolution),
            Integer("moneyResolution", (int)CancelPlannedRelationshipOutcome.FinancialResolutionRequired),
            Integer("invalidAccess", (int)CancelPlannedRelationshipOutcome.InvalidAccessPolicy),
        };

        using var lease = _auditScope.BeginInternalRawDmlBatch(
            new("LeaseAgreements", AtomicRawDmlOperation.Update),
            new("LeaseAddenda", AtomicRawDmlOperation.Update),
            new("TenantUserAccesses", AtomicRawDmlOperation.Update),
            new("TenantAccounts", AtomicRawDmlOperation.Update),
            new("LeaseManagements", AtomicRawDmlOperation.Update));
        var row = await _db.Database.SqlQueryRaw<CancelPlannedRelationshipRow>(
                CancelPlannedRelationshipSql, parameters)
            .SingleAsync(ct);
        return new(
            (CancelPlannedRelationshipOutcome)row.Outcome,
            row.CanceledAtUtc,
            row.TenantAccountId,
            DeserializeIds(row.CanceledAgreementDraftIdsJson),
            DeserializeIds(row.CanceledAddendumDraftIdsJson),
            DeserializeIds(row.RevokedAccessIdsJson),
            DeserializeIds(row.RetainedAccessIdsJson));
    }

    private static NpgsqlParameter JsonParameter(string name, string json) =>
        new(name, NpgsqlDbType.Jsonb) { Value = json };

    private static NpgsqlParameter Integer(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };

    private static NpgsqlParameter Timestamp(string name, DateTime value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value };

    private static NpgsqlParameter Text(string name, string value) =>
        new(name, NpgsqlDbType.Text) { Value = value };

    private static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

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

    private sealed class AgreementSignerReplacementRow
    {
        public bool Eligible { get; set; }
        public string DeletedSignerIdsJson { get; set; } = "[]";
        public string CreatedSignerIdsJson { get; set; } = "[]";
    }

    private sealed class AgreementSignerCopyRow
    {
        public bool Eligible { get; set; }
        public string CreatedSignerIdsJson { get; set; } = "[]";
    }

    private sealed class ReturnPossessionRow
    {
        public int Outcome { get; set; }
        public int? TurnoverPeriodId { get; set; }
        public DateTime? PossessionReturnedAtUtc { get; set; }
        public string EndedPartyIdsJson { get; set; } = "[]";
        public string RevokedAccessIdsJson { get; set; } = "[]";
    }

    private sealed class CancelPlannedRelationshipRow
    {
        public int Outcome { get; set; }
        public DateTime? CanceledAtUtc { get; set; }
        public int? TenantAccountId { get; set; }
        public string CanceledAgreementDraftIdsJson { get; set; } = "[]";
        public string CanceledAddendumDraftIdsJson { get; set; } = "[]";
        public string RevokedAccessIdsJson { get; set; } = "[]";
        public string RetainedAccessIdsJson { get; set; } = "[]";
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

    private const string ReplaceAgreementDraftSignersSql = """
        WITH input AS MATERIALIZED (
            SELECT lease_management_party_id, tenant_id, signer_role, name_snapshot,
                   email_snapshot, signing_order, is_required
            FROM jsonb_to_recordset(@signers::jsonb) AS row(
                lease_management_party_id integer,
                tenant_id integer,
                signer_role integer,
                name_snapshot text,
                email_snapshot text,
                signing_order smallint,
                is_required boolean)
        ),
        eligible AS MATERIALIZED (
            SELECT agreement."Id"
            FROM "LeaseAgreements" AS agreement
            WHERE agreement."Id" = @leaseAgreementId
              AND agreement."PortfolioId" = @portfolioId
              AND agreement."LeaseManagementId" = @leaseManagementId
              AND agreement."DraftRevision" = @requiredDraftRevision
              AND agreement."IssuedAtUtc" IS NULL
              AND agreement."IssuedArtifactId" IS NULL
              AND agreement."FullyExecutedAtUtc" IS NULL
              AND agreement."ExecutedArtifactId" IS NULL
              AND agreement."VoidedAtUtc" IS NULL
              AND agreement."DraftCanceledAtUtc" IS NULL
            FOR UPDATE
        ),
        deleted AS (
            DELETE FROM "LeaseAgreementSigners" AS signer
            USING eligible
            WHERE signer."LeaseAgreementId" = eligible."Id"
              AND signer."PortfolioId" = @portfolioId
            RETURNING signer."Id"
        ),
        inserted AS (
            INSERT INTO "LeaseAgreementSigners"
                ("PortfolioId", "LeaseAgreementId", "LeaseManagementPartyId", "TenantId",
                 "SignerRole", "NameSnapshot", "EmailSnapshot", "SigningOrder", "IsRequired")
            SELECT @portfolioId, eligible."Id", input.lease_management_party_id, input.tenant_id,
                   CASE input.signer_role
                       WHEN 0 THEN 'PrimaryTenant'
                       WHEN 1 THEN 'CoTenant'
                       WHEN 2 THEN 'Guarantor'
                       WHEN 3 THEN 'Manager'
                       WHEN 4 THEN 'Owner'
                       WHEN 5 THEN 'Other'
                   END,
                   input.name_snapshot, input.email_snapshot, input.signing_order, input.is_required
            FROM input
            CROSS JOIN eligible
            ORDER BY input.signing_order
            RETURNING "Id"
        )
        SELECT EXISTS (SELECT 1 FROM eligible) AS "Eligible",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM deleted), '[]'::jsonb)::text
                   AS "DeletedSignerIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM inserted), '[]'::jsonb)::text
                   AS "CreatedSignerIdsJson"
        """;

    private const string CopyAgreementDraftSignersSql = """
        WITH eligible AS MATERIALIZED (
            SELECT successor."Id" AS successor_id
            FROM "LeaseAgreements" AS source
            INNER JOIN "LeaseAgreements" AS successor
                ON successor."Id" = @successorAgreementId
               AND successor."PortfolioId" = @portfolioId
               AND successor."LeaseManagementId" = @leaseManagementId
               AND successor."IssuedAtUtc" IS NULL
               AND successor."DraftCanceledAtUtc" IS NULL
            WHERE source."Id" = @sourceAgreementId
              AND source."PortfolioId" = @portfolioId
              AND source."LeaseManagementId" = @leaseManagementId
              AND source."FullyExecutedAtUtc" IS NOT NULL
              AND source."VoidedAtUtc" IS NULL
        ),
        inserted AS (
            INSERT INTO "LeaseAgreementSigners"
                ("PortfolioId", "LeaseAgreementId", "LeaseManagementPartyId", "TenantId",
                 "SignerRole", "NameSnapshot", "EmailSnapshot", "SigningOrder", "IsRequired")
            SELECT source."PortfolioId", eligible.successor_id, source."LeaseManagementPartyId",
                   source."TenantId", source."SignerRole", source."NameSnapshot",
                   source."EmailSnapshot", source."SigningOrder", source."IsRequired"
            FROM "LeaseAgreementSigners" AS source
            CROSS JOIN eligible
            WHERE source."PortfolioId" = @portfolioId
              AND source."LeaseAgreementId" = @sourceAgreementId
            ORDER BY source."SigningOrder"
            RETURNING "Id"
        )
        SELECT EXISTS (SELECT 1 FROM eligible) AS "Eligible",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM inserted), '[]'::jsonb)::text
                   AS "CreatedSignerIdsJson"
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

    private const string CancelPlannedRelationshipSql = """
        WITH access_input AS MATERIALIZED (
            SELECT access_id, disposition
            FROM jsonb_to_recordset(@accesses::jsonb) AS row(access_id integer, disposition integer)
        ),
        relationship AS MATERIALIZED (
            SELECT relationship.*
            FROM "LeaseManagements" AS relationship
            WHERE relationship."Id" = @leaseManagementId
              AND relationship."PortfolioId" = @portfolioId
            FOR UPDATE
        ),
        account AS MATERIALIZED (
            SELECT account.*
            FROM "TenantAccounts" AS account
            INNER JOIN relationship
                ON relationship."Id" = account."LeaseManagementId"
               AND relationship."PortfolioId" = account."PortfolioId"
            FOR UPDATE OF account
        ),
        active_access AS MATERIALIZED (
            SELECT access."Id"
            FROM "TenantUserAccesses" AS access
            INNER JOIN "LeaseManagementParties" AS party
                ON party."Id" = access."LeaseManagementPartyId"
               AND party."PortfolioId" = access."PortfolioId"
            WHERE party."LeaseManagementId" = @leaseManagementId
              AND party."PortfolioId" = @portfolioId
              AND access."RevokedAtUtc" IS NULL
        ),
        validation AS MATERIALIZED (
            SELECT
                relationship."CanceledAtUtc" IS NOT NULL AS already_canceled,
                relationship."CanceledAtUtc" AS canceled_at,
                relationship."PossessionGivenAtUtc" IS NOT NULL AS possession_given,
                account."Id" AS tenant_account_id,
                EXISTS (
                    SELECT 1
                    FROM "LeaseAgreements" AS agreement
                    WHERE agreement."PortfolioId" = @portfolioId
                      AND agreement."LeaseManagementId" = @leaseManagementId
                      AND agreement."IssuedAtUtc" IS NOT NULL
                      AND agreement."VoidedAtUtc" IS NULL)
                OR EXISTS (
                    SELECT 1
                    FROM "LeaseAddenda" AS addendum
                    WHERE addendum."PortfolioId" = @portfolioId
                      AND addendum."LeaseManagementId" = @leaseManagementId
                      AND addendum."IssuedAtUtc" IS NOT NULL
                      AND addendum."VoidedAtUtc" IS NULL)
                    AS unresolved_issued_artifacts,
                EXISTS (
                    SELECT 1
                    FROM "TenantLedgerEntries" AS entry
                    WHERE entry."PortfolioId" = @portfolioId
                      AND entry."TenantAccountId" = account."Id")
                OR EXISTS (
                    SELECT 1
                    FROM "SecurityDepositEntries" AS entry
                    INNER JOIN "SecurityDepositAccounts" AS deposit
                        ON deposit."Id" = entry."SecurityDepositAccountId"
                       AND deposit."PortfolioId" = entry."PortfolioId"
                    WHERE deposit."PortfolioId" = @portfolioId
                      AND deposit."TenantAccountId" = account."Id")
                OR COALESCE((
                    SELECT sum(CASE WHEN entry."Direction" = 'Debit' THEN entry."Amount" ELSE -entry."Amount" END)
                    FROM "TenantLedgerEntries" AS entry
                    WHERE entry."PortfolioId" = @portfolioId
                      AND entry."TenantAccountId" = account."Id"), 0) <> 0
                OR COALESCE((
                    SELECT sum(CASE WHEN entry."Direction" = 'Increase' THEN entry."Amount" ELSE -entry."Amount" END)
                    FROM "SecurityDepositEntries" AS entry
                    INNER JOIN "SecurityDepositAccounts" AS deposit
                        ON deposit."Id" = entry."SecurityDepositAccountId"
                       AND deposit."PortfolioId" = entry."PortfolioId"
                    WHERE deposit."PortfolioId" = @portfolioId
                      AND deposit."TenantAccountId" = account."Id"), 0) <> 0
                OR EXISTS (
                    SELECT 1
                    FROM "TenantPaymentAttempts" AS attempt
                    WHERE attempt."PortfolioId" = @portfolioId
                      AND attempt."TenantAccountId" = account."Id"
                      AND attempt."State" NOT IN ('Failed', 'Canceled'))
                OR EXISTS (
                    SELECT 1
                    FROM "TenantAutopayEnrollments" AS enrollment
                    WHERE enrollment."PortfolioId" = @portfolioId
                      AND enrollment."TenantAccountId" = account."Id"
                      AND enrollment."CanceledAtUtc" IS NULL)
                    AS money_resolution_required,
                (SELECT count(*) FROM access_input) =
                    (SELECT count(DISTINCT access_id) FROM access_input)
                AND NOT EXISTS (
                    SELECT access_id FROM access_input EXCEPT SELECT "Id" FROM active_access)
                AND NOT EXISTS (
                    SELECT "Id" FROM active_access EXCEPT SELECT access_id FROM access_input)
                AND NOT EXISTS (
                    SELECT 1 FROM access_input
                    WHERE disposition NOT IN (@revokeNow, @retain))
                    AS access_policy_valid
            FROM relationship
            INNER JOIN account ON true
        ),
        decision AS MATERIALIZED (
            SELECT *,
                CASE
                    WHEN already_canceled THEN @alreadyCanceled
                    WHEN possession_given THEN @possessionGiven
                    WHEN unresolved_issued_artifacts THEN @issuedArtifacts
                    WHEN money_resolution_required THEN @moneyResolution
                    WHEN NOT access_policy_valid THEN @invalidAccess
                    ELSE @canceled
                END AS outcome
            FROM validation
        ),
        canceled_agreement_drafts AS (
            UPDATE "LeaseAgreements" AS agreement
            SET "DraftCanceledAtUtc" = @changedAt,
                "DraftCancellationReason" = @draftReason,
                "UpdatedAtUtc" = @changedAt
            FROM decision
            WHERE decision.outcome = @canceled
              AND agreement."PortfolioId" = @portfolioId
              AND agreement."LeaseManagementId" = @leaseManagementId
              AND agreement."IssuedAtUtc" IS NULL
              AND agreement."DraftCanceledAtUtc" IS NULL
            RETURNING agreement."Id"
        ),
        canceled_addendum_drafts AS (
            UPDATE "LeaseAddenda" AS addendum
            SET "DraftCanceledAtUtc" = @changedAt,
                "DraftCancellationReason" = @draftReason,
                "UpdatedAtUtc" = @changedAt
            FROM decision
            WHERE decision.outcome = @canceled
              AND addendum."PortfolioId" = @portfolioId
              AND addendum."LeaseManagementId" = @leaseManagementId
              AND addendum."IssuedAtUtc" IS NULL
              AND addendum."DraftCanceledAtUtc" IS NULL
            RETURNING addendum."Id"
        ),
        revoked_access AS (
            UPDATE "TenantUserAccesses" AS access
            SET "RevokedAtUtc" = @changedAt,
                "RevokedByUserId" = @actorUserId,
                "Reason" = @accessReason
            FROM access_input AS input, decision
            WHERE decision.outcome = @canceled
              AND input.disposition = @revokeNow
              AND access."Id" = input.access_id
              AND access."PortfolioId" = @portfolioId
              AND access."RevokedAtUtc" IS NULL
            RETURNING access."Id"
        ),
        closed_account AS (
            UPDATE "TenantAccounts" AS account
            SET "ClosedAtUtc" = @changedAt,
                "CloseReasonCode" = @accountCloseReason,
                "CloseNote" = @accountCloseNote
            FROM decision
            WHERE decision.outcome = @canceled
              AND account."Id" = decision.tenant_account_id
              AND account."PortfolioId" = @portfolioId
              AND account."ClosedAtUtc" IS NULL
            RETURNING account."Id", account."ClosedAtUtc"
        ),
        canceled_relationship AS (
            UPDATE "LeaseManagements" AS relationship
            SET "CanceledAtUtc" = @changedAt,
                "CancellationReasonCode" = @reasonCode,
                "CancellationNote" = @cancellationNote,
                "AccountClosedAtUtc" = @changedAt,
                "UpdatedAtUtc" = @changedAt,
                "RowVersion" = gen_random_uuid()
            FROM decision, closed_account
            WHERE decision.outcome = @canceled
              AND relationship."Id" = @leaseManagementId
              AND relationship."PortfolioId" = @portfolioId
              AND relationship."CanceledAtUtc" IS NULL
            RETURNING relationship."CanceledAtUtc"
        )
        SELECT
            COALESCE((SELECT outcome FROM decision), @invalidAccess) AS "Outcome",
            COALESCE(
                (SELECT "CanceledAtUtc" FROM canceled_relationship),
                (SELECT canceled_at FROM decision)) AS "CanceledAtUtc",
            (SELECT tenant_account_id FROM decision) AS "TenantAccountId",
            COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM canceled_agreement_drafts), '[]'::jsonb)::text
                AS "CanceledAgreementDraftIdsJson",
            COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM canceled_addendum_drafts), '[]'::jsonb)::text
                AS "CanceledAddendumDraftIdsJson",
            COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM revoked_access), '[]'::jsonb)::text
                AS "RevokedAccessIdsJson",
            COALESCE((
                SELECT jsonb_agg(input.access_id ORDER BY input.access_id)
                FROM access_input AS input, decision
                WHERE decision.outcome = @canceled AND input.disposition = @retain), '[]'::jsonb)::text
                AS "RetainedAccessIdsJson"
        """;
}
