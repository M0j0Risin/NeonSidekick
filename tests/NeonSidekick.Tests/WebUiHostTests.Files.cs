using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet's Files tab over HTTP (2026-11-16, plan lucid-roaming-lantern #41): the list behind the cookie with an ETag (a 404 without a
/// screen); a made file by its token alone, under its recorded name, whole only (a Range, even an unsatisfiable one, gets the whole file and
/// <c>Accept-Ranges: none</c>); an unknown token a 404, a file gone a 410; HTML a sandboxed attachment, a PDF inline with the page's policy, a
/// non-ASCII name in the <c>filename*</c> form; a navigation's same-origin and none pass, cross-site and same-site don't; over Funnel too.
/// </summary>
public sealed partial class WebUiHostTests
{
    private const string MadeToken = "0123456789abcdef0123456789abcdef";

    private string MadeFile(string name, byte[] bytes)
    {
        string dir = Path.Combine(Path.GetTempPath(), "neon-made-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        _backend.MadeFilePaths[MadeToken] = path;
        return path;
    }

    private static void Tidy(string path) => Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);

    [Fact]
    public async Task TheFiles_AreReadBehindTheCookie_WithAnETag_And404WithoutAScreen()
    {
        using var none = await _client.SendAsync(Get(WebUiHost.FilesPath));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        _backend.FilesView = new WebFilesView([new WebFileRow("a.pdf", "out", "3 KB", "pdf", "📄", WebUiHost.MadeFilePrefix + MadeToken + "/a.pdf")]);
        using var unsigned = await _client.SendAsync(Get(WebUiHost.FilesPath, signedIn: false));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using var first = await _client.SendAsync(Get(WebUiHost.FilesPath));
        var view = JsonSerializer.Deserialize(await first.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebFilesView)!;
        Assert.Equal("a.pdf", Assert.Single(view.Rows).Name);

        var again = Get(WebUiHost.FilesPath);
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var same = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf(WebUiHost.FilesPath));
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf(WebUiHost.MadeFilePrefix + MadeToken + "/a.pdf"));
    }

    [Fact]
    public async Task AMadeFile_IsServedWhole_ByItsToken_UnderItsOwnName()
    {
        string path = MadeFile("notes.txt", Encoding.UTF8.GetBytes("hello made file"));
        try
        {
            foreach (var range in new[] { new RangeHeaderValue(2, 4), new RangeHeaderValue(900, 999), null })
            {
                var request = Get(WebUiHost.MadeFilePrefix + MadeToken + "/renamed.txt");
                request.Headers.Range = range;
                using var whole = await _client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
                Assert.Equal("hello made file", await whole.Content.ReadAsStringAsync());
                Assert.Equal("none", Assert.Single(whole.Headers.GetValues("Accept-Ranges")));
                Assert.Equal("text/plain; charset=utf-8", whole.Content.Headers.ContentType?.ToString());
                Assert.Equal("inline; filename*=UTF-8''notes.txt", whole.Content.Headers.ContentDisposition?.ToString());
                Assert.Equal(WebUiHost.ContentSecurityPolicy + "; sandbox", Assert.Single(whole.Headers.GetValues("Content-Security-Policy")));
                Assert.Equal("nosniff", Assert.Single(whole.Headers.GetValues("X-Content-Type-Options")));
            }

            foreach (string unknown in new[] { "ffffffffffffffffffffffffffffffff/notes.txt", "notatoken/notes.txt", MadeToken.ToUpperInvariant() + "/notes.txt" })
            {
                using var refused = await _client.SendAsync(Get(WebUiHost.MadeFilePrefix + unknown));
                Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            }

            File.Delete(path);
            using var gone = await _client.SendAsync(Get(WebUiHost.MadeFilePrefix + MadeToken + "/notes.txt"));
            Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        }
        finally
        {
            Tidy(path);
        }
    }

    [Fact]
    public async Task Html_IsASandboxedAttachment_APdfInlineUnderThePagesPolicy_AndANonAsciiName_IsFilenameStar()
    {
        string html = MadeFile("page.html", Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
        try
        {
            using var page = await _client.SendAsync(Get(WebUiHost.MadeFilePrefix + MadeToken + "/page.html"));
            Assert.Equal(MadeFileTypes.Bytes, page.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment; filename*=UTF-8''page.html", page.Content.Headers.ContentDisposition?.ToString());
            Assert.Equal(WebUiHost.ContentSecurityPolicy + "; sandbox", Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        }
        finally
        {
            Tidy(html);
        }

        string pdf = MadeFile("scan.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
        try
        {
            using var shown = await _client.SendAsync(Get(WebUiHost.MadeFilePrefix + MadeToken + "/scan.pdf"));
            Assert.Equal("application/pdf", shown.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("inline;", shown.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
            Assert.Equal(WebUiHost.ContentSecurityPolicy, Assert.Single(shown.Headers.GetValues("Content-Security-Policy")));
        }
        finally
        {
            Tidy(pdf);
        }

        string word = MadeFile("résumé.docx", [1, 2, 3]);
        try
        {
            using var download = await _client.SendAsync(Get(WebUiHost.MadeFilePrefix + MadeToken + "/x.docx"));
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("attachment; filename*=UTF-8''r%C3%A9sum%C3%A9.docx", Assert.Single(download.Content.Headers.GetValues("Content-Disposition")));
        }
        finally
        {
            Tidy(word);
        }
    }

    [Fact]
    public async Task AMadeFile_PassesANavigation_NotACrossSiteAsk_IsServedOverFunnel_AndNeedsTheCookie()
    {
        string path = MadeFile("a.txt", "a"u8.ToArray());
        try
        {
            foreach (var (site, status) in new[] { ("same-origin", HttpStatusCode.OK), ("none", HttpStatusCode.OK), ("cross-site", HttpStatusCode.Forbidden), ("same-site", HttpStatusCode.Forbidden) })
            {
                var request = Get(WebUiHost.MadeFilePrefix + MadeToken + "/a.txt");
                request.Headers.Add("Sec-Fetch-Site", site);
                using var answer = await _client.SendAsync(request);
                Assert.Equal(status, answer.StatusCode);
            }

            using var funnel = await _client.SendAsync(ThroughServe(Get(WebUiHost.MadeFilePrefix + MadeToken + "/a.txt"), "203.0.113.9", funnel: true));
            Assert.Equal(HttpStatusCode.OK, funnel.StatusCode);
            using var signedOut = await _client.SendAsync(Get(WebUiHost.MadeFilePrefix + MadeToken + "/a.txt", signedIn: false));
            Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        }
        finally
        {
            Tidy(path);
        }
    }
}
