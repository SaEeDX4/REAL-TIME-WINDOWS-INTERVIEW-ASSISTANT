using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App.Views;

public enum AppDialogResult { None, Primary, Secondary, Cancel }
public enum DialogTone { Info, Question, Warning, Danger, Success }
public enum DialogButtonRole { Primary, Secondary, Destructive }

/// <summary>One button of an in-app dialog.</summary>
public sealed class DialogAction
{
    public required string Text { get; init; }
    public AppDialogResult Result { get; init; } = AppDialogResult.Primary;
    public DialogButtonRole Role { get; init; } = DialogButtonRole.Secondary;
    /// <summary>Enter (outside buttons and multi-line fields) runs this action; it also receives the initial focus.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Escape runs this action.</summary>
    public bool IsCancel { get; init; }
    /// <summary>Runs before the dialog closes; false keeps it open (e.g. a validation error shown inside the dialog).
    /// While it runs every button is disabled, so a double click cannot submit twice.</summary>
    public Func<Task<bool>>? Validate { get; init; }
}

public sealed class DialogRequest
{
    public string? Title { get; init; }
    public string? Message { get; init; }
    public DialogTone Tone { get; init; } = DialogTone.Info;
    /// <summary>Optional form content below the message (fact editor, password prompts).</summary>
    public UIElement? Body { get; init; }
    public required IReadOnlyList<DialogAction> Actions { get; init; }
    /// <summary>Element focused when the dialog opens; default: the default action's button.</summary>
    public IInputElement? InitialFocus { get; init; }
    public double MaxWidth { get; init; } = 460;
}

/// <summary>
/// In-app modal dialogs (replaces the native MessageBox): a dimmed backdrop over the owning window's content and a
/// card with icon, title, message and a clear button hierarchy (primary / secondary / destructive). Awaitable, keyboard
/// complete (focus stays inside the dialog, Tab cycles, Enter = default action, Escape = cancel), localized buttons,
/// right-to-left aware (inherits the window's FlowDirection) and announced as a dialog to assistive technology.
/// Destructive confirmations default to Cancel.
/// </summary>
public static class AppDialog
{
    public static Task<AppDialogResult> ShowAsync(Window owner, DialogRequest request) => DialogHost.For(owner).ShowAsync(request);

    /// <summary>A message with a single OK button.</summary>
    public static async Task AlertAsync(Window owner, string message, DialogTone tone = DialogTone.Info, string? title = null) =>
        await ShowAsync(owner, new DialogRequest
        {
            Title = title, Message = message, Tone = tone,
            Actions = new[] { new DialogAction { Text = Loc.T("dlg_ok"), Role = DialogButtonRole.Primary, IsDefault = true, IsCancel = true } },
        });

    /// <summary>
    /// Yes/no question. Non-destructive: the confirm button is the default. Destructive (delete, sign out during an
    /// interview…): the confirm button is red and Cancel is the default, so Enter never destroys anything by accident.
    /// </summary>
    public static async Task<bool> ConfirmAsync(Window owner, string message, string confirmText, bool destructive = false, string? title = null)
    {
        var result = await ShowAsync(owner, new DialogRequest
        {
            Title = title, Message = message, Tone = destructive ? DialogTone.Danger : DialogTone.Question,
            Actions = new[]
            {
                new DialogAction { Text = Loc.T("dlg_cancel"), Result = AppDialogResult.Cancel, Role = DialogButtonRole.Secondary, IsCancel = true, IsDefault = destructive },
                new DialogAction { Text = confirmText, Result = AppDialogResult.Primary, Role = destructive ? DialogButtonRole.Destructive : DialogButtonRole.Primary, IsDefault = !destructive },
            },
        });
        return result == AppDialogResult.Primary;
    }

