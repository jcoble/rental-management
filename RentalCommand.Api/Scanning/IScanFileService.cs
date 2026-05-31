using RentalCommand.Core.Entities;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Writes an uploaded file to the blob store and records the <see cref="StoredFile"/> row.
/// The row is the source-of-truth for the file; the blob path is on the row.
/// </summary>
public interface IScanFileService
{
    /// <summary>
    /// Store the file bytes, create a <see cref="StoredFile"/> DB row, and return it.
    /// Throws <see cref="ArgumentException"/> if validation fails (controller maps to 400).
    /// </summary>
    Task<StoredFile> StoreAsync(
        int portfolioId,
        string targetEntityType,
        byte[] bytes,
        string fileName,
        string contentType,
        CancellationToken ct = default);
}
