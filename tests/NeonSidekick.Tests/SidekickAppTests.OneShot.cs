using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Headless one-shot runs and exit 4 (2026-11-21, feature ideas #41 to #44): <c>--prompt</c> and <c>--prompt-file</c> print the reply alone on
/// stdout, piped stdin joins a message as context, <c>--quiet</c> splits the writers, <c>--session</c> carries a stored row on, and a run in which
/// a message or a command failed exits 4, which beats 3.
/// </summary>
public partial class SidekickAppTests
{
    /// <summary>A headless run with its own stderr: the two writers' text.</summary>
    private async Task<(string Out, string Err)> OneShot(SidekickOptions options, string stdin = "", bool redirected = false, int exitCode = 0, TextReader? reader = null, TimeProvider? time = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = App(stdin: reader ?? new StringReader(stdin), stdout: stdout, stderr: stderr, redirected: redirected, time: time);
        app.HeadlessPassRetryWaitForTests = TimeSpan.Zero;
        int code = await app.RunAsync(options with { Headless = true }, CancellationToken.None);
        if (code != exitCode)
        {
            Assert.Fail($"Exit {code}, not {exitCode}: {stdout} || {stderr}");
        }

        return (stdout.ToString(), stderr.ToString());
    }

    private static SidekickOptions Prompt(string text) => SidekickOptions.None with { Prompt = text, Headless = true };

    [Fact]
    public async Task Headless_AMessageWithNoServer_Exits4_WithTheSummary_ACommandsOnlyRunExits0()
    {
        string output = await Headless("/clear\nhello\n", exitCode: SidekickApp.HeadlessFailedExitCode);

        Assert.Contains("[notice] " + HeadlessText.FailedSummary([(HeadlessText.AMessage, "No LLM server. Set NEONSIDEKICK_LLM_URL and restart.")]), output);
        Assert.Equal(4, SidekickApp.HeadlessFailedExitCode);

        string commands = await Headless("/clear\n/new\n");   // nothing needed a server: 0
        Assert.DoesNotContain(" failed: ", commands);
    }

    [WindowsFact]
    public async Task Headless_AFailureBeatsARefusal_4Over3()
    {
        ServerOn1234("llama");
        _chat.Enqueue(FakeChatClient.Call("c1", "run_command", new Dictionary<string, object?> { ["command"] = "echo hi", ["shell"] = "cmd" }));
        _chat.EnqueueText("Then not.");

        string output = await Headless("run it\n/update\n", exitCode: SidekickApp.HeadlessFailedExitCode);

        Assert.Contains("[notice] " + NeonSidekick.Shell.ShellText.RefusedSummary(["echo hi"]), output);
        Assert.Contains("[notice] 1 failed: /update (", output);
    }

    [Fact]
    public void Headless_ACallToAServerThatNeedsASignIn_IsAFailure_OnceAServer()
    {
        var failures = new HeadlessFailures();
        failures.NextLine();
        failures.AddSignIn("linear");
        failures.NextLine();
        failures.AddSignIn("linear");
        failures.Add("/ha", "no such entity");

        Assert.Equal([("linear", HeadlessText.NeedsSignIn), ("/ha", "no such entity")], failures.Entries);
        Assert.Equal("2 failed: linear (needs a sign-in), /ha (no such entity)", failures.SummaryLine());
        int mark = failures.Mark();
        failures.NextLine();
        failures.Add(HeadlessText.AMessage, "Model error");
        failures.BackTo(mark);   // the pass ran again
        Assert.Equal(2, failures.Entries.Count);
        Assert.Null(new HeadlessFailures().SummaryLine());
    }

    [Fact]
    public async Task Prompt_PrintsTheReplyAlone_OnStdout_AndTheBannerOnStderr()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Paris.");

        var (stdout, stderr) = await OneShot(Prompt("capital of France?"));

        Assert.Equal("Paris." + Environment.NewLine, stdout);
        Assert.Contains(SidekickApp.HeadlessModeLine, stderr);
        Assert.DoesNotContain("You: ", stderr + stdout);
        Assert.Equal("capital of France?", _chat.Requests[^1].Last(m => m.Role == ChatRole.User).Text);
        Assert.Matches(@"\[notice\] session #\d+", stderr);   // the row it wrote, for --session
    }

    [Fact]
    public async Task Prompt_WithNoServer_Exits4()
    {
        var (stdout, stderr) = await OneShot(Prompt("hello"), exitCode: SidekickApp.HeadlessFailedExitCode);

        Assert.Equal("", stdout);
        Assert.Contains(" failed: a message (", stderr);
    }

    [Fact]
    public async Task Prompt_PipedStdin_JoinsTheMessage_InAFenceLongerThanItsBackticks()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Two lines.");

        await OneShot(Prompt("summarize this"), stdin: "line one\n```\nline two\n", redirected: true);

        string sent = _chat.Requests[^1].Last(m => m.Role == ChatRole.User).Text!;
        Assert.Equal("summarize this\n\n" + HeadlessText.PipedContextHeading + "\n````\nline one\n```\nline two\n````", sent);
    }

    [Fact]
    public void PipedContext_PastTheCap_IsCutHeadAndTail_WithANote()
    {
        string piped = "start\n" + new string('x', 150_000) + "\nend";

        var (text, notice) = SidekickApp.HeadlessWithPiped("look", piped, windowTokens: null);

        Assert.NotNull(notice);
        Assert.True(text.Length < SidekickApp.PipedUnknownWindowChars + 1_000, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("start", text);
        Assert.Contains("end\n```", text);
        Assert.Contains("(the piped input was cut to fit: ", text);
        var (small, none) = SidekickApp.HeadlessWithPiped("look", piped, windowTokens: 1_000_000);   // 1.5M chars of room: whole
        Assert.Null(none);
        Assert.Contains(new string('x', 150_000), small);
    }

    [Fact]
    public async Task Prompt_ASilentPipe_RunsThePromptAlone_AfterTheWait()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Alone.");
        var time = new ManualTimeProvider();
        var run = OneShot(Prompt("hi"), redirected: true, reader: new SilentReader(), time: time);
        for (int i = 0; i < 200 && !run.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        var (stdout, stderr) = await run;

        Assert.Equal("Alone." + Environment.NewLine, stdout);
        Assert.Contains("[notice] " + HeadlessText.NoPipedInput, stderr);
        Assert.Equal("hi", _chat.Requests[^1].Last(m => m.Role == ChatRole.User).Text);
    }

    /// <summary>A stdin redirected and never written to.</summary>
    private sealed class SilentReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => 0, TaskScheduler.Default));
    }

    [Fact]
    public async Task Prompt_ACommand_LeavesThePipedTextOut_WithTheNotice()
    {
        var (stdout, stderr) = await OneShot(Prompt("/clear"), stdin: "ignored text", redirected: true);

        Assert.Contains("(conversation cleared)", stdout);
        Assert.Contains("[notice] " + HeadlessText.PipedInputIgnored("/clear"), stderr);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Prompt_ACustomCommand_TakesThePipedText_AfterItsExpansion()
    {
        WriteHeadlessCommand("review", "---\ndescription: review\n---\nReview this.");
        ServerOn1234("llama");
        _chat.EnqueueText("Reviewed.");

        await OneShot(Prompt("/review"), stdin: "code", redirected: true);

        Assert.Equal("Review this.\n\n" + HeadlessText.PipedContextHeading + "\n```\ncode\n```", _chat.Requests[^1].Last(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task PromptFile_IsAlwaysAMessage_EvenStartingWithACommand()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("A path.");
        string file = Path.Combine(_dir, "ask.md");
        File.WriteAllText(file, "﻿/plan is a word here\nsecond line", new System.Text.UTF8Encoding(false));

        var (stdout, _) = await OneShot(SidekickOptions.None with { PromptFile = file, Headless = true });

        Assert.Equal("A path." + Environment.NewLine, stdout);
        Assert.Equal("/plan is a word here\nsecond line", _chat.Requests[^1].Last(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task PromptFile_Missing_OrBlank_IsExit2_WithNothingRun()
    {
        var (_, missing) = await OneShot(SidekickOptions.None with { PromptFile = Path.Combine(_dir, "nope.md"), Headless = true }, exitCode: 2);
        Assert.Contains("[error] --prompt-file ", missing);

        string blank = Path.Combine(_dir, "blank.md");
        File.WriteAllText(blank, "  \n");
        var (_, err) = await OneShot(SidekickOptions.None with { PromptFile = blank, Headless = true }, exitCode: 2);
        Assert.Contains("[error] " + HeadlessText.BlankPrompt, err);
        await OneShot(Prompt("   "), exitCode: 2);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Quiet_PutsOnlyTheReplies_OnStdout_ABlankLineBetween()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("One.").EnqueueText("Two.");

        var (stdout, stderr) = await OneShot(SidekickOptions.None with { Quiet = true }, stdin: "first\nsecond\n");

        string nl = Environment.NewLine;
        Assert.Equal("One." + nl + nl + "Two." + nl, stdout);
        Assert.Contains("You: ", stderr);
        Assert.Contains(SidekickApp.HeadlessModeLine, stderr);
    }

    private long SeedHeadlessSession(string folder, string? mark = null)
    {
        using var store = new SessionStore(_settings.ProfileDirectory);
        long id = store.Begin("earlier", "llama", folder)!.Value;
        store.AppendTurn(id, "the code word is heron", "Noted.", 0, [], [], 0, 3, 2, false, cwd: folder);
        var history = new NeonSidekick.Llm.ConversationHistory("");
        history.AddUser("the code word is heron");
        history.AddAssistant("Noted.");
        store.SaveHistory(id, SessionHistory.ToJson(history.Messages, mark: mark, madeFiles: [new NeonSidekick.Files.StoredMadeFile(Path.Combine(folder, "made.txt"), 1)]));
        return id;
    }

    [Fact]
    public async Task Session_CarriesTheRowOn_InItsFolder_KeepingItsMarkAndMadeFiles()
    {
        ServerOn1234("llama");
        string folder = Path.Combine(_dir, "work");
        Directory.CreateDirectory(folder);
        long id = SeedHeadlessSession(folder, mark: "before-refactor");
        _chat.Enqueue(FakeChatClient.Call("c1", "get_working_directory", new Dictionary<string, object?>())).EnqueueText("heron.");

        var (stdout, stderr) = await OneShot(Prompt("what was the code word?") with { SessionId = id });

        Assert.Equal("heron." + Environment.NewLine, stdout);
        Assert.Contains(_chat.Requests[0], m => m.Text == "the code word is heron");
        Assert.Contains("[notice] " + HeadlessText.SessionResumed(id, 1), stderr);
        Assert.Contains("[notice] " + HeadlessText.SessionFolder(folder), stderr);
        Assert.Contains(folder, stderr);   // get_working_directory's answer names the session's folder
        Assert.Contains("[notice] session #" + id, stderr);
        using var store = new SessionStore(_settings.ProfileDirectory);
        var record = store.Load(id)!;
        Assert.Equal(2, record.Summary.Turns);
        SessionHistory.FromJsonWhole(record.HistoryJson, out var stored);
        Assert.Equal("before-refactor", stored.Mark);
        Assert.Equal(Path.Combine(folder, "made.txt"), Assert.Single(stored.MadeFiles!).Path);
        Assert.Equal("", _settings.Current.WorkingDirectory);   // the folder was this run's, never saved
    }

    [Fact]
    public async Task Session_Last_IsTheNewestRow_AndAnUnknownIdIsExit2()
    {
        ServerOn1234("llama");
        SeedHeadlessSession(Path.Combine(_dir, "gone"));
        long newest = SeedHeadlessSession(Path.Combine(_dir, "gone2"));
        _chat.EnqueueText("Again.");

        var (_, stderr) = await OneShot(Prompt("and?") with { SessionLast = true });

        Assert.Contains("[notice] " + HeadlessText.SessionResumed(newest, 1), stderr);
        Assert.Contains("[notice] " + HeadlessText.SessionFolderGone(Path.Combine(_dir, "gone2")), stderr);
        var (_, unknown) = await OneShot(Prompt("and?") with { SessionId = 999 }, exitCode: 2);
        Assert.Contains("[error] " + HeadlessText.NoSuchSession(999), unknown);
        Assert.Single(_chat.Requests);
    }
}
