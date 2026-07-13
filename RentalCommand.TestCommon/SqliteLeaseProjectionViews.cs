using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace RentalCommand.TestCommon;

/// <summary>
/// Installs lightweight SQLite equivalents of the canonical PostgreSQL lease projections.
/// These views exist only for service tests whose query shape must remain projection-backed;
/// PostgreSQL integration tests continue to prove the complete production definitions.
/// </summary>
public static class SqliteLeaseProjectionViews
{
    public static void InstallCanonicalLeaseProjectionViewsForSqlite(this DatabaseFacade database)
    {
        database.ExecuteSqlRaw("""
            DROP VIEW IF EXISTS "vw_morning_briefing_candidates";
            DROP VIEW IF EXISTS "vw_lease_management_lifecycle";
            DROP VIEW IF EXISTS "vw_unit_occupancy";
            DROP VIEW IF EXISTS "vw_lease_agreement_status";
            DROP VIEW IF EXISTS "vw_lease_addendum_status";
            DROP VIEW IF EXISTS "vw_tenant_charge_balances";
            DROP VIEW IF EXISTS "vw_tenant_account_balances";
            DROP VIEW IF EXISTS "vw_security_deposit_balances";

            CREATE VIEW "vw_lease_agreement_status" AS
            SELECT
                agreement."PortfolioId",
                agreement."LeaseManagementId",
                agreement."Id" AS "AgreementId",
                date('now') AS "BusinessDate",
                agreement."GoverningFromOn",
                MIN(date(agreement."TermEndOn", '+1 day'), agreement."SupersededEffectiveOn")
                    AS "GoverningThroughExclusiveOn",
                CASE
                    WHEN agreement."VoidedAtUtc" IS NOT NULL THEN 'Void'
                    WHEN agreement."DraftCanceledAtUtc" IS NOT NULL THEN 'Canceled'
                    WHEN agreement."IssuedAtUtc" IS NULL THEN 'Draft'
                    WHEN agreement."FullyExecutedAtUtc" IS NULL THEN 'AwaitingSignatures'
                    WHEN agreement."SupersededEffectiveOn" IS NOT NULL
                         AND date('now') >= agreement."SupersededEffectiveOn" THEN 'Superseded'
                    WHEN date('now') < agreement."GoverningFromOn" THEN 'Upcoming'
                    WHEN (agreement."TermEndOn" IS NULL OR date('now') <= agreement."TermEndOn")
                         AND (agreement."SupersededEffectiveOn" IS NULL
                              OR date('now') < agreement."SupersededEffectiveOn") THEN 'Active'
                    ELSE 'Expired'
                END AS "AgreementStatus",
                CASE WHEN agreement."FullyExecutedAtUtc" IS NOT NULL
                           AND agreement."VoidedAtUtc" IS NULL
                           AND agreement."DraftCanceledAtUtc" IS NULL
                           AND date('now') >= agreement."GoverningFromOn"
                           AND (agreement."TermEndOn" IS NULL OR date('now') <= agreement."TermEndOn")
                           AND (agreement."SupersededEffectiveOn" IS NULL
                                OR date('now') < agreement."SupersededEffectiveOn")
                     THEN 1 ELSE 0 END AS "IsGoverning"
            FROM "LeaseAgreements" AS agreement;

            CREATE VIEW "vw_lease_addendum_status" AS
            WITH effective_portfolio_time AS (
                SELECT portfolio."Id" AS "PortfolioId", date('now') AS "BusinessDate"
                FROM "Portfolios" AS portfolio
                WHERE portfolio."DeletedAt" IS NULL
            ), financial_effects AS (
                SELECT
                    effect."PortfolioId",
                    effect."LeaseAddendumId",
                    COUNT(*) AS "FinancialEffectCount",
                    MAX(CASE
                        WHEN effect."EffectType" = 'OneTimeCharge' THEN
                            CASE WHEN effect."DueOn" <= effective_time."BusinessDate"
                                      AND NOT EXISTS (
                                          SELECT 1
                                          FROM "TenantLedgerEntries" AS posted_charge
                                          WHERE posted_charge."PortfolioId" = effect."PortfolioId"
                                            AND posted_charge."LeaseAddendumId" = effect."LeaseAddendumId"
                                            AND posted_charge."EntryType" = 'AddendumCharge'
                                            AND posted_charge."BusinessKey"
                                                = 'addendum-effect:' || effect."Id"
                                      )
                                 THEN 1 ELSE 0 END
                        ELSE
                            CASE WHEN effect."EffectiveFromOn" <= effective_time."BusinessDate"
                                      AND (effect."EffectiveThroughOn" IS NULL
                                           OR effective_time."BusinessDate" < date(
                                               effect."EffectiveThroughOn", '+1 day'))
                                 THEN 1 ELSE 0 END
                    END) AS "HasEffectiveFinancialEffect"
                FROM "LeaseAddendumFinancialEffects" AS effect
                JOIN effective_portfolio_time AS effective_time
                  ON effective_time."PortfolioId" = effect."PortfolioId"
                GROUP BY effect."PortfolioId", effect."LeaseAddendumId"
            ), status_rows AS (
                SELECT
                    addendum."PortfolioId",
                    addendum."LeaseManagementId",
                    addendum."Id" AS "LeaseAddendumId",
                    addendum."BaseAgreementId",
                    effective_time."BusinessDate",
                    addendum."EffectiveFromOn",
                    CASE
                        WHEN addendum."EffectiveThroughOn" IS NULL
                            THEN addendum."SupersededEffectiveOn"
                        WHEN addendum."SupersededEffectiveOn" IS NULL
                            THEN date(addendum."EffectiveThroughOn", '+1 day')
                        ELSE MIN(
                            date(addendum."EffectiveThroughOn", '+1 day'),
                            addendum."SupersededEffectiveOn")
                    END AS "EffectiveThroughExclusiveOn",
                    COALESCE(financial_effects."FinancialEffectCount", 0)
                        AS "FinancialEffectCount",
                    COALESCE(financial_effects."HasEffectiveFinancialEffect", 0)
                        AS "HasEffectiveFinancialEffect",
                    CASE
                        WHEN addendum."VoidedAtUtc" IS NOT NULL THEN 'Void'
                        WHEN addendum."DraftCanceledAtUtc" IS NOT NULL THEN 'Canceled'
                        WHEN addendum."IssuedAtUtc" IS NULL THEN 'Draft'
                        WHEN addendum."FullyExecutedAtUtc" IS NULL THEN 'AwaitingSignatures'
                        WHEN addendum."SupersededEffectiveOn" IS NOT NULL
                             AND effective_time."BusinessDate" >= addendum."SupersededEffectiveOn"
                            THEN 'Superseded'
                        WHEN effective_time."BusinessDate" < addendum."EffectiveFromOn"
                            THEN 'Upcoming'
                        WHEN effective_time."BusinessDate" >= addendum."EffectiveFromOn"
                             AND (addendum."EffectiveThroughOn" IS NULL
                                  OR effective_time."BusinessDate" < date(
                                      addendum."EffectiveThroughOn", '+1 day'))
                             AND (addendum."SupersededEffectiveOn" IS NULL
                                  OR effective_time."BusinessDate"
                                      < addendum."SupersededEffectiveOn")
                             AND base_agreement."Id" IS NOT NULL
                            THEN 'Active'
                        ELSE 'Expired'
                    END AS "AddendumStatus"
                FROM "LeaseAddenda" AS addendum
                JOIN effective_portfolio_time AS effective_time
                  ON effective_time."PortfolioId" = addendum."PortfolioId"
                LEFT JOIN "LeaseAgreements" AS base_agreement
                  ON base_agreement."Id" = addendum."BaseAgreementId"
                 AND base_agreement."LeaseManagementId" = addendum."LeaseManagementId"
                 AND base_agreement."PortfolioId" = addendum."PortfolioId"
                 AND base_agreement."FullyExecutedAtUtc" IS NOT NULL
                 AND base_agreement."VoidedAtUtc" IS NULL
                 AND base_agreement."DraftCanceledAtUtc" IS NULL
                LEFT JOIN financial_effects
                  ON financial_effects."PortfolioId" = addendum."PortfolioId"
                 AND financial_effects."LeaseAddendumId" = addendum."Id"
            )
            SELECT
                "PortfolioId",
                "LeaseManagementId",
                "LeaseAddendumId",
                "BaseAgreementId",
                "BusinessDate",
                "EffectiveFromOn",
                "EffectiveThroughExclusiveOn",
                "AddendumStatus",
                "FinancialEffectCount",
                CASE WHEN "AddendumStatus" = 'Active'
                           AND "HasEffectiveFinancialEffect" = 1
                     THEN 1 ELSE 0 END AS "HasCurrentlyBillableFinancialEffect"
            FROM status_rows;

            CREATE VIEW "vw_tenant_charge_balances" AS
            WITH reversals AS (
                SELECT reversal."PortfolioId", reversal."TenantAccountId",
                       reversal."ReversesEntryId" AS "TenantLedgerEntryId",
                       SUM(reversal."Amount") AS "ReversedAmount"
                FROM "TenantLedgerEntries" AS reversal
                WHERE reversal."EntryType" = 'Reversal'
                GROUP BY reversal."PortfolioId", reversal."TenantAccountId",
                         reversal."ReversesEntryId"
            ), allocations AS (
                SELECT allocation."PortfolioId", allocation."TenantAccountId",
                       allocation."DebitEntryId" AS "TenantLedgerEntryId",
                       SUM(allocation."Amount") AS "NetAllocations"
                FROM "TenantLedgerAllocations" AS allocation
                GROUP BY allocation."PortfolioId", allocation."TenantAccountId",
                         allocation."DebitEntryId"
            )
            SELECT entry."PortfolioId", entry."TenantAccountId",
                   entry."Id" AS "TenantLedgerEntryId", date('now') AS "BusinessDate",
                   entry."EntryType", entry."Currency", entry."EffectiveOn", entry."DueOn",
                   entry."Amount" AS "OriginalAmount",
                   COALESCE(reversals."ReversedAmount", 0) AS "ReversedAmount",
                   COALESCE(allocations."NetAllocations", 0) AS "NetAllocations",
                   MAX(0, entry."Amount" - COALESCE(reversals."ReversedAmount", 0)
                                      - COALESCE(allocations."NetAllocations", 0)) AS "OpenAmount",
                   CASE WHEN entry."DueOn" IS NOT NULL AND entry."DueOn" < date('now')
                                  AND entry."Amount" - COALESCE(reversals."ReversedAmount", 0)
                                      - COALESCE(allocations."NetAllocations", 0) > 0
                        THEN 1 ELSE 0 END AS "IsPastDue"
            FROM "TenantLedgerEntries" AS entry
            LEFT JOIN reversals
              ON reversals."PortfolioId" = entry."PortfolioId"
             AND reversals."TenantAccountId" = entry."TenantAccountId"
             AND reversals."TenantLedgerEntryId" = entry."Id"
            LEFT JOIN allocations
              ON allocations."PortfolioId" = entry."PortfolioId"
             AND allocations."TenantAccountId" = entry."TenantAccountId"
             AND allocations."TenantLedgerEntryId" = entry."Id"
            WHERE entry."Direction" = 'Debit'
              AND entry."EntryType" NOT IN ('Reversal', 'TransferOut');

            CREATE VIEW "vw_tenant_account_balances" AS
            WITH entry_totals AS (
                SELECT entry."PortfolioId", entry."TenantAccountId",
                       SUM(CASE WHEN entry."Direction" = 'Debit'
                                AND entry."EntryType" NOT IN ('Reversal', 'TransferOut')
                                THEN entry."Amount" ELSE 0 END) AS "TotalDebits",
                       SUM(CASE WHEN entry."Direction" = 'Credit'
                                AND entry."EntryType" <> 'Reversal'
                                THEN entry."Amount" ELSE 0 END) AS "TotalCredits"
                FROM "TenantLedgerEntries" AS entry
                GROUP BY entry."PortfolioId", entry."TenantAccountId"
            ), charge_totals AS (
                SELECT charge."PortfolioId", charge."TenantAccountId",
                       SUM(charge."OpenAmount") AS "ReceivableBalance",
                       SUM(CASE WHEN charge."IsPastDue" = 1 THEN charge."OpenAmount" ELSE 0 END)
                           AS "PastDueAmount",
                       SUM(CASE WHEN charge."IsPastDue" = 1 THEN 1 ELSE 0 END) AS "PastDueCount",
                       MIN(CASE WHEN charge."OpenAmount" > 0 THEN charge."DueOn" END) AS "NextDueOn"
                FROM "vw_tenant_charge_balances" AS charge
                GROUP BY charge."PortfolioId", charge."TenantAccountId"
            ), last_receipt AS (
                SELECT entry."PortfolioId", entry."TenantAccountId",
                       entry."EffectiveOn" AS "LastReceiptOn", entry."Amount" AS "LastReceiptAmount",
                       ROW_NUMBER() OVER (
                           PARTITION BY entry."PortfolioId", entry."TenantAccountId"
                           ORDER BY entry."EffectiveOn" DESC, entry."Id" DESC) AS receipt_rank
                FROM "TenantLedgerEntries" AS entry
                WHERE entry."Direction" = 'Credit' AND entry."EntryType" = 'Receipt'
            )
            SELECT account."PortfolioId", account."LeaseManagementId",
                   account."Id" AS "TenantAccountId", CURRENT_TIMESTAMP AS "EffectiveNowUtc",
                   date('now') AS "BusinessDate", account."Currency",
                   COALESCE(entries."TotalDebits", 0) AS "TotalDebits",
                   COALESCE(entries."TotalCredits", 0) AS "TotalCredits",
                   COALESCE(charges."ReceivableBalance", 0) AS "ReceivableBalance",
                   MAX(COALESCE(entries."TotalCredits", 0) - COALESCE(entries."TotalDebits", 0), 0)
                       AS "UnappliedCredit",
                   COALESCE(charges."PastDueAmount", 0) AS "PastDueAmount",
                   COALESCE(charges."PastDueCount", 0) AS "PastDueCount",
                   charges."NextDueOn",
                   COALESCE((
                       SELECT SUM(open_charge."OpenAmount")
                       FROM "vw_tenant_charge_balances" AS open_charge
                       WHERE open_charge."PortfolioId" = account."PortfolioId"
                         AND open_charge."TenantAccountId" = account."Id"
                         AND open_charge."DueOn" = charges."NextDueOn"
                   ), 0) AS "NextDueAmount",
                   CASE WHEN COALESCE(charges."PastDueAmount", 0) > 0
                        THEN 'PastDue' ELSE 'Current' END AS "Condition",
                   receipt."LastReceiptOn", receipt."LastReceiptAmount"
            FROM "TenantAccounts" AS account
            LEFT JOIN entry_totals AS entries
              ON entries."PortfolioId" = account."PortfolioId"
             AND entries."TenantAccountId" = account."Id"
            LEFT JOIN charge_totals AS charges
              ON charges."PortfolioId" = account."PortfolioId"
             AND charges."TenantAccountId" = account."Id"
            LEFT JOIN last_receipt AS receipt
              ON receipt."PortfolioId" = account."PortfolioId"
             AND receipt."TenantAccountId" = account."Id"
             AND receipt.receipt_rank = 1;

            CREATE VIEW "vw_security_deposit_balances" AS
            WITH reversals AS (
                SELECT reversal."PortfolioId", reversal."SecurityDepositAccountId",
                       reversal."ReversesEntryId" AS "SecurityDepositEntryId",
                       SUM(reversal."Amount") AS "ReversedAmount"
                FROM "SecurityDepositEntries" AS reversal
                WHERE reversal."EntryType" = 'Reversal'
                GROUP BY reversal."PortfolioId", reversal."SecurityDepositAccountId",
                         reversal."ReversesEntryId"
            ), effective_entries AS (
                SELECT entry."PortfolioId", entry."SecurityDepositAccountId",
                       entry."EntryType", entry."Direction",
                       MAX(entry."Amount" - COALESCE(reversals."ReversedAmount", 0), 0)
                           AS "NetAmount"
                FROM "SecurityDepositEntries" AS entry
                LEFT JOIN reversals
                  ON reversals."PortfolioId" = entry."PortfolioId"
                 AND reversals."SecurityDepositAccountId" = entry."SecurityDepositAccountId"
                 AND reversals."SecurityDepositEntryId" = entry."Id"
                WHERE entry."EntryType" <> 'Reversal'
            ), totals AS (
                SELECT entry."PortfolioId", entry."SecurityDepositAccountId",
                       SUM(CASE WHEN entry."EntryType" = 'Receipt' THEN entry."NetAmount" ELSE 0 END)
                           AS "TotalReceived",
                       SUM(CASE WHEN entry."EntryType" = 'Deduction' THEN entry."NetAmount" ELSE 0 END)
                           AS "TotalDeductions",
                       SUM(CASE WHEN entry."EntryType" = 'Refund' THEN entry."NetAmount" ELSE 0 END)
                           AS "TotalRefunded",
                       SUM(CASE WHEN entry."EntryType" = 'TransferIn' THEN entry."NetAmount" ELSE 0 END)
                           AS "TotalTransferredIn",
                       SUM(CASE WHEN entry."EntryType" = 'TransferOut' THEN entry."NetAmount" ELSE 0 END)
                           AS "TotalTransferredOut",
                       SUM(CASE WHEN entry."EntryType" = 'Adjustment' AND entry."Direction" = 'Increase'
                                THEN entry."NetAmount"
                                WHEN entry."EntryType" = 'Adjustment' AND entry."Direction" = 'Decrease'
                                THEN -entry."NetAmount" ELSE 0 END) AS "NetAdjustments",
                       SUM(CASE WHEN entry."Direction" = 'Increase' THEN entry."NetAmount"
                                ELSE -entry."NetAmount" END) AS "HeldBalance"
                FROM effective_entries AS entry
                GROUP BY entry."PortfolioId", entry."SecurityDepositAccountId"
            )
            SELECT deposit."PortfolioId", account."LeaseManagementId",
                   deposit."TenantAccountId", deposit."Id" AS "SecurityDepositAccountId",
                   CURRENT_TIMESTAMP AS "EffectiveNowUtc", date('now') AS "BusinessDate",
                   deposit."Currency",
                   COALESCE(totals."TotalReceived", 0) AS "TotalReceived",
                   COALESCE(totals."TotalDeductions", 0) AS "TotalDeductions",
                   COALESCE(totals."TotalRefunded", 0) AS "TotalRefunded",
                   COALESCE(totals."TotalTransferredIn", 0) AS "TotalTransferredIn",
                   COALESCE(totals."TotalTransferredOut", 0) AS "TotalTransferredOut",
                   COALESCE(totals."NetAdjustments", 0) AS "NetAdjustments",
                   COALESCE(totals."HeldBalance", 0) AS "HeldBalance",
                   CASE
                       WHEN COALESCE(totals."TotalReceived", 0)
                            + COALESCE(totals."TotalTransferredIn", 0)
                            + MAX(COALESCE(totals."NetAdjustments", 0), 0) = 0 THEN 'NotFunded'
                       WHEN management."AccountClosedAtUtc" IS NULL
                            OR COALESCE(totals."HeldBalance", 0) > 0 THEN 'Held'
                       WHEN COALESCE(totals."TotalDeductions", 0) > 0
                            AND COALESCE(totals."TotalRefunded", 0)
                                + COALESCE(totals."TotalTransferredOut", 0) = 0 THEN 'Withheld'
                       WHEN COALESCE(totals."TotalDeductions", 0) > 0 THEN 'PartiallyReturned'
                       WHEN COALESCE(totals."TotalRefunded", 0)
                            + COALESCE(totals."TotalTransferredOut", 0) > 0 THEN 'Returned'
                       ELSE 'Held'
                   END AS "DepositStatus"
            FROM "SecurityDepositAccounts" AS deposit
            JOIN "TenantAccounts" AS account
              ON account."PortfolioId" = deposit."PortfolioId"
             AND account."Id" = deposit."TenantAccountId"
            JOIN "LeaseManagements" AS management
              ON management."PortfolioId" = account."PortfolioId"
             AND management."Id" = account."LeaseManagementId"
            LEFT JOIN totals
              ON totals."PortfolioId" = deposit."PortfolioId"
             AND totals."SecurityDepositAccountId" = deposit."Id";

            CREATE VIEW "vw_unit_occupancy" AS
            SELECT
                unit."PortfolioId",
                unit."PropertyId",
                unit."Id" AS "UnitId",
                CURRENT_TIMESTAMP AS "EffectiveNowUtc",
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM "LeaseManagements" AS management
                    WHERE management."PortfolioId" = unit."PortfolioId"
                      AND management."PropertyId" = unit."PropertyId"
                      AND management."UnitId" = unit."Id"
                      AND management."CanceledAtUtc" IS NULL
                      AND management."PossessionGivenAtUtc" IS NOT NULL
                      AND management."PossessionGivenAtUtc" <= CURRENT_TIMESTAMP
                      AND (management."PossessionReturnedAtUtc" IS NULL
                           OR management."PossessionReturnedAtUtc" > CURRENT_TIMESTAMP)
                ) THEN 1 ELSE 0 END AS "IsOccupied",
                (
                    SELECT management."Id"
                    FROM "LeaseManagements" AS management
                    WHERE management."PortfolioId" = unit."PortfolioId"
                      AND management."PropertyId" = unit."PropertyId"
                      AND management."UnitId" = unit."Id"
                      AND management."CanceledAtUtc" IS NULL
                      AND management."PossessionGivenAtUtc" IS NOT NULL
                      AND management."PossessionGivenAtUtc" <= CURRENT_TIMESTAMP
                      AND (management."PossessionReturnedAtUtc" IS NULL
                           OR management."PossessionReturnedAtUtc" > CURRENT_TIMESTAMP)
                    ORDER BY management."PossessionGivenAtUtc" DESC, management."Id" DESC
                    LIMIT 1
                ) AS "CurrentLeaseManagementId",
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM "LeaseManagements" AS management
                    WHERE management."PortfolioId" = unit."PortfolioId"
                      AND management."PropertyId" = unit."PropertyId"
                      AND management."UnitId" = unit."Id"
                      AND management."CanceledAtUtc" IS NULL
                      AND management."PossessionGivenAtUtc" IS NULL
                      AND management."PlannedPossessionAtUtc" IS NOT NULL
                ) THEN 1 ELSE 0 END AS "HasScheduledMoveIn",
                (
                    SELECT management."PlannedPossessionAtUtc"
                    FROM "LeaseManagements" AS management
                    WHERE management."PortfolioId" = unit."PortfolioId"
                      AND management."PropertyId" = unit."PropertyId"
                      AND management."UnitId" = unit."Id"
                      AND management."CanceledAtUtc" IS NULL
                      AND management."PossessionGivenAtUtc" IS NULL
                      AND management."PlannedPossessionAtUtc" IS NOT NULL
                    ORDER BY management."PlannedPossessionAtUtc", management."Id"
                    LIMIT 1
                ) AS "NextPlannedPossessionAtUtc",
                (
                    SELECT management."Id"
                    FROM "LeaseManagements" AS management
                    WHERE management."PortfolioId" = unit."PortfolioId"
                      AND management."PropertyId" = unit."PropertyId"
                      AND management."UnitId" = unit."Id"
                      AND management."CanceledAtUtc" IS NULL
                      AND management."PossessionGivenAtUtc" IS NULL
                      AND management."PlannedPossessionAtUtc" IS NOT NULL
                    ORDER BY management."PlannedPossessionAtUtc", management."Id"
                    LIMIT 1
                ) AS "PlannedLeaseManagementId",
                0 AS "IsInTurnover",
                0 AS "IsOutOfService",
                0 AS "IsOnManagementHold",
                0 AS "HasGoverningAgreementWithoutPossession",
                0 AS "HasPossessionWithoutGoverningAgreement",
                NULL AS "OccupancyExceptionCode"
            FROM "Units" AS unit
            JOIN "Properties" AS property
              ON property."PortfolioId" = unit."PortfolioId"
             AND property."Id" = unit."PropertyId"
             AND property."DeletedAt" IS NULL
            WHERE unit."DeletedAt" IS NULL;

            CREATE VIEW "vw_lease_management_lifecycle" AS
            SELECT
                management."PortfolioId",
                management."PropertyId",
                management."UnitId",
                management."Id" AS "LeaseManagementId",
                CURRENT_TIMESTAMP AS "EffectiveNowUtc",
                date('now') AS "BusinessDate",
                CASE
                    WHEN management."CanceledAtUtc" IS NOT NULL
                      OR (management."PossessionReturnedAtUtc" IS NOT NULL
                          AND management."PossessionReturnedAtUtc" <= CURRENT_TIMESTAMP)
                        THEN 'Closed'
                    WHEN management."NoticeGivenAtUtc" IS NOT NULL THEN 'Ending'
                    WHEN management."PossessionGivenAtUtc" IS NOT NULL
                      AND management."PossessionGivenAtUtc" <= CURRENT_TIMESTAMP
                        THEN 'Occupied'
                    WHEN management."PlannedPossessionAtUtc" IS NOT NULL THEN 'Planned'
                    ELSE 'Setup'
                END AS "Lifecycle",
                (
                    SELECT agreement."Id"
                    FROM "LeaseAgreements" AS agreement
                    WHERE agreement."PortfolioId" = management."PortfolioId"
                      AND agreement."LeaseManagementId" = management."Id"
                      AND agreement."FullyExecutedAtUtc" IS NOT NULL
                      AND agreement."VoidedAtUtc" IS NULL
                      AND agreement."DraftCanceledAtUtc" IS NULL
                      AND agreement."GoverningFromOn" <= date('now')
                      AND (agreement."TermEndOn" IS NULL OR agreement."TermEndOn" >= date('now'))
                      AND (agreement."SupersededEffectiveOn" IS NULL
                           OR agreement."SupersededEffectiveOn" > date('now'))
                    ORDER BY agreement."GoverningFromOn" DESC,
                             agreement."VersionNumber" DESC,
                             agreement."Id" DESC
                    LIMIT 1
                ) AS "CurrentAgreementId",
                (
                    SELECT agreement."Id"
                    FROM "LeaseAgreements" AS agreement
                    WHERE agreement."PortfolioId" = management."PortfolioId"
                      AND agreement."LeaseManagementId" = management."Id"
                      AND agreement."FullyExecutedAtUtc" IS NOT NULL
                      AND agreement."VoidedAtUtc" IS NULL
                      AND agreement."DraftCanceledAtUtc" IS NULL
                      AND agreement."GoverningFromOn" > date('now')
                    ORDER BY agreement."GoverningFromOn", agreement."VersionNumber", agreement."Id"
                    LIMIT 1
                ) AS "UpcomingAgreementId",
                (
                    SELECT COUNT(*)
                    FROM "LeaseManagementParties" AS party
                    WHERE party."PortfolioId" = management."PortfolioId"
                      AND party."LeaseManagementId" = management."Id"
                      AND party."EffectiveFrom" <= date('now')
                      AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= date('now'))
                ) AS "CurrentPartyCount",
                (
                    SELECT COUNT(*)
                    FROM "LeaseManagementParties" AS party
                    WHERE party."PortfolioId" = management."PortfolioId"
                      AND party."LeaseManagementId" = management."Id"
                      AND party."Role" IN ('PrimaryTenant', 'CoTenant', 'Occupant')
                      AND party."EffectiveFrom" <= date('now')
                      AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= date('now'))
                ) AS "CurrentResidentCount",
                (
                    SELECT COUNT(*)
                    FROM "LeaseManagementParties" AS party
                    WHERE party."PortfolioId" = management."PortfolioId"
                      AND party."LeaseManagementId" = management."Id"
                      AND party."Role" IN ('PrimaryTenant', 'CoTenant', 'Guarantor')
                      AND party."EffectiveFrom" <= date('now')
                      AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= date('now'))
                ) AS "CurrentFinanciallyResponsiblePartyCount",
                (
                    SELECT party."Id"
                    FROM "LeaseManagementParties" AS party
                    WHERE party."PortfolioId" = management."PortfolioId"
                      AND party."LeaseManagementId" = management."Id"
                      AND party."Role" = 'PrimaryTenant'
                      AND party."EffectiveFrom" <= date('now')
                      AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= date('now'))
                    ORDER BY party."EffectiveFrom" DESC, party."Id" DESC
                    LIMIT 1
                ) AS "CurrentPrimaryPartyId",
                (
                    SELECT party."TenantId"
                    FROM "LeaseManagementParties" AS party
                    WHERE party."PortfolioId" = management."PortfolioId"
                      AND party."LeaseManagementId" = management."Id"
                      AND party."Role" = 'PrimaryTenant'
                      AND party."EffectiveFrom" <= date('now')
                      AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= date('now'))
                    ORDER BY party."EffectiveFrom" DESC, party."Id" DESC
                    LIMIT 1
                ) AS "CurrentPrimaryTenantId",
                (
                    SELECT trim(tenant."FirstName" || ' ' || tenant."LastName")
                    FROM "LeaseManagementParties" AS party
                    JOIN "Tenants" AS tenant
                      ON tenant."PortfolioId" = party."PortfolioId"
                     AND tenant."Id" = party."TenantId"
                    WHERE party."PortfolioId" = management."PortfolioId"
                      AND party."LeaseManagementId" = management."Id"
                      AND party."Role" = 'PrimaryTenant'
                      AND party."EffectiveFrom" <= date('now')
                      AND (party."EffectiveThrough" IS NULL OR party."EffectiveThrough" >= date('now'))
                    ORDER BY party."EffectiveFrom" DESC, party."Id" DESC
                    LIMIT 1
                ) AS "CurrentPrimaryTenantName",
                (
                    SELECT account."Id"
                    FROM "TenantAccounts" AS account
                    WHERE account."PortfolioId" = management."PortfolioId"
                      AND account."LeaseManagementId" = management."Id"
                    ORDER BY account."Id"
                    LIMIT 1
                ) AS "TenantAccountId",
                CASE WHEN NOT EXISTS (
                    SELECT 1 FROM "TenantAccounts" AS account
                    WHERE account."PortfolioId" = management."PortfolioId"
                      AND account."LeaseManagementId" = management."Id"
                ) THEN 1 ELSE 0 END AS "HasMissingTenantAccount",
                0 AS "HasMultipleGoverningAgreements",
                0 AS "HasMultipleCurrentPrimaryTenants",
                0 AS "HasAccountCloseMismatch",
                0 AS "HasGoverningAgreementWithoutPossession",
                0 AS "HasPossessionWithoutGoverningAgreement",
                0 AS "HasReconciliationException"
            FROM "LeaseManagements" AS management;

            CREATE VIEW "vw_morning_briefing_candidates" AS
            SELECT work."PortfolioId", work."PropertyId", work."UnitId",
                   'work.read' AS "RequiredCapability", 1 AS "SortOrder", 0 AS "SeverityOrder",
                   'Maintenance' AS "Category", 'WorkOrder' AS "EntityType", work."Id" AS "EntityId",
                   work."Title" AS "TitleText", work."Description" AS "DetailText",
                   NULL AS "LeaseNumber", NULL AS "UnitNumber", NULL AS "TenantName",
                   property."Name" AS "PropertyName", 0.0 AS "Amount",
                   work."RequestedAt" AS "EventDateTime", NULL AS "EventDateOnly", 0 AS "TypeValue"
            FROM "WorkOrders" AS work
            JOIN "Properties" AS property
              ON property."Id" = work."PropertyId"
             AND property."PortfolioId" = work."PortfolioId"
            WHERE work."Priority" = 3
              AND work."Status" NOT IN (4, 5, 7)
              AND work."DeletedAt" IS NULL

            UNION ALL

            SELECT charge."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId",
                   'money.balances.read', 2, 1, 'RentLate', 'TenantAccount', account."Id",
                   NULL, NULL, COALESCE(agreement."AgreementNumber", account."AccountNumber"),
                   unit."UnitNumber", lifecycle."CurrentPrimaryTenantName", property."Name",
                   SUM(charge."OpenAmount"), NULL, charge."DueOn", 0
            FROM "vw_tenant_charge_balances" AS charge
            JOIN "TenantAccounts" AS account
              ON account."Id" = charge."TenantAccountId"
             AND account."PortfolioId" = charge."PortfolioId"
            JOIN "vw_lease_management_lifecycle" AS lifecycle
              ON lifecycle."LeaseManagementId" = account."LeaseManagementId"
             AND lifecycle."PortfolioId" = account."PortfolioId"
            JOIN "Units" AS unit
              ON unit."Id" = lifecycle."UnitId"
             AND unit."PortfolioId" = lifecycle."PortfolioId"
            JOIN "Properties" AS property
              ON property."Id" = lifecycle."PropertyId"
             AND property."PortfolioId" = lifecycle."PortfolioId"
            LEFT JOIN "LeaseAgreements" AS agreement
              ON agreement."Id" = lifecycle."CurrentAgreementId"
             AND agreement."PortfolioId" = lifecycle."PortfolioId"
            WHERE charge."DueOn" IS NOT NULL
              AND charge."OpenAmount" > 0
              AND lifecycle."Lifecycle" IN ('Occupied', 'Ending')
            GROUP BY charge."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId", account."Id",
                     agreement."AgreementNumber", account."AccountNumber", unit."UnitNumber",
                     lifecycle."CurrentPrimaryTenantName", property."Name", charge."DueOn"

            UNION ALL

            SELECT charge."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId",
                   'money.balances.read', 3, 2, 'RentDue', 'TenantAccount', account."Id",
                   NULL, NULL, COALESCE(agreement."AgreementNumber", account."AccountNumber"),
                   unit."UnitNumber", lifecycle."CurrentPrimaryTenantName", property."Name",
                   SUM(charge."OpenAmount"), NULL, charge."DueOn", 0
            FROM "vw_tenant_charge_balances" AS charge
            JOIN "TenantAccounts" AS account
              ON account."Id" = charge."TenantAccountId"
             AND account."PortfolioId" = charge."PortfolioId"
            JOIN "vw_lease_management_lifecycle" AS lifecycle
              ON lifecycle."LeaseManagementId" = account."LeaseManagementId"
             AND lifecycle."PortfolioId" = account."PortfolioId"
            JOIN "Units" AS unit
              ON unit."Id" = lifecycle."UnitId"
             AND unit."PortfolioId" = lifecycle."PortfolioId"
            JOIN "Properties" AS property
              ON property."Id" = lifecycle."PropertyId"
             AND property."PortfolioId" = lifecycle."PortfolioId"
            LEFT JOIN "LeaseAgreements" AS agreement
              ON agreement."Id" = lifecycle."CurrentAgreementId"
             AND agreement."PortfolioId" = lifecycle."PortfolioId"
            WHERE charge."EntryType" = 'RentCharge'
              AND charge."DueOn" IS NOT NULL
              AND charge."OpenAmount" > 0
              AND lifecycle."Lifecycle" IN ('Occupied', 'Ending')
            GROUP BY charge."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId", account."Id",
                     agreement."AgreementNumber", account."AccountNumber", unit."UnitNumber",
                     lifecycle."CurrentPrimaryTenantName", property."Name", charge."DueOn"

            UNION ALL

            SELECT appointment."PortfolioId", appointment."PropertyId", appointment."UnitId",
                   CASE appointment."Type"
                     WHEN 0 THEN 'leasing.showings.manage'
                     WHEN 1 THEN 'leasing.onboarding.manage'
                     WHEN 2 THEN 'leasing.onboarding.manage'
                     WHEN 3 THEN 'work.read'
                     WHEN 4 THEN 'work.read'
                     ELSE 'rentals.read'
                   END,
                   4, 2, 'Appointment', 'Appointment', appointment."Id", appointment."Title", NULL,
                   NULL, NULL, NULL, property."Name", 0.0, appointment."ScheduledStart", NULL,
                   appointment."Type"
            FROM "Appointments" AS appointment
            JOIN "Properties" AS property
              ON property."Id" = appointment."PropertyId"
             AND property."PortfolioId" = appointment."PortfolioId"
            WHERE appointment."PropertyId" IS NOT NULL
              AND appointment."Status" IN (0, 1)

            UNION ALL

            SELECT inspection."PortfolioId", inspection."PropertyId", inspection."UnitId",
                   'work.read', 5, 2, 'Inspection', 'Inspection', inspection."Id", NULL, NULL,
                   NULL, NULL, NULL, property."Name", 0.0, inspection."ScheduledFor", NULL,
                   inspection."Type"
            FROM "Inspections" AS inspection
            JOIN "Properties" AS property
              ON property."Id" = inspection."PropertyId"
             AND property."PortfolioId" = inspection."PortfolioId"
            WHERE inspection."Status" = 0

            UNION ALL

            SELECT status."PortfolioId", lifecycle."PropertyId", lifecycle."UnitId",
                   'rentals.read', 6, 1, 'LeaseExpiring', 'LeaseAgreement', agreement."Id", NULL, NULL,
                   agreement."AgreementNumber", unit."UnitNumber", lifecycle."CurrentPrimaryTenantName",
                   property."Name", 0.0, NULL, agreement."TermEndOn", 0
            FROM "vw_lease_agreement_status" AS status
            JOIN "LeaseAgreements" AS agreement
              ON agreement."Id" = status."AgreementId"
             AND agreement."PortfolioId" = status."PortfolioId"
            JOIN "vw_lease_management_lifecycle" AS lifecycle
              ON lifecycle."LeaseManagementId" = status."LeaseManagementId"
             AND lifecycle."PortfolioId" = status."PortfolioId"
            JOIN "Units" AS unit
              ON unit."Id" = lifecycle."UnitId"
             AND unit."PortfolioId" = lifecycle."PortfolioId"
            JOIN "Properties" AS property
              ON property."Id" = lifecycle."PropertyId"
             AND property."PortfolioId" = lifecycle."PortfolioId"
            WHERE status."IsGoverning" = 1
              AND lifecycle."Lifecycle" IN ('Occupied', 'Ending')
              AND agreement."TermEndOn" IS NOT NULL;
            """);
    }
}
