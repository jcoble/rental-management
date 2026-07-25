using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Leasing;

internal sealed partial class AtomicLeaseMutationPersistence
{
    public async Task<AtomicTransferLeaseManagementMutationResult> TransferLeaseManagementAsync(
        TransferLeaseManagementCommand command,
        int destinationDocumentSourceVersionId,
        DateTime changedAtUtc,
        CancellationToken ct = default)
    {
        // The clean baseline names each overlap FK/exclusion DEFERRABLE. Deferring all deferrable
        // constraints is deliberate here: source possession closes and destination possession may
        // open in the same transaction, while final-state validation still runs before commit.
        await _db.Database.ExecuteSqlRawAsync(
            "SET CONSTRAINTS ALL DEFERRED",
            cancellationToken: ct);

        var parameters = new NpgsqlParameter[]
        {
            Integer("portfolioId", command.PortfolioId),
            Integer("sourceLeaseManagementId", command.SourceLeaseManagementId),
            Integer("sourceUnitId", command.SourceUnitId),
            Integer("destinationUnitId", command.DestinationUnitId),
            Integer("actorUserId", command.CreatedByUserId),
            Timestamp("changedAt", changedAtUtc),
            new("businessDate", NpgsqlDbType.Date) { Value = command.EffectiveOn },
            new("transferPublicId", NpgsqlDbType.Uuid) { Value = command.TransferPublicId },
            NullableTimestamp("plannedPossession", command.PlannedDestinationPossessionAtUtc),
            new("givePossessionNow", NpgsqlDbType.Boolean) { Value = command.GiveDestinationPossessionNow },
            NullableText("possessionExceptionReason", command.PossessionAgreementExceptionReason?.Trim()),
            Integer("documentTemplateId", command.DestinationDocumentTemplateId),
            Integer("documentSourceVersionId", destinationDocumentSourceVersionId),
            new("carryTenantBalance", NpgsqlDbType.Boolean) { Value = command.CarryTenantBalance },
            new("carrySecurityDeposit", NpgsqlDbType.Boolean) { Value = command.CarrySecurityDeposit },
            Text("transferReason", command.TransferReason.Trim()),
            Text("businessKey", command.DeliveryIdempotencyKey),
            Integer("transferred", (int)TransferLeaseManagementOutcome.Transferred),
            Integer("alreadyTransferred", (int)TransferLeaseManagementOutcome.AlreadyTransferred),
            Integer("sourceNotOpen", (int)TransferLeaseManagementOutcome.SourcePossessionNotOpen),
            Integer("destinationUnavailable", (int)TransferLeaseManagementOutcome.DestinationUnavailable),
            Integer("agreementRequired", (int)TransferLeaseManagementOutcome.GoverningAgreementRequired),
            Integer("accountNotOpen", (int)TransferLeaseManagementOutcome.TenantAccountNotOpen),
            Integer("invalidTemplate", (int)TransferLeaseManagementOutcome.InvalidTemplate),
            Integer("invalidHousehold", (int)TransferLeaseManagementOutcome.InvalidHousehold),
            Integer("invalidFinancial", (int)TransferLeaseManagementOutcome.InvalidFinancialState),
        };

        using var lease = _auditScope.BeginInternalRawDmlBatch(
            new("LeaseManagements", AtomicRawDmlOperation.Update),
            new("LeaseManagements", AtomicRawDmlOperation.Insert),
            new("LeaseManagementParties", AtomicRawDmlOperation.Update),
            new("LeaseManagementParties", AtomicRawDmlOperation.Insert),
            new("TenantAccounts", AtomicRawDmlOperation.Insert),
            new("LeaseAgreements", AtomicRawDmlOperation.Insert),
            new("LeaseAgreementSigners", AtomicRawDmlOperation.Insert),
            new("TenantLedgerEntries", AtomicRawDmlOperation.Insert),
            new("TenantLedgerAllocations", AtomicRawDmlOperation.Insert),
            new("SecurityDepositAccounts", AtomicRawDmlOperation.Insert),
            new("SecurityDepositEntries", AtomicRawDmlOperation.Insert),
            new("TenantUserAccesses", AtomicRawDmlOperation.Update),
            new("TenantUserAccesses", AtomicRawDmlOperation.Insert),
            new("WorkspaceAccessContexts", AtomicRawDmlOperation.Update),
            new("UnitOperationalPeriods", AtomicRawDmlOperation.Insert));

        var row = await _db.Database.SingleTopLevelResultAsync<TransferLeaseManagementRow>(
            TransferSql, parameters, ct);
        return new(
            (TransferLeaseManagementOutcome)row.Outcome,
            row.TransferPublicId,
            row.SourceTenantAccountId,
            row.SourceSecurityDepositAccountId,
            row.DestinationLeaseManagementId,
            row.DestinationTenantAccountId,
            row.DestinationAgreementId,
            row.DestinationSecurityDepositAccountId,
            row.TurnoverPeriodId,
            row.SourcePossessionReturnedAtUtc,
            row.DestinationPossessionGivenAtUtc,
            row.CarriedTenantBalance,
            row.CarriedSecurityDeposit,
            DeserializeIds(row.EndedSourcePartyIdsJson),
            DeserializeIds(row.DestinationPartyIdsJson),
            DeserializeIds(row.DestinationSignerIdsJson),
            DeserializeIds(row.RevokedSourceAccessIdsJson),
            DeserializeIds(row.DestinationAccessIdsJson),
            DeserializeLongIds(row.TenantLedgerEntryIdsJson),
            DeserializeLongIds(row.SecurityDepositEntryIdsJson));
    }

