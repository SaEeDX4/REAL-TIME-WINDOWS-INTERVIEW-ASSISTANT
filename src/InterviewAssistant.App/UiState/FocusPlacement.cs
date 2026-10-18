namespace InterviewAssistant.App.UiState;

/// <summary>Where Interview Focus puts the Live window: close to a webcam at the top of the screen.</summary>
public enum FocusPreset { TopCentre, TopLeft, TopRight }

/// <summary>A rectangle in logical pixels of one monitor (left/top may be negative on monitors left of or above the primary).</summary>
public readonly record struct PlacementRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public bool Contains(PlacementRect r, double tolerance = 0.5) =>
        r.Left >= Left - tolerance && r.Top >= Top - tolerance && r.Right <= Right + tolerance && r.Bottom <= Bottom + tolerance;
}

/// <summary>
/// Interview Focus geometry (no WPF types: unit-tested). The window gets a recommended size (~560×360) that is never
/// larger than 40 % of the monitor's work area in either direction — except that it never goes below the window's own
/// minimum size — and is placed at the top edge (centre, left or right), fully inside the work area. Works for any
/// monitor origin (negative coordinates) and any DPI: callers pass the work area in the monitor's logical pixels.
/// </summary>
public static class FocusPlacement
{
    public const double RecommendedWidth = 560;
    public const double RecommendedHeight = 360;
    public const double MaxShare = 0.40;
    /// <summary>Gap to the left/right screen edge for the corner presets (the window's own 8 px shadow margin adds to it).</summary>
    public const double EdgeGap = 8;

    public static PlacementRect Compute(PlacementRect work, FocusPreset preset, double minWidth = LiveChrome.MinWidth, double minHeight = 260)
    {
        var width = Math.Min(RecommendedWidth, work.Width * MaxShare);
        var height = Math.Min(RecommendedHeight, work.Height * MaxShare);
        // the window cannot be smaller than its minimum size, and never larger than the work area itself
        width = Math.Min(Math.Max(width, minWidth), work.Width);
        height = Math.Min(Math.Max(height, minHeight), work.Height);
        var left = preset switch
        {
            FocusPreset.TopLeft => work.Left + EdgeGap,
            FocusPreset.TopRight => work.Right - width - EdgeGap,
            _ => work.Left + (work.Width - width) / 2,
        };
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        var top = work.Top;   // right under the camera: the top edge of the work area
        return new PlacementRect(Math.Round(left), Math.Round(top), Math.Floor(width), Math.Floor(height));
    }

    /// <summary>Localization key of the preset's label.</summary>
    public static string Key(FocusPreset p) => p switch
    {
        FocusPreset.TopLeft => "focus_top_left",
        FocusPreset.TopRight => "focus_top_right",
        _ => "focus_top_centre",
    };

    /// <summary>The stored preset (settings), Top centre when none or unknown.</summary>
    public static FocusPreset Parse(string? stored) => Enum.TryParse<FocusPreset>(stored, ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : FocusPreset.TopCentre;
}
