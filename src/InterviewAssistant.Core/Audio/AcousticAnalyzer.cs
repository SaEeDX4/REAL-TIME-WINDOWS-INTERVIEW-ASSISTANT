using System.Buffers.Binary;

namespace InterviewAssistant.Core.Audio;

/// <summary>
/// Acoustic summary of one captured frame (milliseconds of each kind of sound) plus pitch-movement counts over pairs of
/// consecutive periodic hops: <see cref="SteadyPairs"/> held their pitch within 0.6 % (a note or tone), <see cref="JumpPairs"/>
/// moved by more than 2.5 % (no continuous voice pitch, e.g. a knock's wobbling resonance).
/// </summary>
/// <remarks>
/// <see cref="TrackedHops"/>, <see cref="RunHops"/> and <see cref="FlatRunHops"/> come from a <see cref="PitchRunTracker"/>
/// (pitch runs cross frame boundaries): periodic hops seen, hops in continuous pitch runs of at least five windows, and
/// those of them in runs whose pitch never left a 2 % band — a held note, buzz or chord, not a voice. <see cref="StretchHops"/>:
/// hops in continuous periodic stretches of at least 160 ms (any pitch movement).
/// </remarks>
public readonly record struct FrameAcoustics(int DurationMs, int ActiveMs, int VoicedMs, int TonalMs, int PitchPairs = 0, int SteadyPairs = 0, int JumpPairs = 0,
    int TrackedHops = 0, int RunHops = 0, int FlatRunHops = 0, int StretchHops = 0)
{
    public static FrameAcoustics operator +(FrameAcoustics a, FrameAcoustics b) =>
        new(a.DurationMs + b.DurationMs, a.ActiveMs + b.ActiveMs, a.VoicedMs + b.VoicedMs, a.TonalMs + b.TonalMs,
            a.PitchPairs + b.PitchPairs, a.SteadyPairs + b.SteadyPairs, a.JumpPairs + b.JumpPairs,
            a.TrackedHops + b.TrackedHops, a.RunHops + b.RunHops, a.FlatRunHops + b.FlatRunHops, a.StretchHops + b.StretchHops);
}

/// <summary>
/// Follows the pitch of consecutive analysis windows across frames and splits it into runs at every break (a hop with no
/// voice-range pitch, or a jump of more than 2.5 % per hop). A closed run of at least <see cref="MinRunWindows"/> windows is
/// "flat" when its whole pitch span stays under <see cref="FlatSpan"/> (2 %): a held note, a buzz with vibrato, a chord.
/// A voice — even a calm, monotone "Tell me more." — glides by several percent within each voiced stretch (intonation,
/// declination, micro-prosody), so its runs are not flat although consecutive hops differ by under 0.6 %.
/// It also measures continuous periodic STRETCHES (consecutive windows with a voice-range pitch, whatever the pitch does):
/// hops in stretches of at least <see cref="MinStretchWindows"/> windows (160 ms). A spoken word of two or more
/// syllables has one — vowels and voiced consonants run together — even when a low, creaky voice's pitch estimate jumps
/// between harmonics and so forms no run; a door knock's ringing decays within ~120 ms per strike and has none.
/// One tracker per audio stream (it is stateful); <see cref="Reset"/> when the stream restarts. Thread-safe: frames are
/// tracked on the capture thread while <see cref="OpenRun"/> is read by the transcriber thread (partial trust boundary).
/// </summary>
public sealed class PitchRunTracker
{
    public const int MinRunWindows = 5;
    public const int MinStretchWindows = 8;
    public const double FlatSpan = 0.02;
    private const double JumpPerHop = 0.025;
    private readonly object _sync = new();
    private double _last, _min, _max;
    private int _len, _stretch;
    private int _tracked, _runHops, _flatHops, _stretchHops;

    public void Reset() { lock (_sync) { _len = _stretch = 0; _last = 0; _tracked = _runHops = _flatHops = _stretchHops = 0; } }

    /// <summary>
    /// Tracks one frame's distinct analysis windows (periods, 0 = none; window start offsets; hop size in samples) and
    /// returns the counts credited since the previous frame (runs and stretches are credited when they close).
    /// </summary>
    internal (int Tracked, int Runs, int Flat, int Stretch) Track(double[] period, int[] starts, int hops, int hop)
    {
        lock (_sync)
        {
            // Distinct windows only (the last hop may re-analyse the previous window); across frames one window is unseen.
            for (int h = 0; h < hops; h++)
            {
                if (h > 0 && starts[h] == starts[h - 1]) continue;
                Add(period[h], h == 0 ? 2 : (starts[h] - starts[h - 1]) / hop);
            }
            var r = (_tracked, _runHops, _flatHops, _stretchHops);
            _tracked = _runHops = _flatHops = _stretchHops = 0;
            return r;
        }
    }

