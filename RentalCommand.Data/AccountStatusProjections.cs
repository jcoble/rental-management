using Microsoft.EntityFrameworkCore;

namespace RentalCommand.Data;

/// <summary>Database-derived lifecycle and billing facts for one Addendum version.</summary>
public sealed class LeaseAddendumStatusProjection
{
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int LeaseAddendumId { get; set; }
    public int BaseAgreementId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateOnly EffectiveFromOn { get; set; }
    public DateOnly? EffectiveThroughExclusiveOn { get; set; }
    public string AddendumStatus { get; set; } = string.Empty;
    public int FinancialEffectCount { get; set; }
    public bool HasCurrentlyBillableFinancialEffect { get; set; }
}

/// <summary>Database-derived receivable position for one continuous tenant account.</summary>
public sealed class TenantAccountBalanceProjection
{
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int TenantAccountId { get; set; }
    public DateTime EffectiveNowUtc { get; set; }
    public DateOnly BusinessDate { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal ReceivableBalance { get; set; }
    public decimal UnappliedCredit { get; set; }
    public decimal PastDueAmount { get; set; }
    public int PastDueCount { get; set; }
    public DateOnly? NextDueOn { get; set; }
    public decimal NextDueAmount { get; set; }
    public string Condition { get; set; } = string.Empty;
    public DateOnly? LastReceiptOn { get; set; }
    public decimal? LastReceiptAmount { get; set; }
}

/// <summary>Database-derived fund position for one security-deposit subledger.</summary>
public sealed class SecurityDepositBalanceProjection
{
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int TenantAccountId { get; set; }
    public int SecurityDepositAccountId { get; set; }
    public DateTime EffectiveNowUtc { get; set; }
    public DateOnly BusinessDate { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal TotalReceived { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalRefunded { get; set; }
    public decimal TotalTransferredIn { get; set; }
    public decimal TotalTransferredOut { get; set; }
    public decimal NetAdjustments { get; set; }
    public decimal HeldBalance { get; set; }
    public string DepositStatus { get; set; } = string.Empty;
}

/// <summary>Database-derived open position for one immutable debit entry.</summary>
public sealed class TenantChargeBalanceProjection
{
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public long TenantLedgerEntryId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public DateOnly EffectiveOn { get; set; }
    public DateOnly? DueOn { get; set; }
    public decimal OriginalAmount { get; set; }
    public decimal ReversedAmount { get; set; }
    public decimal NetAllocations { get; set; }
    public decimal OpenAmount { get; set; }
    public bool IsPastDue { get; set; }
}

internal static class AccountStatusProjectionModelConfiguration
{
    internal static void ConfigureAccountStatusProjections(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeaseAddendumStatusProjection>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_lease_addendum_status");
            entity.Property(row => row.BusinessDate).HasColumnType("date");
            entity.Property(row => row.EffectiveFromOn).HasColumnType("date");
            entity.Property(row => row.EffectiveThroughExclusiveOn).HasColumnType("date");
            entity.Property(row => row.AddendumStatus).HasMaxLength(40);
        });

        modelBuilder.Entity<TenantAccountBalanceProjection>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_tenant_account_balances");
            entity.Property(row => row.BusinessDate).HasColumnType("date");
            entity.Property(row => row.NextDueOn).HasColumnType("date");
            entity.Property(row => row.LastReceiptOn).HasColumnType("date");
            entity.Property(row => row.Currency).HasMaxLength(3);
            entity.Property(row => row.Condition).HasMaxLength(30);
            ConfigureMoney(entity.Property(row => row.TotalDebits));
            ConfigureMoney(entity.Property(row => row.TotalCredits));
            ConfigureMoney(entity.Property(row => row.ReceivableBalance));
            ConfigureMoney(entity.Property(row => row.UnappliedCredit));
            ConfigureMoney(entity.Property(row => row.PastDueAmount));
            ConfigureMoney(entity.Property(row => row.NextDueAmount));
            ConfigureMoney(entity.Property(row => row.LastReceiptAmount));
        });

