using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Web;

/// <summary>
/// What a headless run produced: the rendered DOM, or null with the reason. Since 2026-10-11 a page that settled with no bot check
/// showing also brings the address it ended on (<see cref="FinalUrl"/>, <c>""</c> when unknown) and the browser's cookies for the
/// host asked for and that one (<see cref="Cookies"/>), for the HTTP client's jar.
/// </summary>
public sealed record BrowserDump(string? Html, string Detail, string FinalUrl = "", IReadOnlyList<BrowserCookie>? Cookies = null);

/// <summary>What a print-to-PDF run produced (2026-10-03): whether the PDF is at the output path, else why not.</summary>
public sealed record BrowserPdf(bool Ok, string Detail);

/// <summary>The headless-browser seam: where the browser is, and a page through it. <see cref="HeadlessBrowser"/> in the app, a fake in tests.</summary>
public interface IHeadlessBrowser
{
    /// <summary>The executable to run: <paramref name="configuredPath"/> when it is a file, else the first of the known browsers installed; null for none.</summary>
    string? Locate(string configuredPath);

    /// <summary>
    /// The browser <see cref="Locate"/> finds for <paramref name="configuredPath"/>, as the web requests claim it (2026-10-11):
    /// its brand and version, <see cref="BrowserIdentity.Fallback"/> with none.
    /// </summary>
    BrowserIdentity Identify(string configuredPath);

    /// <summary>The DOM of <paramref name="url"/> after the page ran its scripts, through <paramref name="executable"/>.</summary>
    Task<BrowserDump> DumpDomAsync(string executable, Uri url, CancellationToken cancellationToken);

    /// <summary><paramref name="page"/> printed to a PDF at <paramref name="outputPath"/> through <paramref name="executable"/> (2026-10-03).</summary>
    Task<BrowserPdf> PrintToPdfAsync(string executable, Uri page, string outputPath, CancellationToken cancellationToken);
}

/// <summary>
/// A real browser for the pages the HTTP client cannot read — a script shell that renders
/// client-side, a gate that reads the TLS fingerprint — run as Chromium's <c>--headless=new</c>:
/// Edge (always on Windows 11), Chrome or Brave, found in their standard install
/// folders or named by the setting <c>Web browser path</c>. <b>The second <c>Process.Start</c> site in the app</b> (the
/// editor opener is the first): no shell, no window, a throwaway profile folder under the temp
/// directory — without one a running Edge takes the URL over and this process exits with nothing —
/// one run at a time, killed with its tree at <see cref="Timeout"/>.
/// <para>
/// Since 2026-10-11 (the user's ask: get past more bot checks) a page is read over the DevTools protocol (<see cref="DevToolsClient"/>)
/// rather than <c>--dump-dom</c>, which dumps at the load event or after fast-forwarded virtual time: the page runs in real time and is
/// looked at every half second until it settles (<see cref="DomSettle"/>), so a check that counts real seconds and reloads into the page
/// gets to. Then <c>Browser.close</c> shuts the browser down cleanly within <see cref="CloseGrace"/> — Chromium writes cookies to its
/// profile in batches, and a killed browser loses a clearance cookie it just earned, which the profile folder keeps for the next run.
/// The browser also stopped saying it is headless: <c>--user-agent</c> is the installed browser's own (<see cref="BrowserIdentity"/>; it
/// said <c>HeadlessChrome</c>, and its client hints follow the flag), <c>--disable-gpu</c> went (WebGL named a software renderer), and
/// the window and screen are a desktop's 1920×1080 (they were 756×488 inside an 800×600 screen). <c>navigator.webdriver</c>, false
/// under <c>--dump-dom</c>, turned true with the DevTools port; <c>--disable-blink-features=AutomationControlled</c> puts it back.
/// Later that day, a page that settled with no bot check showing hands back the browser's cookies for its host
/// (<see cref="HarvestAsync"/>), so a clearance the browser earned also lets the HTTP client in (<see cref="WebFetcher"/>).
/// </para>
/// <para>
/// Since 2026-10-03 it also prints pages to PDF (<c>convert_to_pdf</c>, <c>/pdf</c>): Chromium's <c>--print-to-pdf</c> on a page
/// the app wrote to the temp folder or on a web page, with images on and the browser's own header and footer off, within
/// <see cref="PdfTimeout"/>. Still the same one process-start site — <c>RunProcessAsync</c> serves both — and the same one run at
/// a time, so this instance is the web tools' own (<c>WebAccess.Browser</c>). The PDF is judged by its file, not by stdout:
/// there, whole and starting <c>%PDF</c>.
/// </para>
/// </summary>
public sealed class HeadlessBrowser : IHeadlessBrowser
{
    /// <summary>How long one page may take, scripts included, before the process is killed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>How long one print to PDF may take: a long document lays out for a while.</summary>
    public static readonly TimeSpan PdfTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Virtual time a web page's scripts get to settle before it is printed to PDF (a page read for its DOM runs in real time, <see cref="DomSettle"/>).</summary>
    public const int VirtualTimeBudgetMs = 5000;

