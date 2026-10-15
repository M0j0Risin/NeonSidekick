using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;

namespace NeonSidekick.Web;

/// <summary>
/// What a headless run produced: the rendered DOM, or null with the reason. Since 2026-10-11 a page that settled with no bot check
/// showing also brings the address it ended on (<see cref="FinalUrl"/>, <c>""</c> when unknown) and the browser's cookies for the
/// host asked for and that one (<see cref="Cookies"/>), for the HTTP client's jar.
/// </summary>
public sealed record BrowserDump(string? Html, string Detail, string FinalUrl = "", IReadOnlyList<BrowserCookie>? Cookies = null)
{
    /// <summary>Whether the run's browser began in the app's kill-on-close job, so all it started was born there too (2026-10-11, <see cref="EmbeddedLlm.ChildJob.Start"/>); false for a run that never started, and off Windows.</summary>
    public bool InJob { get; init; }
}

/// <summary>What a print-to-PDF run produced (2026-10-03): whether the PDF is at the output path, else why not.</summary>
public sealed record BrowserPdf(bool Ok, string Detail)
{
    /// <summary>As <see cref="BrowserDump.InJob"/> (2026-10-11).</summary>
    public bool InJob { get; init; }
}

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

    /// <summary><paramref name="pdf"/>'s pages read through <paramref name="executable"/> (2026-10-11): each page's text and picture, by pdf.js in the page (<see cref="BrowserPdfReader"/>).</summary>
    Task<PdfRead> ReadPdfAsync(string executable, byte[] pdf, PdfReadOptions options, CancellationToken cancellationToken);

    /// <summary><paramref name="job"/>'s PDFs edited through <paramref name="executable"/> (2026-10-14): merged, cut, turned or filled in, by pdf-lib in the page (<see cref="BrowserPdfEditor"/>).</summary>
    Task<PdfEdit> EditPdfAsync(string executable, PdfEditJob job, CancellationToken cancellationToken);
}

