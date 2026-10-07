using System.Text.Json;
using System.Text.RegularExpressions;

namespace InterviewAssistant.Core.Knowledge;

/// <summary>A titled paragraph from a markdown playbook/brief, used for compact retrieval.</summary>
public sealed record KnowledgeSnippet(string Source, string Heading, string Text);

/// <summary>Everything loaded from the knowledge/ folder. Immutable after load.</summary>
public sealed class KnowledgeBase
{
    public required CandidateProfile Profile { get; init; }
    public required IReadOnlyList<CandidateStory> Stories { get; init; }
    public required IReadOnlyList<BankQuestion> Questions { get; init; }
    public required IReadOnlyList<KnowledgeSnippet> Snippets { get; init; }
    public required string SystemPromptTemplate { get; init; }
    public TargetContext Context { get; init; } = new();
    public string Directory { get; init; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static readonly string[] SnippetFiles =
    {
        "product_owner_playbook.md", "crypto_fintech_playbook.md", "company_brief.md",
        "product_brief.md", "role_brief.md", "whitepaper_notes.md",
    };

    public static KnowledgeBase Load(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Knowledge directory not found: {directory}");

        T Read<T>(string file) where T : new()
        {
            var path = Path.Combine(directory, file);
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? new T();
        }

        var snippets = new List<KnowledgeSnippet>();
        foreach (var file in SnippetFiles)
        {
            var path = Path.Combine(directory, file);
            if (File.Exists(path)) snippets.AddRange(SplitMarkdown(file, File.ReadAllText(path)));
        }
        foreach (var path in System.IO.Directory.Exists(Path.Combine(directory, "notes")) ? System.IO.Directory.GetFiles(Path.Combine(directory, "notes"), "*.md") : Array.Empty<string>())
            snippets.AddRange(SplitMarkdown(Path.GetFileName(path), File.ReadAllText(path)));

        return new KnowledgeBase
        {
            Profile = Read<CandidateProfile>("candidate_profile.json"),
            Stories = Read<StoriesFile>("candidate_stories.json").Stories,
            Questions = Read<QuestionBankFile>("question_bank.json").Questions,
            Snippets = snippets,
            SystemPromptTemplate = PromptTemplates.LiveAnswer,
            Context = LoadContext(directory, Read<CandidateProfile>("candidate_profile.json")),
            Directory = directory,
        };
    }

    /// <summary>Generic knowledge with no candidate data: live AI answers still work; nothing is claimed as history.</summary>
    public static KnowledgeBase Empty(IReadOnlyList<KnowledgeSnippet>? playbooks = null) => new()
    {
        Profile = new CandidateProfile(), Stories = Array.Empty<CandidateStory>(), Questions = Array.Empty<BankQuestion>(),
        Snippets = playbooks ?? Array.Empty<KnowledgeSnippet>(), SystemPromptTemplate = PromptTemplates.LiveAnswer, Context = new TargetContext(),
    };

    private static TargetContext LoadContext(string directory, CandidateProfile profile)
    {
        var path = Path.Combine(directory, "target_context.json");
        var ctx = File.Exists(path) ? JsonSerializer.Deserialize<TargetContext>(File.ReadAllText(path), JsonOptions) ?? new() : new TargetContext();
        if (ctx.CandidateName == "the candidate" && profile.Name.Length > 0) ctx.CandidateName = profile.Name;
        ctx.Employers = profile.Experience.Select(e => e.Company).ToList();
        return ctx;
    }

    /// <summary>Splits markdown into small retrievable snippets: one per bullet / table row / paragraph, tagged with its heading.</summary>
    public static IEnumerable<KnowledgeSnippet> SplitMarkdown(string source, string markdown)
    {
        var heading = "";
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) { heading = line.TrimStart('#').Trim(); continue; }
            if (Regex.IsMatch(line, @"^\|[\s\-|]+\|$")) continue; // table separator
            if (line.StartsWith("| Fact") || line.StartsWith("| # ") || line.StartsWith("| Theme")) continue; // table header
            var text = line.TrimStart('-', '*', ' ').Replace("**", "");
            if (text.Length < 25) continue;
            yield return new KnowledgeSnippet(source, heading, text);
        }
    }

    /// <summary>Searches upward from a starting directory for a "knowledge" folder (works for dev runs and published builds).</summary>
    /// <summary>Walks up from <paramref name="start"/> to find <paramref name="relative"/> containing <paramref name="marker"/>.</summary>
    public static string? FindDirectory(string start, string relative = "knowledge", string marker = "question_bank.json")
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(Path.Combine(candidate, marker))) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
