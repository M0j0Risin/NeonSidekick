using NeonSidekick.App;
using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// The Mac's Browser window's decisions (2026-10-18, the Mac Browser plan): no test gets the main thread, so the window is proven by the smoke's
/// <c>browser:webkit</c> and the live runs, and everything with a decision in it is here — the jobs' lifecycle (<see cref="BrowserJobs"/>,
/// against the Windows window's behaviour), the keys (<see cref="MacKeys.BrowserAction"/>), <see cref="MacBrowserRules"/>, Safari's identity
/// and headers, the staged PDF's file, the wording.
/// </summary>
public class MacBrowserTests
{
    private static TaskCompletionSource<BotCheckResult> Done() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static BotCheckRequest Request(string url = "https://example.com/") => new(new Uri(url), BotCheckUse.Fetch, "403");

    // ── BrowserJobs ──────────────────────────────────────────────────────

    [Fact]
    public void Jobs_ACheckAlone_HidesAtItsEnd_AndClosesAfterTheLinger()
    {
        var jobs = new BrowserJobs();
        var (check, older) = jobs.BeginCheck(Request(), Done());

        Assert.Null(older);
        Assert.False(check.KeepOpen);
        Assert.Same(check, jobs.Check);
        Assert.True(BrowserJobs.EndCloses(check));
        Assert.True(BrowserJobs.CancelCloses(check));
        Assert.True(jobs.EndCheck(check, close: BrowserJobs.EndCloses(check)));
        Assert.Null(jobs.Check);
        Assert.True(check.Finishing);
        Assert.True(jobs.LingerCloses());
        Assert.Equal(TimeSpan.FromSeconds(3), BrowserJobs.Linger);
    }

    [Fact]
    public void Jobs_ACheckOverAVisit_KeepsTheWindowOpen()
    {
        var jobs = new BrowserJobs();
        Assert.Null(jobs.BeginVisit());
        var (check, _) = jobs.BeginCheck(Request(), Done());

        Assert.True(check.KeepOpen);
        Assert.False(BrowserJobs.EndCloses(check));
        Assert.False(BrowserJobs.CancelCloses(check));
        jobs.EndCheck(check, close: false);
        Assert.False(jobs.LingerCloses());   // the visit holds it
    }

    [Fact]
    public void Jobs_ANewerCheck_OrAVisit_GivesTheOlderUp()
    {
        var jobs = new BrowserJobs();
        var (first, _) = jobs.BeginCheck(Request("https://a.example/"), Done());
        var (second, older) = jobs.BeginCheck(Request("https://b.example/"), Done());

        Assert.Same(first, older);
        Assert.Same(second, jobs.Check);
        Assert.Same(second, jobs.BeginVisit());

        // Ending the older one leaves the newer in place.
        jobs.EndCheck(first, close: false);
        Assert.Same(second, jobs.Check);
    }

    [Fact]
    public void Jobs_ALingerAfterAnotherCheckCame_DoesNotClose()
    {
        var jobs = new BrowserJobs();
        var (check, _) = jobs.BeginCheck(Request(), Done());
        jobs.EndCheck(check, close: true);
        jobs.BeginCheck(Request(), Done());

        Assert.False(jobs.LingerCloses());
    }

    [Fact]
    public void Jobs_TheVisit_RecordsWebHostsOnce_InOrder_LeavingStagedPdfsAndSignInLinksOut()
    {
        var jobs = new BrowserJobs();
        jobs.Navigated("https://first.example/a");   // before the visit: nothing kept
        jobs.BeginVisit();
        jobs.Navigated("https://www.example.com/a");
        jobs.Navigated("https://WWW.example.com/b");
        jobs.Navigated("about:blank");
        jobs.Navigated("https://" + PdfView.HostName + "/0123456789abcdef/a.pdf");
        jobs.Navigated("http://other.example:8080/");
        jobs.Navigated(null);

        Assert.Equal(["www.example.com", "other.example"], jobs.HarvestHosts);
    }

    [Fact]
    public void Jobs_TheHarvest_IsAtMostFiftyHosts()
    {
        var jobs = new BrowserJobs();
        jobs.BeginVisit();
        for (int i = 0; i < 60; i++)
        {
            jobs.Navigated($"https://h{i}.example/");
        }

        Assert.Equal(BrowserJobs.MaxVisitedHosts, jobs.HarvestHosts.Count);
        Assert.Equal("h0.example", jobs.HarvestHosts[0]);
        Assert.Equal(50, BrowserJobs.MaxVisitedHosts);
    }

