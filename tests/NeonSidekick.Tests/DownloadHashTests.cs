using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>download_file</c>'s <c>sha256</c> / <c>sha512</c> (2026-11-15, the web-tools plan's §5, feature idea #29): a match says so, a mismatch
/// keeps nothing (an old file under <c>overwrite</c> intact), a bad value is refused before any download; and a server's file name loses the
/// characters that would disguise it.
/// </summary>
public sealed class DownloadHashTests : IDisposable
{
    private static readonly byte[] Body = [1, 2, 3, 4, 5];

    private readonly StubHttpMessageHandler _http = new();
    private readonly ManualTimeProvider _time = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly DownloadFileTool _download;

    public DownloadHashTests()
    {
        var web = new WebAccess(new HttpClient(_http), new FakeHeadlessBrowser(), _time, (host, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }), _ => { });
        _root = Path.Combine(_dir, "files");
        _download = new DownloadFileTool(web, new Files.WorkingDirectory(() => _root, _time), () => new AppSettingsData());
        _http.Map("https://example.com/tool.zip", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Body, "application/zip")));
        _http.Map("https://example.com/data.tgz", (_, _) =>
        {
            var response = StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Body, "application/gzip");
            response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly string Sha256 = Convert.ToHexStringLower(SHA256.HashData(Body));
    private static readonly string Sha512 = Convert.ToHexStringLower(SHA512.HashData(Body));
    private static readonly string Wrong = new('0', 64);

    private async Task<string> CallAsync(params (string Name, object? Value)[] pairs) =>
        ToolAnswers.Text(await _download.InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value)), CancellationToken.None))!;

    [Fact]
    public async Task AMatch_IsSaved_AndSaysVerified()
    {
        Assert.EndsWith(WebText.Verified("SHA-256"), await CallAsync(("url", "https://example.com/tool.zip"), ("sha256", "SHA256:" + Sha256.ToUpperInvariant()[..10] + " " + Sha256.ToUpperInvariant()[10..])), StringComparison.Ordinal);
        Assert.Equal(Body, File.ReadAllBytes(Path.Combine(_root, "tool.zip")));

        string again = await CallAsync(("url", "https://example.com/tool.zip"), ("path", "b.zip"), ("sha512", Sha512), ("sha256", "  "));
        Assert.EndsWith(WebText.Verified("SHA-512"), again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMismatch_KeepsNothing_NamesBothDigests_AndLeavesAnOldFileAsItWas()
    {
        string answer = await CallAsync(("url", "https://example.com/tool.zip"), ("sha256", Wrong));

        Assert.Equal(WebText.HashMismatch(WebText.Sha256Name, Wrong, Sha256, "https://example.com/tool.zip", "https://example.com/tool.zip", "", replaced: false), answer);
        Assert.StartsWith("Error:", answer, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "tool.zip")));
        Assert.Empty(Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : []);

        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "tool.zip"), [7]);
        string replaced = await CallAsync(("url", "https://example.com/tool.zip"), ("sha256", Wrong), ("overwrite", true));
        Assert.StartsWith("Error:", replaced, StringComparison.Ordinal);
        Assert.Equal(WebText.HashMismatch(WebText.Sha256Name, Wrong, Sha256, "https://example.com/tool.zip", "https://example.com/tool.zip", "", replaced: true), replaced);
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(_root, "tool.zip")));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AMismatchSentCompressed_NamesTheEncoding_AndAPlainOneDoesNot()
    {
        string compressed = await CallAsync(("url", "https://example.com/data.tgz"), ("sha256", Wrong));
        string plain = await CallAsync(("url", "https://example.com/tool.zip"), ("sha256", Wrong));

        Assert.Equal(WebText.HashMismatch(WebText.Sha256Name, Wrong, Sha256, "https://example.com/data.tgz", "https://example.com/data.tgz", "gzip", replaced: false), compressed);
        Assert.Contains("Content-Encoding: gzip", compressed, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Encoding", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Encoding", WebText.HashMismatch("SHA-256", "a", "b", "https://x.example/", "", "identity", false), StringComparison.Ordinal);
    }

    [Fact]
    public void AMismatchAfterARedirect_NamesTheHostItCameFrom()
    {
        Assert.Contains("(via cdn.example)", WebText.HashMismatch("SHA-256", "a", "b", "https://x.example/f", "https://cdn.example/f", "", false), StringComparison.Ordinal);
        Assert.DoesNotContain("via", WebText.HashMismatch("SHA-256", "a", "b", "https://x.example/f", "https://x.example/g", "", false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{0}  tool.zip")]
    [InlineData("sha-256:{0}")]
    [InlineData("sha256={0}")]
    [InlineData("SHA256 (tool.zip) = {0}")]
    public void ADigestInACommonForm_IsRead(string form)
    {
        var (expected, error) = DownloadFileTool.ReadDigest(string.Format(System.Globalization.CultureInfo.InvariantCulture, form, Sha256.ToUpperInvariant()), "");

        Assert.Null(error);
        Assert.Equal(Sha256, expected!.Hex);
    }

    [Fact]
    public void TwoDifferentDigestsInOneValue_AreRefused()
    {
        Assert.NotNull(DownloadFileTool.ReadDigest(Sha256 + " " + Wrong, "").Error);
        Assert.Null(DownloadFileTool.ReadDigest(Sha256 + " = " + Sha256, "").Error);
    }

    [Fact]
    public async Task ARefusalBeforeAnyByte_SaysTheDigestWasNotChecked()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "tool.zip"), [7]);

        string answer = await CallAsync(("url", "https://example.com/tool.zip"), ("sha256", Sha256));

        Assert.EndsWith(WebText.DigestNotChecked(WebText.Sha256Name), answer, StringComparison.Ordinal);
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(_root, "tool.zip")));
    }

    [Fact]
    public async Task AFailedDownloadIntoNewFolders_LeavesNoFolderBehind_AndKeepsOneThatWasThere()
    {
        string answer = await CallAsync(("url", "https://example.com/tool.zip"), ("path", "new/deep/x.zip"), ("sha256", Wrong));

        Assert.StartsWith("Error:", answer, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "new")));

        Directory.CreateDirectory(Path.Combine(_root, "kept"));
        await CallAsync(("url", "https://example.com/tool.zip"), ("path", "kept/inner/x.zip"), ("sha256", Wrong));
        Assert.True(Directory.Exists(Path.Combine(_root, "kept")));
        Assert.False(Directory.Exists(Path.Combine(_root, "kept", "inner")));
    }

    [Fact]
    public async Task ADigest_AsksForTheBodyAsSent()
    {
        HttpRequestMessage? seen = null;
        _http.Map("https://example.com/raw.bin", (request, _) =>
        {
            seen = request;
            return Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Body, "application/octet-stream"));
        });

        await CallAsync(("url", "https://example.com/raw.bin"), ("sha256", Sha256));

        Assert.NotNull(seen);
        Assert.True(seen!.Options.TryGetValue(WebHttp.RawBodyKey, out bool raw) && raw);
        Assert.Equal(["identity"], seen.Headers.AcceptEncoding.Select(e => e.Value));
    }

    [Theory]
    [InlineData("abc", "")]
    [InlineData("", "abc")]
    public async Task ABadDigest_IsRefused_BeforeAnyDownload(string sha256, string sha512)
    {
        string answer = await CallAsync(("url", "https://example.com/tool.zip"), ("sha256", sha256), ("sha512", sha512));

        Assert.StartsWith("Error: " + (sha256.Length > 0 ? "sha256 must be 64" : "sha512 must be 128") + " hex digits", answer, StringComparison.Ordinal);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task BothDigests_AreRefused_BeforeAnyDownload()
    {
        Assert.Equal(WebText.BothDigests, await CallAsync(("url", "https://example.com/tool.zip"), ("sha256", Sha256), ("sha512", Sha512)));
        Assert.Empty(_http.Requests);
        Assert.Equal((null, null), DownloadFileTool.ReadDigest("", " "));
        Assert.NotNull(DownloadFileTool.ReadDigest(new string('g', 64), "").Error);
    }

    [Fact]
    public void AServersFileName_LosesTheCharactersThatWouldDisguiseIt_ButKeepsItsJoiners()
    {
        Assert.Equal("invoice-fdp.exe", Files.FileNames.ServerName("invoice\u202Efdp.exe"));
        Assert.Equal("a-b.txt", Files.FileNames.ServerName("a\u200Bb.txt"));
        Assert.Equal("a-b.txt", Files.FileNames.ServerName("a\u00ADb.txt"));
        Assert.Equal("a-b.txt", Files.FileNames.ServerName("a\u200E\u200Fb.txt"));
        Assert.Equal("Family 👨\u200D👩.png", Files.FileNames.ServerName("Family 👨\u200D👩.png"));
        Assert.Equal("می\u200Cخواهم.pdf", Files.FileNames.ServerName("می\u200Cخواهم.pdf"));
        Assert.Equal("a-.txt", Files.FileNames.ServerName("a\u200D.txt"));
        Assert.Equal("", Files.FileNames.ServerName("\u202E\u200B"));
    }

    [Fact]
    public void TheUsersOwnName_KeepsEveryCharacterButAPathsAndTheInvalidOnes()
    {
        Assert.Equal("می\u200Cخواهم.docx", DownloadFileTool.Sanitise("می\u200Cخواهم.docx"));
        Assert.Equal("x.txt", DownloadFileTool.Sanitise("dir/x.txt"));
    }

    [Fact]
    public async Task AServerNameOfOnlyHiddenCharacters_FallsBackToTheUrls()
    {
        _http.Map("https://example.com/report.pdf", (_, _) =>
        {
            var response = StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Body, "application/pdf");
            response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = "\u202E\u200B" };
            return Task.FromResult(response);
        });

        await CallAsync(("url", "https://example.com/report.pdf"));

        Assert.True(File.Exists(Path.Combine(_root, "report.pdf")));
    }

    [Fact]
    public void TheSchema_TakesBothDigests()
    {
        var properties = _download.JsonSchema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("sha256", out _));
        Assert.True(properties.TryGetProperty("sha512", out _));
    }
}
