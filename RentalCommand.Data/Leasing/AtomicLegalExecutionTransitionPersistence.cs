using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Leasing;

public static partial class AtomicLeaseMutationPersistence
{
    public static async Task<AtomicLegalExecutionTransitionResult> ExecuteLegalArtifactTransitionAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int leaseManagementId,
        int? leaseAgreementId,
        int? leaseAddendumId,
        int executedArtifactId,
        DateTime executedAtUtc,
        CancellationToken ct = default)
    {
        if ((leaseAgreementId is null) == (leaseAddendumId is null))
        {
            throw new ArgumentException("Exactly one legal parent is required.");
        }

        // Both the overlap exclusion and reciprocal-lineage validators must inspect the final
        // transaction state. This removes any dependency on EF/PostgreSQL statement ordering.
        await db.Database.ExecuteSqlRawAsync(
            "SET CONSTRAINTS \"EX_LeaseAgreements_GoverningPeriod\", " +
            "\"TR_LeaseAgreements_ValidateReciprocalLineage\", " +
            "\"TR_LeaseAddenda_ValidateReciprocalLineage\" DEFERRED",
            cancellationToken: ct);

        var parameters = new NpgsqlParameter[]
        {
            Integer("portfolioId", portfolioId),
            Integer("leaseManagementId", leaseManagementId),
            NullableInteger("leaseAgreementId", leaseAgreementId),
            NullableInteger("leaseAddendumId", leaseAddendumId),
            Integer("executedArtifactId", executedArtifactId),
            Timestamp("executedAt", executedAtUtc),
            Integer("applied", (int)AtomicLegalExecutionTransitionOutcome.Applied),
            Integer("targetChanged", (int)AtomicLegalExecutionTransitionOutcome.TargetChanged),
            Integer("successorConflict", (int)AtomicLegalExecutionTransitionOutcome.SuccessorConflict),
            Integer("invalidEffectiveDate", (int)AtomicLegalExecutionTransitionOutcome.InvalidEffectiveDate),
            Integer("renewalAddendumStateChanged",
                (int)AtomicLegalExecutionTransitionOutcome.RenewalAddendumStateChanged),
        };

        LegalExecutionTransitionRow row;
        using (var lease = RequireAuditScope(db, context).BeginInternalRawDmlBatch(
            new("LeaseAgreements", AtomicRawDmlOperation.Update),
            new("LeaseAddenda", AtomicRawDmlOperation.Update)))
        {
            row = await db.Database.SingleTopLevelResultAsync<LegalExecutionTransitionRow>(
                ExecuteLegalArtifactTransitionSql, parameters, ct);
        }

        if (row.Outcome == (int)AtomicLegalExecutionTransitionOutcome.Applied
            && leaseAgreementId.HasValue)
        {
            await ReconcileInitialSecurityDepositChargeAsync(
                db, context, portfolioId, leaseAgreementId.Value, executedAtUtc, ct);
        }

        return new(
            (AtomicLegalExecutionTransitionOutcome)row.Outcome,
            row.PredecessorId,
            DeserializeIds(row.SupersededAddendumIdsJson),
            DeserializeIds(row.ReissuedAddendumIdsJson));
    }

    public static async Task<int> ReconcileInitialSecurityDepositChargeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int leaseAgreementId,
        DateTime postedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseAgreementId);
        if (postedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Posted time must be UTC.", nameof(postedAtUtc));

        var charges = await ReconcileInitialSecurityDepositChargesForAgreementAsync(
            db, context, portfolioId, leaseAgreementId, postedAtUtc, ct);
        StageInitialSecurityDepositChargeAudits(db, context, charges);
        return charges.Count;
    }

    public static async Task<IReadOnlyList<AtomicInitialSecurityDepositCharge>>
        ReconcileCompletedNativeEsignInitialSecurityDepositChargesAsync(
            RentalCommandDbContext db,
            IAtomicCommandContext context,
            int batchSize,
            CancellationToken ct = default)
    {
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));

        using var lease = RequireAuditScope(db, context).BeginInternalRawDmlBatch(
            new("TenantLedgerEntries", AtomicRawDmlOperation.Insert),
            new("OutboxMessages", AtomicRawDmlOperation.Insert));
        var charges = await db.Database.SqlQuery<AtomicInitialSecurityDepositCharge>($"""
            WITH candidate AS MATERIALIZED (
                SELECT agreement."PortfolioId",
                       account."Id" AS tenant_account_id,
                       agreement."Id" AS lease_agreement_id,
                       agreement."SecurityDepositObligation" AS amount,
                       agreement."Currency",
                       account."CreatedByUserId",
                       rc_business_date(agreement."PortfolioId") AS business_date,
                       rc_effective_now_utc(agreement."PortfolioId") AS posted_at_utc,
                       'security-deposit:agreement:' || agreement."PublicId"::text AS business_key
                FROM "SignatureRequests" AS request
                JOIN "LeaseAgreements" AS agreement
                  ON agreement."Id" = request."LeaseAgreementId"
                 AND agreement."PortfolioId" = request."PortfolioId"
                JOIN "LeaseManagements" AS management
                  ON management."Id" = agreement."LeaseManagementId"
                 AND management."PortfolioId" = agreement."PortfolioId"
                JOIN "TenantAccounts" AS account
                  ON account."LeaseManagementId" = management."Id"
                 AND account."PortfolioId" = management."PortfolioId"
                WHERE request."Status" = 'Completed'
                  AND request."ExecutedArtifactId" IS NOT NULL
                  AND request."LeaseAgreementId" IS NOT NULL
                  AND request."LeaseAddendumId" IS NULL
                  AND agreement."ChangeType" = 'Initial'
                  AND agreement."ReplacesAgreementId" IS NULL
                  AND agreement."RenewsAgreementId" IS NULL
                  AND agreement."FullyExecutedAtUtc" IS NOT NULL
                  AND agreement."ExecutedArtifactId" IS NOT NULL
                  AND agreement."VoidedAtUtc" IS NULL
                  AND agreement."DraftCanceledAtUtc" IS NULL
                  AND agreement."SecurityDepositObligation" > 0
                  AND management."CanceledAtUtc" IS NULL
                  AND account."ClosedAtUtc" IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerEntries" AS existing
                      WHERE existing."TenantAccountId" = account."Id"
                        AND existing."BusinessKey" =
                            'security-deposit:agreement:' || agreement."PublicId"::text)
                ORDER BY request."CompletedAtUtc" NULLS LAST, request."Id"
                FOR UPDATE OF request SKIP LOCKED
                LIMIT {batchSize}
            ), inserted AS (
                INSERT INTO "TenantLedgerEntries" (
                    "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount",
                    "Currency", "EffectiveOn", "DueOn", "PostedAtUtc", "Description",
                    "BusinessKey", "LeaseAgreementId", "CreatedByUserId")
                SELECT candidate."PortfolioId", candidate.tenant_account_id,
                       'DepositCharge', 'Debit', candidate.amount,
                       candidate."Currency", candidate.business_date, candidate.business_date,
                       candidate.posted_at_utc,
                       'Security deposit due at lease execution',
                       candidate.business_key, candidate.lease_agreement_id,
                       candidate."CreatedByUserId"
                FROM candidate
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Id", "PortfolioId", "TenantAccountId", "LeaseAgreementId",
                          "Amount", "EffectiveOn", "DueOn", "BusinessKey",
                          "CreatedByUserId", "PostedAtUtc"
            ), outbox AS (
                INSERT INTO "OutboxMessages" (
                    "PortfolioId", "MessageType", "Payload", "IdempotencyKey", "AttemptCount",
                    "CreatedAtUtc", "NextAttemptAtUtc")
                SELECT inserted."PortfolioId",
                       'data-update',
                       jsonb_build_object(
                           'entityType', 'TenantLedgerEntry',
                           'entityId', inserted."Id",
                           'data', jsonb_build_object(
                               'TenantAccountId', inserted."TenantAccountId",
                               'LeaseAgreementId', inserted."LeaseAgreementId",
                               'EntryType', 'DepositCharge')),
                       'native-esign-deposit-charge:' || md5(
                           inserted."TenantAccountId"::text || chr(31)
                               || inserted."BusinessKey"),
                       0,
                       inserted."PostedAtUtc",
                       inserted."PostedAtUtc"
                FROM inserted
                ON CONFLICT ("IdempotencyKey") DO NOTHING
                RETURNING 1
            )
            SELECT inserted."Id" AS "LedgerEntryId",
                   inserted."PortfolioId",
                   inserted."TenantAccountId",
                   inserted."LeaseAgreementId",
                   inserted."Amount",
                   inserted."EffectiveOn",
                   inserted."DueOn",
                   inserted."BusinessKey",
                   inserted."CreatedByUserId",
                   inserted."PostedAtUtc"
            FROM inserted
            ORDER BY inserted."Id"
            """).ToListAsync(ct);
        StageInitialSecurityDepositChargeAudits(db, context, charges);
        return charges;
    }

    private static async Task<IReadOnlyList<AtomicInitialSecurityDepositCharge>>
        ReconcileInitialSecurityDepositChargesForAgreementAsync(
            RentalCommandDbContext db,
            IAtomicCommandContext context,
            int portfolioId,
            int leaseAgreementId,
            DateTime postedAtUtc,
            CancellationToken ct)
    {
        using var lease = RequireAuditScope(db, context).BeginInternalRawDmlBatch(
            new("TenantLedgerEntries", AtomicRawDmlOperation.Insert),
            new("OutboxMessages", AtomicRawDmlOperation.Insert));
        return await db.Database.SqlQuery<AtomicInitialSecurityDepositCharge>($"""
            WITH candidate AS MATERIALIZED (
                SELECT agreement."PortfolioId",
                       account."Id" AS tenant_account_id,
                       agreement."Id" AS lease_agreement_id,
                       agreement."SecurityDepositObligation" AS amount,
                       agreement."Currency",
                       account."CreatedByUserId",
                       rc_business_date(agreement."PortfolioId") AS business_date,
                       'security-deposit:agreement:' || agreement."PublicId"::text AS business_key
                FROM "LeaseAgreements" AS agreement
                JOIN "LeaseManagements" AS management
                  ON management."Id" = agreement."LeaseManagementId"
                 AND management."PortfolioId" = agreement."PortfolioId"
                JOIN "TenantAccounts" AS account
                  ON account."LeaseManagementId" = management."Id"
                 AND account."PortfolioId" = management."PortfolioId"
                WHERE agreement."PortfolioId" = {portfolioId}
                  AND agreement."Id" = {leaseAgreementId}
                  AND agreement."ChangeType" = 'Initial'
                  AND agreement."ReplacesAgreementId" IS NULL
                  AND agreement."RenewsAgreementId" IS NULL
                  AND agreement."FullyExecutedAtUtc" IS NOT NULL
                  AND agreement."ExecutedArtifactId" IS NOT NULL
                  AND agreement."VoidedAtUtc" IS NULL
                  AND agreement."DraftCanceledAtUtc" IS NULL
                  AND agreement."SecurityDepositObligation" > 0
                  AND management."CanceledAtUtc" IS NULL
                  AND account."ClosedAtUtc" IS NULL
            ), inserted AS (
                INSERT INTO "TenantLedgerEntries" (
                    "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount",
                    "Currency", "EffectiveOn", "DueOn", "PostedAtUtc", "Description",
                    "BusinessKey", "LeaseAgreementId", "CreatedByUserId")
                SELECT candidate."PortfolioId", candidate.tenant_account_id,
                       'DepositCharge', 'Debit', candidate.amount,
                       candidate."Currency", candidate.business_date, candidate.business_date,
                       {postedAtUtc},
                       'Security deposit due at lease execution',
                       candidate.business_key, candidate.lease_agreement_id,
                       candidate."CreatedByUserId"
                FROM candidate
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Id", "PortfolioId", "TenantAccountId", "LeaseAgreementId",
                          "Amount", "EffectiveOn", "DueOn", "BusinessKey",
                          "CreatedByUserId", "PostedAtUtc"
            ), outbox AS (
                INSERT INTO "OutboxMessages" (
                    "PortfolioId", "MessageType", "Payload", "IdempotencyKey", "AttemptCount",
                    "CreatedAtUtc", "NextAttemptAtUtc")
                SELECT inserted."PortfolioId",
                       'data-update',
                       jsonb_build_object(
                           'entityType', 'TenantLedgerEntry',
                           'entityId', inserted."Id",
                           'data', jsonb_build_object(
                               'TenantAccountId', inserted."TenantAccountId",
                               'LeaseAgreementId', inserted."LeaseAgreementId",
                               'EntryType', 'DepositCharge')),
                       'native-esign-deposit-charge:' || md5(
                           inserted."TenantAccountId"::text || chr(31)
                               || inserted."BusinessKey"),
                       0,
                       inserted."PostedAtUtc",
                       inserted."PostedAtUtc"
                FROM inserted
                ON CONFLICT ("IdempotencyKey") DO NOTHING
                RETURNING 1
            )
            SELECT inserted."Id" AS "LedgerEntryId",
                   inserted."PortfolioId",
                   inserted."TenantAccountId",
                   inserted."LeaseAgreementId",
                   inserted."Amount",
                   inserted."EffectiveOn",
                   inserted."DueOn",
                   inserted."BusinessKey",
                   inserted."CreatedByUserId",
                   inserted."PostedAtUtc"
            FROM inserted
            ORDER BY inserted."Id"
            """).ToListAsync(ct);
    }

    private static void StageInitialSecurityDepositChargeAudits(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        IReadOnlyList<AtomicInitialSecurityDepositCharge> charges)
    {
        foreach (var charge in charges)
        {
            RequireAuditScope(db, context).StageSemanticEvent(new AtomicSemanticAudit(
                charge.PortfolioId,
                nameof(TenantAccount),
                charge.TenantAccountId,
                AuditLogOperation.Updated,
                UserId: charge.CreatedByUserId,
                ActorLabel: "native-esign",
                NewValues: JsonSerializer.Serialize(new
                {
                    charge.LedgerEntryId,
                    charge.LeaseAgreementId,
                    EntryType = nameof(TenantLedgerEntryType.DepositCharge),
                    charge.Amount,
                    charge.EffectiveOn,
                    charge.DueOn,
                    charge.BusinessKey,
                }),
                ChangeReason: "Posted initial security-deposit charge at Agreement execution."),
                charge.PostedAtUtc);
        }
    }

    private static NpgsqlParameter NullableInteger(string name, int? value) =>
        new(name, NpgsqlDbType.Integer) { Value = value is null ? DBNull.Value : value.Value };

    private sealed class LegalExecutionTransitionRow
    {
        public int Outcome { get; set; }
        public int? PredecessorId { get; set; }
        public string SupersededAddendumIdsJson { get; set; } = "[]";
        public string ReissuedAddendumIdsJson { get; set; } = "[]";
    }

    internal const string ExecuteLegalArtifactTransitionSql = """
        WITH target_agreement AS MATERIALIZED (
            SELECT agreement.*,
                   COALESCE(agreement."ReplacesAgreementId", agreement."RenewsAgreementId")
                       AS predecessor_id
            FROM "LeaseAgreements" AS agreement
            WHERE @leaseAgreementId IS NOT NULL
              AND agreement."Id" = @leaseAgreementId
              AND agreement."PortfolioId" = @portfolioId
              AND agreement."LeaseManagementId" = @leaseManagementId
              AND agreement."IssuedAtUtc" IS NOT NULL
              AND agreement."IssuedArtifactId" IS NOT NULL
              AND agreement."FullyExecutedAtUtc" IS NULL
              AND agreement."ExecutedArtifactId" IS NULL
              AND agreement."VoidedAtUtc" IS NULL
              AND agreement."DraftCanceledAtUtc" IS NULL
            FOR UPDATE
        ),
        target_addendum AS MATERIALIZED (
            SELECT addendum.*, addendum."ReplacesAddendumId" AS predecessor_id
            FROM "LeaseAddenda" AS addendum
            WHERE @leaseAddendumId IS NOT NULL
              AND addendum."Id" = @leaseAddendumId
              AND addendum."PortfolioId" = @portfolioId
              AND addendum."LeaseManagementId" = @leaseManagementId
              AND addendum."IssuedAtUtc" IS NOT NULL
              AND addendum."IssuedArtifactId" IS NOT NULL
              AND addendum."FullyExecutedAtUtc" IS NULL
              AND addendum."ExecutedArtifactId" IS NULL
              AND addendum."VoidedAtUtc" IS NULL
              AND addendum."DraftCanceledAtUtc" IS NULL
            FOR UPDATE
        ),
        agreement_predecessor AS MATERIALIZED (
            SELECT predecessor.*
            FROM "LeaseAgreements" AS predecessor
            INNER JOIN target_agreement AS target ON target.predecessor_id = predecessor."Id"
            WHERE predecessor."PortfolioId" = @portfolioId
              AND predecessor."LeaseManagementId" = @leaseManagementId
              AND predecessor."FullyExecutedAtUtc" IS NOT NULL
              AND predecessor."ExecutedArtifactId" IS NOT NULL
              AND predecessor."VoidedAtUtc" IS NULL
              AND predecessor."DraftCanceledAtUtc" IS NULL
            FOR UPDATE
        ),
        addendum_predecessor AS MATERIALIZED (
            SELECT predecessor.*
            FROM "LeaseAddenda" AS predecessor
            INNER JOIN target_addendum AS target ON target.predecessor_id = predecessor."Id"
            WHERE predecessor."PortfolioId" = @portfolioId
              AND predecessor."LeaseManagementId" = @leaseManagementId
              AND predecessor."SeriesPublicId" = target."SeriesPublicId"
              AND predecessor."FullyExecutedAtUtc" IS NOT NULL
              AND predecessor."ExecutedArtifactId" IS NOT NULL
              AND predecessor."VoidedAtUtc" IS NULL
              AND predecessor."DraftCanceledAtUtc" IS NULL
            FOR UPDATE
        ),
        renewal_decisions AS MATERIALIZED (
            SELECT decision.*
            FROM "LeaseRenewalAddendumDecisions" AS decision
            INNER JOIN target_agreement AS renewal
              ON renewal."Id" = decision."RenewalAgreementId"
             AND renewal."ChangeType" IN ('Renewal', 'MonthToMonth')
            WHERE decision."PortfolioId" = @portfolioId
              AND decision."LeaseManagementId" = @leaseManagementId
        ),
        target_addendum_reissue_decision AS MATERIALIZED (
            SELECT target."Id", renewal."Id" AS renewal_id
            FROM target_addendum AS target
            INNER JOIN "LeaseRenewalAddendumDecisions" AS renewal_decision
              ON renewal_decision."ReplacementAddendumId" = target."Id"
             AND renewal_decision."PortfolioId" = target."PortfolioId"
             AND renewal_decision."LeaseManagementId" = target."LeaseManagementId"
             AND renewal_decision."SourceAddendumSeriesPublicId" = target."SeriesPublicId"
             AND renewal_decision."Decision" = 'ReissueAsAddendum'
            INNER JOIN "LeaseAgreements" AS renewal
              ON renewal."Id" = renewal_decision."RenewalAgreementId"
             AND renewal."Id" = target."BaseAgreementId"
             AND renewal."PortfolioId" = target."PortfolioId"
             AND renewal."LeaseManagementId" = target."LeaseManagementId"
             AND renewal."ChangeType" IN ('Renewal', 'MonthToMonth')
        ),
        -- A renewal reissue may finish signing before its base renewal. It is legally executed
        -- but remains Upcoming because the exact base Agreement is not executed yet. Do not run
        -- correction supersession here: renewal execution owns the source cutoff atomically.
        target_addendum_renewal_reissue AS MATERIALIZED (
            SELECT target."Id", source."Id" AS predecessor_id
            FROM target_addendum AS target
            INNER JOIN target_addendum_reissue_decision AS reissue
              ON reissue."Id" = target."Id"
            INNER JOIN "LeaseAgreements" AS renewal
              ON renewal."Id" = reissue.renewal_id
             AND renewal."PortfolioId" = target."PortfolioId"
             AND renewal."LeaseManagementId" = target."LeaseManagementId"
             AND renewal."IssuedAtUtc" IS NOT NULL
             AND renewal."IssuedArtifactId" IS NOT NULL
             AND renewal."VoidedAtUtc" IS NULL
             AND renewal."DraftCanceledAtUtc" IS NULL
            INNER JOIN "LeaseAddenda" AS source
              ON source."Id" = target."ReplacesAddendumId"
             AND source."PortfolioId" = target."PortfolioId"
             AND source."LeaseManagementId" = target."LeaseManagementId"
             AND source."SeriesPublicId" = target."SeriesPublicId"
             AND source."BaseAgreementId" = renewal."RenewsAgreementId"
             AND source."FullyExecutedAtUtc" IS NOT NULL
             AND source."ExecutedArtifactId" IS NOT NULL
             AND source."VoidedAtUtc" IS NULL
             AND source."DraftCanceledAtUtc" IS NULL
             AND source."SupersededEffectiveOn" IS NULL
             AND source."SupersededByAddendumId" IS NULL
             AND source."EffectiveFromOn" < renewal."GoverningFromOn"
             AND (source."EffectiveThroughOn" IS NULL
                  OR source."EffectiveThroughOn" >= renewal."GoverningFromOn")
             AND target."EffectiveFromOn" = renewal."GoverningFromOn"
        ),
        effective_source_addenda AS MATERIALIZED (
            SELECT source.*
            FROM target_agreement AS renewal
            INNER JOIN "LeaseAddenda" AS source
              ON source."PortfolioId" = renewal."PortfolioId"
             AND source."LeaseManagementId" = renewal."LeaseManagementId"
             AND source."BaseAgreementId" = renewal.predecessor_id
             AND source."FullyExecutedAtUtc" IS NOT NULL
             AND source."ExecutedArtifactId" IS NOT NULL
             AND source."VoidedAtUtc" IS NULL
             AND source."DraftCanceledAtUtc" IS NULL
             AND source."EffectiveFromOn" < renewal."GoverningFromOn"
             AND (source."EffectiveThroughOn" IS NULL
                  OR source."EffectiveThroughOn" >= renewal."GoverningFromOn")
             AND source."SupersededEffectiveOn" IS NULL
            -- Do not exclude a higher executed version: an exact renewal reissue is deliberately
            -- staged without superseding this source until the base renewal executes.
            WHERE renewal."ChangeType" IN ('Renewal', 'MonthToMonth')
            FOR UPDATE OF source
        ),
        renewal_addendum_validation AS MATERIALIZED (
            SELECT NOT EXISTS (
                       SELECT "SeriesPublicId" FROM effective_source_addenda
                       EXCEPT SELECT "SourceAddendumSeriesPublicId" FROM renewal_decisions)
                   AND NOT EXISTS (
                       SELECT "SourceAddendumSeriesPublicId" FROM renewal_decisions
                       EXCEPT SELECT "SeriesPublicId" FROM effective_source_addenda)
                   AND NOT EXISTS (
                       SELECT 1
                       FROM renewal_decisions AS decision
                       LEFT JOIN effective_source_addenda AS source
                         ON source."SeriesPublicId" = decision."SourceAddendumSeriesPublicId"
                       LEFT JOIN "LeaseAddenda" AS replacement
                         ON replacement."Id" = decision."ReplacementAddendumId"
                        AND replacement."PortfolioId" = decision."PortfolioId"
                        AND replacement."LeaseManagementId" = decision."LeaseManagementId"
                       CROSS JOIN target_agreement AS renewal
                       WHERE source."Id" IS NULL
                          OR (decision."Decision" = 'ReissueAsAddendum' AND (
                              replacement."Id" IS NULL
                              OR replacement."BaseAgreementId" IS DISTINCT FROM renewal."Id"
                              OR replacement."SeriesPublicId" IS DISTINCT FROM source."SeriesPublicId"
                              OR replacement."ReplacesAddendumId" IS DISTINCT FROM source."Id"
                              OR replacement."EffectiveFromOn" IS DISTINCT FROM renewal."GoverningFromOn"
                              OR replacement."IssuedAtUtc" IS NULL
                              OR replacement."IssuedArtifactId" IS NULL
                              OR replacement."FullyExecutedAtUtc" IS NULL
                              OR replacement."ExecutedArtifactId" IS NULL
                              OR replacement."SupersededEffectiveOn" IS NOT NULL
                              OR replacement."SupersededByAddendumId" IS NOT NULL
                              OR replacement."VoidedAtUtc" IS NOT NULL
                              OR replacement."DraftCanceledAtUtc" IS NOT NULL))
                          OR (decision."Decision" IN ('End', 'IncorporateIntoBase')
                              AND decision."ReplacementAddendumId" IS NOT NULL)) AS valid
        ),
        decision AS MATERIALIZED (
            SELECT CASE
                WHEN @leaseAgreementId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM target_agreement)
                    THEN @targetChanged
                WHEN @leaseAddendumId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM target_addendum)
                    THEN @targetChanged
                WHEN EXISTS (
                    SELECT 1 FROM target_agreement
                    WHERE predecessor_id IS NOT NULL)
                     AND NOT EXISTS (SELECT 1 FROM agreement_predecessor)
                    THEN @targetChanged
                WHEN EXISTS (
                    SELECT 1 FROM target_addendum
                    WHERE predecessor_id IS NOT NULL)
                     AND NOT EXISTS (SELECT 1 FROM addendum_predecessor)
                    THEN @targetChanged
                WHEN EXISTS (SELECT 1 FROM target_addendum_reissue_decision)
                     AND NOT EXISTS (SELECT 1 FROM target_addendum_renewal_reissue)
                    THEN @renewalAddendumStateChanged
                WHEN EXISTS (
                    SELECT 1
                    FROM agreement_predecessor AS predecessor
                    CROSS JOIN target_agreement AS successor
                    WHERE (predecessor."SupersededByAgreementId" IS NOT NULL
                           AND (predecessor."SupersededByAgreementId" <> successor."Id"
                                OR predecessor."SupersededEffectiveOn" <> successor."GoverningFromOn"))
                       OR EXISTS (
                           SELECT 1
                           FROM "LeaseAgreements" AS branch
                           WHERE branch."PortfolioId" = predecessor."PortfolioId"
                             AND branch."LeaseManagementId" = predecessor."LeaseManagementId"
                             AND branch."Id" <> successor."Id"
                             AND COALESCE(branch."ReplacesAgreementId", branch."RenewsAgreementId") = predecessor."Id"
                             AND branch."DraftCanceledAtUtc" IS NULL
                             AND (branch."VoidedAtUtc" IS NULL OR branch."FullyExecutedAtUtc" IS NOT NULL)))
                    THEN @successorConflict
                WHEN EXISTS (
                    SELECT 1
                    FROM addendum_predecessor AS predecessor
                    CROSS JOIN target_addendum AS successor
                    WHERE predecessor."SupersededByAddendumId" IS NOT NULL
                      AND (predecessor."SupersededByAddendumId" <> successor."Id"
                           OR predecessor."SupersededEffectiveOn" <> successor."EffectiveFromOn"))
                    THEN @successorConflict
                WHEN EXISTS (
                    SELECT 1
                    FROM agreement_predecessor AS predecessor
                    CROSS JOIN target_agreement AS successor
                    WHERE successor."GoverningFromOn" <= predecessor."GoverningFromOn")
                    OR EXISTS (
                    SELECT 1
                    FROM addendum_predecessor AS predecessor
                    CROSS JOIN target_addendum AS successor
                    WHERE successor."EffectiveFromOn" <= predecessor."EffectiveFromOn")
                    THEN @invalidEffectiveDate
                WHEN EXISTS (
                    SELECT 1 FROM target_agreement
                    WHERE "ChangeType" IN ('Renewal', 'MonthToMonth'))
                     AND NOT COALESCE((SELECT valid FROM renewal_addendum_validation), false)
                    THEN @renewalAddendumStateChanged
                ELSE @applied
            END AS outcome
        ),
        executed_agreement AS (
            UPDATE "LeaseAgreements" AS agreement
            SET "ExecutedArtifactId" = @executedArtifactId,
                "FullyExecutedAtUtc" = @executedAt,
                "UpdatedAtUtc" = @executedAt
            FROM target_agreement AS target, decision
            WHERE decision.outcome = @applied
              AND agreement."Id" = target."Id"
              AND agreement."PortfolioId" = @portfolioId
            RETURNING agreement."Id"
        ),
        superseded_agreement AS (
            UPDATE "LeaseAgreements" AS predecessor
            SET "SupersededEffectiveOn" = successor."GoverningFromOn",
                "SupersededByAgreementId" = successor."Id",
                "SupersessionRecordedAtUtc" = @executedAt,
                "UpdatedAtUtc" = @executedAt
            FROM target_agreement AS successor, agreement_predecessor AS locked, decision
            WHERE decision.outcome = @applied
              AND predecessor."Id" = locked."Id"
              AND predecessor."PortfolioId" = @portfolioId
              AND predecessor."SupersededEffectiveOn" IS NULL
            RETURNING predecessor."Id"
        ),
        executed_addendum AS (
            UPDATE "LeaseAddenda" AS addendum
            SET "ExecutedArtifactId" = @executedArtifactId,
                "FullyExecutedAtUtc" = @executedAt,
                "UpdatedAtUtc" = @executedAt
            FROM target_addendum AS target, decision
            WHERE decision.outcome = @applied
              AND addendum."Id" = target."Id"
              AND addendum."PortfolioId" = @portfolioId
            RETURNING addendum."Id"
        ),
        superseded_addendum_correction AS (
            UPDATE "LeaseAddenda" AS predecessor
            SET "SupersededEffectiveOn" = successor."EffectiveFromOn",
                "SupersededByAddendumId" = successor."Id",
                "SupersessionRecordedAtUtc" = @executedAt,
                "UpdatedAtUtc" = @executedAt
            FROM target_addendum AS successor, addendum_predecessor AS locked, decision
            WHERE decision.outcome = @applied
              AND predecessor."Id" = locked."Id"
              AND predecessor."PortfolioId" = @portfolioId
              AND predecessor."SupersededEffectiveOn" IS NULL
              AND NOT EXISTS (SELECT 1 FROM target_addendum_reissue_decision)
            RETURNING predecessor."Id", successor."Id" AS reissued_id
        ),
        superseded_addendum_renewal AS (
            UPDATE "LeaseAddenda" AS source
            SET "SupersededEffectiveOn" = renewal."GoverningFromOn",
                "SupersededByAddendumId" = CASE
                    WHEN renewal_decision."Decision" = 'ReissueAsAddendum'
                    THEN renewal_decision."ReplacementAddendumId"
                    ELSE NULL
                END,
                "SupersessionRecordedAtUtc" = @executedAt,
                "UpdatedAtUtc" = @executedAt
            FROM effective_source_addenda AS locked
            INNER JOIN renewal_decisions AS renewal_decision
              ON renewal_decision."SourceAddendumSeriesPublicId" = locked."SeriesPublicId"
            CROSS JOIN target_agreement AS renewal
            CROSS JOIN decision
            WHERE decision.outcome = @applied
              AND source."Id" = locked."Id"
              AND source."PortfolioId" = @portfolioId
            RETURNING source."Id", renewal_decision."ReplacementAddendumId" AS reissued_id
        )
        SELECT decision.outcome AS "Outcome",
               COALESCE(
                   (SELECT "Id" FROM agreement_predecessor),
                   (SELECT "Id" FROM addendum_predecessor)) AS "PredecessorId",
               COALESCE((
                   SELECT jsonb_agg(ids."Id" ORDER BY ids."Id")
                   FROM (
                       SELECT "Id" FROM superseded_addendum_correction
                       UNION ALL
                       SELECT "Id" FROM superseded_addendum_renewal
                   ) AS ids), '[]'::jsonb)::text AS "SupersededAddendumIdsJson",
               COALESCE((
                   SELECT jsonb_agg(ids.reissued_id ORDER BY ids.reissued_id)
                   FROM (
                       SELECT reissued_id FROM superseded_addendum_correction
                       UNION ALL
                       SELECT reissued_id FROM superseded_addendum_renewal
                   ) AS ids
                   WHERE ids.reissued_id IS NOT NULL), '[]'::jsonb)::text AS "ReissuedAddendumIdsJson"
        FROM decision
        """;
}
