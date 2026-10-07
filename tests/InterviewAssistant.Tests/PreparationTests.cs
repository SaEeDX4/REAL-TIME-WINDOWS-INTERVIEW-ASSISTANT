using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Persistence;
using InterviewAssistant.Core.Preparation;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

public class PreparationTests
{
    private readonly ITestOutputHelper _out;
    public PreparationTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task PreparationRunsAllStagesAndBuildsAdaptiveBank()
    {
        var p = Fixtures.ShervinProfile();
        var t = Fixtures.TeroxxTarget(p.Id);
        var progress = new List<PreparationProgress>();
        var pack = await new PreparationPipeline(Fixtures.Assets).RunAsync(p, t, progress: new SyncProgress(progress));
        foreach (var l in pack.StageLog) _out.WriteLine(l);
        Assert.Equal(PreparationPipeline.Stages.Length, progress.Select(x => x.Stage).Distinct().Count());
        Assert.InRange(pack.Questions.Count, 50, 150);
        Assert.Equal("Product Owner - Abloxx (XAB)", t.JobTitle);
        Assert.Equal("Teroxx", t.Company);
        Assert.Equal(PreparationState.Prepared, t.Preparation);
        Assert.Contains(pack.Gaps, g => g.Contains("ledger", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pack.Terminology, x => x == "MiCAR" || x == "MiCA");
        Assert.True(pack.QuestionsToAsk.Count >= 10);
        Assert.All(pack.Questions, q => { Assert.Equal(3, q.CoachKeywords.Count); Assert.Contains("→", q.AnswerStructure); Assert.InRange(q.ShortBullets.Count, 2, 4); });
        Assert.Contains(pack.Questions, q => q.Mode == AnswerMode.Bridge && q.CanonicalQuestion.Contains("ledger", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pack.Questions, q => q.CanonicalQuestion == "Tell me about your experience at Arzif Crypto Exchange.");
    }

    [Fact]
    public async Task VerifiedPreparedAnswersOnlyUseConfirmedFactsAndPassValidator()
    {
        var pack = await Fixtures.ShervinPackAsync();
        var v = new FactValidator(pack.Profile, pack.Context);
        var problems = pack.Questions.Select(q => (q, r: v.Validate(q.ShortBullets, q.Mode, AnswerStyle.Technical))).Where(x => x.r.HasFactualIssues).ToList();
        foreach (var (q, r) in problems) _out.WriteLine($"{q.CanonicalQuestion}: {string.Join("; ", r.Issues.Select(i => i.Kind + " " + i.Detail))}");
        Assert.Empty(problems);
        var intro = pack.Questions.Single(q => q.CanonicalQuestion == "Tell me about yourself.");
        foreach (var b in intro.ShortBullets) _out.WriteLine("• " + b);
        Assert.Contains(intro.ShortBullets, b => b.Contains("Gabrielyte UAB"));
    }

    [Fact]
    public async Task PartiallyConfirmedProfileNeverLeaksUnconfirmedMetrics()
    {
        var p = Fixtures.ShervinProfile(confirm: false);
        p.ConfirmAll(f => f.Kind == FactKind.Role || f.Kind == FactKind.Skill || f.Company == "Arzif Crypto Exchange");
        var pack = await new PreparationPipeline(Fixtures.Assets).RunAsync(p, Fixtures.TeroxxTarget(p.Id));
        var all = string.Join(" ", pack.Questions.SelectMany(q => q.ShortBullets.Append(q.OptionalFullAnswer)));
        Assert.DoesNotContain("128", all);      // Epson metric (unconfirmed)
        Assert.DoesNotContain("12%", all);      // Philips metric (unconfirmed)
        Assert.Contains("60,000", all);         // Arzif metric (confirmed)
        Assert.Contains(pack.Warnings, w => w.Contains("not confirmed"));
    }

    [Fact]
    public async Task PreparationRequiresConfirmedFacts()
    {
        var p = Fixtures.ShervinProfile(confirm: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PreparationPipeline(Fixtures.Assets).RunAsync(p, Fixtures.TeroxxTarget(p.Id)));
    }

    [Fact]
    public async Task PackDrivesTheLiveEngineEndToEnd()
    {
        var pack = await Fixtures.ShervinPackAsync();
        var kb = pack.ToKnowledgeBase();
        var engine = new InterviewEngine(kb, null, null, new EngineOptions { AutoTick = false });
        await engine.SubmitManualQuestionAsync("Tell me about your experience at Arzif");
        var a = engine.Current!;
        Assert.Equal(AnswerSource.Cache, a.Source);
        Assert.Contains(a.Bullets, b => b.Contains("60,000"));
        Assert.Contains("Teroxx", kb.Context.BuildTranscriptionPrompt());
        Assert.DoesNotContain("Teroxx", PromptTemplates.LiveAnswer);
    }

    [Fact]
    public async Task LowConfidenceTemplateAnswersAreReferenceNotInstant()
    {
        var pack = await Fixtures.ShervinPackAsync();
        var provider = new FakeProvider();
        var engine = new InterviewEngine(pack.ToKnowledgeBase(), null, provider, new EngineOptions { AutoTick = false });
        var templ = pack.Questions.First(q => q.Confidence < 0.7);
        await engine.SubmitManualQuestionAsync(templ.CanonicalQuestion);
        Assert.Equal(AnswerSource.Llm, engine.Current!.Source);
        Assert.Contains("PREPARED REFERENCE ANSWER", provider.Calls[0][1].Content);
    }

    [Fact]
    public async Task MultipleProfilesAndTargetsAreIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "ia-ws-" + Guid.NewGuid().ToString("n"));
        var store = new WorkspaceStore(root, new NoProtection());
        var shervin = Fixtures.ShervinProfile();
        var maria = new CandidateProfileRecord();
        ProfileService.AddResume(maria, ProfileService.CreateDocument("m.txt", DocumentKind.Resume, Fixtures.Text("resumes", "maria_keller_de.txt")));
        maria.ConfirmAll();
        store.SaveProfile(shervin); store.SaveProfile(maria);
        var t1 = Fixtures.TeroxxTarget(shervin.Id);
        var t2 = new InterviewTarget { ProfileId = shervin.Id, JobTitle = "Head of Growth", Company = "Contoso", JobDescriptionText = "Responsibilities\n- Drive user acquisition and retention.\nRequirements\n- Growth marketing experience." };
        var t3 = new InterviewTarget { ProfileId = maria.Id, JobDescriptionText = Fixtures.Text("jobs", "backend_engineer_de.txt"), ExpectedLanguage = "de" };
        var pipe = new PreparationPipeline(Fixtures.Assets);
        foreach (var (p, t) in new[] { (shervin, t1), (shervin, t2), (maria, t3) }) { store.SavePack(await pipe.RunAsync(p, t)); store.SaveTarget(t); }

        Assert.Equal(2, store.Profiles().Count);
        Assert.Equal(2, store.Targets(shervin.Id).Count);
        Assert.Single(store.Targets(maria.Id));
        var mariaPack = store.GetPack(t3.Id)!;
        var mariaText = string.Join(" ", mariaPack.Questions.SelectMany(q => q.ShortBullets)) + mariaPack.Context.Positioning;
        Assert.DoesNotContain("Arzif", mariaText);
        Assert.DoesNotContain("Teroxx", mariaText);
        Assert.Equal("de", mariaPack.Context.InterviewLanguage);
        Assert.Contains("PayFlow GmbH", mariaPack.Profile.Experience.Select(e => e.Company));
        Assert.Equal("Contoso", store.GetPack(t2.Id)!.Context.CompanyName);

        store.DeleteProfile(shervin.Id);
        Assert.Null(store.GetPack(t1.Id));
        Assert.Empty(store.Targets(shervin.Id));
        Assert.Single(store.Profiles());
        Directory.Delete(root, true);
    }

    private sealed class SyncProgress : IProgress<PreparationProgress>
    {
        private readonly List<PreparationProgress> _l; public SyncProgress(List<PreparationProgress> l) => _l = l;
        public void Report(PreparationProgress v) => _l.Add(v);
    }
}
