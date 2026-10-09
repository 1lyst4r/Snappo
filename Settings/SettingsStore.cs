using System;
using System.IO;
using System.Text.Json;

namespace Snappo.Settings;

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snappo", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                settings.Hotkeys ??= new();   // "Hotkeys": null in a hand-edited file
                settings.SaveFolder ??= AppSettings.DefaultSaveFolder;
                return settings;
            }
        }
        catch (Exception)
        {
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);

        // Write to a temp file first so a crash or full disk mid-write can't leave a half-written settings file.
        string tempPath = SettingsFilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tempPath, SettingsFilePath, overwrite: true);
    }
}
