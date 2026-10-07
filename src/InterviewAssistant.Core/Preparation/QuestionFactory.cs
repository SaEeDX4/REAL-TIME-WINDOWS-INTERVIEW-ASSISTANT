using System.Text.RegularExpressions;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Ingestion;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Preparation;

/// <summary>
/// Generates the per-target question bank with prepared answers, Coach keywords and structures.
/// Verified answers are assembled ONLY from confirmed facts (verbatim content, first-person phrasing);
/// hypothetical answers come from the company-neutral expert library or approach templates.
/// Confidence &lt; 0.7 marks template answers that the live engine uses as AI reference instead of showing instantly.
/// </summary>
internal sealed class QuestionFactory
{
    private readonly CandidateProfileRecord _p;
    private readonly InterviewTarget _t;
    private readonly JobAnalysis _jd;
    private readonly List<Story> _stories;
    private readonly List<RequirementMatch> _matches;
    private readonly List<string> _gaps;
    private readonly Dictionary<string, ProfileFact> _facts;
    private readonly List<BankQuestion> _out = new();
    private readonly Dictionary<string, int> _storyUse = new();

    public QuestionFactory(CandidateProfileRecord p, InterviewTarget t, JobAnalysis jd, List<Story> stories, List<RequirementMatch> matches, List<string> gaps, List<ProfileFact> confirmed)
    {
        _p = p; _t = t; _jd = jd; _stories = stories; _matches = matches; _gaps = gaps;
        _facts = confirmed.ToDictionary(f => f.Id);
    }

    private string Role => _t.JobTitle.Length > 0 ? _t.JobTitle : "this role";
    private string Company => _t.Company.Length > 0 ? _t.Company : "your company";
    private ProfileFact? LatestRole => _facts.Values.Where(f => f.Kind == FactKind.Role).OrderByDescending(f => StartYear(f.Period)).FirstOrDefault();

    public List<BankQuestion> Build(List<GenericLibraryItem> library)
    {
        Core();
        Behavioural();
        PerRole();
        Requirements();
        Responsibilities();
        foreach (var l in library)
            Add(l.Category, AnswerMode.Hypothetical, l.CanonicalQuestion, l.SemanticVariants, l.ShortBullets, 0.85, full: l.OptionalFullAnswer, follow: l.FollowUpQuestions);

        // Adaptive size: 50–150 depending on role complexity; drop lowest-confidence template items first.
        var ordered = _out.GroupBy(q => TextNormalizer.Fingerprint(q.CanonicalQuestion)).Select(g => g.First()).ToList();
        if (ordered.Count > 150) ordered = ordered.OrderByDescending(q => q.Confidence).Take(150).ToList();
        return ordered.Select((q, i) => Renumber(q, i + 1)).ToList();
    }

    // ---------------- sections ----------------

