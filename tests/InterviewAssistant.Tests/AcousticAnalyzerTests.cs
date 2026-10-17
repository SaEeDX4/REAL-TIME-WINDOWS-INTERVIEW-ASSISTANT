using InterviewAssistant.Core.Audio;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

public class AcousticAnalyzerTests
{
    private readonly ITestOutputHelper _out;
    public AcousticAnalyzerTests(ITestOutputHelper output) => _out = output;

    private FrameAcoustics Sum(double[] signal)
    {
        var total = default(FrameAcoustics);
        foreach (var f in SyntheticAudio.Frames(signal)) total += AcousticAnalyzer.Analyze(f);
        _out.WriteLine($"{total}");
        return total;
    }

    [Theory]
    [InlineData(95)]
    [InlineData(130)]
    [InlineData(210)]
    [InlineData(240)]
    public void Speech_IsVoiced_AndNotTonal(double pitch)
    {
        var a = Sum(SyntheticAudio.Speech(2000, pitch));
        Assert.True(a.VoicedMs >= 1000, $"voiced {a.VoicedMs}");
        Assert.True(a.TonalMs <= a.DurationMs / 5, $"tonal {a.TonalMs}");   // occasional single-harmonic windows are fine
    }

    [Fact]
    public void ShortWhy_HasEnoughVoicedEvidence()
    {
        var a = Sum(SyntheticAudio.Concat(SyntheticAudio.Silence(200), SyntheticAudio.Speech(320, 160, seed: 7), SyntheticAudio.Silence(300)));
        Assert.True(a.VoicedMs >= SpeechEvidence.MinVoicedMs, $"voiced {a.VoicedMs}");
    }

    [Theory]
    [InlineData(440, 0)]
    [InlineData(880, 0)]
    [InlineData(1200, 0)]
    [InlineData(660, 990)]   // two-note chime
    [InlineData(523, 784)]
    public void BeepsAndChimes_HaveNoSpeechEvidence(double hz, double second)
    {
        var a = Sum(SyntheticAudio.Concat(SyntheticAudio.Silence(100), SyntheticAudio.Beep(600, hz, secondHz: second), SyntheticAudio.Silence(300)));
        Assert.True(a.VoicedMs < SpeechEvidence.MinVoicedMs, $"voiced {a.VoicedMs}");
        Assert.True(a.ActiveMs > 0);
    }

    [Fact]
    public void ShutterNoise_HasNoSpeechEvidence()
    {
        var a = Sum(SyntheticAudio.Concat(SyntheticAudio.Shutter(), SyntheticAudio.Silence(400)));
        Assert.True(a.VoicedMs < SpeechEvidence.MinVoicedMs, $"voiced {a.VoicedMs}");
    }

    [Fact]
    public void Silence_IsInactive()
    {
        var a = Sum(SyntheticAudio.Silence(1000));
        Assert.Equal(0, a.ActiveMs);
        Assert.Equal(1000, a.DurationMs);
    }

    [Fact]
    public void QuietSpeech_StillVoiced()
    {
        var a = Sum(SyntheticAudio.Speech(1500, 140, level: 0.03));
        Assert.True(a.VoicedMs >= 600, $"voiced {a.VoicedMs}");
    }

    [Fact]
    public void Timeline_SumsOnlyTheRequestedWindow()
    {
        var tl = new SpeechEvidenceTimeline(10);
        for (int i = 0; i < 25; i++) tl.Add(i * 100, new FrameAcoustics(100, 100, i >= 20 ? 80 : 0, 0));
        var ev = tl.Sum(2000, 2400);
        Assert.Equal(5, ev.FramesObserved);
        Assert.Equal(400, ev.VoicedMs);
        Assert.True(tl.Sum(0, 500).Unknown); // evicted from the bounded ring
    }

    [Fact]
    public void Analysis_IsCheapEnoughForTheCaptureThread()
    {
        var frames = SyntheticAudio.Frames(SyntheticAudio.Speech(10_000)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in frames) AcousticAnalyzer.Analyze(f);
        var perFrameMs = sw.Elapsed.TotalMilliseconds / frames.Count;
        _out.WriteLine($"{perFrameMs:0.000} ms per 100 ms frame");
        Assert.True(perFrameMs < 10, $"{perFrameMs} ms per frame");
        // rc.5: the engine path also follows pitch runs across frames (PitchRunTracker) — same budget.
        var runs = new PitchRunTracker();
        sw.Restart();
        foreach (var f in frames) AcousticAnalyzer.Analyze(f, runs);
        var trackedMs = sw.Elapsed.TotalMilliseconds / frames.Count;
        _out.WriteLine($"{trackedMs:0.000} ms per 100 ms frame with pitch-run tracking");
        Assert.True(trackedMs < 10, $"{trackedMs} ms per frame with pitch-run tracking");
    }
}
