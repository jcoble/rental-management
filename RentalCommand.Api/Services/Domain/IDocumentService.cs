using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="StoredFile"/> rows surfaced via the Documents hub.
/// Blob storage is handled directly by <see cref="Core.Interfaces.IFileStorage"/>; this service
/// manages the DB rows and validation.
/// </summary>
public interface IDocumentService
{
    /// <summary>
    /// List non-deleted <see cref="StoredFile"/>s for a given entity in the portfolio,
    /// newest first.
    /// </summary>
    Task<IReadOnlyList<DocumentDto>> ListAsync(
        int portfolioId,
        string entityType,
        int entityId,
        CancellationToken ct = default);

    /// <summary>
    /// Persist a <see cref="StoredFile"/> row for an already-stored blob. Returns the DTO.
    /// </summary>
    Task<DocumentDto> CreateAsync(
        int portfolioId,
        string entityType,
        int entityId,
        string fileName,
        string contentType,
        long sizeBytes,
        string storagePath,
        CancellationToken ct = default);

    /// <summary>
    /// Find a <see cref="StoredFile"/> row by id in the portfolio (non-deleted).
    /// Returns null when not found or not in the portfolio.
    /// </summary>
    Task<StoredFile?> FindAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Soft-delete a <see cref="StoredFile"/> row (set <c>DeletedAt</c>).
    /// Returns false when not found or already deleted.
    /// </summary>
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
