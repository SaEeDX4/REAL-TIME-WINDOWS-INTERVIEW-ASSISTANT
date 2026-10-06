namespace InterviewAssistant.Core.Diagnostics;

/// <summary>Bounded exponential backoff with jitter: 1s, 2s, 4s, 8s, 16s, capped at 30s.</summary>
public sealed class ExponentialBackoff
{
    private readonly TimeSpan _initial, _max;
    private readonly Random _random = new();
    public int Attempt { get; private set; }

    public ExponentialBackoff(TimeSpan? initial = null, TimeSpan? max = null)
    {
        _initial = initial ?? TimeSpan.FromSeconds(1);
        _max = max ?? TimeSpan.FromSeconds(30);
    }

    public TimeSpan Next()
    {
        var baseMs = Math.Min(_max.TotalMilliseconds, _initial.TotalMilliseconds * Math.Pow(2, Math.Min(Attempt, 10)));
        Attempt++;
        var jitter = 0.85 + _random.NextDouble() * 0.3; // ±15% avoids synchronized retries
        return TimeSpan.FromMilliseconds(Math.Min(_max.TotalMilliseconds, baseMs * jitter));
    }

    public void Reset() => Attempt = 0;
}
