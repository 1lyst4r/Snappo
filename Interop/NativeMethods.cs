using System;
using System.Runtime.InteropServices;

namespace Snappo.Interop;

internal static class NativeMethods
{
    // Low-level hooks (global hotkeys)
    public const int KeyboardHookId = 13;      // WH_KEYBOARD_LL
    public const int MouseHookId = 14;         // WH_MOUSE_LL

    public const int KeyDownMessage = 0x0100;
    public const int KeyUpMessage = 0x0101;
    public const int SystemKeyDownMessage = 0x0104;   // key down while Alt is held
    public const int SystemKeyUpMessage = 0x0105;
    public const int XButtonDownMessage = 0x020B;     // mouse side buttons
    public const int XButtonUpMessage = 0x020C;

    public delegate IntPtr LowLevelHookCallback(int hookCode, IntPtr messageId, IntPtr eventData);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hookId, LowLevelHookCallback callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hookHandle, int hookCode, IntPtr messageId, IntPtr eventData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    // Message loop (the hotkey hooks run on their own thread)
    [StructLayout(LayoutKind.Sequential)]
    public struct ThreadMessage
    {
        public IntPtr WindowHandle;
        public int Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public int Time;
        public NativePoint Point;
        public int Private;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessage(out ThreadMessage message, IntPtr windowHandle, int filterMin, int filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out ThreadMessage message, IntPtr windowHandle, int filterMin, int filterMax, int removeFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint threadId, int message, IntPtr wParam, IntPtr lParam);

    // Windows and focus
    public static readonly IntPtr TopmostWindowGroup = new(-1);   // HWND_TOPMOST
    public const uint DoNotActivate = 0x0010;                     // SWP_NOACTIVATE

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint threadToAttach, uint threadToAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr iconHandle);

    public static void ForceToForeground(IntPtr windowHandle)
    {
        IntPtr currentForeground = GetForegroundWindow();
        if (currentForeground == windowHandle)
        {
            return;
        }

        uint foregroundThread = GetWindowThreadProcessId(currentForeground, out _);
        uint ourThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != ourThread
                        && AttachThreadInput(ourThread, foregroundThread, true);

        SetForegroundWindow(windowHandle);

        if (attached)
        {
            AttachThreadInput(ourThread, foregroundThread, false);
        }
    }

    // Mouse cursor (for "Capture the cursor on a screenshot")
    public const int CursorIsShowing = 0x0001;    // CURSOR_SHOWING
    public const uint DrawIconNormal = 0x0003;    // DI_NORMAL

    [StructLayout(LayoutKind.Sequential)]
    public struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr CursorHandle;
        public NativePoint ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetIconInfo(IntPtr iconHandle, out IconInfo iconInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DrawIconEx(IntPtr deviceContext, int x, int y, IntPtr iconHandle,
                                         int width, int height, uint frameIndex, IntPtr backgroundBrush, uint flags);

    // GDI (screen capture)
    public const uint CopySourceToDestination = 0x00CC0020;   // SRCCOPY

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;          // negative = top-down rows
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public int ColorsUsed;
        public int ColorsImportant;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapInfoHeader bitmapInfo, uint usage,
                                                 out IntPtr pixelData, IntPtr sectionHandle, uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr destination, int destX, int destY, int width, int height,
                                     IntPtr source, int sourceX, int sourceY, uint rasterOperation);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("user32.dll")]
    public static extern IntPtr CreateIconIndirect(ref IconInfo iconInfo);

    // Monitors
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    public delegate bool MonitorEnumCallback(IntPtr monitor, IntPtr deviceContext, ref NativeRect bounds, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumCallback callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // Tray icon
    public const int NotifyIconAdd = 0;          // NIM_ADD
    public const int NotifyIconModify = 1;       // NIM_MODIFY
    public const int NotifyIconDelete = 2;       // NIM_DELETE
    public const int NotifyIconMessageFlag = 0x01;   // NIF_MESSAGE
    public const int NotifyIconIconFlag = 0x02;      // NIF_ICON
    public const int NotifyIconTipFlag = 0x04;       // NIF_TIP
    public const int NotifyIconInfoFlag = 0x10;      // NIF_INFO
    public const int BalloonInfoIcon = 1;            // NIIF_INFO
    public const int BalloonErrorIcon = 3;           // NIIF_ERROR

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int Size;
        public IntPtr WindowHandle;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShellNotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string message);

    // Popup menus
    public const uint MenuString = 0x0000;       // MF_STRING
    public const uint MenuGrayed = 0x0001;       // MF_GRAYED
    public const uint MenuSeparator = 0x0800;    // MF_SEPARATOR
    public const uint TrackRightButton = 0x0002; // TPM_RIGHTBUTTON
    public const uint TrackNoNotify = 0x0080;    // TPM_NONOTIFY
    public const uint TrackReturnCommand = 0x0100; // TPM_RETURNCMD

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr windowHandle, IntPtr options);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);
}
