using SkiaSharp;

namespace Aegis.Studio.Web.Media;

internal static class StudioImageProcessor
{
    public const long MaxPixels = 60_000_000;

    public static SKBitmap DecodeOriented(Stream source, long maxPixels = MaxPixels)
    {
        using var codec = SKCodec.Create(source) ?? throw new InvalidOperationException("Unsupported image format.");
        SKImageInfo info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > maxPixels)
            throw new InvalidOperationException("Image exceeds the supported pixel limit.");

        using SKBitmap decoded = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("Unable to decode image.");
        return ApplyOrientation(decoded, codec.EncodedOrigin);
    }

    public static SKBitmap ResizeMax(SKBitmap source, int edge)
    {
        double scale = Math.Min(1d, Math.Min((double)edge / source.Width, (double)edge / source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return Draw(source, width, height, new SKRect(0, 0, width, height));
    }

    public static SKBitmap ResizeCrop(SKBitmap source, int width, int height)
    {
        double scale = Math.Max((double)width / source.Width, (double)height / source.Height);
        float scaledWidth = (float)(source.Width * scale);
        float scaledHeight = (float)(source.Height * scale);
        float left = (width - scaledWidth) / 2f;
        float top = (height - scaledHeight) / 2f;
        return Draw(source, width, height, new SKRect(left, top, left + scaledWidth, top + scaledHeight));
    }

    public static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new InvalidOperationException("Unable to encode JPEG derivative.");
        return data.ToArray();
    }

    private static SKBitmap Draw(SKBitmap source, int width, int height, SKRect destination)
    {
        var output = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(output);
        canvas.Clear(SKColors.Black);
        canvas.DrawBitmap(source, destination);
        canvas.Flush();
        return output;
    }

    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int width = swap ? source.Height : source.Width;
        int height = swap ? source.Width : source.Height;
        var output = new SKBitmap(width, height, source.ColorType, source.AlphaType);

        SKMatrix matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, source.Width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, source.Width, 0, -1, source.Height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, source.Height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, source.Width, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, source.Width, -1, 0, source.Height, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, source.Height, 0, 0, 1),
            _ => SKMatrix.CreateIdentity()
        };

        using var canvas = new SKCanvas(output);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return output;
    }
}