    private void Core()
    {
        var yrs = ProfileService.YearsOfExperience(_p);
        var latest = LatestRole;
        var top = TopMatchedAchievements(3);
        var intro = new List<string>();
        intro.Add(_p.Headline.Length > 0
            ? $"I'm {Article(_p.Headline)} {Lower(_p.Headline)}{(yrs > 0 ? $" with over {yrs} years of experience" : "")}."
            : yrs > 0 ? $"I have over {yrs} years of professional experience." : "I bring a strong mix of practical experience and fast learning.");
        if (latest != null) intro.Add($"Most recently I've been {Article(latest.Title)} {latest.Title} at {latest.Company}{(top.Count > 0 && top[0].ParentId == latest.Id ? ", where " + Clause(top[0]) : "")}.");
        if (top.Count > 0 && (latest == null || top[0].ParentId != latest.Id)) intro.Add(Speak(top[0]));
        intro.Add($"{Cap(Role)} is a natural next step because it builds directly on {StrengthPhrase()}.");
        Add("INTRODUCTION", AnswerMode.Verified, "Tell me about yourself.", new() { "Walk me through your background", "Introduce yourself", "Tell us a bit about your career", "Walk me through your CV" }, intro.Take(4).ToList(), 0.8,
            coach: new() { "PRESENT", "PROOF", "WHY THIS ROLE" }, structure: "Who I am now → strongest proof → why this role", storyIds: top.Select(f => "s" + f.Id).ToList());

        Add("MOTIVATION", AnswerMode.Hypothetical, $"Why do you want to work at {Company}?", new() { $"Why {Company}", "Why this company", "What do you know about us", "Why do you want this job" },
            new()
            {
                _jd.About.Count > 0 ? $"What attracts me is {Lower(KeyPhraseShort(_jd.About[0], 14))}." : $"What attracts me is the chance to contribute to {Company}'s growth in this specific area.",
                $"The {Role} role connects directly with what I do best: {StrengthPhrase()}.",
                "I'd like to bring that experience into a team where I can have clear, measurable impact.",
            }, 0.6, coach: new() { Upper(Company), "FIT", "IMPACT" }, structure: "What attracts me → why I fit → impact I'd make");

        Add("MOTIVATION", AnswerMode.Verified, "Why should we hire you?", new() { "What makes you the right candidate", "Why you", "What would you bring to this role" },
            top.Take(2).Select(Speak).Append($"I'd bring that combination of results and {Lower(StrengthPhrase())} straight into the {Role} role.").ToList(), top.Count >= 2 ? 0.8 : 0.6,
            coach: new() { "RESULTS", "FIT", "VALUE" }, structure: "Proof 1 → proof 2 → value for you", storyIds: top.Take(2).Select(f => "s" + f.Id).ToList());

        var skills = _facts.Values.Where(f => f.Kind == FactKind.Skill).Select(f => f.Text).Take(3).ToList();
        if (skills.Count > 0 && top.Count > 0)
            Add("GENERAL_BUSINESS", AnswerMode.Verified, "What are your strengths?", new() { "Your main strengths", "What are you good at" },
                new() { $"My core strengths are {Join(skills.Select(Lower))}.", Speak(top[0]), "I combine that with a strong focus on clear priorities and measurable outcomes." }, 0.75,
                coach: new() { Upper(skills[0]), "PROOF", "OUTCOMES" }, structure: "Strength → proof → how I work");

        Add("GENERAL_BUSINESS", AnswerMode.Bridge, "What is your biggest weakness?", new() { "Your weaknesses", "What do you need to improve" },
            new()
            {
                _gaps.Count > 0 ? $"An area I'm actively strengthening is {_gaps[0]}." : "I sometimes take on too much myself instead of delegating early.",
                _gaps.Count > 0 ? "I'm closing that gap through focused learning and by working closely with experts on real problems." : "I've learned to agree priorities up front and hand over ownership sooner.",
                "I'm open about it, because it keeps expectations realistic and helps me improve faster.",
            }, 0.7, coach: new() { "HONEST", "ACTION", "PROGRESS" }, structure: "Real area → what I'm doing → progress");

        Add("GENERAL_BUSINESS", AnswerMode.Hypothetical, "What would you do in your first 90 days?", new() { "30 60 90 day plan", "First three months", "How would you start in the role" },
            new()
            {
                $"In the first thirty days I'd learn the product, the customers and the team's priorities{(_jd.Responsibilities.Count > 0 ? ", especially " + Lower(PreparationPipeline.KeyPhrase(_jd.Responsibilities[0], 8)) : "")}.",
                "By sixty days I'd have clarified goals and success metrics with stakeholders and prioritised the biggest opportunities.",
                "By ninety days I'd deliver first measurable improvements and set a steady rhythm of planning, review and measurement.",
            }, 0.8, coach: new() { "LEARN", "PRIORITISE", "DELIVER" }, structure: "30: learn → 60: prioritise → 90: deliver");

        var ask = QuestionsToAsk(_t, _jd);
        Add("GENERAL_BUSINESS", AnswerMode.Hypothetical, "Do you have any questions for us?", new() { "Any questions for me", "What would you like to ask us" },
            ask.Take(3).ToList(), 0.85, full: string.Join(" ", ask), coach: new() { "SUCCESS", "CHALLENGES", "TEAM" }, structure: "Success metric → biggest challenge → how the team works");

        Add("GENERAL_BUSINESS", AnswerMode.Hypothetical, "Where do you see yourself in five years?", new() { "Five year plan", "Career goals" },
            new() { $"I see myself growing into a leading expert in {Lower(StrengthPhraseShort())}, with real ownership of outcomes.", $"Ideally I'd have helped {Company} achieve clear, measurable results in this area.", "And I'd like to be developing others as well as my own skills." }, 0.65,
            coach: new() { "GROWTH", "IMPACT", "PEOPLE" }, structure: "Expertise → impact here → developing others");
    }

