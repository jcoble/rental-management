using SkiaSharp;

namespace RentalCommand.Api.Imaging;

/// <summary>
/// On-the-fly JPEG thumbnail generator backed by SkiaSharp.
/// All methods are side-effect-free and never throw; callers receive <c>null</c> on any failure.
/// </summary>
public static class ThumbnailResizer
{
    /// <summary>
    /// Decode <paramref name="src"/>, scale so the longest side is ≤ <paramref name="maxDim"/> (aspect
    /// preserved), and encode as JPEG at the given <paramref name="quality"/>.
    /// Returns <c>null</c> if the input is not a decodeable image or any step fails.
    /// </summary>
    public static byte[]? ResizeToJpeg(byte[] src, int maxDim = 500, int quality = 80)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(src);
            if (bitmap is null)
                return null;

            int srcW = bitmap.Width;
            int srcH = bitmap.Height;
            if (srcW <= 0 || srcH <= 0)
                return null;

            // Scale so that max(width, height) ≤ maxDim, preserving aspect ratio.
            int dstW, dstH;
            if (srcW >= srcH && srcW > maxDim)
            {
                dstW = maxDim;
                dstH = (int)Math.Round((double)srcH * maxDim / srcW);
            }
            else if (srcH > srcW && srcH > maxDim)
            {
                dstH = maxDim;
                dstW = (int)Math.Round((double)srcW * maxDim / srcH);
            }
            else
            {
                // Already within maxDim — still re-encode as JPEG at the requested quality.
                dstW = srcW;
                dstH = srcH;
            }

            dstW = Math.Max(1, dstW);
            dstH = Math.Max(1, dstH);

            using var scaled = bitmap.Resize(new SKImageInfo(dstW, dstH), SKSamplingOptions.Default);
            if (scaled is null)
                return null;

            using var image = SKImage.FromBitmap(scaled);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (encoded is null)
                return null;

            return encoded.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
