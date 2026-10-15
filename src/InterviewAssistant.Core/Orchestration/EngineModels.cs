using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Orchestration;

/// <summary>
/// Live states. <see cref="Quiet"/> is normal silence (neutral); <see cref="NoAudio"/> is a one-time warning when nothing
/// has been heard since listening started (likely the wrong playback device).
/// </summary>
public enum EngineStatus { Ready, Listening, SpeechDetected, Finalizing, Answering, Paused, Reconnecting, NoAudio, ApiError, Stopped, Quiet, AnswerReady }

/// <summary><see cref="Guidance"/>: deterministic coaching shown instead of an invented personal answer.</summary>
public enum AnswerSource { Cache, Llm, CacheFallback, Error, Guidance }

/// <summary>Why an answer exists: a heard question, a typed question, or a rework of an existing answer.</summary>
public enum AnswerKind { Live, Typed, Variant }

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
    /// <summary>Set when this answer replaces an earlier one for the same question (multi-part continuation/restatement).</summary>
    public int? ReplacesAnswerId { get; init; }
    /// <summary>Superseded by a continuation of the same question: its output is discarded.</summary>
    public bool Superseded { get; internal set; }
    /// <summary>Interviewer turn this answer belongs to (0 = typed question or variant).</summary>
    public int TurnId { get; init; }
    /// <summary>Error category when the answer failed (null on success).</summary>
    public Providers.ProviderErrorKind? Error { get; set; }
    internal Live.QuestionRecord? Record { get; set; }
    internal bool BridgePending { get; set; }
    /// <summary>Live question, typed question, or a variant (rework) of an existing answer.</summary>
    public AnswerKind Kind { get; init; }
    /// <summary>Short interviewer context before the actual ask (empty for a direct question).</summary>
    public string ContextSummary { get; init; } = "";
    /// <summary>The full interviewer turn the question was extracted from.</summary>
    public string OriginalText { get; init; } = "";
    public IReadOnlyList<string> SubQuestions { get; init; } = Array.Empty<string>();
    public Intelligence.AnswerConfidence Confidence { get; set; } = Intelligence.AnswerConfidence.Medium;
    public Intelligence.AnswerPlan? Plan { get; set; }
    internal Intelligence.ExtractedQuestion? Extracted { get; init; }
    /// <summary>Clears bullets (a variant re-generates the content of the same history item).</summary>
    internal void ClearBullets() { lock (Bullets) Bullets.Clear(); }
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
    /// <summary>
    /// After a live question is finalized, an AI request (not a prepared answer) waits this long for the interviewer
    /// to continue the question; if they do, the request is never sent and the merged question is answered instead.
    /// Hides most of the cost of multi-part questions with ~1 s pauses at a small latency cost.
    /// </summary>
    public int ContinuationHoldMs { get; set; } = 300;
    /// <summary>Seconds of silence before the neutral "Listening · quiet" state.</summary>
    public int QuietAfterSeconds { get; set; } = 6;
    /// <summary>Questions kept in the navigable history (the whole interview; old answers are small).</summary>
    public int HistoryCapacity { get; set; } = 500;
    /// <summary>Acoustic evidence window opens this long before the server's speech_started (onset lag + VAD prefix).</summary>
    public int EvidenceLeadMs { get; set; } = 1000;
    /// <summary>Minimum interval between live-transcript UI updates (long monologues never flood the UI thread).</summary>
    public int LiveTextIntervalMs { get; set; } = 120;
    /// <summary>Characters of the live transcript shown (tail).</summary>
    public int LiveTextMaxChars { get; set; } = 360;
    /// <summary>Developer own-key mode: credential errors point to Settings instead of sign-in.</summary>
    public bool DeveloperKeyMode { get; set; } = true;
}
