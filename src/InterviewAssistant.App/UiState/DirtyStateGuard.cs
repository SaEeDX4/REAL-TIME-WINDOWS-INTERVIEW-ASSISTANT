namespace InterviewAssistant.App.UiState;

/// <summary>Why the UI is about to replace what is on screen.</summary>
public enum ChangeReason
{
    // ---- real data-loss actions: unsaved edits would be thrown away ----
    /// <summary>Leaving the page (another sidebar item, "Start interview", a deep link).</summary>
    Navigate,
    /// <summary>Selecting another item in the same page (another profile, another interview).</summary>
    SwitchItem,
    /// <summary>Closing the window.</summary>
    CloseWindow,
    /// <summary>Signing out or switching account (the workspace is closed).</summary>
    SignOut,
    /// <summary>Reloading the edited item from storage (e.g. after importing another document into it).</summary>
    ReloadData,

    // ---- presentation refreshes: never data loss, never a prompt ----
    /// <summary>The UI language changed (texts re-render; drafts, selection and dirty state stay).</summary>
    LanguageChange,
    /// <summary>The window was resized, maximized or moved to another monitor/DPI.</summary>
    Resize,
    /// <summary>Theme or high-contrast change.</summary>
    ThemeChange,
    /// <summary>Resources or account data refreshed in the background.</summary>
    ResourceRefresh,
    /// <summary>A list was rebuilt and re-selected the item that is already being edited.</summary>
    ListRefresh,
}

/// <summary>What the user chose when asked about unsaved changes.</summary>
public enum LeaveDecision { Save, Discard, Stay }

/// <summary>
/// Dirty state of one editor (a profile, an interview, the Settings page). It only tracks edits made by the user:
/// programmatic field updates run inside <see cref="Suspend"/>, so loading a record or re-rendering texts after a
/// language switch never marks the editor dirty and never clears it either. Plain class (no WPF) so it is unit-tested.
/// </summary>
public sealed class DirtyStateGuard
{
    private int _suspended;

    public DirtyStateGuard(string name) => Name = name;

    /// <summary>Diagnostic name ("profile", "interview", "settings").</summary>
    public string Name { get; }
    public bool IsDirty { get; private set; }
    public bool IsSuspended => _suspended > 0;
    /// <summary>Raised when <see cref="IsDirty"/> flips (the page shows or hides its "Unsaved changes" indicator).</summary>
    public event Action? DirtyChanged;

    /// <summary>Saves the pending edits. Returns false when validation failed (the user stays on the page).</summary>
    public Func<bool>? Save { get; set; }
    /// <summary>Reverts side effects of the pending edits (e.g. a live audio-device preview) when the user discards them.</summary>
    public Action? Discard { get; set; }

    /// <summary>Called from the editor's change handlers. Ignored while a programmatic update is running.</summary>
    public void MarkDirty()
    {
        if (_suspended > 0 || IsDirty) return;
        IsDirty = true;
        DirtyChanged?.Invoke();
    }

    /// <summary>After a successful save, a load of another record, or an explicit discard.</summary>
    public void MarkClean()
    {
        if (!IsDirty) return;
        IsDirty = false;
        DirtyChanged?.Invoke();
    }

    /// <summary>Programmatic updates inside the returned scope never change the dirty state.</summary>
    public IDisposable Suspend()
    {
        _suspended++;
        return new Scope(this);
    }

    /// <summary>True for actions that would throw away unsaved edits.</summary>
    public static bool IsDataLoss(ChangeReason reason) =>
        reason is ChangeReason.Navigate or ChangeReason.SwitchItem or ChangeReason.CloseWindow or ChangeReason.SignOut or ChangeReason.ReloadData;

    /// <summary>The user must be asked only when there are unsaved edits AND the action would lose them.</summary>
    public bool RequiresConfirmation(ChangeReason reason) => IsDirty && IsDataLoss(reason);

    /// <summary>
    /// A list selection change is a real switch only when the user picked a different item. Rebuilding the list (for
    /// example to re-render its labels in another language) and re-selecting the edited item is not.
    /// </summary>
    public static ChangeReason ClassifySelection(string? editingId, string? selectedId, bool programmatic) =>
        programmatic || string.Equals(editingId, selectedId, StringComparison.Ordinal) ? ChangeReason.ListRefresh : ChangeReason.SwitchItem;

    private sealed class Scope(DirtyStateGuard owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._suspended--;
        }
    }
}

/// <summary>
/// The window-wide guard: before a data-loss action it asks about every affected editor with unsaved edits (Save /
/// Discard / Stay) through <c>ask</c> (the in-app dialog in the app, a stub in tests). Presentation refreshes pass
/// straight through without asking.
/// </summary>
public sealed class NavigationGuard
{
    private readonly List<DirtyStateGuard> _guards = new();
    private readonly Func<DirtyStateGuard, ChangeReason, Task<LeaveDecision>> _ask;
    private bool _asking;

    public NavigationGuard(Func<DirtyStateGuard, ChangeReason, Task<LeaveDecision>> ask) => _ask = ask;

    public IReadOnlyList<DirtyStateGuard> Guards => _guards;
    public bool AnyDirty => _guards.Any(g => g.IsDirty);

    public DirtyStateGuard Register(DirtyStateGuard guard)
    {
        _guards.Add(guard);
        return guard;
    }

    /// <summary>
    /// True when the action may proceed. <paramref name="scope"/> limits the question to the editors the action affects
    /// (switching profile only concerns the profile editor); empty = every registered editor.
    /// </summary>
    public async Task<bool> CanProceedAsync(ChangeReason reason, params DirtyStateGuard[] scope)
    {
        if (!DirtyStateGuard.IsDataLoss(reason)) return true;
        if (_asking) return false;   // a question is already on screen: never stack a second one
        var affected = (scope.Length > 0 ? scope : _guards.ToArray()).Where(g => g.RequiresConfirmation(reason)).ToList();
        if (affected.Count == 0) return true;
        _asking = true;
        try
        {
            // Ask about every affected editor first; nothing is saved or discarded unless the user lets the action go
            // ahead for all of them ("Stay" on the second must not leave the first already discarded).
            var decisions = new List<(DirtyStateGuard Guard, LeaveDecision Decision)>();
            foreach (var g in affected)
            {
                var decision = await _ask(g, reason);
                if (decision == LeaveDecision.Stay) return false;
                decisions.Add((g, decision));
            }
            foreach (var (g, decision) in decisions)
            {
                if (decision == LeaveDecision.Save)
                {
                    if (g.Save == null || !g.Save()) return false;
                    g.MarkClean();
                }
                else
                {
                    g.Discard?.Invoke();
                    g.MarkClean();
                }
            }
            return true;
        }
        finally { _asking = false; }
    }
}
