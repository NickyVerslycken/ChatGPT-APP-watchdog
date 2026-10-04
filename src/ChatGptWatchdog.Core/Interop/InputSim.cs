using System.Runtime.InteropServices;
using System.Text;

namespace ChatGptWatchdog.Core.Interop;

/// <summary>Keyboard/mouse/clipboard/foreground helpers built on SendInput.</summary>
public static class InputSim
{
    private static readonly int InputSize = Marshal.SizeOf<Native.INPUT>();

    /// <summary>Milliseconds since the user last touched keyboard or mouse.</summary>
    public static int UserIdleMilliseconds()
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return 0;
        return (int)(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    public static IntPtr Foreground() => Native.GetForegroundWindow();

    public static int ForegroundPid()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    /// <summary>Brings a window to the front, working around Windows' foreground lock.</summary>
    public static bool BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        if (Native.GetForegroundWindow() == hwnd) return true;

        if (TryForeground(hwnd)) return true;
        // Last resort: an ALT tap makes Windows accept SetForegroundWindow from this process.
        KeyTap(Native.VK_MENU);
        return TryForeground(hwnd);
    }

    private static bool TryForeground(IntPtr hwnd)
    {
        var fg = Native.GetForegroundWindow();
        var fgThread = Native.GetWindowThreadProcessId(fg, out _);
        var myThread = Native.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != myThread && Native.AttachThreadInput(myThread, fgThread, true);
        try
        {
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
            Native.ShowWindow(hwnd, Native.SW_SHOW);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(myThread, fgThread, false);
        }
        Thread.Sleep(150);
        return Native.GetForegroundWindow() == hwnd;
    }

    public static (int left, int top, int right, int bottom) WindowRect(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var r);
        return (r.Left, r.Top, r.Right, r.Bottom);
    }

    // ---------------- keyboard ----------------

    private static Native.INPUT Key(ushort vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } },
    };

    private static Native.INPUT Unicode(char c, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION
        {
            ki = new Native.KEYBDINPUT { wVk = 0, wScan = c, dwFlags = Native.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0) },
        },
    };

    private static void Send(params Native.INPUT[] inputs) => Native.SendInput((uint)inputs.Length, inputs, InputSize);

    public static void KeyTap(ushort vk) => Send(Key(vk, false), Key(vk, true));

    public static void Chord(ushort modifier, ushort vk) =>
        Send(Key(modifier, false), Key(vk, false), Key(vk, true), Key(modifier, true));

    public static void ShiftEnter() => Chord(Native.VK_SHIFT, Native.VK_RETURN);
    public static void Enter() => KeyTap(Native.VK_RETURN);
    public static void CtrlV() => Chord(Native.VK_CONTROL, Native.VK_V);
    public static void CtrlA() => Chord(Native.VK_CONTROL, Native.VK_A);

    /// <summary>
    /// Types text as Unicode key events. Newlines become Shift+Enter (so they don't send the message).
    /// <paramref name="stillOk"/> is checked every few characters; typing stops when it returns false.
    /// </summary>
    public static bool TypeText(string text, Func<bool> stillOk, int delayMs = 4)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        int i = 0;
        foreach (var c in normalized)
        {
            if (i++ % 20 == 0 && !stillOk()) return false;
            if (c == '\n') ShiftEnter();
            else Send(Unicode(c, false), Unicode(c, true));
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
        return true;
    }

    // ---------------- mouse ----------------

    public static void LeftClickAt(int x, int y)
    {
        Native.GetCursorPos(out var old);
        Native.SetCursorPos(x, y);
        Thread.Sleep(30);
        Send(
            new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dwFlags = Native.MOUSEEVENTF_LEFTDOWN } } },
            new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dwFlags = Native.MOUSEEVENTF_LEFTUP } } });
        Thread.Sleep(30);
        Native.SetCursorPos(old.X, old.Y);
    }

    // ---------------- clipboard ----------------

    public static string? GetClipboardText()
    {
        if (!Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT)) return null;
        if (!OpenClipboardRetry()) return null;
        try
        {
            var h = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = Native.GlobalLock(h);
            try { return p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p); }
            finally { Native.GlobalUnlock(h); }
        }
        finally { Native.CloseClipboard(); }
    }

    public static bool SetClipboardText(string text)
    {
        if (!OpenClipboardRetry()) return false;
        try
        {
            Native.EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (h == IntPtr.Zero) return false;
            var p = Native.GlobalLock(h);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Native.GlobalUnlock(h);
            if (Native.SetClipboardData(Native.CF_UNICODETEXT, h) == IntPtr.Zero)
            {
                Native.GlobalFree(h);
                return false;
            }
            return true;
        }
        finally { Native.CloseClipboard(); }
    }

    private static bool OpenClipboardRetry()
    {
        for (int i = 0; i < 10; i++)
        {
            if (Native.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(50);
        }
        return false;
    }
}
