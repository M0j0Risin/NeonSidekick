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

        Assert.Contains("[notice] " + HeadlessText.FailedSummary([(HeadlessText.AMessage, "No LLM server. Set NEONSIDEKICK_LLM_URL and restart.", 1)]), output);
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

        Assert.Equal([("linear", HeadlessText.NeedsSignIn, 1), ("/ha", "no such entity", 1)], failures.Entries);
        Assert.Equal("2 failed: linear (needs a sign-in), /ha (no such entity)", failures.SummaryLine());
        int mark = failures.Mark();
        failures.NextLine();
        failures.Add(HeadlessText.AMessage, "Model error");
        failures.AddSignIn("notion");   // during the pass: the pass run again can't sign in either (2026-11-22, the review's #1)
        failures.BackTo(mark);   // the pass ran again
        Assert.Equal([("linear", HeadlessText.NeedsSignIn, 1), ("/ha", "no such entity", 1), ("notion", HeadlessText.NeedsSignIn, 1)], failures.Entries);
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

        var (text, notice) = SidekickApp.HeadlessWithPiped("look", SidekickApp.PipedText.Of(piped), roomTokens: null);

        Assert.NotNull(notice);
        Assert.True(text.Length < SidekickApp.PipedUnknownWindowChars + 1_000, text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("start", text);
        Assert.Contains("end\n```", text);
        Assert.Contains("(the piped input was cut to fit: ", text);
        var (small, none) = SidekickApp.HeadlessWithPiped("look", SidekickApp.PipedText.Of(piped), roomTokens: 1_000_000);   // 1.5M chars of room: whole
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
        Assert.Contains("[notice] " + HeadlessText.NoPipedInput(SidekickApp.PipedFirstByteWait, OperatingSystem.IsWindows()), stderr);
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

    // ── The review's fixes (2026-11-22, section 7) ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prompt_TakesAValueStartingWithADash_AsGiven_AndOnlyAFlagIsRefused()
    {
        Assert.Equal("- list the files", SidekickOptions.Parse(["--prompt", "- list the files"]).Prompt);   // #44
        Assert.Equal("-notes.txt", SidekickOptions.Parse(["--prompt-file", "-notes.txt"]).PromptFile is { } file ? Path.GetFileName(file) : null);
        Assert.Equal("  keep it \n", SidekickOptions.Parse(["--prompt=  keep it \n"]).Prompt);   // #143: untrimmed
        Assert.NotNull(SidekickOptions.Parse(["--prompt", "--quiet"]).Error);
        Assert.NotNull(SidekickOptions.Parse(["--prompt", "-h"]).Error);
        Assert.NotNull(SidekickOptions.Parse(["--prompt", "   "]).Error);
        Assert.NotNull(SidekickOptions.Parse(["--cwd", "-x"]).Error);   // another flag's value keeps the old rule
    }

    [Fact]
    public async Task Prompt_KeepsItsIndentation_AndAnUnknownSlashWord_TakesThePipe()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Indented.").EnqueueText("A path.");

        await OneShot(Prompt("  - first\n    - nested\n"));
        Assert.Equal("  - first\n    - nested", _chat.Requests[^1].Last(m => m.Role == ChatRole.User).Text);   // #143

        var (_, stderr) = await OneShot(Prompt("/Users/me/notes.txt what is in it?"), stdin: "the notes", redirected: true);   // #28
        string sent = _chat.Requests[^1].Last(m => m.Role == ChatRole.User).Text!;
        Assert.StartsWith("/Users/me/notes.txt what is in it?\n\n" + HeadlessText.PipedContextHeading, sent);
        Assert.Contains("the notes", sent);
        Assert.DoesNotContain("The piped input was left out", stderr);
    }

    [Fact]
    public async Task Prompt_WithNoServer_AndAPipe_NeverCallsTheMessageACommand()
    {
        var (_, stderr) = await OneShot(Prompt("hello there"), stdin: "context", redirected: true, exitCode: SidekickApp.HeadlessFailedExitCode);   // #11

        Assert.DoesNotContain("The piped input was left out", stderr);
    }

    [Fact]
    public async Task Prompt_TheRowAndItsTitle_KeepThePromptAlone_NeverThePipe()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Summed.");

        await OneShot(Prompt("sum this up"), stdin: "zebracorn appears only in the pipe", redirected: true);   // #106

        using var store = new SessionStore(_settings.ProfileDirectory);
        var row = Assert.Single(store.List(5));
        Assert.Equal(SessionText.FirstLineTitle("sum this up"), row.Title);
        Assert.Empty(store.Search("zebracorn", 5));
        Assert.Contains("zebracorn", _chat.Requests[^1].Last(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task APipePastTheCeiling_IsReadInPieces_AndTheNoteCountsAllOfIt()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Long.");
        string piped = "A" + new string('x', 2_500_000) + "Z";

        await OneShot(Prompt("how long?"), stdin: piped, redirected: true);   // #68

        string sent = _chat.Requests[^1].Last(m => m.Role == ChatRole.User).Text!;
        Assert.Contains(" of 2,500,002 characters", sent);
        Assert.Contains("\nA", sent);
        Assert.Contains("xZ\n", sent);
        Assert.True(sent.Length < SidekickApp.PipedUnknownWindowChars + 1_000, sent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void APipeReadInPart_IsCutFromItsTwoEnds_NeverAcrossTheGap()
    {
        var piped = new SidekickApp.PipedText(new string('a', 1_000), new string('z', 1_000) + "\n", 3_000_000);

        var (text, notice) = SidekickApp.HeadlessWithPiped("look", piped, roomTokens: null);

        Assert.Equal(HeadlessText.PipedCutNotice(2_000, 3_000_000), notice);
        Assert.Contains(new string('a', 1_000) + "\n" + NeonSidekick.Llm.TurnText.CutMarker(2_998_000) + "\n" + new string('z', 1_000) + "\n```", text);
        Assert.Contains(HeadlessText.PipedCutNote(2_000, 3_000_000), text);
    }

    [Fact]
    public void APipe_IsCutToHalfTheRoomLeft_NotHalfTheWindow()
    {
        var piped = SidekickApp.PipedText.Of(new string('x', 150_000));
        long perToken = NeonSidekick.Llm.ConversationCompactor.GuardCharsPerToken;

        var (some, notice) = SidekickApp.HeadlessWithPiped("look", piped, roomTokens: 10_000);   // #127
        Assert.NotNull(notice);
        Assert.True(some.Length < 10_000 * perToken / 2 + 1_000, some.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var (least, _) = SidekickApp.HeadlessWithPiped("look", piped, roomTokens: 0);   // a full window still keeps a little
        Assert.True(least.Length < SidekickApp.PipedFloorChars + 1_000 && least.Length > SidekickApp.PipedFloorChars / 2);
    }

    [Fact]
    public void NoPipedInput_NamesTheWait_AndThisSystemsEmptyInput()
    {
        Assert.Contains("within 3 seconds", HeadlessText.NoPipedInput(TimeSpan.FromSeconds(3), windows: true), StringComparison.Ordinal);   // #51
        Assert.EndsWith("< NUL.", HeadlessText.NoPipedInput(TimeSpan.FromSeconds(3), windows: true), StringComparison.Ordinal);
        Assert.EndsWith("< /dev/null.", HeadlessText.NoPipedInput(TimeSpan.FromSeconds(3), windows: false), StringComparison.Ordinal);
    }

    /// <summary>A stdin whose pipe broke.</summary>
    private sealed class BrokenReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("the pipe broke"));
    }

    [Fact]
    public async Task ABrokenPipe_IsABadStart_Exit2_NeverTheCrashHandler()
    {
        var (_, stderr) = await OneShot(Prompt("hi"), redirected: true, reader: new BrokenReader(), exitCode: 2);   // #135

        Assert.Contains("[error] " + HeadlessText.PipedUnreadable("the pipe broke"), stderr);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Session_WithAStoreThatWontOpen_SaysSo_Exit2()
    {
        string path;
        using (var probe = new SessionStore(_settings.ProfileDirectory))
        {
            path = probe.FilePath;
        }

        Directory.CreateDirectory(path);   // a folder where the file goes: SQLite can't open it (#62)

        var (_, stderr) = await OneShot(Prompt("and?") with { SessionId = 1 }, exitCode: 2);

        Assert.Contains("[error] " + HeadlessText.SessionsUnavailable, stderr);
        Assert.DoesNotContain(HeadlessText.NoSuchSession(1), stderr);
    }

    [Fact]
    public async Task Session_WhoseFolderIsTheOtherSystems_StaysInThisOne()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Here.");
        string folder = OperatingSystem.IsWindows() ? "/Users/x/Repo" : @"C:\x\Repo";
        long id = SeedHeadlessSession(folder);

        var (_, stderr) = await OneShot(Prompt("where?") with { SessionId = id });   // #82

        Assert.Contains("[notice] " + HeadlessText.SessionFolderGone(folder), stderr);
        Assert.DoesNotContain(HeadlessText.SessionFolder(folder), stderr);
    }

    [Fact]
    public async Task Session_WithLoggingOff_LeavesTheRowAsItWas()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Not saved.");
        long id = SeedHeadlessSession(Path.Combine(_dir, "work"));
        _settings.Update(d => d.SessionLogging = false);

        var (_, stderr) = await OneShot(Prompt("more") with { SessionId = id });   // #37

        Assert.Contains("[notice] " + HeadlessText.SessionNotSaved, stderr);
        using var store = new SessionStore(_settings.ProfileDirectory);
        Assert.Equal(1, store.Load(id)!.Summary.Turns);
        Assert.DoesNotContain("[notice] session #", stderr);
    }

    [Fact]
    public async Task Session_DropsTheMadeFiles_OfTurnsItNoLongerHas()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Kept.");
        string folder = Path.Combine(_dir, "work");
        Directory.CreateDirectory(folder);
        long id = SeedHeadlessSession(folder);
        using (var seed = new SessionStore(_settings.ProfileDirectory))
        {
            var history = new NeonSidekick.Llm.ConversationHistory("");
            history.AddUser("the code word is heron");
            history.AddAssistant("Noted.");
            seed.SaveHistory(id, SessionHistory.ToJson(history.Messages, madeFiles:
                [new NeonSidekick.Files.StoredMadeFile(Path.Combine(folder, "made.txt"), 1), new NeonSidekick.Files.StoredMadeFile(Path.Combine(folder, "rewound.txt"), 4)]));
        }

        await OneShot(Prompt("and?") with { SessionId = id });   // #69: turn 4 was rewound away

        using var store = new SessionStore(_settings.ProfileDirectory);
        SessionHistory.FromJsonWhole(store.Load(id)!.HistoryJson, out var stored);
        Assert.Equal(Path.Combine(folder, "made.txt"), Assert.Single(stored.MadeFiles!).Path);
    }

    [Fact]
    public async Task ASessionSaveThatFails_FailsTheRun_Exit4_NamingTheSession()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("Answered.");
        long id = SeedHeadlessSession(Path.Combine(_dir, "work"));
        string path;
        using (var probe = new SessionStore(_settings.ProfileDirectory))
        {
            path = probe.FilePath;
        }

        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            raw.Open();
            using var trigger = raw.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER no_save BEFORE UPDATE OF history_json ON sessions BEGIN SELECT RAISE(ABORT, 'disk on fire'); END;";
            trigger.ExecuteNonQuery();
        }

        var (stdout, stderr) = await OneShot(Prompt("more") with { SessionId = id }, exitCode: SidekickApp.HeadlessFailedExitCode);   // #125

        Assert.Equal("Answered." + Environment.NewLine, stdout);
        Assert.Contains(" failed: " + HeadlessText.TheSession + " (not saved: ", stderr);
        Assert.Contains("disk on fire", stderr);
    }

    [Fact]
    public async Task TheRowsFigures_AreTheWholeTurns_EveryRequestOfIt()
    {
        ServerOn1234("llama");
        _chat.Enqueue(FakeChatClient.Call("c1", "get_working_directory", new Dictionary<string, object?>()), FakeChatClient.Usage(100, 10));
        _chat.Enqueue(FakeChatClient.Text("Done."), FakeChatClient.Usage(150, 20));

        await OneShot(Prompt("where am I?"));   // #21, #22

        using var store = new SessionStore(_settings.ProfileDirectory);
        Assert.Equal(30, Assert.Single(store.ModelStats()).Output);
    }
}
