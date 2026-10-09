using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Snappo.Hotkeys;
using Snappo.Interop;

namespace Snappo.Settings;

public partial class SettingsWindow : Window
{
    private const string DefaultHintText =
        "Click the box, then press ANY key (or a mouse side button). Click anywhere else to cancel.";

    private const int JpegFormatIndex = 1;

    private static readonly int[] CaptureDelayOptions = { 0, 3, 5, 10 };

    private readonly AppSettings currentSettings;
    private readonly HotkeyManager hotkeyManager;
    private readonly Action<AppSettings> onSettingsSaved;

    private HotkeyTrigger chosenCaptureTrigger;
    private bool isRecordingHotkey;

    private Key? modifierBeingHeld;

    internal SettingsWindow(AppSettings settings, HotkeyManager hotkeyManager, Action<AppSettings> onSettingsSaved)
    {
        InitializeComponent();

        currentSettings = settings;
        this.hotkeyManager = hotkeyManager;
        this.onSettingsSaved = onSettingsSaved;

        // General
        NotificationCheckBox.IsChecked = settings.ShowSaveNotification;
        KeepSelectionCheckBox.IsChecked = settings.KeepSelectedAreaPosition;
        CaptureCursorCheckBox.IsChecked = settings.CaptureCursor;
        SaveFolderTextBox.Text = settings.GetSaveFolderOrDefault();
        CaptureDelayComboBox.SelectedIndex = Math.Max(0, Array.IndexOf(CaptureDelayOptions, settings.CaptureDelaySeconds));

        try
        {
            StartupCheckBox.IsChecked = StartupRegistration.IsEnabled();
        }
        catch (Exception)
        {
            StartupCheckBox.IsChecked = false;   // never let a registry read failure stop the window from opening
        }

        // Hotkeys
        chosenCaptureTrigger = settings.GetTrigger(HotkeyAction.Capture);
        ShowChosenHotkey();
        SetHint(DefaultHintText, Brushes.Gray);

        // Format
        FormatComboBox.SelectedIndex = settings.ImageFormat == SaveImageFormat.Jpeg ? JpegFormatIndex : 0;
        QualitySlider.Value = settings.GetJpegQualityInRange();
        UpdateQualityControls();
        FormatComboBox.SelectionChanged += (_, _) => UpdateQualityControls();
        QualitySlider.ValueChanged += (_, _) => UpdateQualityControls();

        SourceInitialized += (_, _) => RenderModes.UseSoftware(this);
        Closed += (_, _) => hotkeyManager.IsPaused = false;
    }

    // Format tab
    private void UpdateQualityControls()
    {
        bool isJpeg = FormatComboBox.SelectedIndex == JpegFormatIndex;

        QualitySlider.IsEnabled = isJpeg;
        QualityTitleText.IsEnabled = isJpeg;
        QualityValueText.Text = isJpeg ? ((int)QualitySlider.Value).ToString() : "-";
        QualityHintText.Text = isJpeg
            ? "Higher quality means a sharper picture and a bigger file."
            : "PNG is lossless, so it has no quality setting.";
    }

