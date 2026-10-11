using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>The browser the web requests claim, the headless browser's settle rule and its DevTools port file (2026-10-11).</summary>
public class BrowserIdentityTests
{
    [Fact]
    public void SecChUa_IsChromiumsList_ForTheMajorVersion()
    {
        // Both read off the real browsers on 2026-10-11 (navigator.userAgentData.brands): the order, the GREASE name and its version all follow the major.
        Assert.Equal("\"Chromium\";v=\"154\", \"Google Chrome\";v=\"154\", \"Not A(Brand\";v=\"99\"", new BrowserIdentity(BrowserBrand.Chrome, 154, BrowserPlatform.Windows).SecChUa);
        Assert.Equal("\"Microsoft Edge\";v=\"155\", \"Chromium\";v=\"155\", \"Not(A:Brand\";v=\"24\"", new BrowserIdentity(BrowserBrand.Edge, 155, BrowserPlatform.Windows).SecChUa);
        Assert.Equal("\"Chromium\";v=\"140\", \"Not=A?Brand\";v=\"24\", \"Brave\";v=\"140\"", new BrowserIdentity(BrowserBrand.Brave, 140, BrowserPlatform.Windows).SecChUa);
    }

    [Fact]
    public void UserAgent_IsTheReducedForm_PerPlatformAndBrand()
    {
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36", new BrowserIdentity(BrowserBrand.Chrome, 154, BrowserPlatform.Windows).UserAgent);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0", new BrowserIdentity(BrowserBrand.Edge, 155, BrowserPlatform.Windows).UserAgent);
        Assert.Equal("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36", new BrowserIdentity(BrowserBrand.Brave, 154, BrowserPlatform.MacOS).UserAgent);
        Assert.Equal("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36", new BrowserIdentity(BrowserBrand.Chrome, 154, BrowserPlatform.Linux).UserAgent);
        Assert.Equal("\"Windows\"", new BrowserIdentity(BrowserBrand.Chrome, 154, BrowserPlatform.Windows).PlatformHint);
        Assert.Equal("\"macOS\"", new BrowserIdentity(BrowserBrand.Chrome, 154, BrowserPlatform.MacOS).PlatformHint);
        Assert.DoesNotContain("Headless", BrowserIdentity.Fallback.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void Of_ReadsTheBrandFromTheName_AndTheMajorFromTheVersion()
    {
        Assert.Equal(new BrowserIdentity(BrowserBrand.Edge, 155, BrowserPlatform.Windows), BrowserIdentity.Of(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", _ => "155.0.4283.45", BrowserPlatform.Windows));
        Assert.Equal(new BrowserIdentity(BrowserBrand.Chrome, 154, BrowserPlatform.Windows), BrowserIdentity.Of(@"C:\Program Files\Google\Chrome\Application\chrome.exe", _ => "154.0.8037.98", BrowserPlatform.Windows));
        Assert.Equal(BrowserBrand.Brave, BrowserIdentity.BrandOf(@"C:\x\BraveSoftware\Brave-Browser\Application\brave.exe"));
        Assert.Equal(BrowserBrand.Edge, BrowserIdentity.BrandOf("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"));
        Assert.Equal(BrowserBrand.Chrome, BrowserIdentity.BrandOf("/usr/bin/chromium"));
        // No version, or someone else's numbering (Brave's own 1.83), falls back to the pin.
        Assert.Equal(BrowserIdentity.FallbackMajor, BrowserIdentity.Of("chrome.exe", _ => null, BrowserPlatform.Windows).Major);
        Assert.Equal(BrowserIdentity.FallbackMajor, BrowserIdentity.Of("brave.exe", _ => "1.83.120", BrowserPlatform.Windows).Major);
        Assert.Null(BrowserIdentity.MajorOf("not a version"));
        Assert.Null(BrowserIdentity.MajorOf(" "));
        Assert.Equal(141, BrowserIdentity.MajorOf("141.1.83.120"));
    }

    [Fact]
    public void BundleVersion_ReadsTheShortVersionString()
    {
        Assert.Equal("155.0.4283.45", BrowserIdentity.BundleVersion("<dict>\n\t<key>CFBundleName</key>\n\t<string>Edge</string>\n\t<key>CFBundleShortVersionString</key>\n\t<string>155.0.4283.45</string>\n</dict>"));
        Assert.Null(BrowserIdentity.BundleVersion("<dict><key>CFBundleVersion</key><string>4283.45</string></dict>"));
    }

    [WindowsFact]
    public void For_ReadsAnInstalledBrowsersVersion_WhenThereIsOne()
    {
        if (new HeadlessBrowser().Locate("") is not { } executable)
        {
            return;
        }

        var identity = BrowserIdentity.For(executable);
        Assert.Equal(BrowserIdentity.MajorOf(BrowserIdentity.ReadVersion(executable)) ?? BrowserIdentity.FallbackMajor, identity.Major);
        Assert.Same(identity, BrowserIdentity.For(executable));   // read once while the file is unchanged
        Assert.NotEqual(DateTime.MinValue, BrowserIdentity.VersionStamp(executable));
    }

    [Fact]
    public void For_ReadsTheVersionAgain_WhenTheFileItComesFromChanges()
    {
        // A browser that updates while the app runs is claimed at its new version (2026-10-11), not the one read at the first fetch.
        string path = Path.Combine(Path.GetTempPath(), "neon-identity-" + Guid.NewGuid().ToString("N"), "chrome.exe");
        var stamp = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
        string version = "154.0.8037.98";
        int reads = 0;
        BrowserIdentity Ask() => BrowserIdentity.For(path, _ => stamp, _ => { reads++; return version; }, BrowserPlatform.Windows);

        Assert.Equal(154, Ask().Major);
        Assert.Equal(154, Ask().Major);
        Assert.Equal(1, reads);                       // unchanged: the held answer

        version = "155.0.8100.12";
        Assert.Equal(154, Ask().Major);               // a version alone never shows without a new write time
        stamp = stamp.AddMinutes(5);                  // the update renamed over the executable
        Assert.Equal(155, Ask().Major);
        Assert.Equal(2, reads);
        Assert.Equal(155, Ask().Major);
        Assert.Equal(2, reads);
    }

    private static readonly TimeSpan S = TimeSpan.FromSeconds(1);

    [Fact]
    public void Settle_TakesALoadedPage_OnceItsDomHoldsStill()
    {
        var settle = new DomSettle();
        Assert.False(settle.Observe(0.5 * S, loaded: false, changed: true, gated: false, thin: false));
        Assert.False(settle.Observe(1.0 * S, loaded: true, changed: true, gated: false, thin: false));
        Assert.False(settle.Observe(2.0 * S, loaded: true, changed: false, gated: false, thin: false));   // 1 s quiet
        Assert.True(settle.Observe(2.5 * S, loaded: true, changed: false, gated: false, thin: false));    // 1.5 s quiet
    }

    [Fact]
    public void Settle_WaitsOnAThinOrRestlessPage_ToTheLatestPoint()
    {
        var thin = new DomSettle();
        Assert.False(thin.Observe(1 * S, loaded: true, changed: true, gated: false, thin: true));
        Assert.False(thin.Observe(5.5 * S, loaded: true, changed: false, gated: false, thin: true));   // quiet for 4.5 s, still thin
        Assert.True(thin.Observe(6 * S, loaded: true, changed: false, gated: false, thin: true));      // 5 s after it loaded

        var ticker = new DomSettle();
        for (double t = 1; t < 6; t += 0.5)
        {
            Assert.False(ticker.Observe(t * S, loaded: true, changed: true, gated: false, thin: false));
        }

        Assert.True(ticker.Observe(6 * S, loaded: true, changed: true, gated: false, thin: false));
    }

    [Fact]
    public void Settle_WaitsOutABotCheck_ThenCountsThePageAfterAfresh()
    {
        var settle = new DomSettle();
        Assert.False(settle.Observe(1 * S, loaded: true, changed: true, gated: true, thin: false));
        Assert.False(settle.Observe(9 * S, loaded: true, changed: false, gated: true, thin: false));   // quiet, but a bot check
        Assert.False(settle.Observe(9.5 * S, loaded: true, changed: true, gated: false, thin: false)); // cleared: the page's own count starts
        Assert.False(settle.Observe(10.5 * S, loaded: true, changed: false, gated: false, thin: false));
        Assert.True(settle.Observe(11 * S, loaded: true, changed: false, gated: false, thin: false));

        var stuck = new DomSettle();
        Assert.False(stuck.Observe(1 * S, loaded: true, changed: true, gated: true, thin: false));
        Assert.True(stuck.Observe(1 * S + DomSettle.MostChallenge, loaded: true, changed: false, gated: true, thin: false));   // taken as it is; the fetcher says blocked
    }

    [Fact]
    public void LooksThin_IsScriptsAndNextToNoText()
    {
        Assert.True(DomSettle.LooksThin("<html><body><p>EARLY</p><script>x()</script></body></html>", "EARLY"));
        Assert.False(DomSettle.LooksThin("<html><body><p>EARLY</p></body></html>", "EARLY"));
        Assert.False(DomSettle.LooksThin("<html><body><script>x()</script></body></html>", new string('a', WebFetcher.ShellTextChars)));
    }

    [Fact]
    public void ParseActivePort_IsThePortThenTheBrowsersPath()
    {
        Assert.Equal(new Uri("ws://127.0.0.1:55031/devtools/browser/f674dbb2-3f6b"), DevToolsClient.ParseActivePort("55031\n/devtools/browser/f674dbb2-3f6b\n"));
        Assert.Equal(new Uri("ws://127.0.0.1:9222/devtools/browser/x"), DevToolsClient.ParseActivePort("9222\r\n/devtools/browser/x"));
        Assert.Null(DevToolsClient.ParseActivePort("55031\n"));                     // half written
        Assert.Null(DevToolsClient.ParseActivePort("0\n/devtools/browser/x"));
        Assert.Null(DevToolsClient.ParseActivePort("70000\n/devtools/browser/x"));
        Assert.Null(DevToolsClient.ParseActivePort("9222\n/devtools/page/x"));
    }
}
