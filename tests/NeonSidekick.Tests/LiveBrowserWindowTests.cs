using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// Whether the real Browser window may be opened (2026-10-26, the bot-check load wait, the user's pick: gated):
/// <c>NEONSIDEKICK_TEST_BROWSER_WINDOW</c> is <c>1</c>, on Windows with a WebView2 Runtime or on macOS 14 and later. Skipped without, so
/// <c>build.ps1</c> and CI never open a window; never a CI safety net. The window shows for a few seconds while it runs. Setting the variable
/// says a window can show: a run with none (a Mac over SSH) fails <see cref="BotCheckEnd.Unavailable"/> with the window's own reason, since
/// xUnit cannot skip a test once it runs.
/// </summary>
internal static class LiveBrowserWindow
{
    public const string Variable = "NEONSIDEKICK_TEST_BROWSER_WINDOW";

    public static string? Unavailable
    {
        get
        {
            if (Environment.GetEnvironmentVariable(Variable)?.Trim() != "1")
            {
                return $"{Variable} is not set (1 opens the Browser window for a few seconds).";
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    return WebViewNative.RuntimeVersion(out _) is null ? "No WebView2 Runtime is installed." : null;
                }
                catch (DllNotFoundException)
                {
                    return "The WebView2 loader is not beside the tests.";
                }
            }

            return OperatingSystem.IsMacOSVersionAtLeast(14) ? null : "The Browser window is Windows or macOS 14 and later only.";
        }
    }

    /// <summary>The platform's window: WebView2's on Windows, WebKit's on a Mac.</summary>
    public static IBrowserWindow Window =>
        OperatingSystem.IsWindows() ? BrowserWindow.Window
        : OperatingSystem.IsMacOS() ? MacBrowserWindows.Window
        : throw new PlatformNotSupportedException("The Browser window is Windows or macOS 14 and later only.");

    /// <summary>The window's profile folder pointed at <paramref name="folder"/>; the old one back when the result is disposed.</summary>
    public static IDisposable UseProfile(string folder) =>
        OperatingSystem.IsWindows() ? UseWindowsProfile(folder)
        : OperatingSystem.IsMacOS() ? UseMacProfile(folder)
        : new Restore(() => { });

    [SupportedOSPlatform("windows")]
    private static Restore UseWindowsProfile(string folder)
    {
        var old = BrowserWindow.ProfileDirectory;
        BrowserWindow.ProfileDirectory = () => folder;
        return new Restore(() => BrowserWindow.ProfileDirectory = old);
    }

    [SupportedOSPlatform("macos")]
    private static Restore UseMacProfile(string folder)
    {
        var old = MacBrowserWindows.ProfileDirectory;
        MacBrowserWindows.ProfileDirectory = () => folder;
        return new Restore(() => MacBrowserWindows.ProfileDirectory = old);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>Skips unless <see cref="LiveBrowserWindow"/> allows the real window.</summary>
public sealed class BrowserWindowFactAttribute : FactAttribute
{
    public BrowserWindowFactAttribute()
    {
        if (LiveBrowserWindow.Unavailable is { } why)
        {
            Skip = why;
        }
    }
}

/// <summary>
/// The real Browser window on a check that clears by itself (2026-10-26, the user's report, session 795: a Cloudflare check passed, and the
/// window refused the page under the check's 403). A loopback server's <c>/</c> is a 403 "Just a moment" page that moves on to <c>/ok</c>
/// after 1.5 s, as a solved check does; <c>/ok</c> is a 200 article with a cookie whose one picture answers after 8 s, so its load completes
/// later than <see cref="DomSettle.MostAfterLoad"/>. The window must wait for that load, then read the page: before the fix it took the
/// article while it still loaded, under the first page's status, and ended Refused (HTTP 403).
/// </summary>
public sealed class LiveBrowserWindowTests
{
    private const string Marker = "Mushroomwordmarker";
    private const string CookieName = "neon_check";
    private static readonly TimeSpan SlowPicture = TimeSpan.FromSeconds(8);

    private const string CheckPage =
        "<!doctype html><html><head><title>Just a moment...</title></head><body><p>Checking your browser.</p>"
        + "<script>setTimeout(() => location.href = '/ok', 1500);</script></body></html>";

    private static readonly string Article =
        "<!doctype html><html><head><title>Stuffed mushrooms</title></head><body><article><h1>Stuffed mushrooms</h1>"
        + string.Concat(Enumerable.Range(1, 8).Select(i => $"<p>{Marker} paragraph {i.ToString(CultureInfo.InvariantCulture)}: wipe the mushrooms clean, take out the stems, chop them fine with garlic and scallions, and fill the caps before they bake until golden.</p>"))
        + "<img src=\"/slow.png\" alt=\"\"></article></body></html>";

    // The smallest PNG: one transparent pixel.
    private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [BrowserWindowFact]
    public async Task ACheckThatMovesOn_IsReadOnceTheNextPageHasLoaded()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var serving = ServeAsync(listener, log, stop.Token);
        string profile = Path.Combine(Path.GetTempPath(), "NeonSidekick-browser-window-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var window = LiveBrowserWindow.Window;
        void Heard(DiagnosticEvent e)
        {
            if (e.Category is "Browser" or "Viewer")
            {
                log.Enqueue(e.Message);
            }
        }

        DiagnosticLog.Emitted += Heard;
        try
        {
            using (LiveBrowserWindow.UseProfile(profile))
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var url = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
                    var result = await window.SolveAsync(new BotCheckRequest(url, BotCheckUse.Fetch, "HTTP 403"), timeout.Token);

                    Assert.True(result.End == BotCheckEnd.Cleared, $"{result.End} (HTTP {result.Status.ToString(CultureInfo.InvariantCulture)}): {result.Detail}{Environment.NewLine}{string.Join(Environment.NewLine, log)}");
                    Assert.Equal(200, result.Status);
                    Assert.Contains(Marker, result.Html ?? "", StringComparison.Ordinal);
                    Assert.Contains(result.Cookies, c => c.Name == CookieName);
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            DiagnosticLog.Emitted -= Heard;
            await stop.CancelAsync();
            listener.Stop();
            await serving;
            DeleteBestEffort(profile);
        }
    }

    // Each connection on its own task, one request each (Connection: close), until stopped.
    private static async Task ServeAsync(TcpListener listener, System.Collections.Concurrent.ConcurrentQueue<string> log, CancellationToken stop)
    {
        var connections = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                connections.Add(AnswerAsync(client, log, stop));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        await Task.WhenAll(connections);
    }

    private static async Task AnswerAsync(TcpClient client, System.Collections.Concurrent.ConcurrentQueue<string> log, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[4096];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(buffer, stop);
                    if (read == 0)
                    {
                        return;
                    }

                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                string line = head.ToString()[..head.ToString().IndexOf("\r\n", StringComparison.Ordinal)];
                string path = line.Split(' ') is [_, var p, ..] ? p : "/";
                log.Enqueue("The test's server was asked for " + path + ".");
                var (status, type, body, extra) = path switch
                {
                    "/" => ("403 Forbidden", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(CheckPage), ""),
                    "/ok" => ("200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Article), $"Set-Cookie: {CookieName}=1; Path=/\r\n"),
                    "/slow.png" => ("200 OK", "image/png", Pixel, ""),
                    _ => ("404 Not Found", "text/plain", Encoding.ASCII.GetBytes("no"), ""),
                };
                if (path == "/slow.png")
                {
                    await Task.Delay(SlowPicture, stop);
                }

                string answer = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\n{extra}Cache-Control: no-store\r\n"
                    + $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), stop);
                await stream.WriteAsync(body, stop);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    // The browser's processes can hold the profile for a moment after the close.
    private static void DeleteBestEffort(string folder)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
