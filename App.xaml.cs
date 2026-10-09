using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snappo.Capture;
using Snappo.Editor;
using Snappo.Hotkeys;
using Snappo.Interop;
using Snappo.Overlay;
using Snappo.Settings;
using Snappo.Tray;

namespace Snappo;

public partial class App : Application
{
    private Mutex? singleInstanceMutex;
    private bool ownsSingleInstanceMutex;
    private AppSettings settings = new();
    private HotkeyManager? hotkeyManager;
    private TrayIcon? trayIcon;
    private SettingsWindow? openSettingsWindow;
    private bool isCapturePending;

    internal static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        singleInstanceMutex = new Mutex(true, "Snappo.SingleInstance", out ownsSingleInstanceMutex);
        if (!ownsSingleInstanceMutex)
        {
            Shutdown();     // already running in the tray
            return;
        }

        DispatcherUnhandledException += OnUnexpectedError;
        RenderModes.ChooseForProcess();

        settings = SettingsStore.Load();
        trayIcon = new TrayIcon(
            takeScreenshot: StartCapture,
            continueEditing: ContinueEditingLastScreenshot,
            openScreenshotsFolder: OpenScreenshotsFolder,
            openSettings: ShowSettings,
            exitApp: ExitApp);

        hotkeyManager = new HotkeyManager(Dispatcher);
        hotkeyManager.ActionTriggered += OnHotkeyTriggered;
        hotkeyManager.ShouldPassKeysThrough = () => CaptureSession.IsOpen;   // during a capture every key belongs to the overlay

        try
        {
            ApplyHotkeySettings();
        }
        catch (Win32Exception problem)
        {
            trayIcon.ShowError("Hotkeys unavailable", problem.Message);
        }

        Dispatcher.InvokeAsync(RunStartupWarmUp, DispatcherPriority.ApplicationIdle);
    }

    private void ExitApp()
    {
        IsExiting = true;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;   // Windows is logging off: let every window close
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        hotkeyManager?.Dispose();
        trayIcon?.Dispose();

        if (ownsSingleInstanceMutex)
        {
            singleInstanceMutex?.ReleaseMutex();
        }

        base.OnExit(e);
    }

    private void ApplyHotkeySettings()
    {
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            hotkeyManager!.SetTrigger(action, settings.GetTrigger(action));
        }
    }

    private void OnHotkeyTriggered(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Capture:
                StartCapture();
                break;
        }
    }

    private async void StartCapture()
    {
        if (isCapturePending || CaptureSession.IsOpen || trayIcon is null)
        {
            return;
        }

        isCapturePending = true;

        try
        {
            // No "taking a screenshot in N seconds" notification here: it would still be on screen and end up in the shot.
            int delaySeconds = settings.CaptureDelaySeconds;
            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }

            if (CaptureSession.IsOpen) return;   // "continue editing" may have opened one during the delay

            await new CaptureSession(settings, trayIcon).StartAsync();
        }
        catch (Exception problem)
        {
            trayIcon?.ShowError("Couldn't start the screenshot", problem.Message);
        }
        finally
        {
            isCapturePending = false;
        }
    }

    private void ContinueEditingLastScreenshot()
    {
        if (isCapturePending || CaptureSession.IsOpen || trayIcon is null || LastCaptureMemory.Value is not LastCapture previous)
        {
            return;
        }

        try
        {
            new CaptureSession(settings, trayIcon).StartWithPreviousCapture(previous);
        }
        catch (Exception problem)
        {
            trayIcon.ShowError("Couldn't reopen the screenshot", problem.Message);
        }
    }

    private void ShowSettings()
    {
        if (openSettingsWindow is not null)
        {
            openSettingsWindow.Activate();
            return;
        }

        openSettingsWindow = new SettingsWindow(settings, hotkeyManager!, OnSettingsSaved);
        openSettingsWindow.Closed += (_, _) => openSettingsWindow = null;
        openSettingsWindow.Show();
        openSettingsWindow.Activate();
    }

    private void OnSettingsSaved(AppSettings savedSettings)
    {
        settings = savedSettings;

        try
        {
            ApplyHotkeySettings();
        }
        catch (Win32Exception problem)
        {
            trayIcon?.ShowError("Hotkeys unavailable", problem.Message);
        }
    }

    private void OpenScreenshotsFolder()
    {
        try
        {
            string folder = settings.GetSaveFolderOrDefault();
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception problem)
        {
            trayIcon?.ShowError("Couldn't open the screenshots folder", problem.Message);
        }
    }

    private void RunStartupWarmUp()
    {
        if (CaptureSession.IsOpen || trayIcon is null)
        {
            return;
        }

        OverlayWindow? warmupOverlay = null;

        ScreenCapturer.WarmUp();   // the GDI screen-grab code is the slowest thing to run cold

        try
        {
            var warmupSession = new CaptureSession(settings, trayIcon);
            warmupOverlay = new OverlayWindow(CreateDummyMonitorShot(), warmupSession, isWarmUp: true)
            {
                ShowActivated = false,   // never steals focus or activates
            };

            warmupOverlay.Show();
            warmupOverlay.RunWarmUpRenderPass();
        }
        catch
        {
        }
        finally
        {
            warmupOverlay?.CloseForGood();
            MemoryTrimmer.TrimWhenIdle();
        }
    }

    private static MonitorShot CreateDummyMonitorShot()
    {
        const int size = 8;
        var pixels = new byte[size * size * 4];   // BGR32, left as zero; contents don't matter for a warm-up

        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr32, null, pixels, size * 4);
        bitmap.Freeze();

        return new MonitorShot(new Int32Rect(-32000, -32000, size, size), bitmap);
    }

    private void OnUnexpectedError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CaptureSession.AbortCurrentSession();   // never leave a dimmed screen behind
        trayIcon?.ShowError("Snappo hit a problem", e.Exception.Message);
        e.Handled = true;
    }
}