    [Fact]
    public void Jobs_Closing_HarvestsOnce_OnlyForAUserCloseOfAVisitThatWentSomewhere()
    {
        var none = new BrowserJobs();
        Assert.Equal(BrowserCloseStep.Close, none.Closing(handOver: true, viewAlive: true));

        var empty = new BrowserJobs();
        empty.BeginVisit();
        Assert.Equal(BrowserCloseStep.Close, empty.Closing(handOver: true, viewAlive: true));

        var quiet = new BrowserJobs();
        quiet.BeginVisit();
        quiet.Navigated("https://example.com/");
        Assert.Equal(BrowserCloseStep.Close, quiet.Closing(handOver: false, viewAlive: true));   // the app's exit, a profile switch
        Assert.True(quiet.Harvested);

        var visit = new BrowserJobs();
        visit.BeginVisit();
        visit.Navigated("https://example.com/");
        Assert.Equal(BrowserCloseStep.Harvest, visit.Closing(handOver: true, viewAlive: true));
        Assert.Equal(BrowserCloseStep.Close, visit.Closing(handOver: true, viewAlive: true));   // once
    }

    [Fact]
    public void Jobs_AFileDuringACheck_EndsItAsAFile_OnceAndOnlyThen()
    {
        var jobs = new BrowserJobs();
        Assert.False(jobs.FileDuringCheck());   // a /browser download is the window's own

        var (check, _) = jobs.BeginCheck(Request(), Done());
        Assert.True(jobs.FileDuringCheck());
        Assert.True(check.WasFile);
        Assert.True(check.Busy);

        check.Finishing = true;
        Assert.False(jobs.FileDuringCheck());
    }

    [Fact]
    public void Jobs_TheEndings_FollowTheWatch()
    {
        Assert.Equal(BotCheckEnd.Cleared, BrowserJobs.Outcome(BrowserWatchStep.Read));
        Assert.Equal(BotCheckEnd.Refused, BrowserJobs.Outcome(BrowserWatchStep.Refused));
        Assert.Null(BrowserJobs.Outcome(BrowserWatchStep.Wait));

        var asked = new Uri("https://example.com/x");
        Assert.Equal("https://www.example.com/y", BrowserJobs.FinalUrl("https://www.example.com/y", asked));
        Assert.Equal(asked.AbsoluteUri, BrowserJobs.FinalUrl("about:blank", asked));
        Assert.Equal(asked.AbsoluteUri, BrowserJobs.FinalUrl(null, asked));
        Assert.Equal(["example.com", "www.example.com"], BrowserJobs.CheckHosts(asked, "https://www.example.com/y"));
        Assert.Equal(["example.com", "example.com"], BrowserJobs.CheckHosts(asked, "about:blank"));
    }

    [Fact]
    public void Jobs_ACleared_Answer_CarriesThePage_ButNotForAFile()
    {
        var jobs = new BrowserJobs();
        var (check, _) = jobs.BeginCheck(new BotCheckRequest(new Uri("https://example.com/"), BotCheckUse.Pdf, "403", "/tmp/x.pdf"), Done());
        check.Watch.Navigated(true, 200);
        check.Watch.Read(TimeSpan.Zero, "<html><body><p>Through.</p></body></html>", new Uri("https://example.com/"));
        check.PdfWritten = true;
        var cookie = new BrowserCookie("cf_clearance", "v", ".example.com", "/", -1, true, true);
        var identity = BrowserIdentity.Safari("26.6");

        var cleared = BrowserJobs.Cleared(check, "https://example.com/", [cookie], identity);
        Assert.Equal(BotCheckEnd.Cleared, cleared.End);
        Assert.Contains("Through.", cleared.Html);
        Assert.Equal(200, cleared.Status);
        Assert.True(cleared.PdfWritten);
        Assert.Same(identity, cleared.Identity);
        Assert.Single(cleared.Cookies);

        check.WasFile = true;
        Assert.Null(BrowserJobs.Cleared(check, "https://example.com/", [], identity).Html);

        check.Watch.Navigated(false, 0);
        var refused = BrowserJobs.Refused(check, "https://example.com/");
        Assert.Equal(BotCheckEnd.Refused, refused.End);
        Assert.Equal(BrowserText.DidNotLoad, refused.Detail);
    }

