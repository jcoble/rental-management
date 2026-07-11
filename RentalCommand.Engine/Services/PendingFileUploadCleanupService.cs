using RentalCommand.Core.Interfaces;
using RentalCommand.Data.Documents;

namespace RentalCommand.Engine.Services;

public sealed class PendingFileUploadCleanupService
{
    private static readonly TimeSpan PreparedRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);
    private readonly IPendingFileUploadStore _store;
    private readonly IFileStorage _storage;
    private readonly ILogger<PendingFileUploadCleanupService> _logger;

    public PendingFileUploadCleanupService(
        IPendingFileUploadStore store,
        IFileStorage storage,
        ILogger<PendingFileUploadCleanupService> logger)
    {
        _store = store;
        _storage = storage;
        _logger = logger;
    }

    public async Task<int> RunBatchAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var claims = await _store.ClaimExpiredAsync(
            now, now.Subtract(PreparedRetention), ClaimLease, batchSize: 25, ct);
        var cleaned = 0;
        foreach (var claim in claims)
        {
            try
            {
                await _storage.DeleteAsync(claim.StoragePath, ct);
                cleaned += await _store.MarkAbandonedAsync(
                    claim.Id, claim.ClaimToken, DateTime.UtcNow, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Pending upload {PendingUploadId} cleanup failed; it will retry.", claim.Id);
                await _store.ReleaseCleanupClaimAsync(claim.Id, claim.ClaimToken, CancellationToken.None);
            }
        }
        return cleaned;
    }
}