    /// <summary>One analysis window: its pitch period (0 = none) and its distance in hops from the previous one.</summary>
    private void Add(double period, int hopsSincePrevious)
    {
        if (period <= 0) { Close(); CloseStretch(); return; }
        _tracked++;
        _stretch++;
        if (_len > 0 && Math.Abs(Math.Log(period / _last)) <= JumpPerHop * Math.Max(1, hopsSincePrevious))
        {
            _len++; _last = period; _min = Math.Min(_min, period); _max = Math.Max(_max, period);
            return;
        }
        Close();
        _len = 1; _last = _min = _max = period;
    }

    private void Close()
    {
        if (_len >= MinRunWindows)
        {
            _runHops += _len;
            if (Math.Log(_max / _min) < FlatSpan) _flatHops += _len;
        }
        _len = 0;
    }

    private void CloseStretch()
    {
        if (_stretch >= MinStretchWindows) _stretchHops += _stretch;
        _stretch = 0;
    }

    /// <summary>
    /// The run and the stretch still in progress as if they closed now (run hops, flat hops, stretch hops): decisions
    /// mid-sound must see a long held note before it ends, not only the runs that already closed.
    /// </summary>
    public (int Runs, int Flat, int Stretch) OpenRun()
    {
        lock (_sync)
        {
            var (runs, flat) = _len >= MinRunWindows ? (_len, Math.Log(_max / _min) < FlatSpan ? _len : 0) : (0, 0);
            return (runs, flat, _stretch >= MinStretchWindows ? _stretch : 0);
        }
    }
}

/// <summary>
/// Cheap, dependency-free speech evidence from 24 kHz mono PCM16 (the exact stream sent for transcription).
/// Each 20 ms hop is analysed over a 40 ms window at 12 kHz (the last hop of a frame uses the frame's last full window):
///  - ACTIVE: level above the floor (something is playing);
///  - VOICED: a periodic signal with a pitch between ~80 and ~400 Hz (normalized autocorrelation) — human voiced speech;
///  - TONAL: nearly all energy in one or two narrow spectral peaks with no harmonic ladder — beeps, chimes, notification
///    tones (voiced speech always shows several harmonics). A periodic hop also counts as TONAL when the signal repeats
///    faster than any voice (a period shorter than 2.5 ms, i.e. a fundamental above 400 Hz): the in-range
///    autocorrelation peak is then only a multiple of that period — chimes, alert tones, most notification sounds.
///  - SUSTAINED (active, neither voiced nor tonal): a periodic hop whose spectrum is unchanged in the disjoint windows
///    40 ms before and after it. Struck or held notes and alert tones ring with fixed partials; voiced speech does not
///    hold its harmonics and formants still for 80–120 ms (pitch glides and articulation move them).
///  - PITCH MOVEMENT between consecutive periodic hops (20 ms apart): a voice's pitch glides continuously (typically
///    0.5–1.5 % per hop with intonation and jitter); a note or alert tone holds it (&lt; 0.6 %). Jumps (&gt; 2.5 %) are counted
///    for diagnostics. Counted per pair so the evidence for a whole segment can say how voice-like the pitch was.
/// Speech evidence = voiced and not tonal. A Windows beep is tonal; a screenshot/click sound is an unvoiced noise
/// burst; neither produces speech evidence. Harmonic sounds with a moving pitch inside the voice range (singing, music,
/// a low alert tone with vibrato, the low ringing of a door knock) can still look voiced, so this is one layer of
/// several, never the only one (measured limits: tests/fixtures/audio/MANIFEST.md).
/// </summary>
public static class AcousticAnalyzer
{
    public const int SampleRate = 24000;
    private const int Decimated = SampleRate / 2;          // 12 kHz
    private const int HopMs = 20, WindowMs = 40;
    private const int Window = Decimated * WindowMs / 1000; // 480 samples
    private const int MinLag = Decimated / 400, MaxLag = Decimated / 80; // 30..150
    private const int Fft = 512;
    public const double ActiveDbfs = -50;
    public const double VoicedCorrelation = 0.5;
    public const double TonalConcentration = 0.97;
    /// <summary>Spectral cosine similarity above which two disjoint 40 ms windows hold the same sustained sound.</summary>
    public const double SustainedSimilarity = 0.95;
    /// <summary>A shorter period is the true one when it is (almost) as periodic as the in-range peak.</summary>
    private const double ShortPeriodMargin = 0.02;
    private const int MinShortLag = 5;                     // 2.4 kHz
    private const int SpectrumBins = Fft / 2;
    private const byte Inactive = 0, Unvoiced = 1, Voiced = 2, Tonal = 3;
    /// <summary>|ln(f2/f1)| below which consecutive periodic hops hold the same pitch (0.6 %).</summary>
    public const double SteadyPitchDelta = 0.006;
    /// <summary>|ln(f2/f1)| above which consecutive periodic hops have no continuous pitch (2.5 %).</summary>
    public const double PitchJumpDelta = 0.025;

