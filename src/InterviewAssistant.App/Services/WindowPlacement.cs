using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Borderless-window helpers: maximize to the monitor's WORK AREA (a WindowStyle=None window would otherwise cover the
/// taskbar), and restore saved bounds only when they are still visible on a connected monitor (monitor unplugged,
/// resolution or DPI changed → fall back to a centred default).
/// </summary>
public static class WindowPlacement
{
    public static void HookMaximizeToWorkArea(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var src = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            src?.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_GETMINMAXINFO) { ApplyWorkArea(hwnd, l); handled = false; }
                return IntPtr.Zero;
            });
        };
    }

    /// <summary>True when the window's title area (where it can be dragged) lies on a connected monitor.</summary>
    public static bool IsVisibleOnAnyMonitor(double left, double top, double width)
    {
        var scale = SystemDpiScale();
        var probe = new RECT { Left = (int)((left + 40) * scale), Top = (int)((top + 8) * scale), Right = (int)((left + Math.Min(width, 240)) * scale), Bottom = (int)((top + 40) * scale) };
        return MonitorFromRect(ref probe, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    /// <summary>Restores bounds (DIPs) if visible; otherwise centres a default-sized window on the primary work area.</summary>
    public static void Restore(Window w, double? left, double? top, double width, double height, bool maximized)
    {
        var work = SystemParameters.WorkArea;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Width = Math.Clamp(width, w.MinWidth, Math.Max(w.MinWidth, work.Width));
        w.Height = Math.Clamp(height, w.MinHeight, Math.Max(w.MinHeight, work.Height));
        if (left is double l && top is double t && IsVisibleOnAnyMonitor(l, t, w.Width)) { w.Left = l; w.Top = t; }
        else { w.Left = work.Left + (work.Width - w.Width) / 2; w.Top = work.Top + (work.Height - w.Height) / 2; }
        if (maximized) w.Loaded += (_, _) => w.WindowState = WindowState.Maximized;
    }

    private static double SystemDpiScale()
    {
        try { return GetDpiForSystem() / 96.0; } catch (EntryPointNotFoundException) { return 1.0; }
    }

    private static void ApplyWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
}
