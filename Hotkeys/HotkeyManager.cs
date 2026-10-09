using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using Snappo.Interop;

namespace Snappo.Hotkeys;

internal sealed class HotkeyManager : IDisposable
{
    private const int ShiftKeyCode = 0x10;
    private const int ControlKeyCode = 0x11;
    private const int AltKeyCode = 0x12;
    private const int LeftWindowsKeyCode = 0x5B;
    private const int RightWindowsKeyCode = 0x5C;
    private const int KeyIsDownBit = 0x8000;

    private const int MouseDataOffset = 8;

    private const int UpdateHooksMessage = 0x8001;   // WM_APP + 1
    private const int QuitMessage = 0x0012;          // WM_QUIT

    private readonly Dispatcher uiDispatcher;
    private readonly Dictionary<HotkeyAction, HotkeyTrigger> triggersByAction = new();   // UI thread only
    private readonly NativeMethods.LowLevelHookCallback keyboardCallback;   // kept in fields so the GC can't collect them
    private readonly NativeMethods.LowLevelHookCallback mouseCallback;

    // The hooks live on their own thread with its own message loop, so a busy UI thread
    // can never lag the keyboard system-wide or make Windows silently drop the hook.
    private readonly Thread hookThread;
    private readonly AutoResetEvent hookThreadAnswered = new(false);
    private uint hookThreadId;
    private int hookErrorCode;

    private volatile KeyValuePair<HotkeyAction, HotkeyTrigger>[] activeTriggers = Array.Empty<KeyValuePair<HotkeyAction, HotkeyTrigger>>();

    // Only touched on the hook thread.
    private IntPtr keyboardHook = IntPtr.Zero;
    private IntPtr mouseHook = IntPtr.Zero;
    private int swallowedKeyCode;                                   // lets us also swallow the matching key-up and auto-repeats
    private SideMouseButton swallowedMouseButton = SideMouseButton.None;

    public event Action<HotkeyAction>? ActionTriggered;

    public bool IsPaused { get; set; }

    public Func<bool>? ShouldPassKeysThrough { get; set; }

    private bool IsListening => !IsPaused && ShouldPassKeysThrough?.Invoke() != true;

    public HotkeyManager(Dispatcher uiDispatcher)
    {
        this.uiDispatcher = uiDispatcher;
        keyboardCallback = OnKeyboardEvent;
        mouseCallback = OnMouseEvent;

        // Windows makes every mouse/keyboard event in the system wait for our hook, so this thread must
        // always get the CPU right away, even right after boot when everything else is starting up.
        hookThread = new Thread(RunHookThread) { IsBackground = true, Name = "Snappo hotkeys", Priority = ThreadPriority.Highest };
        hookThread.Start();
        hookThreadAnswered.WaitOne();   // wait until its message queue exists
    }

    public void SetTrigger(HotkeyAction action, HotkeyTrigger trigger)
    {
        triggersByAction[action] = trigger;
        activeTriggers = triggersByAction.ToArray();
        UpdateHooksOnHookThread();
    }

    public void Dispose()
    {
        triggersByAction.Clear();
        activeTriggers = Array.Empty<KeyValuePair<HotkeyAction, HotkeyTrigger>>();
        NativeMethods.PostThreadMessage(hookThreadId, QuitMessage, IntPtr.Zero, IntPtr.Zero);
        hookThread.Join(1000);
    }

