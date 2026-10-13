using System.Net;
using System.Security.Cryptography;
using InterviewAssistant.Backend.Infrastructure;

namespace InterviewAssistant.Backend.Endpoints;

/// <summary>
/// The only HTML this API serves. /pay is the Paddle "default payment link": Paddle appends ?_ptxn=txn_… and Paddle.js
/// opens the hosted checkout for that server-created transaction (card data is entered only into Paddle's frame).
/// /auth/confirmed is where the sign-up confirmation e-mail lands. Neither page reads or stores any token.
/// </summary>
public static class PublicPages
{
    public static IResult Checkout(HttpContext http, PaddleOptions paddle)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        http.Response.Headers["Content-Security-Policy"] =
            $"default-src 'none'; script-src 'nonce-{nonce}' https://cdn.paddle.com https://*.paddle.com; frame-src https://*.paddle.com; " +
            "connect-src https://*.paddle.com; style-src 'unsafe-inline' https://*.paddle.com; img-src https: data:; font-src https: data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        var configured = paddle.ClientToken.Length > 0;
        var txn = http.Request.Query["_ptxn"].ToString();
        var validTxn = txn.StartsWith("txn_", StringComparison.Ordinal) && txn.Length < 64 && txn.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        string body;
        if (!configured) body = "<h1>Checkout unavailable</h1><p>Checkout is not available right now. Please try again later or contact support.</p>";
        else if (!validTxn) body = "<h1>Upgrade from the app</h1><p>Open Interview Assistant → Account → Upgrade to start a checkout.</p>";
        else body = $@"<h1 id=t>Opening secure checkout…</h1><p id=m>Payment is handled by Paddle, our reseller and merchant of record.</p>
<script src=""https://cdn.paddle.com/paddle/v2/paddle.js""></script>
<script nonce=""{nonce}"">
{(paddle.IsSandbox ? "Paddle.Environment.set('sandbox');" : "")}
Paddle.Initialize({{ token: {System.Text.Json.JsonSerializer.Serialize(paddle.ClientToken)}, eventCallback: function (e) {{
  if (e.name === 'checkout.completed') {{ document.getElementById('t').textContent = 'Payment complete'; document.getElementById('m').textContent = 'Return to Interview Assistant — your plan updates within a minute.'; }}
  if (e.name === 'checkout.closed') {{ document.getElementById('t').textContent = 'Checkout closed'; document.getElementById('m').textContent = 'You can close this tab and return to the app.'; }}
}} }});
</script>";
        return Page("Checkout — Interview Assistant", body);
    }

    public static IResult EmailConfirmed(HttpContext http)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        http.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        // Drop any token fragment from the address bar (it is never sent to the server and never stored here).
        var body = $@"<h1>E-mail confirmed</h1><p>Return to Interview Assistant and sign in with your e-mail and password.</p>
<script nonce=""{nonce}"">if (location.hash) history.replaceState(null, '', location.pathname);</script>";
        return Page("E-mail confirmed — Interview Assistant", body);
    }

    private static IResult Page(string title, string body) => Results.Content(
        $@"<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=""width=device-width,initial-scale=1""><meta name=robots content=noindex>
<title>{WebUtility.HtmlEncode(title)}</title><style>body{{font-family:system-ui,Segoe UI,sans-serif;max-width:560px;margin:12vh auto;padding:0 24px;color:#1f2328}}h1{{font-size:22px}}p{{color:#57606a;line-height:1.5}}</style></head><body>{body}</body></html>",
        "text/html; charset=utf-8");
}
