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
                  AND ({entryType} IS NULL OR balance."EntryType" = {entryType})
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
}
