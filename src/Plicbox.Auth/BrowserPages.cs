using System.Net;
using Plicbox.Core;

namespace Plicbox.Auth;

/// <summary>
/// What the browser tab shows once a sign-in has handed control back to Plicbox: a small card with the Plicbox logo,
/// in the app's colours (light or dark, as the browser is), saying it worked or that it didn't finish.
/// </summary>
internal static class BrowserPages
{
    /// <summary>
    /// Google's sign-in serves this page for every answer, cancelled or refused ones too: the page itself looks at
    /// its address (?error=…) and shows the "didn't finish" text then.
    /// </summary>
    public static string SignedIn => Page(failed: false);

    /// <summary>
    /// Microsoft's sign-in shows this one when it ends in an error. MSAL passes it through string.Format (for its
    /// {0}/{1} placeholders, not used: the page reads the error from its address and shows it as plain text), so
    /// every brace is doubled.
    /// </summary>
    public static string SignInFailed => Page(failed: true).Replace("{", "{{").Replace("}", "}}");

    private static string Page(bool failed)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var okTitle = E(L.T("You're signed in"));
        var okText = E(L.T("You can close this tab and return to Plicbox."));
        var failTitle = E(L.T("Sign-in didn't finish"));
        var failText = E(L.T("Nothing was changed. Close this tab and try again in Plicbox."));
        return $$"""
            <!doctype html>
            <html lang="{{E(L.Language)}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light dark">
            <title>Plicbox</title>
            <style>
              :root { --page: #f3f3f3; --card: #ffffff; --line: #e2e2e2; --text: #1f1f1f; --muted: #616161; --ok: #1e8e3e; --fail: #c42b1c; }
              @media (prefers-color-scheme: dark) {
                :root { --page: #202020; --card: #2b2b2b; --line: #3a3a3a; --text: #ececec; --muted: #a3a3a3; --ok: #4cc26b; --fail: #ff7b72; }
              }
              * { box-sizing: border-box; }
              html, body { height: 100%; margin: 0; }
              body { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 20px; padding: 24px;
                     background: var(--page); color: var(--text); font: 15px/1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
              .card { width: 100%; max-width: 420px; padding: 40px 36px 34px; text-align: center; background: var(--card);
                      border: 1px solid var(--line); border-radius: 12px; box-shadow: 0 8px 28px rgba(0,0,0,.08); }
              .logo { position: relative; width: 64px; height: 64px; margin: 0 auto 22px; }
              .logo svg.mark { width: 64px; height: 64px; display: block; }
              .badge { position: absolute; right: -6px; bottom: -6px; width: 26px; height: 26px; border-radius: 50%;
                       background: var(--ok); border: 3px solid var(--card); display: flex; align-items: center; justify-content: center; }
              .badge svg { width: 12px; height: 12px; }
              h1 { margin: 0 0 8px; font-size: 22px; font-weight: 600; letter-spacing: -.01em; }
              p { margin: 0; color: var(--muted); }
              .detail { margin-top: 14px; font-size: 12.5px; color: var(--muted); word-break: break-word; }
              .detail:empty { display: none; }
              .brand { font-size: 12.5px; color: var(--muted); }
              .fail .badge { background: var(--fail); }
              .fail .ok, .card:not(.fail) .failed { display: none; }
            </style>
            </head>
            <body>
            <main class="card{{(failed ? " fail" : "")}}" id="card">
              <div class="logo">
                <svg class="mark" viewBox="0 0 256 256" aria-hidden="true"><defs><linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#FFAA3B"/><stop offset="1" stop-color="#EE7A0C"/></linearGradient></defs><path fill="url(#bg)" fill-rule="evenodd" d="M62 10H194A52 52 0 0 1 246 62V194A52 52 0 0 1 194 246H62A52 52 0 0 1 10 194V62A52 52 0 0 1 62 10ZM47 40H107A7 7 0 0 1 114 47V107A7 7 0 0 1 107 114H47A7 7 0 0 1 40 107V47A7 7 0 0 1 47 40ZM149 40H209A7 7 0 0 1 216 47V107A7 7 0 0 1 209 114H149A7 7 0 0 1 142 107V47A7 7 0 0 1 149 40ZM47 142H107A7 7 0 0 1 114 149V209A7 7 0 0 1 107 216H47A7 7 0 0 1 40 209V149A7 7 0 0 1 47 142Z"/></svg>
                <span class="badge" aria-hidden="true">
                  <svg class="ok" viewBox="0 0 12 12"><path d="M2.2 6.3 4.8 8.8 9.8 3.4" fill="none" stroke="#fff" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"/></svg>
                  <svg class="failed" viewBox="0 0 12 12"><path d="M3.3 3.3 8.7 8.7M8.7 3.3 3.3 8.7" fill="none" stroke="#fff" stroke-width="1.9" stroke-linecap="round"/></svg>
                </span>
              </div>
              <h1 class="ok">{{okTitle}}</h1>
              <p class="ok">{{okText}}</p>
              <h1 class="failed">{{failTitle}}</h1>
              <p class="failed">{{failText}}</p>
              <p class="detail failed" id="detail"></p>
            </main>
            <div class="brand">Plicbox</div>
            <script>
              (function () {
                var query = new URLSearchParams(location.search);
                if (!query.has("error")) return;
                document.getElementById("card").classList.add("fail");
                document.getElementById("detail").textContent = query.get("error_description") || query.get("error");
              })();
            </script>
            </body>
            </html>
            """;
    }
}
