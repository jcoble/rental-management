using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260811160000_FixTenantLedgerViewsAsOfReversalsAndAllocations")]
public partial class FixTenantLedgerViewsAsOfReversalsAndAllocations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "CREATE OR REPLACE VIEW \"vw_tenant_charge_balances\" WITH (security_invoker = true) AS\n"
            + TenantChargeBalanceViewSql.Definition);
        migrationBuilder.Sql(
            "CREATE OR REPLACE VIEW \"vw_tenant_account_balances\" WITH (security_invoker = true) AS\n"
            + TenantAccountBalanceViewSql.Definition);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "CREATE OR REPLACE VIEW \"vw_tenant_charge_balances\" WITH (security_invoker = true) AS\n"
            + PreviousTenantChargeBalanceDefinition);
        migrationBuilder.Sql(
            "CREATE OR REPLACE VIEW \"vw_tenant_account_balances\" WITH (security_invoker = true) AS\n"
            + PreviousTenantAccountBalanceDefinition);
    }

    private const string PreviousTenantChargeBalanceDefinition = """
        WITH effective_portfolio_time AS MATERIALIZED (
          SELECT portfolio."Id" AS "PortfolioId",
                 effective_time."NowUtc",
                 (effective_time."NowUtc" AT TIME ZONE
                    COALESCE(NULLIF(clock_state."TimeZoneId", ''), portfolio."TimeZone"))::date
                   AS "BusinessDate"
          FROM "Portfolios" AS portfolio
          LEFT JOIN "SimulationClocks" AS clock_state ON clock_state."Id" = 1
          CROSS JOIN LATERAL (
            SELECT rc_effective_now_utc(portfolio."Id") AS "NowUtc"
          ) AS effective_time
          WHERE portfolio."DeletedAt" IS NULL
        ),
        entry_reversals AS (
          SELECT reversal."PortfolioId",
                 reversal."TenantAccountId",
                 reversal."ReversesEntryId" AS "TenantLedgerEntryId",
                 sum(reversal."Amount") AS "ReversedAmount"
          FROM "TenantLedgerEntries" AS reversal
          WHERE reversal."EntryType" = 'Reversal'
          GROUP BY reversal."PortfolioId", reversal."TenantAccountId", reversal."ReversesEntryId"
        ),
        debit_allocations AS (
          SELECT allocation."PortfolioId",
                 allocation."TenantAccountId",
                 allocation."DebitEntryId" AS "TenantLedgerEntryId",
                 sum(allocation."Amount") AS "NetAllocations"
          FROM "TenantLedgerAllocations" AS allocation
          GROUP BY allocation."PortfolioId", allocation."TenantAccountId", allocation."DebitEntryId"
        ),
        charge_rows AS (
          SELECT entry."PortfolioId",
                 entry."TenantAccountId",
                 entry."Id" AS "TenantLedgerEntryId",
                 effective_time."BusinessDate",
                 entry."EntryType",
                 entry."Currency",
                 entry."EffectiveOn",
                 entry."DueOn",
                 entry."Amount" AS "OriginalAmount",
                 COALESCE(entry_reversals."ReversedAmount", 0::numeric) AS "ReversedAmount",
                 COALESCE(debit_allocations."NetAllocations", 0::numeric) AS "NetAllocations"
          FROM "TenantLedgerEntries" AS entry
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = entry."PortfolioId"
          LEFT JOIN entry_reversals
            ON entry_reversals."PortfolioId" = entry."PortfolioId"
           AND entry_reversals."TenantAccountId" = entry."TenantAccountId"
           AND entry_reversals."TenantLedgerEntryId" = entry."Id"
          LEFT JOIN debit_allocations
            ON debit_allocations."PortfolioId" = entry."PortfolioId"
           AND debit_allocations."TenantAccountId" = entry."TenantAccountId"
           AND debit_allocations."TenantLedgerEntryId" = entry."Id"
          WHERE entry."Direction" = 'Debit'
            AND entry."EffectiveOn" <= effective_time."BusinessDate"
            AND entry."EntryType" NOT IN ('Refund', 'Reversal', 'TransferOut')
        )
        SELECT "PortfolioId",
               "TenantAccountId",
               "TenantLedgerEntryId",
               "BusinessDate",
               "EntryType",
               "Currency",
               "EffectiveOn",
               "DueOn",
               "OriginalAmount",
               "ReversedAmount",
               "NetAllocations",
               GREATEST(
                 0::numeric,
                 "OriginalAmount" - "ReversedAmount" - "NetAllocations"
               ) AS "OpenAmount",
               ("DueOn" IS NOT NULL
                 AND "DueOn" < "BusinessDate"
                 AND "OriginalAmount" - "ReversedAmount" - "NetAllocations" > 0)
                 AS "IsPastDue"
        FROM charge_rows;
        """;

    private const string PreviousTenantAccountBalanceDefinition = """
        WITH effective_portfolio_time AS MATERIALIZED (
          SELECT portfolio."Id" AS "PortfolioId",
                 effective_time."NowUtc",
                 (effective_time."NowUtc" AT TIME ZONE
                    COALESCE(NULLIF(clock_state."TimeZoneId", ''), portfolio."TimeZone"))::date
                   AS "BusinessDate"
          FROM "Portfolios" AS portfolio
          LEFT JOIN "SimulationClocks" AS clock_state ON clock_state."Id" = 1
          CROSS JOIN LATERAL (
            SELECT rc_effective_now_utc(portfolio."Id") AS "NowUtc"
          ) AS effective_time
          WHERE portfolio."DeletedAt" IS NULL
        ),
        entry_reversals AS (
          SELECT reversal."PortfolioId",
                 reversal."TenantAccountId",
                 reversal."ReversesEntryId" AS "TenantLedgerEntryId",
                 sum(reversal."Amount") AS "ReversedAmount"
          FROM "TenantLedgerEntries" AS reversal
          WHERE reversal."EntryType" = 'Reversal'
          GROUP BY reversal."PortfolioId", reversal."TenantAccountId", reversal."ReversesEntryId"
        ),
        effective_entries AS (
          SELECT entry."PortfolioId",
                 entry."TenantAccountId",
                 entry."Id" AS "TenantLedgerEntryId",
                 entry."EntryType",
                 entry."Direction",
                 entry."EffectiveOn",
                 entry."DueOn",
                 entry."PostedAtUtc",
                 GREATEST(entry."Amount" - COALESCE(entry_reversals."ReversedAmount", 0::numeric), 0::numeric)
                   AS "NetAmount"
          FROM "TenantLedgerEntries" AS entry
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = entry."PortfolioId"
          LEFT JOIN entry_reversals
            ON entry_reversals."PortfolioId" = entry."PortfolioId"
           AND entry_reversals."TenantAccountId" = entry."TenantAccountId"
           AND entry_reversals."TenantLedgerEntryId" = entry."Id"
          WHERE entry."EntryType" <> 'Reversal'
            AND entry."EffectiveOn" <= effective_time."BusinessDate"
        ),
        debit_allocations AS (
          SELECT allocation."PortfolioId",
                 allocation."TenantAccountId",
                 allocation."DebitEntryId" AS "TenantLedgerEntryId",
                 sum(allocation."Amount") AS "NetAllocations"
          FROM "TenantLedgerAllocations" AS allocation
          GROUP BY allocation."PortfolioId", allocation."TenantAccountId", allocation."DebitEntryId"
        ),
        account_allocations AS (
          SELECT allocation."PortfolioId",
                 allocation."TenantAccountId",
                 sum(allocation."Amount") AS "NetAllocations"
          FROM "TenantLedgerAllocations" AS allocation
          GROUP BY allocation."PortfolioId", allocation."TenantAccountId"
        ),
        account_entry_totals AS (
          SELECT entry."PortfolioId",
                 entry."TenantAccountId",
                 COALESCE(sum(entry."NetAmount") FILTER (WHERE entry."Direction" = 'Debit'), 0::numeric)
                   AS "TotalDebits",
                 COALESCE(sum(entry."NetAmount") FILTER (WHERE entry."Direction" = 'Credit'), 0::numeric)
                   AS "TotalCredits",
                 COALESCE(sum(entry."NetAmount") FILTER (
                   WHERE entry."EntryType" = 'Refund' AND entry."Direction" = 'Debit'
                 ), 0::numeric) AS "ReturnedPaymentDebits"
          FROM effective_entries AS entry
          GROUP BY entry."PortfolioId", entry."TenantAccountId"
        ),
        open_charges AS (
          SELECT entry."PortfolioId",
                 entry."TenantAccountId",
                 entry."TenantLedgerEntryId",
                 entry."DueOn",
                 GREATEST(
                   entry."NetAmount" - COALESCE(debit_allocations."NetAllocations", 0::numeric),
                   0::numeric
                 ) AS "OpenAmount"
          FROM effective_entries AS entry
          LEFT JOIN debit_allocations
            ON debit_allocations."PortfolioId" = entry."PortfolioId"
           AND debit_allocations."TenantAccountId" = entry."TenantAccountId"
           AND debit_allocations."TenantLedgerEntryId" = entry."TenantLedgerEntryId"
          WHERE entry."Direction" = 'Debit'
            AND entry."EntryType" <> 'Refund'
        ),
        charge_summary AS (
          SELECT charge."PortfolioId",
                 charge."TenantAccountId",
                 COALESCE(sum(charge."OpenAmount") FILTER (
                   WHERE charge."DueOn" < effective_time."BusinessDate"
                 ), 0::numeric) AS "PastDueAmount",
                 count(*) FILTER (
                   WHERE charge."DueOn" < effective_time."BusinessDate"
                     AND charge."OpenAmount" > 0
                 )::int AS "PastDueCount",
                 min(charge."DueOn") FILTER (
                   WHERE charge."DueOn" >= effective_time."BusinessDate"
                     AND charge."OpenAmount" > 0
                 ) AS "NextDueOn"
          FROM open_charges AS charge
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = charge."PortfolioId"
          GROUP BY charge."PortfolioId", charge."TenantAccountId"
        ),
        next_due_amount AS (
          SELECT charge."PortfolioId",
                 charge."TenantAccountId",
                 sum(charge."OpenAmount") AS "NextDueAmount"
          FROM open_charges AS charge
          JOIN charge_summary
            ON charge_summary."PortfolioId" = charge."PortfolioId"
           AND charge_summary."TenantAccountId" = charge."TenantAccountId"
           AND charge_summary."NextDueOn" = charge."DueOn"
          GROUP BY charge."PortfolioId", charge."TenantAccountId"
        ),
        active_conditions AS (
          SELECT condition."PortfolioId",
                 condition."TenantAccountId",
                 bool_or(condition."Condition" = 'Collections') AS "HasCollections",
                 bool_or(condition."Condition" = 'PaymentPlan') AS "HasPaymentPlan"
          FROM "TenantAccountConditionPeriods" AS condition
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = condition."PortfolioId"
          WHERE condition."StartedAtUtc" <= effective_time."NowUtc"
            AND (condition."EndedAtUtc" IS NULL OR condition."EndedAtUtc" > effective_time."NowUtc")
          GROUP BY condition."PortfolioId", condition."TenantAccountId"
        ),
        receipt_rows AS (
          SELECT entry."PortfolioId",
                 entry."TenantAccountId",
                 entry."EffectiveOn" AS "LastReceiptOn",
                 entry."NetAmount" AS "LastReceiptAmount",
                 row_number() OVER (
                   PARTITION BY entry."PortfolioId", entry."TenantAccountId"
                   ORDER BY entry."EffectiveOn" DESC, entry."PostedAtUtc" DESC,
                            entry."TenantLedgerEntryId" DESC
                 ) AS receipt_rank
          FROM effective_entries AS entry
          WHERE entry."EntryType" = 'PaymentReceipt'
            AND entry."NetAmount" > 0
        )
        SELECT account."PortfolioId",
               account."LeaseManagementId",
               account."Id" AS "TenantAccountId",
               effective_time."NowUtc" AS "EffectiveNowUtc",
               effective_time."BusinessDate",
               account."Currency",
               COALESCE(entry_totals."TotalDebits", 0::numeric) AS "TotalDebits",
               COALESCE(entry_totals."TotalCredits", 0::numeric) AS "TotalCredits",
               COALESCE(entry_totals."TotalDebits", 0::numeric)
                 - COALESCE(entry_totals."TotalCredits", 0::numeric) AS "ReceivableBalance",
               GREATEST(
                 COALESCE(entry_totals."TotalCredits", 0::numeric)
                   - COALESCE(entry_totals."ReturnedPaymentDebits", 0::numeric)
                   - COALESCE(account_allocations."NetAllocations", 0::numeric),
                 0::numeric
               ) AS "UnappliedCredit",
               COALESCE(charge_summary."PastDueAmount", 0::numeric) AS "PastDueAmount",
               COALESCE(charge_summary."PastDueCount", 0) AS "PastDueCount",
               charge_summary."NextDueOn",
               COALESCE(next_due_amount."NextDueAmount", 0::numeric) AS "NextDueAmount",
               CASE
                 WHEN COALESCE(active_conditions."HasCollections", false) THEN 'Collections'
                 WHEN COALESCE(active_conditions."HasPaymentPlan", false) THEN 'PaymentPlan'
                 WHEN COALESCE(charge_summary."PastDueAmount", 0::numeric) > 0 THEN 'PastDue'
                 WHEN COALESCE(entry_totals."TotalDebits", 0::numeric)
                        - COALESCE(entry_totals."TotalCredits", 0::numeric) < 0
                   THEN 'Credit'
                 ELSE 'Current'
               END AS "Condition",
               last_receipt."LastReceiptOn",
               last_receipt."LastReceiptAmount"
        FROM "TenantAccounts" AS account
        JOIN effective_portfolio_time AS effective_time
          ON effective_time."PortfolioId" = account."PortfolioId"
        LEFT JOIN account_entry_totals AS entry_totals
          ON entry_totals."PortfolioId" = account."PortfolioId"
         AND entry_totals."TenantAccountId" = account."Id"
        LEFT JOIN account_allocations
          ON account_allocations."PortfolioId" = account."PortfolioId"
         AND account_allocations."TenantAccountId" = account."Id"
        LEFT JOIN charge_summary
          ON charge_summary."PortfolioId" = account."PortfolioId"
         AND charge_summary."TenantAccountId" = account."Id"
        LEFT JOIN next_due_amount
          ON next_due_amount."PortfolioId" = account."PortfolioId"
         AND next_due_amount."TenantAccountId" = account."Id"
        LEFT JOIN active_conditions
          ON active_conditions."PortfolioId" = account."PortfolioId"
         AND active_conditions."TenantAccountId" = account."Id"
        LEFT JOIN receipt_rows AS last_receipt
          ON last_receipt."PortfolioId" = account."PortfolioId"
         AND last_receipt."TenantAccountId" = account."Id"
         AND last_receipt.receipt_rank = 1;
        """;
}
