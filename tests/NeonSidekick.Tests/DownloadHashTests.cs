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
        _http.Map("https://example.com/data.tgz", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Body, "application/gzip")));
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

        Assert.Equal(WebText.HashMismatch("SHA-256", Wrong, Sha256, "https://example.com/tool.zip", "tool.zip"), answer);
        Assert.StartsWith("Error:", answer, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "tool.zip")));
        Assert.Empty(Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : []);

        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "tool.zip"), [7]);
        Assert.StartsWith("Error:", await CallAsync(("url", "https://example.com/tool.zip"), ("sha256", Wrong), ("overwrite", true)), StringComparison.Ordinal);
        Assert.Equal([7], File.ReadAllBytes(Path.Combine(_root, "tool.zip")));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AMismatchOnACompressedName_SaysItMayHaveBeenUnpackedInTransit()
    {
        Assert.Contains("unpacked in transit", await CallAsync(("url", "https://example.com/data.tgz"), ("sha256", Wrong)), StringComparison.Ordinal);
        Assert.DoesNotContain("unpacked", await CallAsync(("url", "https://example.com/tool.zip"), ("sha256", Wrong)), StringComparison.Ordinal);
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
    public void AServersFileName_LosesTheCharactersThatWouldDisguiseIt_ButKeepsAnEmojisJoiner()
    {
        Assert.Equal("invoice-fdp.exe", DownloadFileTool.Sanitise("invoice‮fdp.exe"));
        Assert.Equal("a-b.txt", DownloadFileTool.Sanitise("a​b.txt"));
        Assert.Equal("Family 👨‍👩.png", DownloadFileTool.Sanitise("Family 👨‍👩.png"));
    }

    [Fact]
    public void TheSchema_TakesBothDigests()
    {
        var properties = _download.JsonSchema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("sha256", out _));
        Assert.True(properties.TryGetProperty("sha512", out _));
    }
}
