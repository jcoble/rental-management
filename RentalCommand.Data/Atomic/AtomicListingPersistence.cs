using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicListingPersistence : IAtomicListingPersistence
{
    private const string ValidationSql = """
        WITH requested AS (
            SELECT item."PhotoId", item."Position"::integer
            FROM unnest(@photoIds::integer[]) WITH ORDINALITY AS item("PhotoId", "Position")
        )
        SELECT
            COUNT(*) = COUNT(DISTINCT requested."PhotoId")
            AND COUNT(*) = (
                SELECT COUNT(*)
                FROM "ListingPhotos" photo
                WHERE photo."RentalListingId" = @listingId
                  AND photo."PortfolioId" = @portfolioId)
            AND COUNT(*) = COUNT(photo."Id") AS "IsValid",
            COALESCE(BOOL_OR(photo."Position" <> requested."Position"), FALSE) AS "HasChanges"
        FROM requested
        LEFT JOIN "ListingPhotos" photo
          ON photo."Id" = requested."PhotoId"
         AND photo."RentalListingId" = @listingId
         AND photo."PortfolioId" = @portfolioId
        """;

    private const string OffsetSql = """
        UPDATE "ListingPhotos"
        SET "Position" = "Position" + 1000000
        WHERE "RentalListingId" = @listingId
          AND "PortfolioId" = @portfolioId
        """;

    private const string ApplySql = """
        UPDATE "ListingPhotos" AS photo
        SET "Position" = requested."Position"::integer
        FROM unnest(@photoIds::integer[]) WITH ORDINALITY AS requested("PhotoId", "Position")
        WHERE photo."Id" = requested."PhotoId"
          AND photo."RentalListingId" = @listingId
          AND photo."PortfolioId" = @portfolioId
        """;

    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicListingPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
        => (_db, _scope) = (db, scope);

    public async Task<AtomicListingPhotoOrderResult> ReorderPhotosAsync(
        int portfolioId,
        int rentalListingId,
        int[] photoIds,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rentalListingId);
        ArgumentNullException.ThrowIfNull(photoIds);

        var validation = await _db.Database.SqlQueryRaw<PhotoOrderValidation>(
                ValidationSql,
                PhotoIds(photoIds),
                ListingId(rentalListingId),
                PortfolioId(portfolioId))
            .SingleAsync(ct);
        if (!validation.IsValid || !validation.HasChanges)
            return new AtomicListingPhotoOrderResult(validation.IsValid, validation.HasChanges);

        using var mutation = _scope.BeginInternalRawDml("ListingPhotos", AtomicRawDmlOperation.Update);
        var offset = await _db.Database.ExecuteSqlRawAsync(
            OffsetSql,
            [ListingId(rentalListingId), PortfolioId(portfolioId)],
            ct);
        var applied = await _db.Database.ExecuteSqlRawAsync(
            ApplySql,
            [PhotoIds(photoIds), ListingId(rentalListingId), PortfolioId(portfolioId)],
            ct);
        if (offset != photoIds.Length || applied != photoIds.Length)
            throw new AtomicReceiptInvariantException("The listing photo manifest changed during reordering.");

        return new AtomicListingPhotoOrderResult(true, true);
    }

    private static NpgsqlParameter PhotoIds(int[] values) =>
        new("photoIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = values };
    private static NpgsqlParameter ListingId(int value) => new("listingId", NpgsqlDbType.Integer) { Value = value };
    private static NpgsqlParameter PortfolioId(int value) => new("portfolioId", NpgsqlDbType.Integer) { Value = value };

    private sealed class PhotoOrderValidation
    {
        public bool IsValid { get; set; }
        public bool HasChanges { get; set; }
    }
}
