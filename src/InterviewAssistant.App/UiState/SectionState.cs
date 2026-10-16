namespace InterviewAssistant.App.UiState;

public enum SectionStatus { NotLoaded, Loading, Ready, Failed }

/// <summary>
/// Load state of one independently loaded part of a page (Account: plan and usage, devices, billing actions). A failed
/// or timed-out request only marks its own section: other sections keep their data and status, data already shown
/// stays visible as "saved data from &lt;time&gt;", and the next successful load clears the error by itself.
/// Responses that arrive after a newer request started (or after a sign-out) are ignored. Plain class (no WPF).
/// </summary>
public sealed class SectionState
{
    private int _generation;

    public SectionState(string name) => Name = name;

    public string Name { get; }
    public SectionStatus Status { get; private set; } = SectionStatus.NotLoaded;
    /// <summary>User-facing message of the last failure (no correlation id: that goes to the log).</summary>
    public string? Error { get; private set; }
    /// <summary>When the data on screen was loaded successfully; null = no data yet.</summary>
    public DateTimeOffset? LoadedAt { get; private set; }
    public bool HasData => LoadedAt != null;
    public bool IsLoading => Status == SectionStatus.Loading;
    /// <summary>Data is on screen but the latest refresh failed: show it with a "saved data from …" note.</summary>
    public bool IsStale => Status == SectionStatus.Failed && HasData;
    public event Action? Changed;

    /// <summary>Starts a load and returns its ticket; pass it back to <see cref="Succeed"/> or <see cref="Fail"/>.</summary>
    public int Begin()
    {
        Status = SectionStatus.Loading;
        Changed?.Invoke();
        return ++_generation;
    }

    /// <summary>Returns false (and changes nothing) when a newer load started or the section was reset meanwhile.</summary>
    public bool Succeed(int ticket, DateTimeOffset now)
    {
        if (ticket != _generation) return false;
        Status = SectionStatus.Ready;
        Error = null;
        LoadedAt = now;
        Changed?.Invoke();
        return true;
    }

    public bool Fail(int ticket, string error)
    {
        if (ticket != _generation) return false;
        Status = SectionStatus.Failed;
        Error = error;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Data that arrived through another path (e.g. the account summary loaded at sign-in).</summary>
    public void Loaded(DateTimeOffset now)
    {
        _generation++;
        Status = SectionStatus.Ready;
        Error = null;
        LoadedAt = now;
        Changed?.Invoke();
    }

    /// <summary>A failure reported through another path (e.g. a background account refresh that failed).</summary>
    public void Failed(string error)
    {
        _generation++;
        Status = SectionStatus.Failed;
        Error = error;
        Changed?.Invoke();
    }

    /// <summary>Sign-out or account switch: nothing of the previous account remains, late responses are dropped.</summary>
    public void Reset()
    {
        _generation++;
        Status = SectionStatus.NotLoaded;
        Error = null;
        LoadedAt = null;
        Changed?.Invoke();
    }

    /// <summary>"Showing saved data from 14:05" when stale, else null. <paramref name="formatTime"/> renders the local time.</summary>
    public string? StaleNote(Func<DateTimeOffset, string> formatTime) =>
        IsStale ? Core.Languages.Loc.F("acct_section_stale", formatTime(LoadedAt!.Value)) : null;
}

/// <summary>The Account page's independently loaded sections.</summary>
public sealed class AccountSections
{
    public SectionState Summary { get; } = new("summary");
    public SectionState Devices { get; } = new("devices");
    public SectionState Billing { get; } = new("billing");
    public IEnumerable<SectionState> All => new[] { Summary, Devices, Billing };

    public void ResetAll()
    {
        foreach (var s in All) s.Reset();
    }
}
