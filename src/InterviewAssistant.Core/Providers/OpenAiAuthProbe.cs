using System.Net;
using System.Net.Http.Headers;

namespace InterviewAssistant.Core.Providers;

/// <summary>Validates an API key with GET /v1/models — costs no tokens.</summary>
public static class OpenAiAuthProbe
{
    public sealed record Result(bool Ok, ProviderErrorKind? Kind, string Detail, long Ms);

    public static async Task<Result> CheckAsync(string? key, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return new Result(false, ProviderErrorKind.InvalidApiKey, "No API key saved — open Settings and paste it.", 0);
        using var http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(10);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => new Result(true, null, $"Key accepted ({sw.ElapsedMilliseconds} ms)", sw.ElapsedMilliseconds),
                HttpStatusCode.Unauthorized => new Result(false, ProviderErrorKind.InvalidApiKey, "Key rejected (401) — copy a fresh key from platform.openai.com.", sw.ElapsedMilliseconds),
                HttpStatusCode.Forbidden => new Result(false, ProviderErrorKind.InvalidApiKey, "Key not permitted (403) — check project/organisation permissions.", sw.ElapsedMilliseconds),
                HttpStatusCode.TooManyRequests => new Result(false, ProviderErrorKind.RateLimited, "Rate limited or out of quota (429) — check billing.", sw.ElapsedMilliseconds),
                _ => new Result(false, ProviderErrorKind.ServerError, $"Unexpected HTTP {(int)resp.StatusCode}", sw.ElapsedMilliseconds),
            };
        }
        catch (HttpRequestException ex) { return new Result(false, ProviderErrorKind.Network, "Cannot reach api.openai.com: " + ex.Message, sw.ElapsedMilliseconds); }
        catch (TaskCanceledException) { return new Result(false, ProviderErrorKind.Timeout, "Timed out reaching api.openai.com", sw.ElapsedMilliseconds); }
    }
}
