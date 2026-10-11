using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>Local-disk <see cref="IFileStorage"/>. Stores files under a Guid-prefixed relative
/// path inside <see cref="UploadSettings.BasePath"/>. The returned path is the relative key.</summary>
public sealed class DiskFileStorage : IFileStorage
{
    private readonly string _basePath;

    public DiskFileStorage(IOptions<UploadSettings> settings)
    {
        _basePath = string.IsNullOrWhiteSpace(settings.Value.BasePath) ? "./uploads" : settings.Value.BasePath;
        Directory.CreateDirectory(_basePath);
    }

    public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        var safe = SanitizeFileName(fileName);
        var key = $"{Guid.NewGuid():N}_{safe}";
        var full = Path.Combine(_basePath, key);
        await using (var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fs, ct);
        }
        return key;
    }

    public async Task UploadAtAsync(
        Stream content,
        string storagePath,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        var full = ResolveWithinBase(storagePath);
        var temporary = $"{full}.{Guid.NewGuid():N}.uploading";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await content.CopyToAsync(stream, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
    {
        var full = ResolveWithinBase(path);
        Stream s = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(s);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var full = ResolveWithinBase(path);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    /// <summary>Resolve a stored key to an absolute path, guaranteeing it stays inside
    /// <see cref="_basePath"/>. Stored keys are server-generated (Guid-prefixed, sanitized) and
    /// contain no separators; anything rooted, with separators, or with ".." is rejected so a
    /// tampered key cannot traverse out of the upload directory.</summary>
    private string ResolveWithinBase(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains('/') || path.Contains('\\'))
        {
            throw new UnauthorizedAccessException("Invalid storage path.");
        }

        var baseFull = Path.GetFullPath(_basePath);
        var full = Path.GetFullPath(Path.Combine(baseFull, path));
        if (!full.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid storage path.");

        return full;
    }

    public static string SanitizeFileName(string name)
    {
        var trimmed = Path.GetFileName(name); // strips any directory components / traversal
        foreach (var c in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(c, '_');
        trimmed = trimmed.Replace("..", "_");
        return string.IsNullOrWhiteSpace(trimmed) ? "file" : trimmed;
    }
}
