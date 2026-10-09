using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snappo.Editor;
using Snappo.Interop;

namespace Snappo.Tray;

// Talks to the shell directly instead of going through WinForms' NotifyIcon,
// so the app never has to load WinForms or System.Drawing.
internal sealed class TrayIcon : IUserNotifier, IDisposable
{
    private const int IconSize = 32;
    private const int TrayCallbackMessage = 0x8001;   // WM_APP + 1
    private const int LeftButtonUp = 0x0202;           // WM_LBUTTONUP
    private const int RightButtonUp = 0x0205;          // WM_RBUTTONUP
    private const int NullMessage = 0x0000;            // WM_NULL
    private const string TooltipText = "Snappo";

    private enum MenuCommand
    {
        TakeScreenshot = 1,
        ContinueEditing,
        OpenFolder,
        Settings,
        Exit,
    }

    private static readonly int TaskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource messageWindow;
    private readonly IntPtr iconHandle;
    private readonly Action takeScreenshot;
    private readonly Action continueEditing;
    private readonly Action openScreenshotsFolder;
    private readonly Action openSettings;
    private readonly Action exitApp;
    private bool isDisposed;

    public TrayIcon(Action takeScreenshot, Action continueEditing, Action openScreenshotsFolder, Action openSettings, Action exitApp)
    {
        this.takeScreenshot = takeScreenshot;
        this.continueEditing = continueEditing;
        this.openScreenshotsFolder = openScreenshotsFolder;
        this.openSettings = openSettings;
        this.exitApp = exitApp;

        // A hidden top-level window (not message-only, so it still hears "TaskbarCreated" after Explorer restarts).
        messageWindow = new HwndSource(new HwndSourceParameters("SnappoTray") { Width = 0, Height = 0, WindowStyle = 0 });
        messageWindow.AddHook(OnWindowMessage);
        RenderModes.UseSoftware(messageWindow);

        iconHandle = CreateIconHandle();
        AddToTray();
    }

    public void ShowInfo(string title, string message) => ShowBalloon(title, message, NativeMethods.BalloonInfoIcon);

