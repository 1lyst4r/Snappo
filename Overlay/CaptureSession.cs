using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private static CaptureSession? currentSession;

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

    // True while reopening the last screenshot, so the overlay doesn't also apply the remembered selection.
    public bool IsContinuingEdit { get; private set; }

    public static bool IsOpen => currentSession is not null;

    public static void AbortCurrentSession() => currentSession?.CloseAllOverlays();

    public async Task StartAsync()
    {
        currentSession = this;

        try
        {
            IReadOnlyList<MonitorShot> shots = await Task.Run(() => ScreenCapturer.CaptureAllMonitors(settings.CaptureCursor));

            RetireStalePooledOverlays(shots);

            foreach (MonitorShot shot in shots)
            {
                overlays.Add(RentOverlay(shot));
            }

            foreach (OverlayWindow overlay in overlays)
            {
                overlay.Show();
            }

            FindOverlayUnderCursor()?.BringToFront();
        }
        catch
        {
            CloseAllOverlays();
            throw;
        }
    }

    // Pooling

    private OverlayWindow RentOverlay(MonitorShot shot)
    {
        if (pooledOverlays.Remove(shot.Bounds, out OverlayWindow? pooled))
        {
            pooled.RearmForCapture(shot, this);
            return pooled;
        }

        return new OverlayWindow(shot, this);
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
        IsContinuingEdit = true;

        try
        {
            OverlayWindow overlay = RentOverlay(previous.MonitorShot);
            overlays.Add(overlay);
            overlay.Show();

            overlay.Dispatcher.InvokeAsync(
                () => overlay.RestoreAnnotatedSelection(previous.Selection, previous.Annotations),
                DispatcherPriority.Loaded);

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

    public void Finish(BitmapSource finalImage, FinishAction action)
    {
        CloseAllOverlays();

        if (action == FinishAction.CopyToClipboard)
        {
            _ = CopyToClipboardAsync(finalImage);
        }
        else
        {
            _ = SaveToFileAsync(finalImage);
        }
    }

    private async Task CopyToClipboardAsync(BitmapSource image)
    {
        try
        {
            await ClipboardService.CopyImageAsync(image);

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

    private async Task SaveToFileAsync(BitmapSource image)
    {
        string? chosenPath = ScreenshotSaver.PromptForSavePath(settings);
        if (chosenPath is null)
        {
            return;
        }

        try
        {
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
            if (overlay.ContainsScreenPoint(cursor.X, cursor.Y)) return overlay;
        }

        return overlays.Count > 0 ? overlays[0] : null;
    }

    private void CloseAllOverlays()
    {
        foreach (OverlayWindow overlay in overlays)
        {
            overlay.ReleaseForPool();
            pooledOverlays[overlay.MonitorBounds] = overlay;
        }

        overlays.Clear();
        currentSession = null;
        IsContinuingEdit = false;

        MemoryTrimmer.TrimWhenIdle();
    }
}
