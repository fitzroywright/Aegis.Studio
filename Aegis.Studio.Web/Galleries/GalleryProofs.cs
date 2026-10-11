using Aegis.Studio.Web.Media;
using SkiaSharp;

namespace Aegis.Studio.Web.Galleries;

public static class GalleryProofs
{
    public static bool IsProofVariant(string variant) => variant.Equals("preview", StringComparison.OrdinalIgnoreCase) || variant.Equals("thumbnail", StringComparison.OrdinalIgnoreCase);

    // Stroke lettering avoids a missing system font silently removing the watermark.
    public static byte[] Render(Stream source, bool thumbnail = false)
    {
        using var image = StudioImageProcessor.DecodeOriented(source);
        using var proof = StudioImageProcessor.ResizeMax(image, thumbnail ? 360 : 1200);
        using var canvas = new SKCanvas(proof);
        float scale = Math.Max(1, proof.Width / 60f);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 0.28f, IsAntialias = true };
        using var ink = new SKPaint { Color = new SKColor(255, 255, 255, 210), Style = SKPaintStyle.Stroke, StrokeWidth = 0.15f, IsAntialias = true };
        using var builder = new SKPathBuilder();
        // P R O O F, in a fourteen-unit line.
        builder.MoveTo(0,3); builder.LineTo(0,0); builder.LineTo(2,0); builder.LineTo(2,1.5f); builder.LineTo(0,1.5f);
        builder.MoveTo(3,3); builder.LineTo(3,0); builder.LineTo(5,0); builder.LineTo(5,1.5f); builder.LineTo(3,1.5f); builder.MoveTo(4,1.5f); builder.LineTo(5,3);
        foreach (float x in new[] { 6f, 9f }) { builder.MoveTo(x,0); builder.LineTo(x+2,0); builder.LineTo(x+2,3); builder.LineTo(x,3); builder.Close(); }
        builder.MoveTo(12,3); builder.LineTo(12,0); builder.LineTo(14,0); builder.MoveTo(12,1.5f); builder.LineTo(14,1.5f);
        using var letters = builder.Detach();
        for (int row = 1; row <= 3; row++)
        {
            canvas.Save();
            canvas.Translate((proof.Width - 14 * scale) / 2f, proof.Height * row / 4f - 1.5f * scale);
            canvas.Scale(scale);
            canvas.DrawPath(letters, shadow);
            canvas.DrawPath(letters, ink);
            canvas.Restore();
        }
        canvas.Flush();
        return StudioImageProcessor.EncodeJpeg(proof, 75);
    }
}
