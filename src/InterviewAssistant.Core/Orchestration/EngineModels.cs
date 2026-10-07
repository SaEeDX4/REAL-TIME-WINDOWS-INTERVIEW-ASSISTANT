using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Orchestration;

public enum EngineStatus { Ready, Listening, SpeechDetected, Finalizing, Answering, Paused, Reconnecting, NoAudio, ApiError, Stopped }

public enum AnswerSource { Cache, Llm, CacheFallback, Error }

/// <summary>An answer being displayed. Bullets are append-only (stable under the reader's eyes).</summary>
public sealed class AnswerView
{
    public required int Id { get; init; }
    public required string Question { get; init; }
    public required AnswerStyle Style { get; init; }
    public AnswerSource Source { get; set; }
    public AnswerMode Mode { get; set; }
    public string Category { get; set; } = "";
    public string? MatchedQuestionId { get; set; }
    public double MatchScore { get; set; }
    public List<string> Bullets { get; } = new();
    /// <summary>Thread-safe copy (engine appends on a background thread while the UI reads).</summary>
    public IReadOnlyList<string> SnapshotBullets() { lock (Bullets) return Bullets.ToArray(); }
    internal void AppendBullet(string b) { lock (Bullets) Bullets.Add(b); }
    public bool IsComplete { get; set; }
    public string? Note { get; set; }
    public List<string> ValidationFlags { get; } = new();
    public int QuestionIndex { get; set; }
    public string DetectedLanguage { get; set; } = "und";
    public string AnswerLanguage { get; set; } = "en";
    public Live.Presentation Presentation { get; set; }
    public Live.CoachOutput? Coach { get; set; }
    public Live.AdaptiveLevel Adaptive { get; set; }
    public List<string> StoryIds { get; } = new();
}

public sealed class EngineOptions
{
    public bool AutoTick { get; set; } = true;
    public int TickMs { get; set; } = 40;
    /// <summary>Use the prepared answer instantly when the match is High confidence.</summary>
    public bool UseFastCache { get; set; } = true;
    /// <summary>Prepared answers below this confidence are used as AI reference context, not shown instantly.</summary>
    public double MinCacheConfidence { get; set; } = 0.7;
    public AnswerStyle DefaultStyle { get; set; } = AnswerStyle.Balanced;
    /// <summary>Seconds without audio level above the floor while listening before "NO AUDIO" is shown.</summary>
    public int NoAudioWarningSeconds { get; set; } = 20;
    public int HistoryCapacity { get; set; } = 30;
}
