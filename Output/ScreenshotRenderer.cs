using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snappo.Editor;
using Snappo.Editor.Annotations;

namespace Snappo.Output;

internal static class ScreenshotRenderer
{
    public static BitmapSource Render(
        BitmapSource fullScreenshot,
        Rect selection,
        IEnumerable<Annotation> annotations,
        RenderContext context)
    {
        double pixelsPerUnitX = fullScreenshot.PixelWidth / context.ScreenBounds.Width;
        double pixelsPerUnitY = fullScreenshot.PixelHeight / context.ScreenBounds.Height;

        int left = Math.Clamp((int)Math.Round(selection.X * pixelsPerUnitX), 0, fullScreenshot.PixelWidth - 1);
        int top = Math.Clamp((int)Math.Round(selection.Y * pixelsPerUnitY), 0, fullScreenshot.PixelHeight - 1);
        int width = Math.Clamp((int)Math.Round(selection.Width * pixelsPerUnitX), 1, fullScreenshot.PixelWidth - left);
        int height = Math.Clamp((int)Math.Round(selection.Height * pixelsPerUnitY), 1, fullScreenshot.PixelHeight - top);

        var area = new Int32Rect(left, top, width, height);

        IReadOnlyCollection<Annotation> annotationList = annotations as IReadOnlyCollection<Annotation> ?? new List<Annotation>(annotations);
        if (annotationList.Count == 0)
        {
            return CopyArea(fullScreenshot, area);   // nothing drawn on it: just copy the pixels, no rendering pass
        }

        var croppedScreenshot = new CroppedBitmap(fullScreenshot, area);

        var canvas = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(canvas, BitmapScalingMode.NearestNeighbor);

        using (DrawingContext drawing = canvas.RenderOpen())
        {
            drawing.DrawImage(croppedScreenshot, new Rect(0, 0, width, height));

            drawing.PushTransform(new ScaleTransform(pixelsPerUnitX, pixelsPerUnitY));
            drawing.PushTransform(new TranslateTransform(-selection.X, -selection.Y));

            foreach (Annotation annotation in annotationList)
            {
                annotation.Draw(drawing, context);
            }

            drawing.Pop();
            drawing.Pop();
        }

        var finalImage = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        finalImage.Render(canvas);
        finalImage.Freeze();   // frozen = safe to hand to a background thread for saving or copying
        return finalImage;
    }

    private static BitmapSource CopyArea(BitmapSource source, Int32Rect area)
    {
        int stride = area.Width * ((source.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * area.Height];
        source.CopyPixels(area, pixels, stride, 0);

        BitmapSource copy = BitmapSource.Create(area.Width, area.Height, 96, 96, source.Format, source.Palette, pixels, stride);
        copy.Freeze();
        return copy;
    }
}