    private static readonly (string Topic, string Question, string[] Variants, string Lesson)[] BehaviouralSet =
    {
        ("leadership", "Tell me about a time you led a team.", new[] { "Give an example of your leadership", "How have you managed a team" }, "Clear goals and real ownership get the best from a team."),
        ("growth", "Tell me about a time you drove growth.", new[] { "Give an example of increasing revenue", "Tell me about a growth success" }, "Growth comes from combining the right priorities with consistent execution."),
        ("retention", "Tell me about a time you improved customer retention.", new[] { "How have you reduced churn", "Example of improving customer satisfaction" }, "Retention improves when you remove friction customers actually feel."),
        ("cost", "Tell me about a time you reduced costs.", new[] { "Example of cost optimisation", "How have you managed a budget" }, "Protect what customers value and cut what doesn't change outcomes."),
        ("launch", "Tell me about a product launch or initiative you led.", new[] { "Tell me about a go-to-market", "Give me a launch example" }, "Strong launches start from a clear customer and a measurable goal."),
        ("data", "Tell me about a time you used data to make a decision.", new[] { "Example of a data-driven decision", "How do you use analytics" }, "Data is most useful when it's tied to a specific decision."),
        ("partnership", "Tell me about a successful partnership or negotiation.", new[] { "Tell me about a negotiation", "Example of working with partners" }, "Good partnerships start from a shared goal and clear trade-offs."),
        ("reliability", "Tell me about a time you worked with technical teams to improve something.", new[] { "Example of working with engineers", "How have you collaborated with technical teams" }, "Reliability is a product feature, and it deserves real priority."),
        ("process", "Tell me about a time you improved a process.", new[] { "Example of process improvement", "How have you made a team more efficient" }, "Small process changes compound when you measure them."),
        ("customer", "Tell me about a time you improved the customer experience.", new[] { "Example of customer focus", "How have you improved user experience" }, "Listening to customers is the fastest way to find what to fix."),
        ("compliance", "Tell me about a time you managed regulatory or compliance risk.", new[] { "Experience with compliance", "How have you handled regulation" }, "Compliance works best when it's built in from the start."),
    };

    private void Behavioural()
    {
        foreach (var (topic, q, variants, lesson) in BehaviouralSet)
        {
            var story = PickStory(topic);
            if (story == null) continue;
            var fact = _facts.GetValueOrDefault(story.FactIds[0]);
            var bullets = new List<string>
            {
                story.Company.Length > 0 ? $"At {story.Company}, as {story.Role}, this was one of my main priorities." : "This was one of my main priorities in a previous role.",
                fact != null ? Speak(fact) : $"I {Lower(story.Action)}",
                $"What I took from it is that {Lower(lesson)}",
            };
            Add("BEHAVIOURAL", AnswerMode.Verified, q, variants.ToList(), bullets, 0.75,
                coach: new() { Upper(FirstWord(story.Company)), Upper(topic), story.Metrics.FirstOrDefault()?.ToUpperInvariant() ?? "RESULT" },
                structure: "Situation → what I did → measurable result", storyIds: new() { story.Id });
        }
        // Failure/conflict questions: approach framing (no invented incidents).
        Add("BEHAVIOURAL", AnswerMode.Hypothetical, "Tell me about a time you failed.", new() { "Describe a mistake you made", "What's a setback you learned from" },
            new() { "I'd pick a real example where I underestimated something early and had to correct course.", "The key is owning it quickly, fixing the impact and telling the people affected.", "And then changing how I work, so the same mistake doesn't happen again." }, 0.5,
            coach: new() { "OWN IT", "FIX", "LESSON" }, structure: "Real setback → how I fixed it → what changed",
            gap: "Use a real personal example; the résumé does not describe failures.");
    }

