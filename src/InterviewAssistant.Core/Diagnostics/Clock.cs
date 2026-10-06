using System.Diagnostics;

namespace InterviewAssistant.Core.Diagnostics;

/// <summary>Monotonic millisecond clock; injectable for deterministic tests.</summary>
public interface IClock { long NowMs { get; } }

public sealed class SystemClock : IClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public long NowMs => _sw.ElapsedMilliseconds;
}

public sealed class ManualClock : IClock
{
    public long NowMs { get; set; }
    public void Advance(long ms) => NowMs += ms;
}
