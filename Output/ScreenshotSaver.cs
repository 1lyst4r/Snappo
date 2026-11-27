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
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg, *.jpeg)|*.jpg;*.jpeg",
            FilterIndex = settings.ImageFormat == SaveImageFormat.Jpeg ? 2 : 1,
            AddExtension = true,
            OverwritePrompt = true,
        };

        dialog.FileOk += (_, e) =>
        {
            if (GetFormat(dialog.FileName) is not null) return;
            e.Cancel = true;
            System.Windows.MessageBox.Show("Use a .png, .jpg or .jpeg filename.", "Save Screenshot");
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static SaveImageFormat? GetFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => SaveImageFormat.Png,
        ".jpg" or ".jpeg" => SaveImageFormat.Jpeg,
        _ => null,
    };

    public static Task SaveToPathAsync(BitmapSource frozenImage, string filePath, AppSettings settings)
    {
        int jpegQuality = settings.GetJpegQualityInRange();
        return Task.Run(() =>
        {
            BitmapEncoder encoder = GetFormat(filePath) switch
            {
                SaveImageFormat.Png => new PngBitmapEncoder(),
                SaveImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = jpegQuality },
                _ => throw new ArgumentException("Use a .png, .jpg or .jpeg filename.", nameof(filePath)),
            };
            encoder.Frames.Add(BitmapFrame.Create(frozenImage));

            string destination = Path.GetFullPath(filePath);
            string temporary = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetRandomFileName());
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                using (stream) encoder.Save(stream);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        });
    }
}
