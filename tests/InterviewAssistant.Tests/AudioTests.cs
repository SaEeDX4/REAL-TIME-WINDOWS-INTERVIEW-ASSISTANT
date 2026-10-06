using System.Buffers.Binary;
using InterviewAssistant.Core.Audio;
using Xunit;

namespace InterviewAssistant.Tests;

public class AudioTests
{
    private static byte[] SineFloatStereo(int rate, double freq, double seconds, float amp = 0.5f)
    {
        int n = (int)(rate * seconds);
        var bytes = new byte[n * 8];
        for (int i = 0; i < n; i++)
        {
            var v = (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8), v);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8 + 4), v);
        }
        return bytes;
    }

    private static short[] ToShorts(byte[] pcm) => Enumerable.Range(0, pcm.Length / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2))).ToArray();

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(16000)]
    public void ConvertsToMono24kWithCorrectLengthAndAmplitude(int rate)
    {
        var conv = new PcmConverter(new SourceFormat(rate, 2, SampleEncoding.Float32));
        var input = SineFloatStereo(rate, 440, 1.0);
        var output = new List<byte>();
        // Feed in odd-sized chunks (including partial frames) like WASAPI does.
        int pos = 0, chunk = 3333;
        while (pos < input.Length) { var len = Math.Min(chunk, input.Length - pos); output.AddRange(conv.Convert(input.AsSpan(pos, len))); pos += len; }
        var samples = ToShorts(output.ToArray());
        Assert.InRange(samples.Length, 23900, 24000);
        var peak = samples.Skip(100).Max(s => Math.Abs((int)s)) / 32768.0;
        Assert.InRange(peak, 0.42, 0.52);
    }

    [Fact]
    public void ChunkBoundariesAreContinuous()
    {
        var conv = new PcmConverter(new SourceFormat(48000, 2, SampleEncoding.Float32));
        var input = SineFloatStereo(48000, 200, 0.5);
        var whole = ToShorts(new PcmConverter(new SourceFormat(48000, 2, SampleEncoding.Float32)).Convert(input));
        var parts = new List<byte>();
        for (int p = 0; p < input.Length; p += 960 * 8 + 4) parts.AddRange(conv.Convert(input.AsSpan(p, Math.Min(960 * 8 + 4, input.Length - p))));
        var chunked = ToShorts(parts.ToArray());
        Assert.Equal(whole.Length, chunked.Length);
        for (int i = 0; i < whole.Length; i++) Assert.InRange(chunked[i] - whole[i], -2, 2);
    }

    [Fact]
    public void Pcm16InputAndLevelMeter()
    {
        var conv = new PcmConverter(new SourceFormat(24000, 1, SampleEncoding.Pcm16));
        var silence = new byte[4800];
        Assert.Equal(-100, PcmConverter.LevelDb(conv.Convert(silence)));
        var loud = new byte[4800];
        for (int i = 0; i < 2400; i++) BinaryPrimitives.WriteInt16LittleEndian(loud.AsSpan(i * 2), (short)(i % 2 == 0 ? 16000 : -16000));
        Assert.InRange(PcmConverter.LevelDb(conv.Convert(loud)), -7, -5);
    }

    [Fact]
    public void FramerProduces100msFrames()
    {
        var f = new AudioFramer();
        var frames = f.Push(new byte[4800 * 2 + 100]).ToList();
        Assert.Equal(2, frames.Count);
        Assert.All(frames, fr => Assert.Equal(4800, fr.Length));
        Assert.Single(f.Push(new byte[4700]));
    }
}
