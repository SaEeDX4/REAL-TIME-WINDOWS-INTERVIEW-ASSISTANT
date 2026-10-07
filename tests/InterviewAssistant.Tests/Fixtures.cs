using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Preparation;

namespace InterviewAssistant.Tests;

public static class Fixtures
{
    public static string Path(params string[] parts) => System.IO.Path.Combine(new[] { AppContext.BaseDirectory, "fixtures" }.Concat(parts).ToArray());
    public static string Text(params string[] parts) => File.ReadAllText(Path(parts));
    public static PreparationAssets Assets => PreparationAssets.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "generic-knowledge"));

    public static CandidateProfileRecord ShervinProfile(bool confirm = true)
    {
        var p = new CandidateProfileRecord { InterviewLanguage = "en" };
        ProfileService.AddResume(p, ProfileService.CreateDocument("shervin.txt", DocumentKind.Resume, Text("resumes", "shervin_fallahdoust.txt")));
        if (confirm) p.ConfirmAll();
        return p;
    }

    public static InterviewTarget TeroxxTarget(string profileId) => new()
    {
        ProfileId = profileId, JobDescriptionText = Text("jobs", "teroxx_product_owner.txt"), ExpectedLanguage = "en",
    };

    public static async Task<PreparedPack> ShervinPackAsync()
    {
        var p = ShervinProfile();
        return await new PreparationPipeline(Assets).RunAsync(p, TeroxxTarget(p.Id));
    }
}
