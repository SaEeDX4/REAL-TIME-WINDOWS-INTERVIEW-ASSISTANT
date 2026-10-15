using System.IO.Compression;
using System.Text;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Persistence;
using Xunit;

namespace InterviewAssistant.Tests;

public class WorkspaceStoreTests
{
    private sealed class XorProtector : IDataProtector
    {
        public byte[] Protect(byte[] p) => p.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] c) => Protect(c);
    }

    [Fact]
    public void DataIsEncryptedAtRestAndRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "ia-ws-" + Guid.NewGuid().ToString("n"));
        var store = new WorkspaceStore(root, new XorProtector());
        var p = Fixtures.ShervinProfile();
        store.SaveProfile(p);
        var raw = File.ReadAllBytes(Directory.GetFiles(Path.Combine(root, "profiles"))[0]);
        Assert.DoesNotContain("Arzif", Encoding.UTF8.GetString(raw));
        var back = store.GetProfile(p.Id)!;
        Assert.Equal(p.Facts.Count, back.Facts.Count);
        Assert.All(back.Facts, f => Assert.Equal(FactStatus.UserConfirmed, f.Status));
        Directory.Delete(root, true);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void IdsCannotTraversePaths(string id)
    {
        var store = new WorkspaceStore(Path.Combine(Path.GetTempPath(), "ia-ws-" + Guid.NewGuid().ToString("n")), new NoProtection());
        Assert.Throws<ArgumentException>(() => store.GetProfile(id));
    }

    [Fact]
    public void ExportContainsProfileTargetsAndSessionsAndRetentionDeletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ia-ws-" + Guid.NewGuid().ToString("n"));
        var store = new WorkspaceStore(root, new NoProtection());
        var p = Fixtures.ShervinProfile();
        store.SaveProfile(p);
        var t = Fixtures.TeroxxTarget(p.Id); store.SaveTarget(t);
        store.SaveSession(new StoredSession { ProfileId = p.Id, TargetId = t.Id, StartedUtc = DateTime.UtcNow.AddDays(-100) });
        store.SaveSession(new StoredSession { ProfileId = p.Id, TargetId = t.Id });
        var ms = new MemoryStream();
        store.ExportProfile(p.Id, ms);
        using (var zip = new ZipArchive(new MemoryStream(ms.ToArray())))
        {
            Assert.NotNull(zip.GetEntry("profile.json"));
            Assert.Single(zip.Entries, e => e.FullName.StartsWith("targets/"));
            Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("sessions/")));
        }
        Assert.Equal(1, store.ApplyRetention(TimeSpan.FromDays(90), DateTime.UtcNow));
        Assert.Single(store.Sessions());
        Directory.Delete(root, true);
    }
}

public class AccountWorkspaceIsolationTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "ia-ws-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void AccountsHaveSeparateWorkspaces_AndNothingLeaksAcrossThem()
    {
        var baseDir = TempDir();
        var a = new InterviewAssistant.Core.Persistence.WorkspaceStore(InterviewAssistant.Core.Persistence.WorkspaceLocator.RootFor(baseDir, "user-a"), new InterviewAssistant.Core.Persistence.NoProtection());
        var profile = new InterviewAssistant.Core.Domain.CandidateProfileRecord { Name = "Alice" };
        InterviewAssistant.Core.Domain.ProfileService.SetPersonal(profile, "hobby", "Sailing");
        a.SaveProfile(profile);
        a.SaveSession(new InterviewAssistant.Core.Persistence.StoredSession { Id = "s1", ProfileId = profile.Id, ReportJson = "{}" });
        a.SetActive(profile.Id, null);

        var b = new InterviewAssistant.Core.Persistence.WorkspaceStore(InterviewAssistant.Core.Persistence.WorkspaceLocator.RootFor(baseDir, "user-b"), new InterviewAssistant.Core.Persistence.NoProtection());
        Assert.Empty(b.Profiles());
        Assert.Empty(b.Sessions());
        Assert.Equal((null, null), b.GetActive());
        Assert.NotEqual(a.Root, b.Root);
        Assert.DoesNotContain("user-a", a.Root);                       // no identifiers in paths
        Assert.Single(new InterviewAssistant.Core.Persistence.WorkspaceStore(InterviewAssistant.Core.Persistence.WorkspaceLocator.RootFor(baseDir, "user-a"), new InterviewAssistant.Core.Persistence.NoProtection()).Profiles());
    }

    [Fact]
    public void LegacySharedWorkspace_IsAdoptedOnceByTheFirstAccount()
    {
        var baseDir = TempDir();
        var legacy = new InterviewAssistant.Core.Persistence.WorkspaceStore(baseDir, new InterviewAssistant.Core.Persistence.NoProtection());
        legacy.SaveProfile(new InterviewAssistant.Core.Domain.CandidateProfileRecord { Name = "Legacy" });
        Assert.True(InterviewAssistant.Core.Persistence.WorkspaceLocator.AdoptLegacy(baseDir, "first"));
        Assert.False(InterviewAssistant.Core.Persistence.WorkspaceLocator.AdoptLegacy(baseDir, "second"));
        var first = new InterviewAssistant.Core.Persistence.WorkspaceStore(InterviewAssistant.Core.Persistence.WorkspaceLocator.RootFor(baseDir, "first"), new InterviewAssistant.Core.Persistence.NoProtection());
        var second = new InterviewAssistant.Core.Persistence.WorkspaceStore(InterviewAssistant.Core.Persistence.WorkspaceLocator.RootFor(baseDir, "second"), new InterviewAssistant.Core.Persistence.NoProtection());
        Assert.Equal("Legacy", Assert.Single(first.Profiles()).Name);
        Assert.Empty(second.Profiles());
        Assert.Empty(legacy.Profiles());
    }

    [Fact]
    public void Rc1ProfileMigratesWithoutLosingConfirmedFacts()
    {
        var baseDir = TempDir();
        var store = new InterviewAssistant.Core.Persistence.WorkspaceStore(baseDir, new InterviewAssistant.Core.Persistence.NoProtection());
        // An RC1 profile: no schema version, no provenance fields.
        var json = """{"Id":"abc123","Name":"Shervin","Facts":[{"Id":"f1","Kind":"Role","Text":"Lead","Company":"Arzif","Status":"UserConfirmed","Provenance":{"DocumentId":"d1","LineStart":3,"LineEnd":3,"Snippet":"Lead"}},{"Id":"f2","Kind":"Summary","Text":"PO","Status":"Source"}],"Documents":[{"Id":"d1","FileName":"cv.pdf","Text":"…"}]}""";
        File.WriteAllText(Path.Combine(baseDir, "profiles", "abc123.bin"), json);
        var p = store.GetProfile("abc123")!;
        Assert.Equal(InterviewAssistant.Core.Domain.CandidateProfileRecord.CurrentSchemaVersion, p.SchemaVersion);
        var f1 = p.Facts.Single(f => f.Id == "f1");
        Assert.Equal(InterviewAssistant.Core.Domain.FactStatus.UserConfirmed, f1.Status);
        Assert.Equal(InterviewAssistant.Core.Domain.FactSource.ResumeImport, f1.Source);
        Assert.Equal("cv.pdf", f1.SourceName);
        Assert.Equal(new InterviewAssistant.Core.Domain.FactStats(2, 1, 1), p.Stats);   // one authoritative count
        store.SaveProfile(p);
        Assert.Equal(2, store.GetProfile("abc123")!.Facts.Count);
    }

    [Fact]
    public void FactStats_HeaderListAndFiltersAgreeAfterEveryChange()
    {
        var p = new InterviewAssistant.Core.Domain.CandidateProfileRecord();
        var role = InterviewAssistant.Core.Domain.ProfileService.AddManualFact(p, InterviewAssistant.Core.Domain.FactKind.Role, "Lead", company: "Arzif", confirmed: false);
        var summary = InterviewAssistant.Core.Domain.ProfileService.AddManualFact(p, InterviewAssistant.Core.Domain.FactKind.Summary, "PO with 10 years", confirmed: false);
        Assert.Equal(new InterviewAssistant.Core.Domain.FactStats(2, 0, 2), p.Stats);
        role.SetConfirmed(true, DateTime.UtcNow);
        Assert.Equal(new InterviewAssistant.Core.Domain.FactStats(2, 1, 1), p.Stats);
        p.ConfirmAll();
        Assert.Equal(new InterviewAssistant.Core.Domain.FactStats(2, 2, 0), p.Stats);
        Assert.Equal(0, p.PendingReviewCount);
        summary.SetConfirmed(false, DateTime.UtcNow);
        Assert.Equal(new InterviewAssistant.Core.Domain.FactStats(2, 1, 1), p.Stats);
        Assert.True(InterviewAssistant.Core.Domain.ProfileService.DeleteFact(p, summary.Id));
        Assert.Equal(new InterviewAssistant.Core.Domain.FactStats(1, 1, 0), p.Stats);
    }

    [Fact]
    public void PersonalAnswers_OnlyConfirmedReachTheEngineProfile()
    {
        var p = new InterviewAssistant.Core.Domain.CandidateProfileRecord { Name = "Maria" };
        InterviewAssistant.Core.Domain.ProfileService.SetPersonal(p, "hobby", "Bouldering");
        InterviewAssistant.Core.Domain.ProfileService.SetPersonal(p, "avoid_topic", "My previous salary");
        var engine = InterviewAssistant.Core.Domain.ProfileService.ToEngineProfile(p);
        Assert.Equal("Bouldering", Assert.Single(engine.Personal).Text);
        Assert.Contains("My previous salary", engine.Boundaries);
        p.Facts.Single(f => f.Category == "hobby").SetConfirmed(false, DateTime.UtcNow);
        Assert.Empty(InterviewAssistant.Core.Domain.ProfileService.ToEngineProfile(p).Personal);
        InterviewAssistant.Core.Domain.ProfileService.SetPersonal(p, "hobby", "");
        Assert.DoesNotContain(p.Facts, f => f.Category == "hobby");
    }
}