    private static NpgsqlParameter NullableTimestamp(string name, DateTime? value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value is null ? DBNull.Value : value };

    private static long[] DeserializeLongIds(string json) =>
        JsonSerializer.Deserialize<long[]>(json)
        ?? throw new InvalidOperationException("Lease transfer returned invalid long-id JSON.");

    private sealed class TransferLeaseManagementRow
    {
        public int Outcome { get; set; }
        public Guid TransferPublicId { get; set; }
        public int SourceTenantAccountId { get; set; }
        public int? SourceSecurityDepositAccountId { get; set; }
        public int DestinationLeaseManagementId { get; set; }
        public int DestinationTenantAccountId { get; set; }
        public int DestinationAgreementId { get; set; }
        public int? DestinationSecurityDepositAccountId { get; set; }
        public int TurnoverPeriodId { get; set; }
        public DateTime? SourcePossessionReturnedAtUtc { get; set; }
        public DateTime? DestinationPossessionGivenAtUtc { get; set; }
        public decimal CarriedTenantBalance { get; set; }
        public decimal CarriedSecurityDeposit { get; set; }
        public string EndedSourcePartyIdsJson { get; set; } = "[]";
        public string DestinationPartyIdsJson { get; set; } = "[]";
        public string DestinationSignerIdsJson { get; set; } = "[]";
        public string RevokedSourceAccessIdsJson { get; set; } = "[]";
        public string DestinationAccessIdsJson { get; set; } = "[]";
        public string TenantLedgerEntryIdsJson { get; set; } = "[]";
        public string SecurityDepositEntryIdsJson { get; set; } = "[]";
    }