    private void PerRole()
    {
        foreach (var role in _facts.Values.Where(f => f.Kind == FactKind.Role).OrderByDescending(f => StartYear(f.Period)).Take(6))
        {
            var ach = _facts.Values.Where(f => f.ParentId == role.Id && f.Kind == FactKind.Achievement).OrderByDescending(f => f.Metrics.Count).Take(2).ToList();
            if (ach.Count == 0) continue;
            var bullets = new List<string> { $"At {role.Company} I was {role.Title}{(role.Period.Length > 0 ? ", " + role.Period.Replace(" – ", " to ") : "")}." };
            bullets.AddRange(ach.Select(Speak));
            Add("RESUME_EXPERIENCE", AnswerMode.Verified, $"Tell me about your experience at {role.Company}.", new() { $"What did you do at {role.Company}", $"Tell me about your role at {FirstWord(role.Company)}", $"Your time at {FirstWord(role.Company)}" },
                bullets, 0.85, coach: new() { Upper(FirstWord(role.Company)), Upper(FirstWord(role.Title)), ach[0].Metrics.FirstOrDefault()?.ToUpperInvariant() ?? "RESULT" },
                structure: "Role → key result → second result", storyIds: ach.Select(a => "s" + a.Id).ToList());
        }
    }

    private void Requirements()
    {
        foreach (var m in _matches.Take(30))
        {
            var phrase = PreparationPipeline.KeyPhrase(m.Requirement);
            if (phrase.Length < 3) continue;
            var keywords = ContentKeywords(phrase);
            if (!m.IsGap)
            {
                var facts = m.FactIds.Select(id => _facts.GetValueOrDefault(id)).Where(f => f != null && f.Kind is FactKind.Achievement or FactKind.Project).Cast<ProfileFact>().Take(2).ToList();
                if (facts.Count > 0)
                    Add("RESUME_EXPERIENCE", AnswerMode.Verified, $"What experience do you have with {phrase}?", new() { $"Tell me about your experience with {phrase}", $"Have you worked with {phrase}" },
                        facts.Select(Speak).Append($"In this role I'd apply the same approach to {phrase}, starting with clear goals and measurable results.").ToList(), 0.75,
                        coach: keywords, structure: "Direct answer → proof → how I'd apply it here", storyIds: facts.Select(f => "s" + f.Id).ToList());
            }
            else
            {
                var closest = TopMatchedAchievements(1).FirstOrDefault();
                Add("GAP_EXPERIENCE", AnswerMode.Bridge, $"Have you personally worked with {phrase}?", new() { $"Do you have experience with {phrase}", $"Have you done {phrase} before" },
                    new()
                    {
                        $"I haven't owned {phrase} directly, but I understand what it requires and how to approach it.",
                        $"I'd start by clarifying the goals, rules and risks with the experts, then turn them into clear, testable requirements.",
                        closest != null ? "My closest experience: " + Lower(Speak(closest)) : "I learn new domains quickly by working closely with specialists on real problems.",
                    }, 0.7, coach: new() { "HONEST", Upper(keywords[0]), "CLOSEST PROOF" }, structure: "Honest bridge → approach → closest evidence",
                    gap: $"Résumé shows no direct experience with: {phrase}.");
            }
            Add("CASE_STUDY", AnswerMode.Hypothetical, $"How would you approach {phrase} in this role?", new() { $"How would you handle {phrase}", $"What is your approach to {phrase}" },
                new()
                {
                    $"I'd start by clarifying the goal and the constraints around {phrase}, with the people who own it today.",
                    "Then I'd prioritise the highest-impact steps, agree success criteria and deliver in small, measurable increments.",
                    "I'd track the results closely and adjust based on what the data and the users tell us.",
                }, 0.55, coach: keywords, structure: "Goal → approach → measure");
        }
    }

