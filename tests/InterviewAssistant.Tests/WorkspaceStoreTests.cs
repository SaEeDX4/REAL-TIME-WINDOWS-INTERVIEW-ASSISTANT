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
