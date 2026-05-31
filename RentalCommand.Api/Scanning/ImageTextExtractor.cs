using System.Diagnostics;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Attempts to extract plain text from an image file using the <c>tesseract</c> CLI.
/// Returns <c>null</c> on any failure (binary missing, non-zero exit, exception, or too little
/// text), allowing callers to fall back to a vision path. Never throws.
/// </summary>
public static class ImageTextExtractor
{
    private const int MinNonWhitespaceChars = 20;
    private const int TimeoutMs = 30_000;

    /// <summary>
    /// Attempts to extract text from raw image bytes via the <c>tesseract</c> CLI.
    /// </summary>
    /// <param name="bytes">Raw image bytes.</param>
    /// <param name="contentType">MIME type (e.g. "image/jpeg", "image/png"); used to pick the temp-file extension.</param>
    /// <returns>
    /// A non-empty trimmed string when Tesseract extracts at least ~20 non-whitespace characters;
    /// <c>null</c> when the binary is missing, the process exits non-zero, an exception occurs,
    /// or the extracted text is too short to be useful.
    /// </returns>
    public static string? TryExtractText(byte[] bytes, string contentType)
    {
        var extension = MimeToExtension(contentType);
        var tmpFile = Path.Combine(Path.GetTempPath(), $"rc_ocr_{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllBytes(tmpFile, bytes);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "tesseract",
                    Arguments = $"\"{tmpFile}\" stdout -l eng",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeoutMs);

            if (process.ExitCode != 0)
                return null;

            var text = output.Trim();
            var nonWhitespaceCount = text.Count(c => !char.IsWhiteSpace(c));
            return nonWhitespaceCount >= MinNonWhitespaceChars ? text : null;
        }
        catch
        {
            // Binary missing, process error, or I/O failure — fall back to vision path.
            return null;
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { /* best-effort cleanup */ }
        }
    }

    private static string MimeToExtension(string contentType) =>
        contentType.ToLowerInvariant() switch
        {
            "image/png"  => ".png",
            "image/gif"  => ".gif",
            "image/tiff" => ".tiff",
            "image/webp" => ".webp",
            "image/heic" => ".heic",
            _            => ".png"   // default to .png; covers image/jpeg and unknowns
        };
}