    private void Responsibilities()
    {
        foreach (var r in _jd.Responsibilities.Take(20))
        {
            var phrase = PreparationPipeline.KeyPhrase(r, 10);
            if (phrase.Length < 4) continue;
            Add("PRODUCT_EXECUTION", AnswerMode.Hypothetical, $"How would you {VerbPhrase(phrase)}?", new() { $"How do you approach {phrase}", $"Walk me through how you'd handle {phrase}" },
                new()
                {
                    $"I'd begin by understanding who is affected and what success looks like for {phrase}.",
                    "Then I'd align stakeholders on priorities and break the work into clear, testable steps.",
                    "After delivery I'd measure the impact and iterate, so we keep improving rather than just shipping.",
                }, 0.55, coach: ContentKeywords(phrase), structure: "Who & why → plan → measure");
        }
    }

    public static List<string> QuestionsToAsk(InterviewTarget t, JobAnalysis jd)
    {
        var role = t.JobTitle.Length > 0 ? t.JobTitle : "this role";
        var list = new List<string>
        {
            $"How do you define success for the {role} in the first six months?",
            "What is the biggest challenge the team is facing right now?",
            "How do product, engineering and the business work together on priorities?",
            "Which metrics does leadership review most closely for this area?",
            "What does a typical week look like in this role?",
            "How is the team structured, and who would I work with most closely?",
            "What would make someone exceptional in this role after one year?",
            "How are decisions made when stakeholders disagree?",
            "What are the next big milestones on the roadmap?",
            "What are the next steps in the interview process?",
        };
        foreach (var r in jd.Responsibilities.Take(3)) list.Insert(2, $"How do you currently approach {PreparationPipeline.KeyPhrase(r, 8)}, and where would you like it to improve?");
        return list.Distinct().ToList();
    }

    public string Positioning()
    {
        var yrs = ProfileService.YearsOfExperience(_p);
        var top = TopMatchedAchievements(3).Select(f => $"{f.Company}: {Trim(f.Text, 18)}");
        return $"{_p.Headline}{(yrs > 0 ? $", {yrs}+ years" : "")}. Strongest verified evidence for this role: {string.Join(" | ", top)}. Mention evidence only when it genuinely strengthens the answer.";
    }

    // ---------------- helpers ----------------

    private List<ProfileFact> TopMatchedAchievements(int n)
    {
        var scores = new Dictionary<string, double>();
        foreach (var m in _matches.Where(m => !m.IsGap)) foreach (var id in m.FactIds) scores[id] = scores.GetValueOrDefault(id) + m.Score;
        return _facts.Values.Where(f => f.Kind == FactKind.Achievement)
            .OrderByDescending(f => scores.GetValueOrDefault(f.Id) + f.Metrics.Count * 0.3 + (StartYear(f.Period) / 10000.0))
            .Take(n).ToList();
    }

    private Story? PickStory(string topic)
    {
        var candidates = _stories.Where(s => s.Skills.Contains(topic)).OrderBy(s => _storyUse.GetValueOrDefault(s.Id)).ThenByDescending(s => s.Metrics.Count).ToList();
        var s = candidates.FirstOrDefault();
        if (s != null) _storyUse[s.Id] = _storyUse.GetValueOrDefault(s.Id) + 1;
        return s;
    }

