using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260728203000_AddPortalTenantAccountHistoryFunction")]
public sealed class AddPortalTenantAccountHistoryFunction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        CREATE OR REPLACE FUNCTION rc_portal_tenant_account_history(
          p_portfolio_id integer,
          p_user_id integer,
          p_access_context_id integer,
          p_access_revision bigint,
          p_tenant_account_id integer,
          p_period text,
          p_from_on date,
          p_to_on date,
          p_skip integer,
          p_take integer,
          p_focused_entry_id bigint)
        RETURNS TABLE (
          "TenantAccountId" integer,
          "LeaseManagementId" integer,
          "Currency" text,
          "BusinessDate" date,
          "Period" text,
          "PeriodFrom" date,
          "PeriodTo" date,
          "CurrentDue" numeric,
          "BeginningBalance" numeric,
          "ClosingBalance" numeric,
          "TotalCount" integer,
          "ItemsJson" text)
        LANGUAGE sql
        STABLE
        SECURITY INVOKER
        AS $function$
        WITH authorized_account AS MATERIALIZED (
          SELECT account."PortfolioId",
                 account."Id" AS "TenantAccountId",
                 account."LeaseManagementId",
                 account."RentTrackingStartOn",
                 balance."BusinessDate",
                 balance."Currency",
                 balance."ReceivableBalance"
          FROM "TenantAccounts" AS account
          JOIN "vw_effective_tenant_access" AS access
            ON access."PortfolioId" = account."PortfolioId"
           AND access."TenantAccountId" = account."Id"
           AND access."LeaseManagementId" = account."LeaseManagementId"
           AND access."UserId" = p_user_id
           AND access."AccessContextId" = p_access_context_id
           AND access."AccessRevision" = p_access_revision
          JOIN "vw_tenant_account_balances" AS balance
            ON balance."PortfolioId" = account."PortfolioId"
           AND balance."TenantAccountId" = account."Id"
          WHERE account."PortfolioId" = p_portfolio_id
            AND account."Id" = p_tenant_account_id
        ),
        period_bounds AS MATERIALIZED (
          SELECT account.*,
                 CASE p_period
                   WHEN 'all' THEN NULL::date
                   WHEN 'previousMonth' THEN
                     (date_trunc('month', account."BusinessDate"::timestamp) - interval '1 month')::date
                   WHEN 'thisYear' THEN make_date(extract(year FROM account."BusinessDate")::integer, 1, 1)
                   WHEN 'last3Months' THEN
                     (date_trunc('month', account."BusinessDate"::timestamp) - interval '2 months')::date
                   WHEN 'custom' THEN COALESCE(p_from_on, account."RentTrackingStartOn")
                   ELSE date_trunc('month', account."BusinessDate"::timestamp)::date
                 END AS "PeriodFrom",
                 LEAST(
                   account."BusinessDate",
                   CASE
                     WHEN p_period = 'custom' THEN COALESCE(p_to_on, account."BusinessDate")
                     WHEN p_period = 'previousMonth' THEN
                       (date_trunc('month', account."BusinessDate"::timestamp) - interval '1 day')::date
                     ELSE account."BusinessDate"
                   END
                 ) AS "PeriodTo"
          FROM authorized_account AS account
        ),
        reversal_totals AS MATERIALIZED (
          SELECT reversal."PortfolioId",
                 reversal."TenantAccountId",
                 reversal."ReversesEntryId",
                 sum(reversal."Amount") AS "ReversedAmount"
          FROM period_bounds AS bounds
          JOIN "TenantLedgerEntries" AS reversal
            ON reversal."PortfolioId" = bounds."PortfolioId"
           AND reversal."TenantAccountId" = bounds."TenantAccountId"
          WHERE reversal."EntryType" = 'Reversal'
            AND reversal."EffectiveOn" <= bounds."BusinessDate"
          GROUP BY reversal."PortfolioId", reversal."TenantAccountId", reversal."ReversesEntryId"
        ),
        visible_entries AS MATERIALIZED (
          SELECT entry.*,
                 CASE WHEN entry."Direction" = 'Debit' THEN entry."Amount" ELSE -entry."Amount" END
                   AS "SignedAmount"
          FROM period_bounds AS bounds
          JOIN "TenantLedgerEntries" AS entry
            ON entry."PortfolioId" = bounds."PortfolioId"
           AND entry."TenantAccountId" = bounds."TenantAccountId"
          LEFT JOIN "TenantLedgerEntries" AS reversed_entry
            ON reversed_entry."PortfolioId" = entry."PortfolioId"
           AND reversed_entry."TenantAccountId" = entry."TenantAccountId"
           AND reversed_entry."Id" = entry."ReversesEntryId"
          LEFT JOIN reversal_totals AS original_reversals
            ON original_reversals."PortfolioId" = entry."PortfolioId"
           AND original_reversals."TenantAccountId" = entry."TenantAccountId"
           AND original_reversals."ReversesEntryId" = entry."Id"
          LEFT JOIN reversal_totals AS reversed_entry_reversals
            ON reversed_entry_reversals."PortfolioId" = reversed_entry."PortfolioId"
           AND reversed_entry_reversals."TenantAccountId" = reversed_entry."TenantAccountId"
           AND reversed_entry_reversals."ReversesEntryId" = reversed_entry."Id"
          WHERE entry."EffectiveOn" <= bounds."BusinessDate"
            -- RentTrackingStartOn hides only fully corrected pre-tracking backfill pairs. An
            -- unreversed pre-marker charge remains visible because a later receipt and the current
            -- balance cannot be explained without it.
            AND NOT (
              bounds."RentTrackingStartOn" IS NOT NULL
              AND entry."EffectiveOn" < bounds."RentTrackingStartOn"
              AND entry."EntryType" <> 'Reversal'
              AND COALESCE(original_reversals."ReversedAmount", 0::numeric) >= entry."Amount")
            AND NOT (
              bounds."RentTrackingStartOn" IS NOT NULL
              AND entry."EntryType" = 'Reversal'
              AND reversed_entry."EffectiveOn" < bounds."RentTrackingStartOn"
              AND COALESCE(reversed_entry_reversals."ReversedAmount", 0::numeric)
                    >= reversed_entry."Amount")
        ),
        account_summary AS MATERIALIZED (
          SELECT bounds."TenantAccountId",
                 bounds."LeaseManagementId",
                 bounds."Currency",
                 bounds."BusinessDate",
                 p_period AS "Period",
                 bounds."PeriodFrom",
                 bounds."PeriodTo",
                 bounds."ReceivableBalance" AS "CurrentDue",
                 COALESCE(sum(entry."SignedAmount") FILTER (
                   WHERE bounds."PeriodFrom" IS NOT NULL
                     AND entry."EffectiveOn" < bounds."PeriodFrom"), 0::numeric) AS "BeginningBalance",
                 COALESCE(sum(entry."SignedAmount") FILTER (
                   WHERE entry."EffectiveOn" <= bounds."PeriodTo"), 0::numeric) AS "ClosingBalance"
          FROM period_bounds AS bounds
          LEFT JOIN visible_entries AS entry ON true
          GROUP BY bounds."TenantAccountId", bounds."LeaseManagementId", bounds."Currency",
                   bounds."ReceivableBalance",
                   bounds."BusinessDate", bounds."PeriodFrom", bounds."PeriodTo"
        ),
        period_entries AS MATERIALIZED (
          SELECT entry.*,
                 bounds."PeriodFrom",
                 bounds."PeriodTo",
                 row_number() OVER (
                   ORDER BY entry."EffectiveOn",
                            CASE WHEN entry."Direction" = 'Debit' THEN 0 ELSE 1 END,
                            entry."Id") AS history_row,
                 count(*) OVER ()::integer AS history_count,
                 COALESCE(summary."BeginningBalance", 0::numeric)
                   + sum(entry."SignedAmount") OVER (
                       ORDER BY entry."EffectiveOn",
                                CASE WHEN entry."Direction" = 'Debit' THEN 0 ELSE 1 END,
                                entry."Id"
                       ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
                     AS "RunningBalance",
                 reversed_by."ReversedByEntryId",
                 COALESCE(charge."OpenAmount", 0::numeric) AS "OpenAmount"
          FROM period_bounds AS bounds
          JOIN account_summary AS summary ON true
          JOIN visible_entries AS entry
            ON (bounds."PeriodFrom" IS NULL OR entry."EffectiveOn" >= bounds."PeriodFrom")
           AND entry."EffectiveOn" <= bounds."PeriodTo"
          LEFT JOIN "vw_tenant_charge_balances" AS charge
            ON charge."PortfolioId" = entry."PortfolioId"
           AND charge."TenantAccountId" = entry."TenantAccountId"
           AND charge."TenantLedgerEntryId" = entry."Id"
          LEFT JOIN LATERAL (
            SELECT reversal."Id" AS "ReversedByEntryId"
            FROM "TenantLedgerEntries" AS reversal
            WHERE reversal."PortfolioId" = entry."PortfolioId"
              AND reversal."TenantAccountId" = entry."TenantAccountId"
              AND reversal."ReversesEntryId" = entry."Id"
              AND reversal."EffectiveOn" <= bounds."BusinessDate"
            ORDER BY reversal."EffectiveOn", reversal."PostedAtUtc", reversal."Id"
            LIMIT 1
          ) AS reversed_by ON true
        ),
        selected_entries AS (
          SELECT entry.*
          FROM period_entries AS entry
          WHERE (entry.history_row > GREATEST(p_skip, 0)
                 AND entry.history_row <= GREATEST(p_skip, 0) + LEAST(GREATEST(p_take, 1), 200))
             OR entry."Id" = p_focused_entry_id
        ),
        period_count AS (
          SELECT count(*)::integer AS "TotalCount"
          FROM period_entries
        )
        SELECT summary."TenantAccountId",
               summary."LeaseManagementId",
               summary."Currency",
               summary."BusinessDate",
               summary."Period",
               summary."PeriodFrom",
               summary."PeriodTo",
               summary."CurrentDue",
               summary."BeginningBalance",
               summary."ClosingBalance",
               period_count."TotalCount",
               COALESCE(
                 jsonb_agg(
                   jsonb_build_object(
                     'tenantLedgerEntryId', entry."Id",
                     'entryType', entry."EntryType"::text,
                     'direction', entry."Direction"::text,
                     'displayType', CASE entry."EntryType"::text
                       WHEN 'PaymentReceipt' THEN 'Payment'
                       WHEN 'Credit' THEN 'Credit'
                       WHEN 'Refund' THEN 'Refund'
                       WHEN 'Reversal' THEN 'Reversal'
                       WHEN 'TransferIn' THEN 'Credit'
                       WHEN 'TransferOut' THEN 'Charge'
                       WHEN 'Adjustment' THEN
                         CASE WHEN entry."Direction" = 'Debit' THEN 'Charge' ELSE 'Credit' END
                       ELSE 'Charge'
                     END,
                     'description', entry."Description",
                     'effectiveOn', entry."EffectiveOn",
                     'dueOn', entry."DueOn",
                     'postedAtUtc', entry."PostedAtUtc",
                     'signedAmount', entry."SignedAmount",
                     'runningBalance', entry."RunningBalance",
                     'openAmount', entry."OpenAmount",
                     'payable', entry."OpenAmount" > 0,
                     'reversesEntryId', entry."ReversesEntryId",
                     'reversedByEntryId', entry."ReversedByEntryId",
                     'isFocused', COALESCE(entry."Id" = p_focused_entry_id, false))
                   ORDER BY entry."EffectiveOn",
                            CASE WHEN entry."Direction" = 'Debit' THEN 0 ELSE 1 END,
                            entry."Id")
                   FILTER (WHERE entry."Id" IS NOT NULL),
                 '[]'::jsonb)::text AS "ItemsJson"
        FROM account_summary AS summary
        CROSS JOIN period_count
        LEFT JOIN selected_entries AS entry ON true
        GROUP BY summary."TenantAccountId", summary."LeaseManagementId", summary."Currency",
                 summary."BusinessDate", summary."Period", summary."PeriodFrom",
                 summary."PeriodTo", summary."CurrentDue", summary."BeginningBalance",
                 summary."ClosingBalance", period_count."TotalCount";
        $function$;

        REVOKE ALL ON FUNCTION rc_portal_tenant_account_history(
          integer, integer, integer, bigint, integer, text, date, date, integer, integer, bigint)
          FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_portal_tenant_account_history(
          integer, integer, integer, bigint, integer, text, date, date, integer, integer, bigint)
          TO rentalcommand_api;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DROP FUNCTION IF EXISTS rc_portal_tenant_account_history(
          integer, integer, integer, bigint, integer, text, date, date, integer, integer, bigint);
        """);
}
