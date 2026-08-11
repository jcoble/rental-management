using RentalCommand.Core.Atomic;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Payments;

internal static class TenantMoneyPersistence
{

    public static async Task<TenantMoneyOpeningSecurityDepositRecovery> RecoverOpeningSecurityDepositsAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        DateOnly effectiveOn,
        int expectedAccountCount,
        decimal expectedTotal,
        string financialReference,
        int actorUserId,
        DateTime postedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        if (effectiveOn == default) throw new ArgumentException("Opening effective date is required.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedAccountCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedTotal);
        ArgumentException.ThrowIfNullOrWhiteSpace(financialReference);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorUserId);
        if (postedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Opening recovery posting time must be UTC.", nameof(postedAtUtc));

        var normalizedReference = financialReference.Trim();
        if (normalizedReference.Length > 80)
            throw new ArgumentException("Financial reference cannot exceed 80 characters.");

        var mutationTargets = new AtomicSqlMutationTarget[] {
            new AtomicSqlMutationTarget("SecurityDepositAccounts", AtomicSqlMutationOperation.Insert),
            new AtomicSqlMutationTarget("SecurityDepositEntries", AtomicSqlMutationOperation.Insert) };

        // This intentionally does not create tenant charges, payment attempts, tenant receipts, or
        // allocations. FIN opening balances describe money already held before the simulation;
        // pretending they were current-period payments would corrupt tenant history and cash flow.
        var rows = await db.ExecuteAtomicSqlMutationAsync<TenantMoneyOpeningSecurityDepositRecovery>(context, $"""
            WITH candidates AS MATERIALIZED (
                SELECT account."PortfolioId",
                       account."Id" AS tenant_account_id,
                       agreement."Id" AS agreement_id,
                       account."Currency",
                       agreement."SecurityDepositObligation" AS amount,
                       count(*) OVER (PARTITION BY account."Id") AS governing_count
                FROM "TenantAccounts" AS account
                JOIN "LeaseManagements" AS management
                  ON management."Id" = account."LeaseManagementId"
                 AND management."PortfolioId" = account."PortfolioId"
                JOIN "LeaseAgreements" AS agreement
                  ON agreement."LeaseManagementId" = management."Id"
                 AND agreement."PortfolioId" = management."PortfolioId"
                 AND agreement."TermStartOn" <= {effectiveOn}
                 AND agreement."TermEndOn" >= {effectiveOn}
                 AND agreement."GoverningFromOn" <= {effectiveOn}
                 AND (agreement."SupersededEffectiveOn" IS NULL
                      OR agreement."SupersededEffectiveOn" > {effectiveOn})
                 AND agreement."FullyExecutedAtUtc" IS NOT NULL
                 AND agreement."VoidedAtUtc" IS NULL
                 AND agreement."DraftCanceledAtUtc" IS NULL
                WHERE account."PortfolioId" = {portfolioId}
                  AND account."ClosedAtUtc" IS NULL
                  AND management."CanceledAtUtc" IS NULL
                  AND agreement."SecurityDepositObligation" > 0
            ),
            candidate_totals AS MATERIALIZED (
                SELECT count(*)::integer AS account_count,
                       count(DISTINCT candidate.tenant_account_id)::integer AS distinct_account_count,
                       COALESCE(sum(candidate.amount), 0)::numeric AS reconciled_total,
                       COALESCE(max(candidate.governing_count), 0)::integer AS max_governing_count
                FROM candidates AS candidate
            ),
            existing_account_conflicts AS MATERIALIZED (
                SELECT count(*)::integer AS conflict_count
                FROM candidates AS candidate
                JOIN "SecurityDepositAccounts" AS deposit_account
                  ON deposit_account."TenantAccountId" = candidate.tenant_account_id
                 AND deposit_account."PortfolioId" = candidate."PortfolioId"
                WHERE deposit_account."OriginatingAgreementId" <> candidate.agreement_id
                   OR deposit_account."Currency" <> candidate."Currency"
            ),
            existing_entry_conflicts AS MATERIALIZED (
                SELECT count(*)::integer AS conflict_count
                FROM candidates AS candidate
                JOIN "SecurityDepositAccounts" AS deposit_account
                  ON deposit_account."TenantAccountId" = candidate.tenant_account_id
                 AND deposit_account."PortfolioId" = candidate."PortfolioId"
                JOIN "SecurityDepositEntries" AS deposit_entry
                  ON deposit_entry."SecurityDepositAccountId" = deposit_account."Id"
                 AND deposit_entry."PortfolioId" = deposit_account."PortfolioId"
                 AND deposit_entry."BusinessKey" =
                     'opening-deposit:' || {normalizedReference} || ':account:'
                     || candidate.tenant_account_id::text
                WHERE deposit_entry."EntryType" <> 'Receipt'
                   OR deposit_entry."Direction" <> 'Increase'
                   OR deposit_entry."Amount" <> candidate.amount
                   OR deposit_entry."Currency" <> candidate."Currency"
                   OR deposit_entry."EffectiveOn" <> {effectiveOn}
                   OR deposit_entry."LeaseAgreementId" IS DISTINCT FROM candidate.agreement_id
                   OR deposit_entry."TenantLedgerEntryId" IS NOT NULL
            ),
            validation AS MATERIALIZED (
                SELECT totals.account_count,
                       totals.reconciled_total,
                       totals.account_count = {expectedAccountCount}
                         AND totals.distinct_account_count = {expectedAccountCount}
                         AND totals.reconciled_total = {expectedTotal}
                         AND totals.max_governing_count = 1
                         AND account_conflicts.conflict_count = 0
                         AND entry_conflicts.conflict_count = 0 AS is_valid,
                       CASE
                         WHEN totals.account_count <> {expectedAccountCount}
                           THEN 'Agreement-backed opening account count does not match the control.'
                         WHEN totals.distinct_account_count <> totals.account_count
                              OR totals.max_governing_count <> 1
                           THEN 'An opening tenant account has more than one governing Agreement.'
                         WHEN totals.reconciled_total <> {expectedTotal}
                           THEN 'Agreement-backed opening deposit total does not match the control.'
                         WHEN account_conflicts.conflict_count <> 0
                           THEN 'An existing security-deposit account conflicts with its governing Agreement.'
                         WHEN entry_conflicts.conflict_count <> 0
                           THEN 'An existing opening security-deposit entry conflicts with the control.'
                         ELSE ''
                       END AS validation_error
                FROM candidate_totals AS totals
                CROSS JOIN existing_account_conflicts AS account_conflicts
                CROSS JOIN existing_entry_conflicts AS entry_conflicts
            ),
            inserted_accounts AS (
                INSERT INTO "SecurityDepositAccounts" (
                    "PortfolioId", "TenantAccountId", "OriginatingAgreementId", "Currency",
                    "CreatedAtUtc", "CreatedByUserId")
                SELECT candidate."PortfolioId", candidate.tenant_account_id,
                       candidate.agreement_id, candidate."Currency", {postedAtUtc}, {actorUserId}
                FROM candidates AS candidate
                CROSS JOIN validation
                WHERE validation.is_valid
                ON CONFLICT ("TenantAccountId") DO NOTHING
                RETURNING "Id", "PortfolioId", "TenantAccountId", "OriginatingAgreementId",
                          "Currency"
            ),
            resolved_accounts AS MATERIALIZED (
                SELECT inserted."Id", inserted."PortfolioId", inserted."TenantAccountId",
                       inserted."OriginatingAgreementId", inserted."Currency"
                FROM inserted_accounts AS inserted
                UNION ALL
                SELECT existing."Id", existing."PortfolioId", existing."TenantAccountId",
                       existing."OriginatingAgreementId", existing."Currency"
                FROM "SecurityDepositAccounts" AS existing
                JOIN candidates AS candidate
                  ON candidate.tenant_account_id = existing."TenantAccountId"
                 AND candidate."PortfolioId" = existing."PortfolioId"
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM inserted_accounts AS inserted
                    WHERE inserted."TenantAccountId" = existing."TenantAccountId"
                      AND inserted."PortfolioId" = existing."PortfolioId")
            ),
            inserted_entries AS (
                INSERT INTO "SecurityDepositEntries" (
                    "PortfolioId", "SecurityDepositAccountId", "EntryType", "Direction",
                    "Amount", "Currency", "EffectiveOn", "PostedAtUtc", "BusinessKey",
                    "Description", "LeaseAgreementId", "TenantLedgerEntryId",
                    "CreatedByUserId")
                SELECT candidate."PortfolioId", deposit_account."Id", 'Receipt', 'Increase',
                       candidate.amount, candidate."Currency", {effectiveOn}, {postedAtUtc},
                       'opening-deposit:' || {normalizedReference} || ':account:'
                           || candidate.tenant_account_id::text,
                       'Opening security deposit balance ' || {normalizedReference},
                       candidate.agreement_id, NULL, {actorUserId}
                FROM candidates AS candidate
                JOIN resolved_accounts AS deposit_account
                  ON deposit_account."TenantAccountId" = candidate.tenant_account_id
                 AND deposit_account."PortfolioId" = candidate."PortfolioId"
                CROSS JOIN validation
                WHERE validation.is_valid
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "SecurityDepositEntries" AS existing
                      WHERE existing."SecurityDepositAccountId" = deposit_account."Id"
                        AND existing."PortfolioId" = deposit_account."PortfolioId"
                        AND existing."BusinessKey" =
                            'opening-deposit:' || {normalizedReference} || ':account:'
                            || candidate.tenant_account_id::text)
                ON CONFLICT ("SecurityDepositAccountId", "BusinessKey") DO NOTHING
                RETURNING "Id"
            )
            SELECT validation.is_valid AS "IsValid",
                   validation.account_count AS "AccountCount",
                   (SELECT count(*)::integer FROM inserted_accounts) AS "CreatedAccountCount",
                   (SELECT count(*)::integer FROM inserted_entries) AS "CreatedEntryCount",
                   validation.reconciled_total AS "ReconciledTotal",
                   validation.validation_error AS "ValidationError"
            FROM validation
            """, mutationTargets, ct);

        return rows.Single();
    }