    private string StrengthPhrase()
    {
        var topics = _stories.SelectMany(s => s.Skills).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(3).ToList();
        var nice = topics.Select(t => t switch
        {
            "growth" => "driving growth", "retention" => "improving retention", "leadership" => "leading teams", "cost" => "cost discipline",
            "launch" => "launching products", "data" => "data-driven decisions", "partnership" => "building partnerships", "reliability" => "working closely with technical teams",
            "process" => "improving processes", "customer" => "customer focus", "compliance" => "managing regulatory risk", _ => t,
        }).ToList();
        return nice.Count > 0 ? Join(nice) : "my track record of delivering results";
    }

    private string StrengthPhraseShort() => StrengthPhrase().Split(',')[0];

    private static readonly HashSet<string> PastVerbs = new(StringComparer.OrdinalIgnoreCase)
    { "led", "grew", "built", "ran", "drove", "won", "made", "set", "cut", "took", "wrote", "sold", "brought", "kept", "met", "oversaw", "spearheaded", "began", "found", "held", "taught", "lowered" };
    private static readonly HashSet<string> PassiveVerbs = new(StringComparer.OrdinalIgnoreCase) { "recognized", "recognised", "awarded", "promoted", "selected", "named", "appointed", "ranked", "certified" };

    /// <summary>Résumé bullet → speakable first-person sentence (content unchanged, ≤ ~26 words).</summary>
    public static string Speak(ProfileFact f)
    {
        var t = Regex.Replace(f.Text.Trim(), @"\s+", " ").TrimEnd('.', ';');
        t = ShortenClauses(t, 24);
        var first = t.Split(' ')[0];
        string s;
        if (PassiveVerbs.Contains(first)) s = "I was " + Lower(t);
        else if (PastVerbs.Contains(first) || first.EndsWith("ed", StringComparison.OrdinalIgnoreCase)) s = "I " + Lower(t);
        else s = (f.Company.Length > 0 ? $"At {f.Company}: " : "") + t;
        return Cap(s) + ".";
    }

    private static string Clause(ProfileFact f) { var s = Speak(f); return Lower(s.StartsWith("I ") ? s : s).TrimEnd('.'); }

