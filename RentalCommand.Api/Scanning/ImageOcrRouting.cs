using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Scanning;

public enum ImageOcrRoutingMode
{
    Disabled,
    TextOnly,
    Hybrid,
}

public static class ImageOcrRouting
{
    private const int MinUsefulNonWhitespaceChars = 20;

    public static ImageOcrRoutingMode Resolve(AssistantConfig config)
    {
        var mode = config.ImageOcrMode?.Trim();
        if (!string.IsNullOrWhiteSpace(mode))
        {
            return mode.ToLowerInvariant() switch
            {
                "hybrid" or "image-plus-ocr" or "image_plus_ocr" => ImageOcrRoutingMode.Hybrid,
                "text-only" or "text_only" or "ocr-only" or "ocr_only" => ImageOcrRoutingMode.TextOnly,
                "disabled" or "off" or "false" or "none" => ImageOcrRoutingMode.Disabled,
                _ => config.UseImageOcr ? ImageOcrRoutingMode.TextOnly : ImageOcrRoutingMode.Disabled,
            };
        }

        return config.UseImageOcr ? ImageOcrRoutingMode.TextOnly : ImageOcrRoutingMode.Disabled;
    }

    public static bool IsUseful(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Count(c => !char.IsWhiteSpace(c)) >= MinUsefulNonWhitespaceChars;

    public static string BuildHybridHint(string ocrText) =>
        "Extract the fields from the attached document image. The image is authoritative. " +
        "OCR text from local image extraction is included below only as a secondary hint; " +
        "ignore it when it conflicts with the image, when it appears misread, or when it omits table columns. " +
        "If OCR flattens a receipt/invoice table into separate description, quantity, unit-price, and amount columns, " +
        "use the image plus printed column order to reconstruct rows without inventing absent cells.\n\n" +
        "OCR text from local image extraction:\n" +
        ocrText.Trim();
}
