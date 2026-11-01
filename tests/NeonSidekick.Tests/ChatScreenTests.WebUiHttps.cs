using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/webui</c>'s HTTPS address on the screen (2026-10-31, plan in-the-past-we-ve-iterative-seahorse): Tailscale Serve's address (a fake seam)
/// first, copied by <c>/webui copy</c> and the pane's copy, <c>off</c> and a typed address, the hints (another port, a failed ask, a broken saved
/// value), Funnel's note after a copy, and the shown tab kept by its title when the HTTPS tab comes between two passes. The 5 s reuse is the
/// detector's (2026-11-01, the review's P2-9: <c>TailscaleServeTests</c>); the fake here answers every pass.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>Tailscale Serve's rules for the screen; null (the default) = none (not Windows).</summary>
    private Func<int, CancellationToken, Task<TailscaleServeResult?>>? _tailscaleServe;

    private const string ServeAddress = "https://hoopdiethink.tail9511ae.ts.net";

    /// <summary>A Serve seam that answers <paramref name="answer"/> and counts its calls.</summary>
    private Func<int> ServeAnswers(Func<int, TailscaleServeResult?> answer)
    {
        int calls = 0;
        _tailscaleServe = (_, _) => Task.FromResult(answer(++calls));
        return () => calls;
    }

    private void HttpsReady(string address = WebUiHttpsSetting.Auto)
    {
        // local: the links are the HTTPS one and localhost's alone, whatever this machine's adapters.
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = true; d.WebUiReach = "local"; d.WebUiHttpsAddress = address; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _webUiHost = _ => new RecordingHost();
    }

    [Fact]
    public async Task WebUiCopy_CopiesTheHttpsLink_WhenServeForwardsToThePort()
    {
        HttpsReady();
        var calls = ServeAnswers(_ => new TailscaleServeResult(ServeAddress, false, []));
        var input = WhenIdle(i => PushLine(i, "/webui copy"), i => PushLine(i, "/webui copy"), i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal(2, _copied.Count);
        Assert.All(_copied, c => Assert.StartsWith(ServeAddress + "/signin?t=", c, StringComparison.Ordinal));
        Assert.Equal(2, calls());   // the screen keeps nothing: the detector does (2026-11-01)
    }

    [Fact]
    public async Task WebUiCopy_OfAFunneledHttpsLink_SaysSoAfterTheCopy()
    {
        HttpsReady();
        ServeAnswers(_ => new TailscaleServeResult(ServeAddress, true, []));
        var input = WhenIdle(i => PushLine(i, "/webui copy"), i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.StartsWith(ServeAddress + "/signin?t=", Assert.Single(_copied), StringComparison.Ordinal);
        Assert.Contains(WebUiText.FunnelNote, output);   // local: Funnel's requests are let in
    }

    [Fact]
    public async Task ThePane_OpensOnTheHttpsTab_HeadedByServesNote_AndItsCopyCopiesTheHttpsLink()
    {
        HttpsReady();
        ServeAnswers(_ => new TailscaleServeResult(ServeAddress, false, []));
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Char(WebUiText.CopyKey), Keys.Escape);
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Contains(WebUiText.HttpsTabTitle + " · localhost", output);   // the HTTPS tab first
        Assert.Contains(WebUiText.HttpsDetectedNote, output);
        Assert.Contains("Reach: local — localhost only" + WebUiText.LocalPlusHttps, output);
        Assert.StartsWith(ServeAddress + "/signin?t=", Assert.Single(_copied), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Off_ShowsNoHttpsTab_AndATypedAddress_WinsWithoutAskingServe()
    {
        HttpsReady(WebUiHttpsSetting.Off);
        var calls = ServeAnswers(_ => new TailscaleServeResult(ServeAddress, false, []));
        var input = WhenIdle(
            i => PushLine(i, "/webui copy"),
            i =>
            {
                _settings.Update(d => d.WebUiHttpsAddress = "https://proxy.example:8443");
                PushLine(i, "/webui copy");
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal(0, calls());
        Assert.StartsWith("http://localhost:", _copied[0], StringComparison.Ordinal);
        Assert.StartsWith("https://proxy.example:8443/signin?t=", _copied[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARuleForAnotherPort_IsNamedInThePanesStatus()
    {
        HttpsReady();
        ServeAnswers(_ => new TailscaleServeResult(null, false, [8766], FreeServePort: 443));
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Escape);
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Contains(WebUiText.ServeNoRuleHint(AppSettingsDataDefaults.WebUiPort, 443), output);
        Assert.Contains("Tailscale Serve has no rule for port 8765: tailscale serve --bg 8765 adds one.", output);
        Assert.DoesNotContain(WebUiText.HttpsTabTitle + " · ", output);
    }

    [Fact]
    public async Task AFailedAsk_AndABrokenSavedValue_AreHintedAt()
    {
        HttpsReady("htps:/oops");
        var calls = ServeAnswers(_ => new TailscaleServeResult(null, false, [], Failure: WebUiText.ServeFailureStatus(403)));
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Escape);
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.True(calls() >= 1);   // a broken value is detected as auto is
        Assert.Contains("Tailscale Serve couldn't be asked (HTTP 403): no HTTPS tab.", output);
        Assert.Contains(WebUiText.HttpsSettingBrokenHint, output);
    }

    [Fact]
    public async Task ATypedAddress_CountsOnlyWhileServing()
    {
        HttpsReady("https://proxy.example:8443");
        _settings.Update(d => d.WebUi = false);
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Escape);
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Contains("Reach: local — localhost only", output);
        Assert.DoesNotContain(WebUiText.LocalPlusHttps, output);   // not serving: no HTTPS address, typed or found
    }

    /// <summary>
    /// Pass 51: the HTTPS tab appears between two passes (Serve answered between them) while the localhost tab is shown, so the pane
    /// reopens on the localhost tab by its title, not on the index that now holds the HTTPS one; its copy copies localhost's link again.
    /// </summary>
    [Fact]
    public async Task TheShownTab_IsKeptByItsTitle_WhenTheHttpsTabComesBetweenTwoPasses()
    {
        HttpsReady();
        var calls = ServeAnswers(n => n == 1 ? null : new TailscaleServeResult(ServeAddress, false, []));
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Char(WebUiText.CopyKey), Keys.Char(WebUiText.CopyKey), Keys.Escape);
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.True(calls() >= 2);
        Assert.Equal(2, _copied.Count);
        Assert.All(_copied, c => Assert.StartsWith("http://localhost:", c, StringComparison.Ordinal));
    }
}