        modelBuilder.Entity<SecurityDepositBalanceProjection>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_security_deposit_balances");
            entity.Property(row => row.BusinessDate).HasColumnType("date");
            entity.Property(row => row.Currency).HasMaxLength(3);
            entity.Property(row => row.DepositStatus).HasMaxLength(30);
            ConfigureMoney(entity.Property(row => row.TotalReceived));
            ConfigureMoney(entity.Property(row => row.TotalDeductions));
            ConfigureMoney(entity.Property(row => row.TotalRefunded));
            ConfigureMoney(entity.Property(row => row.TotalTransferredIn));
            ConfigureMoney(entity.Property(row => row.TotalTransferredOut));
            ConfigureMoney(entity.Property(row => row.NetAdjustments));
            ConfigureMoney(entity.Property(row => row.HeldBalance));
        });

        modelBuilder.Entity<TenantChargeBalanceProjection>(entity =>
        {
            entity.HasNoKey();
            entity.ToView("vw_tenant_charge_balances");
            entity.Property(row => row.BusinessDate).HasColumnType("date");
            entity.Property(row => row.EntryType).HasMaxLength(40);
            entity.Property(row => row.Currency).HasMaxLength(3);
            entity.Property(row => row.EffectiveOn).HasColumnType("date");
            entity.Property(row => row.DueOn).HasColumnType("date");
            ConfigureMoney(entity.Property(row => row.OriginalAmount));
            ConfigureMoney(entity.Property(row => row.ReversedAmount));
            ConfigureMoney(entity.Property(row => row.NetAllocations));
            ConfigureMoney(entity.Property(row => row.OpenAmount));
        });
    }

    private static void ConfigureMoney<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> property)
        => property.HasPrecision(18, 2);
}

internal static class LeaseAddendumStatusViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_lease_addendum_status\";";
    public const string Create =
        "CREATE VIEW \"vw_lease_addendum_status\" WITH (security_invoker = true) AS\n" + Definition;

    public const string Definition = """
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
        financial_effects AS (
          SELECT effect."PortfolioId",
                 effect."LeaseAddendumId",
                 count(*)::int AS "FinancialEffectCount",
                 bool_or(
                   CASE
                     WHEN effect."EffectType" = 'OneTimeCharge' THEN
                       effect."DueOn" <= effective_time."BusinessDate"
                       AND NOT EXISTS (
                         SELECT 1
                         FROM "TenantLedgerEntries" AS posted_charge
                         WHERE posted_charge."PortfolioId" = effect."PortfolioId"
                           AND posted_charge."LeaseAddendumId" = effect."LeaseAddendumId"
                           AND posted_charge."EntryType" = 'AddendumCharge'
                           AND posted_charge."BusinessKey" = 'addendum-effect:' || effect."Id"::text
                       )
                     ELSE
                       effect."EffectiveFromOn" <= effective_time."BusinessDate"
                       AND (effect."EffectiveThroughOn" IS NULL
                            OR effective_time."BusinessDate" < effect."EffectiveThroughOn" + 1)
                   END
                 ) AS "HasEffectiveFinancialEffect"
          FROM "LeaseAddendumFinancialEffects" AS effect
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = effect."PortfolioId"
          GROUP BY effect."PortfolioId", effect."LeaseAddendumId"
        ),
        status_rows AS (
          SELECT addendum."PortfolioId",
                 addendum."LeaseManagementId",
                 addendum."Id" AS "LeaseAddendumId",
                 addendum."BaseAgreementId",
                 effective_time."BusinessDate",
                 addendum."EffectiveFromOn",
                 LEAST(addendum."EffectiveThroughOn" + 1, addendum."SupersededEffectiveOn")
                   AS "EffectiveThroughExclusiveOn",
                 COALESCE(financial_effects."FinancialEffectCount", 0) AS "FinancialEffectCount",
                 COALESCE(financial_effects."HasEffectiveFinancialEffect", false)
                   AS "HasEffectiveFinancialEffect",
                 CASE
                   WHEN addendum."VoidedAtUtc" IS NOT NULL THEN 'Void'
                   WHEN addendum."DraftCanceledAtUtc" IS NOT NULL THEN 'Canceled'
                   WHEN addendum."IssuedAtUtc" IS NULL THEN 'Draft'
                   WHEN addendum."FullyExecutedAtUtc" IS NULL THEN 'AwaitingSignatures'
                   WHEN addendum."SupersededEffectiveOn" IS NOT NULL
                        AND effective_time."BusinessDate" >= addendum."SupersededEffectiveOn"
                     THEN 'Superseded'
                   WHEN effective_time."BusinessDate" < addendum."EffectiveFromOn" THEN 'Upcoming'
                   WHEN effective_time."BusinessDate" >= addendum."EffectiveFromOn"
                        AND (addendum."EffectiveThroughOn" IS NULL
                             OR effective_time."BusinessDate" < addendum."EffectiveThroughOn" + 1)
                        AND (addendum."SupersededEffectiveOn" IS NULL
                             OR effective_time."BusinessDate" < addendum."SupersededEffectiveOn")
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
        SELECT "PortfolioId",
               "LeaseManagementId",
               "LeaseAddendumId",
               "BaseAgreementId",
               "BusinessDate",
               "EffectiveFromOn",
               "EffectiveThroughExclusiveOn",
               "AddendumStatus",
               "FinancialEffectCount",
               ("AddendumStatus" = 'Active' AND "HasEffectiveFinancialEffect")
                 AS "HasCurrentlyBillableFinancialEffect"
        FROM status_rows;
        """;
}

