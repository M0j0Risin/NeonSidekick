using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// A resumed session's folder (2026-11-21, feature idea #4): one that worked elsewhere offers the switch, a yes saving it as <c>/cwd</c> does;
/// a gone folder, <c>--cwd</c> and the session of this folder ask nothing; <c>/sessions here</c> parses, and each turn records the folder.
/// </summary>
public partial class ChatScreenTests
{
    private long SeedSessionIn(string folder)
    {
        using var store = OpenSessions();
        long id = store.Begin("worked elsewhere", "llama", folder)!.Value;
        store.AppendTurn(id, "worked elsewhere", "Pass the folder to the detector.", 0, [], [], 0, 3, 4, false, cwd: folder);
        var history = new ConversationHistory("");
        history.AddUser("worked elsewhere");
        history.AddAssistant("Pass the folder to the detector.");
        store.SaveHistory(id, SessionHistory.ToJson(history.Messages));
        return id;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resume_OfASessionFromAnotherFolder_OffersTheSwitch_AYesSavingIt(bool switched)
    {
        _settings.Update(d => d.TtsOutput = false);
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        long id = SeedSessionIn(elsewhere);
        if (switched)
        {
            StepsWhenIdle(Line("/sessions " + id), Key(Keys.Down), Key(Keys.Enter), Line("/exit"));   // no is the first choice
        }
        else
        {
            StepsWhenIdle(Line("/sessions " + id), Key(Keys.Enter), Line("/exit"));
        }

        string output = await RunAsync();

        Assert.Contains("Pass the folder to the detector.", output);
        Assert.Contains(SessionText.SwitchFolderQuestion(elsewhere), output);
        Assert.Equal(switched ? elsewhere : "", _settings.Current.WorkingDirectory);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Resume_OfASessionWhoseFolderIsGone_IsANotice_AndNoQuestion()
    {
        _settings.Update(d => d.TtsOutput = false);
        string gone = Path.Combine(_dir, "gone");
        long id = SeedSessionIn(gone);
        PushLine("/sessions " + id);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SessionText.FolderGone(gone), output);
        Assert.DoesNotContain(SessionText.SwitchFolderQuestion(gone), output);
        Assert.Equal("", _settings.Current.WorkingDirectory);
    }

    [Fact]
    public async Task Resume_UnderCwd_SaysTheFlagHoldsTheFolder_AndAsksNothing()
    {
        _settings.Update(d => d.TtsOutput = false);
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        long id = SeedSessionIn(elsewhere);
        _overriddenBy = field => field == SettingsField.WorkingDirectory ? "--cwd" : null;
        PushLine("/sessions " + id);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SessionText.FolderHeld(elsewhere, "--cwd"), output);
        Assert.DoesNotContain(SessionText.SwitchFolderQuestion(elsewhere), output);
    }

    [Fact]
    public async Task Resume_OfThisFoldersSession_AsksNothing_AndATurnRecordsTheFolderAndModel()
    {
        _settings.Update(d => d.TtsOutput = false);
        string here = NeonSidekick.Files.WorkingDirectory.Resolve("", _settings.ProfileDirectory);
        Directory.CreateDirectory(here);
        long id = SeedSessionIn(here + Path.DirectorySeparatorChar);
        _chat.EnqueueText("More.");
        PushLine("/sessions " + id);
        PushLine("and more");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain("This session worked in", output);
        using var store = OpenSessions();
        var summary = store.Summary(id)!;
        Assert.Equal(2, summary.Turns);
        Assert.True(NeonSidekick.Files.FolderMatch.Same(here, summary.Cwd));
    }

    [Theory]
    [InlineData("here", SessionActionKind.Here)]
    [InlineData("HERE", SessionActionKind.Here)]
    [InlineData("here now", SessionActionKind.Invalid)]
    public void ParseSessionArgs_ReadsHere(string args, SessionActionKind kind) => Assert.Equal(kind, ChatScreen.ParseSessionArgs(args).Kind);

    [Fact]
    public void SessionsHere_OpensOverAReply_AndIsTheSessionsTabFromThePage()
    {
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Session, "here"));
        Assert.Same(WebCommandVerdict.SessionsTab, WebCommands.Check(SlashCommand.Session, "here", "/sessions here", new WebCommandSettings(Commands: true, FileTools: true, ObsidianTools: true)));
        Assert.Contains(ChatScreen.SessionVerbs, v => v.Text == SessionText.HereWord && v.Note == SessionText.HereNote);
    }
}

/// <summary>The page's side of a session's folder (2026-11-21): the Sessions tab marks this folder's rows with a flag, and a resume from the page asks nothing.</summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task ThePage_MarksThisFoldersSessions_AndAResumeFromItOnlyTellsThePc()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        string here = NeonSidekick.Files.WorkingDirectory.Resolve("", _settings.ProfileDirectory);
        Directory.CreateDirectory(here);
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        long mine = SeedSessionIn(here);
        long other = SeedSessionIn(elsewhere);
        NeonSidekick.WebUi.WebSessionsView? view = null;
        NeonSidekick.WebUi.WebReply resumed = default;
        StepsWhenIdle(
            _ =>
            {
                view = Web.Sessions(null);
                resumed = Web.ResumeSession(new NeonSidekick.WebUi.WebSessionResume(other, _settings.ProfileName), null);
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.True(view!.Rows.Single(r => r.Id == mine).Here);
        Assert.False(view.Rows.Single(r => r.Id == other).Here);
        Assert.Equal(NeonSidekick.WebUi.WebOutcome.Ok, resumed.Outcome);
        Assert.Contains(SessionText.FolderDiffers(elsewhere), output);
        Assert.DoesNotContain(SessionText.SwitchFolderQuestion(elsewhere), output);
        Assert.Equal("", _settings.Current.WorkingDirectory);
    }
}

/// <summary><c>/usage models</c> with no pane (2026-11-21, feature idea #7): the model rows, never the Statistics figures.</summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task UsageModels_WithoutThePane_PrintsTheModelRows()
    {
        _settings.Update(d => d.TtsOutput = false);
        using (var store = OpenSessions())
        {
            long id = store.Begin("t", "llama")!.Value;
            store.AppendTurn(id, "q", "r", 2, [], [], 0, 3, 40, false, model: "qwen3-scorecard");
        }

        PushLine("/usage models");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(UsageText.ModelsModelHeading, output);
        Assert.Contains("qwen3-scorecard", output);
        Assert.DoesNotContain(UsageText.NothingCounted, output);
    }
}
