using System.Net;
using System.Text;

namespace InterviewAssistant.Client;

/// <summary>
/// One-shot loopback redirect receiver (RFC 8252 §7.3) on 127.0.0.1 with an ephemeral port. Only accepts the
/// callback path carrying the expected state; everything else gets 404. Closes after the first valid callback.
/// </summary>
public sealed class LoopbackRedirect : IDisposable
{
    private readonly HttpListener _listener = new();
    public int Port { get; }
    public string State { get; }
    public string RedirectUri => $"http://127.0.0.1:{Port}/callback?s={Uri.EscapeDataString(State)}";

    public LoopbackRedirect(string state, int? port = null)
    {
        State = state;
        for (int attempt = 0; ; attempt++)
        {
            var p = port ?? Random.Shared.Next(49215, 65000);
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{p}/callback/");
                _listener.Start();
                Port = p;
                break;
            }
            catch (HttpListenerException) when (port == null && attempt < 20) { }
        }
    }

    /// <summary>Waits for the browser to hit the redirect; returns the authorization code.</summary>
    public async Task<string> WaitForCodeAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var reg = cts.Token.Register(() => { try { _listener.Stop(); } catch (ObjectDisposedException) { } });
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("Sign-in was not completed in the browser.");
            }
            var q = ctx.Request.QueryString;
            var stateOk = q["s"] == State;
            var code = q["code"];
            var error = q["error_description"] ?? q["error"];
            if (!stateOk || ctx.Request.Url?.AbsolutePath.TrimEnd('/') != "/callback")
            {
                Respond(ctx, 404, "Not found.");
                continue;
            }
            if (!string.IsNullOrEmpty(error)) { Respond(ctx, 400, "Sign-in failed. You can close this tab and try again in the app."); throw new AuthException("browser_error", error); }
            if (string.IsNullOrEmpty(code)) { Respond(ctx, 400, "Missing authorization code."); continue; }
            Respond(ctx, 200, "Signed in. You can close this tab and return to Interview Assistant.");
            return code;
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string message)
    {
        var html = "<!doctype html><meta charset=utf-8><title>Interview Assistant</title><body style=\"font-family:system-ui;padding:3em\"><p>" + WebUtility.HtmlEncode(message) + "</p>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose() { try { _listener.Close(); } catch (ObjectDisposedException) { } }
}
