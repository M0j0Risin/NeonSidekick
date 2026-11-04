using System.Net;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

public class UpdateCheckTests
{
    private const string Latest = "https://api.github.com/repos/M0j0Risin/NeonSidekick/releases/latest";
    private const string Download = "https://github.com/M0j0Risin/NeonSidekick/releases/download/";
    private static readonly UpdateVersion Running = new(0, 5, 9);

    private static string Release(string tag = "v0.5.10", string rid = "win-x64", string extension = ".zip", string? body = "notes", string? urlPrefix = null, bool sig = true, bool sha = true, long size = 100)
    {
        string name = $"NeonSidekick-{tag}-{rid}{extension}";
        string prefix = urlPrefix ?? Download + tag + "/";
        var assets = new List<string> { Asset(name, prefix, size) };
        if (sha)
        {
            assets.Add(Asset(name + ".sha256", Download + tag + "/", 150));
        }

        if (sig)
        {
            assets.Add(Asset(name + ".sha256.sig", Download + tag + "/", 90));
        }

        string bodyJson = body is null ? "null" : JsonSerializer.Serialize(body);
        return $$"""{"tag_name":"{{tag}}","body":{{bodyJson}},"html_url":"https://github.com/M0j0Risin/NeonSidekick/releases/tag/{{tag}}","assets":[{{string.Join(",", assets)}}]}""";
    }

    private static string Asset(string name, string prefix, long size) =>
        $$"""{"name":"{{name}}","size":{{size}},"browser_download_url":"{{prefix}}{{name}}"}""";

    private static (UpdateCheck Check, StubHttpMessageHandler Stub) Make(string json, HttpStatusCode status = HttpStatusCode.OK, string rid = "win-x64")
    {
        var stub = new StubHttpMessageHandler().Map(Latest, status, json);
        return (new UpdateCheck(new HttpClient(stub), UpdateSource.GitHub(AboutText.RepositoryUrl), Running, rid), stub);
    }

    private static Task<UpdateCheckResult> Run(string json, HttpStatusCode status = HttpStatusCode.OK, string rid = "win-x64") =>
        Make(json, status, rid).Check.CheckAsync(CancellationToken.None);

