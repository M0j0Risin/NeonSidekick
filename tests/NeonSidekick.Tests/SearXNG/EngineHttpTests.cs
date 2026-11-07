// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using NeonSidekick.Tests.Fakes;
using SearXNG.Net.Net;

namespace SearXNG.Net.Tests;

/// <summary>Cases follow searx/network/raise_for_httperror.py, with rest times from searx/settings.yml.</summary>
public class EngineHttpTests
{
    private static readonly Uri Page = new("https://example.com/search");

    private static EngineException Raise(int status, string server = "", string text = "") =>
        Assert.Throws<EngineException>(() => EngineHttp.RaiseForHttpError(status, server, text, Page));

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(302)]
    public void BelowFourHundred_IsNotAnError(int status)
    {
        EngineHttp.RaiseForHttpError(status, "", "", Page);
    }

    [Theory]
    [InlineData(402)]
    [InlineData(403)]
    public void AccessDenied_RestsThreeMinutes_AndNamesThePage(int status)
    {
        var e = Raise(status);

        Assert.Equal(EngineFailureKind.AccessDenied, e.Kind);
        Assert.Equal(TimeSpan.FromSeconds(180), e.SuspendFor);
        Assert.Equal(Page, e.RefusedUrl);
    }

    [Fact]
    public void TooManyRequests_RestsThreeMinutes()
    {
        var e = Raise(429);

        Assert.Equal(EngineFailureKind.TooManyRequests, e.Kind);
        Assert.Equal(TimeSpan.FromSeconds(180), e.SuspendFor);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public void AnotherError_GetsTheDefaultRest(int status)
    {
        var e = Raise(status);

        Assert.Equal(EngineFailureKind.HttpError, e.Kind);
        Assert.Null(e.SuspendFor);
    }

    [Theory]
    [InlineData(429, "x __cf_chl_jschl_tk__= y")]
    [InlineData(503, "/cdn-cgi/challenge-platform/ orchestrate/jsch/v1 window._cf_chl_enter(")]
    [InlineData(403, "__cf_chl_captcha_tk__=")]
    public void CloudflareChallenge_IsACaptcha_RestingFifteenDays(int status, string text)
    {
        var e = Raise(status, "cloudflare", text);

        Assert.Equal(EngineFailureKind.Captcha, e.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1296000), e.SuspendFor);
    }

    [Fact]
    public void CloudflareChallengeText_FromAnotherServer_IsAPlainRateLimit()
    {
        Assert.Equal(EngineFailureKind.TooManyRequests, Raise(429, "nginx", "__cf_chl_jschl_tk__=").Kind);
    }

    [Fact]
    public void CloudflareFirewall_IsDenied_RestingADay()
    {
        var e = Raise(403, "cloudflare", "<span class=\"cf-error-code\">1020</span>");

        Assert.Equal(EngineFailureKind.AccessDenied, e.Kind);
        Assert.Equal(TimeSpan.FromSeconds(86400), e.SuspendFor);
    }

    [Fact]
    public void Recaptcha_RestsAWeek()
    {
        var e = Raise(503, "", "src=\"https://www.google.com/recaptcha/api.js\"");

        Assert.Equal(EngineFailureKind.Captcha, e.Kind);
        Assert.Equal(TimeSpan.FromSeconds(604800), e.SuspendFor);
    }

    [Theory]
    [InlineData("all", "en-US,en;q=0.9")]
    [InlineData("de", "de,de-de;q=0.7,en;q=0.3")]
    [InlineData("de-DE", "de,de-DE;q=0.7,en;q=0.3")]
    [InlineData("zh-Hant", "zh,zh-zh;q=0.7,en;q=0.3")]
    [InlineData("es-419", "es,es-419;q=0.7,en;q=0.3")]
    [InlineData("de-de", "de,de-DE;q=0.7,en;q=0.3")]
    [InlineData("zh-tw", "zh,zh-TW;q=0.7,en;q=0.3")]
    [InlineData("zh-Hant-TW", "zh,zh-TW;q=0.7,en;q=0.3")]
    [InlineData("ca-ES-valencia", "ca,ca-ES;q=0.7,en;q=0.3")]
    [InlineData("en-US-posix", "en,en-US;q=0.7,en;q=0.3")]
    [InlineData("de-1996", "de,de-de;q=0.7,en;q=0.3")]
    [InlineData("de\n", "en-US,en;q=0.9")]
    [InlineData("bad\r\nX-Injected: 1", "en-US,en;q=0.9")]
    [InlineData("", "en-US,en;q=0.9")]
    public void AcceptLanguage_FollowsTheOnlineProcessor_AndRefusesABadLocale(string locale, string expected)
    {
        Assert.Equal(expected, EngineHttp.AcceptLanguage(locale));
    }

    [Fact]
    public async Task GetAsync_SendsTheHeaders_OverHttp2OrLower()
    {
        var handler = new StubHandler(_ => StubHandler.Text("page"));
        using var http = new HttpClient(handler);

        var response = await EngineHttp.GetAsync(http, Page, [new("User-Agent", "test/1.0")], CancellationToken.None);

        Assert.Equal("page", response.Text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
        Assert.Equal("test/1.0", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task GetAsync_ReadsTheServerHeader_ToRecognizeCloudflare()
    {
        var response = StubHandler.Text("__cf_chl_captcha_tk__=", HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("Server", "cloudflare");
        using var http = new HttpClient(new StubHandler(_ => response));

        var e = await Assert.ThrowsAsync<EngineException>(() => EngineHttp.GetAsync(http, Page, [], CancellationToken.None));

        Assert.Equal(EngineFailureKind.Captcha, e.Kind);
    }
}