    [ThreadStatic] private static double[]? _x, _re, _im, _r;
    [ThreadStatic] private static double[][]? _spectra;
    [ThreadStatic] private static byte[]? _kind;
    [ThreadStatic] private static double[]? _period;
    [ThreadStatic] private static int[]? _start;

    public static FrameAcoustics Analyze(ReadOnlySpan<byte> pcm16) => Analyze(pcm16, null);

    /// <summary>Analyses one frame; with a <paramref name="runs"/> tracker the frame's pitch also continues the stream's runs.</summary>
    public static FrameAcoustics Analyze(ReadOnlySpan<byte> pcm16, PitchRunTracker? runs)
    {
        int n24 = pcm16.Length / 2;
        if (n24 < 2) return default;
        int n12 = n24 / 2;
        var x = _x != null && _x.Length >= n12 ? _x : (_x = new double[Math.Max(n12, Window)]);
        for (int i = 0; i < n12; i++)
        {
            int s0 = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 4, 2));
            int s1 = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 4 + 2, 2));
            x[i] = (s0 + s1) / 65536.0; // average pair (cheap anti-aliasing), scaled to [-0.5, 0.5]
        }
        int durationMs = n24 * 1000 / SampleRate;
        int hop = Decimated * HopMs / 1000;
        int hops = Math.Max(1, n12 / hop);
        int msPerHop = Math.Max(1, durationMs / hops);
        int active = 0, voiced = 0, tonal = 0;
        if (_kind == null || _kind.Length < hops) { _kind = new byte[hops]; _spectra = new double[hops][]; _period = new double[hops]; _start = new int[hops]; }
        var kind = _kind;
        var period = _period!;
        var starts = _start!;
        var spectra = _spectra!;
        for (int h = 0; h < hops; h++)
        {
            kind[h] = Inactive;
            period[h] = 0;
            int start = h * hop;
            int len = Math.Min(Window, n12 - start);
            // The last hop of a frame analyses the frame's last full window, not a truncated 20 ms one (a half window
            // smears narrow partials, so a pure tone stopped looking tonal on every fifth hop).
            if (len < Window) { start = Math.Max(0, n12 - Window); len = Math.Min(Window, n12 - start); }
            starts[h] = start;
            if (len <= MinLag) continue;
            var w = new ReadOnlySpan<double>(x, start, len);
            double energy = 0, mean = 0;
            foreach (var v in w) mean += v;
            mean /= len;
            foreach (var v in w) energy += (v - mean) * (v - mean);
            var rms = Math.Sqrt(energy / len) * 2; // back to full scale
            var db = rms <= 1e-9 ? -120 : 20 * Math.Log10(rms);
            if (db < ActiveDbfs) continue;
            active += msPerHop;
            var power = spectra[h] ??= new double[SpectrumBins];
            var (conc, _, peaks) = Spectrum(w, mean, power);
            // Narrow-band sound: energy in one or two partials and almost nothing else (beeps, chimes, notification
            // tones). Voiced speech always shows a ladder of harmonics, so it has several strong spectral peaks.
            bool isTonal = peaks <= 1 ? conc >= 0.9 : peaks <= 2 && conc >= TonalConcentration;
            if (isTonal) { kind[h] = Tonal; continue; }
            kind[h] = Unvoiced;
            if (len <= MaxLag + 60) continue;
            var (r, aboveVoice, lag) = Periodicity(w, mean);
            if (r >= VoicedCorrelation) { kind[h] = aboveVoice ? Tonal : Voiced; if (!aboveVoice) period[h] = lag; }
        }
        var runCounts = runs?.Track(period, starts, hops, hop) ?? default;
        int pairs = 0, steady = 0, jumps = 0;
        for (int h = 1; h < hops; h++)
        {
            // Pitch movement over every pair of consecutive periodic hops in the voice range (sustained ones included:
            // their steadiness is exactly what distinguishes a held tone from a voice).
            // Only hops a full hop apart: the last hop of a frame re-analyses (part of) the previous window.
            if (period[h] <= 0 || period[h - 1] <= 0 || starts[h] - starts[h - 1] != hop) continue;
            var delta = Math.Abs(Math.Log(period[h] / period[h - 1]));
            pairs++;
            if (delta < SteadyPitchDelta) steady++;
            else if (delta > PitchJumpDelta) jumps++;
        }
        for (int h = 0; h < hops; h++)
        {
            if (kind[h] == Tonal) tonal += msPerHop;
            else if (kind[h] == Voiced)
            {
                // A sustained note is neither speech nor a narrow-band beep: it only counts as active sound.
                if (!Sustained(h, hops, kind, spectra)) voiced += msPerHop;
            }
        }
        var (tracked, runHops, flatHops, stretchHops) = runCounts;
        return new FrameAcoustics(durationMs, Math.Min(active, durationMs), Math.Min(voiced, durationMs), Math.Min(tonal, durationMs), pairs, steady, jumps,
            tracked, runHops, flatHops, stretchHops);
    }

    /// <summary>
    /// Maximum normalized autocorrelation over human pitch lags, and whether the signal's true period is shorter than any
    /// voice's: a lag under 2.5 ms (above 400 Hz) that is about as periodic as the in-range peak, which is (close to) an
    /// integer multiple of it. The in-range peak is then a multiple of a chime/alert-tone period, not a voice pitch.
    /// </summary>
    private static (double Best, bool AboveVoiceRange, double Period) Periodicity(ReadOnlySpan<double> w, double mean)
    {
        var r = _r ??= new double[MaxLag + 2];
        Array.Clear(r);
        int n = w.Length;
        int bestLag = 0;
        double best = 0;
        for (int lag = MinShortLag - 1; lag <= MaxLag && lag < n - 60; lag++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 0; i + lag < n; i++)
            {
                double a = w[i] - mean, b = w[i + lag] - mean;
                xy += a * b; xx += a * a; yy += b * b;
            }
            r[lag] = xx <= 0 || yy <= 0 ? 0 : xy / Math.Sqrt(xx * yy);
            if (lag >= MinLag && r[lag] > best) { best = r[lag]; bestLag = lag; }
        }
        if (best < VoicedCorrelation) return (best, false, 0);
        double period = Interpolate(r, bestLag);
        for (int lag = MinShortLag; lag < MinLag; lag++)
        {
            if (r[lag] < best - ShortPeriodMargin || r[lag] < r[lag - 1] || r[lag] < r[lag + 1]) continue;
            double shortPeriod = Interpolate(r, lag);
            double k = Math.Round(period / shortPeriod);
            if (k >= 2 && Math.Abs(period - k * shortPeriod) <= 0.04 * period) return (best, true, period);
        }
        return (best, false, period);
    }

    /// <summary>Parabolic interpolation of a correlation peak (fractional lag).</summary>
    private static double Interpolate(double[] r, int lag)
    {
        if (lag <= 0 || lag + 1 >= r.Length) return lag;
        double a = r[lag - 1], b = r[lag], c = r[lag + 1], d = a - 2 * b + c;
        return d < 0 ? lag + 0.5 * (a - c) / d : lag;
    }

    /// <summary>
    /// A voiced-looking hop whose spectrum is unchanged in the disjoint windows 40 ms before and after it (those of this
    /// frame that hold sound; at least one) is a sustained note or tone, not speech.
    /// </summary>
    private static bool Sustained(int h, int hops, byte[] kind, double[][] spectra)
    {
        int partners = 0;
        for (int j = h - 2; j <= h + 2; j += 4)
        {
            if (j < 0 || j >= hops || kind[j] == Inactive) continue;
            if (Cosine(spectra[h], spectra[j]) < SustainedSimilarity) return false;
            partners++;
        }
        return partners > 0;
    }

    /// <summary>Cosine similarity of two power spectra over 70 Hz – 4 kHz (the band the peak analysis uses).</summary>
    private static double Cosine(double[] a, double[] b)
    {
        int lo = (int)(70.0 * Fft / Decimated), hi = (int)(4000.0 * Fft / Decimated);
        double xy = 0, xx = 0, yy = 0;
        for (int k = lo; k <= hi; k++) { xy += a[k] * b[k]; xx += a[k] * a[k]; yy += b[k] * b[k]; }
        return xx <= 0 || yy <= 0 ? 0 : xy / Math.Sqrt(xx * yy);
    }

    /// <summary>Share of spectral power in the two strongest narrow peaks (±2 bins). Pure/dual tones ≈ 0.9+, speech ≈ 0.2–0.5.</summary>
    private static (double Share, double PeakHz, int Peaks) Spectrum(ReadOnlySpan<double> w, double mean, double[] power)
    {
        var re = _re ??= new double[Fft];
        var im = _im ??= new double[Fft];
        Array.Clear(re); Array.Clear(im);
        int n = Math.Min(w.Length, Fft);
        for (int i = 0; i < n; i++)
        {
            var hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            re[i] = (w[i] - mean) * hann;
        }
        FftInPlace(re, im);
        int half = Fft / 2;
        Span<double> p = stackalloc double[half];
        double total = 0;
        Array.Clear(power);
        for (int k = 1; k < half; k++) { p[k] = re[k] * re[k] + im[k] * im[k]; total += p[k]; power[k] = p[k]; }
        if (total <= 0) return (0, 0, 0);
        // Strong peaks: local maxima within 20 dB of the strongest bin, between 70 Hz and 4 kHz.
        double max = 0; for (int k = 1; k < half; k++) max = Math.Max(max, p[k]);
        int lo = Math.Max(2, (int)(70.0 * Fft / Decimated)), hi = Math.Min(half - 2, (int)(4000.0 * Fft / Decimated));
        int strong = 0;
        for (int k = lo; k <= hi; k++)
            if (p[k] >= max * 0.01 && p[k] >= p[k - 1] && p[k] > p[k + 1] && p[k] >= p[k - 2] && p[k] > p[k + 2]) strong++;
        double peaks = 0, peakHz = 0;
        for (int round = 0; round < 2; round++)
        {
            int best = 1;
            for (int k = 2; k < half; k++) if (p[k] > p[best]) best = k;
            if (round == 0) peakHz = best * (double)Decimated / Fft;
            for (int k = Math.Max(1, best - 2); k <= Math.Min(half - 1, best + 2); k++) { peaks += p[k]; p[k] = 0; }
        }
        return (peaks / total, peakHz, strong);
    }

    private static void FftInPlace(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = ncr;
                }
            }
        }
    }
}