    /// <summary>How long the browser gets to shut down after <c>Browser.close</c> (writing its cookies) before it is killed.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(3);

    /// <summary>The window and screen a page sees: a desktop's.</summary>
    public const string WindowSize = "1920,1080";

    /// <summary>What a page is asked for when its DOM is read: its ready state and its DOM.</summary>
    public const string PageExpression = "({ s: document.readyState, h: document.documentElement ? document.documentElement.outerHTML : '' })";

    /// <summary>What a settled page is asked for before its cookies are read: the address it ended on, after any redirect.</summary>
    public const string LocationExpression = "({ u: location.href })";

    /// <summary>
    /// What a page is asked for at each look (2026-10-11, the review: the whole DOM, up to megabytes, crossed the socket and was
    /// converted to Markdown at every look of a page with a clock or rotating ads): its ready state and its DOM's <see cref="Fingerprint"/>
    /// worked out in the page — the length and an FNV-1a hash over its UTF-16 units — leaving nothing there a script could find.
    /// </summary>
    public const string PrintExpression =
        "(() => { const d = document.documentElement; const h = d ? d.outerHTML : ''; let x = 0x811c9dc5; " +
        "for (let i = 0; i < h.length; i++) { x = Math.imul(x ^ h.charCodeAt(i), 0x01000193); } " +
        "return { s: document.readyState, n: h.length, x: x >>> 0 }; })()";

    /// <summary><see cref="PrintExpression"/>'s fingerprint of <paramref name="html"/>, worked out here: its length and its 32-bit FNV-1a hash over UTF-16 units. Pure.</summary>
    public static (int Length, uint Hash) Fingerprint(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        uint x = 2166136261;
        foreach (char c in html)
        {
            x = unchecked((x ^ c) * 16777619);
        }

        return (html.Length, x);
    }

    /// <summary>How long the output must stay whole and unchanged before a browser that has not exited is stopped (<see cref="WaitForExitOrWholeAsync"/>).</summary>
    public static readonly TimeSpan WholeGrace = TimeSpan.FromSeconds(1.5);

    private static readonly TimeSpan WholePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The profile folder under the temp directory, so the user's own browser profile is never touched.</summary>
    public static string UserDataDirectory => Path.Combine(Path.GetTempPath(), "NeonSidekick", "browser");

