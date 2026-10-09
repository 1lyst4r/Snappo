using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Snappo.Output;

internal static class ClipboardService
{
    private const int MaxAttempts = 10;
    private const int RetryDelayMilliseconds = 50;

    public static Task CopyImageAsync(BitmapSource frozenImage)
    {
        var finished = new TaskCompletionSource();

        var clipboardThread = new Thread(() =>
        {
            // Another app (clipboard managers, RDP, Office...) can hold the clipboard for a moment, so retry a few times.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    Clipboard.SetImage(frozenImage);   // copies with flush, so the image stays available after we exit
                    finished.SetResult();
                    return;
                }
                catch (Exception problem) when (attempt < MaxAttempts && problem is ExternalException)
                {
                    Thread.Sleep(RetryDelayMilliseconds);
                }
                catch (Exception problem)
                {
                    finished.SetException(problem);
                    return;
                }
            }
        });

        clipboardThread.SetApartmentState(ApartmentState.STA);
        clipboardThread.Start();

        return finished.Task;
    }
}
