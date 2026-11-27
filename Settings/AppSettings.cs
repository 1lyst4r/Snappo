using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Snappo.Hotkeys;

namespace Snappo.Settings;

internal enum SaveImageFormat
{
    Png,
    Jpeg,
}

internal sealed class AppSettings
{
    // General
    public bool ShowSaveNotification { get; set; } = true;

    public bool KeepSelectedAreaPosition { get; set; }

    public bool CaptureCursor { get; set; }

    public string SaveFolder { get; set; } = DefaultSaveFolder;

    public int CaptureDelaySeconds { get; set; }
    internal static readonly int[] CaptureDelayOptions = { 0, 3, 5, 10 };

    // Hotkeys
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    // Format
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SaveImageFormat ImageFormat { get; set; } = SaveImageFormat.Png;

    public int JpegQuality { get; set; } = 90;

    // Helpers
    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snappo");

    public static HotkeyTrigger DefaultTriggerFor(HotkeyAction action) => action switch
    {
        HotkeyAction.Capture => HotkeyTrigger.ForKey(Key.Snapshot, ModifierKeys.None),   // PrintScreen
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No default trigger defined."),
    };

    public HotkeyTrigger GetTrigger(HotkeyAction action)
    {
        if (Hotkeys.TryGetValue(action.ToString(), out string? savedText)
            && HotkeyTrigger.TryParse(savedText, out HotkeyTrigger? savedTrigger))
        {
            return savedTrigger;
        }

        return DefaultTriggerFor(action);
    }

    public void SetTrigger(HotkeyAction action, HotkeyTrigger trigger)
    {
        Hotkeys[action.ToString()] = trigger.ToString();
    }

    public string GetSaveFolderOrDefault() =>
        string.IsNullOrWhiteSpace(SaveFolder) ? DefaultSaveFolder : SaveFolder;

    public int GetJpegQualityInRange() => Math.Clamp(JpegQuality, 1, 100);

    public string GetFileExtension() => ImageFormat == SaveImageFormat.Jpeg ? ".jpg" : ".png";

    public AppSettings Clone() => new()
    {
        ShowSaveNotification = ShowSaveNotification,
        KeepSelectedAreaPosition = KeepSelectedAreaPosition,
        CaptureCursor = CaptureCursor,
        SaveFolder = SaveFolder,
        CaptureDelaySeconds = CaptureDelaySeconds,
        Hotkeys = new Dictionary<string, string>(Hotkeys),
        ImageFormat = ImageFormat,
        JpegQuality = JpegQuality,
    };
}
