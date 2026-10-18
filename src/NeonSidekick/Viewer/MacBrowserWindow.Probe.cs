using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

// ── The smoke's browser:webkit (2026-10-18, the Mac Browser plan) ─────────

/// <summary>
/// <c>browser:webkit</c>: the Mac's Browser window in the published binary, over a store that keeps nothing (never the user's) and pages from an
/// in-process loopback listener (nothing leaves the machine, pass 17). The window is made as the app makes it, then put on screen transparent
/// and deaf to the mouse (a snapshot and a print want a window on screen, pass 10), never activated. It proves, in order: an <c>http://</c>
/// page loaded (ATS); a read in the app's world; a <c>#hash</c> seen by KVO and gone back from; the store's cookies; a click sent straight to
/// the view arriving trusted; a snapshot as JPEG; a paged print to a <c>%PDF</c>; a page's <c>file:</c> navigation refused; a staged PDF shown;
/// a download's destination chosen and cancelled; the bar's controls and a tooltip; the menus' rows; <c>removeDataOfTypes:</c>.
/// Skipped with no AppKit host or no window server.
/// </summary>
internal sealed partial class MacBrowserWindow
{
    private string? _downloadsFolder;
    private bool _probe;
    private int _downloadsEnded;

    /// <summary>Where downloads go: the user's Downloads, or the probe's own folder.</summary>
    private string DownloadsFolder => _downloadsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static MacBrowserWindow MakeProbe(string folder)
    {
        var window = new MacBrowserWindow(folder, MacBrowserWindows.Identity, storeId: null) { _downloadsFolder = folder, _handOver = false, _probe = true };
        window.Build();
        SendVoidDouble(window.Window, Sel("setAlphaValue:"), 0);
        SendVoidBool(window.Window, Sel("setIgnoresMouseEvents:"), 1);
        window.ShowWindow(activate: false, raise: true);
        return window;
    }

