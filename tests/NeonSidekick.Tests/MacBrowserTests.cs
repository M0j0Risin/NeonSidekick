using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
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

    // ── BrowserCheckWatch (2026-10-20, the code review's G8 and G1: both windows' watch, shared) ──

    private const string APrint = "{\"s\":\"complete\",\"n\":42,\"x\":7,\"st\":200,\"u\":\"https://example.com/\",\"o\":1}";
    private const string APage = "{\"h\":\"<html><body><p>Through.</p></body></html>\",\"o\":1}";

    // A look at document 1 of example.com (2026-10-26, the watch keeps its state per document).
    private static BrowserLook Look(int status = 200, string url = "https://example.com/", double document = 1) =>
        new("complete", (42, 7u), status, url, document);

    // A check whose window went to its address and shows document 1 with this status.
    private static BrowserCheck LoadedCheck(BotCheckRequest? request = null, bool keepOpen = false, int status = 200)
    {
        var check = new BrowserCheck(request ?? Request(), Done(), keepOpen);
        check.GoNow();
        check.Watch.WantsRead(TimeSpan.Zero, Look(status), status, errorPage: false);
        return check;
    }

    // Document 1's DOM read.
    private static void ReadPage(BrowserCheck check, string html) =>
        Assert.True(check.Watch.Read(TimeSpan.Zero, html, new Uri("https://example.com/"), 1));

    [Fact]
    public void Watch_AFileDuringACheck_EndsItAsAFile_OnceAndOnlyThen()
    {
        var check = LoadedCheck();
        var page = new FakeCheckPage();

        Assert.True(BrowserCheckWatch.EndAsFile(page, check));
        Assert.True(check.WasFile);
        Assert.True(check.Busy);
        Assert.True(check.Finishing);
        Assert.False(BrowserCheckWatch.EndAsFile(page, check));   // a second download, or the answer that is no page after it
        var read = Assert.Single(page.CookieReads);
        Assert.Empty(page.Prints);   // a file is no page to print

        read.Answer([new BrowserCookie("cf_clearance", "v", ".example.com", "/", -1, true, true)]);
        var (result, close) = Assert.Single(page.Finishes);
        Assert.Equal(BotCheckEnd.Cleared, result.End);
        Assert.True(result.WasFile);
        Assert.Null(result.Html);
        Assert.Single(result.Cookies);
        Assert.True(close);
    }

    [Fact]
    public void Watch_ALookInFlight_AsTheCheckEndsAsAFile_IsDropped()
    {
        // G1: the look's answer came back after a download had ended the check as a file, and concluded it again (refused, or through twice).
        var check = LoadedCheck();
        var page = new FakeCheckPage();
        BrowserCheckWatch.Tick(page, check);
        var look = Assert.Single(page.Evaluations);
        Assert.Equal(HeadlessBrowser.PrintExpression, look.Expression);

        BrowserCheckWatch.EndAsFile(page, check);
        look.Answer(APrint);

        Assert.Single(page.Evaluations);   // no DOM read after it
        Assert.Single(page.CookieReads);
        page.CookieReads[0].Answer([]);
        Assert.True(Assert.Single(page.Finishes).Result.WasFile);
    }

    [Fact]
    public void Watch_ADomReadInFlight_AsTheCheckEnds_IsNotTaken()
    {
        var check = LoadedCheck();
        var page = new FakeCheckPage();
        BrowserCheckWatch.Tick(page, check);
        page.Evaluations[0].Answer(APrint);   // the first look wants the DOM
        var read = page.Evaluations[1];
        Assert.Equal(HeadlessBrowser.PageExpression, read.Expression);

        BrowserCheckWatch.EndAsFile(page, check);
        read.Answer(APage);

        Assert.Null(check.Watch.Html);   // the late read was never judged
        Assert.Equal(2, page.Evaluations.Count);
        Assert.Empty(page.Finishes);
        page.CookieReads[0].Answer([]);
        Assert.Single(page.Finishes);
    }

    [Fact]
    public void Watch_ALookForACheckNoLongerTheWindows_IsDropped()
    {
        var check = LoadedCheck();
        var page = new FakeCheckPage(check);
        BrowserCheckWatch.Tick(page, check);
        page.Holding = LoadedCheck();   // a newer check took the window

        page.Evaluations[0].Answer(APrint);
        Assert.Single(page.Evaluations);
        Assert.True(check.Busy);   // never looked at again: the window gave it up
    }

    [Fact]
    public void Watch_NoLook_BeforeTheWindowWent_WhileOneIsOnItsWay_OrWithNothingToSendItTo()
    {
        var page = new FakeCheckPage();
        var unloaded = new BrowserCheck(Request(), Done(), keepOpen: false);
        BrowserCheckWatch.Tick(page, unloaded);
        Assert.Empty(page.Evaluations);   // not gone yet (Start): no look, and no go before a poll's time

        var check = LoadedCheck();
        BrowserCheckWatch.Tick(page, check);
        BrowserCheckWatch.Tick(page, check);
        Assert.Single(page.Evaluations);   // one look at a time

        page.Evaluations[0].Answer(null);   // mid-navigation
        Assert.False(check.Busy);

        var blind = new FakeCheckPage { CanEvaluate = false };
        var other = LoadedCheck();
        BrowserCheckWatch.Tick(blind, other);
        Assert.False(other.Busy);   // nothing sent: the next tick tries again
    }

    // 2026-10-26, the redesign: with no wait for loads, a reused window's page from before could answer the first looks and be read.
    [Fact]
    public void Start_LooksAtThePageFromBefore_ThenGoes_AndThatPageIsNeverJudged()
    {
        var page = new FakeCheckPage();
        var check = new BrowserCheck(Request(), Done(), keepOpen: false);
        int went = 0;
        BrowserCheckWatch.Start(page, check, () => went++);
        var first = Assert.Single(page.Evaluations);
        Assert.Equal(HeadlessBrowser.PrintExpression, first.Expression);
        Assert.Equal(0, went);   // not before the look answers
        BrowserCheckWatch.Tick(page, check);
        Assert.Single(page.Evaluations);   // no look of the tick's while the first is out

        first.Answer("{\"s\":\"complete\",\"n\":42,\"x\":7,\"st\":200,\"u\":\"https://news.example.org/\",\"o\":5}");
        Assert.Equal(1, went);
        Assert.True(check.Gone);
        Assert.Equal(5, check.Watch.Before);

        Assert.False(check.Watch.WantsRead(TimeSpan.Zero, Look(document: 5), 200, errorPage: false));
        Assert.False(check.Watch.Observe(TimeSpan.FromMinutes(1), Look(document: 5), 200, errorPage: false));
        Assert.Equal(BrowserWatchStep.Wait, check.Watch.Decide());
    }

    [Fact]
    public void Start_GoesAtOnce_WithNothingToAsk_OrNoDocument_AndAfterAPoll_ForAPageThatDoesNotAnswer()
    {
        var blind = new FakeCheckPage { CanEvaluate = false };
        var check = new BrowserCheck(Request(), Done(), keepOpen: false);
        int went = 0;
        BrowserCheckWatch.Start(blind, check, () => went++);
        Assert.Equal(1, went);   // no view yet: nothing showed before
        Assert.Null(check.Watch.Before);

        var page = new FakeCheckPage();
        var fresh = new BrowserCheck(Request(), Done(), keepOpen: false);
        went = 0;
        BrowserCheckWatch.Start(page, fresh, () => went++);
        page.Evaluations[0].Answer(null);   // a fresh window, no document
        Assert.Equal(1, went);
        Assert.Null(fresh.Watch.Before);

        var hung = new BrowserCheck(Request(), Done(), keepOpen: false);
        var stuck = new FakeCheckPage();
        went = 0;
        BrowserCheckWatch.Start(stuck, hung, () => went++);
        while (hung.Clock.Elapsed < DomSettle.PollInterval)
        {
            BrowserCheckWatch.Tick(stuck, hung);
            Assert.Equal(0, went);
            Thread.Sleep(20);
        }

        BrowserCheckWatch.Tick(stuck, hung);
        Assert.Equal(1, went);
        stuck.Evaluations[0].Answer(APrint);   // late: it may be the new document's, so it marks nothing
        Assert.Null(hung.Watch.Before);
        Assert.Equal(1, went);
    }

    [Fact]
    public void Start_TheSamePageButForTheFragment_IsNoPageFromBefore()
    {
        var page = new FakeCheckPage();
        var check = new BrowserCheck(Request("https://example.com/a#part"), Done(), keepOpen: false);
        BrowserCheckWatch.Start(page, check, () => { });
        page.Evaluations[0].Answer("{\"s\":\"complete\",\"n\":42,\"x\":7,\"st\":200,\"u\":\"https://example.com/a\",\"o\":5}");
        Assert.Null(check.Watch.Before);   // the window only scrolls: the same document is the page

        Assert.True(BrowserCheckWatch.SameDocumentTarget("https://example.com/a#x", new Uri("https://example.com/a#part")));
        Assert.False(BrowserCheckWatch.SameDocumentTarget("https://example.com/a", new Uri("https://example.com/a")));   // no fragment: a new document
        Assert.False(BrowserCheckWatch.SameDocumentTarget("https://example.com/b", new Uri("https://example.com/a#part")));
    }

    [Fact]
    public void Watch_AReadFromTheNextDocument_IsDropped_AndTheWindowSaysWhatThePageIs()
    {
        var check = LoadedCheck();
        var page = new FakeCheckPage { Describing = look => (404, look.Url.StartsWith("chrome-error:", StringComparison.Ordinal)) };
        BrowserCheckWatch.Tick(page, check);
        page.Evaluations[0].Answer("{\"s\":\"complete\",\"n\":42,\"x\":8,\"st\":200,\"u\":\"https://example.com/\",\"o\":1}");
        var read = page.Evaluations[1];
        Assert.Equal(HeadlessBrowser.PageExpression, read.Expression);
        read.Answer("{\"h\":\"<html><body><p>Next.</p></body></html>\",\"o\":2}");   // the next document came in between
        Assert.Null(check.Watch.Html);
        Assert.Equal(200, check.Watch.Status);   // document 1's, taken when it was first seen

        var fresh = new BrowserCheck(Request(), Done(), keepOpen: false);
        fresh.GoNow();
        BrowserCheckWatch.Tick(page, fresh);
        page.Evaluations[2].Answer("{\"s\":\"complete\",\"n\":42,\"x\":8,\"st\":0,\"u\":\"chrome-error://chromewebdata/\",\"o\":3}");
        Assert.Equal(404, fresh.Watch.Status);   // the window's say, not the look's
        Assert.True(fresh.Watch.LoadFailed);
    }

    [Fact]
    public void Watch_AnErrorPage_IsRefusedWhereItEnded()
    {
        var check = LoadedCheck(status: 404);
        ReadPage(check, "<html><body><p>Not here.</p></body></html>");
        var page = new FakeCheckPage { Source = "https://example.com/gone" };

        BrowserCheckWatch.Conclude(page, check);

        var (result, close) = Assert.Single(page.Finishes);
        Assert.Equal(BotCheckEnd.Refused, result.End);
        Assert.Equal("https://example.com/gone", result.FinalUrl);
        Assert.Equal(404, result.Status);
        Assert.True(close);
        Assert.Empty(page.CookieReads);
    }

    [Fact]
    public void Watch_Through_PrintsThenReadsTheCookies_ThenEndsOnce()
    {
        var check = LoadedCheck(new BotCheckRequest(new Uri("https://example.com/"), BotCheckUse.Pdf, "403", "/tmp/x.pdf"), keepOpen: true);
        ReadPage(check, "<html><body><p>Through.</p></body></html>");
        var page = new FakeCheckPage { Source = "https://www.example.com/landed" };

        BrowserCheckWatch.Conclude(page, check);
        BrowserCheckWatch.Conclude(page, check);   // a second say while the first is on its way: nothing more

        var print = Assert.Single(page.Prints);
        Assert.Equal("/tmp/x.pdf", print.Path);
        Assert.Empty(page.CookieReads);
        print.Written(true);

        var read = Assert.Single(page.CookieReads);
        Assert.Equal(["example.com", "www.example.com"], read.Hosts);
        read.Answer([]);
        read.Answer([]);   // a store that answers twice

        var (result, close) = Assert.Single(page.Finishes);
        Assert.Equal(BotCheckEnd.Cleared, result.End);
        Assert.True(result.PdfWritten);
        Assert.Equal("https://www.example.com/landed", result.FinalUrl);
        Assert.Contains("Through.", result.Html);
        Assert.False(close);   // a visit holds the window
    }

    [Fact]
    public void Watch_APrintOrCookiesAfterTheCheckEnded_DoNothing()
    {
        var check = LoadedCheck(new BotCheckRequest(new Uri("https://example.com/"), BotCheckUse.Pdf, "403", "/tmp/x.pdf"));
        ReadPage(check, "<html><body><p>Through.</p></body></html>");
        var page = new FakeCheckPage();
        BrowserCheckWatch.Conclude(page, check);

        page.Finish(check, BotCheckResult.Ended(BotCheckEnd.GaveUp, "https://example.com/"), close: false);   // the user gave it up
        page.Prints[0].Written(true);

        Assert.Empty(page.CookieReads);
        Assert.Equal(BotCheckEnd.GaveUp, Assert.Single(page.Finishes).Result.End);
        Assert.False(check.PdfWritten);
    }

    [Fact]
    public void Watch_NoPrint_GoesStraightToTheCookies()
    {
        var check = LoadedCheck(new BotCheckRequest(new Uri("https://example.com/"), BotCheckUse.Pdf, "403", "/tmp/x.pdf"));
        ReadPage(check, "<html><body><p>Through.</p></body></html>");
        var page = new FakeCheckPage { CanPrint = false };   // a sheet up on the Mac, no view on Windows

        BrowserCheckWatch.Conclude(page, check);
        Assert.Single(page.CookieReads).Answer([]);

        Assert.False(Assert.Single(page.Finishes).Result.PdfWritten);
    }

    [Fact]
    public void Check_EndsOnce()
    {
        var check = new BrowserCheck(Request(), Done(), keepOpen: false);
        Assert.True(check.TryEnd());
        Assert.True(check.Ended);
        Assert.True(check.Finishing);
        Assert.False(check.TryEnd());
    }

    [Fact]
    public void CloseClaim_GoesToWhoeverTakesItFirst()
    {
        // E4: a close the caller stopped waiting for is called off; one the window's thread took counts.
        var late = new BrowserCloseClaim();
        Assert.True(late.TakeForCaller());
        Assert.False(late.TakeForWork());

        var prompt = new BrowserCloseClaim();
        Assert.True(prompt.TakeForWork());
        Assert.False(prompt.TakeForCaller());
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
        check.Watch.WantsRead(TimeSpan.Zero, Look(), 200, errorPage: false);
        ReadPage(check, "<html><body><p>Through.</p></body></html>");
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

        check.Watch.WantsRead(TimeSpan.Zero, Look(0, "chrome-error://chromewebdata/", 2), 0, errorPage: true);   // the window's error page
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
    public void Rules_AProfileIsKept_WhileItsStoreIsStillThere()
    {
        // C5 (2026-10-20, the code review): /profile delete took the store's id with the folder even when the store stayed behind.
        string root = Path.Combine(Path.GetTempPath(), "ns-storekept-" + Guid.NewGuid().ToString("N"));
        try
        {
            string stores = Path.Combine(root, "WebsiteDataStore");
            string profile = Path.Combine(root, "work");
            Assert.Null(MacBrowserRules.KeptWithoutWindow(profile, stores));   // never opened: nothing to keep it for

            string id = MacBrowserRules.StoreId(profile)!;
            Assert.Null(MacBrowserRules.KeptWithoutWindow(profile, stores));   // an id whose store was never written
            Assert.Null(MacBrowserRules.StoreKept(id, "busy", stores));

            Directory.CreateDirectory(Path.Combine(stores, id.ToLowerInvariant()));   // WebKit names the folder in lower case
            Assert.Equal(BrowserText.MacStoreNotRemoved(BrowserText.MacUnavailable), MacBrowserRules.KeptWithoutWindow(profile, stores));
            Assert.Equal(BrowserText.MacStoreNotRemoved("Data store is in use"), MacBrowserRules.StoreKept(id, "Data store is in use", stores));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [UnixFact]
    public void Rules_AStoreIdThatCannotBeRead_IsAReason_NotAThrow()
    {
        // G6: the window-less /browser clear all read the file with no catch.
        if (OperatingSystem.IsWindows())
        {
            return;   // [UnixFact] skips it; the guard is for the platform analyzer (File.SetUnixFileMode)
        }

        string root = Path.Combine(Path.GetTempPath(), "ns-storeid-unread-" + Guid.NewGuid().ToString("N"));
        string profile = Path.Combine(root, "work");
        string file = Path.Combine(profile, MacBrowserRules.StoreIdFileName);
        try
        {
            MacBrowserRules.StoreId(profile);
            File.SetUnixFileMode(file, UnixFileMode.None);

            Assert.Null(MacBrowserRules.TryReadStoreId(profile, out string? unreadable));
            Assert.StartsWith("the Browser window's store id could not be read: ", unreadable);
            Assert.StartsWith(BrowserText.MacStoreNotRemoved("").Split(':')[0], MacBrowserRules.KeptWithoutWindow(profile, Path.Combine(root, "stores")));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Rules_ADeadPage_IsReloadedOnce_ThenShownAsAnError()
    {
        // G5: the count went back to 0 at every load's end, so a page whose process died after each load reloaded for ever.
        var none = default(MacContentEnds);
        var (first, reload) = MacBrowserRules.ContentEnded(none, "https://example.com/", TimeSpan.FromSeconds(10));
        Assert.True(reload);
        var (second, again) = MacBrowserRules.ContentEnded(first, "https://example.com/", TimeSpan.FromSeconds(15));   // right after its reload
        Assert.False(again);
        Assert.False(MacBrowserRules.ContentEnded(second, "https://example.com/", TimeSpan.FromSeconds(20)).Reload);

        Assert.True(MacBrowserRules.ContentEnded(second, "https://example.org/", TimeSpan.FromSeconds(20)).Reload);   // another page
        Assert.True(MacBrowserRules.ContentEnded(second, "https://example.com/", TimeSpan.FromSeconds(15) + MacBrowserRules.ContentEndQuiet).Reload);   // up a while since
    }

    [Fact]
    public void Text_AKeptProfile_SaysWhyAndWhatToDo()
    {
        Assert.Equal(
            "its Browser window data (a WebKit store kept outside the profile folder) could not be removed: WebKit did not answer in time. The profile is kept; try again in a desktop session, after restarting the app if WebKit still holds it",
            BrowserText.MacStoreNotRemoved(BrowserText.MacRemoveTimedOut + "."));
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
