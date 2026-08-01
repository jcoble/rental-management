using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Leasing;

public static partial class AtomicLeaseMutationPersistence
{
    public static async Task<bool> ValidateAgreementDraftSignerScopeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int leaseManagementId,
        IReadOnlyList<AtomicAgreementDraftSignerInput> signers,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(signers.Select(signer => new
        {
            lease_management_party_id = signer.LeaseManagementPartyId,
            tenant_id = signer.TenantId,
        }));
        RequireAuditScope(db, context);
        var row = await db.Database.SqlQueryRaw<SignerScopeValidationRow>(
                ValidateAgreementDraftSignerScopeSql,
                JsonParameter("signers", payload),
                Integer("portfolioId", portfolioId),
                Integer("leaseManagementId", leaseManagementId))
            .SingleAsync(ct);
        return row.InputValid;
    }

    public static async Task<AtomicRenewalAddendumDraftResult> CreateRenewalAddendumDraftsAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int leaseManagementId,
        int sourceAgreementId,
        int renewalAgreementId,
        DateOnly governingFromOn,
        IReadOnlyList<AtomicRenewalAddendumDecisionInput> decisions,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(decisions.Select(decision => new
        {
            source_addendum_series_public_id = decision.SourceAddendumSeriesPublicId,
            decision = decision.Decision,
        }));
        var parameters = new NpgsqlParameter[]
        {
            JsonParameter("decisions", payload),
            Integer("portfolioId", portfolioId),
            Integer("leaseManagementId", leaseManagementId),
            Integer("sourceAgreementId", sourceAgreementId),
            Integer("renewalAgreementId", renewalAgreementId),
            Date("governingFromOn", governingFromOn),
            Integer("actorUserId", actorUserId),
            Timestamp("createdAt", createdAtUtc),
            Integer("end", 0),
            Integer("incorporate", 1),
            Integer("reissue", 2),
        };
        using var lease = RequireAuditScope(db, context).BeginInternalRawDmlBatch(
            new("LeaseAddenda", AtomicRawDmlOperation.Insert),
            new("LeaseAddendumSigners", AtomicRawDmlOperation.Insert),
            new("LeaseAddendumFinancialEffects", AtomicRawDmlOperation.Insert),
            new("LeaseRenewalAddendumDecisions", AtomicRawDmlOperation.Insert));
        var row = await db.Database.SingleTopLevelResultAsync<RenewalAddendumDraftRow>(
            CreateRenewalAddendumDraftsSql, parameters, ct);
        return new(
            row.InputValid,
            DeserializeIds(row.DecisionIdsJson),
            DeserializeIds(row.ReplacementAddendumIdsJson),
            DeserializeIds(row.ReplacementSignerIdsJson),
            DeserializeIds(row.ReplacementFinancialEffectIdsJson));
    }

    public static async Task<AtomicAgreementDraftSignerReplacementResult> ReplaceAgreementDraftSignersAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
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
        using var lease = RequireAuditScope(db, context).BeginInternalRawDmlBatch(
            new("LeaseAgreementSigners", AtomicRawDmlOperation.Delete),
            new("LeaseAgreementSigners", AtomicRawDmlOperation.Insert));
        var row = await db.Database.SingleTopLevelResultAsync<AgreementSignerReplacementRow>(
            ReplaceAgreementDraftSignersSql, parameters, ct);
        if (!row.Eligible)
        {
            throw new InvalidOperationException(
                "Agreement draft changed or became immutable before signer replacement completed.");
        }
        return new(DeserializeIds(row.DeletedSignerIdsJson), DeserializeIds(row.CreatedSignerIdsJson));
    }

    public static async Task<IReadOnlyList<int>> CopyAgreementDraftSignersAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
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
        using var lease = RequireAuditScope(db, context).BeginInternalRawDml(
            "LeaseAgreementSigners", AtomicRawDmlOperation.Insert);
        var row = await db.Database.SingleTopLevelResultAsync<AgreementSignerCopyRow>(
            CopyAgreementDraftSignersSql, parameters, ct);
        if (!row.Eligible)
        {
            throw new InvalidOperationException(
                "Source or successor Agreement changed before signer copy completed.");
        }
        return DeserializeIds(row.CreatedSignerIdsJson);
    }

    public static async Task<IReadOnlyList<int>> CopyIssuedAgreementReplacementDraftSignersAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int leaseManagementId,
        int sourceAgreementId,
        int replacementAgreementId,
        CancellationToken ct = default)
    {
        var parameters = new NpgsqlParameter[]
        {
            Integer("portfolioId", portfolioId),
            Integer("leaseManagementId", leaseManagementId),
            Integer("sourceAgreementId", sourceAgreementId),
            Integer("replacementAgreementId", replacementAgreementId),
        };
        using var lease = RequireAuditScope(db, context).BeginInternalRawDml(
            "LeaseAgreementSigners", AtomicRawDmlOperation.Insert);
        var row = await db.Database.SingleTopLevelResultAsync<AgreementSignerCopyRow>(
            CopyIssuedAgreementReplacementDraftSignersSql, parameters, ct);
        if (!row.Eligible)
        {
            throw new InvalidOperationException(
                "Voided source or replacement Agreement changed before signer copy completed.");
        }
        return DeserializeIds(row.CreatedSignerIdsJson);
    }

    public static async Task<AtomicAddendumCorrectionChildCopyResult> CopyAddendumCorrectionChildrenAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int leaseManagementId,
        int sourceAddendumId,
        int correctionAddendumId,
        CancellationToken ct = default)
    {
        var parameters = new NpgsqlParameter[]
        {
            Integer("portfolioId", portfolioId),
            Integer("leaseManagementId", leaseManagementId),
            Integer("sourceAddendumId", sourceAddendumId),
            Integer("correctionAddendumId", correctionAddendumId),
        };
        using var lease = RequireAuditScope(db, context).BeginInternalRawDmlBatch(
            new("LeaseAddendumSigners", AtomicRawDmlOperation.Insert),
            new("LeaseAddendumFinancialEffects", AtomicRawDmlOperation.Insert));
        var row = await db.Database.SingleTopLevelResultAsync<AddendumCorrectionChildCopyRow>(
            CopyAddendumCorrectionChildrenSql, parameters, ct);
        return new(
            row.Eligible,
            DeserializeIds(row.CreatedSignerIdsJson),
            DeserializeIds(row.CreatedFinancialEffectIdsJson));
    }

    public static async Task<AtomicTenantAccessTransitionResult> TransitionTenantAccessAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
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

        using var lease = RequireAuditScope(db, context).BeginInternalRawDmlBatch(
            new("TenantUserAccesses", AtomicRawDmlOperation.Update),
            new("TenantUserAccesses", AtomicRawDmlOperation.Insert));
        var row = await db.Database.SingleTopLevelResultAsync<AccessTransitionRow>(
            AccessTransitionSql, parameters, ct);
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

    public static async Task<AtomicReturnPossessionMutationResult> ReturnPossessionAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int leaseManagementId,
        int unitId,
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

        using var lease = RequireAuditScope(db, context).BeginInternalRawDmlBatch(
            new("LeaseManagementParties", AtomicRawDmlOperation.Update),
            new("TenantUserAccesses", AtomicRawDmlOperation.Update),
            new("LeaseManagements", AtomicRawDmlOperation.Update),
            new("UnitOperationalPeriods", AtomicRawDmlOperation.Insert));
        var row = await db.Database.SingleTopLevelResultAsync<ReturnPossessionRow>(
            ReturnPossessionSql, parameters, ct);
        return new(
            (ReturnPossessionOutcome)row.Outcome,
            row.TurnoverPeriodId,
            row.PossessionReturnedAtUtc,
            DeserializeIds(row.EndedPartyIdsJson),
            DeserializeIds(row.RevokedAccessIdsJson));
    }

    public static async Task<AtomicCancelPlannedRelationshipMutationResult> CancelPlannedRelationshipAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
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

        using var lease = RequireAuditScope(db, context).BeginInternalRawDmlBatch(
            new("LeaseAgreements", AtomicRawDmlOperation.Update),
            new("LeaseAddenda", AtomicRawDmlOperation.Update),
            new("TenantUserAccesses", AtomicRawDmlOperation.Update),
            new("WorkspaceAccessContexts", AtomicRawDmlOperation.Update),
            new("TenantAccounts", AtomicRawDmlOperation.Update),
            new("LeaseManagements", AtomicRawDmlOperation.Update));
        var row = await db.Database.SingleTopLevelResultAsync<CancelPlannedRelationshipRow>(
            CancelPlannedRelationshipSql, parameters, ct);
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

    private static NpgsqlParameter Date(string name, DateOnly value) =>
        new(name, NpgsqlDbType.Date) { Value = value };

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

    private sealed class SignerScopeValidationRow
    {
        public bool InputValid { get; set; }
    }

    private sealed class RenewalAddendumDraftRow
    {
        public bool InputValid { get; set; }
        public string DecisionIdsJson { get; set; } = "[]";
        public string ReplacementAddendumIdsJson { get; set; } = "[]";
        public string ReplacementSignerIdsJson { get; set; } = "[]";
        public string ReplacementFinancialEffectIdsJson { get; set; } = "[]";
    }

    private sealed class AgreementSignerCopyRow
    {
        public bool Eligible { get; set; }
        public string CreatedSignerIdsJson { get; set; } = "[]";
    }

    private sealed class AddendumCorrectionChildCopyRow
    {
        public bool Eligible { get; set; }
        public string CreatedSignerIdsJson { get; set; } = "[]";
        public string CreatedFinancialEffectIdsJson { get; set; } = "[]";
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
        locked_contexts AS MATERIALIZED (
            SELECT context."Id"
            FROM "WorkspaceAccessContexts" AS context
            WHERE context."Id" IN (SELECT DISTINCT "AccessContextId" FROM source_access)
            FOR UPDATE
        ),
        revoked AS (
            UPDATE "TenantUserAccesses" AS access
            SET "RevokedAtUtc" = @changedAt,
                "RevokedByUserId" = @actorUserId,
                "Reason" = @reason
            FROM source_access AS source
            INNER JOIN locked_contexts AS locked ON locked."Id" = source."AccessContextId"
            WHERE access."Id" = source."Id"
              AND source.kind <> @retain
            RETURNING access."Id", access."AccessContextId", access."ApplicationUserId",
                      source.replacement_party_id, source.kind
        ),
        continued AS (
            INSERT INTO "TenantUserAccesses"
                ("PublicId", "PortfolioId", "AccessContextId", "ApplicationUserId", "LeaseManagementPartyId",
                 "GrantedAtUtc", "GrantedByUserId", "Reason")
            SELECT gen_random_uuid(), @portfolioId, revoked."AccessContextId", revoked."ApplicationUserId",
                   revoked.replacement_party_id, @changedAt, @actorUserId, @reason
            FROM revoked
            INNER JOIN "LeaseManagementParties" AS replacement
                ON replacement."Id" = revoked.replacement_party_id
               AND replacement."PortfolioId" = @portfolioId
               AND replacement."LeaseManagementId" = @leaseManagementId
            WHERE revoked.kind = @continue
            RETURNING "Id"
        ),
        revised_contexts AS (
            UPDATE "WorkspaceAccessContexts" AS context
            SET "AccessRevision" = context."AccessRevision" + 1,
                "UpdatedAtUtc" = @changedAt
            WHERE context."Id" IN (SELECT DISTINCT "AccessContextId" FROM revoked)
            RETURNING context."Id"
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
        input_validation AS MATERIALIZED (
            SELECT NOT EXISTS (
                SELECT 1
                FROM input
                LEFT JOIN "LeaseManagementParties" AS party
                  ON party."Id" = input.lease_management_party_id
                 AND party."PortfolioId" = @portfolioId
                 AND party."LeaseManagementId" = @leaseManagementId
                WHERE (input.lease_management_party_id IS NULL) <> (input.tenant_id IS NULL)
                   OR (input.lease_management_party_id IS NOT NULL
                       AND (party."Id" IS NULL OR party."TenantId" <> input.tenant_id))
            ) AS input_valid
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
              AND (SELECT input_valid FROM input_validation)
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

    private const string ValidateAgreementDraftSignerScopeSql = """
        WITH input AS MATERIALIZED (
            SELECT lease_management_party_id, tenant_id
            FROM jsonb_to_recordset(@signers::jsonb) AS row(
                lease_management_party_id integer,
                tenant_id integer)
        )
        SELECT NOT EXISTS (
            SELECT 1
            FROM input
            LEFT JOIN "LeaseManagementParties" AS party
              ON party."Id" = input.lease_management_party_id
             AND party."PortfolioId" = @portfolioId
             AND party."LeaseManagementId" = @leaseManagementId
            WHERE (input.lease_management_party_id IS NULL) <> (input.tenant_id IS NULL)
               OR (input.lease_management_party_id IS NOT NULL
                   AND (party."Id" IS NULL OR party."TenantId" <> input.tenant_id))
        ) AS "InputValid"
        """;

    private const string CreateRenewalAddendumDraftsSql = """
        WITH input AS MATERIALIZED (
            SELECT source_addendum_series_public_id, decision
            FROM jsonb_to_recordset(@decisions::jsonb) AS row(
                source_addendum_series_public_id uuid,
                decision integer)
        ),
        effective_source AS MATERIALIZED (
            SELECT source.*
            FROM "LeaseAddenda" AS source
            INNER JOIN "LeaseAgreements" AS source_base
              ON source_base."Id" = source."BaseAgreementId"
             AND source_base."PortfolioId" = source."PortfolioId"
             AND source_base."LeaseManagementId" = source."LeaseManagementId"
             AND source_base."FullyExecutedAtUtc" IS NOT NULL
             AND source_base."ExecutedArtifactId" IS NOT NULL
             AND source_base."VoidedAtUtc" IS NULL
             AND source_base."DraftCanceledAtUtc" IS NULL
            WHERE source."PortfolioId" = @portfolioId
              AND source."LeaseManagementId" = @leaseManagementId
              AND source."FullyExecutedAtUtc" IS NOT NULL
              AND source."ExecutedArtifactId" IS NOT NULL
              AND source."VoidedAtUtc" IS NULL
              AND source."DraftCanceledAtUtc" IS NULL
              AND source."EffectiveFromOn" <= rc_business_date(@portfolioId)
              AND (source."EffectiveThroughOn" IS NULL
                   OR source."EffectiveThroughOn" >= rc_business_date(@portfolioId))
              AND (source."SupersededEffectiveOn" IS NULL
                   OR source."SupersededEffectiveOn" > rc_business_date(@portfolioId))
              AND NOT EXISTS (
                  SELECT 1
                  FROM "LeaseAddenda" AS newer
                  INNER JOIN "LeaseAgreements" AS newer_base
                    ON newer_base."Id" = newer."BaseAgreementId"
                   AND newer_base."PortfolioId" = newer."PortfolioId"
                   AND newer_base."LeaseManagementId" = newer."LeaseManagementId"
                   AND newer_base."FullyExecutedAtUtc" IS NOT NULL
                   AND newer_base."ExecutedArtifactId" IS NOT NULL
                   AND newer_base."VoidedAtUtc" IS NULL
                   AND newer_base."DraftCanceledAtUtc" IS NULL
                  WHERE newer."PortfolioId" = source."PortfolioId"
                    AND newer."LeaseManagementId" = source."LeaseManagementId"
                    AND newer."SeriesPublicId" = source."SeriesPublicId"
                    AND newer."VersionNumber" > source."VersionNumber"
                    AND newer."FullyExecutedAtUtc" IS NOT NULL
                    AND newer."ExecutedArtifactId" IS NOT NULL
                    AND newer."VoidedAtUtc" IS NULL
                    AND newer."DraftCanceledAtUtc" IS NULL
                    AND newer."EffectiveFromOn" <= rc_business_date(@portfolioId)
                    AND (newer."EffectiveThroughOn" IS NULL
                         OR newer."EffectiveThroughOn" >= rc_business_date(@portfolioId))
                    AND (newer."SupersededEffectiveOn" IS NULL
                         OR newer."SupersededEffectiveOn" > rc_business_date(@portfolioId)))
        ),
        validation AS MATERIALIZED (
            SELECT
                EXISTS (
                    SELECT 1
                    FROM "LeaseAgreements" AS renewal
                    WHERE renewal."Id" = @renewalAgreementId
                      AND renewal."PortfolioId" = @portfolioId
                      AND renewal."LeaseManagementId" = @leaseManagementId
                      AND renewal."RenewsAgreementId" = @sourceAgreementId
                      AND renewal."IssuedAtUtc" IS NULL
                      AND renewal."DraftCanceledAtUtc" IS NULL) AS renewal_valid,
                (SELECT count(*) FROM input) =
                    (SELECT count(DISTINCT source_addendum_series_public_id) FROM input)
                AND NOT EXISTS (SELECT 1 FROM input WHERE decision NOT IN (@end, @incorporate, @reissue))
                AND NOT EXISTS (
                    SELECT source_addendum_series_public_id FROM input
                    EXCEPT SELECT "SeriesPublicId" FROM effective_source)
                AND NOT EXISTS (
                    SELECT "SeriesPublicId" FROM effective_source
                    EXCEPT SELECT source_addendum_series_public_id FROM input) AS decisions_valid
        ),
        next_versions AS MATERIALIZED (
            SELECT source."Id" AS source_addendum_id,
                   max(existing."VersionNumber") + 1 AS next_version_number
            FROM effective_source AS source
            INNER JOIN "LeaseAddenda" AS existing
                ON existing."PortfolioId" = source."PortfolioId"
               AND existing."LeaseManagementId" = source."LeaseManagementId"
               AND existing."SeriesPublicId" = source."SeriesPublicId"
            GROUP BY source."Id"
        ),
        replacements AS (
            INSERT INTO "LeaseAddenda"
                ("PublicId", "SeriesPublicId", "PortfolioId", "LeaseManagementId",
                 "BaseAgreementId", "VersionNumber", "AddendumNumber", "Purpose",
                 "ReplacesAddendumId", "EffectiveFromOn", "EffectiveThroughOn",
                 "TermsSchemaVersion", "TermsPayload", "DocumentSourceVersionId",
                 "CreatedAtUtc", "CreatedByUserId",
                 "UpdatedAtUtc", "DraftRevision")
            SELECT gen_random_uuid(), source."SeriesPublicId", @portfolioId, @leaseManagementId,
                   @renewalAgreementId, next_versions.next_version_number, source."AddendumNumber",
                   source."Purpose", source."Id", @governingFromOn,
                   CASE WHEN source."EffectiveThroughOn" IS NULL
                             OR source."EffectiveThroughOn" >= @governingFromOn
                        THEN source."EffectiveThroughOn" ELSE NULL END,
                   source."TermsSchemaVersion", source."TermsPayload", source."DocumentSourceVersionId",
                   @createdAt, @actorUserId, @createdAt, 1
            FROM input
            INNER JOIN effective_source AS source
                ON source."SeriesPublicId" = input.source_addendum_series_public_id
            INNER JOIN next_versions
                ON next_versions.source_addendum_id = source."Id"
            CROSS JOIN validation
            WHERE validation.renewal_valid AND validation.decisions_valid
              AND input.decision = @reissue
            ORDER BY source."SeriesPublicId"
            RETURNING "Id", "SeriesPublicId", "ReplacesAddendumId"
        ),
        replacement_signers AS (
            INSERT INTO "LeaseAddendumSigners"
                ("PortfolioId", "LeaseAddendumId", "LeaseManagementPartyId", "TenantId",
                 "SignerRole", "NameSnapshot", "EmailSnapshot", "SigningOrder", "IsRequired")
            SELECT @portfolioId, replacement."Id", signer."LeaseManagementPartyId", signer."TenantId",
                   signer."SignerRole", signer."NameSnapshot", signer."EmailSnapshot",
                   signer."SigningOrder", signer."IsRequired"
            FROM replacements AS replacement
            INNER JOIN "LeaseAddendumSigners" AS signer
                ON signer."LeaseAddendumId" = replacement."ReplacesAddendumId"
               AND signer."PortfolioId" = @portfolioId
            ORDER BY replacement."Id", signer."SigningOrder"
            RETURNING "Id"
        ),
        replacement_effects AS (
            INSERT INTO "LeaseAddendumFinancialEffects"
                ("PortfolioId", "LeaseAddendumId", "EffectType", "Amount", "Currency",
                 "ChargeCode", "EffectiveFromOn", "EffectiveThroughOn", "DueOn", "Description")
            SELECT @portfolioId, replacement."Id", effect."EffectType", effect."Amount",
                   effect."Currency", effect."ChargeCode",
                   CASE WHEN effect."EffectiveFromOn" IS NULL THEN NULL ELSE @governingFromOn END,
                   CASE WHEN effect."EffectiveThroughOn" IS NULL
                             OR effect."EffectiveThroughOn" >= @governingFromOn
                        THEN effect."EffectiveThroughOn" ELSE NULL END,
                   CASE WHEN effect."DueOn" IS NULL THEN NULL
                        WHEN effect."DueOn" < @governingFromOn THEN @governingFromOn
                        ELSE effect."DueOn" END,
                   effect."Description"
            FROM replacements AS replacement
            INNER JOIN "LeaseAddendumFinancialEffects" AS effect
                ON effect."LeaseAddendumId" = replacement."ReplacesAddendumId"
               AND effect."PortfolioId" = @portfolioId
            ORDER BY replacement."Id", effect."Id"
            RETURNING "Id"
        ),
        decisions AS (
            INSERT INTO "LeaseRenewalAddendumDecisions"
                ("PortfolioId", "LeaseManagementId", "RenewalAgreementId",
                 "SourceAddendumSeriesPublicId", "Decision", "ReplacementAddendumId",
                 "CreatedAtUtc", "CreatedByUserId")
            SELECT @portfolioId, @leaseManagementId, @renewalAgreementId,
                   input.source_addendum_series_public_id,
                   CASE input.decision WHEN @end THEN 'End'
                       WHEN @incorporate THEN 'IncorporateIntoBase'
                       WHEN @reissue THEN 'ReissueAsAddendum' END,
                   replacement."Id", @createdAt, @actorUserId
            FROM input
            CROSS JOIN validation
            LEFT JOIN replacements AS replacement
                ON replacement."SeriesPublicId" = input.source_addendum_series_public_id
            WHERE validation.renewal_valid AND validation.decisions_valid
            ORDER BY input.source_addendum_series_public_id
            RETURNING "Id"
        )
        SELECT validation.renewal_valid AND validation.decisions_valid AS "InputValid",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM decisions), '[]'::jsonb)::text
                   AS "DecisionIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM replacements), '[]'::jsonb)::text
                   AS "ReplacementAddendumIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM replacement_signers), '[]'::jsonb)::text
                   AS "ReplacementSignerIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM replacement_effects), '[]'::jsonb)::text
                   AS "ReplacementFinancialEffectIdsJson"
        FROM validation
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

    private const string CopyIssuedAgreementReplacementDraftSignersSql = """
        WITH eligible AS MATERIALIZED (
            SELECT replacement."Id" AS replacement_id
            FROM "LeaseAgreements" AS source
            INNER JOIN "LeaseAgreements" AS replacement
                ON replacement."Id" = @replacementAgreementId
               AND replacement."PortfolioId" = @portfolioId
               AND replacement."LeaseManagementId" = @leaseManagementId
               AND replacement."ReissuesAgreementId" = source."Id"
               AND replacement."ChangeType" = source."ChangeType"
               AND replacement."TransferredFromAgreementId" IS NOT DISTINCT FROM source."TransferredFromAgreementId"
               AND replacement."ReplacesAgreementId" IS NOT DISTINCT FROM source."ReplacesAgreementId"
               AND replacement."RenewsAgreementId" IS NOT DISTINCT FROM source."RenewsAgreementId"
               AND replacement."IssuedAtUtc" IS NULL
               AND replacement."IssuedArtifactId" IS NULL
               AND replacement."FullyExecutedAtUtc" IS NULL
               AND replacement."ExecutedArtifactId" IS NULL
               AND replacement."VoidedAtUtc" IS NULL
               AND replacement."DraftCanceledAtUtc" IS NULL
            WHERE source."Id" = @sourceAgreementId
              AND source."PortfolioId" = @portfolioId
              AND source."LeaseManagementId" = @leaseManagementId
              AND source."IssuedAtUtc" IS NOT NULL
              AND source."IssuedArtifactId" IS NOT NULL
              AND source."FullyExecutedAtUtc" IS NULL
              AND source."ExecutedArtifactId" IS NULL
              AND source."VoidedAtUtc" IS NOT NULL
              AND source."DraftCanceledAtUtc" IS NULL
        ),
        inserted AS (
            INSERT INTO "LeaseAgreementSigners"
                ("PortfolioId", "LeaseAgreementId", "LeaseManagementPartyId", "TenantId",
                 "SignerRole", "NameSnapshot", "EmailSnapshot", "SigningOrder", "IsRequired")
            SELECT source."PortfolioId", eligible.replacement_id, source."LeaseManagementPartyId",
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

    private const string CopyAddendumCorrectionChildrenSql = """
        WITH eligible AS MATERIALIZED (
            SELECT source."Id" AS source_id, correction."Id" AS correction_id
            FROM "LeaseAddenda" AS source
            INNER JOIN "LeaseAddenda" AS correction
                ON correction."Id" = @correctionAddendumId
               AND correction."PortfolioId" = @portfolioId
               AND correction."LeaseManagementId" = @leaseManagementId
               AND correction."SeriesPublicId" = source."SeriesPublicId"
               AND correction."ReplacesAddendumId" = source."Id"
               AND correction."IssuedAtUtc" IS NULL
               AND correction."IssuedArtifactId" IS NULL
               AND correction."FullyExecutedAtUtc" IS NULL
               AND correction."ExecutedArtifactId" IS NULL
               AND correction."VoidedAtUtc" IS NULL
               AND correction."DraftCanceledAtUtc" IS NULL
               AND correction."EffectiveFromOn" > source."EffectiveFromOn"
               AND (source."EffectiveThroughOn" IS NULL
                    OR correction."EffectiveFromOn" <= source."EffectiveThroughOn")
            WHERE source."Id" = @sourceAddendumId
              AND source."PortfolioId" = @portfolioId
              AND source."LeaseManagementId" = @leaseManagementId
              AND source."FullyExecutedAtUtc" IS NOT NULL
              AND source."ExecutedArtifactId" IS NOT NULL
              AND source."VoidedAtUtc" IS NULL
              AND source."DraftCanceledAtUtc" IS NULL
              AND source."SupersededByAddendumId" IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "LeaseAddendumSigners" AS existing
                  WHERE existing."PortfolioId" = @portfolioId
                    AND existing."LeaseAddendumId" = correction."Id")
              AND NOT EXISTS (
                  SELECT 1
                  FROM "LeaseAddendumFinancialEffects" AS existing
                  WHERE existing."PortfolioId" = @portfolioId
                    AND existing."LeaseAddendumId" = correction."Id")
            FOR UPDATE OF source, correction
        ),
        inserted_signers AS (
            INSERT INTO "LeaseAddendumSigners"
                ("PortfolioId", "LeaseAddendumId", "LeaseManagementPartyId", "TenantId",
                 "SignerRole", "NameSnapshot", "EmailSnapshot", "SigningOrder", "IsRequired")
            SELECT source."PortfolioId", eligible.correction_id, source."LeaseManagementPartyId",
                   source."TenantId", source."SignerRole", source."NameSnapshot",
                   source."EmailSnapshot", source."SigningOrder", source."IsRequired"
            FROM eligible
            INNER JOIN "LeaseAddendumSigners" AS source
                ON source."PortfolioId" = @portfolioId
               AND source."LeaseAddendumId" = eligible.source_id
            ORDER BY source."SigningOrder", source."Id"
            RETURNING "Id"
        ),
        inserted_effects AS (
            INSERT INTO "LeaseAddendumFinancialEffects"
                ("PortfolioId", "LeaseAddendumId", "EffectType", "Amount", "Currency",
                 "ChargeCode", "EffectiveFromOn", "EffectiveThroughOn", "DueOn", "Description")
            SELECT source."PortfolioId", eligible.correction_id, source."EffectType", source."Amount",
                   source."Currency", source."ChargeCode", source."EffectiveFromOn",
                   source."EffectiveThroughOn", source."DueOn", source."Description"
            FROM eligible
            INNER JOIN "LeaseAddendumFinancialEffects" AS source
                ON source."PortfolioId" = @portfolioId
               AND source."LeaseAddendumId" = eligible.source_id
            ORDER BY source."Id"
            RETURNING "Id"
        )
        SELECT EXISTS (SELECT 1 FROM eligible) AS "Eligible",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM inserted_signers), '[]'::jsonb)::text
                   AS "CreatedSignerIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM inserted_effects), '[]'::jsonb)::text
                   AS "CreatedFinancialEffectIdsJson"
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
        business_clock AS MATERIALIZED (
            SELECT rc_business_date(@portfolioId) AS business_date
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
            CROSS JOIN business_clock
            WHERE party."PortfolioId" = @portfolioId
              AND party."LeaseManagementId" = @leaseManagementId
              AND party."EffectiveFrom" <= business_clock.business_date
              AND (party."EffectiveThrough" IS NULL
                   OR party."EffectiveThrough" >= business_clock.business_date)
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
                    WHEN NOT parties_valid THEN @invalidParty
                    WHEN NOT accesses_valid THEN @invalidAccess
                    ELSE @returned
                END AS outcome
            FROM validation
        ),
        ended_parties AS (
            UPDATE "LeaseManagementParties" AS party
            SET "EffectiveThrough" = business_clock.business_date,
                "ChangeReason" = 'Party membership ended when possession returned.'
            FROM party_input AS input, decision, business_clock
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
            RETURNING access."Id", access."AccessContextId"
        ),
        revised_access_contexts AS (
            UPDATE "WorkspaceAccessContexts" AS context
            SET "AccessRevision" = context."AccessRevision" + 1,
                "UpdatedAtUtc" = @changedAt
            WHERE context."Id" IN (SELECT DISTINCT "AccessContextId" FROM revoked_access)
            RETURNING context."Id"
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
                COALESCE((
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
                      AND attempt."State" IN ('Prepared', 'Submitted', 'Unknown'))
                OR EXISTS (
                    SELECT 1
                    FROM "TenantLedgerAllocations" AS allocation
                    WHERE allocation."PortfolioId" = @portfolioId
                      AND allocation."TenantAccountId" = account."Id"
                      AND allocation."ReversesAllocationId" IS NULL
                      AND allocation."Amount" > 0
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "TenantLedgerAllocations" AS reversal
                          WHERE reversal."PortfolioId" = allocation."PortfolioId"
                            AND reversal."TenantAccountId" = allocation."TenantAccountId"
                            AND reversal."ReversesAllocationId" = allocation."Id"))
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
                "DraftCanceledByUserId" = @actorUserId,
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
            RETURNING access."Id", access."AccessContextId"
        ),
        revised_access_contexts AS (
            UPDATE "WorkspaceAccessContexts" AS context
            SET "AccessRevision" = context."AccessRevision" + 1,
                "UpdatedAtUtc" = @changedAt
            WHERE context."Id" IN (SELECT DISTINCT "AccessContextId" FROM revoked_access)
            RETURNING context."Id"
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