    /// <summary>The probe; see the class's summary.</summary>
    public static (bool Ok, string Detail) Probe(TimeSpan timeout)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            return (true, "skipped: macOS 14 or later needed");
        }

        if (!AppKitHost.IsEnabled)
        {
            return (true, HasWindowServer() ? "skipped: no AppKit host here (the app's main thread runs it)" : "skipped: no window server");
        }

        string folder = Path.Combine(Path.GetTempPath(), "NeonSidekick", "browser-probe-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        using var server = new ProbeServer();
        MacBrowserWindow? window = null;
        try
        {
            var run = Task.Run(async () =>
            {
                if (!AppKitHost.Invoke(() => MakeProbe(folder), out window) || window is null)
                {
                    return (false, "the main thread did not make the window");
                }

                return await window.RunProbeAsync(server, folder).ConfigureAwait(false);
            });
            return run.Wait(timeout) ? run.Result : (false, "the probe did not finish in time");
        }
        catch (AggregateException ex)
        {
            return (false, ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            if (window is not null)
            {
                AppKitHost.Invoke(() =>
                {
                    window.Close();
                    return true;
                }, out _);
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Debug("Browser", $"The probe's folder stays: {ex.Message}");
            }
        }
    }

    private Task<T> Main<T>(Func<T> work) => MacBrowserWindows.OnMain<T>(done => done.TrySetResult(work()), default!);

    private Task<string?> Read(string expression) => MacBrowserWindows.OnMain<string?>(done => MacBrowserKit.Evaluate(_web, expression, json => done.TrySetResult(json)), null);

    // The page loaded: not loading, at an address that passes match, within 10 s.
    private async Task<bool> Loaded(Func<string?, bool> match)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await Main(() => !MacBrowserKit.Loading(_web) && match(Source)).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<(bool, string)> RunProbeAsync(ProbeServer server, string folder)
    {
        var steps = new List<string>();
        string page = server.Url("/");
        var clock = Stopwatch.StartNew();

        await Main(() => { Go(page); return true; }).ConfigureAwait(false);
        if (!await Loaded(s => s == page).ConfigureAwait(false))
        {
            return (false, "the loopback http:// page did not load");
        }

        steps.Add($"an http:// loopback page in {clock.ElapsedMilliseconds} ms");

        string? title = await Read("document.title").ConfigureAwait(false);
        string? page1 = await Read("typeof window.pageSecret").ConfigureAwait(false);
        if (title != "\"Probe\"" || page1 != "\"undefined\"")
        {
            return (false, $"the read in the app's world gave {title} and saw the page's globals as {page1}");
        }

        steps.Add("a read in the app's world, the page's globals unseen");

        await Read("(location.hash = 'h1', 1)").ConfigureAwait(false);
        if (!await Loaded(s => s?.EndsWith("#h1", StringComparison.Ordinal) == true).ConfigureAwait(false))
        {
            return (false, "a #hash change was not seen");
        }

        await Main(() => { SendVoid(_web, Sel("goBack")); return true; }).ConfigureAwait(false);
        if (!await Loaded(s => s == page).ConfigureAwait(false))
        {
            return (false, "going back from the #hash did not come back");
        }

        steps.Add("a #hash seen and gone back from");

        var cookies = await MacBrowserWindows.OnMain<IReadOnlyList<Web.BrowserCookie>>(done => MacBrowserKit.Cookies(_web, c => done.TrySetResult(c)), []).ConfigureAwait(false);
        if (!cookies.Any(c => c.Name == "srv" && c.HttpOnly) || !cookies.Any(c => c.Name == "js"))
        {
            return (false, $"the store's cookies were {string.Join(", ", cookies.Select(c => c.Name))}");
        }

        steps.Add($"{cookies.Count} cookies from the store");

        // A click straight to the view (the live view's way), the window never key.
        await Main(() =>
        {
            var bounds = SendRect(_web, Sel("bounds"));
            Mouse(InMouseMoved, 60, 40);
            Mouse(InMouseDown, 60, 40);
            Mouse(InMouseUp, 60, 40);
            return bounds.Width;
        }).ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);
        string? clicked = await Read("document.body.dataset.log || ''").ConfigureAwait(false);
        if (clicked is null || !clicked.Contains("click:true", StringComparison.Ordinal))
        {
            return (false, "a click sent to the view did not arrive trusted (" + clicked + ")");
        }

        steps.Add("a click to the view arrived trusted");

        var snapshot = await MacBrowserWindows.OnMain<(byte[]? Jpeg, int Width)>(done => MacBrowserKit.Snapshot(_web, 120, BrowserRemote.JpegQuality, (j, w) => done.TrySetResult((j, w))), (null, 0)).ConfigureAwait(false);
        if (snapshot.Jpeg is not { Length: > 100 } jpeg || BrowserRemote.JpegWidth(jpeg) is not int jpegWidth || jpegWidth < 120)
        {
            return (false, "the snapshot gave no JPEG");
        }

        steps.Add($"a snapshot of {jpegWidth} px as JPEG");

        string pdf = Path.Combine(folder, "printed.pdf");
        long token = 0;
        var printed = await MacBrowserWindows.OnMain<bool>(done =>
        {
            token = ++_printToken;
            _printsWaiting[token] = ok => done.TrySetResult(ok);
            if (!MacBrowserKit.Print(_web, Window, _delegate, pdf, token))
            {
                _printsWaiting.Remove(token);
                done.TrySetResult(false);
            }
        }, false).ConfigureAwait(false);
        if (!printed || !HasPdfHeader(pdf))
        {
            return (false, "the paged print wrote no PDF");
        }

        steps.Add($"a paged print to a {new FileInfo(pdf).Length} byte PDF");

        await Read("(location.href = 'file:///etc/hosts', 1)").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
        if (await Main(() => Source).ConfigureAwait(false) != page)
        {
            return (false, "a page's file: navigation was not refused");
        }

        steps.Add("a page's file: navigation refused");

        string original = Path.Combine(folder, "probe.pdf");
        File.Copy(pdf, original, overwrite: true);
        var staged = PdfView.Stage(original, Path.Combine(folder, "staged"));
        await Main(() => { Go(staged.AbsoluteUri); return true; }).ConfigureAwait(false);
        if (!await Loaded(s => s == staged.AbsoluteUri).ConfigureAwait(false))
        {
            return (false, "the staged PDF was not shown");
        }

        steps.Add("a staged PDF shown as its https address");

        int ended = await Main(() => _downloadsEnded).ConfigureAwait(false);
        await Main(() => { Go(server.Url("/dl")); return true; }).ConfigureAwait(false);
        var waited = Stopwatch.StartNew();
        string? destination = null;
        while (waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            var (path, done) = await Main(() => (_downloads.LastOrDefault()?.Path, _downloadsEnded > ended)).ConfigureAwait(false);
            if (path is { Length: > 0 } && destination is null)
            {
                destination = path;
            }

            if (done)
            {
                break;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        if (destination is null || !destination.StartsWith(folder, StringComparison.Ordinal))
        {
            return (false, "a download's destination was not chosen");
        }

        steps.Add("a download saved to its free name");

        var bar = await Main(() => (_buttons.Count, FromNSString(Send(_buttons[BrowserToolItem.Back], Sel("toolTip"))))).ConfigureAwait(false);
        if (bar.Item1 != BrowserBarLayout.Buttons.Count || bar.Item2 != BrowserText.BackName)
        {
            return (false, "the bar's controls were not made");
        }

        var rows = BrowserLinks.MenuItems([new BrowserLink("https://example.com/", "Example", DateTimeOffset.UnixEpoch)], null);
        var (menuRows, chose) = await Main(() => MacPictureMenu.ProbeRows(rows, BrowserLinks.RowBase)).ConfigureAwait(false);
        if (menuRows != rows.Count || !chose)
        {
            return (false, "the saved links' menu did not hand its choice back");
        }

        steps.Add($"the bar's {bar.Item1} buttons with tooltips and a menu");

        bool removed = await MacBrowserWindows.OnMain<bool>(done => MacBrowserKit.RemoveAll(_store, () => done.TrySetResult(true)), false).ConfigureAwait(false);
        if (!removed)
        {
            return (false, "removeDataOfTypes: did not answer");
        }

        steps.Add("the store cleared");

        // A visit closed by the user (the hand-over's path): the store's cookies for the hosts it went to, read after the view is gone.
        var visit = new TaskCompletionSource<BrowserVisit>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Main(() =>
        {
            lock (_pendingVisits)
            {
                _pendingVisits.Add(visit);
            }

            _jobs.BeginVisit();
            Go(page);
            return true;
        }).ConfigureAwait(false);
        if (!await Loaded(s => s == page).ConfigureAwait(false))
        {
            return (false, "the visit's page did not load");
        }

        await Main(() =>
        {
            _handOver = true;
            Close();
            return true;
        }).ConfigureAwait(false);
        BrowserVisit handed;
        try
        {
            handed = await visit.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return (false, "the visit's close handed nothing over");
        }

        if (!handed.Hosts.SequenceEqual(["127.0.0.1"]) || !handed.Cookies.Any(c => c.Name == "srv") || !handed.Cookies.Any(c => c.Name == "js"))
        {
            return (false, $"the visit's close handed over {handed.Cookies.Count} cookies for {string.Join(", ", handed.Hosts)}");
        }

        steps.Add($"a visit's close handed {handed.Cookies.Count} cookies over");
        return (true, "WebKit as Safari " + MacBrowserWindows.Identity.Major.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + MacBrowserWindows.Identity.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + string.Join("; ", steps));
    }

    /// <summary>The probe's loopback pages: a page with a cookie, a click log and a link; a download.</summary>
    private sealed class ProbeServer : IDisposable
    {
        private const string Page =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Probe</title></head><body style=\"margin:0\">" +
            "<button id=\"b\" style=\"position:absolute;left:0;top:0;width:200px;height:100px\">Press</button>" +
            "<script>window.pageSecret = 1; document.cookie = 'js=1; path=/'; document.body.dataset.log = '';" +
            "document.getElementById('b').addEventListener('click', e => { document.body.dataset.log += 'click:' + e.isTrusted + ','; });</script>" +
            "</body></html>";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public ProbeServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public string Url(string path) => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + path;

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    _ = Task.Run(() => ServeAsync(client));
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                }

                string path = line?.Split(' ') is { Length: > 1 } parts ? parts[1] : "/";
                bool download = path.StartsWith("/dl", StringComparison.Ordinal);
                byte[] body = Encoding.UTF8.GetBytes(download ? new string('x', 4096) : Page);
                string head = "HTTP/1.1 200 OK\r\nContent-Type: " + (download ? "application/octet-stream" : "text/html; charset=utf-8") + "\r\n"
                    + (download ? "Content-Disposition: attachment; filename=\"probe.bin\"\r\n" : "Set-Cookie: srv=2; Path=/; HttpOnly\r\n")
                    + "Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
                await stream.WriteAsync(body).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The view went away mid-answer.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
