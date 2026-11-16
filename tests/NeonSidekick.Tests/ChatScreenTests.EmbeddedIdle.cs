using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The embedded model's idle unload on the screen (2026-11-16, plan lucid-roaming-lantern #44): the notice at the idle line after the minutes,
/// 💤 on the Status bar meanwhile, the next message reloading it first; a reload cancelled with Esc or failing puts the message back and keeps
/// the model for the next try; a crash marks it for the same reload; the kill switch forgets it, so the next message asks for a server.
/// </summary>
public partial class ChatScreenTests
{
    private static readonly TimeSpan IdleTwenty = TimeSpan.FromMinutes(20);

    private static string UnloadedE2b => EmbeddedLlmText.IdleUnloadedNotice(EmbeddedModelCatalog.Find("gemma-4-e2b")!, IdleTwenty, null);

    /// <summary>The fixture on the embedded E2B with the idle unload at 20 minutes.</summary>
    private FakeEmbeddedLlm OnTheEmbeddedE2bUnloadingAt20()
    {
        var embedded = OnTheEmbeddedE2b();
        _settings.Update(d => d.EmbeddedIdleUnloadMinutes = 20);
        return embedded;
    }

    [Fact]
    public void UnloadedMark_IsPinned()
    {
        Assert.Equal("💤", ChatScreen.UnloadedGlyph);
        Assert.Equal(ChatScreen.ModelMark("m", "medium") + " 💤", ChatScreen.ModelMark("m", "medium", unloaded: true));
        Assert.Equal("", ChatScreen.ModelMark(null, "medium", unloaded: true));
    }

    [Fact]
    public async Task IdleUnload_SaysSo_ShowsTheSleepMark_AndTheNextMessageReloadsIt()
    {
        var embedded = OnTheEmbeddedE2bUnloadingAt20();
        LoadOnThePane();
        _chat.EnqueueText("Back.");
        bool sleeping = false;
        StepsWhenIdle(
            _ => _time.Advance(IdleTwenty),
            input => { sleeping = _session.ShownUnloaded; PushLine(input, "hello"); },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(UnloadedE2b, output);
        Assert.True(sleeping);
        Assert.Contains(ScreenPane.TrailerText("gemma-4-e2b", ChatScreen.ModelMark("gemma-4-e2b", _settings.Current.LlmReasoning, unloaded: true)), output);
        Assert.Equal(["gemma-4-e2b", "gemma-4-e2b"], embedded.Starts);
        Assert.Equal(1, embedded.Stops);
        Assert.Contains("● Back.", output);
        Assert.Single(_chat.Requests);
        Assert.Null(_session.IdleUnloaded);
        Assert.NotNull(embedded.Running);
        Assert.Equal("embedded", _settings.Current.LlmUrl);
    }

    [Fact]
    public async Task IdleUnload_Off_KeepsTheModel()
    {
        var embedded = OnTheEmbeddedE2b();
        StepsWhenIdle(input => { _time.Advance(TimeSpan.FromDays(1)); PushLine(input, "/exit"); });

        string output = await RunAsync();

        Assert.DoesNotContain("🦙 Unloaded", output);
        Assert.Equal(0, embedded.Stops);
        Assert.NotNull(embedded.Running);
    }

    [Fact]
    public async Task IdleUnload_AFailedReload_PutsTheMessageBack_AndKeepsTheModelForTheNextTry()
    {
        var embedded = OnTheEmbeddedE2bUnloadingAt20();
        _chat.EnqueueText("Back.");
        StepsWhenIdle(
            _ => _time.Advance(IdleTwenty),
            input => { embedded.StartFailure = "out of memory"; PushLine(input, "hello"); },
            input =>
            {
                Assert.True(_session.ShownUnloaded);
                Assert.Empty(_chat.Requests);
                embedded.StartFailure = null;
                input.Push(Keys.Enter);   // the message back on the line: Enter tries again
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("out of memory", output);
        Assert.Equal(3, embedded.Starts.Count);
        Assert.Contains("● Back.", output);
        Assert.Equal("hello", _chat.Requests.Single()[^1].Text);
        Assert.Null(_session.IdleUnloaded);
    }

    [Fact]
    public async Task IdleUnload_EscDuringTheReload_PutsTheMessageBack()
    {
        var embedded = OnTheEmbeddedE2bUnloadingAt20();
        LoadOnThePane();
        StepsWhenIdle(
            _ => _time.Advance(IdleTwenty),
            input =>
            {
                embedded.StartGate = async ct =>
                {
                    input.Push(Keys.Escape);
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);   // the cancel ends it; without one the load lands and the asserts fail
                };
                PushLine(input, "hello");
            },
            input =>
            {
                Assert.Contains("hello", _console.Output[_console.Output.LastIndexOf(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), StringComparison.Ordinal)..]);   // back on the row
                embedded.StartGate = null;
                input.Push(Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
                PushLine(input, "/exit");
            });

        string output = await RunAsync();

        Assert.Contains(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), output);
        Assert.Empty(_chat.Requests);
        Assert.True(_session.ShownUnloaded);
        Assert.Equal("gemma-4-e2b", _session.IdleUnloaded!.Model.Id);
        Assert.Null(embedded.Running);
    }

    [Fact]
    public async Task ACrash_MarksTheModelUnloaded_AndTheNextMessageRestartsIt()
    {
        var embedded = OnTheEmbeddedE2b();
        _chat.EnqueueText("Back.");
        StepsWhenIdle(
            _ => embedded.Crash(),
            Line("hello"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.DoesNotContain("🦙 Unloaded", output);   // the host's error line said it
        Assert.Equal(["gemma-4-e2b", "gemma-4-e2b"], embedded.Starts);
        Assert.Contains("● Back.", output);
        Assert.Null(_session.IdleUnloaded);
    }

    [WindowsFact]
    public async Task KillSwitch_AfterAnIdleUnload_ForgetsTheModel_AndTheNextMessageAsksForAServer()
    {
        var embedded = OnTheEmbeddedE2bUnloadingAt20();
        StepsWhenIdle(
            _ => _time.Advance(IdleTwenty),
            input => input.Push(Keys.CtrlAlt(ConsoleKey.X), Keys.CtrlAlt(ConsoleKey.X)),
            Line("hello"),
            input => { input.Push(Keys.Ctrl(ConsoleKey.A), Keys.Backspace); PushLine(input, "/exit"); });

        string output = await RunAsync();

        Assert.Contains(UnloadedE2b, output);
        Assert.Contains(KilledE2b, output);
        Assert.Contains("✗ " + ChatScreen.NoAssistantError, output);
        Assert.Single(embedded.Starts);
        Assert.Null(_session.IdleUnloaded);
        Assert.Empty(_chat.Requests);
    }
}