    private const string Category = "Web";
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>The known executables in the order they are tried, under the standard install folders of this machine.</summary>
    public static IReadOnlyList<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return WindowsCandidates(programFiles, programFilesX86, localAppData);
        }

        if (OperatingSystem.IsMacOS())
        {
            return
            [
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
            ];
        }

        var linux = new List<string>();
        foreach (var dir in new[] { "/usr/bin", "/usr/local/bin", "/snap/bin", "/opt/homebrew/bin" })
        {
            foreach (var name in new[] { "microsoft-edge", "microsoft-edge-stable", "google-chrome", "google-chrome-stable", "brave-browser", "chromium", "chromium-browser" })
            {
                linux.Add(Path.Combine(dir, name));
            }
        }

        return linux;
    }

    /// <summary>The Windows install paths, Edge → Chrome → Brave, each under Program Files, Program Files (x86) and the per-user folder. Pure; pinned.</summary>
    public static IReadOnlyList<string> WindowsCandidates(string programFiles, string programFilesX86, string localAppData)
    {
        ArgumentNullException.ThrowIfNull(programFiles);
        ArgumentNullException.ThrowIfNull(programFilesX86);
        ArgumentNullException.ThrowIfNull(localAppData);
        var roots = new[] { programFiles, programFilesX86, localAppData }.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var list = new List<string>();
        foreach (var relative in new[]
                 {
                     Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"),
                     Path.Combine("Google", "Chrome", "Application", "chrome.exe"),
                     Path.Combine("BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                 })
        {
            foreach (var root in roots)
            {
                list.Add(Path.Combine(root, relative));
            }
        }

        return list;
    }

    /// <summary>The pure form of <see cref="Locate(string)"/>: the configured path when <paramref name="exists"/> says it is there, else the first candidate that is.</summary>
    public static string? Locate(string configuredPath, IReadOnlyList<string> candidates, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(configuredPath);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(exists);
        string configured = configuredPath.Trim();
        if (configured.Length > 0)
        {
            return exists(configured) ? configured : null;
        }

        foreach (var candidate in candidates)
        {
            if (exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public string? Locate(string configuredPath) => Locate(configuredPath, Candidates(), File.Exists);

    public BrowserIdentity Identify(string configuredPath) => Locate(configuredPath) is { } executable ? BrowserIdentity.For(executable) : BrowserIdentity.Fallback;

    /// <summary>The flags every run shares, after <c>--headless=new</c> and the run's own verb.</summary>
    private static readonly string[] CommonFlags =
    [
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--disable-background-networking",
        "--mute-audio",
    ];

    /// <summary>The command line after the executable for a page read over DevTools as <paramref name="identity"/>. Pinned.</summary>
    public static IReadOnlyList<string> Arguments(Uri url, string userDataDirectory, BrowserIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(userDataDirectory);
        ArgumentNullException.ThrowIfNull(identity);
        return
        [
            "--headless=new",
            "--remote-debugging-port=0",
            "--disable-blink-features=AutomationControlled",
            .. CommonFlags,
            "--blink-settings=imagesEnabled=false",
            "--window-size=" + WindowSize,
            "--screen-info={" + WindowSize.Replace(',', 'x') + "}",
            "--user-agent=" + identity.UserAgent,
            "--user-data-dir=" + userDataDirectory,
            url.AbsoluteUri,
        ];
    }

    /// <summary>
    /// The print-to-PDF command line after the executable (2026-10-03). Pinned. Images stay on — they are half of what a PDF
    /// shows — and the browser's own header and footer (the date, the file's URL) are left off. A web page gets the virtual time
    /// budget to settle its scripts and, since 2026-10-11, the installed browser's user agent (<paramref name="identity"/>) in place
    /// of <c>HeadlessChrome</c>; a page the app wrote has neither to need. No GPU: a print lays out the same without one.
    /// </summary>
    public static IReadOnlyList<string> PdfArguments(Uri page, string outputPath, string userDataDirectory, bool isWeb, BrowserIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(userDataDirectory);
        ArgumentNullException.ThrowIfNull(identity);
        var list = new List<string>(14) { "--headless=new", "--disable-gpu" };
        list.AddRange(CommonFlags);
        list.Add("--no-pdf-header-footer");
        list.Add("--print-to-pdf=" + outputPath);
        if (isWeb)
        {
            list.Add("--virtual-time-budget=" + VirtualTimeBudgetMs.ToString(CultureInfo.InvariantCulture));
            list.Add("--user-agent=" + identity.UserAgent);
        }

        list.Add("--user-data-dir=" + userDataDirectory);
        list.Add(page.AbsoluteUri);
        return list;
    }

    public async Task<BrowserDump> DumpDomAsync(string executable, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(url);
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = BrowserIdentity.For(executable);
            var read = new PageRead();
            var run = await RunProcessAsync(executable, url, userData => Arguments(url, userData, identity), Timeout, (process, userData, token) => WatchPageAsync(process, userData, url, read, token), cancellationToken).ConfigureAwait(false);
            if (run.StartError is not null)
            {
                return new BrowserDump(null, run.StartError);
            }

            string how = run.TimedOut ? $"timed out with {(read.Html is null ? "no page" : "the page as it was")}" : read.Settled ? "settled" : "stopped";
            string cookies = read.Cookies is { } kept ? $", {kept.Count.ToString(CultureInfo.InvariantCulture)} cookies for its host" : "";
            DiagnosticLog.Debug(Category, $"{run.Summary}, {how} after {read.Looks.ToString(CultureInfo.InvariantCulture)} looks, {(read.Html?.Length ?? 0).ToString(CultureInfo.InvariantCulture)} chars{(read.ChallengeSeen ? (read.Gated ? ", still a bot check" : ", a bot check cleared") : "")}{cookies}" + (read.Failure is null ? "" : $"; {read.Failure}") + run.StderrTail);
            if (read.Html is not null)
            {
                return new BrowserDump(read.Html, "", read.FinalUrl, read.Cookies);
            }

            if (run.TimedOut)
            {
                return new BrowserDump(null, $"no page within {Llm.LlmTimeouts.Format(Timeout)}");
            }

            return new BrowserDump(null, run.Failure(read.Failure ?? "it showed no page"));
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public async Task<BrowserPdf> PrintToPdfAsync(string executable, Uri page, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(outputPath);
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool isWeb = page.Scheme is "http" or "https";
            var identity = BrowserIdentity.For(executable);
            var run = await RunProcessAsync(executable, page, userData => PdfArguments(page, outputPath, userData, isWeb, identity), PdfTimeout, (process, _, token) => WaitForExitOrWholeAsync(process, () => WholePdf(outputPath), token), cancellationToken).ConfigureAwait(false);
            if (run.StartError is not null)
            {
                return new BrowserPdf(false, run.StartError);
            }

            DiagnosticLog.Debug(Category, $"{run.Summary}, PDF to {outputPath}" + run.StderrTail);
            if (run.TimedOut)
            {
                return new BrowserPdf(false, $"no PDF within {Llm.LlmTimeouts.Format(PdfTimeout)}");
            }

            if (!File.Exists(outputPath))
            {
                return new BrowserPdf(false, run.Failure("it wrote no PDF"));
            }

            return Printing.PrintToFile.IsPdf(outputPath) ? new BrowserPdf(true, "") : new BrowserPdf(false, "it wrote a file that is not a PDF");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>What <see cref="WatchPageAsync"/> saw, kept as it goes so a run that times out still has its last look.</summary>
    private sealed class PageRead
    {
        public string? Html;
        public bool Settled;
        public bool Gated;
        public bool ChallengeSeen;
        public int Looks;
        public string? Failure;
        public string FinalUrl = "";
        public IReadOnlyList<BrowserCookie>? Cookies;
    }

    /// <summary>
    /// Watches the page over DevTools until <see cref="DomSettle"/> says it is ready, then closes the browser: true when it must
    /// still be killed (it did not exit within <see cref="CloseGrace"/>), false when it exited. Each look reads the DOM's fingerprint
    /// alone (<see cref="PrintExpression"/>); the DOM itself is read into <paramref name="read"/> and judged (a bot check, a thin page)
    /// when it differs from the one last read and that read is <see cref="DomSettle.Quiet"/> old — so a page changing at every look
    /// moves its DOM once a quiet window, not twice a second (2026-10-11, the review) — and once more when the page is ready and
    /// changed since. A DevTools failure is noted in <paramref name="read"/> and ends the watch (the caller kills the browser).
    /// </summary>
    private static async Task<bool> WatchPageAsync(Process process, string userData, Uri url, PageRead read, CancellationToken cancellationToken)
    {
        await using var client = new DevToolsClient();
        try
        {
            var endpoint = await DevToolsClient.WaitForEndpointAsync(userData, () => process.HasExited, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var settle = new DomSettle();
            var watch = Stopwatch.StartNew();
            string? session = null;
            bool thin = false;
            (int, uint)? last = null;        // the last look's fingerprint
            (int, uint)? readPrint = null;   // the fingerprint of read.Html
            var readAt = TimeSpan.Zero;

            // The DOM read and judged; nothing while the page has none to give (mid-navigation): the next look tries again.
            async Task ReadDomAsync()
            {
                if (await LookAsync(client, session!, cancellationToken).ConfigureAwait(false) is not { } full)
                {
                    return;
                }

                read.Html = full.Html;
                readPrint = Fingerprint(full.Html);
                readAt = watch.Elapsed;
                var page = HtmlToMarkdown.Convert(full.Html, url);
                read.Gated = WebFetcher.LooksLikeChallenge(full.Html, page.Markdown);
                read.ChallengeSeen |= read.Gated;
                thin = DomSettle.LooksThin(full.Html, page.Markdown);
            }

            while (true)
            {
                session ??= await AttachAsync(client, cancellationToken).ConfigureAwait(false);
                if (session is not null && await PrintAsync(client, session, cancellationToken).ConfigureAwait(false) is { } look)
                {
                    read.Looks++;
                    bool changed = look.Print != last;
                    last = look.Print;
                    if (look.Print != readPrint && (readPrint is null || watch.Elapsed - readAt >= DomSettle.Quiet))
                    {
                        await ReadDomAsync().ConfigureAwait(false);
                    }

                    bool ready = settle.Observe(watch.Elapsed, look.State is "interactive" or "complete", changed, read.Gated, thin);
                    if (ready && look.Print != readPrint)
                    {
                        // Ready: the DOM as it is now, when the last read is older than the last change.
                        await ReadDomAsync().ConfigureAwait(false);
                    }

                    if (ready && read.Html is not null)
                    {
                        read.Settled = true;
                        break;
                    }
                }

                await Task.Delay(DomSettle.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            if (!read.Gated)
            {
                await HarvestAsync(client, session!, url, read, cancellationToken).ConfigureAwait(false);
            }

            await client.NotifyAsync("Browser.close", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DevToolsException or System.Net.WebSockets.WebSocketException or IOException or JsonException or InvalidOperationException)
        {
            read.Failure = ex.Message;
            return true;
        }

        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(CloseGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }

    /// <summary>
    /// The address the settled page ended on and the browser's cookies for it and for <paramref name="url"/>'s host, into
    /// <paramref name="read"/> (2026-10-11). <c>Storage.getCookies</c> reads the browser's store (the profile folder keeps earlier
    /// runs' cookies too), and only those that would be sent to one of the two hosts are kept. A refusal of either call leaves the
    /// cookies out; the page is still the run's answer.
    /// </summary>
    private static async Task HarvestAsync(DevToolsClient client, string session, Uri url, PageRead read, CancellationToken cancellationToken)
    {
        try
        {
            if (await EvaluateAsync(client, session, LocationExpression, cancellationToken).ConfigureAwait(false) is { } where
                && where.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.String
                && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var final) && WebFetcher.IsHttp(final))
            {
                read.FinalUrl = final.AbsoluteUri;
            }

            string? finalHost = read.FinalUrl.Length > 0 ? new Uri(read.FinalUrl).Host : null;
            var all = BrowserCookie.Parse(await client.SendAsync("Storage.getCookies", null, null, cancellationToken).ConfigureAwait(false));
            read.Cookies = all.Where(c => c.AppliesTo(url.Host) || (finalHost is not null && c.AppliesTo(finalHost))).ToList();
        }
        catch (DevToolsException ex)
        {
            DiagnosticLog.Debug(Category, $"{url}: no cookies from the browser ({ex.Message})");
        }
    }

    /// <summary>A session on the run's page, attached flat so commands carry its id; null while the browser has no page yet.</summary>
    private static async Task<string?> AttachAsync(DevToolsClient client, CancellationToken cancellationToken)
    {
        var targets = await client.SendAsync("Target.getTargets", null, null, cancellationToken).ConfigureAwait(false);
        string? targetId = null;
        if (targets.ValueKind == JsonValueKind.Object && targets.TryGetProperty("targetInfos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            foreach (var info in infos.EnumerateArray())
            {
                if (info.TryGetProperty("type", out var type) && type.ValueEquals("page") && info.TryGetProperty("targetId", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    targetId = id.GetString();
                    break;
                }
            }
        }

        if (targetId is null)
        {
            return null;
        }

        var attached = await client.SendAsync("Target.attachToTarget", w =>
        {
            w.WriteString("targetId", targetId);
            w.WriteBoolean("flatten", true);
        }, null, cancellationToken).ConfigureAwait(false);
        return attached.ValueKind == JsonValueKind.Object && attached.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String ? session.GetString() : null;
    }

    /// <summary>The DOM read (<see cref="PageExpression"/>); null while it has no document to ask, mid-navigation.</summary>
    private static async Task<(string State, string Html)?> LookAsync(DevToolsClient client, string session, CancellationToken cancellationToken)
    {
        if (await EvaluateAsync(client, session, PageExpression, cancellationToken).ConfigureAwait(false) is not { } value)
        {
            return null;
        }

        string state = value.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        string html = value.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
        return html.Length == 0 ? null : (state, html);
    }

    /// <summary>One look at the page (<see cref="PrintExpression"/>): its ready state and its DOM's fingerprint; null while it has no document to ask.</summary>
    private static async Task<(string State, (int, uint) Print)?> PrintAsync(DevToolsClient client, string session, CancellationToken cancellationToken)
    {
        if (await EvaluateAsync(client, session, PrintExpression, cancellationToken).ConfigureAwait(false) is not { } value
            || !value.TryGetProperty("n", out var n) || !n.TryGetInt32(out int length) || length == 0
            || !value.TryGetProperty("x", out var x) || !x.TryGetUInt32(out uint hash))
        {
            return null;
        }

        string state = value.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        return (state, (length, hash));
    }

    /// <summary><paramref name="expression"/> run in the page, its value returned as an object; null when it threw, returned no object, or the page is navigating.</summary>
    private static async Task<JsonElement?> EvaluateAsync(DevToolsClient client, string session, string expression, CancellationToken cancellationToken)
    {
        JsonElement answer;
        try
        {
            answer = await client.SendAsync("Runtime.evaluate", w =>
            {
                w.WriteString("expression", expression);
                w.WriteBoolean("returnByValue", true);
            }, session, cancellationToken).ConfigureAwait(false);
        }
        catch (DevToolsException)
        {
            return null;   // "Execution context was destroyed": the page is navigating; the next look finds the new one.
        }

        return answer.ValueKind == JsonValueKind.Object && !answer.TryGetProperty("exceptionDetails", out _)
            && answer.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    /// <summary>One finished browser run: what it printed, how it ended, or why it never started.</summary>
    private sealed record BrowserRun(string? StartError, int ExitCode, string Stdout, string Stderr, bool TimedOut, string Summary)
    {
        public string StderrTail => Stderr.Length > 0 ? $"; stderr: {LastLine(Stderr)}" : "";

        /// <summary>Why a run that ended made nothing: the exit code when it was not 0, else <paramref name="quiet"/>; stderr's last line after.</summary>
        public string Failure(string quiet)
        {
            string reason = ExitCode != 0 ? $"exit code {ExitCode.ToString(CultureInfo.InvariantCulture)}" : quiet;
            string tail = LastLine(Stderr);
            return tail.Length > 0 ? $"{reason}: {tail}" : reason;
        }
    }

    /// <summary>
    /// The one place the browser is started: the profile folder made (and a stale <see cref="DevToolsClient.ActivePortFile"/> gone,
    /// so a watch never dials the last run's port), the process run with no window, stdout and stderr read, and
    /// <paramref name="watch"/> run on it — true when the run is done and the browser must be stopped, false when it exited by
    /// itself — killed with its tree then or when <paramref name="timeout"/> runs out. The caller holds <c>_oneAtATime</c>.
    /// </summary>
    private static async Task<BrowserRun> RunProcessAsync(string executable, Uri page, Func<string, IReadOnlyList<string>> arguments, TimeSpan timeout, Func<Process, string, CancellationToken, Task<bool>> watch, CancellationToken cancellationToken)
    {
        string userData = UserDataDirectory;
        try
        {
            Directory.CreateDirectory(userData);
            File.Delete(Path.Combine(userData, DevToolsClient.ActivePortFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BrowserRun($"could not create the profile folder {userData} ({ex.Message})", 0, "", "", false, "");
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments(userData))
        {
            start.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new BrowserRun($"could not start {Path.GetFileName(executable)} ({ex.Message})", 0, "", "", false, "");
        }

        if (process is null)
        {
            return new BrowserRun($"could not start {Path.GetFileName(executable)}", 0, "", "", false, "");
        }

        using (process)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            var stdoutText = new StringBuilder();
            var stdout = PumpAsync(process.StandardOutput, stdoutText);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            bool timedOut = false;
            bool stopped = false;
            try
            {
                stopped = await watch(process, userData, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                TryKill(process);
                if (!timedOut)
                {
                    throw;
                }
            }

            if (stopped)
            {
                TryKill(process);
            }

            if (stopped || timedOut)
            {
                using var reaped = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(reaped.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Not reaped in time: the run is over either way.
                }
            }

            await stdout.ConfigureAwait(false);
            string output = Snapshot(stdoutText);
            string errors = await stderr.ConfigureAwait(false);
            string how = stopped ? "stopped" : process.HasExited ? $"exit {process.ExitCode.ToString(CultureInfo.InvariantCulture)}" : "still running";
            string summary = $"{Path.GetFileName(executable)} {page}: {how} after {clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
            return new BrowserRun(null, stopped || !process.HasExited ? 0 : process.ExitCode, output, errors, timedOut, summary);
        }
    }

    /// <summary>
    /// Waits for the browser to exit, or for its output to be whole and to stay so for <see cref="WholeGrace"/> — true then, and
    /// the caller kills it (2026-10-06, the first Mac smoke: Chrome on macOS writes the PDF or prints the DOM and then never
    /// exits, so every run waited out its timeout). <paramref name="whole"/> answers a key while the output is whole (its
    /// length), null before; the key must not change across the grace, so a file still growing is never cut short. Where the
    /// browser exits by itself (Windows) it exits inside the grace and nothing changes.
    /// </summary>
    private static async Task<bool> WaitForExitOrWholeAsync(Process process, Func<long?> whole, CancellationToken cancellationToken)
    {
        var exited = process.WaitForExitAsync(cancellationToken);
        long? held = null;
        long heldSince = 0;
        while (true)
        {
            var tick = Task.Delay(WholePollInterval, cancellationToken);
            await Task.WhenAny(exited, tick).ConfigureAwait(false);
            if (exited.IsCompleted)
            {
                await exited.ConfigureAwait(false);
                return false;
            }

            await tick.ConfigureAwait(false);
            long? key = whole();
            if (key is null || key != held)
            {
                held = key;
                heldSince = Stopwatch.GetTimestamp();
            }
            else if (Stopwatch.GetElapsedTime(heldSince) >= WholeGrace)
            {
                return true;
            }
        }
    }

    /// <summary>Reads <paramref name="reader"/> to its end into <paramref name="text"/>, so the output can be looked at while the browser runs.</summary>
    private static async Task PumpAsync(StreamReader reader, StringBuilder text)
    {
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            lock (text)
            {
                text.Append(buffer, 0, read);
            }
        }
    }

    private static string Snapshot(StringBuilder text)
    {
        lock (text)
        {
            return text.ToString();
        }
    }

    /// <summary>The PDF is whole once the file starts <c>%PDF</c> and ends <c>%%EOF</c>: its length, else null.</summary>
    public static long? WholePdf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (length < 12 || !Printing.PrintToFile.IsPdf(path))
            {
                return null;
            }

            var tail = new byte[Math.Min(32, (int)length)];
            stream.Seek(-tail.Length, SeekOrigin.End);
            stream.ReadExactly(tail);
            return Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal) ? length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to kill: the wait is over either way.
        }
    }

    /// <summary>The last non-empty line of a browser's stderr, for a diagnostic. Pinned.</summary>
    public static string LastLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
