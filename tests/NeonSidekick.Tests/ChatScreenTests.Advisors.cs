using Microsoft.Extensions.AI;
using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>neon_advisor</c> on the screen (2026-10-10, the profile advisors): a profile with a blank URL borrows this chat's server, so the
/// screen's <see cref="FakeChatClient"/> answers for the advisor too — the advisor's request sits between the model's two.
/// </summary>
public partial class ChatScreenTests
{
    private static Dictionary<string, object?> NeonAdvisorArgs(string advisor, string question) =>
        new() { [NeonAdvisorTool.AdvisorArgument] = advisor, [NeonAdvisorTool.QuestionArgument] = question };

    // The dba wears no avatar unless one is given (a fresh AppSettingsData wears 🤖), so its lines keep 🛠️.
    private void AdvisorProfiles(bool on = true, bool confirm = false, string avatar = "")
    {
        Profiles.Create(_settings.StorageDirectory, "dba", new AppSettingsData { ModelAvatar = avatar });
        _settings.Update(d =>
        {
            d.TtsOutput = false;
            d.Advisors = on;
            d.AdvisorConfirm = confirm;
            d.AdvisorList = [new AdvisorEntry("dba", "database architect")];
        });
    }

    [Fact]
    public async Task Advisors_AreOfferedOnlyWithTheSwitchAndAList_WithTheirRule()
    {
        AdvisorProfiles(on: false);
        _chat.EnqueueText("one");
        PushLine("hello");
        PushLine("/exit");
        await RunAsync();

        Assert.DoesNotContain(_chat.Options[0]!.Tools!.Cast<AIFunction>(), t => t.Name == NeonAdvisorTool.ToolName);
        Assert.DoesNotContain(Assistant.NeonAdvisorRule, _chat.Requests[0][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Advisors_ACall_ShowsTheQuestion_TheAnswerAndTheFooter_TheModelReadsIt_AndTheThreadIsStored()
    {
        AdvisorProfiles();
        _chat.Enqueue(FakeChatClient.Call("c1", NeonAdvisorTool.ToolName, NeonAdvisorArgs("dba", "Which index?")));
        _chat.EnqueueText("Index the foreign key.");   // the advisor, on the borrowed server
        _chat.EnqueueText("I'll add it.");
        PushLine("speed up the joins");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(NeonAdvisorTool.ToolName, _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name));
        Assert.Contains(" " + Assistant.NeonAdvisorRule, _chat.Requests[0][0].Text, StringComparison.Ordinal);
        Assert.Contains(AdvisorText.Framing(_settings.ProfileName, "database architect", tools: true), _chat.Requests[1][0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(_chat.Options[1]!.Tools!.Cast<AIFunction>(), t => t.Name is NeonAdvisorTool.ToolName or AskUserTool.ToolName or "write_file");
        Assert.Contains("🛠️ " + AdvisorText.QuestionNote(new AdvisorEntry("dba", "database architect"), "Which index?") + "\n", output);
        Assert.Contains(TranscriptRenderer.ToolAnswerIndent + "Index the foreign key.\n", output);
        Assert.Contains("🛠️ dba · ", output);
        Assert.DoesNotContain("🛠️ " + NeonAdvisorTool.ToolName, output);   // the view's lines stand for the call's and the result's
        Assert.Equal("Index the foreign key.", ToolResult(_chat.Requests[2], "c1"));
        Assert.Contains("I'll add it.", output);

        using var store = new SessionStore(_settings.ProfileDirectory, _time);
        var record = store.Load(Assert.Single(store.List(10)).Id)!;
        var threads = SessionHistory.AdvisorThreadsOf(record.HistoryJson);
        Assert.Equal([new AdvisorPair("Which index?", "Index the foreign key.")], threads!["dba"]);
    }

    [Fact]
    public async Task Advisors_TheQuestionAndTheFooter_WearTheAdvisorsAvatar_ItsToolLinesKeepTheTools()
    {
        AdvisorProfiles(avatar: "🦉");
        _chat.Enqueue(FakeChatClient.Call("c1", NeonAdvisorTool.ToolName, NeonAdvisorArgs("dba", "Which index?")));
        _chat.Enqueue(FakeChatClient.Call("a1", "read_file", new Dictionary<string, object?> { ["path"] = "schema.sql" }));   // the advisor reads a file first
        _chat.EnqueueText("Index the foreign key.");
        _chat.EnqueueText("I'll add it.");
        PushLine("speed up the joins");
        PushLine("/exit");

        string output = await RunAsync();

        string question = AdvisorText.QuestionNote(new AdvisorEntry("dba", "database architect"), "Which index?");
        Assert.Contains("🦉 " + question + "\n", output);
        Assert.DoesNotContain("🛠️ " + question, output);
        Assert.Contains("🦉 dba · ", output);
        Assert.Contains("🛠️ dba › read_file", output);
    }

    [Fact]
    public async Task Advisors_NewForgetsTheThreads()
    {
        AdvisorProfiles();
        _chat.Enqueue(FakeChatClient.Call("c1", NeonAdvisorTool.ToolName, NeonAdvisorArgs("dba", "first?")));
        _chat.EnqueueText("advice one");
        _chat.EnqueueText("ok");
        _chat.Enqueue(FakeChatClient.Call("c2", NeonAdvisorTool.ToolName, NeonAdvisorArgs("dba", "second?")));
        _chat.EnqueueText("advice two");
        _chat.EnqueueText("ok again");
        PushLine("ask it");
        PushLine("/new");
        PushLine("ask again");
        PushLine("/exit");
        await RunAsync();

        Assert.DoesNotContain(_chat.Requests[4], m => m.Text.Contains("first?", StringComparison.Ordinal));   // the advisor's second request: no first pair
    }

    [Fact]
    public async Task Advisors_WithConfirmOn_NoPane_AreRefused()
    {
        AdvisorProfiles(confirm: true);
        _chat.Enqueue(FakeChatClient.Call("c1", NeonAdvisorTool.ToolName, NeonAdvisorArgs("dba", "q?")));
        _chat.EnqueueText("fine, alone then");
        PushLine("ask it");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(AdvisorText.NotAskedError, ToolResult(_chat.Requests[1], "c1"));
        Assert.Contains("✗  Error: neon_advisor needs the user's yes", output);
    }

    [Fact]
    public async Task AdvisorCommand_SendsTheRecentMessages_EvenUnderBrief()
    {
        AdvisorProfiles(on: false);
        _settings.Update(d => d.AdvisorContext = AdvisorContextMode.Brief);   // the model's calls' setting, not /advisor's (2026-10-10, the user's call)
        _chat.EnqueueText("Postgres 16.");
        _chat.EnqueueText("Index the foreign key.");   // the advisor, on the borrowed server
        PushLine("which database do we run?");
        PushLine("/advisor dba Which index?");
        PushLine("/exit");

        await RunAsync();

        string prompt = _chat.Requests[1][^1].Text;
        Assert.StartsWith("Question: Which index?\n\nIts conversation so far", prompt, StringComparison.Ordinal);
        Assert.Contains("\nassistant: Postgres 16.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvisorCommand_Lists_Asks_AndThePairJoinsTheHistory_SwitchOffOrNot()
    {
        AdvisorProfiles(on: false);
        _chat.EnqueueText("Index the foreign key.");   // the advisor, on the borrowed server
        _chat.EnqueueText("ok");
        PushLine("/advisor");
        PushLine("/advisor dba Which index?");
        PushLine("/advisor nobody What now?");
        PushLine("/advisor dba");
        PushLine("what did dba say?");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(AdvisorText.ListHeading, output);
        // The model it answers on, the session's llama it borrows (2026-11-22: LinkedModelOf), in the list's columns.
        var dba = (new AdvisorEntry("dba", "database architect"), (AdvisorServerFacts?)new AdvisorServerFacts(AdvisorServer.Scan, "llama"), true);
        Assert.Contains(AdvisorText.ListLines([dba])[0], output);
        Assert.Contains("  dba — database architect · scan · llama", output);
        Assert.Contains(TranscriptRenderer.ToolAnswerIndent + "Index the foreign key.\n", output);
        Assert.Contains("\"nobody\" names no single advisor", output);
        Assert.Contains(AdvisorText.UsageError, output);
        var last = _chat.Requests[^1];
        Assert.Contains(last, m => m.Role == ChatRole.User && m.Text == AdvisorText.HistoryUser("dba", "Which index?"));
        Assert.Contains(last, m => m.Role == ChatRole.Assistant && m.Text == AdvisorText.HistoryReply("dba", "Index the foreign key."));
    }

    [Fact]
    public void AdvisorCommand_IsRegistered_WithItsHelp()
    {
        Assert.Equal((SlashCommand.Advisor, "dba why?"), SlashCommands.Parse("/advisor dba why?"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Advisor));
        Assert.Contains(SlashCommands.HelpEntries, e => e.Command == AdvisorText.Word);
        Assert.Equal(("dba", "why not?"), AdvisorText.Split("  dba   why not? "));
        Assert.Equal(("dba", ""), AdvisorText.Split("dba"));
        Assert.Equal("x", AdvisorText.ForUser("Error: x"));
        Assert.Equal("[to dba] q", AdvisorText.HistoryUser("dba", " q "));
    }

    /// <summary><c>/advisor</c>'s argument list (2026-10-10, the user's ask): the advisors by profile name with their roles, the first word only.</summary>
    [Fact]
    public void AdvisorCommand_ArgumentList_OffersTheAdvisorNames_ThenNothingForTheQuestion()
    {
        var sources = new ChatScreen.ArgumentSources(() => [], "default", [], _ => [], _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false), _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false),
            Advisors: () => [new("dba", "database architect"), new("doug", "expert .NET solution architect"), new("pm", "")]);

        // templates beside the names (2026-11-22); new only with the templates source, which the Web UI page's snapshot leaves null.
        Assert.Equal(["dba", "doug", "pm", "templates"], ChatScreen.ArgumentItems("/advisor", "", sources).Select(i => i.Text));
        Assert.Equal(["dba", "doug"], ChatScreen.ArgumentItems("/advisor", "D", sources).Select(i => i.Text));
        Assert.Equal(new CompletionItem("doug", "expert .NET solution architect"), Assert.Single(ChatScreen.ArgumentItems("/advisor", "do", sources)));
        Assert.Empty(ChatScreen.ArgumentItems("/advisor", "dba why", sources));
        Assert.Equal(["templates"], ChatScreen.ArgumentItems("/advisor", "", sources with { Advisors = null }).Select(i => i.Text));
        Assert.Empty(ChatScreen.ArgumentItems("/advisor", "new ", sources));

        var screen = sources with { AdvisorTemplates = () => [new("dba", "database administrator"), new("qa", "QA engineer")] };
        Assert.Equal(["dba", "doug", "pm", "templates", "new"], ChatScreen.ArgumentItems("/advisor", "", screen).Select(i => i.Text));
        Assert.Equal(["new "+ "dba", "new qa"], ChatScreen.ArgumentItems("/advisor", "new ", screen).Select(i => i.Text));
        Assert.Equal(new CompletionItem("new qa", "QA engineer"), Assert.Single(ChatScreen.ArgumentItems("/advisor", "new q", screen)));
        Assert.Empty(ChatScreen.ArgumentItems("/advisor", "new qa ", screen));   // the name is free
    }

    /// <summary>
    /// <c>/advisor templates</c> and <c>/advisor new</c> (2026-11-22): with no advisor yet, new makes the first (no <c>NoAdvisorsError</c>), named
    /// after the template or as given, no switch and nothing joining the history; a taken name is refused; words after new that name no
    /// template still ask an advisor.
    /// </summary>
    [Fact]
    public async Task AdvisorCommand_TemplatesAndNew_MakeAnAdvisorFromATemplate_WithNoSwitch()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("ok");
        PushLine("/advisor templates");
        PushLine("/advisor new qa");
        PushLine("/advisor new qa reviewer");
        PushLine("/advisor new qa");
        PushLine("hello");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(AdvisorText.TemplatesHeading, output);
        Assert.Contains("dba — database administrator", output);
        var qa = AdvisorTemplates.BuiltIn.Find("qa")!;
        Assert.Equal([new AdvisorEntry("qa", qa.Role, qa.Description), new AdvisorEntry("reviewer", qa.Role, qa.Description)], _settings.Current.AdvisorList);
        Assert.Contains(AdvisorText.TakenError("qa", "qa", listed: true), output);
        Assert.Equal("neon", _settings.ProfileName);
        Assert.Same(qa, AdvisorTemplates.BuiltIn.Matching(new PersonaFile(Profiles.Directory(_settings.StorageDirectory, "reviewer")).Read()));
        Assert.DoesNotContain(_chat.Requests[^1], m => m.Text.Contains("/advisor", StringComparison.Ordinal) || m.Text.Contains("[to ", StringComparison.Ordinal));
    }

    /// <summary><c>/sys persona template</c> (2026-11-22): the list, an unknown id, a yes that writes (the next reply carries it), a no that keeps.</summary>
    [Fact]
    public async Task SysPersonaTemplate_Lists_RefusesAnUnknownId_WritesAfterAYes_AndKeepsOnANo()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("ok");
        PushLine("/sys persona template");
        PushLine("/sys persona template nope");
        PushLine("/sys persona template dba");
        _console.Input.PushKey(Keys.Down);    // Yes
        _console.Input.PushKey(Keys.Enter);
        PushLine("/sys persona template qa");
        _console.Input.PushKey(Keys.Enter);   // No is on the cursor
        PushLine("/sys persona templates please");
        PushLine("hello");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.PersonaTemplatesHeading, output);
        Assert.Contains(ChatScreen.UnknownTemplateError("nope", AdvisorTemplates.BuiltIn.Ids), output);
        Assert.Contains(ChatScreen.PersonaTemplatePrompt("dba", replacing: false), output);
        Assert.Contains(ChatScreen.PersonaTemplateNotice("dba"), output);
        Assert.Contains(ChatScreen.PersonaTemplatePrompt("qa", replacing: true), output);
        Assert.Contains(ChatScreen.KeptNotice, output);
        Assert.Contains(ChatScreen.PersonaUsageError, output);
        var dba = AdvisorTemplates.BuiltIn.Find("dba")!;
        Assert.Contains(PersonaFile.Normalize(dba.Persona), _chat.Requests[^1][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SysPersona_ArgumentList_OffersTemplate_AndTheTemplatesAfterIt_TheOtherFilesDoNot()
    {
        var sources = new ChatScreen.ArgumentSources(() => [], "default", [], _ => [], _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false), _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false),
            AdvisorTemplates: () => [new("dba", "database administrator"), new("qa", "QA engineer")]);
        Assert.Equal(["persona reset", "persona template"], ChatScreen.ArgumentItems("/sys", "persona ", sources).Select(i => i.Text));
        Assert.Equal(["persona template dba", "persona template qa"], ChatScreen.ArgumentItems("/sys", "persona template ", sources).Select(i => i.Text));
        Assert.Equal(["operata reset"], ChatScreen.ArgumentItems("/sys", "operata ", sources).Select(i => i.Text));
        Assert.Equal(["vocalia reset"], ChatScreen.ArgumentItems("/sys", "vocalia ", sources).Select(i => i.Text));
    }

    /// <summary>
    /// Under <c>/advisor</c> a chord works as under a reply (2026-10-10, the user's report: the chords and the mouse were dead while an
    /// advisor answered, every line pended for the idle line): Ctrl+H opens the help pane while the advisor is still asked, and its
    /// answer lands after.
    /// </summary>
    [Fact]
    public async Task AdvisorCommand_UnderTheAsk_CtrlH_OpensTheHelpPane_AndTheAnswerLandsAfter()
    {
        AdvisorProfiles(on: false);
        LoadOnThePane();
        _chat.EnqueueText("Index the foreign key.");   // the advisor, on the borrowed server
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = WhenIdle(
            i => PushLine(i, "/advisor dba Which index?"),
            i => { i.Push(Keys.Escape); opened.TrySetResult(); },   // the help pane's read, the advisor still asked
            i => PushLine(i, "/exit"));
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 0)
            {
                input.Push(Keys.CtrlH);
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);   // a bounded wait: a chord pended for the idle line (the bug) fails the ask, not the run
            }
        };

