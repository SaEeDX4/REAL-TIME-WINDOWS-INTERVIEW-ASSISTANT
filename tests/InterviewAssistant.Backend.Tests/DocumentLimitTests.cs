using System.Net;
using System.Net.Http.Json;
using InterviewAssistant.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace InterviewAssistant.Backend.Tests;

public sealed class SmallDocumentCapFactory : ApiFactory
{
    protected override void Customize(IWebHostBuilder b) => b.UseSetting("Safety:MaxDocumentsPerKind", "3");
}

/// <summary>Targets/reports are bounded per account (no unbounded storage through the sync API).</summary>
public class DocumentLimitTests : IClassFixture<SmallDocumentCapFactory>
{
    private readonly SmallDocumentCapFactory _f;
    public DocumentLimitTests(SmallDocumentCapFactory f) => _f = f;

    [Fact]
    public async Task TargetsPerAccountAreCapped()
    {
        var c = _f.ClientFor(Guid.NewGuid());
        for (int i = 0; i < 3; i++)
            (await c.PutAsJsonAsync($"/api/v1/targets/{Guid.NewGuid()}", new UpsertDocumentRequest(null, Helpers.Doc(new { jobTitle = "PO" }), null))).EnsureSuccessStatusCode();
        var r = await c.PutAsJsonAsync($"/api/v1/targets/{Guid.NewGuid()}", new UpsertDocumentRequest(null, Helpers.Doc(new { jobTitle = "PO" }), null));
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal("document_limit", (await r.Error()).Code);
        // Updating an existing document is still allowed.
        var list = await (await c.GetAsync("/api/v1/targets")).Read<List<SyncDocument>>();
        (await c.PutAsJsonAsync($"/api/v1/targets/{list[0].Id}", new UpsertDocumentRequest(null, Helpers.Doc(new { jobTitle = "PO 2" }), null))).EnsureSuccessStatusCode();
    }
}