    [Fact]
    public async Task ANewerRelease_ByNumberNotByText_IsOffered()
    {
        var result = await Run(Release());
        Assert.Equal(UpdateCheckKind.Newer, result.Kind);
        Assert.Equal(new UpdateVersion(0, 5, 10), result.Release!.Version);
        Assert.Equal("notes", result.Release.Notes);
        Assert.Equal(100, result.Release.ArchiveBytes);
        Assert.EndsWith(".zip.sha256", result.Release.ShaUrl.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith(".zip.sha256.sig", result.Release.SigUrl.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v0.5.9")]
    [InlineData("v0.5.8")]
    [InlineData("v0.4.99")]
    public async Task TheSameOrOlder_IsNotOffered(string tag) => Assert.Equal(UpdateCheckKind.Latest, (await Run(Release(tag))).Kind);

    [Theory]
    [InlineData("0.5.10")]
    [InlineData("v0.5.10-beta")]
    [InlineData("v0.5")]
    [InlineData("v0.5.10.1")]
    [InlineData("release")]
    public async Task ATagThatIsNotPlain_CountsAsNoRelease(string tag) => Assert.Equal(UpdateCheckKind.Latest, (await Run(Release(tag))).Kind);

    [Fact]
    public async Task NoSignature_IsNotOffered_AndSaysSo()
    {
        var result = await Run(Release(sig: false));
        Assert.Equal(UpdateCheckKind.Unsigned, result.Kind);
        Assert.Null(result.Release);
        Assert.Equal(new UpdateVersion(0, 5, 10), result.Found);
    }

    [Fact]
    public async Task NoSha_OrNoArchiveForThisPlatform_IsNoPackage()
    {
        Assert.Equal(UpdateCheckKind.NoPackage, (await Run(Release(sha: false))).Kind);
        Assert.Equal(UpdateCheckKind.NoPackage, (await Run(Release(), rid: "osx-arm64")).Kind);
    }

    [Fact]
    public async Task TheAsset_IsChosenByTheGivenPlatform()
    {
        var result = await Run(Release(rid: "osx-arm64", extension: ".tar.gz"), rid: "osx-arm64");
        Assert.Equal(UpdateCheckKind.Newer, result.Kind);
        Assert.Equal("NeonSidekick-v0.5.10-osx-arm64.tar.gz", result.Release!.ArchiveName);
    }

    [Theory]
    [InlineData("https://evil.example/M0j0Risin/NeonSidekick/releases/download/v0.5.10/")]
    [InlineData("http://github.com/M0j0Risin/NeonSidekick/releases/download/v0.5.10/")]
    [InlineData("https://github.com/someone/else/releases/download/v0.5.10/")]
    [InlineData("https://github.com/M0j0Risin/NeonSidekick/raw/v0.5.10/")]
    [InlineData("https://github.com.evil.example/M0j0Risin/NeonSidekick/releases/download/v0.5.10/")]
    [InlineData("https://github.com:8443/M0j0Risin/NeonSidekick/releases/download/v0.5.10/")]
    public async Task AnAssetOffTheRule_CountsAsMissing(string prefix) =>
        Assert.Equal(UpdateCheckKind.NoPackage, (await Run(Release(urlPrefix: prefix))).Kind);

    [Fact]
    public async Task ASizeOfNothing_CountsAsMissing() => Assert.Equal(UpdateCheckKind.NoPackage, (await Run(Release(size: 0))).Kind);

    [Fact]
    public async Task NullNotes_StillOffer()
    {
        var result = await Run(Release(body: null));
        Assert.Equal(UpdateCheckKind.Newer, result.Kind);
        Assert.Null(result.Release!.Notes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, UpdateText.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateText.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, UpdateText.NoReleaseFound)]
    public async Task AFailureStatus_IsAReason(HttpStatusCode status, string reason)
    {
        var result = await Run("{}", status);
        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
        Assert.Equal(reason, result.Reason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task MalformedJson_IsAReason(string json)
    {
        var result = await Run(json);
        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
        Assert.Equal(UpdateText.AnswerUnreadable, result.Reason);
    }

    [Fact]
    public async Task ARefusedConnection_IsAReason()
    {
        var check = new UpdateCheck(new HttpClient(new StubHttpMessageHandler()), UpdateSource.GitHub(AboutText.RepositoryUrl), Running, "win-x64");
        var result = await check.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    [Fact]
    public async Task TheRequest_CarriesItsOwnUserAgent_AndNothingOfTheUsers()
    {
        string? agent = null;
        var stub = new StubHttpMessageHandler().Map(Latest, (request, _) =>
        {
            agent = string.Join(" ", request.Headers.GetValues("User-Agent"));
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Release()));
        });
        await new UpdateCheck(new HttpClient(stub), UpdateSource.GitHub(AboutText.RepositoryUrl), Running, "win-x64").CheckAsync(CancellationToken.None);
        Assert.Equal("NeonSidekick/0.5.9", agent);
    }

    [Fact]
    public async Task TheRequest_HasItsTenSecondLimit()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), UpdateCheck.RequestLimit);
        CancellationToken seen = default;
        var stub = new StubHttpMessageHandler().Map(Latest, (_, token) =>
        {
            seen = token;
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Release()));
        });
        await new UpdateCheck(new HttpClient(stub), UpdateSource.GitHub(AboutText.RepositoryUrl), Running, "win-x64").CheckAsync(CancellationToken.None);
        Assert.True(seen.CanBeCanceled);
    }

    [Fact]
    public async Task TheCallersCancel_IsThrown()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var (check, _) = Make(Release());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.CheckAsync(cancel.Token));
    }

    [Fact]
    public async Task AnAnswerOverTheCap_IsRefused()
    {
        var (check, _) = Make(new string('x', UpdateCheck.JsonCap + 1));
        var result = await check.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
        Assert.Equal(UpdateText.AnswerUnreadable, result.Reason);
    }
}