    [Fact]
    public void Jobs_TheVisit_IsHandedOverWithItsHostsAndIdentity()
    {
        var jobs = new BrowserJobs();
        jobs.BeginVisit();
        jobs.Navigated("https://example.com/");
        var identity = BrowserIdentity.Safari("26.6");

        var visit = jobs.Visit([], identity);
        Assert.Equal(["example.com"], visit.Hosts);
        Assert.Same(identity, visit.Identity);
    }

    // ── The keys ─────────────────────────────────────────────────────────

    private const ulong Cmd = MacKeys.CommandFlag;
    private const ulong Shift = MacKeys.ShiftFlag;
    private const ulong Opt = MacKeys.OptionFlag;
    private const ulong Ctrl = MacKeys.ControlFlag;

    [Theory]
    [InlineData(13, Cmd, "w", MacBrowserAction.Close)]
    [InlineData(15, Cmd, "r", MacBrowserAction.Reload)]
    [InlineData(33, Cmd, "[", MacBrowserAction.Back)]
    [InlineData(30, Cmd, "]", MacBrowserAction.Forward)]
    [InlineData(123, Cmd, "", MacBrowserAction.Back)]
    [InlineData(124, Cmd, "", MacBrowserAction.Forward)]
    [InlineData(37, Cmd, "l", MacBrowserAction.Address)]
    [InlineData(4, Cmd | Shift, "H", MacBrowserAction.Home)]
    [InlineData(16, Cmd, "y", MacBrowserAction.History)]
    [InlineData(2, Cmd, "d", MacBrowserAction.SaveLink)]
    [InlineData(11, Cmd | Opt, "b", MacBrowserAction.Links)]
    [InlineData(31, Cmd | Shift, "O", MacBrowserAction.Links)]
    [InlineData(3, Cmd | Ctrl, "f", MacBrowserAction.ToggleFullScreen)]
    [InlineData(3, Cmd, "f", MacBrowserAction.Find)]
    [InlineData(5, Cmd, "g", MacBrowserAction.FindNext)]
    [InlineData(5, Cmd | Shift, "G", MacBrowserAction.FindPrevious)]
    [InlineData(35, Cmd, "p", MacBrowserAction.Print)]
    [InlineData(24, Cmd, "=", MacBrowserAction.ZoomIn)]
    [InlineData(24, Cmd | Shift, "+", MacBrowserAction.ZoomIn)]
    [InlineData(27, Cmd, "-", MacBrowserAction.ZoomOut)]
    [InlineData(29, Cmd, "0", MacBrowserAction.ZoomReset)]
    [InlineData(8, Cmd, "c", MacBrowserAction.Copy)]
    [InlineData(7, Cmd, "x", MacBrowserAction.Cut)]
    [InlineData(9, Cmd, "v", MacBrowserAction.Paste)]
    [InlineData(0, Cmd, "a", MacBrowserAction.SelectAll)]
    [InlineData(6, Cmd, "z", MacBrowserAction.Undo)]
    [InlineData(6, Cmd | Shift, "Z", MacBrowserAction.Redo)]
    [InlineData(0, 0, "a", MacBrowserAction.None)]
    [InlineData(8, Ctrl, "c", MacBrowserAction.None)]
    [InlineData(13, Cmd | Ctrl, "w", MacBrowserAction.None)]
    [InlineData(15, Cmd | Shift, "R", MacBrowserAction.None)]
    public void Keys_SafarisChords_ByTheirCharacters(int keyCode, ulong flags, string characters, MacBrowserAction expected) =>
        Assert.Equal(expected, MacKeys.BrowserAction((ushort)keyCode, flags, characters));

    [Fact]
    public void Keys_AreMatchedByCharacter_NotByKeyCode()
    {
        // A layout where ⌘[ and ⌘L sit on other keys: the character decides; a key that types something else on the ANSI codes does nothing.
        Assert.Equal(MacBrowserAction.Back, MacKeys.BrowserAction(42, Cmd, "["));
        Assert.Equal(MacBrowserAction.Address, MacKeys.BrowserAction(12, Cmd, "l"));
        Assert.Equal(MacBrowserAction.None, MacKeys.BrowserAction(33, Cmd, "ü"));
        Assert.Equal(MacBrowserAction.None, MacKeys.BrowserAction(37, Cmd, null));
    }

