using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Orchestration;

namespace InterviewAssistant.Tests;

/// <summary>
/// Deterministic live-interview simulator on a virtual clock with REALISTIC server-VAD timing:
///  - speech_started is delivered <see cref="OnsetMs"/> after the interviewer actually starts speaking;
///  - a pause shorter than the VAD silence window (<see cref="VadSilenceMs"/>) does not split the segment;
///  - speech_stopped is delivered VadSilenceMs after the real silence began, the transcript <see cref="TranscriptMs"/> later.
/// This reproduces the production bug (a 0.5–1 s pause inside one question used to create two questions).
/// Background answer tasks get real time to run whenever an answer is in flight, so results are deterministic.
/// </summary>
public sealed class InterviewSimulator
{
    public ManualClock Clock { get; } = new();
    public FakeTranscriber T { get; } = new();
    public FakeProvider P { get; } = new();
    public InterviewEngine E { get; }
    public int OnsetMs { get; set; } = 250;
    public int VadSilenceMs { get; set; } = 400;
    public int TranscriptMs { get; set; } = 300;
    public int MsPerWord { get; set; } = 330;
    public int AudioFrameEveryMs { get; set; } = 100;

    private readonly List<AnswerView> _started = new(), _completed = new();
    private readonly SortedList<long, List<Action>> _events = new();
    private int _item;
    // What is audible at each moment (virtual ms → kind), so the engine receives realistic audio frames.
    private readonly List<(long From, long To, SoundKind Kind)> _sounds = new();
    private static readonly byte[][] SpeechFrames = SyntheticAudio.Frames(SyntheticAudio.Speech(2000, 135)).ToArray();
    private static readonly byte[][] BeepFrames = SyntheticAudio.Frames(SyntheticAudio.Beep(600, 880)).ToArray();
    private static readonly byte[][] ShutterFrames = SyntheticAudio.Frames(SyntheticAudio.Concat(SyntheticAudio.Shutter(), SyntheticAudio.Silence(250))).ToArray();
    private static readonly byte[][] ChordFrames = SyntheticAudio.Frames(SyntheticAudio.Chord(2000)).ToArray();
    private static readonly byte[] SilenceFrame = new byte[4800];
    public enum SoundKind { Speech, Beep, Shutter, Music }
    /// <summary>False = the old behaviour (digital silence frames only), for tests about timing without acoustics.</summary>
    public bool SendAudio { get; set; } = true;
    public IReadOnlyList<AnswerView> Started { get { lock (_started) return _started.ToList(); } }
    public IReadOnlyList<AnswerView> Completed { get { lock (_completed) return _completed.ToList(); } }
    /// <summary>Completed answers that are still on screen (not replaced by a continuation of the same question).</summary>
    public IReadOnlyList<AnswerView> Visible => Completed.Where(a => !a.Superseded).ToList();

    public InterviewSimulator(KnowledgeBase? kb = null, bool withProvider = true, EngineOptions? options = null, Func<ManualClock, InterviewAssistant.Core.Providers.IAnswerProvider>? provider = null)
    {
        var p = provider?.Invoke(Clock) ?? (withProvider ? P : null);
        E = new InterviewEngine(kb ?? TestKnowledge.Load(), T, p, options ?? new EngineOptions { AutoTick = false }, Clock);
        E.AnswerStarted += a => { lock (_started) _started.Add(a); };
        E.AnswerCompleted += a => { lock (_completed) _completed.Add(a); };
    }

    public Task StartAsync() => E.StartListeningAsync();

    private int InFlight { get { lock (_started) lock (_completed) return _started.Count - _completed.Count; } }

    private void At(long ms, Action a)
    {
        if (!_events.TryGetValue(ms, out var l)) _events[ms] = l = new List<Action>();
        l.Add(a);
    }

