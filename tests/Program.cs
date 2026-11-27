using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snappo.Editor;
using Snappo.Editor.Annotations;
using Snappo.Hotkeys;
using Snappo.Output;
using Snappo.Settings;

namespace Snappo.Regression;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var source = TestPattern(17, 9);
        CheckPixelation(source);
        CheckShapeAfterCrop(source);
        CheckSavedImages(source);
        CheckHotkeys();
        var text = new TextAnnotation(new Point(), Colors.Red, 20);
        text.Append("e\u0301");
        text.RemoveLastCharacter();
        Require(!text.HasText, "Backspace left part of a combined character.");
        Console.WriteLine("Screenshot, annotation, file and hotkey regression checks passed.");
    }

    private static BitmapSource TestPattern(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            pixels[offset] = (byte)(12 * x);
            pixels[offset + 1] = (byte)(20 * y);
            pixels[offset + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static void CheckPixelation(BitmapSource source)
    {
        Rect bounds = new(0, 0, 17, 9);
        var context = new RenderContext(1, bounds, new(() => ImagePixelator.Pixelate(source, 8)), bounds);
        var blur = new BlurAnnotation(bounds.TopLeft);
        blur.UpdateDrag(bounds.BottomRight);
        byte[] pixels = Pixels(ScreenshotRenderer.Render(source, bounds, new[] { blur }, context));
        for (int y = 0; y < 9; y++)
        for (int x = 0; x < 17; x++)
        {
            int i = (y * 17 + x) * 4;
            int expectedBlue = x < 8 ? 42 : x < 16 ? 138 : 192;
            int expectedGreen = y < 8 ? 70 : 160;
            Require(pixels[i] == expectedBlue && pixels[i + 1] == expectedGreen,
                $"Blur block moved at ({x}, {y}): got {pixels[i]}, {pixels[i + 1]}.");
        }
    }

    private static void CheckShapeAfterCrop(BitmapSource source)
    {
        Rect full = new(0, 0, 17, 9);
        var context = new RenderContext(1, full, new(() => ImagePixelator.Pixelate(source, 8)), full);
        var box = new RectangleAnnotation(new Point(1, 1), Colors.Red, 1, filled: true) { IsConstrained = true };
        box.UpdateDrag(new Point(8, 5));
        box.CommitConstraint(full);
        var fullImage = ScreenshotRenderer.Render(source, full, new[] { box }, context);
        Rect crop = new(3, 2, 2, 6);
        var actual = ScreenshotRenderer.Render(source, crop, new[] { box }, context with { SelectionBounds = crop });
        var expected = new CroppedBitmap(fullImage, new Int32Rect(3, 2, 2, 6));
        Require(Pixels(actual).SequenceEqual(Pixels(expected)), "Cropping changed the geometry of a finished square.");

        var scaled = context with { ScreenBounds = new Rect(0, 0, 13.6, 7.2) };
        Rect logicalCrop = new(2.4, 1.6, 1.6, 4.8);
        var plain = ScreenshotRenderer.Render(source, logicalCrop, Array.Empty<Annotation>(), scaled);
        var plainExpected = new CroppedBitmap(source, new Int32Rect(3, 2, 2, 6));
        Require(Pixels(plain).SequenceEqual(Pixels(plainExpected)), "125% scale selected different source pixels.");
    }

    private static void CheckSavedImages(BitmapSource source)
    {
        string dir = Directory.CreateTempSubdirectory("snappo-test-").FullName;
        try
        {
            var settings = new AppSettings();
            foreach (string extension in new[] { ".png", ".JPEG" })
            {
                string file = Path.Combine(dir, "capture" + extension);
                File.WriteAllText(file, "old contents");
                ScreenshotSaver.SaveToPathAsync(source, file, settings).GetAwaiter().GetResult();
                using var stream = File.OpenRead(file);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Require(extension == ".png" ? decoder is PngBitmapDecoder : decoder is JpegBitmapDecoder, "Wrong file format.");
                Require(decoder.Frames[0].PixelWidth == source.PixelWidth && decoder.Frames[0].PixelHeight == source.PixelHeight,
                    "Saved image dimensions changed.");
            }

            string locked = Path.Combine(dir, "capture.png");
            byte[] original = File.ReadAllBytes(locked);
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    ScreenshotSaver.SaveToPathAsync(TestPattern(5, 5), locked, settings).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Replaced a locked destination.");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            Require(File.ReadAllBytes(locked).SequenceEqual(original), "Failed save modified the previous screenshot.");
            Require(Directory.GetFiles(dir).Length == 2, "Failed save left a temporary file.");
            settings.FilenameTemplate = "{date} {app} {seq}";
            var date = new DateTime(2026, 11, 26);
            string name = ScreenshotSaver.SuggestName(settings, "A:B", dir, date);
            Require(name == "2026-11-26 A_B 001", "Filename tokens or sanitization failed.");
            File.WriteAllText(Path.Combine(dir, name + ".png"), "");
            Require(ScreenshotSaver.SuggestName(settings, "A:B", dir, date).EndsWith("002"), "Sequence reused an existing filename.");
            foreach (var mode in new[] { SaveImageFormat.WebpLossless, SaveImageFormat.WebpLossy })
            {
                settings.ImageFormat = mode;
                string path = Path.Combine(dir, "sample.webp");
                ScreenshotSaver.SaveToPathAsync(source, path, settings).GetAwaiter().GetResult();
                using var decoded = SkiaSharp.SKBitmap.Decode(path);
                Require(decoded != null && decoded.Width == 17 && decoded.Height == 9, "Invalid WebP export.");
                if (mode == SaveImageFormat.WebpLossless)
                    Require(decoded!.GetPixel(16, 8) == new SkiaSharp.SKColor(0, 160, 192), "Lossless WebP changed pixel values.");
                string chunk = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 12, 4);
                Require(chunk == (mode == SaveImageFormat.WebpLossless ? "VP8L" : "VP8 "), "Wrong WebP encoding mode.");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void CheckHotkeys()
    {
        var settings = new AppSettings();
        foreach (HotkeyTrigger hotkey in new[] { HotkeyTrigger.ForKey(Key.Snapshot, ModifierKeys.None),
            HotkeyTrigger.ForKey(Key.D1, ModifierKeys.Control), HotkeyTrigger.ForMouse(SideMouseButton.Button4, ModifierKeys.Alt) })
        {
            settings.SetTrigger(HotkeyAction.Capture, hotkey);
            Require(settings.GetTrigger(HotkeyAction.Capture) == hotkey, $"Lost saved hotkey {hotkey}.");
        }
        foreach (string invalid in new[] { "-1", "9999", "Ctrl+None", "not a key" })
        {
            settings.Hotkeys["Capture"] = invalid;
            Require(settings.GetTrigger(HotkeyAction.Capture) == AppSettings.DefaultTriggerFor(HotkeyAction.Capture),
                $"Invalid hotkey {invalid} disabled capture.");
        }
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