    [Fact]
    public void Keys_Esc_ClosesFind_ThenStops_ThenLeavesFullScreen_ElseIsThePages()
    {
        Assert.Equal(MacBrowserAction.CloseFind, MacKeys.BrowserAction(MacKeys.KeyEscape, 0, "\u001b", loading: true, fullScreen: true, finding: true));
        Assert.Equal(MacBrowserAction.Stop, MacKeys.BrowserAction(MacKeys.KeyEscape, 0, "\u001b", loading: true, fullScreen: true));
        Assert.Equal(MacBrowserAction.LeaveFullScreen, MacKeys.BrowserAction(MacKeys.KeyEscape, 0, "\u001b", fullScreen: true));
        Assert.Equal(MacBrowserAction.None, MacKeys.BrowserAction(MacKeys.KeyEscape, 0, "\u001b"));
        Assert.Equal(MacBrowserAction.Stop, MacKeys.BrowserAction(47, Cmd, ".", loading: true));
        Assert.Equal(MacBrowserAction.None, MacKeys.BrowserAction(47, Cmd, "."));
    }

    [Fact]
    public void Keys_TheSideButtons_GoBackAndForward()
    {
        Assert.Equal(MacBrowserAction.Back, MacKeys.BrowserButton(3));
        Assert.Equal(MacBrowserAction.Forward, MacKeys.BrowserButton(4));
        Assert.Equal(MacBrowserAction.None, MacKeys.BrowserButton(2));
        Assert.Equal(MacBrowserAction.None, MacKeys.BrowserButton(0));
    }

    // ── MacBrowserRules ──────────────────────────────────────────────────

    [Fact]
    public void Rules_TheBarsSymbols_FollowTheState()
    {
        var idle = BrowserBarState.Initial with { Source = "https://example.com/" };
        Assert.Equal("chevron.left", MacBrowserRules.Symbol(BrowserToolItem.Back, idle));
        Assert.Equal("chevron.right", MacBrowserRules.Symbol(BrowserToolItem.Forward, idle));
        Assert.Equal("arrow.clockwise", MacBrowserRules.Symbol(BrowserToolItem.Reload, idle));
        Assert.Equal("xmark", MacBrowserRules.Symbol(BrowserToolItem.Reload, idle with { Loading = true }));
        Assert.Equal("house", MacBrowserRules.Symbol(BrowserToolItem.Home, idle));
        Assert.Equal("clock", MacBrowserRules.Symbol(BrowserToolItem.History, idle));
        Assert.Equal("star", MacBrowserRules.Symbol(BrowserToolItem.Links, idle));
        Assert.Equal("star.fill", MacBrowserRules.Symbol(BrowserToolItem.Links, idle with { Saved = true }));
        Assert.Equal("lock.fill", MacBrowserRules.Symbol(BrowserToolItem.Padlock, idle));
        Assert.Equal("lock.open", MacBrowserRules.Symbol(BrowserToolItem.Padlock, idle with { Source = "http://example.com/" }));
        Assert.Equal("", MacBrowserRules.Symbol(BrowserToolItem.Padlock, idle with { Source = "about:blank" }));
        Assert.Equal("arrow.right.circle", MacBrowserRules.Symbol(BrowserToolItem.Go, idle));
        Assert.Equal("arrow.up.forward.app", MacBrowserRules.Symbol(BrowserToolItem.Open, idle));
        Assert.Equal("", MacBrowserRules.Symbol(BrowserToolItem.Address, idle));
        foreach (var item in BrowserBarLayout.Buttons)
        {
            Assert.NotEqual("", MacBrowserRules.Symbol(item, idle));
        }
    }

    [Fact]
    public void Rules_ACookie_KeepsItsScope_AndASessionsExpiry()
    {
        var domain = MacBrowserRules.Cookie("cf_clearance", "abc", ".example.com", "/", 1_800_000_000, session: false, secure: true, httpOnly: true)!;
        Assert.Equal(new BrowserCookie("cf_clearance", "abc", ".example.com", "/", 1_800_000_000, true, true), domain);
        Assert.False(domain.HostOnly);
        Assert.True(domain.AppliesTo("www.example.com"));

        var host = MacBrowserRules.Cookie("srv", null, "127.0.0.1", "", null, session: true, secure: false, httpOnly: true)!;
        Assert.True(host.HostOnly);
        Assert.Equal(-1, host.Expires);
        Assert.Equal("/", host.Path);
        Assert.Equal("", host.Value);
        Assert.Equal(-1, MacBrowserRules.Cookie("a", "b", "x.example", "/", 5, session: true, false, false)!.Expires);

        Assert.Null(MacBrowserRules.Cookie(null, "b", "x.example", "/", null, true, false, false));
        Assert.Null(MacBrowserRules.Cookie("a", "b", "", "/", null, true, false, false));
    }

