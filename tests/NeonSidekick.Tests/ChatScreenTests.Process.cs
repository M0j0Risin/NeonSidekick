using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/process</c> on the screen (2026-10-05): the list (none yet, then a started process's row), the window's seam called with the
/// session an id prefix names, the stop it is handed ending in the chat's "stopped by you" line, the errors, and the argument list.
/// Real <c>cmd.exe</c> children started by the model's <c>run_command</c> under yolo, as the shell tests do.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task Process_WithNone_SaysSo_AndAnUnknownIdOrTwoWordsAreErrors()
    {
        int opened = 0;
        _openProcessWindow = (_, _) => opened++;
        PushLine("/process");
        PushLine("/process proc_9");
        PushLine("/process a b");
        PushLine("/help");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ProcessWindowText.NoneYet, output);
        Assert.Contains("  ✗ " + ProcessWindowText.NoMatchError("proc_9"), output);
        Assert.Contains("  ✗ " + ProcessWindowText.UsageError("a b"), output);
        Assert.Equal(0, opened);
        Assert.Contains(SlashCommands.HelpEntries.Single(e => e.Command == "/process").Summary, output);
        Assert.Contains(ChatScreen.CommandItems(), i => i.Text == "/process");
        Assert.Empty(_chat.Requests);
    }

    /// <summary>The model starts a quiet background child; then the script runs <paramref name="lines"/> at the idle line, one each wait.</summary>
    private void ProcessFixture(Func<int, string?> lines)
    {
        _settings.Update(d => { d.TtsOutput = false; d.ShellCommandPolicy = "yolo"; });
        _chat.Enqueue(FakeChatClient.Call("c1", RunCommandTool.ToolName, new Dictionary<string, object?> { ["command"] = "ping -n 30 127.0.0.1 >nul", ["shell"] = "cmd", ["background"] = true }));
        _chat.EnqueueText("Started.");
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                return;
            }

            string? line = step == 0 ? "start it" : lines(step);
            if (line is null)
            {
                return;   // waiting for something on the screen
            }

            step++;
            PushLine(input, line);
        };
    }

    [Fact]
    public async Task Process_ListsAStartedOne_OpensItsWindow_AndItsStopIsTheChatsLine()
    {
        ProcessSession? shown = null;
        _openProcessWindow = (session, stop) =>
        {
            shown = session;
            stop(session);   // Ctrl+K twice in the window, at once
        };
        ProcessFixture(step => step switch
        {
            1 => "/process",
            2 => "/process PROC_",   // the one session's unique prefix, any case
            // The kill lands, the registry's signal ends the read and the alert prints; then the list again shows it stopped.
            3 => Output.Contains("was stopped by you", StringComparison.Ordinal) ? "/process" : null,
            4 => "/exit",
            _ => null,
        });

        string output = await RunAsync();

        Assert.NotNull(shown);
        string id = shown!.Id;
        Assert.Contains("1 process (1 running)", output);
        Assert.Contains(id + "  running ", output);
        Assert.Contains(ProcessWindowText.ListHint, output);
        Assert.Contains("  · " + ProcessWindowText.OpenedNotice(id), output);
        Assert.Contains("  ⚡ " + id + " was stopped by you after ", output);
        Assert.Contains(id + "  stopped by you ", output);
        Assert.True(shown.StoppedByUser);
    }

    [Fact]
    public async Task Process_WithNoWindowHere_SaysSo()
    {
        ProcessFixture(step => step switch
        {
            1 => "/process proc_",
            2 => "/exit",
            _ => null,
        });

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ProcessWindowText.Unavailable, output);
    }

    [Fact]
    public async Task Process_AWindowThatFails_IsItsError()
    {
        _openProcessWindow = (_, _) => throw new InvalidOperationException("no thread");
        ProcessFixture(step => step switch
        {
            1 => "/process proc_",
            2 => "/exit",
            _ => null,
        });

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ProcessWindowText.WindowFailedError("no thread"), output);
        Assert.DoesNotContain("in the process window", output);
    }

    [Fact]
    public void Process_ArgumentList_OffersTheIds_WithTheirNotes()
    {
        var sources = new ChatScreen.ArgumentSources(() => [], "default", [], _ => [], _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false), _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false),
            Processes: () => [new("proc_3f2a1b", "running · npm run dev"), new("proc_aa0001", "exited 0 · npm test")]);

        Assert.Equal(["proc_3f2a1b", "proc_aa0001"], ChatScreen.ArgumentItems("/process", "", sources).Select(i => i.Text));
        var one = Assert.Single(ChatScreen.ArgumentItems("/process", "proc_3", sources));
        Assert.Equal(new CompletionItem("proc_3f2a1b", "running · npm run dev"), one);
        Assert.Empty(ChatScreen.ArgumentItems("/process", "proc_3f2a1b x", sources));
        Assert.Empty(ChatScreen.ArgumentItems("/process", "", sources with { Processes = null }));
    }
}
