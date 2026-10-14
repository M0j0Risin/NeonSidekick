using System.Net;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>webui:kestrel</c> (2026-10-14): the Web UI's server on the published binary — ASP.NET Core trimmed and compiled ahead of time —
    /// started on loopback port 0 over a session with nothing in it: <c>/health</c> answers, an API without the cookie is refused, the
    /// sign-in link sets the cookie and leaves the address, the chat page then comes from memory with its policy, and a reply's Markdown
    /// renders with a link vetted. Stopped at the end.
    /// </summary>
    public static SmokeCheck ProbeWebUi()
    {
        const string name = "webui:kestrel";
        try
        {
            return Task.Run(ProbeWebUiAsync).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }

        static async Task<SmokeCheck> ProbeWebUiAsync()
        {
            string token = WebUiAuth.NewToken();
            using var session = new WebUiSession(
                () => new WebState("smoke", "", "", "", false, false, false, "", [], null, WebUiText.Version),
                _ => new WebReply(WebOutcome.Conflict),
                () => null,
                () => { });
            await using var host = new WebUiHost(session, Path.GetTempPath());
            await host.StartAsync(new WebUiBinding(WebUiAudience.Local, 0, token, IPAddress.Loopback), CancellationToken.None).ConfigureAwait(false);
            var root = new Uri("http://127.0.0.1:" + host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
            using var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = root, Timeout = TimeSpan.FromSeconds(10) };
            string health = await client.GetStringAsync("/health").ConfigureAwait(false);
            if (health != "ok")
            {
                return new SmokeCheck(name, false, "/health said " + health);
            }

            using (var refused = await client.GetAsync("/api/shopping").ConfigureAwait(false))
            {
                if (refused.StatusCode != HttpStatusCode.Unauthorized)
                {
                    return new SmokeCheck(name, false, "an API without the cookie answered " + (int)refused.StatusCode);
                }
            }

            string cookie;
            using (var signIn = await client.GetAsync(WebUiAuth.SignInPath + "?t=" + token).ConfigureAwait(false))
            {
                if (signIn.StatusCode != HttpStatusCode.SeeOther || !signIn.Headers.TryGetValues("Set-Cookie", out var set) || set.FirstOrDefault() is not { } first)
                {
                    return new SmokeCheck(name, false, "the sign-in answered " + (int)signIn.StatusCode + " with no cookie");
                }

                cookie = first.Split(';')[0];
            }

            using var page = new HttpRequestMessage(HttpMethod.Get, "/");
            page.Headers.Add("Cookie", cookie);
            using var chat = await client.SendAsync(page).ConfigureAwait(false);
            string body = await chat.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!chat.IsSuccessStatusCode || !body.Contains("chat.js", StringComparison.Ordinal) || !chat.Headers.TryGetValues("Content-Security-Policy", out _))
            {
                return new SmokeCheck(name, false, "the chat page did not come (" + (int)chat.StatusCode + ")");
            }

            string html = WebUiMarkdown.ToHtml("**bold** [ok](https://example.com) [no](javascript:alert(1))");
            if (!html.Contains("<strong>bold</strong>", StringComparison.Ordinal) || html.Contains("javascript:", StringComparison.Ordinal))
            {
                return new SmokeCheck(name, false, "the Markdown came out wrong: " + html);
            }

            await host.StopAsync().ConfigureAwait(false);
            return new SmokeCheck(name, true, "served on loopback:" + host.Port + "; health, cookie gate, sign-in, page and Markdown ok");
        }
    }
}
