using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Snappo.Settings;

namespace Snappo.Output;

internal static class ScreenshotSaver
{
    public static string? PromptForSavePath(AppSettings settings)
    {
        string folder = settings.GetSaveFolderOrDefault();
        Directory.CreateDirectory(folder);   // the dialog needs the folder to exist to default into it

        var dialog = new SaveFileDialog
        {
            Title = "Save Screenshot",
            InitialDirectory = folder,
            FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HH-mm-ss}",
            DefaultExt = settings.GetFileExtension(),
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FilterIndex = settings.ImageFormat == SaveImageFormat.Jpeg ? 2 : 1,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static Task SaveToPathAsync(BitmapSource frozenImage, string filePath, AppSettings settings)
    {
        int jpegQuality = settings.GetJpegQualityInRange();
        bool saveAsJpeg = string.Equals(Path.GetExtension(filePath), ".jpg", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetExtension(filePath), ".jpeg", StringComparison.OrdinalIgnoreCase);

        return Task.Run(() =>
        {
            BitmapEncoder encoder = saveAsJpeg
                ? new JpegBitmapEncoder { QualityLevel = jpegQuality }
                : new PngBitmapEncoder();

            encoder.Frames.Add(BitmapFrame.Create(frozenImage));

            using FileStream fileStream = File.Create(filePath);
            encoder.Save(fileStream);
        });
    }
}