/// <summary>
/// A real browser for the pages the HTTP client cannot read — a script shell that renders
/// client-side, a gate that reads the TLS fingerprint — run as Chromium's <c>--headless=new</c>:
/// Edge (always on Windows 11), Chrome or Brave, found in their standard install
/// folders or named by the setting <c>Web browser path</c>. <b>The second <c>Process.Start</c> site in the app</b> (the
/// editor opener is the first): no shell, no window, a throwaway profile folder under the temp
/// directory — without one a running Edge takes the URL over and this process exits with nothing —
/// one run at a time, killed with its tree at <see cref="Timeout"/>. On Windows the start itself is <see cref="ChildJob.Start"/>'s
/// since 2026-10-11 (code review: started suspended in the app's kill-on-close job, which <c>Process.Start</c> cannot do), the same
/// one site moved, not a second.
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

    /// <summary>The file a Chromium browser on Windows holds open for writing, the writes unshared, in its profile folder while it runs, and deletes as it exits (checked on Edge 155, 2026-10-11).</summary>
    public const string LockFileName = "lockfile";

    /// <summary>The symlink a Chromium browser on macOS (and Linux) keeps in its profile folder while it runs, pointing at <c>host-pid</c>.</summary>
    public const string SingletonLockName = "SingletonLock";

    /// <summary>Win32 <c>ERROR_SHARING_VIOLATION</c> as an <see cref="IOException.HResult"/>.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>How many times <see cref="ProfileInUse(string, int)"/> looks at a held lockfile before it calls the folder held, and how far apart.</summary>
    public const int HeldLooks = 3;

    private static readonly TimeSpan LookInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Whether a running browser holds <paramref name="userDataDirectory"/> (2026-10-11). A second browser started on a held folder
    /// hands its page to the holder and exits (code 21), so a run checks first and says so rather than "exited before it opened
    /// its DevTools port".
    /// <para>On Windows its <see cref="LockFileName"/> there will not open for a write that shares everything: only a holder that
    /// refuses writers (the browser) stops it, not a scanner or an indexer that reads with everything shared, and only a sharing
    /// violation counts, seen <paramref name="looks"/> times 50 ms apart (code review, later that day: the first version opened it
    /// unshared, so any brief reader read as a browser). A probe that sees the browser's handle must conflict with it, so it would
    /// conflict with a browser opening the file at that instant just the same (a read that shares writes opens beside the browser's
    /// handle and sees nothing; code review, later still, checked on this machine) — but the browser deletes the file as it closes it,
    /// however it ends, so the probe opens only a file that is there: one a browser holds, where its failed open costs nothing, or
    /// one left by a machine that lost power, where a browser starting within the same few microseconds is the whole risk.
    /// Elsewhere (code review, the macOS build) its <see cref="SingletonLockName"/> names a browser on this machine still running
    /// (<see cref="SingletonHeld"/>): one of the browsers <see cref="IsBrowserName"/> knows, or <paramref name="executable"/>, the one the
    /// app would start there (code review, later on 2026-10-11: a browser path set to Chrome Canary, Edge Dev or Vivaldi went unseen).</para>
    /// </summary>
    public static bool ProfileInUse(string userDataDirectory, int looks = HeldLooks, string? executable = null) =>
        ProfileInUseAsync(userDataDirectory, looks, executable, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary><see cref="ProfileInUseAsync(string, int, string, CancellationToken)"/> with its full <see cref="HeldLooks"/>.</summary>
    public static Task<bool> ProfileInUseAsync(string userDataDirectory, string? executable, CancellationToken cancellationToken) =>
        ProfileInUseAsync(userDataDirectory, HeldLooks, executable, cancellationToken);

    /// <summary>
    /// <see cref="ProfileInUse(string, int)"/> for the runs, whose waits between looks are delays, not a pool thread asleep (code review,
    /// later on 2026-10-11). The one loop: the smoke's synchronous form waits on this one (code review, later still: two copies of it
    /// could drift apart between the smoke and the runs).
    /// </summary>
    public static async Task<bool> ProfileInUseAsync(string userDataDirectory, int looks, string? executable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userDataDirectory);
        for (int look = 1; ; look++)
        {
            if (LookOnce(userDataDirectory, executable) is { } held)
            {
                return held;
            }

            if (look >= looks)
            {
                return true;
            }

            await Task.Delay(LookInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One look: false when the folder is free, true when held (off Windows), null when the lockfile refused a writer just now (look again).</summary>
    private static bool? LookOnce(string userDataDirectory, string? executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return SingletonHeld(userDataDirectory, process => IsBrowserName(process.ProcessName, executable));
        }

        string lockFile = Path.Combine(userDataDirectory, LockFileName);
        if (!File.Exists(lockFile))
        {
            return false;
        }

        try
        {
            using var open = new FileStream(lockFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            return null;   // held now: a browser, or a reader that will let go
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="userDataDirectory"/>'s <see cref="SingletonLockName"/> names a running browser on this machine: the host
    /// this one (<see cref="SingletonOnHost"/>) and the process <paramref name="isBrowser"/> (code review, later on 2026-10-11: a lock
    /// left by a browser killed or a crash names a process id that some other program may have now, and that blocked every run until
    /// it ended; Chromium itself checks the host and that the process is a browser).
    /// </summary>
    internal static bool SingletonHeld(string userDataDirectory, Func<Process, bool> isBrowser)
    {
        try
        {
            if (new FileInfo(Path.Combine(userDataDirectory, SingletonLockName)).LinkTarget is not { } target
                || !SingletonOnHost(target, Environment.MachineName) || SingletonPid(target) is not { } pid)
            {
                return false;
            }

            using var holder = Process.GetProcessById(pid);
            return !holder.HasExited && isBrowser(holder);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The main process names of the browsers <see cref="Candidates"/> finds: macOS's app names, and the executables' names elsewhere
    /// (Linux's <c>chromium-browser</c> cut to the kernel's 15 characters as well).
    /// </summary>
    private static readonly HashSet<string> BrowserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Edge", "Google Chrome", "Brave Browser", "Chromium",
        "msedge", "chrome", "brave", "chromium", "chromium-browser", "chromium-browse",
    };

    /// <summary>
    /// Whether a process name is a Chromium browser's main process (<c>Microsoft Edge</c>, <c>msedge</c>, <c>Google Chrome</c>, <c>chrome</c>,
    /// <c>Brave Browser</c>, <c>Chromium</c>…): the whole name, not a part of it (code review, later on 2026-10-11: "edge" in <c>Knowledge…</c>,
    /// "chrome" in <c>Google Chrome Helper</c> or <c>chrome_crashpad_handler</c>, a process that got a dead lock's id, blocked every run
    /// until it ended; the lock names the browser's main process, which none of those is). <paramref name="executable"/>, the browser the
    /// app would start, counts by its file name too (code review, later still: a configured Chrome Canary, Edge Dev, Vivaldi or a build of
    /// Chromium, whose names no list holds, was never seen holding the folder). Pure.
    /// </summary>
    public static bool IsBrowserName(string name, string? executable = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim();
        return BrowserNames.Contains(trimmed)
            || (executable is { Length: > 0 } && trimmed.Length > 0
                && string.Equals(trimmed, Path.GetFileNameWithoutExtension(executable), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a <see cref="SingletonLockName"/> target, <c>host-pid</c>, names <paramref name="machineName"/>'s host: their first labels
    /// equal, case aside (Chromium writes the full host name, <c>MacBook-Pro.local</c>; .NET's machine name stops at the first dot). Pure.
    /// </summary>
    public static bool SingletonOnHost(string target, string machineName)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(machineName);
        int dash = target.LastIndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        static string First(string host) => host.Split('.')[0];
        return machineName.Length > 0 && string.Equals(First(target[..dash]), First(machineName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The process id at the end of a <see cref="SingletonLockName"/> target, <c>host-pid</c> (a host name may hold dashes); null for anything else. Pure.</summary>
    public static int? SingletonPid(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        int dash = target.LastIndexOf('-');
        return dash > 0 && int.TryParse(target.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0 ? pid : null;
    }

    private const string Category = "Web";
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly string _userData;
    private readonly string _pdfUserData;

    /// <summary>
    /// The profile folder of the PDF reads (2026-10-14, the PDF plan's gap 58): a folder of their own beside <see cref="UserDataDirectory"/>,
    /// so a PDF from the web, read by pdf.js in the page, never runs in the profile that holds the headless fetches' clearance cookies.
    /// Shared between copies of the app as that one is, so <see cref="ProfileInUse(string, int, string?)"/> guards it the same way.
    /// </summary>
    public static string PdfUserDataDirectory => Path.Combine(Path.GetTempPath(), "NeonSidekick", "browser-pdf");

    /// <param name="userDataDirectory">The profile folder its runs use; null = <see cref="UserDataDirectory"/>, the app's. The smoke
    /// gives one of its own (2026-10-11, code review: on the shared folder a copy of the app reading a page read as the probe's
    /// browser left running). The PDF reads use <see cref="PdfUserDataDirectory"/>, or this folder's name with <c>-pdf</c> after it.</param>
    public HeadlessBrowser(string? userDataDirectory = null)
    {
        _userData = userDataDirectory ?? UserDataDirectory;
        _pdfUserData = userDataDirectory is null ? PdfUserDataDirectory : PdfFolderFor(userDataDirectory);
    }

    /// <summary>The PDF reads' profile folder beside a given one (a test's, the smoke's): its name with <c>-pdf</c> after it.</summary>
    public static string PdfFolderFor(string userDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(userDataDirectory);
        return userDataDirectory.TrimEnd('\\', '/') + "-pdf";
    }

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
        // 2026-10-11: Edge that believes it runs under a compatibility layer starts a new copy of itself with this switch and
        // exits, and the copy outlived the app (not the process the app started, so no kill reached it) holding the profile
        // folder; given up front it never relaunches. Chrome and Brave ignore a switch they do not know.
        "--edge-skip-compat-layer-relaunch",
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
            var run = await RunProcessAsync(executable, url, _userData, userData => Arguments(url, userData, identity), Timeout, (process, userData, token) => WatchPageAsync(process, userData, url, read, token), cancellationToken).ConfigureAwait(false);
            if (run.StartError is not null)
            {
                return new BrowserDump(null, run.StartError);
            }

            string how = run.TimedOut ? $"timed out with {(read.Html is null ? "no page" : "the page as it was")}" : read.Settled ? "settled" : "stopped";
            string cookies = read.Cookies is { } kept ? $", {kept.Count.ToString(CultureInfo.InvariantCulture)} cookies for its host" : "";
            DiagnosticLog.Debug(Category, $"{run.Summary}, {how} after {read.Looks.ToString(CultureInfo.InvariantCulture)} looks, {(read.Html?.Length ?? 0).ToString(CultureInfo.InvariantCulture)} chars{(read.ChallengeSeen ? (read.Gated ? ", still a bot check" : ", a bot check cleared") : "")}{cookies}" + (read.Failure is null ? "" : $"; {read.Failure}") + run.StderrTail);
            if (read.Html is not null)
            {
                return new BrowserDump(read.Html, "", read.FinalUrl, read.Cookies) { InJob = run.InJob };
            }

            if (run.TimedOut)
            {
                return new BrowserDump(null, $"no page within {Llm.LlmTimeouts.Format(Timeout)}") { InJob = run.InJob };
            }

            return new BrowserDump(null, await run.FailureAsync(read.Failure ?? "it showed no page", cancellationToken).ConfigureAwait(false)) { InJob = run.InJob };
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
            var run = await RunProcessAsync(executable, page, _userData, userData => PdfArguments(page, outputPath, userData, isWeb, identity), PdfTimeout, (process, _, token) => WaitForExitOrWholeAsync(process, () => WholePdf(outputPath), token), cancellationToken).ConfigureAwait(false);
            if (run.StartError is not null)
            {
                return new BrowserPdf(false, run.StartError);
            }

            DiagnosticLog.Debug(Category, $"{run.Summary}, PDF to {outputPath}" + run.StderrTail);
            if (run.TimedOut)
            {
                return new BrowserPdf(false, $"no PDF within {Llm.LlmTimeouts.Format(PdfTimeout)}") { InJob = run.InJob };
            }

            if (!File.Exists(outputPath))
            {
                return new BrowserPdf(false, await run.FailureAsync("it wrote no PDF", cancellationToken).ConfigureAwait(false)) { InJob = run.InJob };
            }

            return (Printing.PrintToFile.IsPdf(outputPath) ? new BrowserPdf(true, "") : new BrowserPdf(false, "it wrote a file that is not a PDF")) with { InJob = run.InJob };
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>The longest a PDF read may take (2026-10-11): the browser's start, pdf.js's import and every page drawn.</summary>
    public static readonly TimeSpan PdfReadTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The PDF-reading command line after the executable (2026-10-11): headless on a blank page, DevTools on, no GPU (a canvas draws
    /// the same without one). Since 2026-10-14 (the PDF plan's gaps 58 and 86: <c>web_fetch</c> hands it PDFs from the web, and nothing
    /// kept the page off the network) no request leaves it: no name resolves, and every connection, a literal address and loopback
    /// included, goes to a proxy at the discard port that is not there. The PDF comes over DevTools and pdf.js as <c>data:</c> imports,
    /// neither of which the network carries, and the DevTools socket is the browser's own server, not a request of the page's. Pinned.
    /// </summary>
    public static IReadOnlyList<string> PdfReadArguments(string userDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(userDataDirectory);
        return
        [
            "--headless=new",
            "--remote-debugging-port=0",
            "--disable-gpu",
            .. CommonFlags,
            "--host-resolver-rules=MAP * ~NOTFOUND",
            "--proxy-server=127.0.0.1:9",
            "--proxy-bypass-list=<-loopback>",
            "--window-size=" + WindowSize,
            "--user-data-dir=" + userDataDirectory,
            "about:blank",
        ];
    }

    public async Task<PdfRead> ReadPdfAsync(string executable, byte[] pdf, PdfReadOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(options);
        if (pdf.Length > BrowserPdfReader.MaxBytes)
        {
            return new PdfRead(false, $"the PDF is over {BrowserPdfReader.MaxBytes / (1024 * 1024)} MB", 0, []);
        }

        if (BrowserPdfReader.ModuleBase64(BrowserPdfReader.LibraryResource) is not { } library || BrowserPdfReader.ModuleBase64(BrowserPdfReader.WorkerResource) is not { } worker)
        {
            return new PdfRead(false, "this build carries no pdf.js", 0, []);
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = new PdfReadState();
            var blank = new Uri("about:blank");
            string data = Convert.ToBase64String(pdf);
            var run = await RunProcessAsync(executable, blank, _pdfUserData, PdfReadArguments, PdfReadTimeout, (process, userData, token) => WatchPdfAsync(process, userData, library, worker, data, options, read, token), cancellationToken).ConfigureAwait(false);
            if (run.StartError is not null)
            {
                return new PdfRead(false, run.StartError, 0, []);
            }

            DiagnosticLog.Debug(Category, $"{run.Summary}, a PDF of {read.PageCount.ToString(CultureInfo.InvariantCulture)} pages, {read.Pages.Count.ToString(CultureInfo.InvariantCulture)} read" + (read.Failure is null ? "" : $"; {read.Failure}") + run.StderrTail);
            if (read.Locked)
            {
                return new PdfRead(false, PdfLockedDetail, 0, []) { InJob = run.InJob, Locked = true };
            }

            string detail = read.Failure ?? (run.TimedOut ? $"stopped after {Llm.LlmTimeouts.Format(PdfReadTimeout)}" : ShortDetail(options, read));
            bool ok = read.Pages.Count > 0 || (read.Opened && read.Wanted == 0 && read.Failure is null && !run.TimedOut);
            if (!ok && read.Failure is null && !run.TimedOut)
            {
                detail = await run.FailureAsync("it read no page", cancellationToken).ConfigureAwait(false);
            }

            return new PdfRead(ok, detail, read.PageCount, read.Pages) { InJob = run.InJob, Outline = read.Outline, Labels = read.Labels, Fields = read.Fields, PureXfa = read.PureXfa };
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>A locked PDF's detail (2026-10-14, the PDF plan's gap 2). Pinned.</summary>
    public const string PdfLockedDetail = "the PDF asks for a password";

    /// <summary>The longest an edit may take (2026-10-14): the browser's start, pdf-lib's load, the inputs, the edit and the read back.</summary>
    public static readonly TimeSpan PdfEditTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// <paramref name="job"/> run as <see cref="BrowserPdfEditor"/> describes (2026-10-14, the PDF plan's phase 4b): the PDF reads' own
    /// network-less profile and arguments, one run at a time with the other browser work, within <see cref="PdfEditTimeout"/>. Every
    /// input is held to <see cref="BrowserPdfReader.MaxBytes"/> and all of them to <see cref="BrowserPdfEditor.MaxOutputBytes"/> before
    /// the browser starts (gap 68). The new file is judged by its bytes: there, the size the page said, and starting <c>%PDF</c>.
    /// </summary>
    public async Task<PdfEdit> EditPdfAsync(string executable, PdfEditJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(job);
        if (job.Inputs.Count == 0)
        {
            return new PdfEdit(false, "no PDF to edit", null, 0);
        }

        if (job.Inputs.Any(i => i.Length > BrowserPdfReader.MaxBytes) || job.Inputs.Sum(i => (long)i.Length) > BrowserPdfEditor.MaxOutputBytes)
        {
            return new PdfEdit(false, $"the PDFs are over {BrowserPdfEditor.MaxOutputBytes / (1024 * 1024)} MB together, or one is over {BrowserPdfReader.MaxBytes / (1024 * 1024)} MB", null, 0);
        }

        if (BrowserPdfEditor.LibraryText() is not { } library)
        {
            return new PdfEdit(false, "this build carries no pdf-lib", null, 0);
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = new PdfEditState();
            var blank = new Uri("about:blank");
            var run = await RunProcessAsync(executable, blank, _pdfUserData, PdfReadArguments, PdfEditTimeout, (process, userData, token) => WatchEditAsync(process, userData, library, job, state, token), cancellationToken).ConfigureAwait(false);
            if (run.StartError is not null)
            {
                return new PdfEdit(false, run.StartError, null, 0);
            }

            DiagnosticLog.Debug(Category, $"{run.Summary}, a PDF edit ({job.Op}) of {job.Inputs.Count.ToString(CultureInfo.InvariantCulture)} file(s)" + (state.Output is null ? "" : $", {state.Output.Length.ToString(CultureInfo.InvariantCulture)} B made") + (state.Failure is null ? "" : $"; {state.Failure}") + run.StderrTail);
            if (state.Outcome is { } refused)
            {
                return refused with { InJob = run.InJob };
            }

            if (state.Output is { } output)
            {
                return new PdfEdit(true, "", output, state.PageCount) { InJob = run.InJob, Pages = state.Pages };
            }

            string detail = state.Failure ?? (run.TimedOut ? $"stopped after {Llm.LlmTimeouts.Format(PdfEditTimeout)}" : await run.FailureAsync("it made no PDF", cancellationToken).ConfigureAwait(false));
            return new PdfEdit(false, detail, null, 0) { InJob = run.InJob };
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>What <see cref="WatchEditAsync"/> got to: the new file, or a refusal the caller words, or why it failed.</summary>
    private sealed class PdfEditState
    {
        public byte[]? Output;
        public int PageCount;
        public IReadOnlyList<int> Pages = [];
        public PdfEdit? Outcome;
        public string? Failure;
    }

    /// <summary>
    /// Edits over DevTools (2026-10-14, <see cref="BrowserPdfEditor"/>): pdf-lib evaluated, each input loaded, the pages worked out from the
    /// first input's count, the edit made and saved, the new file read back in chunks, then the browser closed — true when it must still be
    /// killed, false when it exited, <see cref="WatchPdfAsync"/>'s contract.
    /// </summary>
    private static async Task<bool> WatchEditAsync(Process process, string userData, string library, PdfEditJob job, PdfEditState state, CancellationToken cancellationToken)
    {
        await using var client = new DevToolsClient();
        try
        {
            var endpoint = await DevToolsClient.WaitForEndpointAsync(userData, () => process.HasExited, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            string? session;
            while ((session = await AttachAsync(client, cancellationToken).ConfigureAwait(false)) is null)
            {
                await Task.Delay(DomSettle.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            await EditInPageAsync(client, session, library, job, state, cancellationToken).ConfigureAwait(false);
            await client.NotifyAsync("Browser.close", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DevToolsException or System.Net.WebSockets.WebSocketException or IOException or JsonException or InvalidOperationException)
        {
            state.Failure = ex.Message;
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

    // The steps in the page, each failure noted in the state and the rest skipped.
    private static async Task EditInPageAsync(DevToolsClient client, string session, string library, PdfEditJob job, PdfEditState state, CancellationToken cancellationToken)
    {
        var (loaded, loadError) = await EvaluateAwaitAsync(client, session, BrowserPdfEditor.LoadExpression(library), cancellationToken).ConfigureAwait(false);
        if (loaded is not { } answer || !BrowserPdfEditor.Loaded(answer))
        {
            state.Failure = "pdf-lib did not load: " + (loadError ?? "PDFLib missing");
            return;
        }

        int firstCount = 0;
        for (int i = 0; i < job.Inputs.Count; i++)
        {
            var (opened, openError) = await EvaluateAwaitAsync(client, session, BrowserPdfEditor.InputExpression(Convert.ToBase64String(job.Inputs[i]), preserveXfa: job.Op == PdfEditOp.FillForm), cancellationToken).ConfigureAwait(false);
            if (opened is { } locked && BrowserPdfEditor.IsLocked(locked))
            {
                state.Outcome = new PdfEdit(false, PdfLockedDetail, null, 0) { LockedInput = i };
                return;
            }

            if (opened is not { } doc || BrowserPdfEditor.PageCountOf(doc) is not { } count)
            {
                state.Failure = "not a PDF pdf-lib can open: " + (openError ?? "no page count");
                return;
            }

            if (i == 0)
            {
                firstCount = count;
            }
        }

        var (pages, refusal) = job.PagesFor(firstCount);
        if (pages is null)
        {
            state.Outcome = new PdfEdit(false, refusal ?? "no pages", null, firstCount) { Refused = refusal };
            return;
        }

        var (ran, runError) = await EvaluateAwaitAsync(client, session, BrowserPdfEditor.RunExpression(job.Op, pages, job.Angle, job.Fields, job.Flatten), cancellationToken).ConfigureAwait(false);
        if (ran is not { } result)
        {
            state.Failure = "the edit failed: " + (runError ?? "no answer");
            return;
        }

        var (pageCount, size, notMade) = BrowserPdfEditor.ReadRun(result);
        if (notMade is not null)
        {
            state.Outcome = notMade;
            return;
        }

        if (size > BrowserPdfEditor.MaxOutputBytes)
        {
            state.Failure = $"the new PDF would be over {BrowserPdfEditor.MaxOutputBytes / (1024 * 1024)} MB";
            return;
        }

        var bytes = new byte[size];
        for (int start = 0; start < size; start += BrowserPdfEditor.ChunkBytes)
        {
            int length = Math.Min(BrowserPdfEditor.ChunkBytes, size - start);
            var (chunk, chunkError) = await EvaluateAwaitAsync(client, session, BrowserPdfEditor.ChunkExpression(start, length), cancellationToken).ConfigureAwait(false);
            if (chunk is not { } c || BrowserPdfEditor.ReadChunk(c) is not { } part || part.Length != length)
            {
                state.Failure = "the new PDF could not be read back: " + (chunkError ?? "a chunk came back wrong");
                return;
            }

            part.CopyTo(bytes, start);
        }

        if (!Files.PdfFile.HasPdfHeader(bytes))
        {
            state.Failure = "the edit made a file that is not a PDF";
            return;
        }

        state.Output = bytes;
        state.PageCount = pageCount;
        state.Pages = pages;
    }

    /// <summary>
    /// Why a read that ended well read fewer pages than it set out to: the first N of a file's pages under <see cref="PdfReadOptions.MaxPages"/>
    /// (2026-10-11, pinned), or N of the pages a list asked for (2026-10-14); empty when it read them all.
    /// </summary>
    private static string ShortDetail(PdfReadOptions options, PdfReadState read)
    {
        string n = read.Pages.Count.ToString(CultureInfo.InvariantCulture);
        if (options.Pages is null)
        {
            return read.Pages.Count < read.PageCount ? $"read the first {n} of {read.PageCount.ToString(CultureInfo.InvariantCulture)} pages" : "";
        }

        return read.Pages.Count < read.Wanted ? $"read {n} of the {read.Wanted.ToString(CultureInfo.InvariantCulture)} pages asked for" : "";
    }

    /// <summary>What <see cref="WatchPdfAsync"/> read, kept as it goes so a run that times out keeps the pages it got.</summary>
    private sealed class PdfReadState
    {
        public int PageCount;
        public int Wanted;
        public bool Opened;
        public bool Locked;
        public readonly List<PdfPageRead> Pages = [];
        public IReadOnlyList<PdfOutlineItem>? Outline;
        public IReadOnlyList<string>? Labels;
        public IReadOnlyList<PdfFormField>? Fields;
        public bool PureXfa;
        public string? Failure;
    }

    /// <summary>
    /// Reads the PDF over DevTools (2026-10-11, <see cref="BrowserPdfReader"/>): pdf.js imported, the file opened, each page's text and picture
    /// asked for in turn into <paramref name="read"/>, then the browser closed — true when it must still be killed, false when it
    /// exited, <see cref="WatchPageAsync"/>'s contract. A DevTools failure or a script's throw is noted and ends the read.
    /// </summary>
    private static async Task<bool> WatchPdfAsync(Process process, string userData, string library, string worker, string pdf, PdfReadOptions options, PdfReadState read, CancellationToken cancellationToken)
    {
        await using var client = new DevToolsClient();
        try
        {
            var endpoint = await DevToolsClient.WaitForEndpointAsync(userData, () => process.HasExited, cancellationToken).ConfigureAwait(false);
            await client.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            string? session;
            while ((session = await AttachAsync(client, cancellationToken).ConfigureAwait(false)) is null)
            {
                await Task.Delay(DomSettle.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            var (_, loadError) = await EvaluateAwaitAsync(client, session, BrowserPdfReader.LoadExpression(library, worker), cancellationToken).ConfigureAwait(false);
            if (loadError is null && BrowserPdfReader.CMapCount > 0)
            {
                // The packed CMaps (2026-10-14, the PDF plan's gap 3), before the file opens: pdf.js asks for one while it builds a font.
                (_, loadError) = await EvaluateAwaitAsync(client, session, BrowserPdfReader.CMapsExpression(), cancellationToken).ConfigureAwait(false);
            }

            if (loadError is not null)
            {
                read.Failure = "pdf.js did not load: " + loadError;
            }
            else
            {
                var (opened, openError) = await EvaluateAwaitAsync(client, session, BrowserPdfReader.OpenExpression(pdf), cancellationToken).ConfigureAwait(false);
                if (opened is { } locked && BrowserPdfReader.IsLocked(locked))
                {
                    read.Locked = true;
                }
                else if (opened is not { } doc || !doc.TryGetProperty("n", out var n) || !n.TryGetInt32(out int pages))
                {
                    read.Failure = "not a PDF pdf.js can open: " + (openError ?? "no page count");
                }
                else
                {
                    read.Opened = true;
                    read.PageCount = pages;
                    if (options.Outline)
                    {
                        var (outline, _) = await EvaluateAwaitAsync(client, session, BrowserPdfReader.OutlineExpression(), cancellationToken).ConfigureAwait(false);
                        read.Outline = outline is { } o ? BrowserPdfReader.ReadOutline(o) : [];
                    }

                    if (options.Labels)
                    {
                        var (labels, _) = await EvaluateAwaitAsync(client, session, BrowserPdfReader.LabelsExpression(), cancellationToken).ConfigureAwait(false);
                        read.Labels = labels is { } l ? BrowserPdfReader.ReadLabels(l) : [];
                    }

                    if (options.Fields)
                    {
                        // The form's fields (2026-10-14, the PDF plan's gap 98): read_pdf's fields: true, what pdf_edit's fill_form takes.
                        var (fields, _) = await EvaluateAwaitAsync(client, session, BrowserPdfReader.FieldsExpression(), cancellationToken).ConfigureAwait(false);
                        (read.Fields, read.PureXfa) = fields is { } f ? BrowserPdfReader.ReadFields(f) : ([], false);
                    }

                    var wanted = options.PagesOf(pages);
                    read.Wanted = wanted.Count;
                    foreach (int number in wanted)
                    {
                        var (value, pageError) = await EvaluateAwaitAsync(client, session, BrowserPdfReader.PageExpression(number, options.MaxSide, options.Draws(number)), cancellationToken).ConfigureAwait(false);
                        if (value is not { } answer || BrowserPdfReader.ReadPage(number, answer) is not { } page)
                        {
                            read.Failure = $"page {number.ToString(CultureInfo.InvariantCulture)} could not be read: " + (pageError ?? "no answer");
                            break;
                        }

                        read.Pages.Add(page);
                    }
                }
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
    /// <paramref name="expression"/> run in the page and its promise awaited (2026-10-11, the PDF read): its value as an object, or the
    /// exception's first line; a DevTools refusal throws to the caller.
    /// </summary>
    private static async Task<(JsonElement? Value, string? Error)> EvaluateAwaitAsync(DevToolsClient client, string session, string expression, CancellationToken cancellationToken)
    {
        var answer = await client.SendAsync("Runtime.evaluate", w =>
        {
            w.WriteString("expression", expression);
            w.WriteBoolean("returnByValue", true);
            w.WriteBoolean("awaitPromise", true);
        }, session, cancellationToken).ConfigureAwait(false);
        if (answer.ValueKind != JsonValueKind.Object)
        {
            return (null, "no answer");
        }

        if (answer.TryGetProperty("exceptionDetails", out var details))
        {
            string text = details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : details.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "an exception";
            return (null, text.Split('\n')[0].Trim());
        }

        return answer.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
            ? (value.Clone(), null)
            : (null, "no object");
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

    /// <summary>
    /// One finished browser run: what it said on stderr, how it ended, or why it never started; the profile folder it ran on, whether
    /// its browser was gone at the end, and whether it began in the app's job (<see cref="BrowserDump.InJob"/>).
    /// </summary>
    private sealed record BrowserRun(string? StartError, int ExitCode, string Stderr, bool TimedOut, string Summary, string UserData = "", bool Exited = false, bool InJob = false, string Executable = "")
    {
        public string StderrTail => Stderr.Length > 0 ? $"; stderr: {LastLine(Stderr)}" : "";

        /// <summary>
        /// Why a run that ended made nothing: another browser holding the profile folder after this one exited (2026-10-11; looked
        /// at here, only for a run that made nothing, since code review the same day), else the exit code when it was not 0, else
        /// <paramref name="quiet"/>; stderr's last line after.
        /// </summary>
        public async Task<string> FailureAsync(string quiet, CancellationToken cancellationToken)
        {
            if (Exited && UserData.Length > 0 && await ProfileInUseAsync(UserData, Executable, cancellationToken).ConfigureAwait(false))
            {
                DiagnosticLog.Debug(Category, $"{Summary}, the profile folder held by another browser");
                return WebText.BrowserProfileInUse(UserData);
            }

            string reason = ExitCode != 0 ? $"exit code {ExitCode.ToString(CultureInfo.InvariantCulture)}" : quiet;
            string tail = LastLine(Stderr);
            return tail.Length > 0 ? $"{reason}: {tail}" : reason;
        }
    }

    /// <summary>
    /// The one place the browser is started: the profile folder made (and a stale <see cref="DevToolsClient.ActivePortFile"/> gone,
    /// so a watch never dials the last run's port), the process run with no window, its stderr kept, and <paramref name="watch"/>
    /// run on it — true when the run is done and the browser must be stopped, false when it exited by itself — killed with its tree
    /// then or when <paramref name="timeout"/> runs out. The caller holds <c>_oneAtATime</c>.
    ///
    /// <para>2026-10-11 (a relaunched Edge outlived the app and held the folder): a folder another browser holds is refused before
    /// a start (<see cref="ProfileInUseAsync"/>) and named after a run that made nothing; on Windows the browser is in the app's
    /// kill-on-close job (<see cref="ChildJob"/>, as <c>llama-server</c> is), so whatever it starts dies with the app however the app
    /// ends. Since code review the same day, later still, it is started in the job (<see cref="ChildJob.Start"/>: suspended, assigned,
    /// then let run) rather than assigned after <c>Process.Start</c> and its tree swept in from a snapshot, which a child starting
    /// children of its own could still slip; its stdout goes to <c>NUL</c> (nothing read it since the DevTools reading) and its
    /// stderr to a file in the profile folder, read and deleted after. Elsewhere <c>Process.Start</c> as before, the output piped.</para>
    /// </summary>
    private static async Task<BrowserRun> RunProcessAsync(string executable, Uri page, string userData, Func<string, IReadOnlyList<string>> arguments, TimeSpan timeout, Func<Process, string, CancellationToken, Task<bool>> watch, CancellationToken cancellationToken)
    {
        if (await ProfileInUseAsync(userData, executable, cancellationToken).ConfigureAwait(false))
        {
            return new BrowserRun(WebText.BrowserProfileInUse(userData), 0, "", false, "");
        }

        try
        {
            Directory.CreateDirectory(userData);
            File.Delete(Path.Combine(userData, DevToolsClient.ActivePortFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BrowserRun($"could not create the profile folder {userData} ({ex.Message})", 0, "", false, "");
        }

        var clock = Stopwatch.StartNew();
        var started = Start(executable, arguments(userData), userData);
        if (started.Process is not { } process)
        {
            return new BrowserRun($"could not start {Path.GetFileName(executable)} ({started.Error})", 0, "", false, "");
        }

        using (process)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            bool timedOut = false;
            bool stopped = false;
            try
            {
                stopped = await watch(process, userData, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch
            {
                // ESC, or a watch that threw (a WebSocket, JSON or IO error from the DevTools client): the browser is stopped, reaped and
                // its stderr read (the file closed, so gone) before the exception goes on (code review, later on 2026-10-11: only ESC killed
                // it, without waiting, and any other exception left the browser holding the folder and the file open until the app ended).
                await StopAsync(process).ConfigureAwait(false);
                await ReadStderrBoundedAsync(started).ConfigureAwait(false);
                throw;
            }

            if (stopped || timedOut)
            {
                await StopAsync(process).ConfigureAwait(false);
            }

            string errors = await ReadStderrBoundedAsync(started).ConfigureAwait(false);
            string how = stopped ? "stopped" : process.HasExited ? $"exit {process.ExitCode.ToString(CultureInfo.InvariantCulture)}" : "still running";
            string summary = $"{Path.GetFileName(executable)} {page}: {how} after {clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
            return new BrowserRun(null, stopped || !process.HasExited ? 0 : process.ExitCode, errors, timedOut, summary, userData, process.HasExited, started.InJob, executable);
        }
    }

    /// <summary>A browser started: the process (null with <see cref="Error"/> when it did not start), its stderr (read when asked, once the run is over), whether it began in the app's job.</summary>
    private sealed record Started(Process? Process, Func<Task<string>> ReadStderr, bool InJob, string Error);

    /// <summary>
    /// The browser started as <see cref="RunProcessAsync"/> describes: on Windows in the app's job with stderr to a file in
    /// <paramref name="userData"/> (read when the run is over); elsewhere by <c>Process.Start</c> with both streams piped, stdout
    /// drained. The file is made delete-on-close and the app holds it open for the run (code review, later on 2026-10-11: deleted
    /// only by the read, it stayed behind for good when the app crashed, was killed or closed mid-fetch), so it goes with the last
    /// handle: the read's, or the app's and the browser's however they end.
    /// </summary>
    private static Started Start(string executable, IReadOnlyList<string> arguments, string userData)
    {
        if (OperatingSystem.IsWindows())
        {
            string log = Path.Combine(userData, "stderr-" + Guid.NewGuid().ToString("N") + ".log");
            FileStream held;
            try
            {
                held = new FileStream(log, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Started(null, Nothing, false, "could not create " + log + " (" + ex.Message + ")");
            }

            var process = ChildJob.Start(executable, arguments, log, out bool inJob, out string error);
            if (process is null)
            {
                held.Dispose();
                return new Started(null, Nothing, false, error);
            }

            return new Started(process, () => ReadStderrAsync(held), inJob, "");
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
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            if (Process.Start(start) is not { } process)
            {
                return new Started(null, Nothing, false, "no process");
            }

            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            return new Started(process, () => stderr, false, "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new Started(null, Nothing, false, ex.Message);
        }
    }

    private static Task<string> Nothing() => Task.FromResult("");

    /// <summary>The stderr file of a browser started on Windows, read through the app's own handle as it stands (the run is over), then closed, which deletes it.</summary>
    private static async Task<string> ReadStderrAsync(FileStream held)
    {
        try
        {
            held.Position = 0;
            using var reader = new StreamReader(held, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            return await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            return "";
        }
        finally
        {
            await held.DisposeAsync().ConfigureAwait(false);
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

    /// <summary>The browser killed with its tree and reaped, waiting up to 5 s (not the run's token: a cancelled run still waits for its kill).</summary>
    private static async Task StopAsync(Process process)
    {
        TryKill(process);
        using var reaped = new CancellationTokenSource(ReapWait);
        try
        {
            await process.WaitForExitAsync(reaped.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Not reaped in time: the run is over either way.
        }
    }

    /// <summary>
    /// The run's stderr (<see cref="Started.ReadStderr"/>, on Windows its file read and closed, so gone), waiting up to 5 s and never
    /// throwing (code review, later on 2026-10-11: off Windows a child the kill missed still holding the pipe would hold the read
    /// open, and a read that failed went unobserved): "" when it could not be had.
    /// </summary>
    private static async Task<string> ReadStderrBoundedAsync(Started started)
    {
        try
        {
            return await started.ReadStderr().WaitAsync(ReapWait).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            DiagnosticLog.Debug(Category, "The browser's stderr was not read: " + ex.Message);
            return "";
        }
    }

    private static readonly TimeSpan ReapWait = TimeSpan.FromSeconds(5);

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