/// <summary>
/// Acoustic evidence for one interviewer segment or turn. <see cref="WindowMs"/> is the time range the evidence was asked
/// for; <see cref="SpanMs"/> the audio actually analysed in it (less when capture frames were missing).
/// </summary>
public readonly record struct SpeechEvidence(int FramesObserved, int SpanMs, int ActiveMs, int VoicedMs, int TonalMs,
    int PitchPairs = 0, int SteadyPairs = 0, int JumpPairs = 0, int WindowMs = 0, int TrackedHops = 0, int RunHops = 0, int FlatRunHops = 0,
    int StretchHops = 0)
{
    /// <summary>No audio was analysed (no capture data for the period): acoustic layers abstain.</summary>
    public bool Unknown => FramesObserved == 0;
    /// <summary>
    /// Less than three quarters of the requested window was analysed (capture stalled, a segment's start was lost, or a
    /// monologue outlasted the evidence buffer): the words cannot be compared with the audio, so the speech-rate checks
    /// abstain instead of vetoing real speech they could not hear. Whether the audio that WAS heard is a voice still counts.
    /// </summary>
    public bool Incomplete => !Unknown && WindowMs > 0 && SpanMs * 4 < WindowMs * 3;
    /// <summary>Enough voiced, non-tonal sound for a real human utterance (even a short "Why?").</summary>
    public bool HasSpeech => !Unknown && VoicedMs >= MinVoicedMs;
    /// <summary>A short sound with no speech in it: a beep, chime, click or screenshot sound.</summary>
    public bool IsShortNonSpeechEvent => !Unknown && VoicedMs < MinVoicedMs && ActiveMs <= ShortEventMs;

    /// <summary>Share of the periodic sound that was narrow-band (chimes, alert tones): speech ≈ 0–3 %.</summary>
    public double TonalShare => VoicedMs + TonalMs == 0 ? 0 : TonalMs / (double)(VoicedMs + TonalMs);
    /// <summary>Share of consecutive periodic hops that held their pitch (notes, tones): speech ≈ 15–60 %.</summary>
    public double PitchSteadiness => PitchPairs == 0 ? 0 : SteadyPairs / (double)PitchPairs;
    /// <summary>Share of consecutive periodic hops whose pitch jumped (knocks, rattles): speech is continuous.</summary>
    public double PitchJumpShare => PitchPairs == 0 ? 0 : JumpPairs / (double)PitchPairs;

    /// <summary>Pitch runs were tracked for this audio (evidence recorded before 2.1.0-rc.5 has none).</summary>
    public bool PitchTracked => TrackedHops > 0;
    /// <summary>Share of the hops in continuous pitch runs whose pitch never left a 2 % band (held tones: 66–100 %, voices ≤ 40 %).</summary>
    public double FlatRunShare => RunHops == 0 ? 0 : FlatRunHops / (double)RunHops;

    /// <summary>
    /// A held tone: most of the continuous pitch sits in runs that never moved by 2 % (a note, a buzz with vibrato, a
    /// chord), or the pitch is both hop-to-hop steady and at least half flat-run. Judged by the runs once
    /// <see cref="MinRunHops"/> hops are in runs. Hop-to-hop steadiness ALONE never rejects a sound with enough run
    /// evidence: a calm, falling "Tell me more." changes its pitch by under 0.6 % per 20 ms yet glides several percent
    /// over each word (the rc.4 regression). With fewer run hops than that — a 300–400 ms buzz or piano note, a pulsing
    /// tone whose pulses are too short for runs — the runs cannot judge, and the rc.4 rule (≥ 10 pitch pairs, ≥ 65 %
    /// steady) still applies: no real speech fixture combines so few runs with so steady a pitch. Evidence without run
    /// tracking (recorded by older builds) keeps the rc.4 rule.
    /// </summary>
    public bool HeldTone => RunsJudged
        ? FlatRunShare >= MaxFlatRunShare || (PitchPairs >= MinPitchPairs && PitchSteadiness >= SteadyWithFlatRuns && FlatRunShare >= MinFlatRunShareWhenSteady)
        : PitchPairs >= MinPitchPairs && PitchSteadiness >= MaxPitchSteadiness;

    /// <summary>The held-tone test is decided by pitch runs (tracked, and at least <see cref="MinRunHops"/> hops in runs).</summary>
    private bool RunsJudged => PitchTracked && RunHops >= MinRunHops;

    /// <summary>
    /// Voiced sound that also BEHAVES like a voice: little narrow-band energy and a pitch that moves rather than holding
    /// still (<see cref="HeldTone"/>). Too little pitch to judge (a very short "Why?") counts as voice-like when the other
    /// signs agree. Pitch JUMPS are reported but deliberately not used to reject: creaky voice and vocal fry make real
    /// voices jumpy too, and rejecting a real interviewer is the worse failure.
    /// </summary>
    public bool VoiceLike => HasSpeech && !TooTonal && !HeldTone;

    /// <summary>
    /// Narrow-band energy dominates. Judged only once at least <see cref="MinTonalMs"/> of tonal sound was heard: in a
    /// half-second "And then?" two or three tonal hops are measurement noise, while every chime and alert tone measured
    /// holds 200 ms or more.
    /// </summary>
    public bool TooTonal => TonalMs >= MinTonalMs && TonalShare >= MaxTonalShare;

    /// <summary>
    /// The pitch track is long enough to tell a moving voice from a held tone. Live (partial) text is only trusted once
    /// this holds; a turn too short to reach it is judged when it ends.
    /// </summary>
    public bool PitchJudged => PitchTracked ? RunHops >= MinRunHops : PitchPairs >= MinPitchPairs;

    /// <summary>
    /// At least one continuous voiced stretch of 160 ms or more was heard (<see cref="PitchRunTracker.MinStretchWindows"/>):
    /// a spoken word of two or more syllables has one, a knock or a click does not.
    /// </summary>
    public bool SustainedVoicing => StretchHops > 0;
    /// <summary>Stretches were measured (pitch tracked, and not a record written before the count existed: -1).</summary>
    public bool StretchMeasured => PitchTracked && StretchHops >= 0;

    /// <summary>Why the audio does not behave like a voice (for diagnostics); empty when it does.</summary>
    public string NotVoiceReason => !HasSpeech ? "no voiced sound"
        : TooTonal ? $"tonal {TonalShare:P0}"
        : HeldTone ? (RunsJudged ? $"held pitch {FlatRunShare:P0}" : $"steady pitch {PitchSteadiness:P0}") : "";

    /// <summary>
    /// A segment whose audio window is not known (e.g. its start was lost in a reconnect, or its evidence never arrived):
    /// no frames, and a window so large that any turn containing it counts as <see cref="Incomplete"/> — the acoustic layers
    /// abstain rather than judging a whole question by a fragment of its audio.
    /// </summary>
    public static SpeechEvidence Unheard => new(0, 0, 0, 0, 0, WindowMs: UnheardWindowMs);
    public const int UnheardWindowMs = 600_000;

    public const int MinVoicedMs = 100;
    public const int ShortEventMs = 1500;
    public const double MaxTonalShare = 0.2;
    public const int MinTonalMs = 100;
    public const double MaxPitchSteadiness = 0.65;
    public const int MinPitchPairs = 10;
    public const int MinRunHops = 10;
    public const double MaxFlatRunShare = 0.75;
    public const double SteadyWithFlatRuns = 0.65;
    public const double MinFlatRunShareWhenSteady = 0.5;

    public static SpeechEvidence operator +(SpeechEvidence a, SpeechEvidence b) =>
        new(a.FramesObserved + b.FramesObserved, a.SpanMs + b.SpanMs, a.ActiveMs + b.ActiveMs, a.VoicedMs + b.VoicedMs, a.TonalMs + b.TonalMs,
            a.PitchPairs + b.PitchPairs, a.SteadyPairs + b.SteadyPairs, a.JumpPairs + b.JumpPairs, a.WindowMs + b.WindowMs,
            a.TrackedHops + b.TrackedHops, a.RunHops + b.RunHops, a.FlatRunHops + b.FlatRunHops, a.StretchHops + b.StretchHops);

    public override string ToString() => Unknown ? "acoustics: n/a"
        : $"voiced {VoicedMs} ms, active {ActiveMs} ms, tonal {TonalMs} ms over {SpanMs} ms" +
          (PitchPairs > 0 ? $", pitch steady {PitchSteadiness:P0} / jumps {PitchJumpShare:P0} of {PitchPairs}" : "") +
          (RunHops > 0 ? $", flat runs {FlatRunShare:P0} of {RunHops}" : "") + (StretchHops > 0 ? $", stretches {StretchHops}" : "");
}

