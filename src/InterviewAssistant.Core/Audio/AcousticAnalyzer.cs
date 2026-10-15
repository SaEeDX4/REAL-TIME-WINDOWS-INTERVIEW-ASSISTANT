using System.Buffers.Binary;

namespace InterviewAssistant.Core.Audio;

/// <summary>Acoustic summary of one captured frame (milliseconds of each kind of sound).</summary>
public readonly record struct FrameAcoustics(int DurationMs, int ActiveMs, int VoicedMs, int TonalMs)
{
    public static FrameAcoustics operator +(FrameAcoustics a, FrameAcoustics b) =>
        new(a.DurationMs + b.DurationMs, a.ActiveMs + b.ActiveMs, a.VoicedMs + b.VoicedMs, a.TonalMs + b.TonalMs);
}

/// <summary>
/// Cheap, dependency-free speech evidence from 24 kHz mono PCM16 (the exact stream sent for transcription).
/// Each 20 ms hop is analysed over a 40 ms window at 12 kHz:
///  - ACTIVE: level above the floor (something is playing);
///  - VOICED: a periodic signal with a pitch between ~80 and ~400 Hz (normalized autocorrelation) — human voiced speech;
///  - TONAL: nearly all energy in one or two narrow spectral peaks with no harmonic ladder — beeps, chimes, notification
///    tones (voiced speech always shows several harmonics).
/// Speech evidence = voiced and not tonal. A Windows beep is tonal; a screenshot/click sound is an unvoiced noise
/// burst; neither produces speech evidence. Music can look voiced, so this is one layer of several, never the only one.
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

    [ThreadStatic] private static double[]? _x, _re, _im;

    public static FrameAcoustics Analyze(ReadOnlySpan<byte> pcm16)
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
        for (int h = 0; h < hops; h++)
        {
            int start = h * hop;
            int len = Math.Min(Window, n12 - start);
            if (len < MaxLag + 60) { start = Math.Max(0, n12 - Window); len = Math.Min(Window, n12 - start); }
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
            var (conc, _, peaks) = Spectrum(w, mean);
            // Narrow-band sound: energy in one or two partials and almost nothing else (beeps, chimes, notification
            // tones). Voiced speech always shows a ladder of harmonics, so it has several strong spectral peaks.
            bool isTonal = peaks <= 1 ? conc >= 0.9 : peaks <= 2 && conc >= TonalConcentration;
            if (isTonal) { tonal += msPerHop; continue; }
            if (len > MaxLag + 60 && PeakCorrelation(w, mean) >= VoicedCorrelation) voiced += msPerHop;
        }
        return new FrameAcoustics(durationMs, Math.Min(active, durationMs), Math.Min(voiced, durationMs), Math.Min(tonal, durationMs));
    }

    /// <summary>Maximum normalized autocorrelation over human pitch lags.</summary>
    private static double PeakCorrelation(ReadOnlySpan<double> w, double mean)
    {
        double best = 0;
        int n = w.Length;
        for (int lag = MinLag; lag <= MaxLag && lag < n - 60; lag++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 0; i + lag < n; i++)
            {
                double a = w[i] - mean, b = w[i + lag] - mean;
                xy += a * b; xx += a * a; yy += b * b;
            }
            if (xx <= 0 || yy <= 0) continue;
            var r = xy / Math.Sqrt(xx * yy);
            if (r > best) best = r;
        }
        return best;
    }

    /// <summary>Share of spectral power in the two strongest narrow peaks (±2 bins). Pure/dual tones ≈ 0.9+, speech ≈ 0.2–0.5.</summary>
    private static (double Share, double PeakHz, int Peaks) Spectrum(ReadOnlySpan<double> w, double mean)
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
        for (int k = 1; k < half; k++) { p[k] = re[k] * re[k] + im[k] * im[k]; total += p[k]; }
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

/// <summary>Acoustic evidence for one interviewer segment or turn.</summary>
public readonly record struct SpeechEvidence(int FramesObserved, int SpanMs, int ActiveMs, int VoicedMs, int TonalMs)
{
    /// <summary>No audio was analysed (no capture data for the period): acoustic layers abstain.</summary>
    public bool Unknown => FramesObserved == 0;
    /// <summary>Enough voiced, non-tonal sound for a real human utterance (even a short "Why?").</summary>
    public bool HasSpeech => !Unknown && VoicedMs >= MinVoicedMs;
    /// <summary>A short sound with no speech in it: a beep, chime, click or screenshot sound.</summary>
    public bool IsShortNonSpeechEvent => !Unknown && VoicedMs < MinVoicedMs && ActiveMs <= ShortEventMs;
    public const int MinVoicedMs = 100;
    public const int ShortEventMs = 1500;
    public static SpeechEvidence operator +(SpeechEvidence a, SpeechEvidence b) =>
        new(a.FramesObserved + b.FramesObserved, a.SpanMs + b.SpanMs, a.ActiveMs + b.ActiveMs, a.VoicedMs + b.VoicedMs, a.TonalMs + b.TonalMs);
    public override string ToString() => Unknown ? "acoustics: n/a" : $"voiced {VoicedMs} ms, active {ActiveMs} ms, tonal {TonalMs} ms over {SpanMs} ms";
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
            return new SpeechEvidence(frames, total.DurationMs, total.ActiveMs, total.VoicedMs, total.TonalMs);
        }
    }
}
