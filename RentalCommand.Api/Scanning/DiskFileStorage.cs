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

    public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
    {
        var full = Path.Combine(_basePath, path);
        Stream s = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(s);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var full = Path.Combine(_basePath, path);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
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
