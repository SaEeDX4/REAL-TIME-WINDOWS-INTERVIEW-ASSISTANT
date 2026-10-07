using System.IO.Compression;
using System.Text;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Ingestion;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using Xunit;

namespace InterviewAssistant.Tests;

public class ProfileIngestionTests
{
    [Fact]
    public void ResumeParserExtractsAllFiveRolesWithExactFacts()
    {
        var p = Fixtures.ShervinProfile(confirm: false);
        Assert.Equal("Shervin Fallahdoust", p.Name);
        Assert.Equal("Business Management | Marketing & Operations Leader", p.Headline);
        var roles = p.Facts.Where(f => f.Kind == FactKind.Role).ToList();
        Assert.Equal(new[] { "Gabrielyte UAB", "Arzif Crypto Exchange", "LG Electronics", "Philips", "Epson" }, roles.Select(r => r.Company));
        Assert.Equal(new[] { "Executive Director", "Business Management Lead", "Senior Product Marketing Manager", "Director of Sales and Marketing", "Sales Manager" }, roles.Select(r => r.Title));
        Assert.Equal("2019 – 2023", roles[1].Period);
        Assert.Equal("2023 – Present", roles[0].Period);
        var arzif = p.Facts.Where(f => f.ParentId == roles[1].Id).ToList();
        Assert.Equal(6, arzif.Count);
        Assert.Contains(arzif, f => f.Text.Contains("60,000 active traders") && f.Metrics.Contains("60,000"));
        Assert.Contains(arzif, f => f.Text.Contains("Binance, CoinEx, and KuCoin"));
        var epson = p.Facts.Where(f => f.ParentId == roles[4].Id).ToList();
        Assert.Contains(epson, f => f.Metrics.Contains("128") && f.Metrics.Contains("254"));
        Assert.Equal(28, p.Facts.Count(f => f.Kind == FactKind.Achievement)); // 6+6+6+5+5 bullets on the CV
        Assert.Equal(2, p.Facts.Count(f => f.Kind == FactKind.Education));
        Assert.Contains(p.Facts, f => f.Kind == FactKind.Education && f.Text.Contains("Computer Software Engineering") && f.Company == "University of Tehran");
        Assert.Equal(3, p.Facts.Count(f => f.Kind == FactKind.Language));
        Assert.Equal(10, p.Facts.Count(f => f.Kind == FactKind.Skill));
        Assert.Equal("en", p.Documents[0].Language);
    }

    [Fact]
    public void EveryFactHasProvenanceAndStartsUnverified()
    {
        var p = Fixtures.ShervinProfile(confirm: false);
        var lines = p.Documents[0].Text.Split('\n');
        Assert.All(p.Facts, f =>
        {
            Assert.Equal(FactStatus.Source, f.Status);
            Assert.False(f.IsVerifiedClaim);
            Assert.NotNull(f.Provenance);
            Assert.Equal(p.Documents[0].Id, f.Provenance!.DocumentId);
            Assert.InRange(f.Provenance.LineStart, 1, lines.Length);
        });
        var traders = p.Facts.First(f => f.Text.Contains("60,000"));
        Assert.Contains("60,000 active traders", lines[traders.Provenance!.LineStart - 1]);
    }

    [Fact]
    public void UnconfirmedFactsNeverReachTheEngineProfile()
    {
        var p = Fixtures.ShervinProfile(confirm: false);
        Assert.Empty(ProfileService.ToEngineProfile(p).Experience);
        p.ConfirmAll(f => f.Kind == FactKind.Role || f.Text.Contains("Arzif") || f.Company == "Arzif Crypto Exchange");
        var engine = ProfileService.ToEngineProfile(p);
        Assert.Equal(5, engine.Experience.Count);
        Assert.All(engine.Experience.Where(e => e.Company != "Arzif Crypto Exchange"), e => Assert.Empty(e.Achievements));
        Assert.Contains("60,000", engine.VerifiedNumbers);
        Assert.DoesNotContain("128", engine.VerifiedNumbers); // Epson achievement not confirmed
        var rejected = p.Facts.First(f => f.Text.Contains("60,000"));
        rejected.Status = FactStatus.Rejected;
        Assert.DoesNotContain(ProfileService.ToEngineProfile(p).Experience.SelectMany(e => e.Achievements), a => a.Contains("60,000"));
    }

