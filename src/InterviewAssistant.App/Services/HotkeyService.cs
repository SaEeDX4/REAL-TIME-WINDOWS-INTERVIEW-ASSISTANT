using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace InterviewAssistant.App.Services;

/// <summary>System-wide hotkeys via RegisterHotKey. Defaults use Ctrl+Alt to avoid Chrome/Meet shortcuts (Ctrl+D, Ctrl+E, Ctrl+Shift+*).</summary>
public sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5100;

    public HotkeyService(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        Key key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default: if (!Enum.TryParse(part, true, out key)) return false; break;
            }
        }
        if (key == Key.None || modifiers == 0) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return true;
    }

    /// <returns>null on success, else a human-readable reason (e.g. already used by another app).</returns>
    public string? Register(string combo, Action action)
    {
        if (!TryParse(combo, out var mods, out var vk)) return $"Invalid hotkey '{combo}'";
        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk)) return $"Hotkey {combo} is already in use by another application";
        _actions[id] = action;
        return null;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a)) { a(); handled = true; }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
