using System.Diagnostics;
using System.Globalization;
using System.Text;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Web;

/// <summary>What a headless run produced: the rendered DOM, or null with the reason.</summary>
public sealed record BrowserDump(string? Html, string Detail);

/// <summary>What a print-to-PDF run produced (2026-10-03): whether the PDF is at the output path, else why not.</summary>
public sealed record BrowserPdf(bool Ok, string Detail);

/// <summary>The headless-browser seam: where the browser is, and a page through it. <see cref="HeadlessBrowser"/> in the app, a fake in tests.</summary>
public interface IHeadlessBrowser
{
    /// <summary>The executable to run: <paramref name="configuredPath"/> when it is a file, else the first of the known browsers installed; null for none.</summary>
    string? Locate(string configuredPath);

    /// <summary>The DOM of <paramref name="url"/> after the page ran its scripts, through <paramref name="executable"/>.</summary>
    Task<BrowserDump> DumpDomAsync(string executable, Uri url, CancellationToken cancellationToken);

    /// <summary><paramref name="page"/> printed to a PDF at <paramref name="outputPath"/> through <paramref name="executable"/> (2026-10-03).</summary>
    Task<BrowserPdf> PrintToPdfAsync(string executable, Uri page, string outputPath, CancellationToken cancellationToken);
}

/// <summary>
/// A real browser for the pages the HTTP client cannot read — a script shell that renders
/// client-side, a gate that reads the TLS fingerprint — run as Chromium's <c>--headless=new
/// --dump-dom</c>: Edge (always on Windows 11), Chrome or Brave, found in their standard install
/// folders or named by the setting <c>Web browser path</c>. The rendered DOM comes back on stdout;
/// nothing else of the browser is used. <b>The second <c>Process.Start</c> site in the app</b> (the
/// editor opener is the first): no shell, no window, a throwaway profile folder under the temp
/// directory — without one a running Edge takes the URL over and this process exits with nothing —
/// one run at a time, killed with its tree at <see cref="Timeout"/>.
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

    /// <summary>Virtual time the page's scripts get to settle before the DOM is dumped.</summary>
    public const int VirtualTimeBudgetMs = 5000;

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

    /// <summary>The flags every run shares, after <c>--headless=new</c> and the run's own verb.</summary>
    private static readonly string[] CommonFlags =
    [
        "--disable-gpu",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--disable-background-networking",
        "--mute-audio",
    ];

    /// <summary>The command line after the executable. Pinned.</summary>
    public static IReadOnlyList<string> Arguments(Uri url, string userDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(userDataDirectory);
        return
        [
            "--headless=new",
            "--dump-dom",
            .. CommonFlags,
            "--blink-settings=imagesEnabled=false",
            "--virtual-time-budget=" + VirtualTimeBudgetMs.ToString(CultureInfo.InvariantCulture),
            "--user-data-dir=" + userDataDirectory,
            url.AbsoluteUri,
        ];
    }

    /// <summary>
    /// The print-to-PDF command line after the executable (2026-10-03). Pinned. Images stay on — they are half of what a PDF
    /// shows — and the browser's own header and footer (the date, the file's URL) are left off. A web page gets the virtual time
    /// budget to settle its scripts; a page the app wrote has none to settle.
    /// </summary>
    public static IReadOnlyList<string> PdfArguments(Uri page, string outputPath, string userDataDirectory, bool isWeb)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(userDataDirectory);
        var list = new List<string>(12) { "--headless=new" };
        list.AddRange(CommonFlags);
        list.Add("--no-pdf-header-footer");
        list.Add("--print-to-pdf=" + outputPath);
        if (isWeb)
        {
            list.Add("--virtual-time-budget=" + VirtualTimeBudgetMs.ToString(CultureInfo.InvariantCulture));
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
            var run = await RunProcessAsync(executable, url, userData => Arguments(url, userData), Timeout, cancellationToken).ConfigureAwait(false);
            if (run.StartError is not null)
            {
                return new BrowserDump(null, run.StartError);
            }

            DiagnosticLog.Debug(Category, $"{run.Summary}, {run.Stdout.Length.ToString(CultureInfo.InvariantCulture)} chars" + run.StderrTail);
            if (run.TimedOut)
            {
                return new BrowserDump(null, $"no page within {Llm.LlmTimeouts.Format(Timeout)}");
            }

            if (string.IsNullOrWhiteSpace(run.Stdout))
            {
                return new BrowserDump(null, run.Failure("it printed nothing"));
            }

            return new BrowserDump(run.Stdout, "");
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
            var run = await RunProcessAsync(executable, page, userData => PdfArguments(page, outputPath, userData, isWeb), PdfTimeout, cancellationToken).ConfigureAwait(false);
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
    /// The one place the browser is started: the profile folder made, the process run with no window, stdout and stderr read,
    /// killed with its tree when <paramref name="timeout"/> runs out. The caller holds <c>_oneAtATime</c>.
    /// </summary>
    private static async Task<BrowserRun> RunProcessAsync(string executable, Uri page, Func<string, IReadOnlyList<string>> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string userData = UserDataDirectory;
        try
        {
            Directory.CreateDirectory(userData);
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

        var watch = Stopwatch.StartNew();
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
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
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

            string output = await stdout.ConfigureAwait(false);
            string errors = await stderr.ConfigureAwait(false);
            string summary = $"{Path.GetFileName(executable)} {page}: exit {process.ExitCode.ToString(CultureInfo.InvariantCulture)} after {watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
            return new BrowserRun(null, process.ExitCode, output, errors, timedOut, summary);
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