    [Fact]
    public void Rules_ADownload_GetsAFreeSafeName()
    {
        const string Dl = "dl";
        string P(string name) => Path.Combine(Dl, name);   // the platform's separator: the PC runs this too
        var taken = new HashSet<string> { P("report.pdf"), P("report-2.pdf"), P("download") };
        bool Exists(string path) => taken.Contains(path);

        Assert.Equal(P("a.zip"), MacBrowserRules.DownloadPath(Dl, "a.zip", Exists));
        Assert.Equal(P("report-3.pdf"), MacBrowserRules.DownloadPath(Dl, "report.pdf", Exists));
        Assert.Equal(P("passwd"), MacBrowserRules.DownloadPath(Dl, "../../etc/passwd", Exists));
        Assert.Equal(P("x.txt"), MacBrowserRules.DownloadPath(Dl, "C:\\temp\\x.txt", Exists));
        Assert.Equal(P("a-b.txt"), MacBrowserRules.DownloadPath(Dl, "a:b.txt", Exists));
        Assert.Equal(P("profile"), MacBrowserRules.DownloadPath(Dl, ".profile", Exists));
        Assert.Equal(P("download-2"), MacBrowserRules.DownloadPath(Dl, null, Exists));
        Assert.Equal(P("download-2"), MacBrowserRules.DownloadPath(Dl, "  ", Exists));
        Assert.True(Path.GetFileName(MacBrowserRules.DownloadPath(Dl, new string('n', 300) + ".pdf", Exists)).Length <= 200);
        Assert.EndsWith(".pdf", MacBrowserRules.DownloadPath(Dl, new string('n', 300) + ".pdf", Exists), StringComparison.Ordinal);
    }