    /// <summary>
    /// Schedules an interviewer turn starting now: parts separated by real pauses (ms). Returns when all events are
    /// scheduled (call <see cref="RunAsync"/> to play them).
    /// </summary>
    public void Say(params object[] partsAndPauses)
    {
        var parts = new List<(string Text, int PauseAfter)>();
        for (int i = 0; i < partsAndPauses.Length; i++)
            if (partsAndPauses[i] is string text)
                parts.Add((text, i + 1 < partsAndPauses.Length && partsAndPauses[i + 1] is int p ? p : 0));
        long t = Clock.NowMs;
        string pending = "";
        for (int i = 0; i < parts.Count; i++)
        {
            var (text, pause) = parts[i];
            if (pending.Length == 0)
                At(t + OnsetMs, () => T.Start());
            pending = pending.Length == 0 ? text : pending + " " + text;
            var dur = Math.Max(600, text.Split(' ').Length * MsPerWord);
            _sounds.Add((t, t + dur, SoundKind.Speech));
            t += dur;
            bool last = i == parts.Count - 1;
            if (!last && pause < VadSilenceMs) { t += pause; continue; }   // VAD does not split: same segment
            var segment = pending;
            var id = "item" + (++_item);
            At(t + VadSilenceMs, () => T.Stop());
            At(t + VadSilenceMs + TranscriptMs, () => T.Complete(segment, id));
            pending = "";
            t += pause;
        }
    }

    /// <summary>
    /// A non-speech sound (beep, chime, screenshot shutter, music) that the server VAD nevertheless segments and the
    /// recognizer "transcribes" as <paramref name="hallucination"/> (e.g. its own vocabulary prompt). Null = no text.
    /// </summary>
    public void Sound(SoundKind kind, string? hallucination, int durationMs = 600)
    {
        long t = Clock.NowMs;
        _sounds.Add((t, t + durationMs, kind));
        var id = "item" + (++_item);
        At(t + OnsetMs, () => T.Start());
        At(t + durationMs + VadSilenceMs, () => T.Stop());
        At(t + durationMs + VadSilenceMs + TranscriptMs, () => T.Complete(hallucination ?? "", id));
    }

    private byte[] FrameAt(long now)
    {
        if (!SendAudio) return SilenceFrame;
        foreach (var (from, to, kind) in _sounds)
        {
            if (now < from || now >= to) continue;
            var idx = (int)((now - from) / 100);
            return kind switch
            {
                SoundKind.Speech => SpeechFrames[idx % SpeechFrames.Length],
                SoundKind.Beep => BeepFrames[Math.Min(idx, BeepFrames.Length - 1)],
                SoundKind.Shutter => ShutterFrames[Math.Min(idx, ShutterFrames.Length - 1)],
                _ => ChordFrames[idx % ChordFrames.Length],
            };
        }
        return SilenceFrame;
    }

    /// <summary>Plays scheduled events and advances virtual time by <paramref name="ms"/>.</summary>
    public async Task RunAsync(int ms)
    {
        var end = Clock.NowMs + ms;
        while (Clock.NowMs < end)
        {
            Clock.Advance(20);
            var now = Clock.NowMs;
            while (_events.Count > 0 && _events.Keys[0] <= now)
            {
                var actions = _events.Values[0];
                _events.RemoveAt(0);
                foreach (var a in actions) a();
            }
            if (now % AudioFrameEveryMs == 0)
            {
                var f = FrameAt(now);
                E.OnAudioFrame(f, ReferenceEquals(f, SilenceFrame) ? -100 : -30);
                _sounds.RemoveAll(x => x.To < now - 60_000);
            }
            E.Tick();
            // Let background answer work (hold release, streaming) run in real time while an answer is in flight.
            if (InFlight > 0) await Task.Delay(1);
        }
    }

    /// <summary>Waits (virtual + real time) until no answer is in flight.</summary>
    public async Task SettleAsync(int maxMs = 10_000)
    {
        for (int i = 0; i < maxMs / 20 && (InFlight > 0 || _events.Count > 0); i++) await RunAsync(20);
        for (int i = 0; i < 200 && InFlight > 0; i++) await Task.Delay(5);
    }
}
