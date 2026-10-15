using System.Buffers.Binary;

namespace InterviewAssistant.Tests;

/// <summary>
/// Deterministic 24 kHz mono PCM16 test signals, split into 100 ms capture frames:
/// speech-like voiced audio (glottal pulse train with drifting pitch through vowel formants, syllable envelope and
/// unvoiced consonant bursts), Windows-style beeps/chimes, a camera-shutter style noise burst, a music chord, silence.
/// </summary>
public static class SyntheticAudio
{
    public const int Rate = 24000;
    public const int FrameMs = 100;

    public static IEnumerable<byte[]> Frames(double[] signal)
    {
        int per = Rate * FrameMs / 1000;
        for (int i = 0; i < signal.Length; i += per)
        {
            var frame = new byte[per * 2];
            for (int j = 0; j < per && i + j < signal.Length; j++)
                BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(j * 2), (short)Math.Clamp(signal[i + j] * 32767, -32768, 32767));
            yield return frame;
        }
    }

    public static double[] Silence(int ms) => new double[Rate * ms / 1000];

    /// <summary>Speech-like signal: syllables of voiced vowels (pitch 95–240 Hz drifting) with short unvoiced bursts between.</summary>
    public static double[] Speech(int ms, double basePitch = 130, double level = 0.25, int seed = 1)
    {
        var rnd = new Random(seed);
        var n = Rate * ms / 1000;
        var y = new double[n];
        double phase = 0;
        // Formant resonators (two-pole) for a few vowels, cycled per syllable.
        var vowels = new[] { (700.0, 1220.0, 2600.0), (300.0, 2300.0, 3000.0), (500.0, 900.0, 2500.0), (400.0, 1900.0, 2550.0), (350.0, 800.0, 2400.0) };
        int syl = Rate * 220 / 1000; // ~4.5 syllables / s
        double[] s1 = new double[3], s2 = new double[3];
        for (int i = 0; i < n; i++)
        {
            int k = i / syl;
            double inSyl = (i % syl) / (double)syl;
            var (f1, f2, f3) = vowels[k % vowels.Length];
            double pitch = basePitch * (1 + 0.18 * Math.Sin(2 * Math.PI * i / Rate * 0.7 + seed) + 0.06 * Math.Sin(2 * Math.PI * inSyl));
            phase += pitch / Rate;
            double src;
            bool consonant = inSyl < 0.18;
            if (consonant) src = (rnd.NextDouble() * 2 - 1) * 0.3; // unvoiced burst
            else if (phase >= 1) { phase -= 1; src = 1.0; } else src = 0;
            double env = consonant ? 0.5 : Math.Sin(Math.PI * Math.Min(1, (inSyl - 0.18) / 0.82));
            double outv = 0;
            int fi = 0;
            foreach (var f in new[] { f1, f2, f3 })
            {
                double r = Math.Exp(-Math.PI * 90 / Rate), th = 2 * Math.PI * f / Rate;
                double v = src + 2 * r * Math.Cos(th) * s1[fi] - r * r * s2[fi];
                s2[fi] = s1[fi]; s1[fi] = v;
                outv += v * (fi == 0 ? 1.0 : fi == 1 ? 0.6 : 0.3);
                fi++;
            }
            y[i] = outv * env;
        }
        return Normalize(y, level);
    }

    /// <summary>Sine beep with a short attack and exponential decay (Windows "Default Beep"/"Ding" style).</summary>
    public static double[] Beep(int ms, double hz = 880, double level = 0.4, double secondHz = 0)
    {
        var n = Rate * ms / 1000;
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Min(1, t / 0.01) * Math.Exp(-3 * t / (ms / 1000.0));
            double v = Math.Sin(2 * Math.PI * hz * t);
            if (secondHz > 0 && t > ms / 2000.0) v = Math.Sin(2 * Math.PI * secondHz * t); // two-note chime
            y[i] = v * env * level;
        }
        return y;
    }

    /// <summary>Camera-shutter / click style sound: two short broadband noise bursts.</summary>
    public static double[] Shutter(int seed = 3)
    {
        var rnd = new Random(seed);
        var y = new double[Rate * 350 / 1000];
        for (int i = 0; i < y.Length; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Exp(-t * 60) + (t > 0.12 ? Math.Exp(-(t - 0.12) * 50) : 0);
            y[i] = (rnd.NextDouble() * 2 - 1) * env * 0.5;
        }
        return y;
    }

    /// <summary>Sustained major chord (music fragment) — harmonic, can look voiced.</summary>
    public static double[] Chord(int ms, double level = 0.3)
    {
        var n = Rate * ms / 1000;
        var y = new double[n];
        var notes = new[] { 261.6, 329.6, 392.0, 523.3 };
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate, v = 0;
            foreach (var f in notes) for (int h = 1; h <= 4; h++) v += Math.Sin(2 * Math.PI * f * h * t) / (h * h);
            y[i] = v;
        }
        return Normalize(y, level);
    }

    public static double[] Concat(params double[][] parts) => parts.SelectMany(p => p).ToArray();

    private static double[] Normalize(double[] y, double level)
    {
        var peak = y.Max(Math.Abs);
        if (peak <= 0) return y;
        for (int i = 0; i < y.Length; i++) y[i] = y[i] / peak * level;
        return y;
    }
}
