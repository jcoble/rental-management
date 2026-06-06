using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Lookups for the polymorphic <see cref="StoredFile"/> attachment that the scan→draft→confirm flow
/// re-keys onto the record it created (see <c>ScanService.FinalizeDraft</c>, which stamps
/// <see cref="StoredFile.EntityType"/> + <see cref="StoredFile.EntityId"/> on confirm). Centralized so
/// the detail DTOs (scan-present flags) and the file-serving endpoints read the same row the same way.
/// </summary>
internal static class StoredFileQueries
{
    /// <summary>
    /// The most-recently-uploaded, non-deleted file attached to <paramref name="entityType"/> /
    /// <paramref name="entityId"/> within the caller's portfolio, or <c>null</c> when none exists.
    /// </summary>
    public static Task<StoredFile?> FindLatestEntityFileAsync(
        this RentalCommandDbContext db, int portfolioId, string entityType, int entityId, CancellationToken ct)
        => db.StoredFiles
            .AsNoTracking()
            .Where(f => f.PortfolioId == portfolioId &&
                        f.EntityType == entityType &&
                        f.EntityId == entityId &&
                        f.DeletedAt == null)
            .OrderByDescending(f => f.UploadedAt)
            .FirstOrDefaultAsync(ct);
}
