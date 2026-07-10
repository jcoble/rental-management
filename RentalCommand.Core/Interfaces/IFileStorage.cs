namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Abstraction over the file/blob store (local disk in dev, object storage later).
/// Phase 0 defines the contract only; the upload pipeline implementation is Phase 1.
/// </summary>
public interface IFileStorage : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    /// <summary>Persist a file and return the storage path/key that can later be passed to <see cref="DownloadAsync"/>.</summary>
    Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default);

    /// <summary>Open a previously stored file for reading.</summary>
    Task<Stream> DownloadAsync(string path, CancellationToken ct = default);

    /// <summary>Remove a stored file.</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);
}