    /// <summary>Unsaved changes: Save (default) / Discard changes / Cancel (= stay).</summary>
    public static async Task<LeaveDecision> AskUnsavedAsync(Window owner, string message)
    {
        var result = await ShowAsync(owner, new DialogRequest
        {
            Title = Loc.T("dlg_unsaved_title"), Message = message, Tone = DialogTone.Warning,
            Actions = new[]
            {
                new DialogAction { Text = Loc.T("dlg_cancel"), Result = AppDialogResult.Cancel, IsCancel = true },
                new DialogAction { Text = Loc.T("dlg_discard"), Result = AppDialogResult.Secondary, Role = DialogButtonRole.Destructive },
                new DialogAction { Text = Loc.T("dlg_save"), Result = AppDialogResult.Primary, Role = DialogButtonRole.Primary, IsDefault = true },
            },
        });
        return result switch { AppDialogResult.Primary => LeaveDecision.Save, AppDialogResult.Secondary => LeaveDecision.Discard, _ => LeaveDecision.Stay };
    }

    /// <summary>True while a dialog is open in <paramref name="owner"/> (closing the window then only closes the dialog first).</summary>
    public static bool IsOpen(Window owner) => DialogHost.Find(owner)?.IsOpen == true;
}

/// <summary>
/// Overlay host placed once in a window's root grid (spanning all rows/columns, top-most Z order). Dialogs stack: the
/// newest is on top and owns the keyboard.
/// </summary>
public sealed class DialogHost : Grid
{
    private sealed class Layer
    {
        public required Grid Root { get; init; }
        public required Border Card { get; init; }
        public required TaskCompletionSource<AppDialogResult> Done { get; init; }
        public required Dictionary<Button, DialogAction> Buttons { get; init; }
        public IInputElement? PreviousFocus { get; init; }
        public bool Busy { get; set; }
    }

