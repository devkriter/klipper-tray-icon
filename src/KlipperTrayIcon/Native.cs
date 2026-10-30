using System;
using System.Runtime.InteropServices;

namespace KlipperTrayIcon;

/// <summary>The few Win32 calls .NET doesn't wrap.</summary>
internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    public static partial IntPtr MonitorFromPoint(POINT pt, uint flags);
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);
    public const int ASFW_ANY = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr icon);

    /// <summary>Cursor position and the work area / DPI scale of the monitor it's on (all in pixels).</summary>
    public static (POINT Cursor, RECT WorkArea, double Scale) CursorMonitor()
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        double scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return (cursor, info.rcWork, scale);
    }
}
