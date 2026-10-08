using SkiaSharp;

namespace Aegis.Studio.Web.Media;

internal sealed class StudioUnsupportedImageException(string message) : InvalidOperationException(message);

internal static class StudioImageProcessor
{
    public const long MaxPixels = 60_000_000;

    public static SKBitmap DecodeOriented(Stream source, long maxPixels = MaxPixels)
    {
        using var codec = SKCodec.Create(source) ?? throw new StudioUnsupportedImageException("Unsupported image format.");
        SKImageInfo info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
            throw new StudioUnsupportedImageException("Unsupported image dimensions.");
        if ((long)info.Width * info.Height > maxPixels)
            throw new StudioUnsupportedImageException("Image exceeds the supported pixel limit.");

        using SKBitmap decoded = SKBitmap.Decode(codec) ?? throw new StudioUnsupportedImageException("Unable to decode image.");
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
        canvas.DrawBitmap(source, destination, new SKSamplingOptions(SKFilterMode.Linear));
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

        using var canvas = new SKCanvas(output);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(width, height);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, height);
                canvas.RotateDegrees(-90);
                break;
        }
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
        canvas.Flush();
        return output;
    }
}