    [Fact]
    public void Rules_TheStoreId_IsMadeOnce_AndMovesWithTheFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-storeid-" + Guid.NewGuid().ToString("N"));
        try
        {
            string profile = Path.Combine(root, "work");
            var id = Guid.NewGuid();
            string made = MacBrowserRules.StoreId(profile, () => id)!;
            Assert.Equal(id.ToString("D").ToUpperInvariant(), made);
            Assert.Equal(made, MacBrowserRules.StoreId(profile, () => Guid.NewGuid()));   // kept

            // /profile rename moves the folder, and the id with it.
            string renamed = Path.Combine(root, "job");
            Directory.Move(profile, renamed);
            Assert.Equal(made, MacBrowserRules.StoreId(renamed, () => Guid.NewGuid()));
            Assert.Equal(made, MacBrowserRules.ReadStoreId(Path.Combine(renamed, MacBrowserRules.StoreIdFileName)));

            // A file holding no UUID is replaced.
            File.WriteAllText(Path.Combine(renamed, MacBrowserRules.StoreIdFileName), "garbage");
            Assert.Null(MacBrowserRules.ReadStoreId(Path.Combine(renamed, MacBrowserRules.StoreIdFileName)));
            var next = Guid.NewGuid();
            Assert.Equal(next.ToString("D").ToUpperInvariant(), MacBrowserRules.StoreId(renamed, () => next));
            Assert.Null(MacBrowserRules.ReadStoreId(Path.Combine(root, "none", MacBrowserRules.StoreIdFileName)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Rules_Zoom_WalksSafarisSteps()
    {
        Assert.Equal(1.15, MacBrowserRules.Zoom(1.0, 1));
        Assert.Equal(0.85, MacBrowserRules.Zoom(1.0, -1));
        Assert.Equal(1.0, MacBrowserRules.Zoom(2.5, 0));
        Assert.Equal(3.0, MacBrowserRules.Zoom(3.0, 1));
        Assert.Equal(0.5, MacBrowserRules.Zoom(0.5, -1));
        Assert.Equal(1.25, MacBrowserRules.Zoom(1.2, 1));   // between steps: the next one up
        Assert.Equal(1.15, MacBrowserRules.Zoom(1.2, -1));
        Assert.Equal(1.0, MacBrowserRules.Zoom(double.NaN, 1));
    }

    [Fact]
    public void Rules_TheSnapshotWidth_IsPixelsOverTheScale_NeverWiderThanTheView()
    {
        Assert.Equal(225, MacBrowserRules.SnapshotWidth(450, 1100, 2));   // Phase 0: 225 points drew 450 pixels on a Retina screen
        Assert.Equal(450, MacBrowserRules.SnapshotWidth(450, 1100, 1));
        Assert.Equal(800, MacBrowserRules.SnapshotWidth(4000, 800, 2));
        Assert.Equal(450, MacBrowserRules.SnapshotWidth(450, 1100, double.NaN));
        Assert.Equal(1, MacBrowserRules.SnapshotWidth(0, 0, 2));
    }

    [Fact]
    public void Rules_ALiveViewsPlace_Scroll_AndKeys()
    {
        Assert.Equal((550.0, 200.0), MacBrowserRules.PointIn(0.5, 0.25, 1100, 800));
        Assert.Equal((1100.0, 0.0), MacBrowserRules.PointIn(1.5, -1, 1100, 800));
        Assert.Equal(-300, MacBrowserRules.WheelDelta(300));
        Assert.Equal(-(int)BrowserRemote.MaxScroll, MacBrowserRules.WheelDelta(1e9));
        Assert.Equal(0, MacBrowserRules.WheelDelta(double.NaN));
        foreach (string key in BrowserRemote.Keys)
        {
            Assert.NotNull(MacBrowserRules.KeyOf(key));
        }

        Assert.Equal(((ushort)36, "\r"), MacBrowserRules.KeyOf("Enter"));
        Assert.Null(MacBrowserRules.KeyOf("Escape"));
    }

    [Fact]
    public void Rules_QuietFailures_AndWhoAsksForASignIn()
    {
        Assert.True(MacBrowserRules.IsQuietFailure("NSURLErrorDomain", -999));
        Assert.True(MacBrowserRules.IsQuietFailure("WebKitErrorDomain", 102));
        Assert.True(MacBrowserRules.IsQuietFailure("WebKitErrorDomain", 204));
        Assert.False(MacBrowserRules.IsQuietFailure("NSURLErrorDomain", -1003));
        Assert.False(MacBrowserRules.IsQuietFailure(null, 0));

        Assert.True(MacBrowserRules.AsksForSignIn("NSURLAuthenticationMethodHTTPBasic"));
        Assert.True(MacBrowserRules.AsksForSignIn("NSURLAuthenticationMethodHTTPDigest"));
        Assert.False(MacBrowserRules.AsksForSignIn("NSURLAuthenticationMethodNegotiate"));
        Assert.False(MacBrowserRules.AsksForSignIn("NSURLAuthenticationMethodServerTrust"));
        Assert.False(MacBrowserRules.AsksForSignIn("NSURLAuthenticationMethodClientCertificate"));
    }

    [Fact]
    public void Rules_TheStatusBubble_ShowsWebLinksOnly_TheMiddleCut()
    {
        Assert.Equal("https://example.com/a", MacBrowserRules.Hover(" https://example.com/a "));
        Assert.Equal("mailto:a@example.com", MacBrowserRules.Hover("mailto:a@example.com"));
        Assert.Equal("", MacBrowserRules.Hover("javascript:alert(1)"));
        Assert.Equal("", MacBrowserRules.Hover(""));
        string longest = MacBrowserRules.Hover("https://example.com/" + new string('x', 400));
        Assert.Equal(160, longest.Length);
        Assert.Contains('…', longest);
    }

    [Fact]
    public void Rules_TheDownloadMenu_RowsTheirActions_AndReadsTheCommandBack()
    {
        Assert.Equal(BrowserText.NoDownloads, Assert.Single(MacBrowserRules.DownloadMenu([])).Label);

        MacDownloadRow running = new(1, "/dl/a.zip", 0.456, MacDownloadState.Running);
        MacDownloadRow done = new(2, "/dl/b.pdf", 1, MacDownloadState.Finished);
        MacDownloadRow failed = new(3, "/dl/c.bin", 0.1, MacDownloadState.Failed);
        Assert.Equal("a.zip — 46%", MacBrowserRules.DownloadLabel(running));
        Assert.Equal("b.pdf", MacBrowserRules.DownloadLabel(done));
        Assert.Equal("c.bin — " + BrowserText.DownloadFailedWord, MacBrowserRules.DownloadLabel(failed));
        Assert.Equal("c.bin — " + BrowserText.DownloadCancelledWord, MacBrowserRules.DownloadLabel(failed with { State = MacDownloadState.Cancelled }));

        var menu = MacBrowserRules.DownloadMenu([running, done, failed]);
        Assert.Equal(["c.bin — failed", "b.pdf", "a.zip — 46%"], menu.Select(r => r.Label));   // newest first
        var cancel = Assert.Single(menu[2].Children!);
        Assert.Equal(BrowserText.DownloadCancel, cancel.Label);
        Assert.Equal((1, 2), MacBrowserRules.DownloadCommand(cancel.Command));
        Assert.Equal([BrowserText.DownloadOpen, BrowserText.DownloadShow], menu[1].Children!.Select(r => r.Label));
        Assert.Equal((2, 0), MacBrowserRules.DownloadCommand(menu[1].Children![0].Command));
        Assert.Equal((2, 1), MacBrowserRules.DownloadCommand(menu[1].Children![1].Command));
        Assert.False(menu[0].Children![0].Enabled);
        Assert.Null(MacBrowserRules.DownloadCommand(BrowserLinks.RowBase));
        Assert.True(MacBrowserRules.DownloadCommandBase > BrowserLinks.RowBase + 1000);
    }

    // ── Safari's identity and headers ───────────────────────────────────

    [Fact]
    public void Identity_Safari_ReadsItsVersion_AndClaimsSafarisUserAgent()
    {
        var safari = BrowserIdentity.Safari("26.6");
        Assert.Equal(BrowserBrand.Safari, safari.Brand);
        Assert.Equal((26, 6), (safari.Major, safari.Minor));
        Assert.Equal(BrowserPlatform.MacOS, safari.Platform);
        Assert.False(safari.IsChromium);
        Assert.Equal("Safari", safari.BrandName);
        Assert.Equal("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.6 Safari/605.1.15", safari.UserAgent);

        Assert.Equal((18, 0), (BrowserIdentity.Safari("18").Major, BrowserIdentity.Safari("18").Minor));
        Assert.Equal((17, 4), (BrowserIdentity.Safari("17.4.1").Major, BrowserIdentity.Safari("17.4.1").Minor));
        var fallback = BrowserIdentity.Safari(BrowserIdentity.SafariFallbackVersion);
        foreach (string? bad in new[] { null, "", "x", "1.2.3.4", "9.1", "26.x" })
        {
            Assert.Equal(fallback, BrowserIdentity.Safari(bad));
        }

        Assert.True(BrowserIdentity.Fallback.IsChromium);
        Assert.Equal(0, BrowserIdentity.Fallback.Minor);
    }

    [Fact]
    public void Headers_ForSafari_AreTheWindowsOwnSet_InItsOrder()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/page");
        BrowserHeaders.Apply(request, BrowserIdentity.Safari("26.6"), new Uri("https://example.com/"));

        var names = request.Headers.NonValidated.Select(h => h.Key).ToList();   // the validated enumerator would parse (and respace) the values
        Assert.Equal(BrowserHeaders.SafariOrder, names);
        Assert.Equal(BrowserHeaders.SafariAccept, request.Headers.NonValidated["Accept"].ToString());
        Assert.Equal("same-origin", request.Headers.GetValues("Sec-Fetch-Site").Single());
        Assert.Equal(BrowserHeaders.AcceptEncoding, request.Headers.NonValidated["Accept-Encoding"].ToString());
        Assert.Contains("Version/26.6 Safari/", request.Headers.NonValidated["User-Agent"].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(names, n => n.StartsWith("Sec-CH-UA", StringComparison.Ordinal) || n is "Upgrade-Insecure-Requests" or "Sec-Fetch-User" or "Priority");

        using var typed = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");
        BrowserHeaders.Apply(typed, BrowserIdentity.Safari("26.6"), accept: "application/pdf");
        Assert.Equal(BrowserHeaders.SafariOrder.Where(n => n != "Referer"), typed.Headers.NonValidated.Select(h => h.Key));
        Assert.Equal("application/pdf", typed.Headers.NonValidated["Accept"].ToString());
        Assert.Equal("none", typed.Headers.GetValues("Sec-Fetch-Site").Single());
    }

    [MacFact]
    public void Identity_ThisMacsSafari_ReadsThroughNSBundle()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            return;
        }

        // A Mac with Safari (every Mac): its version, as the window claims it.
        string? version = MacBrowserWindows.SafariVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
        var identity = BrowserIdentity.Safari(version);
        Assert.StartsWith(version!.Split('.')[0] + ".", identity.UserAgent.Split("Version/")[1], StringComparison.Ordinal);
    }

    // ── The staged PDF, the wording ──────────────────────────────────────

    [Fact]
    public void PdfView_StagedFileOf_NamesTheCopy_UntilItIsCleared()
    {
        string root = Path.Combine(Path.GetTempPath(), "ns-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "my report.pdf");
            File.WriteAllText(original, "%PDF-1.4");
            string folder = Path.Combine(root, "stage");
            var url = PdfView.Stage(original, folder);

            string staged = PdfView.StagedFileOf(url.AbsoluteUri)!;
            Assert.StartsWith(folder, staged, StringComparison.Ordinal);
            Assert.Equal("my report.pdf", Path.GetFileName(staged));
            Assert.Equal("%PDF-1.4", File.ReadAllText(staged));
            Assert.Null(PdfView.StagedFileOf("https://example.com/x.pdf"));
            Assert.Null(PdfView.StagedFileOf("https://" + PdfView.HostName + "/ffffffffffffffff/x.pdf"));

            PdfView.Clear(folder);
            Assert.Null(PdfView.StagedFileOf(url.AbsoluteUri));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Text_TheErrorPage_EscapesWhatThePageSent()
    {
        string page = BrowserText.ErrorPage("https://example.com/<script>", "A server with the specified hostname could not be found.");
        Assert.Contains("&lt;script&gt;", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", page, StringComparison.Ordinal);
        Assert.Contains(BrowserText.ErrorTitle, page, StringComparison.Ordinal);
        Assert.Equal("A server could not be found. Reload (⌘R) to try again.", BrowserText.ErrorLine("A server could not be found."));
        Assert.Equal("https://example.com says:", BrowserText.DialogFrom("https://example.com"));
        Assert.Equal("The page says:", BrowserText.DialogFrom(null));
        Assert.Equal("Sign in to intranet.example (Staff)", BrowserText.SignInMessage("intranet.example", " Staff "));
        Assert.Equal("Sign in to intranet.example", BrowserText.SignInMessage("intranet.example", null));
    }

    [Fact]
    public void Command_TheWords_NameTheWindow_WhereThereIsOne()
    {
        var before = BrowserCommand.WindowHere;
        try
        {
            BrowserCommand.WindowHere = () => true;
            Assert.Contains("Browser window", BrowserCommand.Words.Single(w => w.Word == BrowserCommand.ListWord).Note, StringComparison.Ordinal);
            BrowserCommand.WindowHere = () => false;
            Assert.Contains("your browser", BrowserCommand.Words.Single(w => w.Word == BrowserCommand.ListWord).Note, StringComparison.Ordinal);
        }
        finally
        {
            BrowserCommand.WindowHere = before;
        }
    }

    [Fact]
    public void Help_OnAMac_TheBrowserFormsAreTheWindows_WithSafarisKeys()
    {
        Assert.False(NeonSidekick.Help.HelpMac.Forms.ContainsKey("/browser <n>"));
        Assert.False(NeonSidekick.Help.HelpMac.Forms.ContainsKey("/browser list"));
        string browse = NeonSidekick.Help.HelpMac.Forms["/browser [<url>]"];
        Assert.Contains("⌘L", browse, StringComparison.Ordinal);
        Assert.Contains("macOS 14", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("Ctrl+", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", NeonSidekick.Help.HelpMac.Forms["/browser clear history|all"], StringComparison.Ordinal);
        Assert.Contains("Browser window", NeonSidekick.Help.HelpMac.Settings[NeonSidekick.App.SettingsField.ThemedExternalWindows], StringComparison.Ordinal);
    }

    [Fact]
    public void MacKeyEvent_CarriesItsCharacters_ByInit()
    {
        var key = new MacKeyEvent(33, MacKeys.CommandFlag, false) { Characters = "[" };
        Assert.Equal("[", key.Characters);
        Assert.Equal("", new MacKeyEvent(0, 0, false).Characters);
    }
}
