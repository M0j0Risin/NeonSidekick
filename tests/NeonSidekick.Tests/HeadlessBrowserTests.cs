using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class HeadlessBrowserTests
{
    private static readonly IReadOnlyList<string> Windows = HeadlessBrowser.WindowsCandidates(@"C:\Program Files", @"C:\Program Files (x86)", @"C:\Users\me\AppData\Local");

    [WindowsFact]
    public void WindowsCandidates_AreEdgeChromeBrave_UnderTheThreeRoots()
    {
        Assert.Equal(
            new[]
            {
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Users\me\AppData\Local\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Users\me\AppData\Local\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
                @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe",
                @"C:\Users\me\AppData\Local\BraveSoftware\Brave-Browser\Application\brave.exe",
            },
            Windows);
    }

    [WindowsFact]
    public void WindowsCandidates_SkipAnEmptyOrRepeatedRoot()
    {
        var list = HeadlessBrowser.WindowsCandidates(@"C:\Program Files", @"C:\Program Files", "");

        Assert.Equal(3, list.Count);
        Assert.All(list, p => Assert.StartsWith(@"C:\Program Files\", p));
    }

    /// <summary>
    /// The page a read attaches to is its own, never one the browser opened for itself (2026-10-19, the CI-like runs: a fresh Edge profile's
    /// <c>edge://sync-confirmation-dialog/</c>, listed first, took pdf.js's import and refused it).
    /// </summary>
    [Theory]
    [InlineData("about:blank", true)]
    [InlineData("https://example.com/page", true)]
    [InlineData("data:text/html,<p>hi</p>", true)]
    [InlineData("edge://sync-confirmation-dialog/", false)]
    [InlineData("EDGE://welcome", false)]
    [InlineData("chrome://newtab/", false)]
    [InlineData("brave://welcome/", false)]
    [InlineData("devtools://devtools/bundled/inspector.html", false)]
    [InlineData("chrome-extension://abc/page.html", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOwnPage_TakesNoPageTheBrowserOpenedForItself(string? url, bool own)
    {
        Assert.Equal(own, HeadlessBrowser.IsOwnPage(url));
    }

    [Fact]
    public void Locate_TakesTheConfiguredPath_WhenItExists()
    {
        Assert.Equal(@"D:\tools\chrome.exe", HeadlessBrowser.Locate(@" D:\tools\chrome.exe ", Windows, p => p == @"D:\tools\chrome.exe"));
        Assert.Null(HeadlessBrowser.Locate(@"D:\tools\chrome.exe", Windows, _ => false));
        // A configured path that is not there never falls back to the candidates: the row says what the user asked for.
        Assert.Null(HeadlessBrowser.Locate(@"D:\tools\chrome.exe", Windows, p => p == Windows[0]));
    }

    [Fact]
    public void Locate_FindsTheFirstCandidate_EdgeBeforeChromeBeforeBrave()
    {
        Assert.Equal(Windows[1], HeadlessBrowser.Locate("", Windows, p => p == Windows[1] || p == Windows[3]));
        Assert.Equal(Windows[3], HeadlessBrowser.Locate("", Windows, p => p == Windows[3] || p == Windows[6]));
        Assert.Null(HeadlessBrowser.Locate("", Windows, _ => false));
    }

    [Fact]
    public void Candidates_OnThisMachine_AreNonEmpty()
    {
        Assert.NotEmpty(HeadlessBrowser.Candidates());
        Assert.EndsWith(Path.Combine("NeonSidekick", "browser"), HeadlessBrowser.UserDataDirectory);
    }

    [Fact]
    public void Arguments_ArePinned()
    {
        Assert.Equal(
            new[]
            {
                "--headless=new", "--remote-debugging-port=0", "--disable-blink-features=AutomationControlled", "--no-first-run",
                "--no-default-browser-check", "--disable-extensions", "--disable-background-networking", "--mute-audio",
                "--edge-skip-compat-layer-relaunch", "--disable-features=msImplicitSignin", "--blink-settings=imagesEnabled=false", "--window-size=1920,1080", "--screen-info={1920x1080}",
                "--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0",
                @"--user-data-dir=C:\Temp\NeonSidekick\browser", "https://example.com/a?b=c",
            },
            HeadlessBrowser.Arguments(new Uri("https://example.com/a?b=c"), @"C:\Temp\NeonSidekick\browser", new BrowserIdentity(BrowserBrand.Edge, 155, BrowserPlatform.Windows)));
        Assert.Equal(TimeSpan.FromSeconds(30), HeadlessBrowser.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(3), HeadlessBrowser.CloseGrace);
    }

    /// <summary>
    /// A profile folder another browser holds (2026-10-11: a relaunched Edge outlived the app and held it, every later run
    /// exiting with code 21): its <c>lockfile</c> open unshared is held; the file free, or none, is not.
    /// </summary>
    [WindowsFact]
    public void ProfileInUse_IsTheLockFileHeldOpen()
    {
        string folder = Path.Combine(Path.GetTempPath(), "NeonSidekick.tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.False(HeadlessBrowser.ProfileInUse(folder));   // no lock file
            string lockFile = Path.Combine(folder, HeadlessBrowser.LockFileName);
            using (new FileStream(lockFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.True(HeadlessBrowser.ProfileInUse(folder));
            }

            Assert.False(HeadlessBrowser.ProfileInUse(folder));   // left behind, no longer held
            Assert.False(HeadlessBrowser.ProfileInUse(Path.Combine(folder, "missing")));

            // Code review, later that day: a scanner or an indexer reading it with everything shared is no browser.
            using (new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.False(HeadlessBrowser.ProfileInUse(folder));
            }

            // A holder that writes and lets others only read (Chromium's ProcessSingleton) is.
            using (new FileStream(lockFile, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                Assert.True(HeadlessBrowser.ProfileInUse(folder));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The macOS lock (code review, 2026-10-11): <c>SingletonLock</c> points at <c>host-pid</c>, a host name with dashes too.</summary>
    [Theory]
    [InlineData("MacBook-Pro.local-4242", 4242)]
    [InlineData("host-1", 1)]
    [InlineData("host", null)]
    [InlineData("-42", null)]
    [InlineData("host-", null)]
    [InlineData("host-0", null)]
    [InlineData("host-+5", null)]
    public void SingletonPid_IsTheNumberAfterTheLastDash(string target, int? pid)
    {
        Assert.Equal(pid, HeadlessBrowser.SingletonPid(target));
    }

    /// <summary>Off Windows the held folder is a <c>SingletonLock</c> naming a running process: this one; a dead one is not held.</summary>
    [UnixFact]
    public void ProfileInUse_OffWindows_IsTheSingletonLocksBrowserRunningHere()
    {
        string folder = Path.Combine(Path.GetTempPath(), "NeonSidekick.tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string link = Path.Combine(folder, HeadlessBrowser.SingletonLockName);
            string here = Environment.MachineName + "-";
            string me = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(HeadlessBrowser.ProfileInUse(folder));
            File.CreateSymbolicLink(link, here + me);
            Assert.True(HeadlessBrowser.SingletonHeld(folder, _ => true));   // this process, running, taken for a browser
            Assert.False(HeadlessBrowser.ProfileInUse(folder));              // code review, later on 2026-10-11: but it is no browser
            File.Delete(link);
            File.CreateSymbolicLink(link, "elsewhere-" + me);
            Assert.False(HeadlessBrowser.SingletonHeld(folder, _ => true));  // another machine's lock
            File.Delete(link);
            File.CreateSymbolicLink(link, here + int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(HeadlessBrowser.SingletonHeld(folder, _ => true));  // nothing running by that id
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The lock's host against .NET's machine name (code review, later on 2026-10-11): first labels, case aside.</summary>
    [Theory]
    [InlineData("MacBook-Pro.local-4242", "MacBook-Pro", true)]
    [InlineData("macbook-pro.local-4242", "MACBOOK-PRO", true)]
    [InlineData("MacBook-Pro-4242", "MacBook-Pro", true)]
    [InlineData("other.local-4242", "MacBook-Pro", false)]
    [InlineData("4242", "MacBook-Pro", false)]
    [InlineData("host-1", "", false)]
    public void SingletonOnHost_ComparesTheFirstLabels(string target, string machine, bool here)
    {
        Assert.Equal(here, HeadlessBrowser.SingletonOnHost(target, machine));
    }

    [Theory]
    [InlineData("Microsoft Edge", true)]
    [InlineData("msedge", true)]
    [InlineData("Google Chrome", true)]
    [InlineData("chrome", true)]
    [InlineData("Brave Browser", true)]
    [InlineData("Chromium", true)]
    [InlineData("testhost", false)]
    [InlineData("zsh", false)]
    // Code review, later on 2026-10-11: the whole name, so a helper or another program with "edge" or "chrome" in it is no browser.
    [InlineData("Google Chrome Helper", false)]
    [InlineData("Google Chrome Helper (Renderer)", false)]
    [InlineData("chrome_crashpad_handler", false)]
    [InlineData("Microsoft Edge Helper", false)]
    [InlineData("KnowledgeBase", false)]
    [InlineData("msedgewebview2", false)]
    public void IsBrowserName_IsAChromiumBrowsers(string name, bool browser)
    {
        Assert.Equal(browser, HeadlessBrowser.IsBrowserName(name));
    }

    /// <summary>The browser the app would start counts by its file name, whatever it is (code review, later on 2026-10-11: a configured Canary or Vivaldi went unseen).</summary>
    [Fact]
    public void IsBrowserName_TheConfiguredExecutable_Counts()
    {
        string canary = "/Applications/Google Chrome Canary.app/Contents/MacOS/Google Chrome Canary";
        Assert.False(HeadlessBrowser.IsBrowserName("Google Chrome Canary"));
        Assert.True(HeadlessBrowser.IsBrowserName("Google Chrome Canary", canary));
        Assert.True(HeadlessBrowser.IsBrowserName("vivaldi", "/usr/bin/vivaldi"));
        Assert.False(HeadlessBrowser.IsBrowserName("Google Chrome Canary Helper", canary));   // its helpers still are not
        Assert.False(HeadlessBrowser.IsBrowserName("", ""));
    }

    /// <summary>One look gives no second chance to a holder that lets go (code review, later on 2026-10-11: the smoke's polling loop counts its own time).</summary>
    [WindowsFact]
    public void ProfileInUse_LooksAsOftenAsAsked()
    {
        string folder = Path.Combine(Path.GetTempPath(), "NeonSidekick.tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string lockFile = Path.Combine(folder, HeadlessBrowser.LockFileName);
            File.WriteAllText(lockFile, "");
            using (new FileStream(lockFile, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                var one = System.Diagnostics.Stopwatch.StartNew();
                Assert.True(HeadlessBrowser.ProfileInUse(folder, looks: 1));
                Assert.True(one.ElapsedMilliseconds < 50, $"one look took {one.ElapsedMilliseconds} ms");
                Assert.True(HeadlessBrowser.ProfileInUseAsync(folder, executable: null, CancellationToken.None).GetAwaiter().GetResult());
            }

            Assert.False(HeadlessBrowser.ProfileInUseAsync(folder, executable: null, CancellationToken.None).GetAwaiter().GetResult());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void BrowserProfileInUse_NamesTheFolder_AndWhatToDo()
    {
        string text = WebText.BrowserProfileInUse(@"C:\T\browser");
        Assert.Contains(@"C:\T\browser", text);
        Assert.Contains("another browser", text);
    }

    [Fact]
    public void LastLine_IsTheLastNonEmptyLine()
    {
        Assert.Equal("[1234:5678:0915/120000.123:ERROR:x.cc(1)] boom", HeadlessBrowser.LastLine("first\r\n\r\n[1234:5678:0915/120000.123:ERROR:x.cc(1)] boom\r\n\r\n"));
        Assert.Equal("", HeadlessBrowser.LastLine("\n\n"));
    }

    [Fact]
    public void WholePdf_NeedsTheHeaderAndTheEndMarker()
    {
        string path = Path.Combine(Path.GetTempPath(), "neon-wholepdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            Assert.Null(HeadlessBrowser.WholePdf(path));   // missing
            File.WriteAllText(path, "%PDF-1.4\n1 0 obj\n");
            Assert.Null(HeadlessBrowser.WholePdf(path));   // still being written
            File.AppendAllText(path, "trailer\n%%EOF\n");
            Assert.Equal(new FileInfo(path).Length, HeadlessBrowser.WholePdf(path));
            File.WriteAllText(path, "<html>not a pdf %%EOF</html>");
            Assert.Null(HeadlessBrowser.WholePdf(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
