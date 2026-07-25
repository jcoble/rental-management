using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicInspectionPersistence : IAtomicInspectionPersistence
{
    private const string ValidationSql = """
        WITH requested AS (
            SELECT item."ItemId", item."Position"::integer - 1 AS "SortOrder"
            FROM unnest(@itemIds::integer[]) WITH ORDINALITY AS item("ItemId", "Position")
        ), target_inspection AS (
            SELECT inspection."Status"
            FROM "Inspections" inspection
            WHERE inspection."Id" = @inspectionId
              AND inspection."PortfolioId" = @portfolioId
        )
        SELECT
            EXISTS(SELECT 1 FROM target_inspection) AS "InspectionExists",
            COALESCE((SELECT "Status" FROM target_inspection), -1) AS "InspectionStatus",
            COUNT(*) = COUNT(DISTINCT requested."ItemId")
              AND COUNT(*) = (
                  SELECT COUNT(*)
                  FROM "InspectionItems" existing
                  WHERE existing."InspectionId" = @inspectionId
                    AND existing."PortfolioId" = @portfolioId)
              AND COUNT(*) = COUNT(item."Id") AS "IsValid",
            COALESCE(BOOL_OR(item."SortOrder" <> requested."SortOrder"), FALSE) AS "HasChanges"
        FROM requested
        LEFT JOIN "InspectionItems" item
          ON item."Id" = requested."ItemId"
         AND item."InspectionId" = @inspectionId
         AND item."PortfolioId" = @portfolioId
        """;

    private const string ApplySql = """
        WITH requested AS (
            SELECT item."ItemId", item."Position"::integer - 1 AS "SortOrder"
            FROM unnest(@itemIds::integer[]) WITH ORDINALITY AS item("ItemId", "Position")
        ), updated_items AS (
            UPDATE "InspectionItems" item
            SET "SortOrder" = requested."SortOrder"
            FROM requested
            WHERE item."Id" = requested."ItemId"
              AND item."InspectionId" = @inspectionId
              AND item."PortfolioId" = @portfolioId
            RETURNING item."InspectionId"
        )
        UPDATE "Inspections" inspection
        SET "UpdatedAt" = @updatedAt
        WHERE inspection."Id" = @inspectionId
          AND inspection."PortfolioId" = @portfolioId
          AND (SELECT COUNT(*) FROM updated_items) = @itemCount
        """;

    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicInspectionPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
        => (_db, _scope) = (db, scope);

    public async Task<AtomicInspectionItemOrderResult> ReorderItemsAsync(
        int portfolioId,
        int inspectionId,
        int[] itemIds,
        DateTime updatedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inspectionId);
        ArgumentNullException.ThrowIfNull(itemIds);

        var validation = await _db.Database.SqlQueryRaw<OrderValidation>(
                ValidationSql,
                ItemIds(itemIds), InspectionId(inspectionId), PortfolioId(portfolioId))
            .SingleAsync(ct);
        if (!validation.InspectionExists || !validation.IsValid || !validation.HasChanges)
        {
            return new AtomicInspectionItemOrderResult(
                validation.InspectionExists,
                validation.InspectionStatus,
                validation.IsValid,
                validation.HasChanges);
        }

        using var mutation = _scope.BeginInternalRawDmlBatch(
            new AtomicRawDmlTarget("InspectionItems", AtomicRawDmlOperation.Update),
            new AtomicRawDmlTarget("Inspections", AtomicRawDmlOperation.Update));
        var affected = await _db.Database.ExecuteSqlRawAsync(
            ApplySql,
            [
                ItemIds(itemIds), InspectionId(inspectionId), PortfolioId(portfolioId),
                new NpgsqlParameter("updatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAtUtc },
                new NpgsqlParameter("itemCount", NpgsqlDbType.Integer) { Value = itemIds.Length },
            ],
            ct);
        if (affected != 1)
            throw new AtomicReceiptInvariantException("The inspection checklist changed during reordering.");

        return new AtomicInspectionItemOrderResult(true, validation.InspectionStatus, true, true);
    }

    private static NpgsqlParameter ItemIds(int[] values) =>
        new("itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = values };
    private static NpgsqlParameter InspectionId(int value) => new("inspectionId", NpgsqlDbType.Integer) { Value = value };
    private static NpgsqlParameter PortfolioId(int value) => new("portfolioId", NpgsqlDbType.Integer) { Value = value };

    private sealed class OrderValidation
    {
        public bool InspectionExists { get; set; }
        public int InspectionStatus { get; set; }
        public bool IsValid { get; set; }
        public bool HasChanges { get; set; }
    }
}