/// <summary>
/// Bounded timeline of analysed frames (last ~3 minutes) so the evidence for a VAD segment can be summed when the
/// server reports speech start/stop. Thread-safe: frames arrive on the capture thread, queries on the event thread.
/// </summary>
public sealed class SpeechEvidenceTimeline
{
    private readonly (long AtMs, FrameAcoustics A)[] _ring;
    private int _head, _count;
    private readonly object _lock = new();

    public SpeechEvidenceTimeline(int capacity = 1800) => _ring = new (long, FrameAcoustics)[capacity];

    public void Add(long atMs, FrameAcoustics a)
    {
        lock (_lock)
        {
            _ring[_head] = (atMs, a);
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
    }

    public void Clear() { lock (_lock) { _head = 0; _count = 0; } }

    /// <summary>Sums frames whose capture time lies in [fromMs, toMs].</summary>
    public SpeechEvidence Sum(long fromMs, long toMs)
    {
        lock (_lock)
        {
            int frames = 0; var total = default(FrameAcoustics);
            for (int i = 0; i < _count; i++)
            {
                var (at, a) = _ring[(_head - 1 - i + _ring.Length) % _ring.Length];
                if (at < fromMs) break;
                if (at > toMs) continue;
                frames++; total += a;
            }
            return new SpeechEvidence(frames, total.DurationMs, total.ActiveMs, total.VoicedMs, total.TonalMs,
                total.PitchPairs, total.SteadyPairs, total.JumpPairs, (int)Math.Max(0, toMs - fromMs), total.TrackedHops, total.RunHops, total.FlatRunHops,
                total.StretchHops);
        }
    }
}
