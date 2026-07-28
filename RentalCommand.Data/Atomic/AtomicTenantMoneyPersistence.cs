using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicTenantMoneyPersistence : IAtomicTenantMoneyPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicTenantMoneyPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<IReadOnlyList<AtomicScheduledTenantCharge>> PostScheduledRentChargesAsync(
        int batchSize,
        DateTime postedAtUtc,
        CancellationToken ct = default)
    {
        if (batchSize is <= 0 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (postedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Scheduled charge posting time must be UTC.", nameof(postedAtUtc));

        using var lease = _scope.BeginInternalRawDml(
            "TenantLedgerEntries", AtomicRawDmlOperation.Insert);

        // The lateral month series is deliberately inside PostgreSQL. It produces every due period
        // through each portfolio's simulation-aware business horizon, applies the persisted
        // proration convention, removes already-posted business keys, and bounds the write before
        // anything is materialized by .NET.
        return await _db.Database.SqlQuery<AtomicScheduledTenantCharge>($"""
            WITH agreement_periods AS MATERIALIZED (
                SELECT agreement."PortfolioId",
                       account."Id" AS "TenantAccountId",
                       agreement."Id" AS "LeaseAgreementId",
                       account."Currency",
                       account."CreatedByUserId",
                       agreement."PublicId" AS agreement_public_id,
                       agreement."ChangeType",
                       agreement."BaseRentAmount",
                       agreement."RentDueDay",
                       agreement."GoverningFromOn",
                       agreement."TermStartOn",
                       agreement."TermEndOn",
                       agreement."SupersededEffectiveOn",
                       effective_date.business_date,
                       month.month_start::date AS month_start,
                       (month.month_start + interval '1 month - 1 day')::date AS month_end,
                       GREATEST(
                           agreement."TermStartOn",
                           agreement."GoverningFromOn",
                           COALESCE(account."RentTrackingStartOn", agreement."TermStartOn"),
                           month.month_start::date) AS period_start,
                       LEAST(
                           COALESCE(agreement."TermEndOn", 'infinity'::date),
                           COALESCE(agreement."SupersededEffectiveOn" - 1, 'infinity'::date),
                           (month.month_start + interval '1 month - 1 day')::date) AS period_end,
                       COALESCE(NULLIF(portfolio."Settings"::jsonb ->> 'prorationConvention', ''), 'ActualDays')
                           AS proration_convention
                FROM "LeaseAgreements" AS agreement
                JOIN "LeaseManagements" AS management
                  ON management."Id" = agreement."LeaseManagementId"
                 AND management."PortfolioId" = agreement."PortfolioId"
                JOIN "TenantAccounts" AS account
                  ON account."LeaseManagementId" = management."Id"
                 AND account."PortfolioId" = management."PortfolioId"
                JOIN "Portfolios" AS portfolio ON portfolio."Id" = agreement."PortfolioId"
                JOIN "AutomationSettings" AS settings
                  ON settings."PortfolioId" = agreement."PortfolioId"
                CROSS JOIN LATERAL (
                    SELECT rc_business_date(agreement."PortfolioId") AS business_date
                ) AS effective_date
                CROSS JOIN LATERAL generate_series(
                    date_trunc('month', GREATEST(
                        agreement."TermStartOn",
                        agreement."GoverningFromOn",
                        COALESCE(account."RentTrackingStartOn", agreement."TermStartOn"))::timestamp),
                    date_trunc('month', (effective_date.business_date
                        + GREATEST(settings."RentChargeLeadDays", 0))::timestamp),
                    interval '1 month') AS month(month_start)
                WHERE agreement."FullyExecutedAtUtc" IS NOT NULL
                  AND agreement."VoidedAtUtc" IS NULL
                  AND agreement."DraftCanceledAtUtc" IS NULL
                  AND management."CanceledAtUtc" IS NULL
                  AND account."ClosedAtUtc" IS NULL
                  AND agreement."BaseRentAmount" > 0
            ), candidates AS MATERIALIZED (
                SELECT period.*,
                       GREATEST(
                           make_date(
                               EXTRACT(year FROM period.month_start)::integer,
                               EXTRACT(month FROM period.month_start)::integer,
                               LEAST(
                                   GREATEST(period."RentDueDay"::integer, 1),
                                   EXTRACT(day FROM period.month_end)::integer)),
                           period.period_start) AS due_on,
                       'rent:' || period.agreement_public_id::text || ':'
                           || to_char(period.month_start, 'YYYY-MM') AS business_key
                FROM agreement_periods AS period
                WHERE period.period_start <= period.period_end
            ), eligible AS MATERIALIZED (
                SELECT candidate.*,
                       CASE
                         WHEN candidate.period_start = candidate.month_start
                          AND candidate.period_end = candidate.month_end
                           THEN candidate."BaseRentAmount"
                         WHEN lower(candidate.proration_convention) = 'thirtyday'
                           THEN round(candidate."BaseRentAmount" * (
                               (CASE WHEN candidate.period_end = candidate.month_end
                                     THEN 30
                                     ELSE LEAST(EXTRACT(day FROM candidate.period_end)::integer, 30)
                                END)
                               - LEAST(EXTRACT(day FROM candidate.period_start)::integer, 30) + 1
                             )::numeric / 30::numeric, 2)
                         ELSE round(candidate."BaseRentAmount" *
                             (candidate.period_end - candidate.period_start + 1)::numeric /
                             EXTRACT(day FROM candidate.month_end)::numeric, 2)
                       END AS charge_amount
                FROM candidates AS candidate
                JOIN "AutomationSettings" AS settings
                  ON settings."PortfolioId" = candidate."PortfolioId"
                WHERE candidate.due_on <= candidate.business_date
                    + GREATEST(settings."RentChargeLeadDays", 0)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerEntries" AS existing
                      WHERE existing."TenantAccountId" = candidate."TenantAccountId"
                        AND existing."BusinessKey" = candidate.business_key)
                  AND (
                      candidate."ChangeType" NOT IN ('Correction', 'Restatement')
                      OR NOT EXISTS (
                          SELECT 1
                          FROM "TenantLedgerEntries" AS existing_period
                          WHERE existing_period."PortfolioId" = candidate."PortfolioId"
                            AND existing_period."TenantAccountId" = candidate."TenantAccountId"
                            AND existing_period."EntryType" = 'RentCharge'
                            AND existing_period."DueOn" >= candidate.month_start
                            AND existing_period."DueOn" < candidate.month_start + interval '1 month'))
                ORDER BY candidate.due_on, candidate."TenantAccountId", candidate."LeaseAgreementId"
                LIMIT {batchSize}
            ), inserted AS (
                INSERT INTO "TenantLedgerEntries" (
                    "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount",
                    "Currency", "EffectiveOn", "DueOn", "PostedAtUtc", "Description",
                    "BusinessKey", "LeaseAgreementId", "CreatedByUserId")
                SELECT eligible."PortfolioId", eligible."TenantAccountId", 'RentCharge', 'Debit',
                       eligible.charge_amount, eligible."Currency", eligible.due_on,
                       eligible.due_on, {postedAtUtc},
                       'Rent due ' || to_char(eligible.due_on, 'Mon FMDD, YYYY'),
                       eligible.business_key, eligible."LeaseAgreementId", eligible."CreatedByUserId"
                FROM eligible
                WHERE eligible.charge_amount > 0
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Id", "PortfolioId", "TenantAccountId", "LeaseAgreementId",
                          "EntryType", "Amount", "EffectiveOn", "DueOn", "BusinessKey",
                          "CreatedByUserId"
            )
            SELECT inserted."Id" AS "LedgerEntryId",
                   inserted."PortfolioId", inserted."TenantAccountId",
                   inserted."LeaseAgreementId", inserted."EntryType",
                   inserted."Amount", inserted."EffectiveOn", inserted."DueOn",
                   inserted."BusinessKey", inserted."CreatedByUserId"
            FROM inserted
            ORDER BY inserted."Id"
            """).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AtomicScheduledTenantCharge>> PostScheduledLateFeesAsync(
        int batchSize,
        string stateCapsJson,
        DateTime postedAtUtc,
        CancellationToken ct = default)
    {
        if (batchSize is <= 0 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateCapsJson);
        if (postedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Scheduled charge posting time must be UTC.", nameof(postedAtUtc));

        using var lease = _scope.BeginInternalRawDml(
            "TenantLedgerEntries", AtomicRawDmlOperation.Insert);

        return await _db.Database.SqlQuery<AtomicScheduledTenantCharge>($"""
            WITH caps AS MATERIALIZED (
                SELECT upper(cap."State") AS state,
                       cap."MaxFlat" AS max_flat,
                       cap."MaxPercentOfRent" AS max_percent
                FROM jsonb_to_recordset(CAST({stateCapsJson} AS jsonb)) AS cap(
                    "State" text,
                    "MaxFlat" numeric,
                    "MaxPercentOfRent" numeric)
            ), candidates AS MATERIALIZED (
                SELECT rent."PortfolioId",
                       rent."TenantAccountId",
                       rent."LeaseAgreementId",
                       rent."Currency",
                       rent."BusinessKey" AS rent_business_key,
                       agreement."LateFeeAmount",
                       agreement."GracePeriodDays",
                       account."CreatedByUserId",
                       effective_date.business_date,
                       LEAST(
                           agreement."LateFeeAmount",
                           COALESCE(cap.max_flat, agreement."LateFeeAmount"),
                           COALESCE(
                               rent."Amount" * cap.max_percent / 100::numeric,
                               agreement."LateFeeAmount")) AS fee_amount,
                       'late-fee:' || rent."BusinessKey" AS business_key
                FROM "vw_tenant_charge_balances" AS balance
                JOIN "TenantLedgerEntries" AS rent
                  ON rent."Id" = balance."TenantLedgerEntryId"
                 AND rent."PortfolioId" = balance."PortfolioId"
                 AND rent."TenantAccountId" = balance."TenantAccountId"
                JOIN "TenantAccounts" AS account
                  ON account."Id" = rent."TenantAccountId"
                 AND account."PortfolioId" = rent."PortfolioId"
                JOIN "LeaseAgreements" AS agreement
                  ON agreement."Id" = rent."LeaseAgreementId"
                 AND agreement."PortfolioId" = rent."PortfolioId"
                JOIN "LeaseManagements" AS management
                  ON management."Id" = agreement."LeaseManagementId"
                 AND management."PortfolioId" = agreement."PortfolioId"
                JOIN "Properties" AS property
                  ON property."Id" = management."PropertyId"
                 AND property."PortfolioId" = management."PortfolioId"
                JOIN "AutomationSettings" AS settings
                  ON settings."PortfolioId" = rent."PortfolioId"
                 AND settings."EnableLateFees"
                LEFT JOIN caps AS cap ON cap.state = upper(COALESCE(property."State", ''))
                CROSS JOIN LATERAL (
                    SELECT rc_business_date(rent."PortfolioId") AS business_date
                ) AS effective_date
                WHERE rent."EntryType" = 'RentCharge'
                  AND balance."OpenAmount" > 0
                  AND rent."DueOn" < effective_date.business_date
                      - GREATEST(
                          agreement."GracePeriodDays"::integer,
                          settings."LateFeeGraceDays",
                          0)
                  AND agreement."LateFeeAmount" > 0
                  AND management."CanceledAtUtc" IS NULL
                  AND account."ClosedAtUtc" IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerEntries" AS existing
                      WHERE existing."TenantAccountId" = rent."TenantAccountId"
                        AND existing."BusinessKey" = 'late-fee:' || rent."BusinessKey")
                ORDER BY rent."DueOn", rent."TenantAccountId", rent."Id"
                LIMIT {batchSize}
            ), inserted AS (
                INSERT INTO "TenantLedgerEntries" (
                    "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount",
                    "Currency", "EffectiveOn", "DueOn", "PostedAtUtc", "Description",
                    "BusinessKey", "LeaseAgreementId", "CreatedByUserId")
                SELECT candidate."PortfolioId", candidate."TenantAccountId", 'LateFeeCharge',
                       'Debit', round(candidate.fee_amount, 2), candidate."Currency",
                       candidate.business_date, candidate.business_date, {postedAtUtc},
                       'Late fee for ' || replace(candidate.rent_business_key, 'rent:', ''),
                       candidate.business_key, candidate."LeaseAgreementId",
                       candidate."CreatedByUserId"
                FROM candidates AS candidate
                WHERE round(candidate.fee_amount, 2) > 0
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Id", "PortfolioId", "TenantAccountId", "LeaseAgreementId",
                          "EntryType", "Amount", "EffectiveOn", "DueOn", "BusinessKey",
                          "CreatedByUserId"
            )
            SELECT inserted."Id" AS "LedgerEntryId",
                   inserted."PortfolioId", inserted."TenantAccountId",
                   inserted."LeaseAgreementId", inserted."EntryType",
                   inserted."Amount", inserted."EffectiveOn", inserted."DueOn",
                   inserted."BusinessKey", inserted."CreatedByUserId"
            FROM inserted
            ORDER BY inserted."Id"
            """).ToListAsync(ct);
    }

    public async Task<AtomicLedgerAllocationSummary> AllocateOldestChargesAsync(
        int portfolioId,
        int tenantAccountId,
        long creditEntryId,
        decimal availableAmount,
        string businessKeyPrefix,
        int createdByUserId,
        DateTime allocatedAtUtc,
        string? entryType = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tenantAccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(creditEntryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(availableAmount);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessKeyPrefix);

        using var lease = _scope.BeginInternalRawDml(
            "TenantLedgerAllocations", AtomicRawDmlOperation.Insert);

        var rows = await _db.Database.SqlQuery<AtomicLedgerAllocationSummary>($"""
            WITH eligible AS (
                SELECT balance."TenantLedgerEntryId",
                       balance."OpenAmount",
                       COALESCE(
                           SUM(balance."OpenAmount") OVER (
                               ORDER BY balance."DueOn" NULLS LAST, balance."TenantLedgerEntryId"
                               ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),
                           0) AS consumed_before
                FROM "vw_tenant_charge_balances" AS balance
                WHERE balance."PortfolioId" = {portfolioId}
                  AND balance."TenantAccountId" = {tenantAccountId}
                  AND balance."OpenAmount" > 0
                  AND (CAST({entryType} AS text) IS NULL
                       OR balance."EntryType" = CAST({entryType} AS text))
            ), inserted AS (
                INSERT INTO "TenantLedgerAllocations" (
                    "PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId",
                    "Amount", "AllocatedAtUtc", "BusinessKey", "CreatedByUserId")
                SELECT {portfolioId}, {tenantAccountId}, eligible."TenantLedgerEntryId", {creditEntryId},
                       LEAST(eligible."OpenAmount", {availableAmount} - eligible.consumed_before),
                       {allocatedAtUtc},
                       {businessKeyPrefix} || ':' || eligible."TenantLedgerEntryId"::text,
                       {createdByUserId}
                FROM eligible
                WHERE eligible.consumed_before < {availableAmount}
                ORDER BY eligible.consumed_before, eligible."TenantLedgerEntryId"
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Amount"
            )
            SELECT COUNT(*)::integer AS "AllocationCount",
                   COALESCE(SUM(inserted."Amount"), 0)::numeric AS "AllocatedAmount"
            FROM inserted
            """).ToListAsync(ct);
        return rows.Single();
    }

    public async Task<AtomicLedgerAllocationSummary> AllocateImportedReceiptsAsync(
        long[] creditEntryIds,
        DateTime allocatedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(creditEntryIds);
        if (creditEntryIds.Length == 0) return new AtomicLedgerAllocationSummary();
        if (creditEntryIds.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(creditEntryIds), "Imported receipt batches are limited to 256 entries.");
        if (creditEntryIds.Any(id => id <= 0) || creditEntryIds.Distinct().Count() != creditEntryIds.Length)
            throw new ArgumentException("Imported receipt ids must be unique positive keys.", nameof(creditEntryIds));

        using var lease = _scope.BeginInternalRawDml(
            "TenantLedgerAllocations", AtomicRawDmlOperation.Insert);

        var rows = await _db.Database.SqlQuery<AtomicLedgerAllocationSummary>($"""
            WITH credits AS MATERIALIZED (
                SELECT credit."Id", credit."PortfolioId", credit."TenantAccountId",
                       credit."Amount", credit."BusinessKey", credit."CreatedByUserId",
                       CASE WHEN EXISTS (
                           SELECT 1
                           FROM "SecurityDepositEntries" AS deposit
                           WHERE deposit."PortfolioId" = credit."PortfolioId"
                             AND deposit."TenantLedgerEntryId" = credit."Id"
                             AND deposit."EntryType" = 'Receipt')
                         THEN 'Deposit' ELSE 'Receivable' END AS allocation_kind
                FROM "TenantLedgerEntries" AS credit
                WHERE credit."Id" = ANY({creditEntryIds})
                  AND credit."EntryType" = 'PaymentReceipt'
                  AND credit."Direction" = 'Credit'
            ), credit_intervals AS MATERIALIZED (
                SELECT credit.*,
                       COALESCE(SUM(credit."Amount") OVER (
                           PARTITION BY credit."TenantAccountId", credit.allocation_kind
                           ORDER BY credit."Id"
                           ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS interval_start,
                       SUM(credit."Amount") OVER (
                           PARTITION BY credit."TenantAccountId", credit.allocation_kind
                           ORDER BY credit."Id") AS interval_end
                FROM credits AS credit
            ), debit_intervals AS MATERIALIZED (
                SELECT balance."PortfolioId", balance."TenantAccountId",
                       balance."TenantLedgerEntryId", balance."OpenAmount",
                       CASE WHEN balance."EntryType" = 'DepositCharge'
                         THEN 'Deposit' ELSE 'Receivable' END AS allocation_kind,
                       COALESCE(SUM(balance."OpenAmount") OVER (
                           PARTITION BY balance."TenantAccountId",
                               CASE WHEN balance."EntryType" = 'DepositCharge'
                                 THEN 'Deposit' ELSE 'Receivable' END
                           ORDER BY balance."DueOn" NULLS LAST, balance."TenantLedgerEntryId"
                           ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS interval_start,
                       SUM(balance."OpenAmount") OVER (
                           PARTITION BY balance."TenantAccountId",
                               CASE WHEN balance."EntryType" = 'DepositCharge'
                                 THEN 'Deposit' ELSE 'Receivable' END
                           ORDER BY balance."DueOn" NULLS LAST, balance."TenantLedgerEntryId") AS interval_end
                FROM "vw_tenant_charge_balances" AS balance
                WHERE balance."OpenAmount" > 0
                  AND balance."TenantAccountId" IN (
                      SELECT credit."TenantAccountId" FROM credits AS credit)
            ), inserted AS (
                INSERT INTO "TenantLedgerAllocations" (
                    "PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId",
                    "Amount", "AllocatedAtUtc", "BusinessKey", "CreatedByUserId")
                SELECT credit."PortfolioId", credit."TenantAccountId",
                       debit."TenantLedgerEntryId", credit."Id",
                       LEAST(credit.interval_end, debit.interval_end)
                           - GREATEST(credit.interval_start, debit.interval_start),
                       {allocatedAtUtc},
                       credit."BusinessKey" || ':allocation:' || debit."TenantLedgerEntryId"::text,
                       credit."CreatedByUserId"
                FROM credit_intervals AS credit
                JOIN debit_intervals AS debit
                  ON debit."PortfolioId" = credit."PortfolioId"
                 AND debit."TenantAccountId" = credit."TenantAccountId"
                 AND debit.allocation_kind = credit.allocation_kind
                 AND debit.interval_end > credit.interval_start
                 AND credit.interval_end > debit.interval_start
                ORDER BY credit."Id", debit.interval_start, debit."TenantLedgerEntryId"
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Amount"
            )
            SELECT COUNT(*)::integer AS "AllocationCount",
                   COALESCE(SUM(inserted."Amount"), 0)::numeric AS "AllocatedAmount"
            FROM inserted
            """).ToListAsync(ct);
        return rows.Single();
    }

    public async Task<AtomicLedgerAllocationSummary> ReverseEntryAllocationsAsync(
        int portfolioId,
        int tenantAccountId,
        long ledgerEntryId,
        string businessKeyPrefix,
        int createdByUserId,
        DateTime allocatedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tenantAccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledgerEntryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessKeyPrefix);

        using var lease = _scope.BeginInternalRawDml(
            "TenantLedgerAllocations", AtomicRawDmlOperation.Insert);

        var rows = await _db.Database.SqlQuery<AtomicLedgerAllocationSummary>($"""
            WITH inserted AS (
                INSERT INTO "TenantLedgerAllocations" (
                    "PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId",
                    "Amount", "AllocatedAtUtc", "BusinessKey", "ReversesAllocationId",
                    "CreatedByUserId")
                SELECT source."PortfolioId", source."TenantAccountId", source."DebitEntryId",
                       source."CreditEntryId", -source."Amount", {allocatedAtUtc},
                       {businessKeyPrefix} || ':' || source."Id"::text, source."Id",
                       {createdByUserId}
                FROM "TenantLedgerAllocations" AS source
                WHERE source."PortfolioId" = {portfolioId}
                  AND source."TenantAccountId" = {tenantAccountId}
                  AND (source."DebitEntryId" = {ledgerEntryId}
                       OR source."CreditEntryId" = {ledgerEntryId})
                  AND source."ReversesAllocationId" IS NULL
                  AND source."Amount" > 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerAllocations" AS existing_reversal
                      WHERE existing_reversal."PortfolioId" = source."PortfolioId"
                        AND existing_reversal."TenantAccountId" = source."TenantAccountId"
                        AND existing_reversal."ReversesAllocationId" = source."Id")
                ORDER BY source."Id"
                RETURNING "Amount"
            )
            SELECT COUNT(*)::integer AS "AllocationCount",
                   COALESCE(-SUM(inserted."Amount"), 0)::numeric AS "AllocatedAmount"
            FROM inserted
            """).ToListAsync(ct);
        return rows.Single();
    }
}