    public void ShowError(string title, string message) => ShowBalloon(title, message, NativeMethods.BalloonErrorIcon);

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        NativeMethods.NotifyIconData data = CreateIconData(0);
        NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconDelete, ref data);
        NativeMethods.DestroyIcon(iconHandle);
        messageWindow.RemoveHook(OnWindowMessage);
        messageWindow.Dispose();
    }

    private void AddToTray()
    {
        NativeMethods.NotifyIconData data = CreateIconData(
            NativeMethods.NotifyIconMessageFlag | NativeMethods.NotifyIconIconFlag | NativeMethods.NotifyIconTipFlag);
        data.CallbackMessage = TrayCallbackMessage;
        data.IconHandle = iconHandle;
        data.Tip = TooltipText;

        NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconAdd, ref data);
    }

    private void ShowBalloon(string title, string message, int balloonIcon)
    {
        if (isDisposed) return;

        NativeMethods.NotifyIconData data = CreateIconData(NativeMethods.NotifyIconInfoFlag);
        data.InfoTitle = Truncate(title, 63);
        data.Info = Truncate(string.IsNullOrEmpty(message) ? " " : message, 255);
        data.InfoFlags = balloonIcon;

        NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconModify, ref data);
    }

    private NativeMethods.NotifyIconData CreateIconData(int flags) => new()
    {
        Size = Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        WindowHandle = messageWindow.Handle,
        Id = 1,
        Flags = flags,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];

    private IntPtr OnWindowMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == TrayCallbackMessage)
        {
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case LeftButtonUp:
                    takeScreenshot();
                    break;

                case RightButtonUp:
                    ShowMenu();
                    break;
            }

            handled = true;
        }
        else if (message == TaskbarCreatedMessage && !isDisposed)
        {
            AddToTray();   // Explorer restarted and forgot every tray icon
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        try
        {
            uint continueFlags = LastCaptureMemory.Value is null ? NativeMethods.MenuGrayed : NativeMethods.MenuString;

            AddItem(menu, NativeMethods.MenuString, MenuCommand.TakeScreenshot, "Take screenshot");
            AddItem(menu, continueFlags, MenuCommand.ContinueEditing, "Continue editing last screenshot");
            AddItem(menu, NativeMethods.MenuString, MenuCommand.OpenFolder, "Open screenshots folder");
            AddItem(menu, NativeMethods.MenuString, MenuCommand.Settings, "Settings...");
            NativeMethods.AppendMenu(menu, NativeMethods.MenuSeparator, UIntPtr.Zero, null);
            AddItem(menu, NativeMethods.MenuString, MenuCommand.Exit, "Exit");
            NativeMethods.SetMenuDefaultItem(menu, (uint)MenuCommand.TakeScreenshot, 0);

            NativeMethods.GetCursorPos(out NativeMethods.NativePoint cursor);

            // Without this the menu won't close when you click somewhere else.
            NativeMethods.SetForegroundWindow(messageWindow.Handle);
            int chosen = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TrackRightButton | NativeMethods.TrackReturnCommand | NativeMethods.TrackNoNotify,
                cursor.X, cursor.Y, messageWindow.Handle, IntPtr.Zero);
            NativeMethods.PostMessage(messageWindow.Handle, NullMessage, IntPtr.Zero, IntPtr.Zero);

            RunCommand((MenuCommand)chosen);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }

        static void AddItem(IntPtr menu, uint flags, MenuCommand command, string text) =>
            NativeMethods.AppendMenu(menu, flags, (UIntPtr)(uint)command, text);
    }

    private void RunCommand(MenuCommand command)
    {
        switch (command)
        {
            case MenuCommand.TakeScreenshot: takeScreenshot(); break;
            case MenuCommand.ContinueEditing: continueEditing(); break;
            case MenuCommand.OpenFolder: openScreenshotsFolder(); break;
            case MenuCommand.Settings: openSettings(); break;
            case MenuCommand.Exit: exitApp(); break;
        }
    }

    // Icon: a blue rounded square with four white corner brackets.
    private static IntPtr CreateIconHandle()
    {
        byte[] pixels = DrawIconPixels();

        var header = new NativeMethods.BitmapInfoHeader
        {
            Size = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
            Width = IconSize,
            Height = -IconSize,
            Planes = 1,
            BitCount = 32,
        };

        IntPtr colorBitmap = NativeMethods.CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr colorBits, IntPtr.Zero, 0);
        IntPtr maskBitmap = NativeMethods.CreateBitmap(IconSize, IconSize, 1, 1, IntPtr.Zero);

        try
        {
            Marshal.Copy(pixels, 0, colorBits, pixels.Length);

            var iconInfo = new NativeMethods.IconInfo { IsIcon = true, MaskBitmap = maskBitmap, ColorBitmap = colorBitmap };
            return NativeMethods.CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            NativeMethods.DeleteObject(colorBitmap);
            NativeMethods.DeleteObject(maskBitmap);
        }
    }

    private static byte[] DrawIconPixels()
    {
        const double near = 8;
        const double far = 24;
        const double bracketLength = 6;

        var bracketPen = new Pen(Brushes.White, 3)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        bracketPen.Freeze();

        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            var background = new SolidColorBrush(Color.FromRgb(10, 132, 255));
            background.Freeze();
            drawing.DrawRoundedRectangle(background, null, new Rect(0, 0, IconSize, IconSize), 5, 5);

            DrawBracket(new Point(near, near + bracketLength), new Point(near, near), new Point(near + bracketLength, near));
            DrawBracket(new Point(far - bracketLength, near), new Point(far, near), new Point(far, near + bracketLength));
            DrawBracket(new Point(near, far - bracketLength), new Point(near, far), new Point(near + bracketLength, far));
            DrawBracket(new Point(far - bracketLength, far), new Point(far, far), new Point(far, far - bracketLength));

            void DrawBracket(Point start, Point corner, Point end)
            {
                var bracket = new StreamGeometry();
                using (StreamGeometryContext figure = bracket.Open())
                {
                    figure.BeginFigure(start, false, false);
                    figure.LineTo(corner, true, true);
                    figure.LineTo(end, true, true);
                }

                drawing.DrawGeometry(null, bracketPen, bracket);
            }
        }

        var target = new RenderTargetBitmap(IconSize, IconSize, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        // Icons want straight (non-premultiplied) alpha.
        var straightAlpha = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[IconSize * IconSize * 4];
        straightAlpha.CopyPixels(pixels, IconSize * 4, 0);
        return pixels;
    }
}
