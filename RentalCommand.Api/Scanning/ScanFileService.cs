using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Implements <see cref="IScanFileService"/>: validates the upload, writes the blob via
/// <see cref="IFileStorage"/>, creates the <see cref="StoredFile"/> DB row, and saves.
/// On <see cref="DbContext"/> failure it best-effort deletes the orphan blob before rethrowing.
/// </summary>
public sealed class ScanFileService : IScanFileService
{
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _storage;
    private readonly UploadSettings _settings;
    private readonly ILogger<ScanFileService> _logger;
    private readonly TimeProvider _timeProvider;

    public ScanFileService(
        RentalCommandDbContext db,
        IFileStorage storage,
        IOptions<UploadSettings> settings,
        ILogger<ScanFileService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _storage = storage;
        _settings = settings.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<StoredFile> StoreAsync(
        int portfolioId,
        string targetEntityType,
        byte[] bytes,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        // 1. Validate before touching storage.
        // Pass the first 16 bytes for magic-byte content sniffing (defense-in-depth).
        var header = bytes.Length > 0 ? bytes[..Math.Min(16, bytes.Length)] : null;
        var (isValid, error) = FileUploadValidator.ValidateScanUpload(fileName, contentType, bytes.Length, _settings, header);
        if (!isValid)
            throw new ArgumentException(error ?? "Invalid upload.");

        // 2. Write the blob.
        var key = await _storage.UploadAsync(new MemoryStream(bytes), fileName, contentType, ct);

        // 3. Persist the StoredFile row; clean up the blob if the DB write fails.
        var storedFile = new StoredFile
        {
            PortfolioId = portfolioId,
            FileName = DiskFileStorage.SanitizeFileName(fileName),
            FilePath = key,
            ContentType = contentType,
            FileSize = bytes.Length,
            EntityType = targetEntityType,
            EntityId = null,
            UploadedAt = _timeProvider.UtcNow()
        };

        _db.StoredFiles.Add(storedFile);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveChangesAsync failed for StoredFile; attempting to delete orphan blob '{Key}'.", key);
            try
            {
                await _storage.DeleteAsync(key, ct);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to clean up orphan blob '{Key}' after DB error.", key);
            }
            throw;
        }

        return storedFile;
    }
}
