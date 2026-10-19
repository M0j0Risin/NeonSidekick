using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NeonSidekick.Web;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The machine's own browser as the Web UI's page tests drive it (2026-10-15, the plan's E4): Edge (or Chrome) headless with its DevTools port,
/// one page attached, the app's <see cref="DevToolsClient"/> carrying the commands — a cookie set, a phone's width, a navigation, an expression
/// evaluated, and a wait that polls an expression with a time limit (never a fixed sleep), failing with what it last saw. Null from
/// <see cref="StartAsync"/> with no browser installed: the tests then prove nothing and pass, as <c>BrowserPdfReaderTests</c> do. The process
/// and its profile go with <see cref="DisposeAsync"/>.
/// </summary>
public sealed class HeadlessPage : IAsyncDisposable
{
    private readonly Process _process;
    private readonly DevToolsClient _client;
    private readonly string _session;
    private readonly string _profile;

    private HeadlessPage(Process process, DevToolsClient client, string session, string profile)
    {
        _process = process;
        _client = client;
        _session = session;
        _profile = profile;
    }

    /// <summary>
    /// The browser started on a blank page, or null when none is installed. <paramref name="offline"/> (2026-10-16, the YouTube tab's tests) cuts
    /// it off from everything but loopback: every name resolves to nothing and anything else goes to a dead proxy, so a page asking for YouTube's
    /// script never reaches the internet; the Web UI's 127.0.0.1 is excluded from the resolver's rule (which catches an address too), and loopback bypasses the proxy by Chromium's own
    /// rule (the PDF reader's <c>&lt;-loopback&gt;</c> is left out: it would send the page's own server to the dead proxy too).
    /// </summary>
    public static async Task<HeadlessPage?> StartAsync(bool offline = false)
    {
        string profile = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "page-" + Guid.NewGuid().ToString("N"));
        if (new HeadlessBrowser(profile).Locate("") is not { } executable)
        {
            return null;
        }

        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        string[] cutOff = offline ? ["--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1", "--proxy-server=127.0.0.1:9"] : [];
        // --disable-features=msImplicitSignin as the app's runs (2026-10-19): a fresh Edge profile signs in by itself, opening its sync prompt
        // and now and then a "Microsoft account" window on the desktop the tests run on.
        foreach (string argument in (string[])["--headless=new", "--remote-debugging-port=0", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-features=msImplicitSignin", .. cutOff, "--user-data-dir=" + profile, "about:blank"])
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start)!;
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var client = new DevToolsClient();
        var endpoint = await DevToolsClient.WaitForEndpointAsync(profile, () => process.HasExited, wait.Token);
        await client.ConnectAsync(endpoint, wait.Token);
        string? session = null;
        while (session is null && !wait.IsCancellationRequested)
        {
            session = await AttachAsync(client, wait.Token);
            if (session is null)
            {
                await Task.Delay(50, wait.Token);
            }
        }

        var page = new HeadlessPage(process, client, session!, profile);
        await page.SendAsync("Page.enable", null);
        await page.SendAsync("Runtime.enable", null);
        return page;
    }

    private static async Task<string?> AttachAsync(DevToolsClient client, CancellationToken cancellationToken)
    {
        var targets = await client.SendAsync("Target.getTargets", null, null, cancellationToken);
        foreach (var info in targets.GetProperty("targetInfos").EnumerateArray())
        {
            // Never a page the browser opened for itself (2026-10-19: a fresh Edge profile's edge://sync-confirmation-dialog/, as the app's reads met).
            if (info.GetProperty("type").ValueEquals("page") && HeadlessBrowser.IsOwnPage(info.TryGetProperty("url", out var url) ? url.GetString() : null))
            {
                string id = info.GetProperty("targetId").GetString()!;
                var attached = await client.SendAsync("Target.attachToTarget", w => { w.WriteString("targetId", id); w.WriteBoolean("flatten", true); }, null, cancellationToken);
                return attached.GetProperty("sessionId").GetString();
            }
        }

        return null;
    }

    private async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? writeParams)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await _client.SendAsync(method, writeParams, _session, wait.Token);
    }

    /// <summary>A cookie for <paramref name="url"/>'s host, as the sign-in link leaves one.</summary>
    public Task SetCookieAsync(string url, string name, string value) =>
        SendAsync("Network.setCookie", w => { w.WriteString("name", name); w.WriteString("value", value); w.WriteString("url", url); });

    /// <summary>A phone's width (and a mobile viewport), or a desktop's with <paramref name="mobile"/> off.</summary>
    public Task ViewportAsync(int width, int height = 800, bool mobile = true) =>
        SendAsync("Emulation.setDeviceMetricsOverride", w => { w.WriteNumber("width", width); w.WriteNumber("height", height); w.WriteNumber("deviceScaleFactor", 1); w.WriteBoolean("mobile", mobile); });

    /// <summary>
    /// Starts going to <paramref name="url"/> and waits for nothing: for a page that leaves on its own as it loads (2026-10-19, the first CI-like
    /// run: <c>GoAsync</c> to a Shopping page the watch pulse sends straight on to YouTube waited for a URL it never stayed at, 2 runs in 5).
    /// The caller waits on where it ends up.
    /// </summary>
    public Task NavigateAsync(string url) => SendAsync("Page.navigate", w => w.WriteString("url", url));

    /// <summary>Goes to <paramref name="url"/> and waits for its document to be complete.</summary>
    public async Task GoAsync(string url)
    {
        await NavigateAsync(url);
        await WaitAsync("document.readyState === 'complete' && location.href.startsWith(" + JsonSerializer.Serialize(url.Split('?')[0]) + ")", "the page to load");
    }

    /// <summary><paramref name="expression"/>'s value (a promise awaited), as JSON; it throwing fails the test with the page's message.</summary>
    public async Task<JsonElement> EvalAsync(string expression)
    {
        var answer = await SendAsync("Runtime.evaluate", w =>
        {
            w.WriteString("expression", expression);
            w.WriteBoolean("returnByValue", true);
            w.WriteBoolean("awaitPromise", true);
        });
        if (answer.TryGetProperty("exceptionDetails", out var failed))
        {
            throw new InvalidOperationException("The page threw on " + expression + ": " + failed.GetRawText());
        }

        return answer.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
    }

    /// <summary>The page's text for <paramref name="expression"/> (empty for a null).</summary>
    public async Task<string> TextAsync(string expression) => (await EvalAsync(expression)) is { ValueKind: JsonValueKind.String } s ? s.GetString()! : "";

    /// <summary>
    /// Polls <paramref name="condition"/> (a boolean expression) every 50 ms until true, at most <paramref name="seconds"/>; a miss fails the test with
    /// <paramref name="what"/> and the last value of <paramref name="show"/> (the body's text by default).
    /// </summary>
    public async Task WaitAsync(string condition, string what, double seconds = 10, string show = "document.body ? document.body.innerText.slice(0, 600) : ''")
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            try
            {
                if ((await EvalAsync(condition)).ValueKind == JsonValueKind.True)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // a page between two documents: the next look finds the new one
            }

            if (DateTime.UtcNow >= until)
            {
                string last;
                try
                {
                    last = (await EvalAsync(show)).ToString();
                }
                catch (InvalidOperationException e)
                {
                    last = e.Message;
                }

                throw new Xunit.Sdk.XunitException("Waited " + seconds.ToString(CultureInfo.InvariantCulture) + " s for " + what + "; the page showed: " + last);
            }

            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            // gone already
        }

        _process.Dispose();
        try
        {
            Directory.Delete(_profile, recursive: true);
        }
        catch (IOException)
        {
            // a file the browser still held: the temp folder's own
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
