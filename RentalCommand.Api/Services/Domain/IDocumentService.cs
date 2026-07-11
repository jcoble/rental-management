using RentalCommand.Api.DTOs;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="StoredFile"/> rows surfaced via the Documents hub.
/// Upload is completed against a durable admission before this boundary. This service owns
/// receipt-backed database mutation and finalizes that admission atomically with the StoredFile row.
/// </summary>
public interface IDocumentService
{
    Task<PendingFileUploadAdmission> PrepareUploadAsync(
        int portfolioId,
        int actorUserId,
        string clientOperationId,
        string requestFingerprint,
        string fileName,
        string contentType,
        long sizeBytes,
        CancellationToken ct = default);

    Task<DocumentDto?> GetFinalizedUploadAsync(
        int portfolioId,
        PendingFileUploadAdmission admission,
        CancellationToken ct = default);

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
    Task<DocumentDto?> CreateAsync(
        Guid pendingUploadId,
        int portfolioId,
        StoredDocumentTarget target,
        int entityId,
        int userId,
        int? tenantId,
        bool isStaff,
        string clientOperationId,
        string requestFingerprint,
        string contentSha256,
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
    Task<bool> DeleteAsync(
        int portfolioId,
        int id,
        int userId,
        int? tenantId,
        bool isStaff,
        string clientOperationId,
        CancellationToken ct = default);
}
