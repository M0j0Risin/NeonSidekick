using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// The app's own client (<see cref="WebHttp"/>) against a loopback socket (2026-10-11): what goes on the wire, not what the request
/// object holds — the headers in Chrome's order, <c>Accept-Encoding</c> once and in its place (the handler adds none of its own),
/// the jar's cookie after the rest, and a gzip body still decoded.
/// </summary>
public class WebHttpWireTests
{
    private const string Page = "<html><head><title>Wire</title></head><body><h1>Wire</h1><p>Read over a real socket, gzip and all.</p></body></html>";

    [Fact]
    public async Task TheClient_WritesChromesOrder_OneAcceptEncoding_AndDecodesGzip()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var jar = new CookieContainer();
        jar.Add(new Uri($"http://127.0.0.1:{port}/"), new Cookie("sid", "1"));
        using var http = WebHttp.Create(new LanPolicy(() => NetworkReach.Both), jar);
        var fetcher = new WebFetcher(http, new FakeHeadlessBrowser { Executable = null }, cookies: jar);

        var served = ServeOnceAsync(listener);
        var page = await fetcher.FetchAsync(new Uri($"http://127.0.0.1:{port}/"), new FetchOptions(FetchEngine.HttpClient, "", NetworkReach.Both), CancellationToken.None);
        var lines = await served;

        Assert.True(page.Ok, page.Detail);
        Assert.Equal("Wire", page.Page!.Title);
        var names = lines.Skip(1).Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)].ToLowerInvariant()).ToList();
        Assert.Equal(["host", .. BrowserHeaders.Order.Where(h => h != "Referer").Select(h => h.ToLowerInvariant()), "cookie"], names);
        Assert.Contains(lines, l => l.Equals("Accept-Encoding: gzip, deflate, br", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Equals("Cookie: sid=1", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads one request's head, answers it with <see cref="Page"/> gzipped, and returns the head's lines.</summary>
    private static async Task<List<string>> ServeOnceAsync(TcpListener listener)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = await listener.AcceptTcpClientAsync(cancel.Token);
        var stream = client.GetStream();
        var head = new StringBuilder();
        var buffer = new byte[4096];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer, cancel.Token);
            if (read == 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        using var zipped = new MemoryStream();
        using (var gzip = new GZipStream(zipped, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(Page));
        }

        byte[] body = zipped.ToArray();
        string answer = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Encoding: gzip\r\n"
            + "Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), cancel.Token);
        await stream.WriteAsync(body, cancel.Token);
        await stream.FlushAsync(cancel.Token);
        string text = head.ToString();
        return text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)].Split("\r\n").ToList();
    }
}