    // Hotkeys tab
    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        isRecordingHotkey = true;
        modifierBeingHeld = null;
        hotkeyManager.IsPaused = true;     // otherwise pressing the current hotkey would take a screenshot
        HotkeyButton.Content = "Press any key or mouse side button...";
        SetHint(DefaultHintText, Brushes.Gray);
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!isRecordingHotkey) return;

        e.Handled = true;

        Key pressedKey = NormalizeKey(e);
        if (pressedKey is Key.None or Key.DeadCharProcessed)
        {
            return;
        }

        if (HotkeyTrigger.IsModifierKey(pressedKey))
        {
            modifierBeingHeld = pressedKey;    // wait and see: combination, or a lone modifier hotkey?
            return;
        }

        modifierBeingHeld = null;
        TryAcceptTrigger(HotkeyTrigger.ForKey(pressedKey, Keyboard.Modifiers));
    }

    private void HotkeyButton_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!isRecordingHotkey) return;

        e.Handled = true;

        Key releasedKey = NormalizeKey(e);

        // Windows never sends a key-down for PrintScreen, only the key-up.
        if (releasedKey == Key.Snapshot)
        {
            modifierBeingHeld = null;
            TryAcceptTrigger(HotkeyTrigger.ForKey(Key.Snapshot, Keyboard.Modifiers));
            return;
        }

        if (modifierBeingHeld == releasedKey)
        {
            modifierBeingHeld = null;
            TryAcceptTrigger(HotkeyTrigger.ForKey(releasedKey, ModifierKeys.None));
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!isRecordingHotkey) return;

        SideMouseButton sideButton = e.ChangedButton switch
        {
            MouseButton.XButton1 => SideMouseButton.Button4,
            MouseButton.XButton2 => SideMouseButton.Button5,
            _ => SideMouseButton.None,
        };

        if (sideButton == SideMouseButton.None) return;

        e.Handled = true;
        TryAcceptTrigger(HotkeyTrigger.ForMouse(sideButton, Keyboard.Modifiers));
    }

    private void HotkeyButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (isRecordingHotkey) StopRecording();
    }

    private static Key NormalizeKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key,
    };

    private void TryAcceptTrigger(HotkeyTrigger trigger)
    {
        chosenCaptureTrigger = trigger;
        StopRecording();

        if (trigger.MayBreakNormalUse)
        {
            SetHint($"Heads up: while Snappo is running, \"{trigger}\" will take a screenshot instead of doing its normal job.",
                    Brushes.DarkOrange);
        }
        else
        {
            SetHint(DefaultHintText, Brushes.Gray);
        }
    }

    private void StopRecording()
    {
        isRecordingHotkey = false;
        modifierBeingHeld = null;
        hotkeyManager.IsPaused = false;
        ShowChosenHotkey();
    }

    private void ShowChosenHotkey()
    {
        HotkeyButton.Content = chosenCaptureTrigger.ToString();
    }

    private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        chosenCaptureTrigger = AppSettings.DefaultTriggerFor(HotkeyAction.Capture);
        SetHint(DefaultHintText, Brushes.Gray);
        ShowChosenHotkey();
    }

    private void SetHint(string text, Brush color)
    {
        HotkeyHintText.Text = text;
        HotkeyHintText.Foreground = color;
    }

    // General tab: save folder
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var folderDialog = new OpenFolderDialog { Title = "Choose where screenshots are saved" };

        if (Directory.Exists(SaveFolderTextBox.Text))
        {
            folderDialog.InitialDirectory = SaveFolderTextBox.Text;
        }

        if (folderDialog.ShowDialog(this) == true)
        {
            SaveFolderTextBox.Text = folderDialog.FolderName;
        }
    }

    // Save
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string chosenFolder;

        try
        {
            chosenFolder = Path.GetFullPath(SaveFolderTextBox.Text.Trim());   // throws for empty or invalid paths
        }
        catch (Exception)
        {
            ShowWarning("That save folder isn't a valid path.");
            return;
        }

        AppSettings updatedSettings = currentSettings.Clone();
        updatedSettings.SetTrigger(HotkeyAction.Capture, chosenCaptureTrigger);
        updatedSettings.SaveFolder = chosenFolder;
        updatedSettings.ShowSaveNotification = NotificationCheckBox.IsChecked == true;
        updatedSettings.KeepSelectedAreaPosition = KeepSelectionCheckBox.IsChecked == true;
        updatedSettings.CaptureCursor = CaptureCursorCheckBox.IsChecked == true;
        updatedSettings.CaptureDelaySeconds = CaptureDelayOptions[Math.Max(0, CaptureDelayComboBox.SelectedIndex)];
        updatedSettings.ImageFormat = FormatComboBox.SelectedIndex == JpegFormatIndex ? SaveImageFormat.Jpeg : SaveImageFormat.Png;
        updatedSettings.JpegQuality = (int)QualitySlider.Value;

        try
        {
            StartupRegistration.SetEnabled(StartupCheckBox.IsChecked == true);
        }
        catch (Exception problem)
        {
            ShowWarning("Couldn't update the Windows startup setting: " + problem.Message);
            return;
        }

        try
        {
            SettingsStore.Save(updatedSettings);
        }
        catch (Exception problem)
        {
            ShowWarning("Couldn't save your settings: " + problem.Message);
            return;
        }

        onSettingsSaved(updatedSettings);
        Close();
    }

    private void ResetToDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();

        NotificationCheckBox.IsChecked = defaults.ShowSaveNotification;
        KeepSelectionCheckBox.IsChecked = defaults.KeepSelectedAreaPosition;
        CaptureCursorCheckBox.IsChecked = defaults.CaptureCursor;
        StartupCheckBox.IsChecked = false;
        SaveFolderTextBox.Text = defaults.GetSaveFolderOrDefault();
        CaptureDelayComboBox.SelectedIndex = 0;

        chosenCaptureTrigger = AppSettings.DefaultTriggerFor(HotkeyAction.Capture);
        SetHint(DefaultHintText, Brushes.Gray);
        ShowChosenHotkey();

        FormatComboBox.SelectedIndex = defaults.ImageFormat == SaveImageFormat.Jpeg ? JpegFormatIndex : 0;
        QualitySlider.Value = defaults.GetJpegQualityInRange();
    }

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, "Snappo", MessageBoxButton.OK, MessageBoxImage.Warning);
}
