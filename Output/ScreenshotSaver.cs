using System;
using System.IO;
using System.Windows.Media;
using System.Windows;
using SkiaSharp;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Snappo.Settings;

namespace Snappo.Output;

internal static class ScreenshotSaver
{
    public static string? PromptForSavePath(AppSettings settings, string sourceApp)
    {
        string folder = settings.GetSaveFolderOrDefault();
        Directory.CreateDirectory(folder);   // the dialog needs the folder to exist to default into it

        var dialog = new SaveFileDialog
        {
            Title = "Save Screenshot",
            InitialDirectory = folder,
            FileName = SuggestName(settings, sourceApp, folder, DateTime.Now),
            DefaultExt = settings.GetFileExtension(),
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg, *.jpeg)|*.jpg;*.jpeg|WebP image (*.webp)|*.webp",
            FilterIndex = Math.Min((int)settings.ImageFormat + 1, 3),
            AddExtension = true,
            OverwritePrompt = true,
        };

        dialog.FileOk += (_, e) =>
        {
            if (GetFormat(dialog.FileName) is not null) return;
            e.Cancel = true;
            System.Windows.MessageBox.Show("Use a .png, .jpg, .jpeg or .webp filename.", "Save Screenshot");
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    internal static string SuggestName(AppSettings settings, string app, string folder, DateTime now)
    {
        string template = string.IsNullOrWhiteSpace(settings.FilenameTemplate) ? AppSettings.DefaultTemplate : settings.FilenameTemplate;
        template = template.Replace("{date}", now.ToString("yyyy-MM-dd")).Replace("{time}", now.ToString("HH-mm-ss")).Replace("{app}", app);
        foreach (char c in Path.GetInvalidFileNameChars()) template = template.Replace(c, '_');
        template = template.Trim().TrimEnd('.');
        if (template.Length == 0) template = "Screenshot";
        if (template.Length > 160) template = template[..160];
        if (System.Text.RegularExpressions.Regex.IsMatch(template, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            template = "_" + template;
        for (int n = 1; ; n++)
        {
            string name = template.Contains("{seq}") ? template.Replace("{seq}", n.ToString("000")) : n == 1 ? template : $"{template} ({n})";
            if (!File.Exists(Path.Combine(folder, name + settings.GetFileExtension()))) return name;
        }
    }

    private static void WriteWebp(BitmapSource image, Stream output, bool lossy, int quality)
    {
        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        using var bitmap = new SKBitmap(pixels.PixelWidth, pixels.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        pixels.CopyPixels(Int32Rect.Empty, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        using var pixmap = bitmap.PeekPixels();
        var options = new SKWebpEncoderOptions(lossy ? SKWebpEncoderCompression.Lossy : SKWebpEncoderCompression.Lossless, quality);
        using var stream = new SKManagedWStream(output);
        if (!pixmap.Encode(stream, options)) throw new IOException("WebP encoding failed.");
    }

    private static SaveImageFormat? GetFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webp" => SaveImageFormat.WebpLossless,
        ".png" => SaveImageFormat.Png,
        ".jpg" or ".jpeg" => SaveImageFormat.Jpeg,
        _ => null,
    };

    public static Task SaveToPathAsync(BitmapSource frozenImage, string filePath, AppSettings settings)
    {
        int jpegQuality = settings.GetJpegQualityInRange();
        return Task.Run(() =>
        {
            BitmapEncoder? encoder = GetFormat(filePath) switch
            {
                SaveImageFormat.Png => new PngBitmapEncoder(),
                SaveImageFormat.WebpLossless => null,
                SaveImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = jpegQuality },
                _ => throw new ArgumentException("Use a .png, .jpg, .jpeg or .webp filename.", nameof(filePath)),
            };
            encoder?.Frames.Add(BitmapFrame.Create(frozenImage));

            string destination = Path.GetFullPath(filePath);
            string temporary = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetRandomFileName());
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                using (stream)
                {
                    if (encoder is null) WriteWebp(frozenImage, stream, settings.ImageFormat == SaveImageFormat.WebpLossy, jpegQuality);
                    else encoder.Save(stream);
                }
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        });
    }
}
