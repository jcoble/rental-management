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

    public async Task<AtomicLedgerAllocationSummary> ReverseChargeAllocationsAsync(
        int portfolioId,
        int tenantAccountId,
        long debitEntryId,
        string businessKeyPrefix,
        int createdByUserId,
        DateTime allocatedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tenantAccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(debitEntryId);
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
                  AND source."DebitEntryId" = {debitEntryId}
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
