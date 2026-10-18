using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace InterviewAssistant.App.Views;

/// <summary>
/// A compact popover anchored under a header button (Quick help, More). Keyboard complete: focus moves inside when it
/// opens, Tab / arrow keys cycle inside, Escape closes and returns focus to the button. A click anywhere else in the
/// window, moving or resizing the window, or switching to another app closes it. Deterministic (StaysOpen=True and the
/// closing rules here) so it behaves the same with and without OS focus (CI desktop).
/// </summary>
public sealed class Popover
{
    private readonly Window _window;
    private readonly Popup _popup;
    private readonly ButtonBase _anchor;

    /// <summary>Raised after the popover closed (whatever closed it).</summary>
    public event Action? Closed;

    public Popover(Window window, Popup popup, ButtonBase anchor)
    {
        _window = window; _popup = popup; _anchor = anchor;
        // End edges aligned: the card grows into the window from the button (mirrored by WPF in right-to-left layouts).
        _popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 8, targetSize.Height + 2), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(0, targetSize.Height + 2), PopupPrimaryAxis.Horizontal),
        };
        _popup.PreviewKeyDown += OnKey;
        window.PreviewKeyDown += OnKey;
        window.PreviewMouseDown += (_, e) =>
        {
            if (!IsOpen || e.OriginalSource is not DependencyObject d) return;
            if (IsWithin(d, _anchor)) return;   // the button toggles by itself
            if (_popup.Child is { } card && IsWithin(d, card)) return;   // popup content routes through the window too
            Close(restoreFocus: false);
        };
        window.Deactivated += (_, _) => { if (IsOpen && !_popup.IsKeyboardFocusWithin) Close(restoreFocus: false); };
        window.LocationChanged += (_, _) => { if (IsOpen) Close(restoreFocus: false); };
        window.SizeChanged += (_, _) => { if (IsOpen) Close(restoreFocus: false); };
        window.StateChanged += (_, _) => { if (IsOpen) Close(restoreFocus: false); };
    }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Opens and focuses <paramref name="initialFocus"/> (default: the first focusable element inside).</summary>
    public void Open(IInputElement? initialFocus = null)
    {
        if (IsOpen) return;
        _popup.IsOpen = true;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (!IsOpen) return;
            if (initialFocus is UIElement u && u.Focusable && u.IsVisible) u.Focus();
            else (_popup.Child as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, DispatcherPriority.Input);
    }

    public void Toggle(IInputElement? initialFocus = null) { if (IsOpen) Close(); else Open(initialFocus); }

    /// <summary>Closes; <paramref name="restoreFocus"/> returns keyboard focus to the anchor button (Escape, a close button).</summary>
    public void Close(bool restoreFocus = true)
    {
        if (!IsOpen) return;
        _popup.IsOpen = false;
        if (restoreFocus && _anchor.IsVisible) _anchor.Focus();
        Closed?.Invoke();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!IsOpen || e.Key != Key.Escape) return;
        e.Handled = true;
        Close(restoreFocus: true);
    }

    private static bool IsWithin(DependencyObject d, DependencyObject ancestor)
    {
        for (var x = d; x != null; x = x is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (ReferenceEquals(x, ancestor)) return true;
        return false;
    }
}