    [Fact]
    public void AmbiguousRolesAreFlaggedNotGuessed()
    {
        var doc = ProfileService.CreateDocument("x.txt", DocumentKind.Resume, "Jane Doe\nAnalyst\n\nEXPERIENCE\nAcme Corp | 2024 – 2019\n• Did things that grew sales by 10%.\n");
        var parsed = ResumeParser.Parse(doc);
        var role = parsed.Facts.Single(f => f.Kind == FactKind.Role);
        Assert.Equal("Acme Corp", role.Company);
        Assert.Contains("missing-title", role.Flags);
        Assert.Contains("end-before-start", role.Flags);
        Assert.True(role.Confidence < 0.6);
    }

    [Fact]
    public void GermanResumeParsesWithLocalizedHeadings()
    {
        var p = new CandidateProfileRecord();
        ProfileService.AddResume(p, ProfileService.CreateDocument("maria.txt", DocumentKind.Resume, Fixtures.Text("resumes", "maria_keller_de.txt")));
        Assert.Equal("de", p.Documents[0].Language);
        Assert.Equal("Maria Keller", p.Name);
        var roles = p.Facts.Where(f => f.Kind == FactKind.Role).ToList();
        Assert.Equal(new[] { "PayFlow GmbH", "DataWerk AG" }, roles.Select(r => r.Company));
        Assert.Equal("2020 – heute", roles[0].Period);
        Assert.Contains(p.Facts, f => f.Kind == FactKind.Achievement && f.Metrics.Contains("40%"));
        Assert.Contains(p.Facts, f => f.Kind == FactKind.Skill && f.Text == "Kafka");
    }

    [Fact]
    public void DocxIsExtracted()
    {
        var bytes = BuildDocx(new[] { "JOHN SMITH", "Product Manager", "EXPERIENCE", "Product Manager", "Contoso | Seattle | 2018 – 2022", "• Grew activation by 35% through onboarding redesign." }, bulletLast: true);
        var (doc, err) = ProfileService.Upload("john.docx", bytes, DocumentKind.Resume);
        Assert.Null(err);
        var parsed = ResumeParser.Parse(doc!);
        Assert.Equal("John Smith", parsed.Name);
        var ach = parsed.Facts.Single(f => f.Kind == FactKind.Achievement);
        Assert.Equal("Contoso", ach.Company);
        Assert.Contains("35%", ach.Metrics);
    }

    [Fact]
    public void PdfIsExtracted()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var lines = new[] { "JANE DOE", "Engineering Manager", "EXPERIENCE", "Engineering Manager", "Fabrikam | Berlin | 2019 - 2024", "- Reduced incidents by 45% across 12 services." };
        double y = 780;
        foreach (var l in lines) { page.AddText(l, 11, new UglyToad.PdfPig.Core.PdfPoint(50, y), font); y -= 20; }
        var (doc, err) = ProfileService.Upload("jane.pdf", builder.Build(), DocumentKind.Resume);
        Assert.Null(err);
        var parsed = ResumeParser.Parse(doc!);
        Assert.Equal("Jane Doe", parsed.Name);
        Assert.Equal("Fabrikam", parsed.Facts.Single(f => f.Kind == FactKind.Role).Company);
        Assert.Contains(parsed.Facts, f => f.Kind == FactKind.Achievement && f.Metrics.Contains("45%"));
    }

    [Fact]
    public void ImageOnlyPdfAsksForTextInsteadOfReturningNothing()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var (doc, err) = ProfileService.Upload("scan.pdf", builder.Build(), DocumentKind.Resume);
        Assert.Null(doc);
        Assert.Contains("images", err);
    }

    [Fact]
    public void MultipleCvVersionsDoNotDuplicateFacts()
    {
        var p = Fixtures.ShervinProfile(confirm: false);
        var before = p.Facts.Count;
        ProfileService.AddResume(p, ProfileService.CreateDocument("v2.txt", DocumentKind.Resume, Fixtures.Text("resumes", "shervin_fallahdoust.txt")));
        Assert.Equal(before, p.Facts.Count);
        Assert.Equal(2, p.Documents.Count);
    }

    internal static byte[] BuildDocx(IEnumerable<string> paragraphs, bool bulletLast = false)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Entry(string name, string content) { using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(content); }
            Entry("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");
            var list = paragraphs.ToList();
            var body = string.Join("", list.Select((p, i) =>
                $"<w:p>{(bulletLast && i == list.Count - 1 ? "<w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>" : "")}<w:r><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(p.TrimStart('•', ' '))}</w:t></w:r></w:p>"));
            Entry("word/document.xml", $"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}</w:body></w:document>");
        }
        return ms.ToArray();
    }
}