    private void UpdateHooksOnHookThread()
    {
        hookErrorCode = 0;
        if (!NativeMethods.PostThreadMessage(hookThreadId, UpdateHooksMessage, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not reach the hotkey thread.");
        }

        hookThreadAnswered.WaitOne();

        if (hookErrorCode != 0)
        {
            throw new Win32Exception(hookErrorCode, "Could not install the global hotkey hook.");
        }
    }

    private void RunHookThread()
    {
        hookThreadId = NativeMethods.GetCurrentThreadId();
        NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // creates this thread's message queue
        hookThreadAnswered.Set();

        while (NativeMethods.GetMessage(out NativeMethods.ThreadMessage message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message == UpdateHooksMessage)
            {
                InstallOnlyTheHooksWeNeed();
                hookThreadAnswered.Set();
            }
        }

        activeTriggers = Array.Empty<KeyValuePair<HotkeyAction, HotkeyTrigger>>();
        InstallOnlyTheHooksWeNeed();   // removes both hooks
    }

    private void InstallOnlyTheHooksWeNeed()
    {
        KeyValuePair<HotkeyAction, HotkeyTrigger>[] triggers = activeTriggers;
        bool needsKeyboardHook = triggers.Any(pair => !pair.Value.UsesMouse);
        bool needsMouseHook = triggers.Any(pair => pair.Value.UsesMouse);

        keyboardHook = UpdateHook(keyboardHook, needsKeyboardHook, NativeMethods.KeyboardHookId, keyboardCallback);
        mouseHook = UpdateHook(mouseHook, needsMouseHook, NativeMethods.MouseHookId, mouseCallback);
    }

    private IntPtr UpdateHook(IntPtr currentHook, bool isNeeded, int hookId, NativeMethods.LowLevelHookCallback callback)
    {
        if (isNeeded && currentHook == IntPtr.Zero)
        {
            IntPtr newHook = NativeMethods.SetWindowsHookEx(hookId, callback, NativeMethods.GetModuleHandle(null), 0);
            if (newHook == IntPtr.Zero)
            {
                hookErrorCode = Marshal.GetLastWin32Error();
            }

            return newHook;
        }

        if (!isNeeded && currentHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(currentHook);
            return IntPtr.Zero;
        }

        return currentHook;
    }

    // Keyboard
    private IntPtr OnKeyboardEvent(int hookCode, IntPtr messageId, IntPtr eventData)
    {
        if (hookCode >= 0)
        {
            int message = messageId.ToInt32();
            bool isKeyDown = message == NativeMethods.KeyDownMessage || message == NativeMethods.SystemKeyDownMessage;
            bool isKeyUp = message == NativeMethods.KeyUpMessage || message == NativeMethods.SystemKeyUpMessage;

            int keyCode = Marshal.ReadInt32(eventData);

            if (keyCode == swallowedKeyCode)
            {
                if (isKeyUp)
                {
                    swallowedKeyCode = 0;
                }

                return (IntPtr)1;
            }

            if (isKeyDown && IsListening)
            {
                Key key = KeyInterop.KeyFromVirtualKey(keyCode);

                ModifierKeys otherModifiers = ReadCurrentModifiers() & ~HotkeyTrigger.ModifierFor(key);

                if (TryFindAction(key, otherModifiers, out HotkeyAction action))
                {
                    swallowedKeyCode = keyCode;
                    RaiseOnUiThread(action);
                    return (IntPtr)1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, hookCode, messageId, eventData);
    }

    private bool TryFindAction(Key key, ModifierKeys modifiers, out HotkeyAction action)
    {
        foreach ((HotkeyAction candidateAction, HotkeyTrigger trigger) in activeTriggers)
        {
            if (!trigger.UsesMouse && trigger.Key == key && trigger.Modifiers == modifiers)
            {
                action = candidateAction;
                return true;
            }
        }

        action = default;
        return false;
    }

    // Mouse side buttons
    private IntPtr OnMouseEvent(int hookCode, IntPtr messageId, IntPtr eventData)
    {
        int message = messageId.ToInt32();
        bool isSideButtonMessage = message == NativeMethods.XButtonDownMessage || message == NativeMethods.XButtonUpMessage;

        if (hookCode >= 0 && isSideButtonMessage)
        {
            SideMouseButton button = ReadSideButton(eventData);

            if (button != SideMouseButton.None && button == swallowedMouseButton)
            {
                if (message == NativeMethods.XButtonUpMessage)
                {
                    swallowedMouseButton = SideMouseButton.None;
                }

                return (IntPtr)1;
            }

            if (message == NativeMethods.XButtonDownMessage && IsListening
                && TryFindAction(button, ReadCurrentModifiers(), out HotkeyAction action))
            {
                swallowedMouseButton = button;
                RaiseOnUiThread(action);
                return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, hookCode, messageId, eventData);
    }

    private bool TryFindAction(SideMouseButton button, ModifierKeys modifiers, out HotkeyAction action)
    {
        foreach ((HotkeyAction candidateAction, HotkeyTrigger trigger) in activeTriggers)
        {
            if (trigger.UsesMouse && trigger.MouseButton == button && trigger.Modifiers == modifiers)
            {
                action = candidateAction;
                return true;
            }
        }

        action = default;
        return false;
    }

    private static SideMouseButton ReadSideButton(IntPtr eventData)
    {
        int mouseData = Marshal.ReadInt32(eventData, MouseDataOffset);
        int whichButton = (mouseData >> 16) & 0xFFFF;

        return whichButton switch
        {
            1 => SideMouseButton.Button4,
            2 => SideMouseButton.Button5,
            _ => SideMouseButton.None,
        };
    }

    // Helpers
    private static ModifierKeys ReadCurrentModifiers()
    {
        var modifiers = ModifierKeys.None;

        if (IsHeldDown(ControlKeyCode)) modifiers |= ModifierKeys.Control;
        if (IsHeldDown(AltKeyCode)) modifiers |= ModifierKeys.Alt;
        if (IsHeldDown(ShiftKeyCode)) modifiers |= ModifierKeys.Shift;
        if (IsHeldDown(LeftWindowsKeyCode) || IsHeldDown(RightWindowsKeyCode)) modifiers |= ModifierKeys.Windows;

        return modifiers;
    }

    private static bool IsHeldDown(int virtualKeyCode) =>
        (NativeMethods.GetAsyncKeyState(virtualKeyCode) & KeyIsDownBit) != 0;

    private void RaiseOnUiThread(HotkeyAction action)
    {
        uiDispatcher.InvokeAsync(() => ActionTriggered?.Invoke(action), DispatcherPriority.Send);
    }
}
