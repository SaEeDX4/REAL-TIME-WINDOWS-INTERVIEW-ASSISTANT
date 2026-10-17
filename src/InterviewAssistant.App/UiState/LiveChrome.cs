namespace InterviewAssistant.App.UiState;

/// <summary>How the pre-interview readiness indicator appears in the Live Assistant header.</summary>
public enum ReadinessIndicator
{
    /// <summary>A pill with the coloured dot and the state label ("Ready", "Not checked") — setup mode, room to spare.</summary>
    Label,
    /// <summary>Only the coloured dot (details in the tooltip) — during an interview, in compact view or when narrow.</summary>
    Dot,
}

/// <summary>Which parts of the Live Assistant header and body show (no WPF types: unit-tested).</summary>
public sealed record LiveChromeLayout(
    ReadinessIndicator Readiness,
    bool StartLabel,
    bool AdaptiveBadge,
    bool PresentationChip,
    bool MinutesLabel,
    bool SetupEmptyState,
    bool HomeLink)
{
    /// <summary>The Coach/Answer choice moves into the More menu when the header has no room for its chip.</summary>
    public bool PresentationInMore => !PresentationChip;
    /// <summary>The minutes-left figure moves into the More menu when the header has no room for it.</summary>
    public bool MinutesInMore => !MinutesLabel;
}

/// <summary>
/// The visibility rule of the Live Assistant chrome. Two modes: <b>setup</b> (no session) shows setup affordances —
/// the readiness pill with its label, a calm empty-state line and a link to Home; <b>interview</b> (listening or
/// paused) keeps only the interview controls — the readiness indicator collapses to a dot. Independently, the header
/// sheds optional items as the window gets narrower so it never clips at the minimum width: the primary items (status,
/// Start/Pause, Stop, readiness, help, More, window buttons) always stay; the overflow (More) takes what does not fit.
/// </summary>
public static class LiveChrome
{
    /// <summary>The Live window's minimum width (logical px): everything the rule keeps must fit here.</summary>
    public const double MinWidth = 440;
    /// <summary>From this width the Start button shows its text next to the icon.</summary>
    public const double StartLabelWidth = 580;
    /// <summary>From this width the "answers are shorter" badge shows (it only appears during an interview).</summary>
    public const double AdaptiveBadgeWidth = 640;
    /// <summary>From this width (setup mode) the readiness indicator shows its label.</summary>
    public const double ReadinessLabelWidth = 700;
    /// <summary>From this width the Coach/Answer chip shows in the header (otherwise in More).</summary>
    public const double PresentationChipWidth = 780;
    /// <summary>From this width the minutes-left label shows in the header (otherwise in More).</summary>
    public const double MinutesLabelWidth = 880;

    public static LiveChromeLayout For(bool sessionActive, bool compact, double width)
    {
        var setup = !sessionActive;
        return new LiveChromeLayout(
            Readiness: setup && !compact && width >= ReadinessLabelWidth ? ReadinessIndicator.Label : ReadinessIndicator.Dot,
            StartLabel: width >= StartLabelWidth,
            AdaptiveBadge: !compact && width >= AdaptiveBadgeWidth,
            PresentationChip: !compact && width >= PresentationChipWidth,
            MinutesLabel: !compact && width >= MinutesLabelWidth,
            SetupEmptyState: setup,
            HomeLink: setup && !compact);
    }
}
