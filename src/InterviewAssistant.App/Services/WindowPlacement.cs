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

    /// <summary>
    /// Fixed-size dialogs: never taller than the work area of the monitor they open on, and fully on that monitor
    /// (small or scaled displays). Call on Loaded, when the startup position is known.
    /// </summary>
    public static void FitToWorkArea(Window w)
    {
        var work = WorkAreaOf(w);
        w.MaxHeight = Math.Max(w.MinHeight, work.Height - 16);
        var width = double.IsNaN(w.Width) ? w.ActualWidth : w.Width;
        var height = Math.Min(double.IsNaN(w.Height) ? w.ActualHeight : w.Height, w.MaxHeight);
        if (!double.IsNaN(w.Left)) w.Left = Math.Clamp(w.Left, work.Left, Math.Max(work.Left, work.Right - width));
        if (!double.IsNaN(w.Top)) w.Top = Math.Clamp(w.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
    }

    /// <summary>Work area (DIPs) of the monitor showing the window; the primary work area when unknown.</summary>
    public static Rect WorkAreaOf(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var monitor = hwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || PresentationSource.FromVisual(w)?.CompositionTarget is not { } target)
            return SystemParameters.WorkArea;
        var m = target.TransformFromDevice;
        return new Rect(m.Transform(new Point(info.rcWork.Left, info.rcWork.Top)), m.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom)));
    }

    /// <summary>
    /// Places <paramref name="w"/> on the monitor it is on now: <paramref name="compute"/> receives that monitor's work area
    /// in the monitor's logical pixels (device pixels / the monitor's DPI scale, so negative origins and mixed DPI work)
    /// and returns the bounds in the same units; they are applied in device pixels. False when the window has no handle yet.
    /// </summary>
    public static bool PlaceOnCurrentMonitor(Window w, Func<Rect, Rect> compute)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return false;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        var scale = WindowDpiScale(w, hwnd);
        var work = new Rect(info.rcWork.Left / scale, info.rcWork.Top / scale, (info.rcWork.Right - info.rcWork.Left) / scale, (info.rcWork.Bottom - info.rcWork.Top) / scale);
        var r = compute(work);
        return SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(r.Left * scale), (int)Math.Round(r.Top * scale),
            (int)Math.Round(r.Width * scale), (int)Math.Round(r.Height * scale), SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static double WindowDpiScale(Window w, IntPtr hwnd)
    {
        try { var dpi = GetDpiForWindow(hwnd); if (dpi > 0) return dpi / 96.0; }
        catch (EntryPointNotFoundException) { }
        return PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 is double m && m > 0 ? m : SystemDpiScale();
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
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
}
