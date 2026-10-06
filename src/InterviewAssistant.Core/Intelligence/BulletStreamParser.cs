using System.Text;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

/// <summary>
/// Converts a token stream into STABLE display units. A bullet is only emitted once complete (newline seen
/// or stream ended), so text already shown never changes under the reader. The trailing incomplete bullet
/// is exposed separately as a "pending" preview.
/// </summary>
public sealed class BulletStreamParser
{
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _bullets = new();
    private bool _modeParsed;

    public AnswerMode? Mode { get; private set; }
    public IReadOnlyList<string> Bullets => _bullets;
    public string Pending { get; private set; } = "";

    /// <summary>Feeds a delta; returns bullets that became complete with this delta.</summary>
    public IReadOnlyList<string> Push(string delta)
    {
        _buffer.Append(delta);
        var completed = new List<string>();
        while (true)
        {
            var text = _buffer.ToString();
            var nl = text.IndexOf('\n');
            if (nl < 0) break;
            var line = text[..nl];
            _buffer.Remove(0, nl + 1);
            var item = ProcessLine(line);
            if (item != null) { _bullets.Add(item); completed.Add(item); }
        }
        var pending = _buffer.ToString();
        Pending = TryParseMode(pending, peekOnly: true) ? "" : TextNormalizer.StripBullet(pending);
        return completed;
    }

    /// <summary>Flushes the final line at end of stream.</summary>
    public IReadOnlyList<string> Complete()
    {
        var rest = _buffer.ToString();
        _buffer.Clear();
        Pending = "";
        var item = ProcessLine(rest);
        if (item == null) return Array.Empty<string>();
        _bullets.Add(item);
        return new[] { item };
    }

    private string? ProcessLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;
        if (TryParseMode(trimmed, peekOnly: false)) return null;
        var content = TextNormalizer.StripBullet(trimmed);
        if (content.Length == 0) return null;
        return content;
    }

    private bool TryParseMode(string line, bool peekOnly)
    {
        var t = line.Trim().TrimStart('*').Trim();
        if (!t.StartsWith("MODE", StringComparison.OrdinalIgnoreCase)) return false;
        if (peekOnly) return true;
        if (_modeParsed) return true;
        _modeParsed = true;
        var upper = t.ToUpperInvariant();
        Mode = upper.Contains("VERIFIED") ? AnswerMode.Verified : upper.Contains("BRIDGE") ? AnswerMode.Bridge : AnswerMode.Hypothetical;
        return true;
    }
}