    private static string ShortenClauses(string t, int maxWords)
    {
        var words = t.Split(' ');
        if (words.Length <= maxWords) return t;
        foreach (var sep in new[] { ", resulting in", ", which", ", leading to", " through ", " via ", " by ", ", " })
        {
            var i = t.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 20 && t[..i].Split(' ').Length <= maxWords && Regex.IsMatch(t[..i], @"\d") == Regex.IsMatch(t, @"\d")) return t[..i];
        }
        return string.Join(' ', words.Take(maxWords));
    }

    private void Add(string category, AnswerMode mode, string question, List<string> variants, List<string> bullets, double confidence,
        List<string>? coach = null, string? structure = null, List<string>? storyIds = null, string gap = "", string full = "", List<string>? follow = null)
    {
        bullets = bullets.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).ToList();
        if (bullets.Count < 2) return;
        var kw = (coach ?? ContentKeywords(question)).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.ToUpperInvariant()).Distinct().ToList();
        foreach (var d in new[] { "GOAL", "APPROACH", "RESULT" }) if (kw.Count < 3 && !kw.Contains(d)) kw.Add(d);
        _out.Add(new BankQuestion
        {
            CanonicalQuestion = question, Category = category, Intent = question.TrimEnd('?', '.'),
            Keywords = TextNormalizer.Tokenize(question + " " + string.Join(" ", variants)).Distinct().Take(14).ToList(),
            SemanticVariants = variants, AnswerModeRaw = mode.ToString().ToUpperInvariant(), ShortBullets = bullets.Take(4).ToList(),
            OptionalFullAnswer = full.Length > 0 ? full : string.Join(" ", bullets), CandidateEvidence = storyIds ?? new(), StoryIds = storyIds ?? new(),
            GapWarning = gap, FollowUpQuestions = follow ?? new(), CoachKeywords = kw.Take(3).ToList(),
            AnswerStructure = structure ?? (mode switch { AnswerMode.Verified => "Situation → what I did → result", AnswerMode.Bridge => "Honest bridge → approach → closest evidence", _ => "Direct answer → approach → measure" }),
            Language = "en", Confidence = confidence,
        });
    }

    private static BankQuestion Renumber(BankQuestion q, int n) => new()
    {
        QuestionId = $"T{n:000}", CanonicalQuestion = q.CanonicalQuestion, Category = q.Category, Intent = q.Intent, Keywords = q.Keywords,
        SemanticVariants = q.SemanticVariants, AnswerModeRaw = q.AnswerModeRaw, ShortBullets = q.ShortBullets, OptionalFullAnswer = q.OptionalFullAnswer,
        CandidateEvidence = q.CandidateEvidence, GapWarning = q.GapWarning, TechnicalNotes = q.TechnicalNotes, ProductNotes = q.ProductNotes,
        FollowUpQuestions = q.FollowUpQuestions, CoachKeywords = q.CoachKeywords, AnswerStructure = q.AnswerStructure, StoryIds = q.StoryIds,
        Language = q.Language, Confidence = q.Confidence,
    };

    private static readonly HashSet<string> NotKeywords = new(StringComparer.OrdinalIgnoreCase)
    { "approach", "would", "role", "experience", "work", "working", "personally", "handle", "tell", "time", "team", "worked", "strong", "proven", "ability", "build", "building", "using", "across", "within", "ensure", "drive", "manage", "own", "owning" };

    public static List<string> ContentKeywords(string text)
    {
        var words = Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}\-]+").Select(m => m.Value).Where(w => w.Length > 2 && !NotKeywords.Contains(w))
            .Where(w => !TextNormalizer.Tokenize(w).Count.Equals(0)).ToList();
        var ranked = words.OrderByDescending(w => char.IsUpper(w[0]) ? 2 : 0).ThenByDescending(w => w.Length).Select(w => w.ToUpperInvariant()).Distinct().Take(3).ToList();
        foreach (var d in new[] { "GOAL", "APPROACH", "MEASURE" }) if (ranked.Count < 3) ranked.Add(d);
        return ranked;
    }

    private static string VerbPhrase(string phrase)
    {
        var first = phrase.Split(' ')[0].ToLowerInvariant();
        var map = new Dictionary<string, string> { ["building"] = "build", ["managing"] = "manage", ["owning"] = "own", ["driving"] = "drive", ["defining"] = "define", ["leading"] = "lead", ["translating"] = "translate", ["implementing"] = "implement", ["overseeing"] = "oversee", ["developing"] = "develop", ["creating"] = "create", ["working"] = "work" };
        if (map.TryGetValue(first, out var v)) return v + phrase[first.Length..];
        if (Regex.IsMatch(first, @"^(build|manage|own|drive|define|lead|translate|implement|oversee|develop|create|work|improve|design|deliver|run|scale|support|coordinate|ensure|maintain)s?$"))
            return first.TrimEnd('s') + phrase[first.Length..];
        return "approach " + phrase;
    }

    private static string KeyPhraseShort(string s, int n) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(n)).TrimEnd('.', ',');
    private static string Trim(string s, int n) { var w = s.Split(' '); return w.Length <= n ? s.TrimEnd('.') : string.Join(' ', w.Take(n)) + "…"; }
    private static string FirstWord(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? s;
    private static string Upper(string s) => s.ToUpperInvariant();
    private static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;
    private static string Cap(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    private static string Article(string s) => Regex.IsMatch(s, @"^[aeiouAEIOU]") ? "an" : "a";
    private static string Join(IEnumerable<string> items) { var l = items.ToList(); return l.Count <= 1 ? string.Join("", l) : string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1]; }
    private static int StartYear(string period) { var m = Regex.Match(period, @"(19|20)\d{2}"); return m.Success ? int.Parse(m.Value) : 0; }
}