internal static class TenantChargeBalanceViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_tenant_charge_balances\";";
    public const string Create =
        "CREATE VIEW \"vw_tenant_charge_balances\" WITH (security_invoker = true) AS\n" + Definition;

    public const string Definition = """
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
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = reversal."PortfolioId"
          WHERE reversal."EntryType" = 'Reversal'
            AND reversal."EffectiveOn" <= effective_time."BusinessDate"
          GROUP BY reversal."PortfolioId", reversal."TenantAccountId", reversal."ReversesEntryId"
        ),
        debit_allocations AS (
          SELECT allocation."PortfolioId",
                 allocation."TenantAccountId",
                 allocation."DebitEntryId" AS "TenantLedgerEntryId",
                 sum(allocation."Amount") AS "NetAllocations"
          FROM "TenantLedgerAllocations" AS allocation
          JOIN "TenantLedgerEntries" AS credit
            ON credit."PortfolioId" = allocation."PortfolioId"
           AND credit."TenantAccountId" = allocation."TenantAccountId"
           AND credit."Id" = allocation."CreditEntryId"
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = allocation."PortfolioId"
          WHERE credit."Direction" = 'Credit'
            AND COALESCE(allocation."EffectiveOn", credit."EffectiveOn") <= effective_time."BusinessDate"
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
}

internal static class TenantAccountBalanceViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_tenant_account_balances\";";
    public const string Create =
        "CREATE VIEW \"vw_tenant_account_balances\" WITH (security_invoker = true) AS\n" + Definition;

    public const string Definition = """
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
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = reversal."PortfolioId"
          WHERE reversal."EntryType" = 'Reversal'
            AND reversal."EffectiveOn" <= effective_time."BusinessDate"
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
          JOIN "TenantLedgerEntries" AS credit
            ON credit."PortfolioId" = allocation."PortfolioId"
           AND credit."TenantAccountId" = allocation."TenantAccountId"
           AND credit."Id" = allocation."CreditEntryId"
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = allocation."PortfolioId"
          WHERE credit."Direction" = 'Credit'
            AND COALESCE(allocation."EffectiveOn", credit."EffectiveOn") <= effective_time."BusinessDate"
          GROUP BY allocation."PortfolioId", allocation."TenantAccountId", allocation."DebitEntryId"
        ),
        account_allocations AS (
          SELECT allocation."PortfolioId",
                 allocation."TenantAccountId",
                 sum(allocation."Amount") AS "NetAllocations"
          FROM "TenantLedgerAllocations" AS allocation
          JOIN "TenantLedgerEntries" AS credit
            ON credit."PortfolioId" = allocation."PortfolioId"
           AND credit."TenantAccountId" = allocation."TenantAccountId"
           AND credit."Id" = allocation."CreditEntryId"
          JOIN effective_portfolio_time AS effective_time
            ON effective_time."PortfolioId" = allocation."PortfolioId"
          WHERE credit."Direction" = 'Credit'
            AND COALESCE(allocation."EffectiveOn", credit."EffectiveOn") <= effective_time."BusinessDate"
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

internal static class SecurityDepositBalanceViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_security_deposit_balances\";";
    public const string Create =
        "CREATE VIEW \"vw_security_deposit_balances\" WITH (security_invoker = true) AS\n" + Definition;

    public const string Definition = """
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
                 reversal."SecurityDepositAccountId",
                 reversal."ReversesEntryId" AS "SecurityDepositEntryId",
                 sum(reversal."Amount") AS "ReversedAmount"
          FROM "SecurityDepositEntries" AS reversal
          WHERE reversal."EntryType" = 'Reversal'
          GROUP BY reversal."PortfolioId", reversal."SecurityDepositAccountId",
                   reversal."ReversesEntryId"
        ),
        effective_entries AS (
          SELECT entry."PortfolioId",
                 entry."SecurityDepositAccountId",
                 entry."EntryType",
                 entry."Direction",
                 GREATEST(entry."Amount" - COALESCE(entry_reversals."ReversedAmount", 0::numeric), 0::numeric)
                   AS "NetAmount"
          FROM "SecurityDepositEntries" AS entry
          LEFT JOIN entry_reversals
            ON entry_reversals."PortfolioId" = entry."PortfolioId"
           AND entry_reversals."SecurityDepositAccountId" = entry."SecurityDepositAccountId"
           AND entry_reversals."SecurityDepositEntryId" = entry."Id"
          WHERE entry."EntryType" <> 'Reversal'
        ),
        deposit_totals AS (
          SELECT entry."PortfolioId",
                 entry."SecurityDepositAccountId",
                 COALESCE(sum(entry."NetAmount") FILTER (WHERE entry."EntryType" = 'Receipt'), 0::numeric)
                   AS "TotalReceived",
                 COALESCE(sum(entry."NetAmount") FILTER (WHERE entry."EntryType" = 'Deduction'), 0::numeric)
                   AS "TotalDeductions",
                 COALESCE(sum(entry."NetAmount") FILTER (WHERE entry."EntryType" = 'Refund'), 0::numeric)
                   AS "TotalRefunded",
                 COALESCE(sum(entry."NetAmount") FILTER (WHERE entry."EntryType" = 'TransferIn'), 0::numeric)
                   AS "TotalTransferredIn",
                 COALESCE(sum(entry."NetAmount") FILTER (WHERE entry."EntryType" = 'TransferOut'), 0::numeric)
                   AS "TotalTransferredOut",
                 COALESCE(sum(
                   CASE WHEN entry."EntryType" = 'Adjustment' AND entry."Direction" = 'Increase'
                          THEN entry."NetAmount"
                        WHEN entry."EntryType" = 'Adjustment' AND entry."Direction" = 'Decrease'
                          THEN -entry."NetAmount"
                        ELSE 0::numeric END
                 ), 0::numeric) AS "NetAdjustments",
                 COALESCE(sum(
                   CASE WHEN entry."Direction" = 'Increase' THEN entry."NetAmount"
                        ELSE -entry."NetAmount" END
                 ), 0::numeric) AS "HeldBalance"
          FROM effective_entries AS entry
          GROUP BY entry."PortfolioId", entry."SecurityDepositAccountId"
        )
        SELECT deposit_account."PortfolioId",
               tenant_account."LeaseManagementId",
               deposit_account."TenantAccountId",
               deposit_account."Id" AS "SecurityDepositAccountId",
               effective_time."NowUtc" AS "EffectiveNowUtc",
               effective_time."BusinessDate",
               deposit_account."Currency",
               COALESCE(totals."TotalReceived", 0::numeric) AS "TotalReceived",
               COALESCE(totals."TotalDeductions", 0::numeric) AS "TotalDeductions",
               COALESCE(totals."TotalRefunded", 0::numeric) AS "TotalRefunded",
               COALESCE(totals."TotalTransferredIn", 0::numeric) AS "TotalTransferredIn",
               COALESCE(totals."TotalTransferredOut", 0::numeric) AS "TotalTransferredOut",
               COALESCE(totals."NetAdjustments", 0::numeric) AS "NetAdjustments",
               COALESCE(totals."HeldBalance", 0::numeric) AS "HeldBalance",
               -- A funded relationship remains Held until both the relationship is closed and
               -- every dollar has been disposed. Final labels then come only from typed facts.
               -- The final Held fallback deliberately exposes an impossible closed/funded/zero
               -- row with no disposition instead of falsely calling it Returned; reconciliation
               -- reports that contradiction in the later exceptions view.
               CASE
                 WHEN COALESCE(totals."TotalReceived", 0::numeric)
                        + COALESCE(totals."TotalTransferredIn", 0::numeric)
                        + GREATEST(COALESCE(totals."NetAdjustments", 0::numeric), 0::numeric) = 0
                   THEN 'NotFunded'
                 WHEN management."AccountClosedAtUtc" IS NULL
                      OR COALESCE(totals."HeldBalance", 0::numeric) > 0
                   THEN 'Held'
                 WHEN COALESCE(totals."TotalDeductions", 0::numeric) > 0
                      AND COALESCE(totals."TotalRefunded", 0::numeric)
                            + COALESCE(totals."TotalTransferredOut", 0::numeric) = 0
                   THEN 'Withheld'
                 WHEN COALESCE(totals."TotalDeductions", 0::numeric) > 0
                   THEN 'PartiallyReturned'
                 WHEN COALESCE(totals."TotalRefunded", 0::numeric)
                        + COALESCE(totals."TotalTransferredOut", 0::numeric) > 0
                   THEN 'Returned'
                 ELSE 'Held'
               END AS "DepositStatus"
        FROM "SecurityDepositAccounts" AS deposit_account
        JOIN "TenantAccounts" AS tenant_account
          ON tenant_account."Id" = deposit_account."TenantAccountId"
         AND tenant_account."PortfolioId" = deposit_account."PortfolioId"
        JOIN "LeaseManagements" AS management
          ON management."Id" = tenant_account."LeaseManagementId"
         AND management."PortfolioId" = tenant_account."PortfolioId"
        JOIN effective_portfolio_time AS effective_time
          ON effective_time."PortfolioId" = deposit_account."PortfolioId"
        LEFT JOIN deposit_totals AS totals
          ON totals."PortfolioId" = deposit_account."PortfolioId"
         AND totals."SecurityDepositAccountId" = deposit_account."Id";
        """;
}