    public static async Task<IReadOnlyList<TenantMoneyScheduledCharge>> PostScheduledRentChargesAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int batchSize,
        DateTime postedAtUtc,
        CancellationToken ct = default)
    {
        if (batchSize is <= 0 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (postedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Scheduled charge posting time must be UTC.", nameof(postedAtUtc));

        var mutationTargets = new AtomicSqlMutationTarget[] {
            new AtomicSqlMutationTarget("TenantLedgerEntries", AtomicSqlMutationOperation.Insert) };

        // The lateral month series is deliberately inside PostgreSQL. Its lower bound is the
        // account's durable tracking start (or the lease start when backfill is selected), while
        // the upper bound is always the current business month so future rent is never posted.
        // Scheduled billing only posts charges; receipt allocation belongs exclusively to the
        // explicit-target receipt command.
        return await db.ExecuteAtomicSqlMutationAsync<TenantMoneyScheduledCharge>(context, $"""
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
                           COALESCE(
                               account."RentTrackingStartOn",
                               agreement."TermStartOn"),
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
                CROSS JOIN LATERAL (
                    SELECT rc_business_date(agreement."PortfolioId") AS business_date
                ) AS effective_date
                CROSS JOIN LATERAL generate_series(
                    date_trunc('month', GREATEST(
                        agreement."TermStartOn",
                        agreement."GoverningFromOn",
                        COALESCE(
                            account."RentTrackingStartOn",
                            agreement."TermStartOn"))::timestamp),
                    date_trunc('month', effective_date.business_date::timestamp),
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
            ), due_charges AS MATERIALIZED (
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
                WHERE candidate.due_on <= candidate.business_date
            ), eligible AS MATERIALIZED (
                SELECT due_charge.*
                FROM due_charges AS due_charge
                WHERE due_charge.charge_amount > 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerEntries" AS existing
                      WHERE existing."TenantAccountId" = due_charge."TenantAccountId"
                        AND existing."BusinessKey" = due_charge.business_key)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerEntries" AS existing_due
                      WHERE existing_due."PortfolioId" = due_charge."PortfolioId"
                        AND existing_due."TenantAccountId" = due_charge."TenantAccountId"
                        AND existing_due."EntryType" = 'RentCharge'
                        AND existing_due."Direction" = 'Debit'
                        AND existing_due."DueOn" = due_charge.due_on)
                  AND (
                      due_charge."ChangeType" NOT IN ('Correction', 'Restatement')
                      OR NOT EXISTS (
                          SELECT 1
                          FROM "TenantLedgerEntries" AS existing_period
                          WHERE existing_period."PortfolioId" = due_charge."PortfolioId"
                            AND existing_period."TenantAccountId" = due_charge."TenantAccountId"
                            AND existing_period."EntryType" = 'RentCharge'
                            AND existing_period."DueOn" >= due_charge.month_start
                            AND existing_period."DueOn" < due_charge.month_start + interval '1 month'))
                ORDER BY due_charge.due_on, due_charge."TenantAccountId", due_charge."LeaseAgreementId"
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
            ORDER BY "LedgerEntryId"
            """, mutationTargets, ct);
    }

    public static async Task<TenantMoneyHistoricalRentChargeRecovery> RecoverHistoricalRentChargeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int tenantAccountId,
        int leaseAgreementId,
        long existingRentChargeEntryId,
        long existingReceiptEntryId,
        long existingAllocationId,
        DateOnly expectedCurrentRentTrackingStartOn,
        DateOnly correctRentTrackingStartOn,
        DateOnly rentPeriodStartOn,
        DateOnly expectedExistingChargeDueOn,
        decimal expectedExistingChargeAmount,
        decimal expectedReceiptAmount,
        decimal correctRentAmount,
        string financialReference,
        int actorUserId,
        DateTime postedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tenantAccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseAgreementId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(existingRentChargeEntryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(existingReceiptEntryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(existingAllocationId);
        if (expectedCurrentRentTrackingStartOn == default
            || correctRentTrackingStartOn == default
            || rentPeriodStartOn == default
            || expectedExistingChargeDueOn == default)
            throw new ArgumentException("Rent recovery dates are required.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedExistingChargeAmount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedReceiptAmount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(correctRentAmount);
        ArgumentException.ThrowIfNullOrWhiteSpace(financialReference);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorUserId);
        if (postedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Rent recovery posting time must be UTC.", nameof(postedAtUtc));

        var normalizedReference = financialReference.Trim();
        if (normalizedReference.Length > 80)
            throw new ArgumentException("Financial reference cannot exceed 80 characters.");
        var replacementBusinessKey = $"historical-rent:{normalizedReference}:replacement";
        var reversalBusinessKey = $"historical-rent:{normalizedReference}:reversal";
        var allocationBusinessKey = $"historical-rent:{normalizedReference}:allocation";
        var allocationReversalBusinessKey = $"historical-rent:{normalizedReference}:allocation-reversal";

        var mutationTargets = new AtomicSqlMutationTarget[] {
            new AtomicSqlMutationTarget("TenantAccounts", AtomicSqlMutationOperation.Update),
            new AtomicSqlMutationTarget("TenantLedgerEntries", AtomicSqlMutationOperation.Insert),
            new AtomicSqlMutationTarget("TenantLedgerAllocations", AtomicSqlMutationOperation.Insert) };

        var rows = await db.ExecuteAtomicSqlMutationAsync<TenantMoneyHistoricalRentChargeRecovery>(context, $"""
            WITH target AS MATERIALIZED (
                SELECT account."PortfolioId",
                       account."Id" AS tenant_account_id,
                       account."Currency",
                       account."RentTrackingStartOn",
                       agreement."Id" AS agreement_id,
                       agreement."PublicId" AS agreement_public_id,
                       agreement."BaseRentAmount",
                       agreement."RentDueDay",
                       agreement."TermStartOn",
                       agreement."TermEndOn",
                       agreement."GoverningFromOn",
                       agreement."SupersededEffectiveOn",
                       charge."Id" AS charge_id,
                       charge."Amount" AS charge_amount,
                       charge."Currency" AS charge_currency,
                       charge."EffectiveOn" AS charge_effective_on,
                       charge."DueOn" AS charge_due_on,
                       charge."BusinessKey" AS charge_business_key,
                       receipt."Id" AS receipt_id,
                       receipt."Amount" AS receipt_amount,
                       receipt."Currency" AS receipt_currency,
                       allocation."Id" AS allocation_id,
                       allocation."Amount" AS allocation_amount,
                       COALESCE(live_receipt_allocations.allocated_amount, 0::numeric)
                           AS live_receipt_allocated_amount,
                       EXISTS (
                           SELECT 1
                           FROM "TenantLedgerEntries" AS reversal
                           WHERE reversal."PortfolioId" = account."PortfolioId"
                             AND reversal."TenantAccountId" = account."Id"
                             AND reversal."EntryType" = 'Reversal'
                             AND reversal."ReversesEntryId" = charge."Id") AS charge_has_reversal,
                       EXISTS (
                           SELECT 1
                           FROM "TenantLedgerEntries" AS existing
                           WHERE existing."PortfolioId" = account."PortfolioId"
                             AND existing."TenantAccountId" = account."Id"
                             AND existing."BusinessKey" IN ({replacementBusinessKey}, {reversalBusinessKey})
                       ) AS entry_business_key_conflict,
                       EXISTS (
                           SELECT 1
                           FROM "TenantLedgerAllocations" AS existing
                           WHERE existing."PortfolioId" = account."PortfolioId"
                             AND existing."TenantAccountId" = account."Id"
                             AND existing."BusinessKey" IN ({allocationBusinessKey}, {allocationReversalBusinessKey})
                       ) AS allocation_business_key_conflict,
                       EXISTS (
                           SELECT 1
                           FROM "TenantLedgerAllocations" AS reversal
                           WHERE reversal."PortfolioId" = allocation."PortfolioId"
                             AND reversal."TenantAccountId" = allocation."TenantAccountId"
                             AND reversal."ReversesAllocationId" = allocation."Id") AS allocation_has_reversal
                FROM "TenantAccounts" AS account
                JOIN "LeaseManagements" AS management
                  ON management."Id" = account."LeaseManagementId"
                 AND management."PortfolioId" = account."PortfolioId"
                JOIN "LeaseAgreements" AS agreement
                  ON agreement."Id" = {leaseAgreementId}
                 AND agreement."PortfolioId" = account."PortfolioId"
                 AND agreement."LeaseManagementId" = management."Id"
                JOIN "TenantLedgerEntries" AS charge
                  ON charge."Id" = {existingRentChargeEntryId}
                 AND charge."PortfolioId" = account."PortfolioId"
                 AND charge."TenantAccountId" = account."Id"
                 AND charge."LeaseAgreementId" = agreement."Id"
                JOIN "TenantLedgerEntries" AS receipt
                  ON receipt."Id" = {existingReceiptEntryId}
                 AND receipt."PortfolioId" = account."PortfolioId"
                 AND receipt."TenantAccountId" = account."Id"
                JOIN "TenantLedgerAllocations" AS allocation
                  ON allocation."Id" = {existingAllocationId}
                 AND allocation."PortfolioId" = account."PortfolioId"
                 AND allocation."TenantAccountId" = account."Id"
                 AND allocation."DebitEntryId" = charge."Id"
                 AND allocation."CreditEntryId" = receipt."Id"
                LEFT JOIN LATERAL (
                    SELECT SUM(live."Amount") AS allocated_amount
                    FROM "TenantLedgerAllocations" AS live
                    WHERE live."PortfolioId" = account."PortfolioId"
                      AND live."TenantAccountId" = account."Id"
                      AND live."CreditEntryId" = receipt."Id"
                      AND live."ReversesAllocationId" IS NULL
                      AND live."Amount" > 0
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "TenantLedgerAllocations" AS reversed
                          WHERE reversed."PortfolioId" = live."PortfolioId"
                            AND reversed."TenantAccountId" = live."TenantAccountId"
                            AND reversed."ReversesAllocationId" = live."Id")
                ) AS live_receipt_allocations ON true
                WHERE account."PortfolioId" = {portfolioId}
                  AND account."Id" = {tenantAccountId}
                  AND account."ClosedAtUtc" IS NULL
                  AND management."CanceledAtUtc" IS NULL
                  AND agreement."FullyExecutedAtUtc" IS NOT NULL
                  AND agreement."VoidedAtUtc" IS NULL
                  AND agreement."DraftCanceledAtUtc" IS NULL
                  AND charge."EntryType" = 'RentCharge'
                  AND charge."Direction" = 'Debit'
                  AND receipt."EntryType" = 'PaymentReceipt'
                  AND receipt."Direction" = 'Credit'
                  AND allocation."ReversesAllocationId" IS NULL
                  AND allocation."Amount" > 0
                FOR UPDATE OF account, charge, receipt, allocation
            ), validation AS MATERIALIZED (
                SELECT target.*,
                       COALESCE(target."RentTrackingStartOn" = {expectedCurrentRentTrackingStartOn}, false)
                         AND {correctRentTrackingStartOn} <= target.charge_due_on
                         AND target."TermStartOn" <= {rentPeriodStartOn}
                         AND COALESCE(target."TermEndOn", 'infinity'::date) >= {rentPeriodStartOn}
                         AND target."GoverningFromOn" <= {rentPeriodStartOn}
                         AND (target."SupersededEffectiveOn" IS NULL
                              OR target."SupersededEffectiveOn" > {rentPeriodStartOn})
                         AND target."BaseRentAmount" = {correctRentAmount}
                         AND target."RentDueDay" = EXTRACT(day FROM {rentPeriodStartOn}::date)::smallint
                         AND target.charge_amount = {expectedExistingChargeAmount}
                         AND target.charge_due_on = {expectedExistingChargeDueOn}
                         AND target.charge_effective_on = {expectedExistingChargeDueOn}
                         AND target.charge_currency = target."Currency"
                         AND target.charge_business_key =
                             'rent:' || target.agreement_public_id::text || ':'
                             || to_char({rentPeriodStartOn}::date, 'YYYY-MM')
                         AND target.receipt_amount = {expectedReceiptAmount}
                         AND target.receipt_currency = target."Currency"
                         AND target.allocation_amount = {expectedExistingChargeAmount}
                         AND target.live_receipt_allocated_amount = {expectedExistingChargeAmount}
                         AND {expectedReceiptAmount} = {correctRentAmount}
                         AND NOT target.charge_has_reversal
                         AND NOT target.allocation_has_reversal
                         AND NOT target.entry_business_key_conflict
                         AND NOT target.allocation_business_key_conflict AS is_valid,
                       CASE
                         WHEN target.tenant_account_id IS NULL
                           THEN 'No matching active tenant account, agreement, charge, receipt, and allocation tuple was found.'
                         WHEN target."RentTrackingStartOn" IS DISTINCT FROM {expectedCurrentRentTrackingStartOn}
                           THEN 'Tenant account rent tracking start no longer matches the expected value.'
                         WHEN {correctRentTrackingStartOn} > target.charge_due_on
                           THEN 'Correct rent tracking start must be on or before the corrected rent due date.'
                         WHEN target."TermStartOn" > {rentPeriodStartOn}
                              OR COALESCE(target."TermEndOn", 'infinity'::date) < {rentPeriodStartOn}
                              OR target."GoverningFromOn" > {rentPeriodStartOn}
                              OR (target."SupersededEffectiveOn" IS NOT NULL
                                  AND target."SupersededEffectiveOn" <= {rentPeriodStartOn})
                           THEN 'Agreement is not governing for the requested rent period.'
                         WHEN target."BaseRentAmount" <> {correctRentAmount}
                           THEN 'Agreement rent does not match the requested corrected rent.'
                         WHEN target."RentDueDay" <> EXTRACT(day FROM {rentPeriodStartOn}::date)::smallint
                           THEN 'Agreement rent due day does not match the requested period.'
                         WHEN target.charge_amount <> {expectedExistingChargeAmount}
                              OR target.charge_due_on <> {expectedExistingChargeDueOn}
                              OR target.charge_effective_on <> {expectedExistingChargeDueOn}
                           THEN 'Existing rent charge does not match the expected prorated charge.'
                         WHEN target.charge_business_key <>
                             'rent:' || target.agreement_public_id::text || ':'
                             || to_char({rentPeriodStartOn}::date, 'YYYY-MM')
                           THEN 'Existing rent charge is not the scheduled charge for the requested period.'
                         WHEN target.receipt_amount <> {expectedReceiptAmount}
                              OR target.receipt_currency <> target."Currency"
                           THEN 'Existing receipt does not match the expected payment amount.'
                         WHEN target.allocation_amount <> {expectedExistingChargeAmount}
                              OR target.live_receipt_allocated_amount <> {expectedExistingChargeAmount}
                           THEN 'Existing receipt allocation is not the exact live prorated allocation.'
                         WHEN {expectedReceiptAmount} <> {correctRentAmount}
                           THEN 'Corrected rent must match the receipt amount for this repair.'
                         WHEN target.charge_has_reversal OR target.allocation_has_reversal
                           THEN 'Existing charge or allocation has already been reversed.'
                         WHEN target.entry_business_key_conflict
                              OR target.allocation_business_key_conflict
                           THEN 'Historical rent recovery rows already exist for this reference.'
                         ELSE ''
                       END AS validation_error
                FROM target
                UNION ALL
                SELECT {portfolioId}, {tenantAccountId}, ''::text, NULL::date,
                       {leaseAgreementId}, '00000000-0000-0000-0000-000000000000'::uuid,
                       0::numeric, 0::smallint, NULL::date, NULL::date, NULL::date, NULL::date,
                       {existingRentChargeEntryId}, 0::numeric, ''::text, NULL::date, NULL::date,
                       ''::text, {existingReceiptEntryId}, 0::numeric, ''::text,
                       {existingAllocationId}, 0::numeric, 0::numeric, false, false, false, false,
                       false,
                       'No matching active tenant account, agreement, charge, receipt, and allocation tuple was found.'
                WHERE NOT EXISTS (SELECT 1 FROM target)
            ), updated_account AS (
                UPDATE "TenantAccounts" AS account
                   SET "RentTrackingStartOn" = {correctRentTrackingStartOn}
                FROM validation
                WHERE validation.is_valid
                  AND account."PortfolioId" = validation."PortfolioId"
                  AND account."Id" = validation.tenant_account_id
                RETURNING account."Id", account."RentTrackingStartOn"
            ), inserted_reversal AS (
                INSERT INTO "TenantLedgerEntries" (
                    "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount",
                    "Currency", "EffectiveOn", "DueOn", "PostedAtUtc", "Description",
                    "BusinessKey", "LeaseAgreementId", "ReversesEntryId", "CreatedByUserId")
                SELECT validation."PortfolioId", validation.tenant_account_id,
                       'Reversal', 'Credit', validation.charge_amount, validation."Currency",
                       {rentPeriodStartOn}, NULL, {postedAtUtc},
                       'Historical rent correction reversal ' || {normalizedReference},
                       {reversalBusinessKey}, NULL, validation.charge_id, {actorUserId}
                FROM validation
                WHERE validation.is_valid
                RETURNING "Id", "ReversesEntryId", "Amount"
            ), inserted_replacement AS (
                INSERT INTO "TenantLedgerEntries" (
                    "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount",
                    "Currency", "EffectiveOn", "DueOn", "PostedAtUtc", "Description",
                    "BusinessKey", "LeaseAgreementId", "CreatedByUserId")
                SELECT validation."PortfolioId", validation.tenant_account_id,
                       'RentCharge', 'Debit', {correctRentAmount}, validation."Currency",
                       {rentPeriodStartOn}, {rentPeriodStartOn}, {postedAtUtc},
                       'Rent due ' || to_char({rentPeriodStartOn}::date, 'Mon FMDD, YYYY')
                           || ' (historical correction ' || {normalizedReference} || ')',
                       {replacementBusinessKey}, validation.agreement_id, {actorUserId}
                FROM validation
                WHERE validation.is_valid
                RETURNING "Id", "Amount"
            ), inserted_allocation_reversal AS (
                INSERT INTO "TenantLedgerAllocations" (
                    "PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId",
                    "Amount", "AllocatedAtUtc", "BusinessKey", "ReversesAllocationId",
                    "CreatedByUserId")
                SELECT validation."PortfolioId", validation.tenant_account_id,
                       validation.charge_id, validation.receipt_id, -validation.allocation_amount,
                       {postedAtUtc}, {allocationReversalBusinessKey}, validation.allocation_id,
                       {actorUserId}
                FROM validation
                WHERE validation.is_valid
                RETURNING "Id", "ReversesAllocationId", -"Amount" AS "Amount"
            ), inserted_reallocation AS (
                INSERT INTO "TenantLedgerAllocations" (
                    "PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId",
                    "Amount", "AllocatedAtUtc", "BusinessKey", "CreatedByUserId")
                SELECT validation."PortfolioId", validation.tenant_account_id,
                       replacement."Id", validation.receipt_id, {correctRentAmount},
                       {postedAtUtc}, {allocationBusinessKey}, {actorUserId}
                FROM validation
                CROSS JOIN inserted_replacement AS replacement
                WHERE validation.is_valid
                RETURNING "Id", "DebitEntryId", "CreditEntryId", "Amount"
            )
            SELECT validation.is_valid AS "IsValid",
                   validation.charge_id AS "ReversedRentChargeEntryId",
                   COALESCE((SELECT "Id" FROM inserted_reversal), 0) AS "ReversalEntryId",
                   COALESCE((SELECT "Id" FROM inserted_replacement), 0) AS "ReplacementRentChargeEntryId",
                   validation.receipt_id AS "ReceiptEntryId",
                   COALESCE((SELECT "Id" FROM inserted_allocation_reversal), 0) AS "ReversedAllocationId",
                   COALESCE((SELECT "Id" FROM inserted_reallocation), 0) AS "ReplacementAllocationId",
                   COALESCE((SELECT "RentTrackingStartOn" FROM updated_account),
                            {correctRentTrackingStartOn}) AS "RentTrackingStartOn",
                   validation.charge_amount AS "ReversedRentAmount",
                   {correctRentAmount} AS "ReplacementRentAmount",
                   COALESCE((SELECT "Amount" FROM inserted_reallocation), 0::numeric)
                       AS "ReallocatedAmount",
                   validation.validation_error AS "ValidationError"
            FROM validation
            """, mutationTargets, ct);

        return rows.Single();
    }

    public static async Task<IReadOnlyList<TenantMoneyScheduledCharge>> PostScheduledLateFeesAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int batchSize,
        string stateCapsJson,
        DateTime postedAtUtc,
        CancellationToken ct = default)
    {
        if (batchSize is <= 0 or > 500) throw new ArgumentOutOfRangeException(nameof(batchSize));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateCapsJson);
        if (postedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Scheduled charge posting time must be UTC.", nameof(postedAtUtc));

        var mutationTargets = new[] { new AtomicSqlMutationTarget("TenantLedgerEntries", AtomicSqlMutationOperation.Insert) };

        return await db.ExecuteAtomicSqlMutationAsync<TenantMoneyScheduledCharge>(context, $"""
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
                       governing_agreement."Id" AS "LeaseAgreementId",
                       rent."Currency",
                       rent."BusinessKey" AS rent_business_key,
                       governing_agreement."LateFeeAmount",
                       governing_agreement."GracePeriodDays",
                       account."CreatedByUserId",
                       effective_date.business_date,
                       LEAST(
                           governing_agreement."LateFeeAmount",
                           COALESCE(cap.max_flat, governing_agreement."LateFeeAmount"),
                           COALESCE(
                               rent."Amount" * cap.max_percent / 100::numeric,
                               governing_agreement."LateFeeAmount")) AS fee_amount,
                       'late-fee:' || canonical_rent."BusinessKey" AS business_key
                FROM "vw_tenant_charge_balances" AS balance
                JOIN "TenantLedgerEntries" AS rent
                  ON rent."Id" = balance."TenantLedgerEntryId"
                 AND rent."PortfolioId" = balance."PortfolioId"
                 AND rent."TenantAccountId" = balance."TenantAccountId"
                JOIN "TenantAccounts" AS account
                  ON account."Id" = rent."TenantAccountId"
                 AND account."PortfolioId" = rent."PortfolioId"
                JOIN "LeaseManagements" AS management
                  ON management."Id" = account."LeaseManagementId"
                 AND management."PortfolioId" = account."PortfolioId"
                JOIN "Properties" AS property
                  ON property."Id" = management."PropertyId"
                 AND property."PortfolioId" = management."PortfolioId"
                JOIN "AutomationSettings" AS settings
                  ON settings."PortfolioId" = rent."PortfolioId"
                 AND settings."EnableLateFees"
                LEFT JOIN caps AS cap ON cap.state = upper(COALESCE(property."State", ''))
                CROSS JOIN LATERAL (
                    SELECT rc_business_date(rent."PortfolioId") AS business_date,
                           rc_effective_now_utc(rent."PortfolioId") AS effective_now_utc
                ) AS effective_date
                JOIN LATERAL (
                    SELECT candidate_agreement."Id",
                           candidate_agreement."LateFeeAmount",
                           candidate_agreement."GracePeriodDays"
                    FROM "LeaseAgreements" AS candidate_agreement
                    WHERE candidate_agreement."PortfolioId" = rent."PortfolioId"
                      AND candidate_agreement."LeaseManagementId" = management."Id"
                      AND candidate_agreement."FullyExecutedAtUtc" IS NOT NULL
                      AND candidate_agreement."VoidedAtUtc" IS NULL
                      AND candidate_agreement."DraftCanceledAtUtc" IS NULL
                      AND candidate_agreement."TermStartOn" <= effective_date.business_date
                      AND (candidate_agreement."TermEndOn" IS NULL
                           OR candidate_agreement."TermEndOn" >= effective_date.business_date)
                      AND candidate_agreement."GoverningFromOn" <= effective_date.business_date
                      AND (candidate_agreement."SupersededEffectiveOn" IS NULL
                           OR candidate_agreement."SupersededEffectiveOn"
                              > effective_date.business_date)
                    ORDER BY candidate_agreement."GoverningFromOn" DESC,
                             candidate_agreement."VersionNumber" DESC,
                             candidate_agreement."Id" DESC
                    LIMIT 1
                ) AS governing_agreement ON true
                JOIN LATERAL (
                    -- A correction/recovery may replace the physical rent row. Keep the first
                    -- immutable rent-period key so every later representation converges on the
                    -- already published late-fee identity.
                    SELECT period_rent."BusinessKey"
                    FROM "TenantLedgerEntries" AS period_rent
                    WHERE period_rent."PortfolioId" = rent."PortfolioId"
                      AND period_rent."TenantAccountId" = rent."TenantAccountId"
                      AND period_rent."EntryType" = 'RentCharge'
                      AND date_trunc('month', period_rent."EffectiveOn"::timestamp)
                          = date_trunc('month', rent."EffectiveOn"::timestamp)
                    ORDER BY period_rent."Id"
                    LIMIT 1
                ) AS canonical_rent ON true
                WHERE rent."EntryType" = 'RentCharge'
                  AND balance."OpenAmount" > 0
                  AND rent."DueOn" <= effective_date.business_date
                      - GREATEST(
                          governing_agreement."GracePeriodDays"::integer,
                          settings."LateFeeGraceDays",
                          0)
                  -- Backfilled rent carries a historical period date but is newly posted. Late
                  -- fees begin only after the configured grace period has elapsed since posting,
                  -- never on the sweep that first creates a historical charge.
                  AND rent."PostedAtUtc" <= effective_date.effective_now_utc
                      - GREATEST(
                          governing_agreement."GracePeriodDays"::integer,
                          settings."LateFeeGraceDays",
                          0) * interval '1 day'
                  AND governing_agreement."LateFeeAmount" > 0
                  AND management."CanceledAtUtc" IS NULL
                  AND account."ClosedAtUtc" IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerEntries" AS existing
                      WHERE existing."TenantAccountId" = rent."TenantAccountId"
                        AND existing."BusinessKey" =
                            'late-fee:' || canonical_rent."BusinessKey")
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
            """, mutationTargets, ct);
    }

    public static async Task<TenantMoneyLateFeeChargeRecovery> RecoverLateFeeChargesAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        string correctionsJson,
        int expectedReviewedChargeCount,
        int expectedReversedChargeCount,
        int expectedReplacementChargeCount,
        decimal expectedReversedTotal,
        decimal expectedReplacementTotal,
        string financialReference,
        int actorUserId,
        DateTime postedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correctionsJson);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedReviewedChargeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedReversedChargeCount);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedReplacementChargeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedReversedTotal);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedReplacementTotal);
        ArgumentException.ThrowIfNullOrWhiteSpace(financialReference);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorUserId);
        if (postedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Late-fee recovery posting time must be UTC.", nameof(postedAtUtc));

        var reference = financialReference.Trim();
        if (reference.Length > 80)
            throw new ArgumentException("Financial reference cannot exceed 80 characters.");

        var mutationTargets = new AtomicSqlMutationTarget[] {
            new AtomicSqlMutationTarget("TenantLedgerEntries", AtomicSqlMutationOperation.Insert),
            new AtomicSqlMutationTarget("TenantLedgerAllocations", AtomicSqlMutationOperation.Insert) };

        var rows = await db.ExecuteAtomicSqlMutationAsync<TenantMoneyLateFeeChargeRecovery>(context, $"""
            WITH requested AS MATERIALIZED (
                SELECT row_number() OVER ()::integer AS request_ordinal,
                       request."TenantAccountId"::integer AS tenant_account_id,
                       request."ExistingLateFeeEntryId"::bigint AS existing_late_fee_entry_id,
                       request."ExpectedExistingAmount"::numeric AS expected_existing_amount,
                       request."ReplacementAmount"::numeric AS replacement_amount,
                       COALESCE(request."AlreadyReversed"::boolean, false) AS already_reversed
                FROM jsonb_to_recordset(CAST({correctionsJson} AS jsonb)) AS request(
                    "TenantAccountId" integer,
                    "ExistingLateFeeEntryId" bigint,
                    "ExpectedExistingAmount" numeric,
                    "ReplacementAmount" numeric,
                    "AlreadyReversed" boolean)
            ),
            requested_validation AS MATERIALIZED (
                SELECT count(*)::integer AS requested_count,
                       count(DISTINCT existing_late_fee_entry_id)::integer AS distinct_charge_count,
                       count(*) FILTER (WHERE tenant_account_id <= 0
                           OR existing_late_fee_entry_id <= 0
                           OR expected_existing_amount <= 0
                           OR replacement_amount < 0
                           OR (already_reversed AND replacement_amount <= 0))::integer AS invalid_row_count,
                       count(*) FILTER (WHERE NOT already_reversed)::integer AS reversal_count,
                       count(*) FILTER (WHERE replacement_amount > 0)::integer AS replacement_count,
                       COALESCE(sum(expected_existing_amount) FILTER (WHERE NOT already_reversed), 0)::numeric
                           AS expected_reversed_total,
                       COALESCE(sum(replacement_amount), 0)::numeric AS expected_replacement_total
                FROM requested
            ),
            targets AS MATERIALIZED (
                SELECT requested.request_ordinal,
                       requested.tenant_account_id,
                       requested.existing_late_fee_entry_id,
                       requested.expected_existing_amount,
                       requested.replacement_amount,
                       requested.already_reversed,
                       charge."PortfolioId",
                       charge."LeaseAgreementId",
                       charge."Currency",
                       charge."EffectiveOn",
                       charge."DueOn",
                       charge."Description",
                       charge."BusinessKey",
                       charge."Amount",
                       COALESCE(reversals.reversed_amount, 0)::numeric AS reversed_amount,
                       COALESCE(allocations.allocated_amount, 0)::numeric AS allocated_amount,
                       COALESCE(allocations.uncompensated_allocation_count, 0)::integer
                           AS uncompensated_allocation_count
                FROM requested
                JOIN "TenantLedgerEntries" AS charge
                  ON charge."Id" = requested.existing_late_fee_entry_id
                 AND charge."TenantAccountId" = requested.tenant_account_id
                 AND charge."PortfolioId" = {portfolioId}
                 AND charge."EntryType" = 'LateFeeCharge'
                 AND charge."Direction" = 'Debit'
                LEFT JOIN LATERAL (
                    SELECT COALESCE(sum(reversal."Amount"), 0)::numeric AS reversed_amount
                    FROM "TenantLedgerEntries" AS reversal
                    WHERE reversal."PortfolioId" = charge."PortfolioId"
                      AND reversal."TenantAccountId" = charge."TenantAccountId"
                      AND reversal."EntryType" = 'Reversal'
                      AND reversal."ReversesEntryId" = charge."Id"
                ) AS reversals ON true
                LEFT JOIN LATERAL (
                    SELECT COALESCE(sum(allocation."Amount"), 0)::numeric AS allocated_amount,
                           count(*) FILTER (
                               WHERE allocation."ReversesAllocationId" IS NULL
                                 AND allocation."Amount" > 0
                                 AND NOT EXISTS (
                                     SELECT 1
                                     FROM "TenantLedgerAllocations" AS existing_reversal
                                     WHERE existing_reversal."PortfolioId" = allocation."PortfolioId"
                                       AND existing_reversal."TenantAccountId" = allocation."TenantAccountId"
                                       AND existing_reversal."ReversesAllocationId" = allocation."Id")
                           )::integer AS uncompensated_allocation_count
                    FROM "TenantLedgerAllocations" AS allocation
                    WHERE allocation."PortfolioId" = charge."PortfolioId"
                      AND allocation."TenantAccountId" = charge."TenantAccountId"
                      AND allocation."DebitEntryId" = charge."Id"
                ) AS allocations ON true
            ),
            target_validation AS MATERIALIZED (
                SELECT requested_validation.requested_count,
                       requested_validation.reversal_count,
                       requested_validation.replacement_count,
                       COALESCE(count(targets.*), 0)::integer AS matched_count,
                       COALESCE(sum(targets."Amount") FILTER (WHERE NOT targets.already_reversed), 0)::numeric
                           AS matched_reversed_total,
                       COALESCE(sum(targets.replacement_amount), 0)::numeric AS matched_replacement_total,
                       COALESCE(sum(targets.allocated_amount), 0)::numeric AS matched_allocated_total,
                       COALESCE(sum(targets.uncompensated_allocation_count), 0)::integer
                           AS uncompensated_allocation_count,
                       requested_validation.requested_count = {expectedReviewedChargeCount}
                         AND requested_validation.distinct_charge_count = requested_validation.requested_count
                         AND requested_validation.invalid_row_count = 0
                         AND requested_validation.reversal_count = {expectedReversedChargeCount}
                         AND requested_validation.replacement_count = {expectedReplacementChargeCount}
                         AND requested_validation.expected_reversed_total = {expectedReversedTotal}
                         AND requested_validation.expected_replacement_total = {expectedReplacementTotal}
                         AND count(targets.*)::integer = requested_validation.requested_count
                         AND COALESCE(sum(targets."Amount") FILTER (WHERE NOT targets.already_reversed), 0)::numeric
                             = {expectedReversedTotal}
                         AND COALESCE(sum(targets.replacement_amount), 0)::numeric = {expectedReplacementTotal}
                         AND COALESCE(sum(CASE
                             WHEN targets."Amount" = targets.expected_existing_amount
                              AND ((NOT targets.already_reversed AND targets.reversed_amount = 0)
                                   OR (targets.already_reversed AND targets.reversed_amount = targets."Amount"))
                              AND targets.replacement_amount <= targets."Amount"
                             THEN 0 ELSE 1 END), 0) = 0 AS is_valid,
                       CASE
                         WHEN requested_validation.requested_count <> {expectedReviewedChargeCount}
                           THEN 'Late-fee recovery row count does not match the reviewed control.'
                         WHEN requested_validation.distinct_charge_count <> requested_validation.requested_count
                           THEN 'Late-fee recovery contains duplicate existing charge rows.'
                         WHEN requested_validation.invalid_row_count <> 0
                           THEN 'Late-fee recovery contains invalid row input.'
                         WHEN requested_validation.reversal_count <> {expectedReversedChargeCount}
                           THEN 'Late-fee reversal count does not match the reviewed control.'
                         WHEN requested_validation.replacement_count <> {expectedReplacementChargeCount}
                           THEN 'Late-fee replacement count does not match the reviewed control.'
                         WHEN requested_validation.expected_reversed_total <> {expectedReversedTotal}
                           THEN 'Requested late-fee reversal total does not match the reviewed control.'
                         WHEN requested_validation.expected_replacement_total <> {expectedReplacementTotal}
                           THEN 'Requested late-fee replacement total does not match the reviewed control.'
                         WHEN count(targets.*)::integer <> requested_validation.requested_count
                           THEN 'A requested late-fee row no longer matches current ledger state.'
                         WHEN COALESCE(sum(targets."Amount") FILTER (WHERE NOT targets.already_reversed), 0)::numeric
                              <> {expectedReversedTotal}
                           THEN 'Current late-fee total no longer matches the reviewed control.'
                         WHEN COALESCE(sum(targets.replacement_amount), 0)::numeric <> {expectedReplacementTotal}
                           THEN 'Current replacement total no longer matches the reviewed control.'
                         WHEN COALESCE(sum(CASE
                             WHEN targets."Amount" = targets.expected_existing_amount
                              AND ((NOT targets.already_reversed AND targets.reversed_amount = 0)
                                   OR (targets.already_reversed AND targets.reversed_amount = targets."Amount"))
                              AND targets.replacement_amount <= targets."Amount"
                             THEN 0 ELSE 1 END), 0) <> 0
                           THEN 'A requested late fee reversal state or amount no longer matches the reviewed control.'
                         ELSE ''
                       END AS validation_error
                FROM requested_validation
                LEFT JOIN targets ON true
                GROUP BY requested_validation.requested_count,
                         requested_validation.distinct_charge_count,
                         requested_validation.invalid_row_count,
                         requested_validation.reversal_count,
                         requested_validation.replacement_count,
                         requested_validation.expected_reversed_total,
                         requested_validation.expected_replacement_total
            ),
            inserted_reversals AS (
                INSERT INTO "TenantLedgerEntries" (
                    "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount",
                    "Currency", "EffectiveOn", "DueOn", "PostedAtUtc", "Description",
                    "BusinessKey", "LeaseAgreementId", "ReversesEntryId", "CreatedByUserId")
                SELECT target."PortfolioId", target.tenant_account_id, 'Reversal',
                       'Credit', target."Amount", target."Currency", target."EffectiveOn",
                       NULL, {postedAtUtc},
                       'Late fee recovery ' || {reference} || ': ' || target."Description",
                       'late-fee-recovery:' || {reference} || ':reverse:'
                           || target.existing_late_fee_entry_id::text,
                       target."LeaseAgreementId", target.existing_late_fee_entry_id,
                       {actorUserId}
                FROM targets AS target
                CROSS JOIN target_validation AS validation
                WHERE validation.is_valid
                  AND NOT target.already_reversed
                ORDER BY target.request_ordinal
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Id", "PortfolioId", "TenantAccountId", "Amount", "ReversesEntryId"
            ),
            inserted_allocation_reversals AS (
                INSERT INTO "TenantLedgerAllocations" (
                    "PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId",
                    "Amount", "AllocatedAtUtc", "BusinessKey", "ReversesAllocationId",
                    "CreatedByUserId")
                SELECT allocation."PortfolioId", allocation."TenantAccountId",
                       allocation."DebitEntryId", allocation."CreditEntryId",
                       -allocation."Amount", {postedAtUtc},
                       'late-fee-recovery:' || {reference} || ':allocation-reverse:'
                           || allocation."Id"::text,
                       allocation."Id", {actorUserId}
                FROM "TenantLedgerAllocations" AS allocation
                JOIN targets AS target
                  ON target."PortfolioId" = allocation."PortfolioId"
                 AND target.tenant_account_id = allocation."TenantAccountId"
                 AND target.existing_late_fee_entry_id = allocation."DebitEntryId"
                CROSS JOIN target_validation AS validation
                WHERE validation.is_valid
                  AND NOT target.already_reversed
                  AND allocation."ReversesAllocationId" IS NULL
                  AND allocation."Amount" > 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerAllocations" AS existing_reversal
                      WHERE existing_reversal."PortfolioId" = allocation."PortfolioId"
                        AND existing_reversal."TenantAccountId" = allocation."TenantAccountId"
                        AND existing_reversal."ReversesAllocationId" = allocation."Id")
                ORDER BY allocation."Id"
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Amount"
            ),
            inserted_replacements AS (
                INSERT INTO "TenantLedgerEntries" (
                    "PortfolioId", "TenantAccountId", "EntryType", "Direction", "Amount",
                    "Currency", "EffectiveOn", "DueOn", "PostedAtUtc", "Description",
                    "BusinessKey", "LeaseAgreementId", "CreatedByUserId")
                SELECT target."PortfolioId", target.tenant_account_id, 'LateFeeCharge',
                       'Debit', target.replacement_amount, target."Currency",
                       target."EffectiveOn", target."DueOn", {postedAtUtc},
                       'Corrected late fee recovery ' || {reference},
                       'late-fee-recovery:' || {reference} || ':replacement:'
                           || target.existing_late_fee_entry_id::text,
                       target."LeaseAgreementId", {actorUserId}
                FROM targets AS target
                CROSS JOIN target_validation AS validation
                WHERE validation.is_valid
                  AND target.replacement_amount > 0
                ORDER BY target.request_ordinal
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Id", "PortfolioId", "TenantAccountId", "Amount", "BusinessKey"
            ),
            resolved_replacements AS MATERIALIZED (
                SELECT replacement."Id",
                       replacement."PortfolioId",
                       replacement."TenantAccountId",
                       replacement."Amount",
                       target.existing_late_fee_entry_id
                FROM inserted_replacements AS replacement
                JOIN targets AS target
                  ON target."PortfolioId" = replacement."PortfolioId"
                 AND target.tenant_account_id = replacement."TenantAccountId"
                 AND replacement."BusinessKey" =
                     'late-fee-recovery:' || {reference} || ':replacement:'
                         || target.existing_late_fee_entry_id::text
            ),
            inserted_replacement_allocations AS (
                INSERT INTO "TenantLedgerAllocations" (
                    "PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId",
                    "Amount", "AllocatedAtUtc", "BusinessKey", "CreatedByUserId")
                SELECT source."PortfolioId", source."TenantAccountId",
                       replacement."Id", source."CreditEntryId",
                       LEAST(source."Amount", replacement."Amount"), {postedAtUtc},
                       'late-fee-recovery:' || {reference} || ':replacement-allocation:'
                           || source."Id"::text,
                       {actorUserId}
                FROM "TenantLedgerAllocations" AS source
                JOIN targets AS target
                  ON target."PortfolioId" = source."PortfolioId"
                 AND target.tenant_account_id = source."TenantAccountId"
                 AND target.existing_late_fee_entry_id = source."DebitEntryId"
                JOIN resolved_replacements AS replacement
                  ON replacement."PortfolioId" = target."PortfolioId"
                 AND replacement."TenantAccountId" = target.tenant_account_id
                 AND replacement.existing_late_fee_entry_id =
                     target.existing_late_fee_entry_id
                 AND replacement.existing_late_fee_entry_id = source."DebitEntryId"
                CROSS JOIN target_validation AS validation
                WHERE validation.is_valid
                  AND NOT target.already_reversed
                  AND source."ReversesAllocationId" IS NULL
                  AND source."Amount" > 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerAllocations" AS existing_reversal
                      WHERE existing_reversal."PortfolioId" = source."PortfolioId"
                        AND existing_reversal."TenantAccountId" = source."TenantAccountId"
                        AND existing_reversal."ReversesAllocationId" = source."Id")
                ORDER BY source."Id"
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Amount"
            )
            SELECT validation.is_valid AS "IsValid",
                   (SELECT count(*)::integer FROM inserted_reversals) AS "ReversedChargeCount",
                   (SELECT count(*)::integer FROM inserted_replacements) AS "ReplacementChargeCount",
                   (SELECT count(*)::integer FROM inserted_allocation_reversals)
                       AS "ReversedAllocationCount",
                   (SELECT count(*)::integer FROM inserted_replacement_allocations)
                       AS "ReplacementAllocationCount",
                   COALESCE((SELECT sum("Amount") FROM inserted_reversals), 0)::numeric
                       AS "ReversedTotal",
                   COALESCE((SELECT sum("Amount") FROM inserted_replacements), 0)::numeric
                       AS "ReplacementTotal",
                   COALESCE(-(SELECT sum("Amount") FROM inserted_allocation_reversals), 0)::numeric
                       AS "ReversedAllocationTotal",
                   COALESCE((SELECT sum("Amount") FROM inserted_replacement_allocations), 0)::numeric
                       AS "ReplacementAllocationTotal",
                   validation.validation_error AS "ValidationError"
            FROM target_validation AS validation
            """, mutationTargets, ct);

        return rows.Single();
    }

    public static async Task<TenantMoneyAllocationSummary> AllocateOldestChargesAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
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

        var mutationTargets = new[] { new AtomicSqlMutationTarget("TenantLedgerAllocations", AtomicSqlMutationOperation.Insert) };

        var rows = await db.ExecuteAtomicSqlMutationAsync<TenantMoneyAllocationSummary>(context, $"""
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
            """, mutationTargets, ct);
        return rows.Single();
    }

    public static async Task<TenantMoneyAllocationSummary> AllocateTargetChargeAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int tenantAccountId,
        long creditEntryId,
        long targetChargeEntryId,
        decimal availableAmount,
        string businessKeyPrefix,
        int createdByUserId,
        DateTime allocatedAtUtc,
        CancellationToken ct = default,
        bool spillToOtherCharges = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tenantAccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(creditEntryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetChargeEntryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(availableAmount);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessKeyPrefix);

        var mutationTargets = new[] { new AtomicSqlMutationTarget("TenantLedgerAllocations", AtomicSqlMutationOperation.Insert) };

        var rows = await db.ExecuteAtomicSqlMutationAsync<TenantMoneyAllocationSummary>(context, $"""
            WITH candidates AS MATERIALIZED (
                SELECT balance."TenantLedgerEntryId",
                       balance."OpenAmount",
                       0 AS allocation_rank,
                       balance."DueOn"
                FROM "vw_tenant_charge_balances" AS balance
                WHERE balance."PortfolioId" = {portfolioId}
                  AND balance."TenantAccountId" = {tenantAccountId}
                  AND balance."TenantLedgerEntryId" = {targetChargeEntryId}
                  AND balance."OpenAmount" > 0
                UNION ALL
                SELECT balance."TenantLedgerEntryId",
                       balance."OpenAmount",
                       1 AS allocation_rank,
                       balance."DueOn"
                FROM "vw_tenant_charge_balances" AS balance
                WHERE balance."PortfolioId" = {portfolioId}
                  AND balance."TenantAccountId" = {tenantAccountId}
                  AND balance."TenantLedgerEntryId" <> {targetChargeEntryId}
                  AND balance."OpenAmount" > 0
            ), eligible AS (
                SELECT candidates."TenantLedgerEntryId",
                       candidates."OpenAmount",
                       candidates.allocation_rank,
                       COALESCE(
                           SUM(candidates."OpenAmount") OVER (
                               ORDER BY candidates.allocation_rank,
                                        candidates."DueOn" NULLS LAST,
                                        candidates."TenantLedgerEntryId"
                               ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING),
                           0) AS consumed_before
                FROM candidates
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
                  AND ({spillToOtherCharges} OR eligible.allocation_rank = 0)
                ORDER BY eligible.consumed_before, eligible."TenantLedgerEntryId"
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Amount"
            )
            SELECT COUNT(*)::integer AS "AllocationCount",
                   COALESCE(SUM(inserted."Amount"), 0)::numeric AS "AllocatedAmount"
            FROM inserted
            """, mutationTargets, ct);
        return rows.Single();
    }

    public static async Task<TenantMoneyRefundedAllocationRecovery> RecoverRefundedAllocationAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int tenantAccountId,
        long existingAllocationId,
        long expectedDebitEntryId,
        long expectedCreditEntryId,
        long expectedRefundPaymentAttemptId,
        decimal expectedAllocationAmount,
        decimal expectedRefundAmount,
        string businessKey,
        int actorUserId,
        DateTime allocatedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tenantAccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(existingAllocationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedDebitEntryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCreditEntryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRefundPaymentAttemptId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedAllocationAmount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRefundAmount);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorUserId);

        var mutationTargets = new[] { new AtomicSqlMutationTarget("TenantLedgerAllocations", AtomicSqlMutationOperation.Insert) };

        var rows = await db.ExecuteAtomicSqlMutationAsync<TenantMoneyRefundedAllocationRecovery>(context, $"""
            WITH valid_source AS MATERIALIZED (
                SELECT allocation."Id",
                       allocation."PortfolioId",
                       allocation."TenantAccountId",
                       allocation."DebitEntryId",
                       allocation."CreditEntryId",
                       allocation."Amount",
                       refund."Id" AS refund_payment_attempt_id
                FROM "TenantLedgerAllocations" AS allocation
                JOIN "TenantLedgerEntries" AS debit
                  ON debit."Id" = allocation."DebitEntryId"
                 AND debit."TenantAccountId" = allocation."TenantAccountId"
                 AND debit."PortfolioId" = allocation."PortfolioId"
                JOIN "TenantLedgerEntries" AS credit
                  ON credit."Id" = allocation."CreditEntryId"
                 AND credit."TenantAccountId" = allocation."TenantAccountId"
                 AND credit."PortfolioId" = allocation."PortfolioId"
                JOIN "TenantPaymentAttempts" AS refund
                  ON refund."Id" = {expectedRefundPaymentAttemptId}
                 AND refund."TenantAccountId" = allocation."TenantAccountId"
                 AND refund."PortfolioId" = allocation."PortfolioId"
                 AND refund."RefundsPaymentAttemptId" = credit."ProviderPaymentAttemptId"
                WHERE allocation."Id" = {existingAllocationId}
                  AND allocation."PortfolioId" = {portfolioId}
                  AND allocation."TenantAccountId" = {tenantAccountId}
                  AND allocation."DebitEntryId" = {expectedDebitEntryId}
                  AND allocation."CreditEntryId" = {expectedCreditEntryId}
                  AND allocation."Amount" = {expectedAllocationAmount}
                  AND allocation."ReversesAllocationId" IS NULL
                  AND debit."Direction" = 'Debit'
                  AND credit."EntryType" = 'PaymentReceipt'
                  AND credit."Direction" = 'Credit'
                  AND refund."AttemptType" = 'Refund'
                  AND refund."State" = 'Succeeded'
                  AND refund."Amount" = {expectedRefundAmount}
                  AND refund."Amount" = credit."Amount"
                  AND refund."Amount" >= allocation."Amount"
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "TenantLedgerAllocations" AS existing_reversal
                      WHERE existing_reversal."PortfolioId" = allocation."PortfolioId"
                        AND existing_reversal."TenantAccountId" = allocation."TenantAccountId"
                        AND existing_reversal."ReversesAllocationId" = allocation."Id")
                FOR UPDATE OF allocation
            ), inserted AS (
                INSERT INTO "TenantLedgerAllocations" (
                    "PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId",
                    "Amount", "AllocatedAtUtc", "BusinessKey", "ReversesAllocationId",
                    "CreatedByUserId")
                SELECT source."PortfolioId", source."TenantAccountId", source."DebitEntryId",
                       source."CreditEntryId", -source."Amount", {allocatedAtUtc},
                       {businessKey}, source."Id", {actorUserId}
                FROM valid_source AS source
                ON CONFLICT ("TenantAccountId", "BusinessKey") DO NOTHING
                RETURNING "Id", "ReversesAllocationId", "DebitEntryId", "CreditEntryId", "Amount"
            )
            SELECT EXISTS (SELECT 1 FROM inserted) AS "IsValid",
                   COALESCE((SELECT inserted."ReversesAllocationId" FROM inserted), 0)
                       AS "ReversedAllocationId",
                   COALESCE((SELECT inserted."Id" FROM inserted), 0) AS "ReversalAllocationId",
                   COALESCE((SELECT inserted."DebitEntryId" FROM inserted), 0) AS "DebitEntryId",
                   COALESCE((SELECT inserted."CreditEntryId" FROM inserted), 0) AS "CreditEntryId",
                   COALESCE((SELECT source.refund_payment_attempt_id FROM valid_source AS source), 0)
                       AS "RefundPaymentAttemptId",
                   COALESCE(-(SELECT inserted."Amount" FROM inserted), 0)::numeric
                       AS "ReversedAmount",
                   CASE
                       WHEN EXISTS (SELECT 1 FROM inserted) THEN ''
                       ELSE 'The reviewed allocation/refund tuple no longer matches or was already compensated.'
                   END AS "ValidationError"
            """, mutationTargets, ct);
        return rows.Single();
    }

    public static async Task<TenantMoneyAllocationSummary> ReverseEntryAllocationsAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int tenantAccountId,
        long ledgerEntryId,
        DateOnly effectiveOn,
        string businessKeyPrefix,
        int createdByUserId,
        DateTime allocatedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tenantAccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ledgerEntryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessKeyPrefix);

        var mutationTargets = new[] { new AtomicSqlMutationTarget("TenantLedgerAllocations", AtomicSqlMutationOperation.Insert) };

        var rows = await db.ExecuteAtomicSqlMutationAsync<TenantMoneyAllocationSummary>(context, $"""
            WITH inserted AS (
                INSERT INTO "TenantLedgerAllocations" (
                    "PortfolioId", "TenantAccountId", "DebitEntryId", "CreditEntryId",
                    "Amount", "AllocatedAtUtc", "EffectiveOn", "BusinessKey", "ReversesAllocationId",
                    "CreatedByUserId")
                SELECT source."PortfolioId", source."TenantAccountId", source."DebitEntryId",
                       source."CreditEntryId", -source."Amount", {allocatedAtUtc},
                       {effectiveOn},
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
            """, mutationTargets, ct);
        return rows.Single();
    }
}