    private readonly List<Layer> _layers = new();
    private Window? _window;

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(DialogHost),
        new FrameworkPropertyMetadata(new CornerRadius(0)));
    /// <summary>Corner radius of the backdrop (matches the window's rounded content border).</summary>
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public bool IsOpen => _layers.Count > 0;
    /// <summary>The card of the dialog on top (self-test: keys are sent through it like a real key press).</summary>
    internal UIElement? TopCard => _layers.Count > 0 ? _layers[^1].Card : null;

    public DialogHost()
    {
        Visibility = Visibility.Collapsed;
        Panel.SetZIndex(this, 100);
        Loaded += (_, _) => Attach(Window.GetWindow(this));
        SizeChanged += (_, _) => { foreach (var l in _layers) l.Card.MaxHeight = Math.Max(160, ActualHeight - 48); };
    }

    public static DialogHost? Find(Window owner) => FindIn(owner);

    /// <summary>The window's host; windows without one in XAML get one added to their root grid.</summary>
    public static DialogHost For(Window owner)
    {
        if (FindIn(owner) is { } host) return host;
        if (owner.Content is not Panel root) throw new InvalidOperationException("The window has no panel to host in-app dialogs.");
        host = new DialogHost();
        if (root is Grid g)
        {
            Grid.SetRowSpan(host, Math.Max(1, g.RowDefinitions.Count));
            Grid.SetColumnSpan(host, Math.Max(1, g.ColumnDefinitions.Count));
        }
        root.Children.Add(host);
        host.Attach(owner);
        return host;
    }

    private static DialogHost? FindIn(DependencyObject root)
    {
        if (root is DialogHost h) return h;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d && FindIn(d) is { } found) return found;
        return null;
    }

    private void Attach(Window? w)
    {
        if (w == null || _window == w) return;
        _window = w;
        // Focus trap: while a dialog is open, keyboard focus cannot leave its card (mouse input is blocked by the backdrop).
        w.PreviewGotKeyboardFocus += (_, e) =>
        {
            if (_layers.Count == 0 || e.NewFocus is not DependencyObject target || target is Window) return;
            var top = _layers[^1];
            if (IsInside(top.Card, target)) return;
            e.Handled = true;
            Dispatcher.BeginInvoke(() => FocusFirst(top), System.Windows.Threading.DispatcherPriority.Input);
        };
        // A closing window completes every pending dialog (callers awaiting a result are never left hanging).
        w.Closed += (_, _) => { _ownerClosed = true; foreach (var l in _layers.ToList()) Complete(l, AppDialogResult.Cancel); };
    }

    private static bool IsInside(DependencyObject ancestor, DependencyObject node)
    {
        for (DependencyObject? d = node; d != null; d = Parent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
        // Popups (ComboBox drop-downs) have their own visual tree: continue through the logical parent.
        static DependencyObject? Parent(DependencyObject d) =>
            (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);
    }

    private bool _ownerClosed;

    public Task<AppDialogResult> ShowAsync(DialogRequest r)
    {
        // a late caller (a finished network wait, a timer) asking a closed window: answer Cancel, never wait forever
        if (_ownerClosed) return Task.FromResult(AppDialogResult.Cancel);
        var res = Application.Current.Resources;
        var done = new TaskCompletionSource<AppDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var buttons = new Dictionary<Button, DialogAction>();

        // ---- card content ----
        var (glyph, brushKey) = r.Tone switch
        {
            DialogTone.Question => ("", "B.Accent"),
            DialogTone.Warning => ("", "B.Thinking"),
            DialogTone.Danger => ("", "B.Error"),
            DialogTone.Success => ("", "B.Listening"),
            _ => ("", "B.Accent"),
        };
        var accent = ((SolidColorBrush)res[brushKey]).Color;
        var icon = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B)),
            Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)res["F.Icons"], FontSize = 15, Foreground = new SolidColorBrush(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var texts = new StackPanel();
        if (!string.IsNullOrWhiteSpace(r.Title))
        {
            var title = new TextBlock { Text = r.Title, FontFamily = (FontFamily)res["F.Display"], FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)res["B.Text"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
            AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
            texts.Children.Add(title);
        }
        if (!string.IsNullOrWhiteSpace(r.Message))
        {
            var noTitle = string.IsNullOrWhiteSpace(r.Title);
            texts.Children.Add(new TextBlock
            {
                Text = r.Message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res[noTitle ? "B.Text" : "B.TextDim"],
                FontSize = noTitle ? 14 : 13, Margin = new Thickness(0, noTitle ? 5 : 0, 0, 0), LineHeight = noTitle ? 20 : 18,
            });
        }
        var header = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon); header.Children.Add(texts);
        var content = new StackPanel();
        content.Children.Add(header);
        if (r.Body != null) content.Children.Add(new Border { Child = r.Body, Margin = new Thickness(0, 10, 0, 0) });
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 4, 0) };

        // ---- buttons: reading order, the primary/destructive action last (right in LTR, left in RTL) ----
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        Layer? layer = null;
        foreach (var a in r.Actions)
        {
            var b = new Button
            {
                Content = a.Text, MinWidth = 88, Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)res[a.Role switch { DialogButtonRole.Primary => "Btn.FormPrimary", DialogButtonRole.Destructive => "Btn.FormDanger", _ => "Btn.Form" }],
            };
            AutomationProperties.SetName(b, a.Text);
            b.Click += async (_, _) => { if (layer != null) await RunAsync(layer, a); };
            buttons[b] = a;
            row.Children.Add(b);
        }
        var panel = new DockPanel();
        DockPanel.SetDock(row, Dock.Bottom);
        panel.Children.Add(row); panel.Children.Add(scroll);

        var card = new Border
        {
            Child = panel, Background = (Brush)res["B.Surface"], BorderBrush = (Brush)res["B.Border"], BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)res["R.Card"], Padding = new Thickness(22, 20, 22, 18), MaxWidth = r.MaxWidth, MinWidth = Math.Min(320, r.MaxWidth),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24),
            Effect = res["E.Shadow"] as System.Windows.Media.Effects.Effect, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.97, 0.97),
        };
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(card, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(card, KeyboardNavigationMode.Contained);
        AutomationProperties.SetName(card, r.Title ?? r.Message ?? "");
        if (r.Title != null && r.Message != null) AutomationProperties.SetHelpText(card, r.Message);
        AutomationProperties.SetIsDialog(card, true);

        var backdrop = new Border { Background = (Brush)res["B.Backdrop"], Opacity = 0 };
        backdrop.SetBinding(Border.CornerRadiusProperty, new System.Windows.Data.Binding(nameof(CornerRadius)) { Source = this });
        backdrop.MouseDown += (_, e) => e.Handled = true;   // modal: clicks never reach the window behind
        var root = new Grid();
        root.Children.Add(backdrop); root.Children.Add(card);
        card.MaxHeight = Math.Max(160, (ActualHeight > 0 ? ActualHeight : 600) - 48);

        layer = new Layer { Root = root, Card = card, Done = done, Buttons = buttons, PreviousFocus = Keyboard.FocusedElement };
        card.PreviewKeyDown += async (_, e) => await OnKeyAsync(layer, e);
        _layers.Add(layer);
        Children.Add(root);
        Visibility = Visibility.Visible;

        var fade = TimeSpan.FromMilliseconds(140);
        backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, fade));
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, fade));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, fade) { EasingFunction = ease });
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, fade) { EasingFunction = ease });

        Dispatcher.BeginInvoke(() =>
        {
            if (!_layers.Contains(layer)) return;
            if (r.InitialFocus is { } f && f.Focusable) { f.Focus(); return; }
            FocusFirst(layer);
        }, System.Windows.Threading.DispatcherPriority.Input);
        return done.Task;
    }

    private static void FocusFirst(Layer layer)
    {
        var def = layer.Buttons.FirstOrDefault(kv => kv.Value.IsDefault).Key;
        if (def is { IsEnabled: true }) { def.Focus(); return; }
        layer.Card.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private async Task OnKeyAsync(Layer layer, KeyEventArgs e)
    {
        if (_layers.Count == 0 || _layers[^1] != layer) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (layer.Busy) return;
            var cancel = layer.Buttons.Values.FirstOrDefault(a => a.IsCancel) ?? (layer.Buttons.Count == 1 ? layer.Buttons.Values.First() : null);
            if (cancel != null) Complete(layer, cancel.Result);
            return;
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (Keyboard.FocusedElement)
        {
            case TextBox { AcceptsReturn: true }: return;                  // new line in a multi-line field
            case ComboBox { IsDropDownOpen: true }: return;                // picks the highlighted item
            case Button b when layer.Buttons.TryGetValue(b, out var focused):  // Enter on a button = that button
                e.Handled = true;
                await RunAsync(layer, focused);
                return;
        }
        var def = layer.Buttons.Values.FirstOrDefault(a => a.IsDefault);
        if (def == null) return;
        e.Handled = true;
        await RunAsync(layer, def);
    }

    private async Task RunAsync(Layer layer, DialogAction action)
    {
        if (layer.Busy || layer.Done.Task.IsCompleted) return;
        if (action.Validate != null && !action.IsCancel)
        {
            layer.Busy = true;
            foreach (var b in layer.Buttons.Keys) b.IsEnabled = false;
            layer.Card.Cursor = Cursors.Wait;
            bool ok;
            try { ok = await action.Validate(); }
            finally
            {
                layer.Busy = false;
                foreach (var b in layer.Buttons.Keys) b.IsEnabled = true;
                layer.Card.Cursor = null;
            }
            if (!ok) return;
        }
        Complete(layer, action.Result);
    }

    private void Complete(Layer layer, AppDialogResult result)
    {
        if (!_layers.Remove(layer)) return;
        Children.Remove(layer.Root);
        if (_layers.Count == 0) Visibility = Visibility.Collapsed;
        layer.Done.TrySetResult(result);
        // Focus returns to where it was (the button that opened the dialog), or into the dialog below.
        if (_layers.Count > 0) FocusFirst(_layers[^1]);
        else if (layer.PreviousFocus is UIElement { IsVisible: true, IsEnabled: true } prev) prev.Focus();
    }
}
