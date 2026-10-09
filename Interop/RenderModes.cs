using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Snappo.Capture;

namespace Snappo.Interop;

// WPF's GPU renderer loads the graphics driver into the process, which costs ~75 MB of RAM that never goes away.
// Software rendering is plenty fast for the tray, the settings window and overlays on normal-sized monitors,
// so only overlays on very large monitors (where repainting on the CPU could stutter) use the GPU.
internal static class RenderModes
{
    private const long LargestSoftwareRenderedMonitor = 2_600_000;   // pixels; 1920x1200 and 2048x1280 still fit

    // Switching the whole process up front is noticeably faster than switching each window after it's created,
    // so do that whenever every monitor is small enough. Otherwise windows are switched one by one.
    public static void ChooseForProcess()
    {
        if (ScreenCapturer.GetMonitorBounds().All(IsSmallEnoughForSoftware))
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
    }

    public static void UseSoftware(HwndSource? source)
    {
        if (source?.CompositionTarget is HwndTarget target)
        {
            target.RenderMode = RenderMode.SoftwareOnly;
        }
    }

    public static void UseSoftware(Window window) => UseSoftware(PresentationSource.FromVisual(window) as HwndSource);

    public static void ChooseForOverlay(Window overlay, Int32Rect monitorBounds)
    {
        if (IsSmallEnoughForSoftware(monitorBounds))
        {
            UseSoftware(overlay);
        }
    }

    private static bool IsSmallEnoughForSoftware(Int32Rect monitorBounds) =>
        (long)monitorBounds.Width * monitorBounds.Height <= LargestSoftwareRenderedMonitor;
}
