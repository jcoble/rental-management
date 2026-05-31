using System.Text;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Extracts selectable (born-digital) text from a PDF using PdfPig. Returns <c>null</c> for
/// scanned/image-only PDFs or on any parse error, allowing callers to fall back to a vision path.
/// </summary>
public static class PdfTextExtractor
{
    /// <summary>
    /// Attempts to extract plain text from a PDF byte array.
    /// </summary>
    /// <param name="bytes">Raw PDF bytes.</param>
    /// <returns>
    /// A non-empty, trimmed string when the PDF contains selectable text with at least ~20
    /// non-whitespace characters; <c>null</c> when the PDF is scanned/image-only, the bytes are
    /// not a valid PDF, or any exception occurs during parsing.
    /// </returns>
    public static string? TryExtractText(byte[] bytes)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in doc.GetPages())
            {
                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append(text);
                    sb.AppendLine();
                }
            }

            var result = sb.ToString().Trim();

            // Require at least ~20 non-whitespace characters so scanned PDFs with stray glyphs
            // don't appear to have extractable text and fall through to the vision path.
            var nonWhitespaceCount = result.Count(c => !char.IsWhiteSpace(c));
            return nonWhitespaceCount >= 20 ? result : null;
        }
        catch
        {
            return null;
        }
    }
}
