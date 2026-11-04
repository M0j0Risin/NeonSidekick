using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Updates;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary><c>/update</c>, its lines and its marker (2026-11-04, self-update), over <see cref="FakeUpdater"/>, so no test can reach GitHub.</summary>
public partial class ChatScreenTests
{
    private const string Size = "NeonSidekick 0.5.7";

    private static readonly ConsoleKeyInfo ShiftI = new('I', ConsoleKey.I, true, false, false);

    /// <summary>A scripted run with the pane on, wide enough that a line stays whole: the input row at 100, the lower rule at 101.</summary>
    private void UpdatePaneOn()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Width = 200;
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null, () => 100);
    }

    // OnWait runs once per read, so a step that pushes nothing hands on to the next at once. The watch over "Checking..." polls and never reads, so no step runs under it.
    private void UpdateScript(params (Func<bool> Ready, Action<ScriptedInput> Act)[] steps)
    {
        var input = Scripted();
        int next = 0;
        input.OnWait = () =>
        {
            while (next < steps.Length && input.Queued == 0 && steps[next].Ready())
            {
                steps[next++].Act(input);
            }
        };
    }

    private bool Shows(string text) => Output.Contains(text, StringComparison.Ordinal);

    // For what the background work does with no wake of the read: blocking the read here is what a keyboard with nothing pressed does.
    private static bool Soon(Func<bool> done) => SpinWait.SpinUntil(done, TimeSpan.FromSeconds(10));

    private bool Asked(int times = 1) => Soon(() => _updater!.Checks >= times);

    // The marker redraws on the tick, which this clock moves by hand.
    private bool MarkerShows(string text) => Soon(() =>
    {
        _time.Advance(ScreenPane.Tick);
        return Shows(text);
    });

    [Fact]
    public void Update_Parses_AndIsAPaneBareAndQuickWithAWord()
    {
        Assert.Equal((SlashCommand.Update, "rollback"), SlashCommands.Parse("/update rollback"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Update));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Update, hasArgs: false));
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Update, hasArgs: true));
    }

    [Theory]
    [InlineData("", 0, true, "")]
    [InlineData("offered", 0, true, "🆙 0.5.7")]
    [InlineData("offered", 0, false, "")]
    [InlineData("downloading", 42, false, "🆙 0.5.7 42%")]
    [InlineData("checking", 0, false, "🆙 0.5.7 checking")]
    [InlineData("ready", 0, false, "🆙 0.5.7 at quit")]
    [InlineData("back", 0, false, "🆙 back to 0.5.7 at quit")]
    public void TheMarker_ReadsItsState_AndOnlyIsOutFollowsTheSetting(string state, int percent, bool checkOn, string expected)
    {
        UpdateScreenState shown = state switch
        {
            "offered" => new UpdateScreenState.Offered("0.5.7"),
            "downloading" => new UpdateScreenState.Downloading("0.5.7", percent),
            "checking" => new UpdateScreenState.Checking("0.5.7"),
            "ready" => new UpdateScreenState.Ready("0.5.7"),
            "back" => new UpdateScreenState.GoingBack("0.5.7"),
            _ => UpdateScreenState.Nothing,
        };
        Assert.Equal(expected, UpdateText.Marker(shown, checkOn));
    }

    // ---- at start

    [Fact]
    public async Task ANewVersionAtStart_SaysItOnce_AndNeverReachesTheModel()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")) && MarkerShows("🆙 0.5.7"), Line("/exit")));

        string output = await RunAsync();

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(output, System.Text.RegularExpressions.Regex.Escape(UpdateText.IsOutLine("0.5.7"))));
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task WithTheCheckOff_NoRequest_NoLine_NoMarker()
    {
        UpdatePaneOn();
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        UpdateScript((() => true, Line("/exit")));

        string output = await RunAsync();

        Assert.Equal(0, _updater.Checks);
        Assert.DoesNotContain(UpdateText.MarkerGlyph, output);
        Assert.DoesNotContain(UpdateText.IsOutLine("0.5.7"), output);
    }

    [Fact]
    public async Task WithTheCheckOff_UpdateStillWorksByHand_AndLaterLeavesNoMarker()
    {
        UpdatePaneOn();
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        UpdateScript(
            (() => true, Line("/update")),
            (() => Shows("the notes"), Key(Keys.Char('l'))),
            (() => { _time.Advance(ScreenPane.Tick); return true; }, Line("/exit")));

        string output = await RunAsync();

        Assert.Equal(1, _updater.Checks);
        Assert.Empty(_updater.Staged);
        Assert.DoesNotContain(UpdateText.MarkerGlyph + " 0.5.7", output);
    }

    [Theory]
    [InlineData(UpdateCheckKind.Failed)]
    [InlineData(UpdateCheckKind.Unsigned)]
    [InlineData(UpdateCheckKind.NoPackage)]
    [InlineData(UpdateCheckKind.Latest)]
    public async Task ACheckWithNothingToOffer_AtStart_PutsNothingOnTheScreen(UpdateCheckKind kind)
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = new UpdateCheckResult(kind, Reason: "a reason", Found: UpdateFixture.V("0.5.7")) };
        UpdateScript((() => Asked(), Line("/exit")));

        string output = await RunAsync();

        Assert.DoesNotContain(UpdateText.MarkerGlyph, output);
        Assert.DoesNotContain("a reason", output);
        Assert.DoesNotContain(UpdateText.ReleasesUrl, output);
    }

    [Fact]
    public async Task AFolderWithNoManifest_MakesNoRequest_AndUpdateSaysWhy()
    {
        _settings.Update(d => d.TtsOutput = false);
        _updater = new FakeUpdater { IsPackage = false, Answer = FakeUpdater.Newer() };
        PushLine("/update");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(0, _updater.Checks);
        Assert.Contains(UpdateText.NotAPackage, output);
    }

    [Fact]
    public async Task NoUpdater_UpdateSaysItCant()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/update");
        PushLine("/exit");

        Assert.Contains(UpdateText.NotAPackage, await RunAsync());
    }

    [Theory]
    [InlineData("Updated to 0.5.7. /update notes shows what changed.")]
    [InlineData("Back on 0.5.6.")]
    public async Task TheStartStepsLine_IsShown(string line)
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { StartLine = line };
        UpdateScript((() => Shows(line), Line("/exit")));
        Assert.Contains(line, await RunAsync());
    }

    [Fact]
    public async Task StartingWithAStageReady_ShowsAtQuit_WithNoIsOutLine_WhateverTheSetting()
    {
        UpdatePaneOn();
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { ReadyOnDisk = "0.5.7" };
        UpdateScript((() => MarkerShows("🆙 0.5.7 at quit"), Line("/exit")));

        string output = await RunAsync();

        Assert.Contains("🆙 0.5.7 at quit", output);
        Assert.DoesNotContain(UpdateText.IsOutLine("0.5.7"), output);
    }

    [Fact]
    public async Task StartingWithAStageReady_TheSameVersionFound_AddsNoLine()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { ReadyOnDisk = "0.5.7", Answer = FakeUpdater.Newer() };
        UpdateScript((() => Asked() && MarkerShows("🆙 0.5.7 at quit"), Line("/exit")));
        Assert.DoesNotContain(UpdateText.IsOutLine("0.5.7"), await RunAsync());
    }

    [Fact]
    public async Task StartingWithAStageReady_ANewerOneFound_NamesIt_AndTheMarkerStays()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { ReadyOnDisk = "0.5.7", Answer = FakeUpdater.Newer("0.5.8") };
        UpdateScript((() => Shows(UpdateText.IsOutLine("0.5.8")) && MarkerShows("🆙 0.5.7 at quit"), Line("/exit")));
        Assert.Contains(UpdateText.IsOutLine("0.5.8"), await RunAsync());
    }

    [Fact]
    public async Task ANewerRelease_ReplacesAReadyStage_ThroughThePane()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { ReadyOnDisk = "0.5.7", Answer = FakeUpdater.Newer("0.5.8") };
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.8")), Line("/update")),
            (() => Shows("the notes"), Key(Keys.Char('i'))),
            (() => Shows(UpdateText.ReadyLine("0.5.8")), Line("/exit")));

        await RunAsync();

        Assert.Equal("0.5.8", Assert.Single(_updater.Staged).Version.ToString());
    }

    [Fact]
    public async Task StartingWithARollbackPending_ShowsIt_WhateverTheSetting()
    {
        UpdatePaneOn();
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { RollbackOnDisk = "0.5.5" };
        UpdateScript((() => MarkerShows("🆙 back to 0.5.5 at quit"), Line("/exit")));
        Assert.Contains("🆙 back to 0.5.5 at quit", await RunAsync());
    }

    [Fact]
    public async Task AStageAnotherCopyLeft_IsFoundByUpdate_WithNoRequest_AndShownOnTheMarker()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater();
        UpdateScript(
            (() => Asked(), i => { _updater.ReadyOnDisk = "0.5.7"; PushLine(i, "/update"); }),
            (() => Shows(UpdateText.UnderWay(new UpdateScreenState.Ready("0.5.7"))), Key(Keys.Char('k'))),
            (() => MarkerShows("🆙 0.5.7 at quit"), Line("/exit")));

        await RunAsync();

        Assert.Empty(_updater.Staged);
    }

    // ---- the pane

    [Fact]
    public async Task Install_Downloads_ShowsThePhases_AndEndsReady()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        var release = new TaskCompletionSource();
        _updater.Stage = async (r, progress, ct) =>
        {
            progress?.Report(new UpdateStagePhase.Downloading(42));
            await release.Task.WaitAsync(ct);
            progress?.Report(new UpdateStagePhase.Checking());
            _updater.ReadyOnDisk = r.Version.ToString();
            return new UpdateStageResult(null);
        };
        string downloading = UpdateText.DownloadingLine("0.5.7", ModelStore.SizeLabel(231_000_000));
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), Line("/update")),
            (() => Shows("the notes"), Key(Keys.Char('i'))),
            (() => Shows(downloading) && MarkerShows("🆙 0.5.7 42%"), _ => release.SetResult()),
            (() => Shows(UpdateText.ReadyLine("0.5.7")) && MarkerShows("🆙 0.5.7 at quit"), Line("/exit")));

        string output = await RunAsync();

        Assert.Contains(downloading, output);
        Assert.Single(_updater.Staged);
    }

    [Fact]
    public async Task ThePane_ShowsTheNotesScrubbed_AndTheReleasePage()
    {
        UpdatePaneOn();
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer(notes: "safe\u001b[31m text\n[bold]not markup[/]") };
        UpdateScript(
            (() => true, Line("/update")),
            (() => Shows("not markup"), Key(Keys.Escape)),
            (() => true, Line("/exit")));

        string output = await RunAsync();

        Assert.Contains("[bold]not markup[/]", output);
        Assert.DoesNotContain("\u001b[31m", output, StringComparison.Ordinal);   // Ordinal: a culture comparison ignores control characters
        Assert.Contains(FakeUpdater.Release().PageUrl, output);
        Assert.Contains(UpdateText.PaneHeading("0.5.7", ModelStore.SizeLabel(231_000_000)), output);
        Assert.Empty(_updater.Staged);
    }

    [Fact]
    public async Task ThePane_WithNoNotes_SaysSo_AndStillLinksThePage()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer(notes: null) };
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), Line("/update")),
            (() => Shows(UpdateText.NoNotesInRelease), Key(Keys.Escape)),
            (() => true, Line("/exit")));
        Assert.Contains(FakeUpdater.Release(notes: null).PageUrl, await RunAsync());
    }

    [Fact]
    public async Task ThePanesFind_IsOff_ItsKeysStillWork()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        string[] tried = [];
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), Line("/update")),
            (() => Shows("the notes"), i =>
            {
                i.Push(Keys.Char('x'));
                i.Push(ShiftI);
                i.PushPaste("pasted");
                i.PushClick(10, 90, MouseButton.Right);
            }),
            (() => true, i => { tried = [.. _updater.Staged.Select(r => r.ArchiveName)]; i.Push(Keys.Char('l')); }),
            (() => true, Line("/exit")));

        string output = await RunAsync();

        Assert.Empty(tried);
        Assert.Empty(_updater.Staged);
        Assert.DoesNotContain("find: ", output);
        Assert.Contains(InfoPane.ButtonsHint([('i', "install"), ('l', "later")], tabs: false, cells: 199, find: false), output);
    }

    [Fact]
    public void TheHint_WithItsFindOff_LeavesItOut_AndAPaneWithoutTheOptionKeepsIt()
    {
        string off = InfoPane.ButtonsHint([('i', "install"), ('l', "later")], tabs: false, find: false);
        Assert.Equal(InfoPane.ScrollLead + "i = install · l = later · Esc = close", off);
        Assert.Contains(InfoPane.ShiftFindKeys, InfoPane.ButtonsHint([('i', "install")], tabs: false));
    }

    [Fact]
    public async Task WithNoPane_UpdatePrintsTheNotes_AndDownloadsNothing()
    {
        _settings.Update(d => d.TtsOutput = false);
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        PushLine("/update");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("the notes", output);
        Assert.Contains(Size, output);
        Assert.Empty(_updater.Staged);
    }

    [Fact]
    public async Task WithNoPane_WhatsUnderWay_NamesCancel()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { RollbackOnDisk = "0.5.5" };
        PushLine("/update");
        PushLine("/exit");
        Assert.Contains(UpdateText.CancelByCommand, await RunAsync());
    }

    [Fact]
    public async Task APreflightRefusal_SaysWhy_AndDownloadsNothing()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer(), PreflightRefusal = "a read-only folder" };
        string line = UpdateText.StageFailedLine("a read-only folder");
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), Line("/update")),
            (() => Shows("the notes"), Key(Keys.Char('i'))),
            (() => Shows(line), Line("/exit")));

        await RunAsync();

        Assert.Empty(_updater.Staged);
    }

    [Fact]
    public async Task AStageThatFails_SaysWhy_WithTheLink_AndTheMarkerGoesBack()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer(), Stage = (_, _, _) => Task.FromResult(new UpdateStageResult("a reason")) };
        string line = UpdateText.StageFailedLine("a reason");
        int mark = 0;
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), Line("/update")),
            (() => Shows("the notes"), Key(Keys.Char('i'))),
            (() => Shows(line), _ => mark = Output.Length),
            (() => true, Line("/exit")));

        await RunAsync();

        Assert.Contains(UpdateText.ReleasesUrl, line);
        Assert.DoesNotContain("🆙 0.5.7 at quit", Output[..mark]);
    }

    // ---- what /update answers with nothing to offer

    [Fact]
    public async Task OnTheLatest_UpdateSaysSo()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater();
        PushLine("/update");
        PushLine("/exit");
        Assert.Contains(UpdateText.LatestLine("0.5.6"), await RunAsync());
    }

    [Fact]
    public async Task AFailedCheck_UpdateSaysWhy_WithTheLink()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { Answer = new UpdateCheckResult(UpdateCheckKind.Failed, Reason: UpdateText.RateLimited) };
        PushLine("/update");
        PushLine("/exit");
        Assert.Contains(UpdateText.CheckFailedLine(UpdateText.RateLimited), await RunAsync());
    }

    [Fact]
    public async Task AnUnsignedRelease_UpdateSaysItIsntSigned()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { Answer = new UpdateCheckResult(UpdateCheckKind.Unsigned, Found: UpdateFixture.V("0.5.7")) };
        PushLine("/update");
        PushLine("/exit");
        Assert.Contains(UpdateText.UnsignedLine("0.5.7"), await RunAsync());
    }

    [Fact]
    public async Task AWordItDoesntKnow_GetsTheUsage()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater();
        PushLine("/update nonsense");
        PushLine("/exit");
        string output = await RunAsync();
        Assert.Contains(UpdateText.Usage, output);
        Assert.Equal(0, _updater.Checks);
    }

    // ---- the start's answer

    [Fact]
    public async Task TheStartsAnswer_IsUsedWithinTheHour_WithNoSecondRequest()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), Line("/update")),
            (() => Shows("the notes"), Key(Keys.Escape)),
            (() => true, Line("/exit")));

        await RunAsync();

        Assert.Equal(1, _updater.Checks);
    }

    [Fact]
    public async Task TheStartsAnswer_PastTheHour_IsAskedAgain_SoAPulledReleaseIsntOffered()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), i =>
            {
                _time.Advance(ChatScreen.UpdateAnswerKept + TimeSpan.FromMinutes(1));
                _updater.Answer = new UpdateCheckResult(UpdateCheckKind.Latest);
                PushLine(i, "/update");
            }),
            (() => Shows(UpdateText.LatestLine("0.5.6")), Line("/exit")));

        string output = await RunAsync();

        Assert.Equal(2, _updater.Checks);
        Assert.DoesNotContain("the notes", output);
    }

    [Fact]
    public async Task AStartsAnswerArrivingAfterUpdateBegan_IsDropped()
    {
        UpdatePaneOn();
        var gate = new TaskCompletionSource();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer(), CheckGate = gate };
        UpdateScript(
            (() => Asked(), i =>
            {
                // /update's own ask is under the watch, so the gate opens from outside the script once both are waiting.
                _ = Task.Run(() => { if (Asked(2)) { gate.SetResult(); } });
                PushLine(i, "/update");
            }),
            (() => Shows("the notes"), Key(Keys.Escape)),
            (() => true, Line("/exit")));

        Assert.DoesNotContain(UpdateText.IsOutLine("0.5.7"), await RunAsync());
    }

    [Fact]
    public async Task EscWhileAsking_StopsIt_AndOpensNothing()
    {
        UpdatePaneOn();
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer(), CheckGate = new TaskCompletionSource() };
        UpdateScript(
            (() => true, i => { PushLine(i, "/update"); i.Push(Keys.Escape); }),   // the watch takes the Esc
            (() => Asked(), Line("/exit")));

        Assert.DoesNotContain("the notes", await RunAsync());
    }

    // ---- /update cancel

    [Fact]
    public async Task CancelDuringADownload_DeletesItsPartial_AndTheMarkerGoesBack()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        _updater.Stage = async (_, progress, ct) =>
        {
            progress?.Report(new UpdateStagePhase.Downloading(42));
            await Task.Delay(Timeout.Infinite, ct);
            return new UpdateStageResult(null);
        };
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), Line("/update")),
            (() => Shows("the notes"), Key(Keys.Char('i'))),
            (() => MarkerShows("🆙 0.5.7 42%"), Line("/update cancel")),
            (() => Shows(UpdateText.CanceledLine), Line("/exit")));

        await RunAsync();

        Assert.Equal([true], _updater.Drops);
    }

    [Fact]
    public async Task CancelOfAStage_KeepsNoStage_AndSaysSo()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { ReadyOnDisk = "0.5.7" };
        PushLine("/update cancel");
        PushLine("/exit");

        Assert.Contains(UpdateText.CanceledLine, await RunAsync());
        Assert.Equal([false], _updater.Drops);
    }

    [Fact]
    public async Task CancelOfARollback_TakesItBack()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { RollbackOnDisk = "0.5.5" };
        PushLine("/update cancel");
        PushLine("/exit");

        Assert.Contains(UpdateText.CanceledLine, await RunAsync());
        Assert.Equal(1, _updater.RollbacksCanceled);
    }

    [Fact]
    public async Task CancelWithNothingUnderWay_SaysSo()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater();
        PushLine("/update cancel");
        PushLine("/exit");
        Assert.Contains(UpdateText.NothingToCancel, await RunAsync());
    }

    [Fact]
    public async Task TheOfferToCancel_EscKeepsWhatsUnderWay()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { RollbackOnDisk = "0.5.5" };
        UpdateScript(
            (() => Asked(), Line("/update")),
            (() => Shows(UpdateText.UnderWay(new UpdateScreenState.GoingBack("0.5.5"))), Key(Keys.Escape)),
            (() => true, Line("/exit")));

        await RunAsync();

        Assert.Equal(0, _updater.RollbacksCanceled);
    }

    [Fact]
    public async Task TheOfferToCancel_CCancels_WithItsFindOff()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { RollbackOnDisk = "0.5.5" };
        UpdateScript(
            (() => Asked(), Line("/update")),
            (() => Shows(UpdateText.UnderWay(new UpdateScreenState.GoingBack("0.5.5"))), Key(Keys.Char('c'))),
            (() => Shows(UpdateText.CanceledLine), Line("/exit")));

        await RunAsync();

        Assert.Equal(1, _updater.RollbacksCanceled);
    }

    [Fact]
    public async Task AStageWithNothingNewer_OffersToCancel_EvenWithNoNetwork()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { ReadyOnDisk = "0.5.7", Answer = new UpdateCheckResult(UpdateCheckKind.Failed, Reason: "no network") };
        UpdateScript(
            (() => Asked(), Line("/update")),
            (() => Shows(UpdateText.UnderWay(new UpdateScreenState.Ready("0.5.7"))), Key(Keys.Char('c'))),
            (() => Shows(UpdateText.CanceledLine), Line("/exit")));

        await RunAsync();

        Assert.Equal([false], _updater.Drops);
    }

    // ---- /update rollback and notes

    [Fact]
    public async Task Rollback_Warns_SaysWhere_AndShowsOnTheMarker()
    {
        UpdatePaneOn();
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater();
        UpdateScript(
            (() => true, Line("/update rollback")),
            (() => Shows(UpdateText.GoingBackLine("0.5.5")) && MarkerShows("🆙 back to 0.5.5 at quit"), Line("/exit")));

        Assert.Contains(UpdateText.RollbackWarning, await RunAsync());
    }

    [Fact]
    public async Task Rollback_WithNothingToGoBackTo_SaysSo()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { RollbackRefusal = UpdateText.NothingToGoBackTo };
        PushLine("/update rollback");
        PushLine("/exit");
        Assert.Contains(UpdateText.NothingToGoBackTo, await RunAsync());
    }

    [Fact]
    public async Task Rollback_Refused_GivesTheReasonAndTheLink()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { RollbackRefusal = UpdateText.OldVersionAltered("a.dll") };
        PushLine("/update rollback");
        PushLine("/exit");
        Assert.Contains(UpdateText.RollbackRefusedLine(UpdateText.OldVersionAltered("a.dll")), await RunAsync());
    }

    [Fact]
    public async Task ASecondRollback_SaysItsPending()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { RollbackOnDisk = "0.5.5" };
        PushLine("/update rollback");
        PushLine("/exit");
        Assert.Contains(UpdateText.AlreadyGoingBack("0.5.5"), await RunAsync());
    }

    [Fact]
    public async Task RollbackDuringADownload_SaysToCancelFirst()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        _updater.Stage = async (_, progress, ct) =>
        {
            progress?.Report(new UpdateStagePhase.Downloading(10));
            await Task.Delay(Timeout.Infinite, ct);
            return new UpdateStageResult(null);
        };
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), Line("/update")),
            (() => Shows("the notes"), Key(Keys.Char('i'))),
            (() => MarkerShows("🆙 0.5.7 10%"), Line("/update rollback")),
            (() => Shows(UpdateText.CancelDownloadFirst), Line("/exit")));

        await RunAsync();

        Assert.Null(_updater.RollbackOnDisk);
    }

    [Fact]
    public async Task Notes_PrintScrubbed()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { Notes = "first line\nsecond\u001b[31m line" };
        PushLine("/update notes");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(UpdateText.NotesHeading("0.5.6"), output);
        Assert.Contains("first line", output);
        Assert.Contains("[31m line", output);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);   // Ordinal: a culture comparison ignores control characters and finds it anywhere
    }

    [Fact]
    public async Task Notes_WithNoneSaved_SaysSo_AndLinksTheReleases()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater();
        PushLine("/update notes");
        PushLine("/exit");
        Assert.Contains(UpdateText.NoNotesSaved("0.5.6"), await RunAsync());
    }

    // ---- under a reply

    [Fact]
    public async Task ALineThatArrivesDuringAReply_WaitsForTheIdleLine()
    {
        _settings.Update(d => d.TtsOutput = false);
        var gate = new TaskCompletionSource();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer(), CheckGate = gate };
        _chat.EnqueueText("the reply");
        string line = UpdateText.IsOutLine("0.5.7");
        bool during = true;
        _chat.AfterLastUpdate = async cancellationToken =>
        {
            gate.SetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            during = Shows(line);
        };
        UpdateScript(
            (() => true, Line("hi")),
            (() => Shows(line), Line("/exit")));

        string output = await RunAsync();

        Assert.False(during);
        Assert.Contains(line, output);
    }

    // ---- the marker's double-click

    [Fact]
    public async Task ADoubleClickOnTheMarker_OpensUpdate()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")) && MarkerShows("🆙 0.5.7"), i => { i.PushClick(193, 101); i.PushClick(193, 101); }),   // 🆙 0.5.7 at columns 189–196
            (() => Shows("the notes"), Key(Keys.Escape)),
            (() => true, Line("/exit")));

        Assert.Contains(UpdateText.PaneHeading("0.5.7", ModelStore.SizeLabel(231_000_000)), await RunAsync());
    }

    [Fact]
    public async Task TheOffMarker_HidesWhenTheSettingGoesOff_ButADownloadStays()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        _updater.Stage = async (_, progress, ct) =>
        {
            progress?.Report(new UpdateStagePhase.Downloading(42));
            await Task.Delay(Timeout.Infinite, ct);
            return new UpdateStageResult(null);
        };
        int mark = 0;
        bool hidden = false;
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")) && MarkerShows("🆙 0.5.7"), _ =>
            {
                _settings.Update(d => d.CheckForUpdatesAtStart = false);
                mark = Output.Length;
                _time.Advance(ScreenPane.Tick);
                hidden = Output.Length > mark && !Output[mark..].Contains(UpdateText.MarkerGlyph, StringComparison.Ordinal);
            }),
            (() => true, Line("/update")),
            (() => Shows("the notes"), Key(Keys.Char('i'))),
            (() => MarkerShows("🆙 0.5.7 42%"), Line("/exit")));

        await RunAsync();

        Assert.True(hidden);
    }

    [Fact]
    public async Task TheOffMarker_HidesOnASwitchToAProfileWithTheCheckOff()
    {
        UpdatePaneOn();
        WorkProfile(more: d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        int mark = 0;
        bool hidden = false;
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")) && MarkerShows("🆙 0.5.7"), Line("/profile work")),
            (() => Shows("work"), _ =>
            {
                mark = Output.Length;
                _time.Advance(ScreenPane.Tick);
                _time.Advance(ScreenPane.Tick);
                hidden = !Output[mark..].Contains(UpdateText.MarkerGlyph, StringComparison.Ordinal);
            }),
            (() => true, Line("/exit")));

        await RunAsync();

        Assert.True(hidden);
    }

    // ---- under a reply

    // What's typed under the reply, the reply held until the watch has read it and what it ran has had its moment. "hi" once ready, and /exit only then: a pane's own read runs OnWait too, and /exit's letters there would press its buttons.
    private void UnderTheReply(Action<ScriptedInput> act, Func<bool>? ready = null)
    {
        var input = Scripted();
        bool over = false;
        _chat.EnqueueText("the reply");
        _chat.AfterLastUpdate = async cancellationToken =>
        {
            act(input);
            Assert.True(Soon(() => input.Queued == 0));
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            Volatile.Write(ref over, true);
        };
        int next = 0;
        input.OnWait = () =>
        {
            if (next == 0 && (ready is null || ready()))
            {
                next++;
                PushLine(input, "hi");
            }
            else if (next == 1 && Volatile.Read(ref over))
            {
                next++;
                PushLine(input, "/exit");
            }
        };
    }

    [Theory]
    [InlineData("/update notes")]
    [InlineData("/update cancel")]
    [InlineData("/update rollback")]
    public async Task UnderAReply_AWord_AnswersInLines_OpensNoPane_AndNeverReachesTheModel(string typed)
    {
        UpdatePaneOn();
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { Notes = "these notes" };
        UnderTheReply(i => PushLine(i, typed));
        string answer = typed switch
        {
            "/update notes" => "these notes",
            "/update cancel" => UpdateText.NothingToCancel,
            _ => UpdateText.GoingBackLine("0.5.5"),
        };

        string output = await RunAsync();

        Assert.Contains(answer, output);
        Assert.Equal(0, _updater.Checks);
        Assert.Equal("hi", Assert.Single(_chat.Requests)[^1].Text);
    }

    [Fact]
    public async Task UnderAReply_TheBareCommand_OpensThePane_AndThePaneStagesNothingOnLater()
    {
        UpdatePaneOn();
        _settings.Update(d => d.CheckForUpdatesAtStart = false);
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        bool opened = false;
        UnderTheReply(i =>
        {
            PushLine(i, "/update");
            opened = Soon(() => Shows("the notes"));
            i.Push(Keys.Char('l'));
        });

        await RunAsync();

        Assert.True(opened);
        Assert.Empty(_updater.Staged);
    }

    [Fact]
    public async Task UnderAReply_ADoubleClickOnTheMarker_OpensThePane()
    {
        UpdatePaneOn();
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        bool opened = false;
        UnderTheReply(i =>
        {
            i.PushClick(193, 101);
            i.PushClick(193, 101);
            opened = Soon(() => Shows("the notes"));
            i.Push(Keys.Char('l'));
        }, () => Shows(UpdateText.IsOutLine("0.5.7")) && MarkerShows("🆙 0.5.7"));

        await RunAsync();

        Assert.True(opened);
        Assert.Equal(1, _updater.Checks);   // the start's answer was used
    }

    /// <summary>Side by side (the plan's mockup: <c>🆙 0.5.7 ── ▶ 1</c>), each answers its own double-click and neither takes the other's.</summary>
    [WindowsFact]
    public async Task TheMarkerAndTheProcessCount_SideBySide_EachOpensItsOwn()
    {
        _openProcessWindow = (_, _) => { };
        _settings.Update(d => { d.TtsOutput = false; d.ShellCommandPolicy = "yolo"; });
        _console.Profile.Width = 80;
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null, () => 100);
        _updater = new FakeUpdater { Answer = FakeUpdater.Newer() };
        _chat.Enqueue(FakeChatClient.Call("c1", RunCommandTool.ToolName, new Dictionary<string, object?> { ["command"] = "ping -n 30 127.0.0.1 >nul", ["background"] = true }));
        _chat.EnqueueText("Started.");
        bool listFirst = false;
        UpdateScript(
            (() => Shows(UpdateText.IsOutLine("0.5.7")), Line("start it")),
            (() => MarkerShows("🆙 0.5.7 ── ▶ 1 ─"), i => { i.PushClick(76, 101); i.PushClick(76, 101); }),   // ▶ 1 at columns 75–77, the marker at 63–70
            (() => Shows(ProcessMenu.Title), i => { listFirst = !Shows("the notes"); i.Push(Keys.Escape); }),
            (() => true, i => { i.PushClick(66, 101); i.PushClick(66, 101); }),
            (() => Shows("the notes"), Key(Keys.Char('l'))),
            (() => true, Line("/exit")));

        await RunAsync();

        Assert.True(listFirst);
    }

    // ---- on the page

    [Fact]
    public async Task OnThePage_NotesRun_TheLinesReachIt_AndTheRestArePcOnly()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; d.CheckForUpdatesAtStart = false; });
        _updater = new FakeUpdater { Notes = "from the page", StartLine = "Updated to 0.5.6. /update notes shows what changed." };
        WebReply notes = default;
        var refused = new List<WebReply>();
        var input = WebSteps(
            _ =>
            {
                refused.Add(Web.Send(new WebSendRequest { Text = "/update" }, System.Net.IPAddress.Loopback));
                refused.Add(Web.Send(new WebSendRequest { Text = "/update rollback" }, System.Net.IPAddress.Loopback));
                refused.Add(Web.Send(new WebSendRequest { Text = "/update cancel" }, System.Net.IPAddress.Loopback));
                notes = Web.Send(new WebSendRequest { Text = "/update notes" }, System.Net.IPAddress.Loopback);
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal(WebOutcome.Ok, notes.Outcome);
        Assert.All(refused, reply => Assert.Equal(WebOutcome.Refused, reply.Outcome));
        Assert.Equal(WebUiText.PcOnly("/update"), refused[0].Message);
        var feed = Web.Feed.Snapshot(m => m);
        Assert.Contains(feed, e => e.Text.Contains("from the page", StringComparison.Ordinal));
        Assert.Contains(feed, e => e.Text.Contains("Updated to 0.5.6.", StringComparison.Ordinal));
        Assert.Equal(0, _updater.Checks);
    }
}
