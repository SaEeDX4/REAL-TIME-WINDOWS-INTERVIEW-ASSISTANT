using System.Buffers.Binary;

namespace InterviewAssistant.Core.Audio;

public enum SampleEncoding { Pcm16, Pcm24, Pcm32, Float32 }

public sealed record SourceFormat(int SampleRate, int Channels, SampleEncoding Encoding)
{
    public int BytesPerSample => Encoding switch { SampleEncoding.Pcm16 => 2, SampleEncoding.Pcm24 => 3, _ => 4 };
    public int BlockAlign => BytesPerSample * Channels;
}

/// <summary>
/// Streaming converter from any WASAPI mix format (typically 48 kHz stereo float32) to the 24 kHz mono
/// little-endian PCM16 required by the realtime transcription API. Down-mixes channels, resamples with
/// box-filter averaging (cheap anti-aliasing for speech) and keeps fractional state across buffers so
/// there are no clicks at chunk boundaries. Not thread-safe: use from the capture thread only.
/// </summary>
public sealed class PcmConverter
{
    public const int TargetRate = 24000;
    private readonly SourceFormat _src;
    private readonly double _ratio;          // input samples per output sample
    private float[] _pending = new float[8192];
    private int _pendingCount;
    private double _pos;                     // fractional read position into _pending
    private byte[] _partialFrame = Array.Empty<byte>();

    public PcmConverter(SourceFormat source)
    {
        if (source.SampleRate <= 0 || source.Channels <= 0) throw new ArgumentException("Invalid source format");
        _src = source;
        _ratio = (double)source.SampleRate / TargetRate;
    }

    /// <summary>Converts a raw capture buffer; returns PCM16 mono 24 kHz bytes (may be empty).</summary>
    public byte[] Convert(ReadOnlySpan<byte> input)
    {
        // Handle buffers that split a sample frame.
        ReadOnlySpan<byte> data = input;
        byte[]? joined = null;
        if (_partialFrame.Length > 0)
        {
            joined = new byte[_partialFrame.Length + input.Length];
            _partialFrame.CopyTo(joined, 0);
            input.CopyTo(joined.AsSpan(_partialFrame.Length));
            data = joined;
        }
        int frames = data.Length / _src.BlockAlign;
        int used = frames * _src.BlockAlign;
        _partialFrame = data.Length > used ? data[used..].ToArray() : Array.Empty<byte>();

        EnsureCapacity(_pendingCount + frames);
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            int baseOffset = f * _src.BlockAlign;
            for (int c = 0; c < _src.Channels; c++) sum += ReadSample(data, baseOffset + c * _src.BytesPerSample);
            _pending[_pendingCount++] = sum / _src.Channels;
        }

        var outCount = (int)Math.Max(0, Math.Floor((_pendingCount - _pos) / _ratio));
        if (outCount <= 0) return Array.Empty<byte>();
        var output = new byte[outCount * 2];
        for (int i = 0; i < outCount; i++)
        {
            double start = _pos, end = _pos + _ratio;
            float v;
            if (_ratio >= 1.0)
            {
                int s = (int)start, e = Math.Min((int)Math.Ceiling(end), _pendingCount);
                float acc = 0; int n = 0;
                for (int k = s; k < e; k++) { acc += _pending[k]; n++; }
                v = n > 0 ? acc / n : 0;
            }
            else
            {
                int s = (int)start; double frac = start - s;
                float a = _pending[s], b = s + 1 < _pendingCount ? _pending[s + 1] : a;
                v = (float)(a + (b - a) * frac);
            }
            _pos = end;
            var clamped = Math.Clamp(v, -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), (short)Math.Round(clamped * short.MaxValue));
        }
        // Discard consumed input, keep the fractional remainder.
        int consumed = Math.Min((int)Math.Floor(_pos), _pendingCount);
        if (consumed > 0)
        {
            Array.Copy(_pending, consumed, _pending, 0, _pendingCount - consumed);
            _pendingCount -= consumed;
            _pos -= consumed;
        }
        return output;
    }

    private float ReadSample(ReadOnlySpan<byte> d, int o) => _src.Encoding switch
    {
        SampleEncoding.Float32 => BinaryPrimitives.ReadSingleLittleEndian(d[o..]),
        SampleEncoding.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(d[o..]) / 32768f,
        SampleEncoding.Pcm24 => ((d[o] | d[o + 1] << 8 | (sbyte)d[o + 2] << 16)) / 8388608f,
        SampleEncoding.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(d[o..]) / 2147483648f,
        _ => 0,
    };

    private void EnsureCapacity(int n)
    {
        if (n <= _pending.Length) return;
        Array.Resize(ref _pending, Math.Max(n, _pending.Length * 2));
    }

    /// <summary>RMS level of a PCM16 buffer in dBFS (-100 .. 0).</summary>
    public static double LevelDb(ReadOnlySpan<byte> pcm16)
    {
        int n = pcm16.Length / 2;
        if (n == 0) return -100;
        double acc = 0;
        for (int i = 0; i < n; i++) { double s = BinaryPrimitives.ReadInt16LittleEndian(pcm16[(i * 2)..]) / 32768.0; acc += s * s; }
        var rms = Math.Sqrt(acc / n);
        return rms <= 1e-5 ? -100 : Math.Max(-100, 20 * Math.Log10(rms));
    }
}

/// <summary>Groups converted audio into fixed ~100 ms frames for the WebSocket (fewer, larger messages).</summary>
public sealed class AudioFramer
{
    private readonly int _frameBytes;
    private readonly MemoryStream _buffer = new();
    public AudioFramer(int frameMs = 100) => _frameBytes = PcmConverter.TargetRate * 2 * frameMs / 1000;

    public IEnumerable<byte[]> Push(byte[] pcm)
    {
        _buffer.Write(pcm);
        while (_buffer.Length >= _frameBytes)
        {
            var all = _buffer.ToArray();
            var frame = all[.._frameBytes];
            _buffer.SetLength(0);
            _buffer.Write(all, _frameBytes, all.Length - _frameBytes);
            yield return frame;
        }
    }
}
