using System;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;

namespace Snappo.Interop;

internal static class MemoryTrimmer
{
    private static bool isScheduled;

    // Screenshots are big native bitmaps that are only freed when their finalizers run,
    // so after a capture we do one real (blocking, but tiny-heap) collection once the app is idle.
    public static void TrimWhenIdle()
    {
        if (isScheduled || Application.Current is null) return;
        isScheduled = true;

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            isScheduled = false;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }, DispatcherPriority.ApplicationIdle);
    }
}
