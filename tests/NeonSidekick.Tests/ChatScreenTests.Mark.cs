using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/mark</c> on the screen (2026-10-18, the user's ask): the rules around the draft line in a color, said; bare, the mark and the choices;
/// an unknown word, the error; kept in the session row and back on restore; a new chat unmarked, a purge of the row keeping it; and under a
/// reply at once.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The mark the current session row holds (the newest row), or null.</summary>
    private string? NewestRowMark()
    {
        using var store = OpenSessions();
        var newest = store.List(10).OrderByDescending(s => s.Id).First();
        return SessionHistory.MarkOf(store.Load(newest.Id)!.HistoryJson);
    }

    [Fact]
    public async Task Mark_SetsTheRules_SaysIt_AndBareShowsItAndTheChoices()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.EmitAnsiSequences();
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);   // the pane, so the rules are drawn
        PushLine("/mark");
        PushLine("/mark GREEN");
        PushLine("/mark");
        PushLine("/mark teal");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(MarkText.ShowLine(null), output);
        Assert.Contains(MarkText.SetNotice("green"), output);
        Assert.Contains(MarkText.ShowLine("green"), output);
        Assert.Contains(MarkText.UnknownError("teal"), output);
        var green = RuleMarks.ColorOf("green");
        Assert.Contains($"38;2;{green.R};{green.G};{green.B}m─", output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Mark_IsSavedWithTheSession_AndDefaultTakesItOff()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("Hello.").EnqueueText("Again.");
        PushLine("hi");
        PushLine("/mark blue");
        PushLine("more");
        PushLine("/exit");

        await RunAsync();

        Assert.Equal("blue", NewestRowMark());   // saved at once, and by the next turn's save
    }

    [Fact]
    public async Task Mark_Default_TakesItOff_AndTheRowForgetsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/mark red");
        PushLine("/mark default");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(MarkText.DefaultNotice, output);
        Assert.Null(NewestRowMark());
    }

    [Fact]
    public async Task Mark_ComesBackWithARestoredSession_AndANewChatIsUnmarked()
    {
        _settings.Update(d => d.TtsOutput = false);
        long id = SeedSession();
        using (var store = OpenSessions())
        {
            var history = new ConversationHistory("");
            history.AddUser("How do I wire the Vosk model?");
            history.AddAssistant("Pass the folder to the detector.");
            store.SaveHistory(id, SessionHistory.ToJson(history.Messages, mark: "purple"));
        }

        PushLine("/sessions " + id);
        PushLine("/mark");
        PushLine("/new");
        PushLine("/mark");
        PushLine("/exit");

        string output = await RunAsync();

        int restored = output.IndexOf(MarkText.ShowLine("purple"), StringComparison.Ordinal);
        Assert.True(restored >= 0);
        Assert.True(output.IndexOf(MarkText.ShowLine(null), restored, StringComparison.Ordinal) > restored);
    }

    [Fact]
    public async Task Mark_AnUnknownStoredColor_IsDropped()
    {
        _settings.Update(d => d.TtsOutput = false);
        long id = SeedSession();
        using (var store = OpenSessions())
        {
            var history = new ConversationHistory("");
            history.AddUser("How do I wire the Vosk model?");
            store.SaveHistory(id, SessionHistory.ToJson(history.Messages, mark: "teal"));
        }

        PushLine("/sessions " + id);
        PushLine("/mark");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(MarkText.ShowLine(null), output);
    }

    [Fact]
    public async Task Mark_IsKeptByAPurgeOfTheCurrentRow()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("Hello.");
        StepsWhenIdle(Line("hi"), Line("/mark cyan"), Line("/sessions purge 1"), Key(Keys.Char('y')), Key(Keys.Enter), Line("/mark"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(SessionsMenu.PurgedNotice(1), output);
        Assert.Contains(MarkText.ShowLine("cyan"), output);
    }

    [Fact]
    public async Task Mark_UnderAReply_RunsAtOnce()
    {
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                PushLine("/mark orange");
            }
        });

        string output = await RunAsync();

        Assert.Contains(MarkText.SetNotice("orange"), output);
        Assert.Equal("orange", NewestRowMark());   // the turn's own save kept it
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Mark, "orange"));
    }
}