        string output = await RunAsync(input);

        int help = output.IndexOf(Titled(InfoPane.Title + " │ Basic · Advanced · Custom · Keys "), StringComparison.Ordinal);
        int answer = output.IndexOf(TranscriptRenderer.ToolAnswerIndent + "Index the foreign key.\n", StringComparison.Ordinal);
        Assert.True(help > 0 && answer > help, output);
        Assert.DoesNotContain("  · " + ChatScreen.CancelledNotice, output);
    }

    /// <summary>Under <c>/advisor</c> the toolbar's 📄 opens the log window while the advisor is still asked (2026-10-10): a quick act runs then, not at the idle line. The 📈's /perfbar until it was retired (2026-10-14).</summary>
    [Fact]
    public async Task AdvisorCommand_UnderTheAsk_TheToolbarsLog_OpensTheWindow_BeforeTheAnswer()
    {
        AdvisorProfiles(on: false);
        int opened = 0;
        _openLogWindow = () => opened++;
        _settings.Update(d => d.ToolbarItems = ["profile", "log"]);
        LoadOnThePane();
        _chat.EnqueueText("Index the foreign key.");
        var input = WhenIdle(
            i => PushLine(i, "/advisor dba Which index?"),
            i => PushLine(i, "/exit"));
        bool toggledUnderTheAsk = false;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 0)
            {
                input.PushClick(3, 103);   // 📄 on the toolbar: /log, a quick act run while the advisor is asked
                input.PushClick(3, 103);
                await UntilAsync(() => Output.Contains(Viewer.LogViewText.WindowOpenedNotice, StringComparison.Ordinal), ct);
                toggledUnderTheAsk = true;
            }
        };

        string output = await RunAsync(input);

        Assert.True(toggledUnderTheAsk);
        Assert.Equal(1, opened);
        Assert.Contains(TranscriptRenderer.ToolAnswerIndent + "Index the foreign key.\n", output);
    }
}
