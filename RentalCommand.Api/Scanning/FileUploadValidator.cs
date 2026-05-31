using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Static validation helpers for scan file uploads. Checks MIME type whitelist, file size,
/// and rejects path-traversal names.
/// </summary>
public static class FileUploadValidator
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".jpg", ".jpeg", ".png", ".heic" };

    /// <summary>
    /// Validates a candidate upload against the configured limits.
    /// </summary>
    /// <returns>
    /// A tuple: <c>IsValid = true</c> when the file is acceptable; otherwise <c>IsValid = false</c>
    /// and <c>Error</c> contains a human-readable rejection reason.
    /// </returns>
    public static (bool IsValid, string? Error) ValidateScanUpload(
        string? fileName,
        string contentType,
        long sizeBytes,
        UploadSettings settings)
    {
        // 1. Non-empty file name required.
        if (string.IsNullOrWhiteSpace(fileName))
            return (false, "File name is required.");

        // 2. Reject path-traversal attempts.
        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return (false, "File name must not contain path separators or '..'.");

        // 3. Size limit.
        if (sizeBytes > settings.MaxFileSizeBytes)
            return (false, $"File size {sizeBytes:N0} bytes exceeds the {settings.MaxFileSizeBytes:N0}-byte limit.");

        // 4. MIME type or extension must be in the allow-list.
        var mimeAllowed = settings.AllowedMimeTypes is { Length: > 0 }
            && settings.AllowedMimeTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

        var ext = Path.GetExtension(fileName);
        var extAllowed = !string.IsNullOrEmpty(ext) && AllowedExtensions.Contains(ext);

        if (!mimeAllowed && !extAllowed)
            return (false, $"Content type '{contentType}' is not permitted for scan uploads.");

        return (true, null);
    }
}
