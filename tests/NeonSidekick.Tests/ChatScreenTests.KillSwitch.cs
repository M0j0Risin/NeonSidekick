using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The embedded model's kill switch, Ctrl+Alt+X (2026-10-01, the user's ask: "immediately unload an embedded model if one is
/// loaded; if using an external LLM server, the command will simply do nothing"): at the idle line, under a reply, in a pane
/// and under a load.
/// </summary>
public partial class ChatScreenTests
{
    private static readonly string KilledE2b = EmbeddedLlmText.KilledNotice(["Gemma 4 E2B"]);

    /// <summary>
    /// A script whose <paramref name="steps"/> run one per idle read (a pane's or the line's, <see cref="ScriptedInput.OnWait"/>):
    /// keys pushed before the run would be read ahead by the startup connect's watcher, a kill landing before the model is up.
    /// </summary>
    private static ScriptedInput WhenIdle(params Action<ScriptedInput>[] steps)
    {
        var input = new ScriptedInput();
        int step = 0;
        input.OnWait = () =>
        {
            if (step < steps.Length)
            {
                steps[step++](input);
            }
        };
        return input;
    }

    /// <summary>The fixture on the saved embedded model, Gemma 4 E2B, speech off.</summary>
    private FakeEmbeddedLlm OnTheEmbeddedE2b()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "embedded"; d.LlmModel = "gemma-4-e2b"; });
        return UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
    }

    [Fact]
    public void KilledNotice_IsPinned()
    {
        Assert.Equal("Embedded model unloaded: Gemma 4 E2B. /server loads it again.", KilledE2b);
        Assert.Equal("Embedded model unloaded: A, B. /server loads it again.", EmbeddedLlmText.KilledNotice(["A", "B"]));
        Assert.Equal("Kill switch (Ctrl+Alt+X): stopped llama-server for gemma-4-e2b.", EmbeddedLlmText.KilledLog(["gemma-4-e2b"]));
        Assert.Equal("Gemma 4 E2B was unloaded (Ctrl+Alt+X) before it finished loading", EmbeddedLlmText.KilledStart(EmbeddedModelCatalog.Find("gemma-4-e2b")!));
    }

    [Fact]
    public async Task KillSwitch_AtTheIdleLine_UnloadsTheModel_AndTheNextMessageAsksForAServer()
    {
        var embedded = OnTheEmbeddedE2b();
        var input = WhenIdle(i =>
        {
            i.Push(Keys.CtrlAlt(ConsoleKey.X));
            PushLine(i, "hello");
            PushLine(i, "/exit");
        });

        string output = await RunAsync(input);

        Assert.Equal(1, embedded.Kills);
        Assert.Null(embedded.Running);
        Assert.Contains(KilledE2b, output);
        Assert.Null(_session.Assistant);
        Assert.Null(_session.Endpoint);   // the hint row's model name goes with it
        Assert.Contains("✗ " + ChatScreen.NoAssistantError, output);
        Assert.Empty(_chat.Requests);
        Assert.Equal("embedded", _settings.Current.LlmUrl);   // the saved URL stays: /server or the next start loads it again
    }

    [Fact]
    public async Task KillSwitch_OnAnExternalServer_DoesNothing()
    {
        _settings.Update(d => d.TtsOutput = false);
        var embedded = UseEmbedded(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        var input = WhenIdle(i =>
        {
            i.Push(Keys.CtrlAlt(ConsoleKey.X));
            PushLine(i, "/exit");
        });

        string output = await RunAsync(input);

        Assert.Equal(0, embedded.Kills);
        Assert.DoesNotContain("Embedded model unloaded", output);
        Assert.NotNull(_session.Assistant);
        Assert.Equal("http://127.0.0.1:1234/v1", _session.Endpoint?.BaseUrl.AbsoluteUri);
    }

    [Fact]
    public async Task KillSwitch_UnderAReply_CancelsIt_ThenUnloads()
    {
        var embedded = OnTheEmbeddedE2b();
        _chat.EnqueueText("One. ", "Two.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1)
            {
                _console.Input.PushKey(Keys.CtrlAlt(ConsoleKey.X));
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }

                PushLine("/exit");
            }
        };
        PushLine("hi");

        string output = await RunAsync();

        Assert.Contains("● One.\n  · " + ChatScreen.CancelledNotice, output);
        Assert.Contains(KilledE2b, output);
        Assert.True(output.IndexOf(KilledE2b, StringComparison.Ordinal) > output.IndexOf(ChatScreen.CancelledNotice, StringComparison.Ordinal));   // after the reply wound down
        Assert.Equal(1, embedded.Kills);
        Assert.Null(_session.Assistant);
    }

    [Fact]
    public async Task KillSwitch_InAPane_UnloadsAndLeavesThePaneOpen()
    {
        var embedded = OnTheEmbeddedE2b();
        UsePane();
        var input = WhenIdle(
            i => PushLine(i, "/help"),
            i => i.Push(Keys.CtrlAlt(ConsoleKey.X), Keys.Escape),   // in /help: ESC closes it, still open after the kill
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Equal(1, embedded.Kills);
        Assert.Contains(KilledE2b, output);
        Assert.Null(_session.Assistant);
    }

    [Fact]
    public async Task KillSwitch_UnderALoad_CancelsTheConnect_AndUnloads()
    {
        var embedded = OnTheEmbeddedE2b();
        embedded.StartGate = async ct =>
        {
            _console.Input.PushKey(Keys.CtrlAlt(ConsoleKey.X));
            await Task.Delay(Timeout.Infinite, ct);
        };
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(1, embedded.Kills);
        Assert.Null(embedded.Running);
        Assert.Contains(KilledE2b, output);
        Assert.Null(_session.Assistant);
    }
}