    private const string TransferSql = """
        WITH locked_units AS MATERIALIZED (
            SELECT unit."Id", unit."PropertyId", unit."PortfolioId"
            FROM "Units" AS unit
            WHERE unit."PortfolioId" = @portfolioId
              AND unit."DeletedAt" IS NULL
              AND unit."Id" IN (@sourceUnitId, @destinationUnitId)
            ORDER BY unit."Id"
            FOR UPDATE
        ),
        source_relationship AS MATERIALIZED (
            SELECT relationship.*
            FROM "LeaseManagements" AS relationship
            WHERE relationship."Id" = @sourceLeaseManagementId
              AND relationship."PortfolioId" = @portfolioId
              AND relationship."UnitId" = @sourceUnitId
            FOR UPDATE
        ),
        existing_destination AS MATERIALIZED (
            SELECT destination."Id", destination."TransferPublicId",
                   account."Id" AS tenant_account_id,
                   agreement."Id" AS agreement_id,
                   deposit."Id" AS deposit_account_id,
                   turnover."Id" AS turnover_id,
                   destination."PossessionGivenAtUtc"
            FROM "LeaseManagements" AS destination
            LEFT JOIN "TenantAccounts" AS account
              ON account."PortfolioId" = destination."PortfolioId"
             AND account."LeaseManagementId" = destination."Id"
            LEFT JOIN "LeaseAgreements" AS agreement
              ON agreement."PortfolioId" = destination."PortfolioId"
             AND agreement."LeaseManagementId" = destination."Id"
             AND agreement."VersionNumber" = 1
            LEFT JOIN "SecurityDepositAccounts" AS deposit
              ON deposit."PortfolioId" = account."PortfolioId"
             AND deposit."TenantAccountId" = account."Id"
            LEFT JOIN "UnitOperationalPeriods" AS turnover
              ON turnover."PortfolioId" = @portfolioId
             AND turnover."SourceLeaseManagementId" = @sourceLeaseManagementId
             AND turnover."Type" = 'Turnover'
             AND turnover."EndedAtUtc" IS NULL
            WHERE destination."PortfolioId" = @portfolioId
              AND destination."TransferredFromLeaseManagementId" = @sourceLeaseManagementId
            LIMIT 1
        ),
        source_account AS MATERIALIZED (
            SELECT account.*, balance."ReceivableBalance", balance."UnappliedCredit"
            FROM "TenantAccounts" AS account
            LEFT JOIN "vw_tenant_account_balances" AS balance
              ON balance."PortfolioId" = account."PortfolioId"
             AND balance."TenantAccountId" = account."Id"
            WHERE account."PortfolioId" = @portfolioId
              AND account."LeaseManagementId" = @sourceLeaseManagementId
        ),
        source_deposit AS MATERIALIZED (
            SELECT deposit.*, balance."HeldBalance"
            FROM "SecurityDepositAccounts" AS deposit
            LEFT JOIN "vw_security_deposit_balances" AS balance
              ON balance."PortfolioId" = deposit."PortfolioId"
             AND balance."SecurityDepositAccountId" = deposit."Id"
            WHERE deposit."PortfolioId" = @portfolioId
              AND deposit."TenantAccountId" = (SELECT "Id" FROM source_account)
        ),
        source_agreement AS MATERIALIZED (
            SELECT agreement.*
            FROM "LeaseAgreements" AS agreement
            INNER JOIN "vw_lease_agreement_status" AS status
              ON status."PortfolioId" = agreement."PortfolioId"
             AND status."LeaseManagementId" = agreement."LeaseManagementId"
             AND status."AgreementId" = agreement."Id"
             AND status."IsGoverning"
            WHERE agreement."PortfolioId" = @portfolioId
              AND agreement."LeaseManagementId" = @sourceLeaseManagementId
              AND agreement."FullyExecutedAtUtc" IS NOT NULL
              AND agreement."ExecutedArtifactId" IS NOT NULL
              AND agreement."VoidedAtUtc" IS NULL
              AND agreement."DraftCanceledAtUtc" IS NULL
            ORDER BY agreement."VersionNumber" DESC
            LIMIT 1
        ),
        destination_unit AS MATERIALIZED (
            SELECT unit.*
            FROM "Units" AS unit
            WHERE unit."PortfolioId" = @portfolioId
              AND unit."Id" = @destinationUnitId
              AND unit."DeletedAt" IS NULL
        ),
        current_parties AS MATERIALIZED (
            SELECT party.*
            FROM "LeaseManagementParties" AS party
            WHERE party."PortfolioId" = @portfolioId
              AND party."LeaseManagementId" = @sourceLeaseManagementId
              AND party."EffectiveFrom" <= @businessDate
              AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= @businessDate)
        ),
        source_signers AS MATERIALIZED (
            SELECT signer.*
            FROM "LeaseAgreementSigners" AS signer
            WHERE signer."PortfolioId" = @portfolioId
              AND signer."LeaseAgreementId" = (SELECT "Id" FROM source_agreement)
        ),
        active_access AS MATERIALIZED (
            SELECT access.*, party."TenantId", party."Role"
            FROM "TenantUserAccesses" AS access
            INNER JOIN current_parties AS party ON party."Id" = access."LeaseManagementPartyId"
            WHERE access."PortfolioId" = @portfolioId
              AND access."RevokedAtUtc" IS NULL
        ),
        validation AS MATERIALIZED (
            SELECT CASE
                WHEN EXISTS (SELECT 1 FROM existing_destination) THEN @alreadyTransferred
                WHEN (SELECT count(*) FROM locked_units) <> 2
                  OR @sourceUnitId = @destinationUnitId
                  OR NOT EXISTS (SELECT 1 FROM destination_unit)
                  OR EXISTS (
                      SELECT 1 FROM "LeaseManagements" AS occupied
                      WHERE occupied."PortfolioId" = @portfolioId
                        AND occupied."UnitId" = @destinationUnitId
                        AND occupied."PossessionGivenAtUtc" IS NOT NULL
                        AND occupied."PossessionReturnedAtUtc" IS NULL
                        AND occupied."CanceledAtUtc" IS NULL)
                  OR EXISTS (
                      SELECT 1 FROM "UnitOperationalPeriods" AS period
                      WHERE period."PortfolioId" = @portfolioId
                        AND period."UnitId" = @destinationUnitId
                        AND period."EndedAtUtc" IS NULL)
                  THEN @destinationUnavailable
                WHEN NOT EXISTS (SELECT 1 FROM source_relationship)
                  OR (SELECT "PossessionGivenAtUtc" FROM source_relationship) IS NULL
                  OR (SELECT "PossessionReturnedAtUtc" FROM source_relationship) IS NOT NULL
                  OR (SELECT "CanceledAtUtc" FROM source_relationship) IS NOT NULL
                  THEN @sourceNotOpen
                WHEN NOT EXISTS (SELECT 1 FROM source_agreement) THEN @agreementRequired
                WHEN NOT EXISTS (SELECT 1 FROM source_account)
                  OR (SELECT "ClosedAtUtc" FROM source_account) IS NOT NULL
                  THEN @accountNotOpen
                WHEN NOT EXISTS (
                    SELECT 1 FROM "DocumentTemplates" AS template
                    WHERE template."Id" = @documentTemplateId
                      AND template."PortfolioId" = @portfolioId
                      AND template."Kind" = 'Lease'
                      AND template."Status" = 'Active'
                      AND template."ArchivedAtUtc" IS NULL
                      AND (template."PropertyId" IS NULL
                           OR template."PropertyId" = (SELECT "PropertyId" FROM destination_unit)))
                  THEN @invalidTemplate
                WHEN rc_business_date(@portfolioId) <> @businessDate
                  OR ((SELECT "TermType" FROM source_agreement) = 'FixedTerm'
                      AND (SELECT "TermEndOn" FROM source_agreement) < @businessDate)
                  OR NOT EXISTS (SELECT 1 FROM current_parties WHERE "Role" = 'PrimaryTenant')
                  OR (SELECT count(*) FROM current_parties WHERE "Role" = 'PrimaryTenant') <> 1
                  OR NOT EXISTS (SELECT 1 FROM source_signers WHERE "IsRequired")
                  OR EXISTS (
                      SELECT 1 FROM source_signers AS signer
                      WHERE signer."LeaseManagementPartyId" IS NOT NULL
                        AND NOT EXISTS (
                            SELECT 1 FROM current_parties AS party
                            WHERE party."Id" = signer."LeaseManagementPartyId"))
                  THEN @invalidHousehold
                WHEN EXISTS (
                    SELECT 1 FROM "TenantPaymentAttempts" AS attempt
                    WHERE attempt."PortfolioId" = @portfolioId
                      AND attempt."TenantAccountId" = (SELECT "Id" FROM source_account)
                      AND attempt."State" IN ('Prepared','Submitted','Unknown'))
                  OR EXISTS (
                    SELECT 1 FROM "TenantAutopayEnrollments" AS enrollment
                    WHERE enrollment."PortfolioId" = @portfolioId
                      AND enrollment."TenantAccountId" = (SELECT "Id" FROM source_account)
                      AND enrollment."CanceledAtUtc" IS NULL)
                  OR (@carrySecurityDeposit AND COALESCE(
                      (SELECT "HeldBalance" FROM source_deposit), 0::numeric) < 0)
                  OR (@carryTenantBalance AND (
                      (COALESCE((SELECT "ReceivableBalance" FROM source_account), 0::numeric) > 0
                       AND COALESCE((SELECT "UnappliedCredit" FROM source_account), 0::numeric) > 0)
                      OR (COALESCE((SELECT "ReceivableBalance" FROM source_account), 0::numeric) <= 0
                          AND EXISTS (
                              SELECT 1 FROM "vw_tenant_charge_balances" AS charge
                              WHERE charge."PortfolioId" = @portfolioId
                                AND charge."TenantAccountId" = (SELECT "Id" FROM source_account)
                                AND charge."OpenAmount" > 0))))
                  THEN @invalidFinancial
                ELSE @transferred
            END AS outcome
        ),
        source_updated AS (
            UPDATE "LeaseManagements" AS relationship
            SET "PossessionReturnedAtUtc" = @changedAt,
                "PlannedMoveOutAtUtc" = COALESCE(relationship."PlannedMoveOutAtUtc", @changedAt),
                "UpdatedAtUtc" = @changedAt,
                "RowVersion" = gen_random_uuid()
            FROM validation
            WHERE validation.outcome = @transferred
              AND relationship."Id" = @sourceLeaseManagementId
              AND relationship."PortfolioId" = @portfolioId
            RETURNING relationship.*
        ),
        source_parties_ended AS (
            UPDATE "LeaseManagementParties" AS party
            SET "EffectiveThrough" = @businessDate
            FROM current_parties, validation
            WHERE validation.outcome = @transferred
              AND party."Id" = current_parties."Id"
              AND party."PortfolioId" = @portfolioId
            RETURNING party."Id"
        ),
        destination_relationship AS (
            INSERT INTO "LeaseManagements"
                ("PublicId", "PortfolioId", "PropertyId", "UnitId",
                 "TransferredFromLeaseManagementId", "TransferPublicId", "TransferredAtUtc", "TransferReason",
                 "RelationshipNumber", "PlannedPossessionAtUtc", "PossessionGivenAtUtc",
                 "PossessionAgreementExceptionReason", "PossessionAgreementExceptionAuthorizedByUserId",
                 "EndingDisposition", "CreatedAtUtc", "CreatedByUserId", "UpdatedAtUtc", "RowVersion")
            SELECT gen_random_uuid(), @portfolioId, destination_unit."PropertyId", @destinationUnitId,
                   @sourceLeaseManagementId, @transferPublicId, @changedAt, @transferReason,
                   'LM-XFER-' || @sourceLeaseManagementId::text || '-' || @destinationUnitId::text,
                   @plannedPossession,
                   CASE WHEN @givePossessionNow THEN @changedAt ELSE NULL END,
                   CASE WHEN @givePossessionNow THEN @possessionExceptionReason ELSE NULL END,
                   CASE WHEN @givePossessionNow THEN @actorUserId ELSE NULL END,
                   'Undecided', @changedAt, @actorUserId, @changedAt, gen_random_uuid()
            FROM destination_unit, validation
            WHERE validation.outcome = @transferred
              AND EXISTS (SELECT 1 FROM source_updated)
            RETURNING *
        ),
        destination_account AS (
            INSERT INTO "TenantAccounts"
                ("PublicId", "PortfolioId", "LeaseManagementId", "AccountNumber", "Currency",
                 "OpenedAtUtc", "CreatedAtUtc", "CreatedByUserId")
            SELECT gen_random_uuid(), @portfolioId, destination."Id",
                   'TA-XFER-' || @sourceLeaseManagementId::text || '-' || @destinationUnitId::text,
                   source_account."Currency", @changedAt, @changedAt, @actorUserId
            FROM destination_relationship AS destination, source_account
            RETURNING *
        ),
        destination_agreement AS (
            INSERT INTO "LeaseAgreements"
                ("PublicId", "PortfolioId", "LeaseManagementId", "VersionNumber", "AgreementNumber",
                 "ChangeType", "TransferredFromAgreementId", "TermType", "TermStartOn", "TermEndOn",
                 "GoverningFromOn", "BaseRentAmount", "RentDueDay", "SecurityDepositObligation",
                 "LateFeeAmount", "GracePeriodDays", "Currency", "TermsSchemaVersion", "TermsPayload",
                 "DocumentSourceVersionId", "CreatedAtUtc", "CreatedByUserId",
                 "UpdatedAtUtc", "DraftRevision")
            SELECT gen_random_uuid(), @portfolioId, destination."Id", 1,
                   'AGR-XFER-' || @sourceLeaseManagementId::text || '-' || @destinationUnitId::text || '-V1',
                   'Transfer', source."Id", source."TermType", @businessDate, source."TermEndOn",
                   @businessDate, source."BaseRentAmount", source."RentDueDay",
                   source."SecurityDepositObligation", source."LateFeeAmount", source."GracePeriodDays",
                   source."Currency", source."TermsSchemaVersion", source."TermsPayload",
                   @documentSourceVersionId, @changedAt, @actorUserId,
                   @changedAt, 1
            FROM destination_relationship AS destination, source_agreement AS source
            RETURNING *
        ),
        destination_parties AS (
            INSERT INTO "LeaseManagementParties"
                ("PortfolioId", "LeaseManagementId", "TenantId", "Role", "EffectiveFrom",
                 "GuarantorLegalNoticeEligible", "ChangeReason", "CreatedAtUtc", "CreatedByUserId")
            SELECT @portfolioId, destination."Id", party."TenantId", party."Role", @businessDate,
                   party."GuarantorLegalNoticeEligible", @transferReason, @changedAt, @actorUserId
            FROM current_parties AS party
            CROSS JOIN destination_relationship AS destination
            ORDER BY party."Id"
            RETURNING *
        ),
        destination_signers AS (
            INSERT INTO "LeaseAgreementSigners"
                ("PortfolioId", "LeaseAgreementId", "LeaseManagementPartyId", "TenantId",
                 "SignerRole", "NameSnapshot", "EmailSnapshot", "SigningOrder", "IsRequired")
            SELECT @portfolioId, agreement."Id", destination_party."Id", signer."TenantId",
                   signer."SignerRole", signer."NameSnapshot", signer."EmailSnapshot",
                   signer."SigningOrder", signer."IsRequired"
            FROM source_signers AS signer
            CROSS JOIN destination_agreement AS agreement
            LEFT JOIN current_parties AS source_party
              ON source_party."Id" = signer."LeaseManagementPartyId"
            LEFT JOIN destination_parties AS destination_party
              ON destination_party."TenantId" = source_party."TenantId"
             AND destination_party."Role" = source_party."Role"
            ORDER BY signer."SigningOrder"
            RETURNING "Id"
        ),
        tenant_ledger_entries AS (
            INSERT INTO "TenantLedgerEntries"
                ("PublicId", "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount",
                 "Currency", "EffectiveOn", "DueOn", "PostedAtUtc", "Description", "BusinessKey",
                 "TransferPublicId", "LeaseAgreementId", "CreatedByUserId")
            SELECT gen_random_uuid(), @portfolioId, source_account."Id", 'TransferOut',
                   CASE WHEN source_account."ReceivableBalance" > 0 THEN 'Credit' ELSE 'Debit' END,
                   abs(source_account."ReceivableBalance"), source_account."Currency", @businessDate,
                   CASE WHEN source_account."ReceivableBalance" < 0 THEN @businessDate ELSE NULL END,
                   @changedAt, 'Balance transferred to destination Unit account.',
                   @businessKey || ':ledger-out', @transferPublicId, source_agreement."Id", @actorUserId
            FROM source_account, source_agreement
            WHERE @carryTenantBalance AND source_account."ReceivableBalance" <> 0
            UNION ALL
            SELECT gen_random_uuid(), @portfolioId, destination_account."Id", 'TransferIn',
                   CASE WHEN source_account."ReceivableBalance" > 0 THEN 'Debit' ELSE 'Credit' END,
                   abs(source_account."ReceivableBalance"), destination_account."Currency", @businessDate,
                   CASE WHEN source_account."ReceivableBalance" > 0 THEN
                       COALESCE((SELECT min(charge."DueOn")
                                 FROM "vw_tenant_charge_balances" AS charge
                                 WHERE charge."PortfolioId" = @portfolioId
                                   AND charge."TenantAccountId" = source_account."Id"
                                   AND charge."OpenAmount" > 0), @businessDate)
                       ELSE NULL END,
                   @changedAt, 'Balance carried from source Unit account.',
                   @businessKey || ':ledger-in', @transferPublicId, destination_agreement."Id", @actorUserId
            FROM destination_account, destination_agreement, source_account
            WHERE @carryTenantBalance AND source_account."ReceivableBalance" <> 0
            RETURNING "Id", "TenantAccountId", "EntryType", "Direction", "Amount"
        ),
        open_source_debits AS MATERIALIZED (
            SELECT charge."TenantLedgerEntryId", charge."OpenAmount",
                   COALESCE(sum(charge."OpenAmount") OVER (
                       ORDER BY charge."DueOn" NULLS LAST, charge."EffectiveOn", charge."TenantLedgerEntryId"
                       ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0::numeric) AS prior_amount
            FROM "vw_tenant_charge_balances" AS charge
            WHERE charge."PortfolioId" = @portfolioId
              AND charge."TenantAccountId" = (SELECT "Id" FROM source_account)
              AND charge."OpenAmount" > 0
        ),
        source_credit_positions AS MATERIALIZED (
            SELECT entry."Id" AS credit_entry_id,
                   GREATEST(
                     entry."Amount"
                       - COALESCE((SELECT sum(reversal."Amount")
                                   FROM "TenantLedgerEntries" AS reversal
                                   WHERE reversal."PortfolioId" = entry."PortfolioId"
                                     AND reversal."TenantAccountId" = entry."TenantAccountId"
                                     AND reversal."EntryType" = 'Reversal'
                                     AND reversal."ReversesEntryId" = entry."Id"), 0::numeric)
                       - COALESCE((SELECT sum(allocation."Amount")
                                   FROM "TenantLedgerAllocations" AS allocation
                                   WHERE allocation."PortfolioId" = entry."PortfolioId"
                                     AND allocation."TenantAccountId" = entry."TenantAccountId"
                                     AND allocation."CreditEntryId" = entry."Id"), 0::numeric),
                     0::numeric) AS available_amount
            FROM "TenantLedgerEntries" AS entry
            WHERE entry."PortfolioId" = @portfolioId
              AND entry."TenantAccountId" = (SELECT "Id" FROM source_account)
              AND entry."Direction" = 'Credit'
              AND entry."EntryType" <> 'Reversal'
        ),
        open_source_credits AS MATERIALIZED (
            SELECT credit_entry_id, available_amount,
                   COALESCE(sum(available_amount) OVER (
                       ORDER BY credit_entry_id
                       ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0::numeric) AS prior_amount
            FROM source_credit_positions
            WHERE available_amount > 0
        ),
        tenant_ledger_allocations AS (
            INSERT INTO "TenantLedgerAllocations"
                ("PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId", "Amount",
                 "AllocatedAtUtc", "BusinessKey", "CreatedByUserId")
            SELECT @portfolioId, source_account."Id", debit."TenantLedgerEntryId", transfer_out."Id",
                   LEAST(debit."OpenAmount",
                         GREATEST(source_account."ReceivableBalance" - debit.prior_amount, 0::numeric)),
                   @changedAt, @businessKey || ':alloc-debit:' || debit."TenantLedgerEntryId"::text,
                   @actorUserId
            FROM source_account
            CROSS JOIN open_source_debits AS debit
            CROSS JOIN LATERAL (
                SELECT entry."Id"
                FROM tenant_ledger_entries AS entry
                WHERE entry."EntryType" = 'TransferOut' AND entry."Direction" = 'Credit'
            ) AS transfer_out
            WHERE source_account."ReceivableBalance" > 0
              AND debit.prior_amount < source_account."ReceivableBalance"
            UNION ALL
            SELECT @portfolioId, source_account."Id", transfer_out."Id", credit.credit_entry_id,
                   LEAST(credit.available_amount,
                         GREATEST(abs(source_account."ReceivableBalance") - credit.prior_amount, 0::numeric)),
                   @changedAt, @businessKey || ':alloc-credit:' || credit.credit_entry_id::text,
                   @actorUserId
            FROM source_account
            CROSS JOIN open_source_credits AS credit
            CROSS JOIN LATERAL (
                SELECT entry."Id"
                FROM tenant_ledger_entries AS entry
                WHERE entry."EntryType" = 'TransferOut' AND entry."Direction" = 'Debit'
            ) AS transfer_out
            WHERE source_account."ReceivableBalance" < 0
              AND credit.prior_amount < abs(source_account."ReceivableBalance")
            RETURNING "Id"
        ),
        destination_deposit_account AS (
            INSERT INTO "SecurityDepositAccounts"
                ("PortfolioId", "TenantAccountId", "OriginatingAgreementId", "Currency",
                 "CreatedAtUtc", "CreatedByUserId")
            SELECT @portfolioId, account."Id", agreement."Id", account."Currency", @changedAt, @actorUserId
            FROM destination_account AS account
            CROSS JOIN destination_agreement AS agreement
            WHERE @carrySecurityDeposit
              AND EXISTS (SELECT 1 FROM source_deposit)
            RETURNING *
        ),
        security_deposit_entries AS (
            INSERT INTO "SecurityDepositEntries"
                ("PublicId", "PortfolioId", "SecurityDepositAccountId", "EntryType", "Direction", "Amount",
                 "Currency", "EffectiveOn", "PostedAtUtc", "BusinessKey", "Description",
                 "TransferPublicId", "LeaseAgreementId", "CreatedByUserId")
            SELECT gen_random_uuid(), @portfolioId, source_deposit."Id", 'TransferOut', 'Decrease',
                   source_deposit."HeldBalance", source_deposit."Currency", @businessDate, @changedAt,
                   @businessKey || ':deposit-out', 'Security deposit transferred to destination Unit account.',
                   @transferPublicId, source_agreement."Id", @actorUserId
            FROM source_deposit, source_agreement
            WHERE @carrySecurityDeposit AND source_deposit."HeldBalance" > 0
            UNION ALL
            SELECT gen_random_uuid(), @portfolioId, destination_deposit."Id", 'TransferIn', 'Increase',
                   source_deposit."HeldBalance", destination_deposit."Currency", @businessDate, @changedAt,
                   @businessKey || ':deposit-in', 'Security deposit carried from source Unit account.',
                   @transferPublicId, destination_agreement."Id", @actorUserId
            FROM destination_deposit_account AS destination_deposit, source_deposit, destination_agreement
            WHERE @carrySecurityDeposit AND source_deposit."HeldBalance" > 0
            RETURNING "Id"
        ),
        revoked_access AS (
            UPDATE "TenantUserAccesses" AS access
            SET "RevokedAtUtc" = @changedAt, "RevokedByUserId" = @actorUserId,
                "Reason" = 'Unit transfer: access continued on destination relationship.'
            FROM active_access
            WHERE access."Id" = active_access."Id"
            RETURNING access."Id", access."AccessContextId", access."ApplicationUserId",
                      access."LeaseManagementPartyId"
        ),
        destination_access AS (
            INSERT INTO "TenantUserAccesses"
                ("PublicId", "PortfolioId", "AccessContextId", "ApplicationUserId", "LeaseManagementPartyId",
                 "GrantedAtUtc", "GrantedByUserId", "Reason")
            SELECT gen_random_uuid(), @portfolioId, revoked."AccessContextId", revoked."ApplicationUserId", party."Id",
                   @changedAt, @actorUserId, 'Unit transfer: access continued from source relationship.'
            FROM revoked_access AS revoked
            INNER JOIN current_parties AS source_party
              ON source_party."Id" = revoked."LeaseManagementPartyId"
            INNER JOIN destination_parties AS party
              ON party."TenantId" = source_party."TenantId" AND party."Role" = source_party."Role"
            RETURNING "Id"
        ),
        revised_access_contexts AS (
            UPDATE "WorkspaceAccessContexts" AS context
            SET "AccessRevision" = context."AccessRevision" + 1,
                "UpdatedAtUtc" = @changedAt
            WHERE context."Id" IN (SELECT DISTINCT "AccessContextId" FROM revoked_access)
            RETURNING context."Id"
        ),
        turnover AS (
            INSERT INTO "UnitOperationalPeriods"
                ("PortfolioId", "PropertyId", "UnitId", "Type", "StartedAtUtc",
                 "SourceLeaseManagementId", "Reason", "CreatedAtUtc", "CreatedByUserId")
            SELECT @portfolioId, source."PropertyId", @sourceUnitId, 'Turnover', @changedAt,
                   @sourceLeaseManagementId, @transferReason, @changedAt, @actorUserId
            FROM source_updated AS source
            RETURNING "Id"
        )
        SELECT validation.outcome AS "Outcome",
               COALESCE((SELECT "TransferPublicId" FROM destination_relationship),
                        (SELECT "TransferPublicId" FROM existing_destination), @transferPublicId)
                 AS "TransferPublicId",
               COALESCE((SELECT "Id" FROM source_account), 0) AS "SourceTenantAccountId",
               (SELECT "Id" FROM source_deposit) AS "SourceSecurityDepositAccountId",
               COALESCE((SELECT "Id" FROM destination_relationship),
                        (SELECT "Id" FROM existing_destination), 0) AS "DestinationLeaseManagementId",
               COALESCE((SELECT "Id" FROM destination_account),
                        (SELECT tenant_account_id FROM existing_destination), 0) AS "DestinationTenantAccountId",
               COALESCE((SELECT "Id" FROM destination_agreement),
                        (SELECT agreement_id FROM existing_destination), 0) AS "DestinationAgreementId",
               COALESCE((SELECT "Id" FROM destination_deposit_account),
                        (SELECT deposit_account_id FROM existing_destination)) AS "DestinationSecurityDepositAccountId",
               COALESCE((SELECT "Id" FROM turnover),
                        (SELECT turnover_id FROM existing_destination), 0) AS "TurnoverPeriodId",
               COALESCE((SELECT "PossessionReturnedAtUtc" FROM source_updated),
                        (SELECT "PossessionReturnedAtUtc" FROM source_relationship)) AS "SourcePossessionReturnedAtUtc",
               COALESCE((SELECT "PossessionGivenAtUtc" FROM destination_relationship),
                        (SELECT "PossessionGivenAtUtc" FROM existing_destination)) AS "DestinationPossessionGivenAtUtc",
               CASE WHEN validation.outcome = @transferred AND @carryTenantBalance
                    THEN COALESCE((SELECT "ReceivableBalance" FROM source_account), 0::numeric)
                    ELSE 0::numeric END AS "CarriedTenantBalance",
               CASE WHEN validation.outcome = @transferred AND @carrySecurityDeposit
                    THEN COALESCE((SELECT "HeldBalance" FROM source_deposit), 0::numeric)
                    ELSE 0::numeric END AS "CarriedSecurityDeposit",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM source_parties_ended), '[]'::jsonb)::text
                 AS "EndedSourcePartyIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM destination_parties), '[]'::jsonb)::text
                 AS "DestinationPartyIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM destination_signers), '[]'::jsonb)::text
                 AS "DestinationSignerIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM revoked_access), '[]'::jsonb)::text
                 AS "RevokedSourceAccessIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM destination_access), '[]'::jsonb)::text
                 AS "DestinationAccessIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM tenant_ledger_entries), '[]'::jsonb)::text
                 AS "TenantLedgerEntryIdsJson",
               COALESCE((SELECT jsonb_agg("Id" ORDER BY "Id") FROM security_deposit_entries), '[]'::jsonb)::text
                 AS "SecurityDepositEntryIdsJson"
        FROM validation
        """;
}
