using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Snappo.Capture;
using Snappo.Editor;
using Snappo.Interop;
using Snappo.Output;
using Snappo.Settings;
using Snappo.Tray;

namespace Snappo.Overlay;

internal enum FinishAction
{
    CopyToClipboard,
    SaveToFile,
}

internal sealed class CaptureSession
{
    private static volatile CaptureSession? currentSession;
    private static volatile bool choosingSavePath;

    // Pooled overlay windows
    private static readonly Dictionary<Int32Rect, OverlayWindow> pooledOverlays = new();

    private readonly List<OverlayWindow> overlays = new();
    private readonly AppSettings settings;
    private readonly IUserNotifier notifier;

    public CaptureSession(AppSettings settings, IUserNotifier notifier)
    {
        this.settings = settings;
        this.notifier = notifier;
    }

    public AppSettings Settings => settings;

    public static bool IsOpen => currentSession is not null || choosingSavePath;

    public static void AbortCurrentSession() => currentSession?.CloseAllOverlays();

    public async Task StartAsync()
    {
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        currentSession = this;

        try
        {
            var (shots, sourceApp) = await Task.Run(() =>
            {
                var images = ScreenCapturer.CaptureAllMonitors(settings.CaptureCursor);
                return (images, GetSourceApp(pid));
            });
            if (currentSession != this || App.IsExiting) return;

            RetireStalePooledOverlays(shots);

            foreach (MonitorShot shot in shots)
            {
                overlays.Add(RentOverlay(shot with { SourceApp = sourceApp }, shot.Bounds));
            }

            foreach (OverlayWindow overlay in overlays)
            {
                overlay.ShowForCapture();
            }

            OverlayWindow? selected = overlays.FirstOrDefault(overlay => overlay.RestoreRememberedSelection());
            (selected ?? FindOverlayUnderCursor())?.BringToFront();
        }
        catch
        {
            CloseAllOverlays();
            throw;
        }
    }

    private static string GetSourceApp(uint pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById((int)pid); return process.ProcessName; }
        catch (ArgumentException) { return "Desktop"; }
        catch (System.ComponentModel.Win32Exception) { return "Desktop"; }
    }

    public void PrepareOverlays()
    {
        foreach (Int32Rect bounds in ScreenCapturer.GetMonitorBounds())
        {
            if (IsOpen || pooledOverlays.ContainsKey(bounds)) continue;
            var overlay = new OverlayWindow(new MonitorShot(bounds, OverlayWindow.PlaceholderBitmap), this);
            try
            {
                new System.Windows.Interop.WindowInteropHelper(overlay).EnsureHandle();
                pooledOverlays.Add(bounds, overlay);
            }
            catch
            {
                overlay.CloseForGood();
                throw;
            }
        }
    }

    // Pooling

    private OverlayWindow RentOverlay(MonitorShot shot, Int32Rect displayBounds)
    {
        if (pooledOverlays.Remove(displayBounds, out OverlayWindow? pooled))
        {
            pooled.RearmForCapture(shot, this);
            return pooled;
        }

        return new OverlayWindow(shot, this, displayBounds);
    }

    private static void RetireStalePooledOverlays(IReadOnlyList<MonitorShot> currentShots)
    {
        var currentBounds = currentShots.Select(shot => shot.Bounds).ToHashSet();

        foreach (Int32Rect bounds in pooledOverlays.Keys.Where(bounds => !currentBounds.Contains(bounds)).ToList())
        {
            pooledOverlays[bounds].CloseForGood();
            pooledOverlays.Remove(bounds);
        }
    }

    public void StartWithPreviousCapture(LastCapture previous)
    {
        currentSession = this;

        try
        {
            List<Int32Rect> displays = ScreenCapturer.GetMonitorBounds();
            if (displays.Count == 0) throw new InvalidOperationException("Windows didn't report an available display.");

            Int32Rect display = previous.MonitorShot.Bounds;
            if (!displays.Contains(display))
            {
                NativeMethods.GetCursorPos(out NativeMethods.NativePoint cursor);
                display = displays.FirstOrDefault(bounds => ContainsPoint(bounds, cursor.X, cursor.Y), displays[0]);
            }

            OverlayWindow overlay = RentOverlay(previous.MonitorShot, display);
            overlays.Add(overlay);
            overlay.ShowForCapture(previous.EditorSize);
            overlay.RestoreAnnotatedSelection(previous.Selection, previous.Annotations);

            overlay.BringToFront();
        }
        catch
        {
            CloseAllOverlays();
            throw;
        }
    }

    public void NotifySelectionStarting(OverlayWindow starter)
    {
        foreach (OverlayWindow overlay in overlays)
        {
            if (overlay != starter) overlay.ResetSelection();
        }
    }

    public void Cancel() => CloseAllOverlays();

    public void Finish(BitmapSource finalImage, FinishAction action, string sourceApp, string? report = null)
    {
        CloseAllOverlays();

        if (action == FinishAction.CopyToClipboard)
        {
            _ = CopyToClipboardAsync(finalImage, report);
        }
        else
        {
            _ = SaveToFileAsync(finalImage, sourceApp);
        }
    }

    private async Task CopyToClipboardAsync(BitmapSource image, string? report)
    {
        try
        {
            await ClipboardService.CopyImageAsync(image, report);

            if (settings.ShowSaveNotification)
            {
                notifier.ShowInfo("Screenshot copied", "Copied to the clipboard");
            }
        }
        catch (Exception problem)
        {
            notifier.ShowError("Couldn't copy the screenshot", problem.Message);
        }
    }

    private async Task SaveToFileAsync(BitmapSource image, string sourceApp)
    {
        try
        {
            string? chosenPath;
            choosingSavePath = true;
            try
            {
                chosenPath = ScreenshotSaver.PromptForSavePath(settings, sourceApp);
            }
            finally
            {
                choosingSavePath = false;
            }

            if (chosenPath is null) return;

            await ScreenshotSaver.SaveToPathAsync(image, chosenPath, settings);

            if (settings.ShowSaveNotification)
            {
                notifier.ShowInfo("Screenshot saved", chosenPath);
            }
        }
        catch (Exception problem)
        {
            notifier.ShowError("Couldn't save the screenshot", problem.Message);
        }
    }

    private OverlayWindow? FindOverlayUnderCursor()
    {
        NativeMethods.GetCursorPos(out NativeMethods.NativePoint cursor);

        foreach (OverlayWindow overlay in overlays)
        {
            if (ContainsPoint(overlay.MonitorBounds, cursor.X, cursor.Y)) return overlay;
        }

        return overlays.Count > 0 ? overlays[0] : null;
    }

    private static bool ContainsPoint(Int32Rect bounds, int x, int y) =>
        x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height;

    private void CloseAllOverlays()
    {
        if (currentSession != this) return;

        foreach (OverlayWindow overlay in overlays)
        {
            overlay.ReleaseForPool();
            pooledOverlays[overlay.MonitorBounds] = overlay;
        }

        overlays.Clear();
        currentSession = null;


    }
}
