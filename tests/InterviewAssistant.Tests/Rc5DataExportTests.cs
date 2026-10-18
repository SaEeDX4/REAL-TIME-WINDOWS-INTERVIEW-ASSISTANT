using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Persistence;
using InterviewAssistant.Core.Reports;

namespace InterviewAssistant.Tests;

/// <summary>rc.5 data export center: secret filter, readable account/profile exports, .iaprofile backup and import.</summary>
public class Rc5DataExportTests
{
    private const string FakeApiKey = "sk-proj-AbCdEfGhIjKlMnOpQrStUvWx1234";   // fake-credential: test fixture
    private const string FakeJwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private static readonly string[] Languages = { "en", "es", "fr", "de", "pt", "it", "ar", "fa", "zh", "hi" };

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "ia-rc5-export-" + Guid.NewGuid().ToString("n"));

    // ------------------------------------------------------------------ secret filter

    [Theory]
    [InlineData("password")] [InlineData("Password")] [InlineData("access_token")] [InlineData("refreshToken")] [InlineData("id_token")]
    [InlineData("apiKey")] [InlineData("api-key")] [InlineData("OPENAI_API_KEY")] [InlineData("client_secret")] [InlineData("ProviderCredential")]
    [InlineData("Authorization")] [InlineData("privateKey")] [InlineData("sessionCookie")] [InlineData("Jwt")]
    public void SecretKeyNamesAreRecognised(string key) => Assert.True(SecretFilter.IsSecretKey(key), key);

    [Theory]
    [InlineData("Text")] [InlineData("Company")] [InlineData("SecurityFlags")] [InlineData("Name")] [InlineData("email")] [InlineData("Kind")]
    [InlineData("Id")] [InlineData("meteredSeconds")] [InlineData("Terminology")] [InlineData("Positioning")] [InlineData("JobDescriptionText")]
    [InlineData("ReportJson")] [InlineData("MemoryJson")] [InlineData("Provenance")] [InlineData("planCode")]
    public void OrdinaryKeyNamesAreKept(string key) => Assert.False(SecretFilter.IsSecretKey(key), key);

    [Fact]
    public void FilterRemovesSecretKeysEverywhere_AndRedactsCredentialShapedValues()
    {
        var node = JsonNode.Parse($$"""
        {
          "account": { "id": "a1", "email": "jane@example.com", "password": "hunter2", "nested": [ { "refresh_token": "r", "keep": "yes" } ] },
          "documents": [ { "data": { "accessToken": "{{FakeJwt}}", "notes": "my key is {{FakeApiKey}} ok" } } ],
          "report": "{\"SessionId\":\"s1\",\"apiKey\":\"abc\",\"Questions\":[]}",
          "header": "Bearer abcdefghijklmnopqrstuvwxyz012345",
          "text": "Reduced checkout drop-off by 18% for 40,000 merchants."
        }
        """)!;
        Assert.True(SecretFilter.Clean(node));
        var json = node.ToJsonString();
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("refresh_token", json);
        Assert.DoesNotContain("accessToken", json);
        Assert.DoesNotContain(FakeApiKey, json);
        Assert.DoesNotContain(FakeJwt, json);
        Assert.DoesNotContain("apiKey", json);                         // inside a JSON document stored as a string
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz012345", json);
        Assert.Contains("jane@example.com", json);
        Assert.Contains("\"keep\":\"yes\"", json);
        Assert.Contains("SessionId", json);
        Assert.Contains("Reduced checkout drop-off by 18% for 40,000 merchants.", json);
        Assert.Contains(SecretFilter.Removed, json);
        Assert.False(SecretFilter.Clean(JsonNode.Parse("""{"text":"nothing secret here"}""")));
    }

    [Theory]
    [InlineData(FakeApiKey)] [InlineData(FakeJwt)] [InlineData("Authorization: Bearer abcdefghijklmnop0123456789")] [InlineData("pdl_live_apikey_01abcdefghijklmnop")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")] [InlineData("password=hunter2")] [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE\n-----END RSA PRIVATE KEY-----")]
    public void CredentialShapedValuesAreDetected(string value)
    {
        Assert.True(SecretFilter.LooksSecret(value));
        Assert.False(SecretFilter.LooksSecret(SecretFilter.Redact(value)));
    }

    [Theory]
    [InlineData("Senior Product Manager — Contoso Payments (2019 – 2024)")] [InlineData("Password management and SSO rollout")]
    [InlineData("Led a team of 6 engineers; sk-learn and pandas")] [InlineData("Tokenomics for a loyalty token")]
    public void OrdinaryTextIsNotTreatedAsSecret(string value) => Assert.False(SecretFilter.LooksSecret(value), value);

    // ------------------------------------------------------------------ human formatting and labels

    [Theory]
    [InlineData(0, "0 sec")] [InlineData(59, "59 sec")] [InlineData(60, "1 min")] [InlineData(4843, "1 hr 20 min 43 sec")]
    [InlineData(3600, "1 hr")] [InlineData(3605, "1 hr 5 sec")] [InlineData(-5, "0 sec")]
    public void DurationsReadLikeAPerson(long seconds, string expected) => Assert.Equal(expected, ExportLabels.Duration("en", seconds));

    [Fact]
    public void DurationsAreLocalized()
    {
        Assert.Equal("1 Std. 20 Min. 43 Sek.", ExportLabels.Duration("de", 4843));
        Assert.Equal("1 小时 20 分钟 43 秒", ExportLabels.Duration("zh", 4843));
        Assert.Equal("0 ثانیه", ExportLabels.Duration("fa", 0));
    }

    [Fact]
    public void ExportLabelsExistAndAreTranslatedInAllTenLanguages()
    {
        foreach (var k in ExportLabels.Keys) Assert.True(Loc.Has(k), k);
        foreach (var lang in Languages)
        {
            Assert.True(ExportLabels.IsComplete(lang), lang);
            if (lang == "en") continue;
            var t = Loc.TableFor(lang);
            // the titles and section headings are real translations, not the English fallback
            foreach (var k in new[] { "exp_acct_title", "exp_prof_title", "exp_section_devices", "exp_section_sessions", "exp_technical", "exp_section_review" })
                Assert.NotEqual(Loc.Get("en", k), t[k]);
        }
        Assert.Equal(ExportLabels.Keys.Length, ExportLabels.Keys.Distinct().Count());
    }

    // ------------------------------------------------------------------ account export (readable HTML)

    private static JsonElement SampleAccountExport() => JsonDocument.Parse($$"""
    {
      "format": "interview-assistant-export/v1", "exportedUtc": "2026-10-17T09:30:00Z",
      "account": { "id": "6f1c2a7e-0000-4000-8000-000000000001", "email": "jane@example.com", "status": "active", "createdUtc": "2026-01-05T08:00:00Z", "password": "hunter2" },
      "subscription": { "planCode": "pro_monthly", "status": "past_due", "currentPeriodEndUtc": "2026-11-01T00:00:00Z", "providerSecret": "whsec_x" },
      "devices": [
        { "name": "<b>Jane's laptop</b>", "platform": "Windows 11", "createdUtc": "2026-01-05T08:00:00Z", "lastSeenUtc": "2026-10-16T18:00:00Z", "revokedUtc": null },
        { "name": "Old PC", "platform": "Windows 10", "createdUtc": "2025-12-01T08:00:00Z", "lastSeenUtc": "2026-02-01T18:00:00Z", "revokedUtc": "2026-03-01T10:00:00Z" }
      ],
      "documents": [ { "id": "d1", "kind": "profile", "parentId": null, "data": { "Name": "Jane", "accessToken": "{{FakeJwt}}" }, "updatedUtc": "2026-10-01T10:00:00Z" },
                     { "id": "d2", "kind": "report", "parentId": "t1", "data": { "note": "{{FakeApiKey}}" }, "updatedUtc": "2026-10-02T10:00:00Z" } ],
      "usage": [ { "kind": "live_seconds", "total": 4843 }, { "kind": "answer_request", "total": 37 }, { "kind": "prep_job", "total": 2 } ],
      "sessions": [
        { "id": "11111111-2222-3333-4444-555555555555", "startedUtc": "2026-10-10T14:00:00Z", "endedUtc": "2026-10-10T14:45:30Z", "meteredSeconds": 2730, "endReason": "client_stop" },
        { "id": "66666666-7777-8888-9999-000000000000", "startedUtc": "2026-10-12T14:00:00Z", "endedUtc": null, "meteredSeconds": 59, "endReason": null }
      ]
    }
    """).RootElement.Clone();

    [Fact]
    public void AccountHtml_IsHuman_Escaped_AndHasNoSecrets()
    {
        var html = AccountExportRenderer.Render(SampleAccountExport(), "en", "Interview Assistant", zone: TimeZoneInfo.Utc);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("default-src 'none'", html);                       // self-contained: no scripts or remote content
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dir=\"ltr\"", html);
        Assert.Contains("Your account data", html);
        Assert.Contains("Live interview usage", html);
        Assert.Contains("1 hr 20 min 43 sec", html);
        Assert.Contains("45 min 30 sec", html);                             // session duration
        Assert.Contains("59 sec", html);                                    // metered time of the running session
        Assert.Contains("Payment overdue", html);                           // billing state in words
        Assert.Contains("Renews on", html);
        Assert.Contains("Stopped by you", html);
        Assert.Contains("Still running", html);
        Assert.Contains("Removed on", html);
        Assert.Contains("&lt;b&gt;Jane&#39;s laptop&lt;/b&gt;", html);      // every value HTML-encoded
        Assert.DoesNotContain("<b>Jane", html);
        Assert.Contains("<details><summary>Show technical details</summary>", html);
        Assert.Contains("6f1c2a7e-0000-4000-8000-000000000001", html);    // ids only in the technical section
        Assert.True(html.IndexOf("<details>", StringComparison.Ordinal) < html.IndexOf("6f1c2a7e", StringComparison.Ordinal));
        Assert.Contains("January 5, 2026", html);                           // dates in the user's culture (member since)
        foreach (var secret in new[] { "hunter2", "whsec_x", FakeJwt, FakeApiKey, "accessToken", "providerSecret" }) Assert.DoesNotContain(secret, html);
        Assert.False(SecretFilter.LooksSecret(html));
    }

    [Fact]
    public void AccountHtml_UsesTheDesktopPlanWordsWhenGiven()
    {
        var html = AccountExportRenderer.Render(SampleAccountExport(), "en", context: new AccountExportContext("Pro", "Pro · payment problem"), zone: TimeZoneInfo.Utc);
        Assert.Contains("<bdi>Pro</bdi>", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("Pro · payment problem"), html);
        Assert.DoesNotContain("pro_monthly", html);
    }

    [Fact]
    public void AccountHtml_EmptyExportStillRendersEverySection()
    {
        var html = AccountExportRenderer.Render(JsonDocument.Parse("{}").RootElement, "en", zone: TimeZoneInfo.Utc, nowUtc: new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        Assert.Contains("No paid subscription", html);
        Assert.Contains("0 sec", html);
        Assert.Contains("Devices (0)", html);
    }

    [Theory]
    [InlineData("en", "ltr")] [InlineData("es", "ltr")] [InlineData("fr", "ltr")] [InlineData("de", "ltr")] [InlineData("pt", "ltr")]
    [InlineData("it", "ltr")] [InlineData("ar", "rtl")] [InlineData("fa", "rtl")] [InlineData("zh", "ltr")] [InlineData("hi", "ltr")]
    public void AccountHtml_IsLocalizedAndMirroredForRtl(string lang, string dir)
    {
        var html = AccountExportRenderer.Render(SampleAccountExport(), lang, zone: TimeZoneInfo.Utc);
        Assert.Contains($"lang=\"{lang}\" dir=\"{dir}\"", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(Loc.Get(lang, "exp_acct_title")), html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(ExportLabels.Duration(lang, 4843)), html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(Loc.Get(lang, "exp_sub_past_due")), html);
        Assert.Contains("<span class=\"ltr\">jane@example.com</span>", html);   // e-mail stays left-to-right in RTL pages
        Assert.DoesNotContain("hunter2", html);
    }

    // ------------------------------------------------------------------ profile export (readable HTML)

    private static CandidateProfileRecord FixtureProfile()
    {
        var p = Fixtures.ShervinProfile(confirm: false);
        p.Name = "Shervin <Fallahdoust>";
        p.Headline = "Product Owner & payments";
        // confirm the roles and their first achievement, leave the rest to review
        foreach (var role in p.Facts.Where(f => f.Kind == FactKind.Role)) role.SetConfirmed(true, DateTime.UtcNow);
        var achievement = p.Facts.First(f => f.Kind == FactKind.Achievement && f.ParentId != null);
        achievement.SetConfirmed(true, DateTime.UtcNow);
        ProfileService.SetPersonal(p, "hobby", "Sailing <img src=x onerror=alert(1)>");
        ProfileService.SetPersonal(p, "avoid_topic", "My previous salary");
        return p;
    }

    [Fact]
    public void ProfileHtml_GroupsConfirmedExperienceByRole_MarksFactsToReview_AndEscapes()
    {
        var p = FixtureProfile();
        var html = ProfileExportRenderer.Render(p, "en", zone: TimeZoneInfo.Utc);
        Assert.Contains("Shervin &lt;Fallahdoust&gt;", html);
        Assert.Contains("Product Owner &amp; payments", html);
        Assert.Contains("Confirmed experience", html);
        var role = p.Facts.First(f => f.Kind == FactKind.Role && f.IsVerifiedClaim);
        Assert.Contains($"<h3><bdi dir=\"auto\">{System.Net.WebUtility.HtmlEncode(ProfileExportRenderer.FactText(role))}</bdi>", html);
        var achievement = p.Facts.First(f => f.Kind == FactKind.Achievement && f.IsVerifiedClaim);
        var confirmedAt = html.IndexOf(System.Net.WebUtility.HtmlEncode(achievement.Text), StringComparison.Ordinal);
        var reviewAt = html.IndexOf("<h2>Still to review", StringComparison.Ordinal);
        Assert.True(confirmedAt > 0 && confirmedAt < reviewAt, "confirmed facts are listed under their role, before the review section");
        var pending = p.Facts.First(f => f.AwaitingReview && f.Kind == FactKind.Achievement);
        Assert.True(html.IndexOf(System.Net.WebUtility.HtmlEncode(pending.Text), StringComparison.Ordinal) > reviewAt, "unconfirmed facts only in the review section");
        Assert.Contains("<span class=\"badge\">To review</span>", html);
        Assert.Contains($"Still to review ({p.Stats.ToReview})", html);
        Assert.Contains("Interview profile answers", html);
        Assert.Contains("<strong>Hobbies:</strong>", html);
        Assert.Contains("<strong>Never mention or speculate about:</strong>", html);
        Assert.Contains("Sailing &lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("Source: from shervin.txt", html);                   // provenance in words
        Assert.Contains("Source: interview profile", html);
        Assert.Contains("Source documents", html);
        Assert.Contains("<details><summary>Show technical details</summary>", html);
        Assert.DoesNotContain(System.Net.WebUtility.HtmlEncode(p.Documents[0].Text), html);   // document text is never exported in the readable profile
    }

    [Theory]
    [InlineData("fa", "rtl")] [InlineData("ar", "rtl")] [InlineData("de", "ltr")] [InlineData("hi", "ltr")]
    public void ProfileHtml_IsLocalized(string lang, string dir)
    {
        var html = ProfileExportRenderer.Render(FixtureProfile(), lang, zone: TimeZoneInfo.Utc);
        Assert.Contains($"lang=\"{lang}\" dir=\"{dir}\"", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(Loc.Get(lang, "exp_prof_title")), html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(Loc.Get(lang, "exp_section_review")), html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(Loc.Get(lang, "pc_hobby_label")), html);
    }

    [Fact]
    public void ProfileHtml_HasNoSecrets()
    {
        var p = FixtureProfile();
        ProfileService.SetPersonal(p, "answer_style", "Simple English. My key: " + FakeApiKey);
        var html = ProfileExportRenderer.Render(p, "en");
        Assert.DoesNotContain(FakeApiKey, html);
        Assert.False(SecretFilter.LooksSecret(html));
    }

    // ------------------------------------------------------------------ .iaprofile bundle

    private sealed record Seeded(WorkspaceStore Store, CandidateProfileRecord Profile, InterviewTarget Target, InterviewTarget Unprepared, StoredSession Report, StoredSession Orphan);

    private static async Task<Seeded> SeedAsync(string root)
    {
        var store = new WorkspaceStore(root, new NoProtection());
        var p = FixtureProfile();
        p.Notes = "Remember: " + FakeApiKey;
        store.SaveProfile(p);
        var t = Fixtures.TeroxxTarget(p.Id); t.JobTitle = "Product Owner"; t.Company = "Teroxx"; t.RoleNotes = "portal password=hunter2";
        store.SaveTarget(t);
        var pack = await new InterviewAssistant.Core.Preparation.PreparationPipeline(Fixtures.Assets).RunAsync(p, t);
        store.SavePack(pack);
        var t2 = new InterviewTarget { ProfileId = p.Id, JobTitle = "Head of Product", Company = "Fabrikam" };
        store.SaveTarget(t2);
        var reportId = Guid.NewGuid().ToString("n");
        var report = new StoredSession
        {
            Id = reportId, ProfileId = p.Id, TargetId = t.Id, StartedUtc = DateTime.UtcNow.AddHours(-2), EndedUtc = DateTime.UtcNow.AddHours(-1),
            ReportJson = ReportSerializer.ToJson(new InterviewReport { SessionId = reportId, ProfileLabel = p.Name, TargetLabel = t.DisplayName, StartedUtc = DateTime.UtcNow.AddHours(-2), EndedUtc = DateTime.UtcNow.AddHours(-1) }),
        };
        store.SaveSession(report);
        var orphan = new StoredSession { ProfileId = p.Id, TargetId = Guid.NewGuid().ToString("n"), StartedUtc = DateTime.UtcNow.AddDays(-3) };   // its interview was deleted
        store.SaveSession(orphan);
        store.SaveSession(new StoredSession { ProfileId = "someoneelse", TargetId = t.Id });   // another profile's session is never exported
        return new Seeded(store, store.GetProfile(p.Id)!, t, t2, report, orphan);
    }

    private static byte[] WriteBundle(Seeded s, DateTime? now = null)
    {
        var ms = new MemoryStream();
        ProfileBundle.Write(s.Store, s.Profile.Id, ms, "2.1.0-rc.5", now ?? new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
        return ms.ToArray();
    }

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        using var z = new ZipArchive(new MemoryStream(zip));
        return z.Entries.ToDictionary(e => e.FullName, e => { using var s = e.Open(); var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); });
    }

    /// <summary>Rebuilds a ZIP from entries (optionally recomputing the manifest hashes so only the later checks can catch it).</summary>
    private static byte[] Zip(Dictionary<string, byte[]> entries, bool rehash = false)
    {
        if (rehash)
        {
            var manifest = JsonNode.Parse(entries["manifest.json"])!.AsObject();
            var list = new JsonArray();
            foreach (var (name, bytes) in entries.Where(e => e.Key != "manifest.json"))
                list.Add(new JsonObject { ["path"] = name, ["size"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
            manifest["entries"] = list;
            entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries) { using var w = z.CreateEntry(name).Open(); w.Write(bytes); }
        return ms.ToArray();
    }

    private static ProfileBundleError Rejected(byte[] zip, ProfileBundleLimits? limits = null) =>
        Assert.Throws<ProfileBundleException>(() => ProfileBundle.Read(new MemoryStream(zip), limits)).Error;

    [Fact]
    public async Task Bundle_HasAManifestWithHashes_AndEveryEntry()
    {
        var root = TempRoot();
        try
        {
            var s = await SeedAsync(root);
            var entries = Entries(WriteBundle(s));
            var manifest = JsonNode.Parse(entries["manifest.json"])!;
            Assert.Equal("iaprofile", (string)manifest["format"]!);
            Assert.Equal(1, (int)manifest["version"]!);
            Assert.Equal("2.1.0-rc.5", (string)manifest["appVersion"]!);
            Assert.Equal(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc), manifest["createdUtc"]!.GetValue<DateTime>());
            Assert.Equal(2, (int)manifest["counts"]!["interviews"]!);
            Assert.Equal(1, (int)manifest["counts"]!["packs"]!);
            Assert.Equal(2, (int)manifest["counts"]!["sessions"]!);
            Assert.Equal(1, (int)manifest["counts"]!["reports"]!);
            Assert.Contains("profile.json", entries.Keys);
            Assert.Contains($"targets/{s.Target.Id}.json", entries.Keys);
            Assert.Contains($"targets/{s.Unprepared.Id}.json", entries.Keys);
            Assert.Contains($"packs/{s.Target.Id}.json", entries.Keys);
            Assert.Contains($"sessions/{s.Report.Id}.json", entries.Keys);
            Assert.Equal(7, entries.Count);                                        // manifest, profile, 2 interviews, 1 pack, 2 sessions: the other profile's session is not included
            foreach (var e in manifest["entries"]!.AsArray())
            {
                var bytes = entries[(string)e!["path"]!];
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), (string)e["sha256"]!);
                Assert.Equal(bytes.Length, (long)e["size"]!);
            }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Bundle_RoundTrip_ImportsAsANewProfileWithRemappedIds_AndNeverOverwrites()
    {
        var rootA = TempRoot(); var rootB = TempRoot();
        try
        {
            var s = await SeedAsync(rootA);
            var bytes = WriteBundle(s);
            var content = ProfileBundle.Read(new MemoryStream(bytes));
            Assert.Equal(s.Profile.Name, content.Preview.ProfileName);
            Assert.Equal(s.Profile.Facts.Count(f => f.IsReviewable), content.Preview.Facts);
            Assert.Equal((2, 1, 1), (content.Preview.Interviews, content.Preview.Packs, content.Preview.Reports));
            Assert.Equal("2.1.0-rc.5", content.Preview.AppVersion);

            // another PC (empty workspace)
            var other = new WorkspaceStore(rootB, new NoProtection());
            var imported = ProfileBundle.Import(other, content);
            AssertImported(other, s, imported);

            // the same PC: a second copy next to the original, the original untouched
            var copy = ProfileBundle.Import(s.Store, ProfileBundle.Read(new MemoryStream(bytes)), "Copy");
            Assert.NotEqual(s.Profile.Id, copy.Id);
            Assert.Equal("Copy", copy.Name);
            Assert.Equal(2, s.Store.Profiles().Count);
            Assert.Equal(s.Profile.Name, s.Store.GetProfile(s.Profile.Id)!.Name);
            Assert.Equal(2, s.Store.Targets(s.Profile.Id).Count);
            Assert.NotNull(s.Store.GetPack(s.Target.Id));
            Assert.Equal(2, s.Store.Sessions().Count(x => x.ProfileId == s.Profile.Id));
            AssertImported(s.Store, s, copy);
        }
        finally
        {
            foreach (var r in new[] { rootA, rootB }) try { Directory.Delete(r, true); } catch (IOException) { }
        }
    }

    private static void AssertImported(WorkspaceStore store, Seeded s, CandidateProfileRecord imported)
    {
        Assert.NotEqual(s.Profile.Id, imported.Id);
        Assert.Equal(JsonSerializer.Serialize(s.Profile.Facts), JsonSerializer.Serialize(imported.Facts));
        Assert.Equal(s.Profile.Documents.Select(d => d.Text), imported.Documents.Select(d => d.Text));
        var targets = store.Targets(imported.Id);
        Assert.Equal(2, targets.Count);
        Assert.DoesNotContain(targets, t => t.Id == s.Target.Id || t.Id == s.Unprepared.Id);
        var target = targets.Single(t => t.Company == "Teroxx");
        Assert.Equal(s.Target.JobDescriptionText, target.JobDescriptionText);
        var pack = store.GetPack(target.Id);
        Assert.NotNull(pack);
        Assert.Equal(target.Id, pack!.TargetId);
        Assert.Equal(imported.Id, pack.ProfileId);
        Assert.Equal(imported.Id, pack.Context.ProfileId);
        Assert.Equal(target.Id, pack.Context.TargetId);
        Assert.Null(store.GetPack(targets.Single(t => t.Company == "Fabrikam").Id));
        var sessions = store.Sessions().Where(x => x.ProfileId == imported.Id).ToList();
        Assert.Equal(2, sessions.Count);
        Assert.DoesNotContain(sessions, x => x.Id == s.Report.Id || x.Id == s.Orphan.Id);
        var report = sessions.Single(x => x.ReportJson.Length > 0);
        Assert.Equal(target.Id, report.TargetId);
        Assert.Equal(report.Id, ReportSerializer.FromJson(report.ReportJson)!.SessionId);   // report → session remapped
        Assert.Equal("", sessions.Single(x => x.ReportJson.Length == 0).TargetId);       // its interview was not in the bundle
    }

    [Fact]
    public async Task Bundle_TamperedEntryIsRejected()
    {
        var root = TempRoot();
        try
        {
            var s = await SeedAsync(root);
            var entries = Entries(WriteBundle(s));
            var profile = Encoding.UTF8.GetString(entries["profile.json"]).Replace("Shervin", "Mallory");
            entries["profile.json"] = Encoding.UTF8.GetBytes(profile);
            Assert.Equal(ProfileBundleError.Tampered, Rejected(Zip(entries)));

            var removed = Entries(WriteBundle(s));
            removed.Remove($"sessions/{s.Report.Id}.json");
            Assert.Equal(ProfileBundleError.Tampered, Rejected(Zip(removed)));

            var added = Entries(WriteBundle(s));
            added["sessions/abcdef.json"] = Encoding.UTF8.GetBytes("{}");
            Assert.Equal(ProfileBundleError.Tampered, Rejected(Zip(added)));

            // consistent hashes but a claimed count that does not match the content
            var counts = Entries(WriteBundle(s));
            var m = JsonNode.Parse(counts["manifest.json"])!; m["counts"]!["sessions"] = 7;
            counts["manifest.json"] = Encoding.UTF8.GetBytes(m.ToJsonString());
            Assert.Equal(ProfileBundleError.Tampered, Rejected(Zip(counts)));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("../evil.json")] [InlineData("targets/../../evil.json")] [InlineData("/etc/evil.json")] [InlineData("C:/Windows/evil.json")]
    [InlineData("targets\\evil.json")] [InlineData("..\\..\\evil.json")]
    public async Task Bundle_PathTraversalIsRejected(string name)
    {
        var root = TempRoot();
        try
        {
            var s = await SeedAsync(root);
            var entries = Entries(WriteBundle(s));
            entries[name] = Encoding.UTF8.GetBytes("{}");
            Assert.Equal(ProfileBundleError.UnsafePath, Rejected(Zip(entries, rehash: true)));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Bundle_UnexpectedEntriesAndBadJsonAreRejected()
    {
        var root = TempRoot();
        try
        {
            var s = await SeedAsync(root);
            var exe = Entries(WriteBundle(s)); exe["payload.exe"] = new byte[] { 0x4d, 0x5a };
            Assert.Equal(ProfileBundleError.Invalid, Rejected(Zip(exe, rehash: true)));

            var badJson = Entries(WriteBundle(s)); badJson["profile.json"] = Encoding.UTF8.GetBytes("{ not json");
            Assert.Equal(ProfileBundleError.Invalid, Rejected(Zip(badJson, rehash: true)));

            var deep = Entries(WriteBundle(s)); deep["profile.json"] = Encoding.UTF8.GetBytes(new string('[', 500) + new string(']', 500));
            Assert.Equal(ProfileBundleError.Invalid, Rejected(Zip(deep, rehash: true)));

            var wrongType = Entries(WriteBundle(s)); wrongType["profile.json"] = Encoding.UTF8.GetBytes("""{"Id":"abc","Facts":"not a list"}""");
            Assert.Equal(ProfileBundleError.Invalid, Rejected(Zip(wrongType, rehash: true)));

            // an interview that belongs to another profile
            var foreign = Entries(WriteBundle(s));
            var tName = $"targets/{s.Unprepared.Id}.json";
            var t = JsonNode.Parse(foreign[tName])!; t["ProfileId"] = "otherprofile";
            foreign[tName] = Encoding.UTF8.GetBytes(t.ToJsonString());
            Assert.Equal(ProfileBundleError.Invalid, Rejected(Zip(foreign, rehash: true)));

            // a pack for an interview that is not in the bundle
            var orphanPack = Entries(WriteBundle(s));
            orphanPack[$"packs/{Guid.NewGuid():n}.json"] = orphanPack[$"packs/{s.Target.Id}.json"];
            Assert.Equal(ProfileBundleError.Invalid, Rejected(Zip(orphanPack, rehash: true)));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Bundle_OversizeAndZipBombsAreRejected()
    {
        var root = TempRoot();
        try
        {
            var s = await SeedAsync(root);
            var bytes = WriteBundle(s);
            Assert.Equal(ProfileBundleError.TooLarge, Rejected(bytes, new ProfileBundleLimits(MaxFileBytes: 1024)));
            Assert.Equal(ProfileBundleError.TooLarge, Rejected(bytes, new ProfileBundleLimits(MaxEntryBytes: 512)));
            Assert.Equal(ProfileBundleError.TooLarge, Rejected(bytes, new ProfileBundleLimits(MaxEntries: 3)));
            Assert.Equal(ProfileBundleError.TooLarge, Rejected(bytes, new ProfileBundleLimits(MaxTotalBytes: 4096)));

            // 40 MB of zeros compresses to a few kilobytes: the uncompressed limit stops it
            var bomb = Entries(bytes);
            bomb["sessions/bomb.json"] = new byte[40 << 20];
            var zip = Zip(bomb, rehash: true);
            Assert.True(zip.Length < 1 << 20);
            Assert.Equal(ProfileBundleError.TooLarge, Rejected(zip));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Bundle_VersionFormatAndNonZipAreRejected()
    {
        var root = TempRoot();
        try
        {
            var s = await SeedAsync(root);
            var newer = Entries(WriteBundle(s));
            var m = JsonNode.Parse(newer["manifest.json"])!; m["version"] = 2;
            newer["manifest.json"] = Encoding.UTF8.GetBytes(m.ToJsonString());
            Assert.Equal(ProfileBundleError.NewerVersion, Rejected(Zip(newer)));

            var format = Entries(WriteBundle(s));
            var f = JsonNode.Parse(format["manifest.json"])!; f["format"] = "something-else";
            format["manifest.json"] = Encoding.UTF8.GetBytes(f.ToJsonString());
            Assert.Equal(ProfileBundleError.NotABundle, Rejected(Zip(format)));

            var noManifest = Entries(WriteBundle(s)); noManifest.Remove("manifest.json");
            Assert.Equal(ProfileBundleError.NotABundle, Rejected(Zip(noManifest)));

            Assert.Equal(ProfileBundleError.NotABundle, Rejected(Encoding.UTF8.GetBytes("this is not a zip file")));
            Assert.Equal(ProfileBundleError.NotABundle, Rejected(Array.Empty<byte>()));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task NoSecretsInAnyProfileExport()
    {
        var root = TempRoot();
        try
        {
            var s = await SeedAsync(root);
            string[] secrets = { FakeApiKey, "hunter2" };
            var bundle = Entries(WriteBundle(s));
            foreach (var (name, bytes) in bundle)
            {
                var text = Encoding.UTF8.GetString(bytes);
                foreach (var secret in secrets) Assert.DoesNotContain(secret, text);
                Assert.False(SecretFilter.LooksSecret(text), name);
            }
            var raw = new MemoryStream();
            s.Store.ExportProfile(s.Profile.Id, raw);
            foreach (var (name, bytes) in Entries(raw.ToArray()))
            {
                var text = Encoding.UTF8.GetString(bytes);
                foreach (var secret in secrets) Assert.DoesNotContain(secret, text);
                Assert.False(SecretFilter.LooksSecret(text), name);
            }
            var html = ProfileExportRenderer.Render(s.Profile, "en");
            foreach (var secret in secrets) Assert.DoesNotContain(secret, html);
            // the import of a filtered bundle still works (nothing structural was removed)
            Assert.NotNull(ProfileBundle.Read(new MemoryStream(WriteBundle(s))));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    // ------------------------------------------------------------------ desktop wiring (source level)

    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "InterviewAssistant.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
        }
    }

    private static string App(string path) => File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.App", path));

    [Fact]
    public void DesktopWiring_ExportChoiceDialogs_AndImportBackupAction()
    {
        var xaml = App("HomeWindow.xaml");
        var actions = xaml[xaml.IndexOf("x:Name=\"ProfileActions\"", StringComparison.Ordinal)..xaml.IndexOf("x:Name=\"ProfileDetail\"", StringComparison.Ordinal)];
        int At(string s) => actions.IndexOf(s, StringComparison.Ordinal);
        Assert.True(At("ImportBackupBtn") > At("ExportProfileBtn") && At("ImportBackupBtn") < At("Style=\"{StaticResource Divider}\""), "import is a secondary action next to export");
        Assert.Contains("Click=\"ImportProfileBackup_Click\"", actions);
        Assert.Matches("x:Name=\"ImportBackupBtn\" Style=\"\\{StaticResource Btn.Form\\}\"", actions);

        var cs = App("HomeWindow.xaml.cs");
        Assert.Contains("await ExportAccountDataAsync();", cs);
        Assert.Contains("await ExportProfileAsync();", cs);
        var export = App("HomeWindow.DataExport.cs");
        Assert.Contains("ExportDialog.ChooseAccountFormatAsync(this)", export);
        Assert.Contains("ExportDialog.ChooseProfileFormatAsync(this", export);
        Assert.Contains("ExportDialog.ConfirmImportAsync(this, content.Preview)", export);
        Assert.Contains("ProfileLimitReachedAsync()", export);
        Assert.Contains("SecretFilter.Clean(export)", export);
        Assert.True(export.IndexOf("ConfirmImportAsync", StringComparison.Ordinal) < export.IndexOf("ProfileBundle.Import(", StringComparison.Ordinal), "preview before import");
        var dialog = App(Path.Combine("Views", "ExportDialog.cs"));
        Assert.Contains("AppDialog.ShowAsync", dialog);
        Assert.DoesNotContain("MessageBox", dialog + export);
    }
}
