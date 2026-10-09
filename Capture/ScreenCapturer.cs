using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snappo.Interop;

namespace Snappo.Capture;

internal static class ScreenCapturer
{
    private const int BitsPerPixel = 32;
    private const int BytesPerPixel = 4;

    public static IReadOnlyList<MonitorShot> CaptureAllMonitors(bool includeCursor)
    {
        var shots = new List<MonitorShot>();

        foreach (Int32Rect bounds in GetMonitorBounds())
        {
            shots.Add(new MonitorShot(bounds, CaptureRegion(bounds, includeCursor)));
        }

        return shots;
    }

    public static List<Int32Rect> GetMonitorBounds()
    {
        var allBounds = new List<Int32Rect>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeMethods.NativeRect _, IntPtr _) =>
        {
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                NativeMethods.NativeRect area = info.Monitor;
                allBounds.Add(new Int32Rect(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top));
            }

            return true;
        }, IntPtr.Zero);

        return allBounds;
    }

    public static void WarmUp()
    {
        try
        {
            CaptureRegion(new Int32Rect(0, 0, 1, 1), includeCursor: true);
        }
        catch (Exception)
        {
        }
    }

    private static void DrawCursorOnto(IntPtr deviceContext, Int32Rect region)
    {
        var cursorInfo = new NativeMethods.CursorInfo { Size = Marshal.SizeOf<NativeMethods.CursorInfo>() };

        bool cursorIsVisible = NativeMethods.GetCursorInfo(ref cursorInfo)
                               && (cursorInfo.Flags & NativeMethods.CursorIsShowing) != 0;
        if (!cursorIsVisible || !NativeMethods.GetIconInfo(cursorInfo.CursorHandle, out NativeMethods.IconInfo iconInfo))
        {
            return;
        }

        try
        {
            int left = cursorInfo.ScreenPosition.X - iconInfo.HotspotX - region.X;
            int top = cursorInfo.ScreenPosition.Y - iconInfo.HotspotY - region.Y;

            NativeMethods.DrawIconEx(deviceContext, left, top, cursorInfo.CursorHandle, 0, 0, 0, IntPtr.Zero, NativeMethods.DrawIconNormal);
        }
        finally
        {
            if (iconInfo.MaskBitmap != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.MaskBitmap);
            if (iconInfo.ColorBitmap != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.ColorBitmap);
        }
    }

    private static BitmapSource CaptureRegion(Int32Rect region, bool includeCursor)
    {
        IntPtr screenContext = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memoryContext = NativeMethods.CreateCompatibleDC(screenContext);
        IntPtr dibHandle = IntPtr.Zero;
        IntPtr originalObject = IntPtr.Zero;

        try
        {
            var header = new NativeMethods.BitmapInfoHeader
            {
                Size = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                Width = region.Width,
                Height = -region.Height,   // negative = top-down, matches how WPF reads rows
                Planes = 1,
                BitCount = BitsPerPixel,
            };

            dibHandle = NativeMethods.CreateDIBSection(memoryContext, ref header, 0, out IntPtr pixelData, IntPtr.Zero, 0);
            if (dibHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate the screenshot buffer.");
            }

            originalObject = NativeMethods.SelectObject(memoryContext, dibHandle);

            bool copied = NativeMethods.BitBlt(
                memoryContext, 0, 0, region.Width, region.Height,
                screenContext, region.X, region.Y,
                NativeMethods.CopySourceToDestination);

            if (!copied)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not copy the screen contents.");
            }

            if (includeCursor)
            {
                DrawCursorOnto(memoryContext, region);
            }

            int stride = region.Width * BytesPerPixel;

            BitmapSource bitmap = BitmapSource.Create(
                region.Width, region.Height, 96, 96, PixelFormats.Bgr32, null,
                pixelData, stride * region.Height, stride);

            bitmap.Freeze();   // frozen bitmaps are cheaper to render and safe to share between threads
            return bitmap;
        }
        finally
        {
            if (originalObject != IntPtr.Zero) NativeMethods.SelectObject(memoryContext, originalObject);
            if (dibHandle != IntPtr.Zero) NativeMethods.DeleteObject(dibHandle);
            NativeMethods.DeleteDC(memoryContext);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenContext);
        }
    }
}
