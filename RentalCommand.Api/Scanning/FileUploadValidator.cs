using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Static validation helpers for scan file uploads. Checks MIME type whitelist, file size,
/// path-traversal names, and (optionally) magic-byte content sniffing.
/// </summary>
public static class FileUploadValidator
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".jpg", ".jpeg", ".png", ".heic" };

    /// <summary>
    /// Inspects the leading bytes of a file to verify it is one of the allowed types
    /// (PDF, JPEG, PNG, or HEIC/HEIF) regardless of the caller-supplied content-type or
    /// extension. Only the first 12 bytes are needed; fewer bytes cause the method to
    /// return <c>false</c> for every check except when the buffer is long enough.
    /// </summary>
    /// <param name="header">
    /// The first N bytes of the file (at least 12 bytes for reliable detection).
    /// An empty span always returns <c>false</c>.
    /// </param>
    public static bool LooksLikeAllowedType(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
            return false;

        // PDF: starts with "%PDF" (25 50 44 46)
        if (header.Length >= 4
            && header[0] == 0x25 && header[1] == 0x50
            && header[2] == 0x44 && header[3] == 0x46)
            return true;

        // JPEG: starts with FF D8 FF
        if (header.Length >= 3
            && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return true;

        // PNG: starts with 89 50 4E 47 0D 0A 1A 0A
        if (header.Length >= 8
            && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            return true;

        // HEIC/HEIF: ISO Base Media File Format box.
        // Bytes 4..7 must be "ftyp" (66 74 79 70); bytes 8..11 are the brand.
        // Recognised brands: heic, heix, hevc, heim, heis, hevm, hevs, mif1, msf1.
        if (header.Length >= 12
            && header[4] == 0x66 && header[5] == 0x74 && header[6] == 0x79 && header[7] == 0x70)
        {
            var brand = System.Text.Encoding.ASCII.GetString(header.Slice(8, 4));
            if (brand is "heic" or "heix" or "hevc" or "heim" or "heis" or "hevm" or "hevs"
                      or "mif1" or "msf1")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Validates a candidate upload against the configured limits.
    /// </summary>
    /// <param name="fileName">Original file name from the upload.</param>
    /// <param name="contentType">MIME type reported by the client.</param>
    /// <param name="sizeBytes">Full file size in bytes.</param>
    /// <param name="settings">Configured upload limits.</param>
    /// <param name="header">
    /// Optional: the first up-to-16 bytes of the file content. When provided (and non-empty),
    /// the magic bytes must match a known allowed type in addition to the MIME/extension check
    /// (defense in depth). Pass <c>null</c> to skip the sniff (legacy callers).
    /// </param>
    /// <returns>
    /// A tuple: <c>IsValid = true</c> when the file is acceptable; otherwise <c>IsValid = false</c>
    /// and <c>Error</c> contains a human-readable rejection reason.
    /// </returns>
    public static (bool IsValid, string? Error) ValidateScanUpload(
        string? fileName,
        string contentType,
        long sizeBytes,
        UploadSettings settings,
        byte[]? header = null)
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

        // 5. Magic-byte sniff (defense in depth): when the caller supplies header bytes,
        //    the file's actual content must match a known allowed type.
        if (header is { Length: > 0 } && !LooksLikeAllowedType(header))
            return (false, "File content does not match an allowed type (PDF, JPEG, PNG, or HEIC).");

        return (true, null);
    }
}
