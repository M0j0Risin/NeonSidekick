using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.UI;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class AssistantTests
{
    private static readonly LlmTimeouts Timeouts = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

    /// <summary>A vocalia.md text: there is no default voice directive since 2026-10-03, so a spoken prompt's tail is only ever a file's.</summary>
    private const string Pirate = "Speak like a pirate.";

    /// <summary>What a fresh profile sends (the web and file tools on by default): the default prompt with the web and download rules on its end. A test-side oracle since 2026-09-24; src never needed the name.</summary>
    private static readonly string DefaultWebSystemPrompt = Assistant.DefaultSystemPrompt + " " + Assistant.WebRule + " " + Assistant.DownloadRule;

    private static (FakeChatClient Client, ConversationHistory History, Assistant Assistant) Build(
        IReadOnlyList<AIFunction>? tools = null, TimeProvider? time = null, LlmTimeouts? timeouts = null, ReasoningEffort? reasoning = null)
    {
        var client = new FakeChatClient();
        var history = new ConversationHistory("sys");
        return (client, history, new Assistant(client, history, timeouts ?? Timeouts, tools, time, reasoning));
    }

    private static async Task<List<TurnEvent>> Run(Assistant assistant, string text, CancellationToken ct = default)
    {
        var events = new List<TurnEvent>();
        await foreach (var evt in assistant.RunTurnAsync(text, ct)) events.Add(evt);
        return events;
    }

    private static string[] Deltas(IEnumerable<TurnEvent> events) =>
        events.OfType<TurnEvent.TextDelta>().Select(d => d.Text).ToArray();

    [Fact]
    public async Task Deltas_StreamInOrder_AndCommitOneAssistantMessage()
    {
        var (client, history, assistant) = Build();
        client.EnqueueText("Hel", "lo");

        var events = await Run(assistant, "hi");

        Assert.Equal(new[] { "Hel", "lo" }, Deltas(events));
        Assert.Equal(2, events.Count);
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal(ChatRole.Assistant, history.Messages[1].Role);
        Assert.Equal("Hello", history.Messages[1].Text);

        var request = Assert.Single(client.Requests);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User }, request.Select(m => m.Role));
        var options = Assert.Single(client.Options)!;
        Assert.Null(options.Tools);     // empty tool list is sent as null, never []
        Assert.Null(options.Reasoning); // no level configured: the server decides
        Assert.Null(assistant.Reasoning);
    }

    [Fact]
    public async Task Reasoning_IsAskedForOnEveryRequest()
    {
        var (client, _, assistant) = Build(reasoning: ReasoningEffort.ExtraHigh);
        client.EnqueueText("ok");
        client.EnqueueText("again");

        await Run(assistant, "one");
        await Run(assistant, "two");

        Assert.Equal(ReasoningEffort.ExtraHigh, assistant.Reasoning);
        Assert.Equal(2, client.Options.Count);
        Assert.All(client.Options, o => Assert.Equal(ReasoningEffort.ExtraHigh, o!.Reasoning!.Effort));
    }

    [Fact]
    public async Task ZeroLengthDeltas_AreSkipped()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("", "a", "");
        Assert.Equal(new[] { "a" }, Deltas(await Run(assistant, "hi")));
    }

    // ── System prompt ───────────────────────────────────────────────────────

    [Fact]
    public void SystemPrompt_TextMode_IsExactlyTheDefault()
    {
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false));
        Assert.DoesNotContain("text-to-speech", Assistant.DefaultSystemPrompt);
    }

    [Fact]
    public void SystemPrompt_VoiceMode_AddsNothing_WithoutADirective_AndAppendsOneLast()
    {
        // No default voice directive since 2026-10-03 (the user's call): a spoken turn is the silent prompt unless vocalia.md has text.
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(true));
        string prompt = Assistant.SystemPrompt(true, null, voiceDirective: Pirate);
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + Pirate, prompt);
        Assert.True(prompt.LastIndexOf(Pirate, StringComparison.Ordinal) > prompt.IndexOf("Use a tool", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemPrompt_MemoryOn_SitsBetweenThePersonaAndTheVoiceDirective_TheListNotInIt()
    {
        var memories = new[] { "Their name is Chris.", "They live in Leeds." };
        string prompt = Assistant.SystemPrompt(true, memories, voiceDirective: Pirate);

        // The directive alone: the list rides the opening recall_memory pair (2026-09-17).
        Assert.StartsWith(Assistant.DefaultSystemPrompt + "\n\n" + MemoryPrompt.Directive, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(MemoryPrompt.Heading, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Their name is Chris.", prompt, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + Pirate, prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf(MemoryPrompt.Directive, StringComparison.Ordinal) < prompt.IndexOf(Pirate, StringComparison.Ordinal));
    }

    [Fact]
    public void SystemPrompt_MemoryOn_WithoutTools_CarriesTheListItself()
    {
        // No pair can carry it (LLM offer tools off), so the prompt does, under the tool-free directive.
        string prompt = Assistant.SystemPrompt(false, new[] { "Their name is Chris.", "They live in Leeds." }, tools: false);

        Assert.Equal(
            Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule + "\n\n" + MemoryPrompt.DirectiveWithoutTool + "\n\n" + MemoryPrompt.Heading + "\n- Their name is Chris.\n- They live in Leeds.",
            prompt);
    }

    [Fact]
    public void SystemPrompt_MemoryOnWithNothingStored_HasTheDirectiveOnly()
    {
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + MemoryPrompt.Directive, Assistant.SystemPrompt(false, Array.Empty<string>()));
        Assert.DoesNotContain(MemoryPrompt.Heading, Assistant.SystemPrompt(false, Array.Empty<string>()));
    }

    [Fact]
    public void SystemPrompt_MemoryOff_IsTheOldShape()
    {
        Assert.Equal(Assistant.SystemPrompt(false), Assistant.SystemPrompt(false, null));
        Assert.Equal(Assistant.SystemPrompt(true), Assistant.SystemPrompt(true, null));
        Assert.DoesNotContain("memory", Assistant.SystemPrompt(true, null));
    }

    [Fact]
    public void DefaultSystemPrompt_IsThePersonaAndTheRules_EachItsOwnBlock()
    {
        Assert.StartsWith("Reply in plain text:", Assistant.OperatingRules, StringComparison.Ordinal);
        Assert.Contains("call get_current_time", Assistant.OperatingRules, StringComparison.Ordinal);
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.OperatingRules, Assistant.DefaultSystemPrompt);
    }

    [Fact]
    public void DefaultPersona_IsTheRepositoryAsset_LfAndTrimmed_WithinThePersonaCap()
    {
        // assets/prompts/persona.md, embedded by the project file (2026-10-03): read as a persona.md is, so a seeded
        // file reads back whole and unchanged.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string asset = File.ReadAllText(Path.Combine(dir.FullName, "assets", "prompts", "persona.md"));
        Assert.Equal(PersonaFile.Normalize(asset), Assistant.DefaultPersona);
        Assert.StartsWith("You are Neon, ", Assistant.DefaultPersona, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', Assistant.DefaultPersona);
        Assert.True(Assistant.DefaultPersona.Length <= PersonaFile.MaxLength, $"{Assistant.DefaultPersona.Length} characters");
    }

    [Fact]
    public void WithoutTools_TheDefaultsLoseEveryToolSentence_AndAreBuiltFromTheSameParts()
    {
        Assert.Equal("Reply in plain text: no Markdown headings or tables, and no code fences unless the user asks for code.", Assistant.PlainTextRule);
        Assert.StartsWith(Assistant.PlainTextRule + " ", Assistant.OperatingRules, StringComparison.Ordinal);
        Assert.DoesNotContain("_", Assistant.PlainTextRule);   // no tool name

        // Both defaults: one paragraph, as with tools. A custom file stands verbatim either way.
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false));
        Assert.Equal("Be terse.\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, persona: "Be terse.", tools: false));
        Assert.Equal(Assistant.DefaultPersona + "\n\nAlways end with a haiku, then call view_image.", Assistant.SystemPrompt(false, null, operatingRules: "Always end with a haiku, then call view_image.", tools: false));
        Assert.Equal(
            Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule + "\n\n" + MemoryPrompt.DirectiveWithoutTool,
            Assistant.SystemPrompt(true, [], tools: false));
        Assert.Equal(
            Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule + "\n\n" + MemoryPrompt.DirectiveWithoutTool + "\n\nSpeak like a pirate.",
            Assistant.SystemPrompt(true, [], voiceDirective: "Speak like a pirate.", tools: false));
        Assert.Equal(Assistant.SystemPrompt(true, ["Their name is Chris."]), Assistant.SystemPrompt(true, ["Their name is Chris."], tools: true));
    }

    [Fact]
    public void SystemPrompt_WebOn_AppendsTheWebRule_ToTheDefaultRulesOnly()
    {
        Assert.Equal(
            "For anything on the web — current facts, news, prices, documentation, a page the user names — search with web_search and read a page with web_fetch (offset continues a long one); say what you looked at. " +
            "To open a page for the user in their browser — when they ask to open, show or see a link rather than have it read out — call open_url.",
            Assistant.WebRule);
        // The download sentence (2026-09-18) rides the web rule only with the file tools on: download_file writes the sandbox.
        Assert.Equal(
            "To save a file or a picture from the web into the working directory call download_file with its URL (and a path when the user names one); it saves without reading — view_image or read_file look at the result.",
            Assistant.DownloadRule);
        Assert.Equal(DefaultWebSystemPrompt, Assistant.SystemPrompt(false, null, web: true));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, web: false));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null));
        // The sentence rides the rules block, ahead of the memory section and the directive.
        Assert.Equal(DefaultWebSystemPrompt + "\n\n" + MemoryPrompt.Directive + "\n\n" + Pirate, Assistant.SystemPrompt(true, [], voiceDirective: Pirate, web: true));
        Assert.Equal("Be terse.\n\n" + Assistant.OperatingRules + " " + Assistant.WebRule + " " + Assistant.DownloadRule, Assistant.SystemPrompt(false, null, persona: "Be terse.", web: true));
        // A custom operata.md stands verbatim: it names the tools itself or not at all.
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", web: true));
        // LLM offer tools off: no web sentence either, whatever the switch says.
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, web: true));
    }

    [Fact]
    public void DefaultRules_DownloadFalse_KeepsTheWebRule_DropsTheDownloadSentence()
    {
        // download_file switched off by name on /tools (2026-09-19): the one single-tool rule follows its tool; the web rule stands.
        Assert.Equal(Assistant.OperatingRules + " " + Assistant.WebRule, Assistant.DefaultRules(false, tools: true, web: true, download: false));
        Assert.Equal(Assistant.OperatingRules + " " + Assistant.WebRule + " " + Assistant.DownloadRule, Assistant.DefaultRules(false, tools: true, web: true));
        Assert.Equal(Assistant.OperatingRulesWithoutFiles + " " + Assistant.WebRule, Assistant.DefaultRules(false, tools: true, files: false, web: true, download: true));   // files off drops it whatever download says
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.WebRule, Assistant.SystemPrompt(false, null, web: true, download: false));
    }

    [Fact]
    public void SystemPrompt_RecallFalse_PutsTheListInThePrompt_TheRulesUntouched()
    {
        // recall_memory switched off on /tools while memory is on (2026-09-19): no opening call carries the list, so the section holds it as under LLM offer tools off.
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + MemoryPrompt.Section(["Their name is Chris."], tools: false), Assistant.SystemPrompt(false, ["Their name is Chris."], recall: false));
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + MemoryPrompt.Section(["Their name is Chris."], tools: true), Assistant.SystemPrompt(false, ["Their name is Chris."]));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, recall: false));   // memory off: nothing either way
        Assert.Contains("- Their name is Chris.", Assistant.SystemPrompt(false, ["Their name is Chris."], recall: false));
    }

    [Fact]
    public void SystemPrompt_SessionsOn_AppendsTheSessionRule_Last_ToTheDefaultRulesOnly()
    {
        Assert.Equal(
            "Your earlier conversations with the user are stored: when they ask what was said, decided or done before, or refer to something you cannot see in this conversation, call session_manager — search with the words they remember, list for the newest, then read a session by its id for the turns; never guess at them. " +
            "The user restores, renames or removes a session with /sessions, not you.",
            Assistant.SessionRule);
        Assert.Equal(DefaultWebSystemPrompt + " " + Assistant.SessionRule, Assistant.SystemPrompt(false, null, web: true, sessions: true));
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.SessionRule, Assistant.SystemPrompt(false, null, sessions: true));
        Assert.Equal(DefaultWebSystemPrompt, Assistant.SystemPrompt(false, null, web: true));
        // After the ask sentence, ahead of the memory section and the directive.
        Assert.Equal(DefaultWebSystemPrompt + " " + Assistant.AskRule(AskLimits.Default) + " " + Assistant.SessionRule + "\n\n" + MemoryPrompt.Directive + "\n\n" + Pirate, Assistant.SystemPrompt(true, [], voiceDirective: Pirate, web: true, ask: AskLimits.Default, sessions: true));
        // A custom operata.md stands verbatim; LLM offer tools off drops it whatever the switch says.
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", sessions: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, sessions: true));
        Assert.Equal(Assistant.DefaultRules(false, true) + " " + Assistant.SessionRule, Assistant.DefaultRules(false, true, sessions: true));
    }

    /// <summary>The git rule (2026-09-20): after the download rule, with the sandbox's sentences, only with tools, only while a git tool is offered; it names the nine default-on tools and neither opt-in.</summary>
    [Fact]
    public void SystemPrompt_GitOn_AppendsTheGitRule_AfterTheDownloadRule_ToTheDefaultRulesOnly()
    {
        Assert.Equal(
            "The working directory may be a git repository (or hold one): gitlib_status shows its state, gitlib_log its history, gitlib_show a commit or a file at a commit, " +
            "gitlib_diff the changes, gitlib_blame who wrote a line; gitlib_stage, gitlib_commit, gitlib_branch and gitlib_stash change it — commit only what the user asked to commit, " +
            "with the message they gave or a short imperative one, and never remove or overwrite work the user did not name.",
            Assistant.GitRule);
        Assert.DoesNotContain("gitlib_discard", Assistant.GitRule);
        Assert.DoesNotContain("gitlib_delete", Assistant.GitRule);
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.GitRule, Assistant.SystemPrompt(false, null, git: true));
        Assert.Equal(DefaultWebSystemPrompt + " " + Assistant.GitRule, Assistant.SystemPrompt(false, null, web: true, git: true));
        Assert.Equal(DefaultWebSystemPrompt + " " + Assistant.GitRule + " " + Assistant.AskRule(AskLimits.Default) + " " + Assistant.SessionRule + " " + Assistant.McpRule, Assistant.SystemPrompt(false, null, web: true, ask: AskLimits.Default, sessions: true, mcp: true, git: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.OperatingRulesWithoutFiles + " " + Assistant.GitRule, Assistant.SystemPrompt(false, null, files: false, git: true));   // its own switch: the file tools off leave it
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null));
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", git: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, git: true));
        Assert.Equal(Assistant.DefaultRules(false, true) + " " + Assistant.GitRule, Assistant.DefaultRules(false, true, git: true));
        Assert.Equal(Assistant.OperatingRules, Assistant.DefaultRules(false, true));
    }

    /// <summary>The vault rule (2026-09-22): after the shell rule, before the ask rule, only with tools, only while a vault tool is offered; it names all eight and the move as the way to rename.</summary>
    [Fact]
    public void SystemPrompt_ObsidianOn_AppendsTheVaultRule_AfterTheShellRule_ToTheDefaultRulesOnly()
    {
        Assert.Equal(
            "The user's Obsidian vault holds their notes: name a note as Obsidian does (its name, a [[wikilink]] or its path in the vault); " +
            "vault_search and vault_list find notes, vault_read reads one, vault_links shows its links and backlinks, vault_daily opens a day's daily note; " +
            "vault_write and vault_properties change notes — write Obsidian Markdown ([[links]], #tags) — and vault_move renames or moves one with its links kept; " +
            "the vault's .obsidian folder belongs to Obsidian, so leave it alone.",
            Assistant.ObsidianRule);
        Assert.All(Fakes.ObsidianToolNames.WithoutDelete, name => Assert.Contains(name, Assistant.ObsidianRule, StringComparison.Ordinal));
        Assert.DoesNotContain("vault_delete", Assistant.ObsidianRule, StringComparison.Ordinal);   // its own sentence, only while it is offered (later on 2026-09-22)
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.ObsidianRule, Assistant.SystemPrompt(false, null, obsidian: true));
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.ShellRuleFor(bridge: true, police: true) + " " + Assistant.ObsidianRule + " " + Assistant.AskRule(AskLimits.Default), Assistant.SystemPrompt(false, null, shell: true, bridge: true, ask: AskLimits.Default, obsidian: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, obsidian: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", obsidian: true));
    }

    /// <summary>The vault delete sentence (later on 2026-09-22): right after the vault rule, only while vault_delete is offered, never without the vault rule.</summary>
    [Fact]
    public void SystemPrompt_ObsidianDelete_FollowsTheVaultRule_OnlyWithIt()
    {
        Assert.Equal("vault_delete moves a note or attachment into the vault's .trash, where Obsidian can restore it; use it only when the user asks for a deletion.", Assistant.ObsidianDeleteRule);
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.ObsidianRule + " " + Assistant.ObsidianDeleteRule, Assistant.SystemPrompt(false, null, obsidian: true, obsidianDelete: true));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, obsidianDelete: true));   // no vault rule, no delete sentence
        Assert.Equal(Assistant.DefaultRules(false, true, obsidian: true), Assistant.DefaultRules(false, true, obsidian: true, obsidianDelete: false));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, obsidian: true, obsidianDelete: true));
    }

    /// <summary>The shell rule (2026-09-21): after the git rule, before the ask rule, only with tools, only while the tool is offered; it names the tool, its two arguments, the sandbox as the start, the approval and the finality of a denial.</summary>
    [Fact]
    public void SystemPrompt_ShellOn_AppendsTheShellRule_AfterTheGitRule_ToTheDefaultRulesOnly()
    {
        Assert.Equal(
            "To run a program, a build, a test or a script the user asks for, call run_command with the command line " +
            "(shell picks powershell, cmd or bash when the user's default will not do; workdir a folder under the working directory); " +
            "it runs in the working directory and may only name paths under it (relative, or absolute under it), and the user approves each command before it runs and may deny it — " +
            "never retry or work around a denied or refused command, and say what you ran. " +
            "For a server or a long job pass background and use process to poll, read, wait for, write to or kill it; " +
            "with notify you are told at your next turn when it exits. " +
            "For a task with several steps or many tool calls, execute_code runs a python, node or powershell script that can call these same tools through its neon_tools module and returns what it printed.",
            Assistant.ShellRule);
        // The bridge rule rides with bridge: true (the setting Shell tool bridge, later on 2026-09-21); the shell tools alone carry the rule without it.
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.ShellRuleFor(bridge: true, police: true), Assistant.SystemPrompt(false, null, shell: true, bridge: true));
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.GitRule + " " + Assistant.ShellRuleFor(bridge: true, police: true), Assistant.SystemPrompt(false, null, git: true, shell: true, bridge: true));
        Assert.Equal(DefaultWebSystemPrompt + " " + Assistant.GitRule + " " + Assistant.ShellRuleFor(bridge: true, police: true) + " " + Assistant.AskRule(AskLimits.Default) + " " + Assistant.SessionRule + " " + Assistant.McpRule, Assistant.SystemPrompt(false, null, web: true, ask: AskLimits.Default, sessions: true, mcp: true, git: true, shell: true, bridge: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.OperatingRulesWithoutFiles + " " + Assistant.ShellRuleFor(bridge: true, police: true), Assistant.SystemPrompt(false, null, files: false, shell: true, bridge: true));   // its own switch: the file tools off leave it
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null));
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", shell: true, bridge: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, shell: true, bridge: true));
        Assert.Equal(Assistant.DefaultRules(false, true) + " " + Assistant.ShellRuleFor(bridge: true, police: true), Assistant.DefaultRules(false, true, shell: true, bridge: true));
    }

    [Fact]
    public void SystemPrompt_ShellOn_BridgeOff_AppendsTheRuleWithoutNeonTools()
    {
        // Shell tool bridge off (later on 2026-09-21, the default): the same head, and execute_code's sentence never promises a module the run does not write.
        Assert.Equal(
            "To run a program, a build, a test or a script the user asks for, call run_command with the command line " +
            "(shell picks powershell, cmd or bash when the user's default will not do; workdir a folder under the working directory); " +
            "it runs in the working directory and may only name paths under it (relative, or absolute under it), and the user approves each command before it runs and may deny it — " +
            "never retry or work around a denied or refused command, and say what you ran. " +
            "For a server or a long job pass background and use process to poll, read, wait for, write to or kill it; " +
            "with notify you are told at your next turn when it exits. " +
            "For a task with several steps, execute_code runs a python, node or powershell script and returns what it printed.",
            Assistant.ShellRuleWithoutBridge);
        Assert.DoesNotContain("neon_tools", Assistant.ShellRuleWithoutBridge);
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.ShellRuleFor(bridge: false, police: true), Assistant.SystemPrompt(false, null, shell: true));
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.GitRule + " " + Assistant.ShellRuleFor(bridge: false, police: true), Assistant.SystemPrompt(false, null, git: true, shell: true, bridge: false));
        Assert.Equal(Assistant.DefaultRules(false, true) + " " + Assistant.ShellRuleFor(bridge: false, police: true), Assistant.DefaultRules(false, true, shell: true));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, bridge: true));   // the bridge rides the shell rule alone
    }

    /// <summary>The police off (Shell police, 2026-09-22): the head says a command starts in the working directory and nothing about where it may reach — until that day it said "can reach the whole computer", and no variant does now.</summary>
    [Fact]
    public void SystemPrompt_ShellOn_PoliceOff_AppendsTheUnpolicedRule()
    {
        const string Unpoliced =
            "To run a program, a build, a test or a script the user asks for, call run_command with the command line " +
            "(shell picks powershell, cmd or bash when the user's default will not do; workdir a folder under the working directory); " +
            "it starts in the working directory, and the user approves each command before it runs and may deny it — " +
            "never retry or work around a denied command, and say what you ran. " +
            "For a server or a long job pass background and use process to poll, read, wait for, write to or kill it; " +
            "with notify you are told at your next turn when it exits. ";
        Assert.Equal(Unpoliced + "For a task with several steps or many tool calls, execute_code runs a python, node or powershell script that can call these same tools through its neon_tools module and returns what it printed.", Assistant.ShellRuleUnpoliced);
        Assert.Equal(Unpoliced + "For a task with several steps, execute_code runs a python, node or powershell script and returns what it printed.", Assistant.ShellRuleWithoutBridgeUnpoliced);
        foreach (string rule in new[] { Assistant.ShellRule, Assistant.ShellRuleWithoutBridge, Assistant.ShellRuleUnpoliced, Assistant.ShellRuleWithoutBridgeUnpoliced })
        {
            Assert.DoesNotContain("reach", rule);
            Assert.DoesNotContain("confined", rule);
        }

        Assert.DoesNotContain("under it", Assistant.ShellRuleUnpoliced);
        Assert.DoesNotContain("under it", Assistant.ShellRuleWithoutBridgeUnpoliced);
        Assert.Equal(Assistant.ShellRule, Assistant.ShellRuleFor(bridge: true, police: true, mac: false));
        Assert.Equal(Assistant.ShellRuleWithoutBridge, Assistant.ShellRuleFor(bridge: false, police: true, mac: false));
        Assert.Equal(Assistant.ShellRuleUnpoliced, Assistant.ShellRuleFor(bridge: true, police: false, mac: false));
        Assert.Equal(Assistant.ShellRuleWithoutBridgeUnpoliced, Assistant.ShellRuleFor(bridge: false, police: false, mac: false));

        // On a Mac (2026-10-06) each rule names the Mac and its shells in place of Windows', and changes nothing else.
        Assert.Equal("picks zsh, bash or pwsh when the user's default will not do (this computer is a Mac: there is no cmd, PowerShell is pwsh, and Windows commands do not run)", Assistant.ShellPickMac);
        foreach (var (bridge, police, windows) in new[] { (true, true, Assistant.ShellRule), (false, true, Assistant.ShellRuleWithoutBridge), (true, false, Assistant.ShellRuleUnpoliced), (false, false, Assistant.ShellRuleWithoutBridgeUnpoliced) })
        {
            string mac = Assistant.ShellRuleFor(bridge, police, mac: true);
            Assert.Contains("shell " + Assistant.ShellPickMac + ";", mac, StringComparison.Ordinal);
            Assert.DoesNotContain("cmd or bash", mac, StringComparison.Ordinal);
            Assert.Equal(windows, mac.Replace(Assistant.ShellPickMac, Assistant.ShellPickWindows, StringComparison.Ordinal));
        }

        Assert.Equal(OperatingSystem.IsMacOS() ? Assistant.ForMac(Assistant.ShellRule) : Assistant.ShellRule, Assistant.ShellRuleFor(bridge: true, police: true));
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.ShellRuleFor(bridge: false, police: false), Assistant.SystemPrompt(false, null, shell: true, police: false));
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.ShellRuleFor(bridge: true, police: false), Assistant.SystemPrompt(false, null, shell: true, bridge: true, police: false));
        Assert.Equal(Assistant.DefaultRules(false, true) + " " + Assistant.ShellRuleFor(bridge: false, police: false), Assistant.DefaultRules(false, true, shell: true, police: false));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, police: false));   // the police rides the shell rule alone
    }

    /// <summary>Shell prefer native tools (2026-09-26): one sentence after the shell rule, naming only the groups offered, with the shell words each replaces.</summary>
    [Fact]
    public void ShellNativeRule_NamesTheOfferedGroups_AfterTheShellRule()
    {
        Assert.Equal(
            "Call run_command only for what no other tool does: read_file and search_files read, search and list files (not cat, type, Get-Content, dir, ls or grep) and the file tools write, copy, move and delete them; " +
            "the gitlib_ tools look at and change the repository (not git status, log, diff, add or commit); web_search and web_fetch reach the web (not curl or Invoke-WebRequest); sql_query reads the databases (not sqlcmd).",
            Assistant.ShellNativeRule(files: true, git: true, web: true, sql: true));
        Assert.Equal("Call run_command only for what no other tool does: the gitlib_ tools look at and change the repository (not git status, log, diff, add or commit).", Assistant.ShellNativeRule(files: false, git: true, web: false, sql: false));
        Assert.Equal("", Assistant.ShellNativeRule(false, false, false, false));

        string files = Assistant.ShellNativeRule(files: true, git: false, web: false, sql: false);
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.ShellRuleFor(bridge: false, police: true) + " " + files, Assistant.SystemPrompt(false, null, shell: true, native: true));
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.GitRule + " " + Assistant.ShellRuleFor(bridge: true, police: true) + " " + Assistant.ShellNativeRule(true, true, false, true) + " " + Assistant.SqlRule, Assistant.SystemPrompt(false, null, git: true, shell: true, bridge: true, sql: true, native: true));   // before the SQL sentence, naming it
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.ShellRuleFor(bridge: false, police: true), Assistant.SystemPrompt(false, null, shell: true, native: false));   // the setting off
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, native: true));   // no shell, nothing to prefer over
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.OperatingRulesWithoutFiles + " " + Assistant.ShellRuleFor(bridge: false, police: true), Assistant.SystemPrompt(false, null, files: false, shell: true, native: true));   // no group: no sentence
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", shell: true, native: true));   // a custom operata.md stands verbatim
    }

    /// <summary>The MCP rule (2026-09-20): after the session rule, only with tools, only when asked for.</summary>
    [Fact]
    public void SystemPrompt_McpOn_AppendsTheMcpRule_Last_ToTheDefaultRulesOnly()
    {
        Assert.Equal(
            "Tools named <server>__<tool> belong to external MCP servers the user connected; each does what its own description says — " +
            "read it before calling, pass exactly the arguments its schema names, and answer from its result.",
            Assistant.McpRule);
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.McpRule, Assistant.SystemPrompt(false, null, mcp: true));
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + Assistant.SessionRule + " " + Assistant.McpRule, Assistant.SystemPrompt(false, null, sessions: true, mcp: true));
        Assert.Equal(DefaultWebSystemPrompt + " " + Assistant.AskRule(AskLimits.Default) + " " + Assistant.SessionRule + " " + Assistant.McpRule + "\n\n" + MemoryPrompt.Directive + "\n\n" + Pirate, Assistant.SystemPrompt(true, [], voiceDirective: Pirate, web: true, ask: AskLimits.Default, sessions: true, mcp: true));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null));
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", mcp: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, mcp: true));
        Assert.Equal(Assistant.DefaultRules(false, true) + " " + Assistant.McpRule, Assistant.DefaultRules(false, true, mcp: true));
        Assert.Equal(Assistant.OperatingRules, Assistant.DefaultRules(false, true));
    }

    [Fact]
    public void SystemPrompt_AskOn_AppendsTheAskRule_AfterTheWebRule_ToTheDefaultRulesOnly()
    {
        var limits = AskLimits.Default;
        string rule = Assistant.AskRule(limits);
        Assert.Equal(
            "When you need the user to decide between a few possibilities — a choice, a preference, a clarification with a short list of answers — call ask_user " +
            "with the options and wait for the result instead of guessing or asking in prose; up to 10 questions in one call, 2 to 10 options each, and the user can type their own answer. " +
            "If the result says the questions were not answered, go on without them.",
            rule);
        // The rule quotes the caps it is given (the Ask tab's two rows), the floor the tool's own.
        Assert.Contains("up to 3 questions in one call, 2 to 15 options each", Assistant.AskRule(new AskLimits(3, 15)));
        Assert.Equal(Assistant.DefaultSystemPrompt + " " + rule, Assistant.SystemPrompt(false, null, ask: limits));
        Assert.Equal(DefaultWebSystemPrompt + " " + rule, Assistant.SystemPrompt(false, null, web: true, ask: limits));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, ask: null));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.OperatingRulesWithoutFiles + " " + rule, Assistant.SystemPrompt(false, null, files: false, ask: limits));
        // The sentence rides the rules block, ahead of the memory section and the directive.
        Assert.Equal(DefaultWebSystemPrompt + " " + rule + "\n\n" + MemoryPrompt.Directive + "\n\n" + Pirate, Assistant.SystemPrompt(true, [], voiceDirective: Pirate, web: true, ask: limits));
        Assert.Equal("Be terse.\n\n" + Assistant.OperatingRules + " " + rule, Assistant.SystemPrompt(false, null, persona: "Be terse.", ask: limits));
        // A custom operata.md stands verbatim; LLM offer tools off wins over everything.
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", ask: limits));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, web: true, ask: limits));
    }

    [Fact]
    public void MarkdownRule_IsPinned_AndReplacesThePlainTextSentence()
    {
        Assert.Equal("Reply in light Markdown: **bold** for emphasis, a bulleted or numbered list where one helps, and a fenced code block with its language for code; no headings or tables unless the user asks for them.", Assistant.MarkdownRule);
        Assert.Equal(Assistant.PlainTextRule, Assistant.TextRule(false));
        Assert.Equal(Assistant.MarkdownRule, Assistant.TextRule(true));

        // The tool sentences are the old const's tail; the default rules are the two joined.
        Assert.Equal(Assistant.PlainTextRule + " " + Assistant.ToolRules, Assistant.OperatingRulesWithoutFiles);
        Assert.StartsWith("Use a tool when it helps", Assistant.ToolRules);

        // DefaultRules with markdown off is the pinned consts byte for byte; on, the Markdown sentence leads.
        Assert.Equal(Assistant.OperatingRules, Assistant.DefaultRules(false, tools: true));
        Assert.Equal(Assistant.OperatingRulesWithoutFiles, Assistant.DefaultRules(false, tools: true, files: false));
        Assert.Equal(Assistant.PlainTextRule, Assistant.DefaultRules(false, tools: false));
        Assert.Equal(Assistant.OperatingRules + " " + Assistant.WebRule + " " + Assistant.DownloadRule, Assistant.DefaultRules(false, tools: true, web: true));
        Assert.Equal(Assistant.OperatingRulesWithoutFiles + " " + Assistant.WebRule, Assistant.DefaultRules(false, tools: true, files: false, web: true));
        Assert.Equal(Assistant.MarkdownRule + " " + Assistant.ToolRules + " " + Assistant.FileRule, Assistant.DefaultRules(true, tools: true));
        Assert.Equal(Assistant.MarkdownRule + " " + Assistant.ToolRules, Assistant.DefaultRules(true, tools: true, files: false));
        Assert.Equal(Assistant.MarkdownRule, Assistant.DefaultRules(true, tools: false));

        // delete switched off on /tools (off in a fresh profile since 2026-09-20): the file rule loses its delete clause, nothing else moves.
        Assert.Equal(Assistant.FileRuleWithoutDelete, Assistant.FileRule.Replace("; delete removes a file or a folder for good, with everything in it. ", ". ", StringComparison.Ordinal));
        Assert.DoesNotContain("delete", Assistant.FileRuleWithoutDelete, StringComparison.Ordinal);
        Assert.DoesNotContain("restore", Assistant.FileRuleWithoutDelete, StringComparison.Ordinal);
        Assert.Equal(Assistant.PlainTextRule + " " + Assistant.ToolRules + " " + Assistant.FileRuleWithoutDelete, Assistant.DefaultRules(false, tools: true, delete: false));
        Assert.Equal(Assistant.OperatingRulesWithoutFiles + " " + Assistant.WebRule, Assistant.DefaultRules(false, tools: true, files: false, web: true, delete: false));   // no file rule, nothing to cut
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule + " " + Assistant.ToolRules + " " + Assistant.FileRuleWithoutDelete, Assistant.SystemPrompt(false, null, delete: false));
        Assert.Equal(Assistant.OperatingRules, Assistant.DefaultRules(false, tools: true, delete: true));

        // The delete clause tells the truth — delete removes for good (FileRuleDeleteInPlace's words since 2026-09-20, under File safe edits off; the
        // only form since 2026-10-01, when that setting, its .trash and restore went) — and names neither restore nor a trash, nor the setting (2026-09-21, the user's ask).
        Assert.DoesNotContain("restore", Assistant.FileRule, StringComparison.Ordinal);
        Assert.DoesNotContain("trash", Assistant.FileRule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe edits", Assistant.FileRule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trash", Assistant.DefaultRules(false, tools: true, web: true, ask: AskLimits.Default, sessions: true, mcp: true, git: true), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Assistant.PlainTextRule + " " + Assistant.ToolRules + " " + Assistant.FileRule, Assistant.DefaultRules(false, tools: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule + " " + Assistant.ToolRules + " " + Assistant.FileRule, Assistant.SystemPrompt(false, null));

        // No timer tool offered (2026-09-20: headless, or the three switched off on /tools): the tool rules lose their timer sentence, nothing else moves.
        Assert.Equal(Assistant.ToolRulesWithoutTimers + " " + Assistant.TimerRule, Assistant.ToolRules);
        Assert.Equal("For a countdown, use start_timer, stop_timer and list_timers; never guess what is left on a timer.", Assistant.TimerRule);
        Assert.EndsWith("instead of counting yourself.", Assistant.ToolRulesWithoutTimers);
        Assert.DoesNotContain("timer", Assistant.ToolRulesWithoutTimers, StringComparison.Ordinal);
        Assert.Equal(Assistant.PlainTextRule + " " + Assistant.ToolRulesWithoutTimers + " " + Assistant.FileRule, Assistant.DefaultRules(false, tools: true, timers: false));
        Assert.Equal(Assistant.PlainTextRule + " " + Assistant.ToolRulesWithoutTimers, Assistant.DefaultRules(false, tools: true, files: false, timers: false));
        Assert.Equal(Assistant.MarkdownRule + " " + Assistant.ToolRulesWithoutTimers + " " + Assistant.FileRule + " " + Assistant.WebRule + " " + Assistant.DownloadRule, Assistant.DefaultRules(true, tools: true, web: true, timers: false));
        Assert.Equal(Assistant.PlainTextRule, Assistant.DefaultRules(false, tools: false, timers: false));   // no tool sentence at all there
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule + " " + Assistant.ToolRulesWithoutTimers + " " + Assistant.FileRule, Assistant.SystemPrompt(false, null, timers: false));
        Assert.DoesNotContain("start_timer", Assistant.SystemPrompt(false, null, timers: false), StringComparison.Ordinal);
        Assert.Equal(Assistant.OperatingRules, Assistant.DefaultRules(false, tools: true, timers: true));

        // Through SystemPrompt: the one paragraph with the default persona, the rule swapped.
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.MarkdownRule + " " + Assistant.ToolRules + " " + Assistant.FileRule, Assistant.SystemPrompt(false, null, markdown: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.MarkdownRule + " " + Assistant.ToolRules + " " + Assistant.FileRule + " " + Assistant.WebRule + " " + Assistant.DownloadRule, Assistant.SystemPrompt(false, null, web: true, markdown: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.MarkdownRule, Assistant.SystemPrompt(false, null, tools: false, markdown: true));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, markdown: false));
        // A custom operata.md stands verbatim; the voice directive still goes last when the turn speaks.
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", markdown: true));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.MarkdownRule + " " + Assistant.ToolRules + " " + Assistant.FileRule + "\n\n" + Pirate, Assistant.SystemPrompt(true, null, voiceDirective: Pirate, markdown: true));
    }

    [Fact]
    public void SystemPrompt_FilesOff_DropsTheFileSentences_FromTheDefaultRulesOnly()
    {
        // The rules are the tool-less-files half and the file sentences, byte-identical to the one const they were (2026-09-15).
        Assert.Equal(Assistant.OperatingRulesWithoutFiles + " " + Assistant.FileRule, Assistant.OperatingRules);
        Assert.StartsWith(Assistant.PlainTextRule + " Use a tool when it helps", Assistant.OperatingRulesWithoutFiles);
        Assert.EndsWith("never guess what is left on a timer.", Assistant.OperatingRulesWithoutFiles);
        Assert.Contains("start_timer", Assistant.OperatingRulesWithoutFiles);
        Assert.DoesNotContain("working directory", Assistant.OperatingRulesWithoutFiles);
        Assert.StartsWith("The user's working directory", Assistant.FileRule);
        Assert.Contains("get_working_directory", Assistant.FileRule);
        Assert.Contains("view_image", Assistant.FileRule);
        Assert.EndsWith("read_file cannot read one.", Assistant.FileRule);

        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.OperatingRulesWithoutFiles, Assistant.SystemPrompt(false, null, files: false));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, files: true));
        // The web sentence still follows the shortened rules.
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.OperatingRulesWithoutFiles + " " + Assistant.WebRule, Assistant.SystemPrompt(false, null, web: true, files: false));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.OperatingRulesWithoutFiles + "\n\n" + MemoryPrompt.Directive + "\n\n" + Pirate, Assistant.SystemPrompt(true, [], voiceDirective: Pirate, files: false));
        Assert.Equal("Be terse.\n\n" + Assistant.OperatingRulesWithoutFiles, Assistant.SystemPrompt(false, null, persona: "Be terse.", files: false));
        // A custom operata.md stands verbatim; LLM offer tools off wins over everything.
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", files: false));
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, web: true, files: false));
    }

    [Fact]
    public void ToolRoleRejection_IsTheMistralTemplatesSentence_AndTheHintIsPinned()
    {
        Assert.True(Assistant.LooksLikeToolRoleRejection("ClientResultException: HTTP 400 (Bad Request): Only user, system and assistant roles are supported!"));
        Assert.True(Assistant.LooksLikeToolRoleRejection("Only user and assistant roles are supported!"));
        Assert.False(Assistant.LooksLikeToolRoleRejection("HttpRequestException: boom -> IOException: socket closed"));
        Assert.Equal(" — this model's chat template accepts no tool messages; turn the setting LLM offer tools off (the LLM tab of /settings) to talk to it without tools", Assistant.ToolRoleHint);
    }

    [Fact]
    public async Task ToolRoleRejection_WithToolsOffered_AppendsTheHint()
    {
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() });
        client.EnqueueText("never");
        client.ThrowAt = 0;
        client.Failure = new InvalidOperationException("Only user, system and assistant roles are supported!");

        var events = await Run(assistant, "hello");

        var notice = Assert.IsType<TurnEvent.Notice>(Assert.Single(events));
        Assert.True(notice.IsError);
        Assert.Equal("Model error: InvalidOperationException: Only user, system and assistant roles are supported!" + Assistant.ToolRoleHint, notice.Text);
    }

    [Fact]
    public async Task ToolRoleRejection_WithNoToolsOffered_IsReportedBare()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("never");
        client.ThrowAt = 0;
        client.Failure = new InvalidOperationException("Only user, system and assistant roles are supported!");

        var events = await Run(assistant, "hello");

        var notice = Assert.IsType<TurnEvent.Notice>(Assert.Single(events));
        Assert.Equal("Model error: InvalidOperationException: Only user, system and assistant roles are supported!", notice.Text);
    }

    [Fact]
    public void SystemPrompt_CustomPersona_ReplacesTheIdentityOnly_AsItsOwnBlock()
    {
        const string persona = "You are Rex, a gruff pirate.\n\nYou answer in one sentence.";
        Assert.Equal(persona + "\n\n" + Assistant.OperatingRules, Assistant.SystemPrompt(false, null, persona));
        Assert.DoesNotContain("Neon", Assistant.SystemPrompt(false, null, persona));
    }

    [Fact]
    public void SystemPrompt_CustomPersona_KeepsTheOrder_PersonaRulesMemoryDirective()
    {
        const string persona = "You are Rex.";
        string prompt = Assistant.SystemPrompt(true, new[] { "Their name is Chris." }, persona, voiceDirective: Pirate);

        Assert.StartsWith(persona + "\n\n" + Assistant.OperatingRules + "\n\n" + MemoryPrompt.Directive, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(MemoryPrompt.Heading, prompt, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + Pirate, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void SystemPrompt_BlankPersona_IsTheDefault(string? persona)
    {
        Assert.Equal(Assistant.SystemPrompt(false), Assistant.SystemPrompt(false, null, persona));
        Assert.Equal(Assistant.SystemPrompt(true, Array.Empty<string>()), Assistant.SystemPrompt(true, Array.Empty<string>(), persona));
    }

    [Fact]
    public void SystemPrompt_CustomPersona_IsTrimmed()
    {
        Assert.Equal("You are Rex.\n\n" + Assistant.OperatingRules, Assistant.SystemPrompt(false, null, "  You are Rex. \n"));
    }

    [Fact]
    public void SystemPrompt_CustomRules_ReplaceTheOperatingRules_AsTheirOwnBlock_AfterTheDefaultPersona()
    {
        const string rules = "Answer in haiku.\n\nNever use a tool.";
        string prompt = Assistant.SystemPrompt(false, null, null, rules);

        Assert.Equal(Assistant.DefaultPersona + "\n\n" + rules, prompt);
        Assert.DoesNotContain("Reply in plain text", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_CustomPersonaAndRules_KeepTheOrder_PersonaRulesMemoryDirective()
    {
        string prompt = Assistant.SystemPrompt(true, new[] { "Their name is Chris." }, "You are Rex.", "Answer in haiku.", Pirate);

        Assert.StartsWith("You are Rex.\n\nAnswer in haiku.\n\n" + MemoryPrompt.Directive, prompt, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + Pirate, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(Assistant.OperatingRules, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void SystemPrompt_BlankRules_AreTheDefault(string? rules)
    {
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, null, rules));
        Assert.Equal("You are Rex.\n\n" + Assistant.OperatingRules, Assistant.SystemPrompt(false, null, "You are Rex.", rules));
    }

    [Fact]
    public void SystemPrompt_CustomRules_AreTrimmed()
    {
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.", Assistant.SystemPrompt(false, null, null, "  Answer in haiku. \n"));
    }

    [Fact]
    public void SystemPrompt_CustomVoiceDirective_GoesLast_WhenSpeaking()
    {
        const string directive = "Speak like a pirate.\n\nKeep it to one breath.";
        string prompt = Assistant.SystemPrompt(true, new[] { "Their name is Chris." }, "You are Rex.", "Answer in haiku.", directive);

        Assert.StartsWith("You are Rex.\n\nAnswer in haiku.\n\n" + MemoryPrompt.Directive, prompt, StringComparison.Ordinal);
        Assert.EndsWith("\n\n" + directive, prompt, StringComparison.Ordinal);
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + directive, Assistant.SystemPrompt(true, null, null, null, directive));
    }

    [Fact]
    public void SystemPrompt_CustomVoiceDirective_IsIgnoredWhenNotSpeaking()
    {
        Assert.Equal(Assistant.SystemPrompt(false), Assistant.SystemPrompt(false, null, null, null, "Speak like a pirate."));
        Assert.Equal(Assistant.SystemPrompt(false, Array.Empty<string>(), "You are Rex."), Assistant.SystemPrompt(false, Array.Empty<string>(), "You are Rex.", null, "Speak like a pirate."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void SystemPrompt_BlankVoiceDirective_AddsNothing(string? directive)
    {
        // No default to fall back on since 2026-10-03: a blank file is no directive, with tools or without.
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(true, null, null, null, directive));
        Assert.Equal(Assistant.SystemPrompt(false, null, tools: false), Assistant.SystemPrompt(true, null, null, null, directive, tools: false));
    }

    [Fact]
    public void SystemPrompt_CustomVoiceDirective_IsTrimmed()
    {
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\nSpeak like a pirate.", Assistant.SystemPrompt(true, null, null, null, "  Speak like a pirate. \n"));
    }

    [Fact]
    public async Task Tools_CanBeReplacedBetweenTurns_AndAnEmptyListIsNull()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(null);
        client.EnqueueText("one").EnqueueText("two");

        await Run(assistant, "a");
        assistant.Tools = new AIFunction[] { echo };
        await Run(assistant, "b");

        Assert.Null(client.Options[0]!.Tools);
        Assert.Single(client.Options[1]!.Tools!);
    }

    [Fact]
    public async Task TextToolCalls_On_ACallWrittenAsText_RunsAsARealOne_AndIsNeverShown()
    {
        var echo = new EchoTool();
        var (client, history, assistant) = Build(new AIFunction[] { echo });
        assistant.TextToolCalls = true;
        client.EnqueueText("Sure. ec", "ho(text=\"hi there\")", " Done.").EnqueueText("It echoed.");

        var events = await Run(assistant, "a");

        Assert.Equal(["hi there"], echo.Received);
        var call = Assert.Single(events.OfType<TurnEvent.ToolCall>());
        Assert.Equal("echo", call.Name);
        Assert.Equal("echo: hi there", Assert.Single(events.OfType<TurnEvent.ToolResult>()).Text);
        Assert.Equal("Sure. Done.It echoed.", string.Concat(Deltas(events)));
        Assert.DoesNotContain(Deltas(events), d => d.Contains("echo(", StringComparison.Ordinal));
        var asked = history.Messages.Single(m => m.Contents.OfType<FunctionCallContent>().Any());
        Assert.Equal("Sure. Done.", asked.Text);
        Assert.Equal(call.CallId, asked.Contents.OfType<FunctionCallContent>().Single().CallId);
    }

    /// <summary>
    /// <see cref="Assistant.TextToolCallNames"/> (2026-10-04, the /botchat bots offered the main chat's tools): a written call to an
    /// offered tool not on the list is neither caught nor run — it stays in the reply, as in the main chat.
    /// </summary>
    [Fact]
    public async Task TextToolCallNames_AWrittenCallToAnotherTool_IsNotRun()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        assistant.TextToolCalls = true;
        assistant.TextToolCallNames = new HashSet<string>(["generate_image"], StringComparer.Ordinal);
        client.EnqueueText("Sure. echo(text=\"hi there\") Done.");

        var events = await Run(assistant, "a");

        Assert.Empty(echo.Received);
        Assert.Empty(events.OfType<TurnEvent.ToolCall>());
        Assert.Contains("echo(text=", string.Concat(Deltas(events)));
    }

    /// <summary>The line form (later on 2026-09-25, the user's /botchat report): the tool's name, then its parameters as <c>key: value</c>, on a line of its own.</summary>
    [Fact]
    public async Task TextToolCalls_On_ACallWrittenAsALine_RunsAsARealOne_AndIsNeverShown()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        assistant.TextToolCalls = true;
        client.EnqueueText("Sure.\nec", "ho text: hi, there\n", "Done.").EnqueueText("It echoed.");

        var events = await Run(assistant, "a");

        Assert.Equal(["hi, there"], echo.Received);
        Assert.Equal("Sure.\nDone.It echoed.", string.Concat(Deltas(events)));
    }

    [Fact]
    public async Task TextToolCalls_Off_TheTextPassesAsWritten()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        client.EnqueueText("Try echo(text=\"hi\") yourself.");

        var events = await Run(assistant, "a");

        Assert.Empty(echo.Received);
        Assert.Empty(events.OfType<TurnEvent.ToolCall>());
        Assert.Equal("Try echo(text=\"hi\") yourself.", string.Concat(Deltas(events)));
    }

    [Fact]
    public async Task ToolCall_IsLoggedAtDebug_WithItsArgumentsCut()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        client.Enqueue(FakeChatClient.Call("call-1", "echo", new Dictionary<string, object?> { ["text"] = "hi" }));
        client.EnqueueText("done");
        var lines = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == "Llm" && e.Level == NeonSidekick.Diagnostics.DiagnosticLevel.Debug && e.Message.StartsWith("Tool call ", StringComparison.Ordinal)) lines.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            await Run(assistant, "echo hi");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(["Tool call echo: {\"text\":\"hi\"}"], lines);
        Assert.Equal("Tool call t: " + new string('a', 1999) + "…", Assistant.ToolCallLogLine("t", new string('a', 2500)));
        Assert.Equal(2000, Assistant.ToolCallLogChars);
    }

    [Fact]
    public async Task ToolCall_IsInvoked_AndTheResultCarriesTheCallId()
    {
        var echo = new EchoTool();
        var (client, history, assistant) = Build(new AIFunction[] { echo });
        client.Enqueue(FakeChatClient.Call("call-42", "echo", new Dictionary<string, object?> { ["text"] = "hi" }));
        client.EnqueueText("done");

        var events = await Run(assistant, "echo hi");

        Assert.Equal(new[] { "hi" }, echo.Received);
        var call = Assert.Single(events.OfType<TurnEvent.ToolCall>());
        Assert.Equal(("echo", "call-42", "{\"text\":\"hi\"}"), (call.Name, call.CallId, call.ArgumentsJson));
        var result = Assert.Single(events.OfType<TurnEvent.ToolResult>());
        Assert.Equal("echo: hi", result.Text);
        Assert.Equal(new[] { "done" }, Deltas(events));

        Assert.Equal(2, client.Requests.Count);
        var second = client.Requests[1];
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool }, second.Select(m => m.Role));
        Assert.Equal("call-42", Assert.Single(second[2].Contents.OfType<FunctionCallContent>()).CallId);
        var fnResult = Assert.Single(second[3].Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-42", fnResult.CallId);
        Assert.Equal("echo: hi", fnResult.Result);
        Assert.Single(client.Options[0]!.Tools!);
        Assert.Equal("done", history.Messages[^1].Text);
    }

    [Fact]
    public async Task PictureTool_ResultIsText_AndThePictureRidesOneCarrierUserMessageAfterTheToolMessage()
    {
        var image = new ImageAttachment("pic.png", [1, 2, 3], ImageFile.Png, 4, 4);
        var (client, history, assistant) = Build(new AIFunction[] { new PictureTool(image) });
        client.Enqueue(FakeChatClient.Call("call-1", "picture", new Dictionary<string, object?>()));
        client.EnqueueText("a square");

        var events = await Run(assistant, "look");

        var result = Assert.Single(events.OfType<TurnEvent.ToolResult>());
        Assert.Equal("saw pic.png", result.Text);
        Assert.Same(image, Assert.Single(result.Images!));

        // The result text goes back under the call id as ever; the carrier follows it, a user
        // message only on the wire — tagged, and no turn.
        var second = client.Requests[1];
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User }, second.Select(m => m.Role));
        var fnResult = Assert.Single(second[3].Contents.OfType<FunctionResultContent>());
        Assert.Equal(("call-1", "saw pic.png"), (fnResult.CallId, fnResult.Result));
        Assert.Empty(second[3].Contents.OfType<DataContent>());
        var carrier = second[4];
        Assert.True(ConversationHistory.IsImageCarrier(carrier));
        Assert.Equal(ConversationHistory.ImageCarrierText(["pic.png"], "picture"), carrier.Text);   // the tool that fetched it is credited (2026-09-24)
        var part = Assert.Single(carrier.Contents.OfType<DataContent>());
        Assert.Equal(ImageFile.Png, part.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, part.Data.ToArray());
        Assert.Equal(1, history.TurnCount);
        Assert.Equal("a square", history.Messages[^1].Text);
    }

    [Fact]
    public async Task TwoPictureCalls_InOneIteration_ShareOneCarrier()
    {
        var one = new ImageAttachment("a.png", [1], ImageFile.Png, 1, 1);
        var two = new ImageAttachment("b.jpg", [2], ImageFile.Jpeg, 1, 1);
        var (client, history, assistant) = Build(new AIFunction[] { new PictureTool(one), new PictureTool(two, "picture2") });
        client.Enqueue(FakeChatClient.Call("c1", "picture", new Dictionary<string, object?>()), FakeChatClient.Call("c2", "picture2", new Dictionary<string, object?>()));
        client.EnqueueText("two");

        await Run(assistant, "look");

        var second = client.Requests[1];
        Assert.Equal(ChatRole.User, second[^1].Role);
        Assert.Equal(2, second[^1].Contents.OfType<DataContent>().Count());
        Assert.Equal(ConversationHistory.ImageCarrierText(["a.png", "b.jpg"], "picture and picture2"), second[^1].Text);   // both tools credited (2026-09-24)
        Assert.Equal(2, second[^2].Contents.OfType<FunctionResultContent>().Count());
        Assert.Equal(1, history.TurnCount);
    }

    [Fact]
    public async Task OrphanCloseTag_AfterAToolResult_IsDroppedFromTheDeltasAndTheHistory()
    {
        // The field shape of 2026-09-11: after the tool result the model skipped the opener, so the
        // server streamed the closing tag as content.
        var (client, history, assistant) = Build(new AIFunction[] { new EchoTool() });
        client.Enqueue(FakeChatClient.Call("call-1", "echo", new Dictionary<string, object?> { ["text"] = "hi" }));
        client.EnqueueText("Noted.\n", "</think>", "\n\n", "Done.");

        var events = await Run(assistant, "echo hi");

        Assert.Equal(new[] { "Noted.\n", "Done." }, Deltas(events));
        Assert.Equal("Noted.\nDone.", history.Messages[^1].Text);
        Assert.Empty(events.OfType<TurnEvent.Notice>());
    }

    [Fact]
    public async Task ReplyEndingInATagStart_StillShowsIt()
    {
        var (client, history, assistant) = Build();
        client.EnqueueText("1 <", "2");
        client.EnqueueText("a <");

        Assert.Equal(new[] { "1 ", "<2" }, Deltas(await Run(assistant, "x")));
        Assert.Equal(new[] { "a ", "<" }, Deltas(await Run(assistant, "y")));
        Assert.Equal("a <", history.Messages[^1].Text);
    }

    [Fact]
    public void ReplaceText_RemovesEveryTextItem_AndPutsTheCleanedTextFirst()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, new List<AIContent> { new TextReasoningContent("r"), new TextContent("a</think>"), new FunctionCallContent("c", "f") }),
            new(ChatRole.Assistant, new List<AIContent> { new TextContent("b") }),
        };

        Assistant.ReplaceText(messages, "ab");

        Assert.Equal(new[] { "TextContent", "TextReasoningContent", "FunctionCallContent" }, messages[0].Contents.Select(c => c.GetType().Name));
        Assert.Equal("ab", messages[0].Text);
        Assert.Empty(messages[1].Contents);

        Assistant.ReplaceText(messages, "");
        Assert.Equal(new[] { "TextReasoningContent", "FunctionCallContent" }, messages[0].Contents.Select(c => c.GetType().Name));
    }

    [Fact]
    public async Task UnknownTool_StillAnswersWithTheCallId()
    {
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() });
        client.Enqueue(FakeChatClient.Call("c2", "nope"));
        client.EnqueueText("ok");

        var events = await Run(assistant, "x");

        var result = Assert.Single(events.OfType<TurnEvent.ToolResult>());
        Assert.Contains("unknown tool 'nope'", result.Text);
        var fnResult = Assert.Single(client.Requests[1][^1].Contents.OfType<FunctionResultContent>());
        Assert.Equal("c2", fnResult.CallId);
    }

    [Fact]
    public async Task ThrowingTool_AnswersWithTheError_AndTheCallId()
    {
        var (client, _, assistant) = Build(new AIFunction[] { new ThrowingTool() });
        client.Enqueue(FakeChatClient.Call("c3", "boom"));
        client.EnqueueText("ok");

        var events = await Run(assistant, "x");

        var result = Assert.Single(events.OfType<TurnEvent.ToolResult>());
        Assert.Equal("Error: boom failed: kaboom", result.Text);
        Assert.Equal("c3", Assert.Single(client.Requests[1][^1].Contents.OfType<FunctionResultContent>()).CallId);
    }

    [Fact]
    public async Task UnparseableArguments_AreReportedToTheModel_NotInvoked()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        var broken = new FunctionCallContent("c4", "echo") { Exception = new JsonException("bad json") };
        client.Enqueue(new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { broken }));
        client.EnqueueText("ok");

        var events = await Run(assistant, "x");

        Assert.Empty(echo.Received);
        Assert.Contains("could not be parsed", Assert.Single(events.OfType<TurnEvent.ToolResult>()).Text);
    }

    [Fact]
    public async Task CallsAreCollectedFromEveryAssistantMessage()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        // A reasoning model: text in one message, the call in a second (different MessageId).
        client.Enqueue(
            new ChatResponseUpdate(ChatRole.Assistant, "thinking...") { MessageId = "m1" },
            new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c5", "echo", new Dictionary<string, object?> { ["text"] = "z" }) }) { MessageId = "m2" });
        client.EnqueueText("ok");

        await Run(assistant, "x");

        Assert.Equal(new[] { "z" }, echo.Received);
    }

    [Fact]
    public async Task StopsAfterMaxToolIterations_WithAnErrorNotice()
    {
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() });
        assistant.MaxToolIterations = 25;   // the default is the 10,000 cap: too many fake calls to queue
        for (int i = 0; i < assistant.MaxToolIterations + 2; i++)
        {
            client.Enqueue(FakeChatClient.Call($"c{i}", "echo", new Dictionary<string, object?> { ["text"] = "again" }));
        }

        var events = await Run(assistant, "loop");

        Assert.Equal(25, client.Requests.Count);
        var notice = Assert.IsType<TurnEvent.Notice>(events[^1]);
        Assert.True(notice.IsError);
        Assert.Contains("25 tool iterations", notice.Text);
    }

    [Fact]
    public async Task MaxToolIterations_IsPerTurnState_AndNeverBelowOne()
    {
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() });
        Assert.Equal(10000, Assistant.DefaultMaxToolIterations);
        Assert.Equal(AppSettingsData.MaxToolIterationsCap, Assistant.DefaultMaxToolIterations);
        Assert.Equal(Assistant.DefaultMaxToolIterations, assistant.MaxToolIterations);
        Assert.Throws<ArgumentOutOfRangeException>(() => assistant.MaxToolIterations = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => assistant.MaxToolIterations = -3);

        assistant.MaxToolIterations = 1;
        client.Enqueue(FakeChatClient.Call("c0", "echo", new Dictionary<string, object?> { ["text"] = "again" }));
        client.Enqueue(FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = "again" }));

        var events = await Run(assistant, "loop");

        Assert.Single(client.Requests);
        Assert.Equal("Stopped after 1 tool iterations without a final answer.", Assert.IsType<TurnEvent.Notice>(events[^1]).Text);
    }

    /// <summary>The botchat's cap (2026-09-30, the user's report): the last round trip is asked without the tools, and its words end the turn.</summary>
    [Fact]
    public async Task LastRoundAnswers_On_TheLastRoundTripOffersNoTools_AndItsWordsEndTheTurn()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        assistant.MaxToolIterations = 3;
        assistant.LastRoundAnswers = true;
        client.Enqueue(FakeChatClient.Call("c0", "echo", new Dictionary<string, object?> { ["text"] = "one" }));
        client.Enqueue(FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = "two" }));
        client.EnqueueText("My line.");

        var events = await Run(assistant, "go");

        Assert.Equal(3, client.Requests.Count);
        Assert.Single(client.Options[0]!.Tools!);
        Assert.Single(client.Options[1]!.Tools!);
        Assert.Null(client.Options[2]!.Tools);
        Assert.Equal(["one", "two"], echo.Received);
        Assert.Equal("My line.", string.Concat(Deltas(events)));
        Assert.DoesNotContain(events, e => e is TurnEvent.Notice { IsError: true });
    }

    /// <summary>A call written out as text in the round that asked for words is kept out of the reply and not run.</summary>
    [Fact]
    public async Task LastRoundAnswers_On_ACallWrittenAsTextInTheLastRound_IsDroppedAndNotRun()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        assistant.MaxToolIterations = 2;
        assistant.LastRoundAnswers = true;
        assistant.TextToolCalls = true;
        client.Enqueue(FakeChatClient.Call("c0", "echo", new Dictionary<string, object?> { ["text"] = "one" }));
        client.EnqueueText("Here. ", "echo(text=\"two\")", " Done.");

        var events = await Run(assistant, "go");

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(["one"], echo.Received);
        Assert.Single(events.OfType<TurnEvent.ToolCall>());
        Assert.Equal("Here. Done.", string.Concat(Deltas(events)));
        Assert.DoesNotContain(events, e => e is TurnEvent.Notice { IsError: true });
    }

    /// <summary>
    /// The tagged markup (2026-09-30, the user's report): a Qwen-style bot writes its call as <c>&lt;tool_call&gt;</c> markup in the round
    /// asked without the tools, where the server leaves it as text; it is kept out of the reply and not run.
    /// </summary>
    [Fact]
    public async Task LastRoundAnswers_On_ATaggedCallInTheLastRound_IsDroppedAndNotRun()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        assistant.MaxToolIterations = 2;
        assistant.LastRoundAnswers = true;
        assistant.TextToolCalls = true;
        client.Enqueue(FakeChatClient.Call("c0", "echo", new Dictionary<string, object?> { ["text"] = "one" }));
        client.EnqueueText("Here. ", "<tool_call> <function=echo> <parameter=text> two </para", "meter> </function> </tool_call>", " Done.");

        var events = await Run(assistant, "go");

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(["one"], echo.Received);
        Assert.Equal("Here. Done.", string.Concat(Deltas(events)));
        Assert.DoesNotContain(events, e => e is TurnEvent.Notice { IsError: true });
    }

    /// <summary>The same markup in a round that offers the tools runs as a real call.</summary>
    [Fact]
    public async Task TextToolCalls_On_ATaggedCall_Runs()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo });
        assistant.TextToolCalls = true;
        client.EnqueueText("<tool_call>\n<function=echo>\n<parameter=text>\ntwo\n</parameter>\n</function>\n</tool_call>");
        client.EnqueueText("Done.");

        var events = await Run(assistant, "go");

        Assert.Equal(["two"], echo.Received);
        Assert.Equal("Done.", string.Concat(Deltas(events)));
    }

    /// <summary>
    /// WrittenCallsTakenOut (2026-09-30, code review: a bot offered no load_skill wrote a load_skill line its preloaded skill named):
    /// the call is kept out of the reply and never run — with no tool offered, and beside an offered one whose written call runs.
    /// </summary>
    [Fact]
    public async Task WrittenCallsTakenOut_TheCallIsKeptOutOfTheReply_AndNeverRun()
    {
        var (client, _, assistant) = Build();
        assistant.WrittenCallsTakenOut = [LoadSkillTool.WrittenForm];
        client.EnqueueText("load_skill name: pony-prompts file: references/tags.md\n", "Love that wave!");

        var events = await Run(assistant, "go");

        Assert.Single(client.Requests);
        Assert.Equal("Love that wave!", string.Concat(Deltas(events)));
        Assert.Empty(events.OfType<TurnEvent.ToolCall>());

        var echo = new EchoTool();
        var (client2, _, both) = Build(new AIFunction[] { echo });
        both.TextToolCalls = true;
        both.WrittenCallsTakenOut = [LoadSkillTool.WrittenForm];
        client2.EnqueueText("load_skill(name=\"pony-prompts\")\necho(text=\"hi\")");
        client2.EnqueueText("Done.");

        var events2 = await Run(both, "go");

        Assert.Equal(["hi"], echo.Received);
        Assert.Equal("echo", Assert.Single(events2.OfType<TurnEvent.ToolCall>()).Name);
        Assert.Equal("Done.", string.Concat(Deltas(events2)));
    }

    [Fact]
    public async Task LastRoundAnswers_Off_TheLastRoundTripStillOffersTheTools()
    {
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() });
        assistant.MaxToolIterations = 2;
        client.Enqueue(FakeChatClient.Call("c0", "echo", new Dictionary<string, object?> { ["text"] = "one" }));
        client.Enqueue(FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = "two" }));

        var events = await Run(assistant, "go");

        Assert.Single(client.Options[1]!.Tools!);
        Assert.Equal("Stopped after 2 tool iterations without a final answer.", Assert.IsType<TurnEvent.Notice>(events[^1]).Text);
    }

    [Fact]
    public async Task ClientFailsMidStream_CommitsPartialText_AndNotices_WithoutThrowing()
    {
        var (client, history, assistant) = Build();
        client.EnqueueText("par", "tial");
        client.ThrowAt = 1;
        client.Failure = new HttpRequestException("boom", new IOException("socket closed"));

        var events = await Run(assistant, "x");

        Assert.Equal(new[] { "par" }, Deltas(events));
        var notice = Assert.IsType<TurnEvent.Notice>(events[^1]);
        Assert.True(notice.IsError);
        Assert.Contains("HttpRequestException: boom -> IOException: socket closed", notice.Text);
        Assert.Equal("par", history.Messages[^1].Text);
        Assert.Equal(ChatRole.Assistant, history.Messages[^1].Role);
    }

    [Fact]
    public async Task ClientFailsBeforeAnyText_NoticesOnly()
    {
        var (client, history, assistant) = Build();
        client.EnqueueText("never");
        client.ThrowAt = 0;

        var events = await Run(assistant, "x");

        Assert.Single(events);
        Assert.IsType<TurnEvent.Notice>(events[0]);
        Assert.Single(history.Messages); // just the user message
    }

    [Fact]
    public async Task CancellationMidStream_CommitsPartialText_AndRethrows()
    {
        var (client, history, assistant) = Build();
        using var cts = new CancellationTokenSource();
        client.EnqueueText("Hel", "lo");
        client.BeforeUpdate = (i, _) =>
        {
            if (i == 1) cts.Cancel();
            return Task.CompletedTask;
        };

        var events = new List<TurnEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in assistant.RunTurnAsync("x", cts.Token)) events.Add(evt);
        });

        Assert.Equal(new[] { "Hel" }, Deltas(events));
        Assert.Equal("Hel", history.Messages[^1].Text);
    }

    [Fact]
    public async Task TurnDeadline_IsCheckedAtTheLoopBoundary_NotMidRequest()
    {
        var clock = new ManualTimeProvider();
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() }, clock, new LlmTimeouts(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        client.Enqueue(FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = "slow" }));
        client.EnqueueText("never sent");
        client.BeforeUpdate = (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(3)); // the first request itself blows the budget
            return Task.CompletedTask;
        };

        var events = await Run(assistant, "x");

        Assert.Single(client.Requests);                       // the tool still ran; the second request never went out
        Assert.Single(events.OfType<TurnEvent.ToolResult>());
        var notice = Assert.IsType<TurnEvent.Notice>(events[^1]);
        Assert.True(notice.IsError);
        Assert.Contains("Turn budget of 2s exhausted", notice.Text);
    }

    [Fact]
    public void DefaultSystemPrompt_IsPinned()
    {
        Assert.Equal(
            Assistant.DefaultPersona + "\n\n" +   // the persona is the repo's asset since 2026-10-03, pinned against the file on its own
            "Reply in plain text: no Markdown headings or tables, and no code fences unless the user asks for code. " +
            "Use a tool when it helps; otherwise answer directly. " +
            "You do not know the current date or time; call get_current_time when a question depends on it, " +
            "and use shift_date or date_difference for calendar arithmetic instead of counting yourself. " +
            "For a countdown, use start_timer, stop_timer and list_timers; never guess what is left on a timer. " +
            "The user's working directory — also called the cwd, the current directory or the current working directory — is a folder on this computer where you may read, search, write and organise files with the file tools " +
            "(get_working_directory gives its path); every path you pass is relative to it and the file tools reach nothing outside it; delete removes a file or a folder for good, with everything in it. " +
            "To look at a picture (png, jpg, gif, webp, bmp) in the working directory call view_image (several at once with paths); read_file cannot read one.",
            Assistant.DefaultSystemPrompt);
        Assert.Equal(10000, Assistant.DefaultMaxToolIterations);
    }

    [Fact]
    public void Explain_UnwrapsUpToFourCauses()
    {
        var ex = new InvalidOperationException("a", new IOException("b", new ArgumentException("c", new Exception("d", new Exception("e")))));
        Assert.Equal("InvalidOperationException: a -> IOException: b -> ArgumentException: c -> Exception: d", Assistant.Explain(ex));
    }

    [Fact]
    public void Explain_SkipsAggregateWrappers_AndRepeatedMessages()
    {
        // The shape the SDK produces for a refused connection: an aggregate whose message repeats
        // every attempt, wrapping the same message three times over.
        var refused = new HttpRequestException("refused (127.0.0.1:9)", new IOException("refused"));
        var ex = new AggregateException("Retry failed after 4 tries. (refused) (refused)",
            new InvalidOperationException("refused (127.0.0.1:9)", refused));

        Assert.Equal("InvalidOperationException: refused (127.0.0.1:9) -> IOException: refused", Assistant.Explain(ex));
        Assert.Equal("AggregateException: empty", Assistant.Explain(new AggregateException("empty")));
    }

    [Fact]
    public void ServerMessage_TakesTheOpenAIErrorShape_OrTheBody_OnOneLine()
    {
        // SGLang's shape (top-level message) and OpenAI's (under "error").
        Assert.Equal("Unexpected reasoning effort high. Supported types are xhigh (default), medium, and low.",
            Assistant.ServerMessage("{\"object\":\"error\",\"message\":\"Unexpected reasoning effort high. Supported types are xhigh (default), medium, and low.\",\"type\":\"BadRequestError\",\"code\":400}"));
        Assert.Equal("model not found", Assistant.ServerMessage("{\"error\":{\"message\":\"model not found\",\"type\":\"invalid_request_error\"}}"));
        Assert.Equal("{\"error\":\"plain\"}", Assistant.ServerMessage("{\"error\":\"plain\"}"));      // no message property: the body itself
        Assert.Equal("502 Bad Gateway from nginx", Assistant.ServerMessage("  502 Bad Gateway\n  from   nginx \n"));
        Assert.Equal("{not json", Assistant.ServerMessage("{not json"));
        Assert.Equal("", Assistant.ServerMessage(""));
        Assert.Equal(new string('x', Assistant.MaxServerDetail) + "…", Assistant.ServerMessage(new string('x', Assistant.MaxServerDetail + 50)));
    }

    [Fact]
    public void SerializeArguments_WritesEveryKindByHand()
    {
        using var doc = JsonDocument.Parse("{\"n\":[1,2]}");
        var args = new Dictionary<string, object?>
        {
            ["s"] = "x\"y",
            ["b"] = true,
            ["i"] = 3,
            ["l"] = 4L,
            ["d"] = 1.5,
            ["m"] = 2.25m,
            ["nul"] = null,
            ["el"] = doc.RootElement.GetProperty("n"),
            ["date"] = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc),
        };

        string json = Assistant.SerializeArguments(args);

        Assert.Equal("{\"s\":\"x\\u0022y\",\"b\":true,\"i\":3,\"l\":4,\"d\":1.5,\"m\":2.25,\"nul\":null,\"el\":[1,2],\"date\":\"09/07/2026 00:00:00\"}", json);
        Assert.Equal("{}", Assistant.SerializeArguments(null));
    }

    // ── The opening calls ───────────────────────────────────────────────────

    private const string ClockText = "Friday 11 September 2026, 14:05 (Pacific Daylight Time, UTC-07:00)";
    private const string CwdText = @"The working directory is 'D:\files' (the profile's default folder); every path you pass to a file tool is relative to it.";

    private static (FakeChatClient Client, ConversationHistory History, Assistant Assistant) BuildWithOpening()
    {
        var clock = new NeonSidekick.Llm.Tools.GetCurrentTimeTool(new ManualTimeProvider());
        var cwd = new FixedTool("get_working_directory", CwdText);
        var built = Build(new AIFunction[] { clock, cwd, new EchoTool() });
        built.Assistant.OpeningCalls = [new(clock, Assistant.OpeningClockCallId), new(cwd, Assistant.OpeningCwdCallId)];
        return built;
    }

    [Fact]
    public async Task FirstTurn_OpensWithTheClockAndTheCwd_BeforeTheFirstRequest()
    {
        var (client, history, assistant) = BuildWithOpening();
        client.EnqueueText("Hello.");

        var events = await Run(assistant, "hi");

        // The four events first, in list order, then the text; all flagged as opening events.
        var call = Assert.IsType<TurnEvent.ToolCall>(events[0]);
        Assert.Equal(("get_current_time", Assistant.OpeningClockCallId, "{}"), (call.Name, call.CallId, call.ArgumentsJson));
        var result = Assert.IsType<TurnEvent.ToolResult>(events[1]);
        Assert.Equal(("get_current_time", Assistant.OpeningClockCallId, ClockText), (result.Name, result.CallId, result.Text));
        var cwdCall = Assert.IsType<TurnEvent.ToolCall>(events[2]);
        Assert.Equal(("get_working_directory", Assistant.OpeningCwdCallId, "{}"), (cwdCall.Name, cwdCall.CallId, cwdCall.ArgumentsJson));
        var cwdResult = Assert.IsType<TurnEvent.ToolResult>(events[3]);
        Assert.Equal(("get_working_directory", Assistant.OpeningCwdCallId, CwdText), (cwdResult.Name, cwdResult.CallId, cwdResult.Text));
        Assert.All(events.Take(4), e => Assert.True(Assistant.IsOpeningEvent(e)));
        Assert.False(Assistant.IsOpeningEvent(events[4]));
        Assert.Equal(new[] { "Hello." }, Deltas(events));

        // Not a request: the one request carries both pairs inside the user's turn, one after the other.
        var request = Assert.Single(client.Requests);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, request.Select(m => m.Role));
        Assert.Equal("hi", request[1].Text);
        Assert.Equal(Assistant.OpeningClockCallId, Assert.Single(request[2].Contents.OfType<FunctionCallContent>()).CallId);
        var fnResult = Assert.Single(request[3].Contents.OfType<FunctionResultContent>());
        Assert.Equal(Assistant.OpeningClockCallId, fnResult.CallId);
        Assert.Equal(ClockText, fnResult.Result);
        Assert.Equal(Assistant.OpeningCwdCallId, Assert.Single(request[4].Contents.OfType<FunctionCallContent>()).CallId);
        var cwdFnResult = Assert.Single(request[5].Contents.OfType<FunctionResultContent>());
        Assert.Equal(Assistant.OpeningCwdCallId, cwdFnResult.CallId);
        Assert.Equal(CwdText, cwdFnResult.Result);
        Assert.Equal("Hello.", history.Messages[^1].Text);
    }

    [Fact]
    public async Task OpeningCalls_OncePerConversation_AndAgainAfterClear()
    {
        var (client, history, assistant) = BuildWithOpening();
        client.EnqueueText("one");
        client.EnqueueText("two");
        client.EnqueueText("three");

        var first = await Run(assistant, "a");
        var second = await Run(assistant, "b");
        history.Clear();
        var third = await Run(assistant, "c");

        Assert.Equal(4, first.Count(Assistant.IsOpeningEvent));
        Assert.DoesNotContain(second, Assistant.IsOpeningEvent);
        Assert.Equal(4, third.Count(Assistant.IsOpeningEvent));
        Assert.Equal(1, client.Requests[1].Count(m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == Assistant.OpeningClockCallId)));
        Assert.Equal(1, client.Requests[1].Count(m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == Assistant.OpeningCwdCallId)));
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, client.Requests[2].Select(m => m.Role));
    }

    [Fact]
    public async Task OpeningCalls_AreNotTheModelsCallsAndNotIterations()
    {
        // A model that calls the clock itself on the first turn: both pairs, distinct ids, and
        // the opening pairs never count against the iteration budget.
        var (client, _, assistant) = BuildWithOpening();
        for (int i = 0; i < assistant.MaxToolIterations + 2; i++)
        {
            client.Enqueue(FakeChatClient.Call($"c{i}", "echo", new Dictionary<string, object?> { ["text"] = "again" }));
        }

        var events = await Run(assistant, "loop");

        Assert.Equal(assistant.MaxToolIterations, client.Requests.Count);
        Assert.Equal(4, events.Count(Assistant.IsOpeningEvent));
        Assert.Equal(assistant.MaxToolIterations, events.OfType<TurnEvent.ToolCall>().Count(c => c.Name == "echo"));
        Assert.True(Assert.IsType<TurnEvent.Notice>(events[^1]).IsError);
    }

    [Fact]
    public async Task NoOpeningCalls_SeedNothing()
    {
        var (client, _, assistant) = Build(new AIFunction[] { new NeonSidekick.Llm.Tools.GetCurrentTimeTool(new ManualTimeProvider()) });
        client.EnqueueText("Hello.");

        var events = await Run(assistant, "hi");

        Assert.Equal(new[] { ChatRole.System, ChatRole.User }, Assert.Single(client.Requests).Select(m => m.Role));
        Assert.Single(events);
    }

    [Fact]
    public async Task OpeningCall_ThatThrows_SeedsTheErrorSentence_AndTheTurnGoesOn()
    {
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() });
        assistant.OpeningCalls = [new(new ThrowingTool(), Assistant.OpeningClockCallId)];
        client.EnqueueText("Hello.");

        var events = await Run(assistant, "hi");

        var result = Assert.IsType<TurnEvent.ToolResult>(events[1]);
        Assert.Equal("Error: boom failed: kaboom", result.Text);
        Assert.Equal(new[] { "Hello." }, Deltas(events));
        var request = Assert.Single(client.Requests);
        Assert.Equal("Error: boom failed: kaboom", Assert.Single(request[3].Contents.OfType<FunctionResultContent>()).Result);
    }

    [Fact]
    public void OpeningCallIds_AreNineAlphanumerics_AndDistinct()
    {
        // Mistral-family templates validate a tool call id as exactly nine alphanumerics.
        var ids = new[] { Assistant.OpeningClockCallId, Assistant.OpeningCwdCallId, Assistant.OpeningMemoryCallId };
        foreach (string id in ids)
        {
            Assert.Equal(9, id.Length);
            Assert.All(id, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        }

        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("neonmemry", Assistant.OpeningMemoryCallId);
    }

    // ── The skills and the project notes (2026-09-16) ───────────────────────

    private static readonly Skill Haiku = new("haiku", "Writes haiku.", SkillScope.Profile, @"D:\home\profiles\default\skills\haiku");

    [Fact]
    public void SystemPrompt_ProjectNotes_FollowTheRules_AsTheirOwnBlock_ToolsOrNot()
    {
        var notes = new ProjectNotes("NEON.md", "This folder is a .NET solution.");
        Assert.Equal("Notes for the working directory (from NEON.md):", Assistant.ProjectNotesHeading("NEON.md"));
        Assert.Equal("Notes for the working directory (from NEON.md):\nThis folder is a .NET solution.", Assistant.ProjectNotesSection(notes));
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + Assistant.ProjectNotesSection(notes), Assistant.SystemPrompt(false, null, project: notes));
        // After the rules, before the memory section, the skills and the directive.
        Assert.Equal(
            Assistant.DefaultSystemPrompt + "\n\n" + Assistant.ProjectNotesSection(notes) + "\n\n" + MemoryPrompt.Directive + "\n\n" + SkillsPrompt.Section([Haiku]) + "\n\n" + Pirate,
            Assistant.SystemPrompt(true, [], voiceDirective: Pirate, project: notes, skills: [Haiku]));
        Assert.Equal("Be terse.\n\nAnswer in haiku.\n\n" + Assistant.ProjectNotesSection(notes), Assistant.SystemPrompt(false, null, persona: "Be terse.", operatingRules: "Answer in haiku.", project: notes));
        // LLM offer tools off keeps the notes: they are context, not a tool.
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule + "\n\n" + Assistant.ProjectNotesSection(notes), Assistant.SystemPrompt(false, null, tools: false, project: notes));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, project: null));
    }

    [Fact]
    public void SystemPrompt_Skills_FollowTheMemorySection_AndGoWithTheTools()
    {
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + SkillsPrompt.Section([Haiku]), Assistant.SystemPrompt(false, null, skills: [Haiku]));
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + SkillsPrompt.DirectiveWithoutSkills, Assistant.SystemPrompt(false, null, skills: []));
        Assert.Equal(Assistant.DefaultSystemPrompt, Assistant.SystemPrompt(false, null, skills: null));
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + MemoryPrompt.Directive + "\n\n" + SkillsPrompt.Section([Haiku]), Assistant.SystemPrompt(false, [], skills: [Haiku]));
        Assert.Equal(Assistant.DefaultSystemPrompt + "\n\n" + SkillsPrompt.Section([Haiku]) + "\n\n" + Pirate, Assistant.SystemPrompt(true, null, voiceDirective: Pirate, skills: [Haiku]));
        // LLM offer tools off: nothing could load one, so the block is dropped whatever was passed.
        Assert.Equal(Assistant.DefaultPersona + "\n\n" + Assistant.PlainTextRule, Assistant.SystemPrompt(false, null, tools: false, skills: [Haiku]));
        // A custom operata.md leaves the block in place: it is not a rule sentence.
        Assert.Equal(Assistant.DefaultPersona + "\n\nAnswer in haiku.\n\n" + SkillsPrompt.Section([Haiku]), Assistant.SystemPrompt(false, null, operatingRules: "Answer in haiku.", skills: [Haiku]));
    }

    [Fact]
    public async Task TheOpeningCalls_CarryNoArguments_AndOnlyTheFirstTurnSeedsThem()
    {
        // /skill <name> seeded a load_skill pair with the skill's name here from 2026-09-16 until later
        // on 2026-09-18; an OpeningCall is a tool and a fixed id now, its arguments always empty.
        var (client, _, assistant) = BuildWithOpening();
        client.EnqueueText("Hello.");
        client.EnqueueText("Again.");

        var first = await Run(assistant, "hi");

        Assert.Equal(4, first.Count(Assistant.IsOpeningEvent));
        Assert.All(first.OfType<TurnEvent.ToolCall>(), call => Assert.Equal("{}", call.ArgumentsJson));
        var request = Assert.Single(client.Requests);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, request.Select(m => m.Role));

        var second = await Run(assistant, "more");
        Assert.DoesNotContain(second, Assistant.IsOpeningEvent);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.User }, client.Requests[1].Select(m => m.Role));
    }

    [Fact]
    public async Task AModelsLoadSkillResult_IsTagged_AnErrorIsNot_OtherToolsNever()
    {
        var load = new FixedTool(LoadSkillTool.ToolName, "<skill_content name=\"haiku\">\nbody\n</skill_content>");
        var (client, _, assistant) = Build(new AIFunction[] { load, new EchoTool() });
        client.Enqueue(FakeChatClient.Call("c1", LoadSkillTool.ToolName, new Dictionary<string, object?> { ["name"] = "haiku" }), FakeChatClient.Call("c2", "echo", new Dictionary<string, object?> { ["text"] = "hi" }));
        client.EnqueueText("done");

        await Run(assistant, "go");

        var results = client.Requests[1][^1].Contents.OfType<FunctionResultContent>().ToList();
        Assert.Equal(["c1", "c2"], results.Select(r => r.CallId));
        Assert.True(ConversationHistory.IsSkillResult(results[0]));
        Assert.False(ConversationHistory.IsSkillResult(results[1]));

        var error = Assistant.ResultContent(new FunctionCallContent("c3", LoadSkillTool.ToolName), "Error: there is no skill named 'x'; no skill is installed");
        Assert.False(ConversationHistory.IsSkillResult(error));
        Assert.Null(error.AdditionalProperties);
        Assert.Equal("neon.skillResult", ConversationHistory.SkillResultKey);
    }

    // ── Usage ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Meter_CountsTheContentChunksWhileTheRequestStreams_AndEndsWithIt()
    {
        var clock = new ManualTimeProvider();
        var (client, _, assistant) = Build(time: clock);
        assistant.Meter = new StreamMeter(clock);
        client.Enqueue(FakeChatClient.Text("Hel"), FakeChatClient.Text("lo"), FakeChatClient.Usage(10, 5));
        client.BeforeUpdate = (i, _) =>
        {
            clock.Advance(i == 0 ? TimeSpan.FromMilliseconds(800) : TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        };

        var seen = new List<StreamMeter.Reading>();
        await foreach (var evt in assistant.RunTurnAsync("hi"))
        {
            seen.Add(assistant.Meter.Read());
        }

        // Each delta read mid-stream: one chunk, then two over the second since the first; the usage report is no chunk.
        Assert.Equal(new StreamMeter.Reading(true, 1, null), seen[0]);
        Assert.Equal(new StreamMeter.Reading(true, 2, 2.0), seen[1]);
        // The usage event comes after the stream: the meter has ended.
        Assert.Equal(StreamMeter.Reading.Idle, seen[^1]);
        Assert.Equal(StreamMeter.Reading.Idle, assistant.Meter.Read());
    }

    [Fact]
    public async Task Meter_EndsWhenTheTurnIsCancelledMidStream()
    {
        var (client, _, assistant) = Build();
        assistant.Meter = new StreamMeter();
        client.EnqueueText("a", "b", "c");
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in assistant.RunTurnAsync("hi", cts.Token))
            {
                Assert.True(assistant.Meter.Read().Streaming);
                cts.Cancel();
            }
        });

        Assert.Equal(StreamMeter.Reading.Idle, assistant.Meter.Read());
    }

    [Fact]
    public async Task Usage_IsRaisedAfterTheStream_WithTheRequestsTwoPhases()
    {
        var clock = new ManualTimeProvider();
        var (client, history, assistant) = Build(time: clock);
        client.Enqueue(FakeChatClient.Text("Hel"), FakeChatClient.Text("lo"), FakeChatClient.Usage(10, 5));
        client.BeforeUpdate = (i, _) =>
        {
            // The server reads the prompt for 800 ms, then streams for 2 s; the usage chunk is last.
            clock.Advance(i switch { 0 => TimeSpan.FromMilliseconds(800), 2 => TimeSpan.FromSeconds(2), _ => TimeSpan.Zero });
            return Task.CompletedTask;
        };

        var events = await Run(assistant, "hi");

        Assert.Equal(new[] { "Hel", "lo" }, Deltas(events));
        var usage = Assert.IsType<TurnEvent.Usage>(events[^1]);
        Assert.Equal(new TokenUsage(10, 5, 15, 1, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(2)), usage.Tokens);
        Assert.Equal(2.5, usage.Tokens.TokensPerSecond);

        // The report is not content: it never goes back to the server as part of the reply.
        Assert.Equal(2, history.Messages.Count);
        Assert.DoesNotContain(history.Messages[1].Contents, c => c is UsageContent);
        Assert.Equal("Hello", history.Messages[1].Text);
    }

    [Fact]
    public async Task Usage_ARoleOnlyLeadingChunk_DoesNotEndTheWait()
    {
        var clock = new ManualTimeProvider();
        var (client, _, assistant) = Build(time: clock);
        client.Enqueue(new ChatResponseUpdate { Role = ChatRole.Assistant }, FakeChatClient.Text("x"), FakeChatClient.Usage(1, 1));
        client.BeforeUpdate = (i, _) =>
        {
            clock.Advance(i switch { 0 => TimeSpan.FromMilliseconds(500), 1 => TimeSpan.FromMilliseconds(300), _ => TimeSpan.FromSeconds(1) });
            return Task.CompletedTask;
        };

        var events = await Run(assistant, "hi");

        var usage = Assert.IsType<TurnEvent.Usage>(events[^1]);
        Assert.Equal(TimeSpan.FromMilliseconds(800), usage.Tokens.ToFirstToken);
        Assert.Equal(TimeSpan.FromSeconds(1), usage.Tokens.Generating);
    }

    [Fact]
    public async Task Usage_ComesOncePerRequest_BeforeItsToolCallsRun()
    {
        var clock = new ManualTimeProvider();
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() }, clock);
        client.Enqueue(FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = "hi" }), FakeChatClient.Usage(50, 8));
        client.Enqueue(FakeChatClient.Text("done"), FakeChatClient.Usage(70, 3));
        client.BeforeUpdate = (_, _) => { clock.Advance(TimeSpan.FromSeconds(1)); return Task.CompletedTask; };

        var events = await Run(assistant, "x");

        Assert.Equal(
            new[] { typeof(TurnEvent.Usage), typeof(TurnEvent.ToolCall), typeof(TurnEvent.ToolResult), typeof(TurnEvent.TextDelta), typeof(TurnEvent.Usage) },
            events.Select(e => e.GetType()));
        var reports = events.OfType<TurnEvent.Usage>().Select(u => u.Tokens).ToArray();
        Assert.Equal(new TokenUsage(50, 8, 58, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), reports[0]);
        Assert.Equal(new TokenUsage(70, 3, 73, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), reports[1]);
        Assert.Equal(new TokenUsage(120, 11, 131, 2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)), reports[0] + reports[1]);
    }

    [Fact]
    public async Task Usage_IsAbsent_WhenTheServerSaysNothing_OrTheStreamFails()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("quiet");
        Assert.Empty((await Run(assistant, "a")).OfType<TurnEvent.Usage>());

        client.Enqueue(FakeChatClient.Text("x"), FakeChatClient.Usage(1, 1));
        client.ThrowAt = 2;   // after the usage chunk, before the stream ends: the request failed
        var events = await Run(assistant, "b");
        Assert.Empty(events.OfType<TurnEvent.Usage>());
        Assert.True(Assert.IsType<TurnEvent.Notice>(events[^1]).IsError);
    }

    [Fact]
    public async Task Usage_ASecondReport_AddsItsCounts_NotItsTime()
    {
        var clock = new ManualTimeProvider();
        var (client, _, assistant) = Build(time: clock);
        client.Enqueue(FakeChatClient.Text("x"), FakeChatClient.Usage(10, 5), FakeChatClient.Usage(1, 1));
        client.BeforeUpdate = (_, _) => { clock.Advance(TimeSpan.FromSeconds(1)); return Task.CompletedTask; };

        var events = await Run(assistant, "hi");

        var usage = Assert.Single(events.OfType<TurnEvent.Usage>());
        Assert.Equal(new TokenUsage(11, 6, 17, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), usage.Tokens);
    }

    [Fact]
    public async Task Usage_CarriesTheReasoningCount_WhenTheServerCountedIt()
    {
        var (client, _, assistant) = Build();
        client.Enqueue(FakeChatClient.Text("x"), FakeChatClient.Usage(10, 5, reasoning: 4));
        var counted = Assert.Single((await Run(assistant, "a")).OfType<TurnEvent.Usage>()).Tokens;
        Assert.Equal(4, counted.Reasoning);

        // Two reports in one request, one of them without the count: the count that was reported, not a dash.
        client.Enqueue(FakeChatClient.Text("y"), FakeChatClient.Usage(10, 5), FakeChatClient.Usage(1, 1, reasoning: 1));
        var mixed = Assert.Single((await Run(assistant, "b")).OfType<TurnEvent.Usage>()).Tokens;
        Assert.Equal(1, mixed.Reasoning);
        Assert.Equal(2, mixed.Requests);

        client.Enqueue(FakeChatClient.Text("z"), FakeChatClient.Usage(10, 5));
        Assert.Null(Assert.Single((await Run(assistant, "c")).OfType<TurnEvent.Usage>()).Tokens.Reasoning);
    }

    [Fact]
    public void UsageLogLine_IsPinned()
    {
        Assert.Equal(
            "Usage: 22 in, 139 out (135 reasoning), 161 total; first token after 0.80s, streamed 2.10s.",
            Assistant.UsageLogLine(new TokenUsage(22, 139, 161, 1, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(2.1), 135)));
        Assert.Equal(
            "Usage: 10 in, 5 out, 15 total; first token after 0.00s, streamed 0.00s.",
            Assistant.UsageLogLine(new TokenUsage(10, 5, 15, 1, TimeSpan.Zero, TimeSpan.Zero)));
    }

    // ── SummarizeAsync (/compact) ───────────────────────────────────────────

    [Fact]
    public async Task Summarize_OneRequest_NoTools_ThinkingOff_TheHistoryUntouched_TimedLikeATurn()
    {
        var clock = new ManualTimeProvider();
        var (client, history, assistant) = Build(new AIFunction[] { new EchoTool() }, clock, reasoning: ReasoningEffort.High);
        history.AddUser("kept");
        client.Enqueue(FakeChatClient.Text("<think>hmm</think>"), FakeChatClient.Text("The user said hi."), FakeChatClient.Usage(40, 6));
        client.BeforeUpdate = (i, _) =>
        {
            clock.Advance(i switch { 0 => TimeSpan.FromMilliseconds(500), 2 => TimeSpan.FromSeconds(3), _ => TimeSpan.Zero });
            return Task.CompletedTask;
        };
        var transcript = new List<ChatMessage> { new(ChatRole.User, "hi"), new(ChatRole.Assistant, "hello") };

        var (text, usage) = await assistant.SummarizeAsync(transcript, "the greeting", CancellationToken.None);

        Assert.Equal("The user said hi.", text);
        Assert.Equal(new TokenUsage(40, 6, 46, 1, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3)), usage);

        var request = Assert.Single(client.Requests);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User }, request.Select(m => m.Role));
        Assert.Equal(ConversationCompactor.SummaryInstruction, request[0].Text);
        Assert.Equal("Summarise the conversation above. Pay particular attention to: the greeting", request[3].Text);
        var options = Assert.Single(client.Options);
        Assert.Null(options!.Tools);                                        // the tools of a turn are not offered
        Assert.Equal(ReasoningEffort.None, options.Reasoning!.Effort);      // whatever the turns ask for

        var kept = Assert.Single(history.Messages);
        Assert.Equal("kept", kept.Text);
    }

    [Fact]
    public async Task Summarize_NoUsageReport_IsNull_AndAnEmptyAnswerThrows()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("A ", "summary.");

        var (text, usage) = await assistant.SummarizeAsync([new ChatMessage(ChatRole.User, "hi")], null, CancellationToken.None);

        Assert.Equal("A summary.", text);
        Assert.Null(usage);
        Assert.Equal(ConversationCompactor.SummaryRequestLine, client.Requests[0][^1].Text);

        client.Enqueue(FakeChatClient.Usage(3, 0)).Enqueue(FakeChatClient.Usage(3, 0));
        var ex = await Assert.ThrowsAsync<EmptySummaryException>(() => assistant.SummarizeAsync([new ChatMessage(ChatRole.User, "hi")], null, CancellationToken.None));
        Assert.Equal(Assistant.EmptySummaryError, ex.Message);
        Assert.Equal(3, client.Requests.Count);   // the empty answer was asked for twice
    }

    /// <summary>2026-09-28: an empty answer is asked for once more over the lean transcript, and the two requests' usage is summed.</summary>
    [Fact]
    public async Task Summarize_AnEmptyAnswer_IsAskedAgainOverTheLeanTranscript_TheUsageSummed()
    {
        var (client, _, assistant) = Build();
        client.Enqueue(new ChatResponseUpdate(ChatRole.Assistant, (string?)null) { FinishReason = ChatFinishReason.Length }, FakeChatClient.Usage(100, 5));
        client.Enqueue(FakeChatClient.Text("The user read a.txt."), FakeChatClient.Usage(40, 6));
        var transcript = new List<ChatMessage>
        {
            new(ChatRole.User, "read it"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "a.txt" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', ConversationCompactor.LeanResultChars + 500))]),
            new(ChatRole.Assistant, "done"),
        };

        var (text, usage) = await assistant.SummarizeAsync(transcript, null, CancellationToken.None);

        Assert.Equal("The user read a.txt.", text);
        Assert.Equal((140L, 11L, 2), (usage!.Value.Input, usage.Value.Output, usage.Value.Requests));
        Assert.Equal(2, client.Requests.Count);
        var lean = client.Requests[1];
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User }, lean.Select(m => m.Role));
        Assert.Equal(ConversationCompactor.SummaryInstruction, lean[0].Text);
        Assert.DoesNotContain(lean, m => m.Contents.Any(c => c is FunctionCallContent or FunctionResultContent));
        Assert.StartsWith("[called read_file ", lean[2].Text);
        Assert.EndsWith(ConversationCompactor.LeanCut(500), lean[3].Text);
        Assert.Equal(ConversationCompactor.SummaryRequestLine, lean[5].Text);
    }

    /// <summary>2026-09-28: a second empty answer says why — the length limit, thinking alone, a tool call.</summary>
    [Fact]
    public async Task Summarize_TwoEmptyAnswers_SayWhy()
    {
        static ChatResponseUpdate Stopped() => new(ChatRole.Assistant, (string?)null) { FinishReason = ChatFinishReason.Length };
        static ChatResponseUpdate Thought() => new(ChatRole.Assistant, [new TextReasoningContent("hmm, hmm")]);

        async Task<string> Fails(Func<ChatResponseUpdate> update)
        {
            var (client, _, assistant) = Build();
            client.Enqueue(update()).Enqueue(update());
            var ex = await Assert.ThrowsAsync<EmptySummaryException>(() => assistant.SummarizeAsync([new ChatMessage(ChatRole.User, "hi")], null, CancellationToken.None));
            Assert.Equal(2, client.Requests.Count);
            return ex.Message;
        }

        Assert.Equal(Assistant.EmptySummaryLengthError, await Fails(Stopped));
        Assert.Equal(Assistant.EmptySummaryThinkingError(8), await Fails(Thought));
        Assert.Equal(Assistant.EmptySummaryToolError("read_file"), await Fails(() => FakeChatClient.Call("c9", "read_file")));
        Assert.Equal(Assistant.EmptySummaryThinkingError(18), await Fails(() => FakeChatClient.Text("<think>hmm</think>")));   // a <think> block streamed as text
        Assert.Contains("(1,234 characters)", Assistant.EmptySummaryThinkingError(1234));
    }

    // ── The picture budget and a dropped request (2026-10-03) ─────────

    /// <summary>The socket reset a llama-server's refusal of an oversized body reads as, the way the SDK wraps it.</summary>
    private static HttpRequestException ConnectionReset() =>
        new("Error while copying content to a stream.", new IOException("Unable to write data to the transport connection.", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)));

    private static ImageAttachment Shot(string path) => new(path, new byte[300_000], ImageFile.Jpeg, 8, 8);

    /// <summary>A history holding <paramref name="count"/> earlier turns of one picture each.</summary>
    private static void Pictures(ConversationHistory history, int count)
    {
        for (int i = 1; i <= count; i++)
        {
            history.AddUser("look " + i, [Shot("shot" + i + ".png")]);
            history.AddAssistant("seen " + i);
        }
    }

    private static int PicturesIn(IEnumerable<ChatMessage> messages) => messages.Sum(m => m.Contents.Count(c => c is DataContent));

    [Fact]
    public void LooksLikeOversizedRequest_IsAResetOrA413()
    {
        Assert.True(Assistant.LooksLikeOversizedRequest(ConnectionReset()));
        Assert.True(Assistant.LooksLikeOversizedRequest(new HttpRequestException("too big", null, System.Net.HttpStatusCode.RequestEntityTooLarge)));
        Assert.True(Assistant.LooksLikeOversizedRequest(new AggregateException(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionAborted))));
        Assert.False(Assistant.LooksLikeOversizedRequest(new HttpRequestException("boom", new IOException("socket closed"))));
        Assert.False(Assistant.LooksLikeOversizedRequest(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
    }

    [Fact]
    public void TheDroppedRequestWording_IsPinned()
    {
        Assert.Equal("The server dropped the request as it was sent (118 MB, 54 pictures); the 27 oldest pictures are now their file names. Trying again.", Assistant.DroppedRequestNotice(118_000_000, 54, 27));
        Assert.Equal("The server dropped the request as it was sent (3 MB, 2 pictures); the oldest picture is now its file name. Trying again.", Assistant.DroppedRequestNotice(3_000_000, 2, 1));
        Assert.Equal(" — the request was 118 MB with 54 pictures; /compact prune takes older pictures out, and LLM picture keep (the LLM tab of /settings) caps them", Assistant.OversizedRequestHint(118_000_000, 54));
    }

    [Fact]
    public async Task PicturesOverTheBudget_AreTakenOutBeforeTheRequest_WithANotice()
    {
        var (client, history, assistant) = Build();
        Pictures(history, 4);
        assistant.PictureBudget = new PictureBudget(2, 0);
        client.EnqueueText("ok");

        var events = await Run(assistant, "and now?");

        var notice = Assert.IsType<TurnEvent.Notice>(events.First(e => e is TurnEvent.Notice));
        Assert.False(notice.IsError);
        Assert.Equal(PictureBudget.TakenOutNotice(3, 1_200_000), notice.Text);   // 4 → 1, half of 2; the newest stays
        Assert.Equal(1, PicturesIn(client.Requests[0]));
        Assert.Equal(1, PicturesIn(history.Messages));                             // out of the history itself
    }

    [Fact]
    public async Task ADefaultBudget_UnderItsCaps_TakesNothingOut()
    {
        var (client, history, assistant) = Build();
        Pictures(history, 3);
        client.EnqueueText("ok");

        var events = await Run(assistant, "and now?");

        Assert.DoesNotContain(events, e => e is TurnEvent.Notice);
        Assert.Equal(3, PicturesIn(client.Requests[0]));
    }

    [Fact]
    public async Task ARequestWithPictures_DroppedAsItWasSent_IsTriedOnceMore_WithHalfThePictures()
    {
        var (client, history, assistant) = Build();
        Pictures(history, 4);
        assistant.PictureBudget = new PictureBudget(0, 0);
        client.EnqueueText("never");
        client.EnqueueText("answered");
        client.BeforeUpdateOf = (request, _, _) => request == 0 ? throw ConnectionReset() : Task.CompletedTask;

        var events = await Run(assistant, "and now?");

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(4, PicturesIn(client.Requests[0]));
        Assert.Equal(2, PicturesIn(client.Requests[1]));
        var notice = Assert.IsType<TurnEvent.Notice>(events.First(e => e is TurnEvent.Notice));
        Assert.False(notice.IsError);
        Assert.Equal(Assistant.DroppedRequestNotice(1_600_000, 4, 2), notice.Text);
        Assert.Equal(["answered"], Deltas(events));
        Assert.Equal("answered", history.Messages[^1].Text);
        Assert.DoesNotContain(events, e => e is TurnEvent.Notice { IsError: true });
    }

    [Fact]
    public async Task ASecondDrop_EndsTheTurn_WithTheHint()
    {
        var (client, history, assistant) = Build();
        Pictures(history, 4);
        assistant.PictureBudget = new PictureBudget(0, 0);
        client.EnqueueText("never");
        client.EnqueueText("never");
        client.ThrowAt = 0;
        client.Failure = ConnectionReset();

        var events = await Run(assistant, "and now?");

        Assert.Equal(2, client.Requests.Count);
        var error = Assert.IsType<TurnEvent.Notice>(events[^1]);
        Assert.True(error.IsError);
        Assert.EndsWith(Assistant.OversizedRequestHint(800_000, 2), error.Text);
    }

    [Fact]
    public async Task AReset_WithNoPictures_IsNotRetried_NorHinted()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("never");
        client.ThrowAt = 0;
        client.Failure = ConnectionReset();

        var events = await Run(assistant, "hi");

        Assert.Single(client.Requests);
        var error = Assert.IsType<TurnEvent.Notice>(events[^1]);
        Assert.True(error.IsError);
        Assert.DoesNotContain("/compact prune", error.Text);
    }

    [Fact]
    public async Task AResetAfterTheReplyBegan_IsNotRetried()
    {
        var (client, history, assistant) = Build();
        Pictures(history, 4);
        assistant.PictureBudget = new PictureBudget(0, 0);
        client.EnqueueText("par", "tial");
        client.ThrowAt = 1;
        client.Failure = ConnectionReset();

        var events = await Run(assistant, "and now?");

        Assert.Single(client.Requests);
        Assert.True(Assert.IsType<TurnEvent.Notice>(events[^1]).IsError);
        Assert.Equal(4, PicturesIn(history.Messages));
    }

    [Fact]
    public async Task Summarize_AFailureWithPicturesInIt_AsksAgainOverTheLeanTranscript()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("never");
        client.EnqueueText("They looked at a photo.");
        client.BeforeUpdateOf = (request, _, _) => request == 0 ? throw ConnectionReset() : Task.CompletedTask;
        List<ChatMessage> transcript = [new(ChatRole.User, [new TextContent("look"), ConversationHistory.ImagePart(Shot("a.png"))]), new(ChatRole.Assistant, "a cat")];

        var (text, _) = await assistant.SummarizeAsync(transcript, null, CancellationToken.None);

        Assert.Equal("They looked at a photo.", text);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(0, PicturesIn(client.Requests[1]));
    }

    [Fact]
    public async Task Summarize_ATransportFailure_IsNotAskedAgain()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("never");
        client.ThrowAt = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => assistant.SummarizeAsync([new ChatMessage(ChatRole.User, "hi")], null, CancellationToken.None));

        Assert.Single(client.Requests);
    }

    [Fact]
    public void Explain_AnEmptySummary_IsItsMessageAlone() =>
        Assert.Equal(Assistant.EmptySummaryLengthError, Assistant.Explain(new EmptySummaryException(Assistant.EmptySummaryLengthError)));

    [Fact]
    public void IsOpeningCallId_IsTheThreeIds()
    {
        Assert.True(Assistant.IsOpeningCallId(Assistant.OpeningClockCallId));
        Assert.True(Assistant.IsOpeningCallId(Assistant.OpeningCwdCallId));
        Assert.True(Assistant.IsOpeningCallId(Assistant.OpeningMemoryCallId));
        Assert.False(Assistant.IsOpeningCallId("call_1"));
    }

    // ── The mid-turn context guard (2026-09-15) ─────────────────────────────

    private const string SdkTimeoutMessage =
        "The operation was cancelled because it exceeded the configured timeout of 0:01:15. The default timeout can be adjusted by passing a custom ClientPipelineOptions.NetworkTimeout value to the client's constructor. See https://aka.ms/net/scm/configure/networktimeout for more information.";

    private static string LongText(int length) => new('x', length);

    /// <summary>
    /// Two echo round trips over a 1,000-token window, the second's usage at 90 %: what the guard sees before the third request —
    /// 97 % since 2026-09-28, the reported 900 plus the 306-character result just appended (76 tokens at four characters each).
    /// </summary>
    private static (FakeChatClient Client, ConversationHistory History, Assistant Assistant) GuardedLoop(Assistant.TurnContextGuard? guard, bool secondReportsUsage = true)
    {
        var (client, history, assistant) = Build(new AIFunction[] { new EchoTool() });
        assistant.ContextGuard = guard;
        client.Enqueue(FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = LongText(300) }), FakeChatClient.Usage(500, 50));
        var second = FakeChatClient.Call("c2", "echo", new Dictionary<string, object?> { ["text"] = LongText(300) });
        if (secondReportsUsage)
        {
            client.Enqueue(second, FakeChatClient.Usage(850, 50));
        }
        else
        {
            client.Enqueue(second);
        }

        client.EnqueueText("done");
        return (client, history, assistant);
    }

    [Fact]
    public async Task Guard_Prune_StubsTheTurnsOlderResults_KeepsTheLastIterations_AndNotices()
    {
        var (client, history, assistant) = GuardedLoop(new Assistant.TurnContextGuard(1000, 80, ToolCompactMode.Prune));

        var events = await Run(assistant, "go");

        Assert.Equal(new[] { "done" }, Deltas(events));
        var notice = Assert.Single(events.OfType<TurnEvent.Notice>());
        Assert.False(notice.IsError);
        Assert.Equal("(✂️ context at 97%: pruned 1 tool result from this turn)", notice.Text);
        // The events: the second request's usage, its call and result, then the notice, then the reply.
        Assert.Equal(
            new[] { typeof(TurnEvent.Usage), typeof(TurnEvent.ToolCall), typeof(TurnEvent.ToolResult), typeof(TurnEvent.Usage), typeof(TurnEvent.ToolCall), typeof(TurnEvent.ToolResult), typeof(TurnEvent.Notice), typeof(TurnEvent.TextDelta) },
            events.Select(e => e.GetType()));

        Assert.Equal(3, client.Requests.Count);
        var third = client.Requests[2];
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, third.Select(m => m.Role));
        Assert.Equal("(a 306-character result, pruned by /compact)", Assert.Single(third[3].Contents.OfType<FunctionResultContent>()).Result);
        Assert.Equal("echo: " + LongText(300), Assert.Single(third[5].Contents.OfType<FunctionResultContent>()).Result);
        Assert.Equal("done", history.Messages[^1].Text);
    }

    [Fact]
    public async Task Guard_Stop_EndsTheTurnAfterTheIterationsResults_WithAnErrorNotice()
    {
        var (client, history, assistant) = GuardedLoop(new Assistant.TurnContextGuard(1000, 80, ToolCompactMode.Stop));

        var events = await Run(assistant, "go");

        Assert.Empty(Deltas(events));
        var notice = Assert.IsType<TurnEvent.Notice>(events[^1]);
        Assert.True(notice.IsError);
        Assert.Equal("🛑 Stopped at 97% of the context window (LLM tool compact type is stop); /compact or /clear before continuing.", notice.Text);
        Assert.Equal(2, client.Requests.Count);
        // No call left unanswered: the history ends with the second iteration's result, verbatim.
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, history.Messages.Select(m => m.Role));
        Assert.Equal("echo: " + LongText(300), Assert.Single(history.Messages[^1].Contents.OfType<FunctionResultContent>()).Result);
        Assert.Equal("echo: " + LongText(300), Assert.Single(history.Messages[2].Contents.OfType<FunctionResultContent>()).Result);
    }

    [Theory]
    [InlineData(ToolCompactMode.Nothing, 80)]
    [InlineData(ToolCompactMode.Prune, 0)]
    [InlineData(ToolCompactMode.Prune, 98)]
    [InlineData(ToolCompactMode.Compact, 98)]
    public async Task Guard_NothingOrOffOrUnderTheShare_LeavesTheTurnAlone(ToolCompactMode mode, int percent)
    {
        var (client, _, assistant) = GuardedLoop(new Assistant.TurnContextGuard(1000, percent, mode));

        var events = await Run(assistant, "go");

        Assert.Empty(events.OfType<TurnEvent.Notice>());
        Assert.Equal(3, client.Requests.Count);
        Assert.Equal("echo: " + LongText(300), Assert.Single(client.Requests[2][3].Contents.OfType<FunctionResultContent>()).Result);
    }

    [Fact]
    public async Task Guard_NoGuard_OrNoUsageReported_LeavesTheTurnAlone()
    {
        var (client, _, assistant) = GuardedLoop(null);
        Assert.Empty((await Run(assistant, "go")).OfType<TurnEvent.Notice>());
        Assert.Equal(3, client.Requests.Count);

        (client, _, assistant) = GuardedLoop(new Assistant.TurnContextGuard(1000, 80, ToolCompactMode.Prune), secondReportsUsage: false);
        Assert.Empty((await Run(assistant, "go")).OfType<TurnEvent.Notice>());
        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task Guard_Prune_NothingOlderInTheTurn_CarriesOnQuietly()
    {
        // One iteration only at the share: its result is the last iteration's, so nothing is stubbed and no notice is written.
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() });
        assistant.ContextGuard = new Assistant.TurnContextGuard(1000, 80, ToolCompactMode.Prune);
        client.Enqueue(FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = LongText(300) }), FakeChatClient.Usage(850, 50));
        client.EnqueueText("done");

        var events = await Run(assistant, "go");

        Assert.Empty(events.OfType<TurnEvent.Notice>());
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal("echo: " + LongText(300), Assert.Single(client.Requests[1][3].Contents.OfType<FunctionResultContent>()).Result);
    }

    [Fact]
    public void Guard_Arithmetic_IsPinned()
    {
        var guard = new Assistant.TurnContextGuard(151_427, 80, ToolCompactMode.Prune);
        var chef = new TokenUsage(128_957, 75, 129_032, 1, TimeSpan.Zero, TimeSpan.Zero);
        Assert.True(guard.Acts);
        Assert.Equal(85, guard.PercentOf(chef));
        Assert.True(guard.Tripped(chef));
        Assert.False(guard.Tripped(new TokenUsage(100_000, 100, 100_100, 1, TimeSpan.Zero, TimeSpan.Zero)));
        Assert.False(guard.Tripped(TokenUsage.Zero));
        Assert.False(new Assistant.TurnContextGuard(151_427, 0, ToolCompactMode.Prune).Tripped(chef));
        Assert.False(new Assistant.TurnContextGuard(151_427, 80, ToolCompactMode.Nothing).Tripped(chef));
        Assert.Equal("(✂️ context at 85%: pruned 23 tool results from this turn)", Assistant.TurnPrunedNotice(85, 23));
        Assert.Equal("(✂️ context at 85%: pruned 1 tool result from this turn)", Assistant.TurnPrunedNotice(85, 1));
        Assert.Equal("🛑 Stopped at 85% of the context window (LLM tool compact type is stop); /compact or /clear before continuing.", Assistant.TurnStoppedNotice(85));
    }

    // ── The mid-turn compact (LLM tool compact type: compact, 2026-09-28) ───

    private static Dictionary<string, object?> EchoArgs(string text) => new() { ["text"] = text };

    [Fact]
    public async Task Guard_Projects_TheResultsJustAppended_TripsBeforeTheNextRequest()
    {
        // Reported 70 % of the window, but the 2,006-character result just appended (501 tokens) takes the next request past it.
        var (client, _, assistant) = Build(new AIFunction[] { new EchoTool() });
        assistant.ContextGuard = new Assistant.TurnContextGuard(1000, 80, ToolCompactMode.Stop);
        client.Enqueue(FakeChatClient.Call("c1", "echo", EchoArgs(LongText(2000))), FakeChatClient.Usage(690, 10));
        client.EnqueueText("never asked");

        var events = await Run(assistant, "go");

        Assert.Equal("🛑 Stopped at 120% of the context window (LLM tool compact type is stop); /compact or /clear before continuing.", Assert.IsType<TurnEvent.Notice>(events[^1]).Text);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Guard_Compact_PruneNotEnough_SummarisesTheOlderTurns_ThisTurnKept()
    {
        var (client, history, assistant) = Build(new AIFunction[] { new EchoTool() });
        client.EnqueueText("noted");
        await Run(assistant, LongText(2000));                                           // an older turn of about 500 tokens

        assistant.ContextGuard = new Assistant.TurnContextGuard(1000, 50, ToolCompactMode.Compact);
        client.Enqueue(FakeChatClient.Call("c1", "echo", EchoArgs(LongText(300))), FakeChatClient.Usage(600, 50));   // 650 + the 306-character result's 76: 72 %
        client.Enqueue(FakeChatClient.Text("The user sent a long line of x."), FakeChatClient.Usage(560, 8));          // the summariser
        client.EnqueueText("done");

        var events = await Run(assistant, "go");

        // Nothing for the prune (one iteration, the last), so the summary: the older turn went, about 500 tokens with it.
        Assert.Empty(events.OfType<TurnEvent.Notice>());
        Assert.Equal(72, Assert.Single(events.OfType<TurnEvent.Compacting>()).Percent);
        var compacted = Assert.Single(events.OfType<TurnEvent.Compacted>());
        Assert.False(compacted.ThisTurn);
        Assert.Equal(72, compacted.Percent);
        Assert.True(compacted.Result.Summarised);
        Assert.Equal("The user sent a long line of x.", compacted.Result.Summary);
        Assert.Equal((560L, 8L), (compacted.Result.Usage!.Value.Input, compacted.Result.Usage.Value.Output));
        Assert.Equal((5, 4), (compacted.Result.MessagesBefore, compacted.Result.MessagesAfter));
        Assert.Equal(new[] { typeof(TurnEvent.Usage), typeof(TurnEvent.ToolCall), typeof(TurnEvent.ToolResult), typeof(TurnEvent.Compacting), typeof(TurnEvent.Compacted), typeof(TurnEvent.TextDelta) }, events.Select(e => e.GetType()));

        Assert.Equal(4, client.Requests.Count);
        var summariser = client.Requests[2];
        Assert.Equal(ConversationCompactor.SummaryInstruction, summariser[0].Text);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User }, summariser.Select(m => m.Role));   // the older turn alone
        var next = client.Requests[3];
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.Assistant, ChatRole.Tool }, next.Select(m => m.Role));
        Assert.Equal(ConversationCompactor.SummaryPreamble + "The user sent a long line of x.", next[1].Text);
        Assert.Equal("go", next[2].Text);
        Assert.Equal("echo: " + LongText(300), Assert.Single(next[4].Contents.OfType<FunctionResultContent>()).Result);   // this turn untouched
        Assert.Equal("done", history.Messages[^1].Text);
    }

    /// <summary>
    /// One turn whose first call carries 1,200 characters of arguments (the model's own words: no prune touches them) and
    /// whose second request reports 91 % of a 1,000-token window: the prune stubs the first result and it is not enough.
    /// </summary>
    private static (FakeChatClient Client, ConversationHistory History, Assistant Assistant) HeavyArguments()
    {
        var (client, history, assistant) = Build(new AIFunction[] { new EchoTool() });
        assistant.ContextGuard = new Assistant.TurnContextGuard(1000, 50, ToolCompactMode.Compact);
        client.Enqueue(FakeChatClient.Call("c1", "echo", EchoArgs(LongText(1200))), FakeChatClient.Usage(100, 10));   // 110 + 301: 41 %
        client.Enqueue(FakeChatClient.Call("c2", "echo", EchoArgs("b")), FakeChatClient.Usage(900, 10));               // 911 before the prune
        return (client, history, assistant);
    }

    [Fact]
    public async Task Guard_Compact_NoOlderTurns_SummarisesThisTurnsEarlierIterations_IntoTheUserMessage()
    {
        var (client, history, assistant) = HeavyArguments();
        client.Enqueue(FakeChatClient.Text("Echoed 1,200 x once."), FakeChatClient.Usage(700, 6));
        client.EnqueueText("done");

        var events = await Run(assistant, "go");

        Assert.Equal("(✂️ context at 91%: pruned 1 tool result from this turn)", Assert.Single(events.OfType<TurnEvent.Notice>()).Text);
        var compacting = Assert.Single(events.OfType<TurnEvent.Compacting>());
        Assert.InRange(compacting.Percent, 50, 90);                                     // the estimate after the prune, still past the share
        var compacted = Assert.Single(events.OfType<TurnEvent.Compacted>());
        Assert.True(compacted.ThisTurn);
        Assert.Equal(compacting.Percent, compacted.Percent);
        Assert.Equal((5, 3), (compacted.Result.MessagesBefore, compacted.Result.MessagesAfter));
        Assert.Equal((1, 2), (compacted.Result.OpeningKept, compacted.Result.RecentKept));

        Assert.Equal(4, client.Requests.Count);
        var summariser = client.Requests[2];
        Assert.Equal(ConversationCompactor.TurnProgressInstruction, summariser[0].Text);
        Assert.Equal(ConversationCompactor.TurnProgressRequest, summariser[^1].Text);
        // Everything ahead of the last iteration: the user's message and the first call with its stubbed result.
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User }, summariser.Select(m => m.Role));

        var next = client.Requests[3];
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool }, next.Select(m => m.Role));
        Assert.Equal(new[] { "go", ConversationCompactor.TurnProgressPreamble + "Echoed 1,200 x once." }, next[1].Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Equal("c2", Assert.Single(next[2].Contents.OfType<FunctionCallContent>()).CallId);
        Assert.Equal("echo: b", Assert.Single(next[3].Contents.OfType<FunctionResultContent>()).Result);
        Assert.Equal("done", history.Messages[^1].Text);
        Assert.Equal(1, history.TurnCount);
    }

    [Fact]
    public async Task Guard_Compact_SummarisesOncePerClimb_ARequestUnderTheShareArmsItAgain_TheNoteReplaced()
    {
        var (client, _, assistant) = HeavyArguments();
        client.Enqueue(FakeChatClient.Text("First note."), FakeChatClient.Usage(700, 6));
        client.Enqueue(FakeChatClient.Call("c3", "echo", EchoArgs("c")), FakeChatClient.Usage(900, 10));                 // still 91 %: no second summary
        client.Enqueue(FakeChatClient.Call("c4", "echo", EchoArgs(LongText(1200))), FakeChatClient.Usage(100, 10));      // 11 % reported (41 % with its result): armed again
        client.Enqueue(FakeChatClient.Call("c5", "echo", EchoArgs("e")), FakeChatClient.Usage(900, 10));                 // past it again: the second summary
        client.Enqueue(FakeChatClient.Text("Second note."), FakeChatClient.Usage(700, 6));
        client.EnqueueText("done");

        var events = await Run(assistant, "go");

        Assert.Equal(2, events.OfType<TurnEvent.Compacted>().Count());
        Assert.All(events.OfType<TurnEvent.Compacted>(), c => Assert.True(c.ThisTurn));
        Assert.Equal(8, client.Requests.Count);
        Assert.Equal(ConversationCompactor.TurnProgressInstruction, client.Requests[2][0].Text);
        Assert.Equal(ConversationCompactor.TurnProgressInstruction, client.Requests[6][0].Text);
        // One note on the user's message, the second: a compact of the same turn replaces the note it made before.
        var user = client.Requests[7][1];
        Assert.Equal(new[] { "go", ConversationCompactor.TurnProgressPreamble + "Second note." }, user.Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool }, client.Requests[7].Select(m => m.Role));
    }

    [Fact]
    public async Task Guard_Compact_ASummariserFailure_IsAnErrorNotice_AndTheTurnCarriesOn()
    {
        var (client, history, assistant) = HeavyArguments();
        client.Enqueue(FakeChatClient.Text("unused"));
        client.EnqueueText("done");
        client.BeforeUpdateOf = (request, _, _) => request == 2 ? Task.FromException(new HttpRequestException("summariser down")) : Task.CompletedTask;

        var events = await Run(assistant, "go");

        var failure = events.OfType<TurnEvent.Notice>().Single(n => n.IsError);
        Assert.StartsWith(Assistant.CompactFailedPrefix, failure.Text);
        Assert.Contains("summariser down", failure.Text);
        Assert.Empty(events.OfType<TurnEvent.Compacted>());
        Assert.Equal(new[] { "done" }, Deltas(events));
        // The history as the prune left it: the first call still there, no note on the user's message.
        Assert.Equal(4, client.Requests.Count);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, client.Requests[3].Select(m => m.Role));
        Assert.Equal("go", client.Requests[3][1].Text);
        Assert.Equal("done", history.Messages[^1].Text);
    }

    [Fact]
    public async Task Guard_Compact_CancelledDuringTheSummary_Propagates_TheHistoryUntouched()
    {
        var (client, history, assistant) = HeavyArguments();
        client.Enqueue(FakeChatClient.Text("never"));
        using var cts = new CancellationTokenSource();
        client.BeforeUpdateOf = (request, _, _) =>
        {
            if (request == 2)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(assistant, "go", cts.Token));

        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool }, history.Messages.Select(m => m.Role));
        Assert.Equal("go", history.Messages[0].Text);
        Assert.Equal("c1", Assert.Single(history.Messages[1].Contents.OfType<FunctionCallContent>()).CallId);
    }

    [Fact]
    public void Guard_Arithmetic_OverTokens_IsTheUsagesOwn()
    {
        var guard = new Assistant.TurnContextGuard(1000, 80, ToolCompactMode.Compact);
        Assert.True(guard.Acts);
        Assert.Equal(97, guard.PercentOf(976L));
        Assert.True(guard.Tripped(800L));
        Assert.False(guard.Tripped(799L));
        Assert.False(guard.Tripped(0L));
        Assert.False(guard.Tripped(-5L));
        Assert.Equal(guard.PercentOf(new TokenUsage(900, 50, 950, 1, TimeSpan.Zero, TimeSpan.Zero)), guard.PercentOf(950L));
        Assert.Equal("🗜️ Compact failed: ", Assistant.CompactFailedPrefix);
        Assert.Equal(Assistant.CompactGlyph, App.CompactionText.CompactGlyph);
    }

    [Fact]
    public void GuardGlyphs_AreTwoCellsEach_ThenASpace()
    {
        // ✂️ = U+2702 + U+FE0F (2026-09-19): the bare scissors are one cell; the selector makes the two-cell emoji and counts one.
        Assert.Equal("✂️ ", Assistant.PruneGlyph);
        Assert.Equal(2, TextCells.Width(Assistant.PruneGlyph.TrimEnd()));
        Assert.Equal(3, TextCells.Width(Assistant.PruneGlyph));
        // 🛑 = U+1F6D1, emoji-presentation by itself: a pair, no selector.
        Assert.Equal("\U0001F6D1 ", Assistant.StopGlyph);
        Assert.Equal(3, TextCells.Width(Assistant.StopGlyph));
    }

    // ── The request timeout in the operator's words ─────────────────────────

    [Fact]
    public void RequestTimeout_IsRecognised_AndExplained()
    {
        Assert.True(Assistant.LooksLikeRequestTimeout(new TaskCanceledException(SdkTimeoutMessage)));
        Assert.True(Assistant.LooksLikeRequestTimeout(new InvalidOperationException("wrapped", new TaskCanceledException(SdkTimeoutMessage, new TaskCanceledException("The operation was canceled.")))));
        Assert.True(Assistant.LooksLikeRequestTimeout(new AggregateException(new TaskCanceledException(SdkTimeoutMessage))));
        Assert.False(Assistant.LooksLikeRequestTimeout(new TaskCanceledException("The operation was canceled.")));
        Assert.False(Assistant.LooksLikeRequestTimeout(new HttpRequestException(SdkTimeoutMessage)));   // the marker on the wrong type is not the SDK's timeout
        Assert.Equal("exceeded the configured timeout", Assistant.NetworkTimeoutMarker);

        Assert.Equal("The server sent nothing for 75s (LLM request timeout (s), LLM tab)", Assistant.RequestTimeoutExplanation(TimeSpan.FromSeconds(75), null));
        Assert.Equal(
            "The server sent nothing for 75s (LLM request timeout (s), LLM tab) — the context was at 85% of the window; a model near its limit can run away (/compact, /clear)",
            Assistant.RequestTimeoutExplanation(TimeSpan.FromSeconds(75), 85));
    }

    [Fact]
    public async Task RequestTimeout_MidLoop_IsTheModelErrorWithTheContextShare()
    {
        var (client, history, assistant) = Build(new AIFunction[] { new EchoTool() });
        assistant.ContextGuard = new Assistant.TurnContextGuard(1000, 80, ToolCompactMode.Nothing);   // the window known, the guard idle
        client.Enqueue(FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = "hi" }), FakeChatClient.Usage(600, 50));
        client.Enqueue();                                           // the second request: the SDK's timeout before any chunk
        client.Failure = new TaskCanceledException(SdkTimeoutMessage, new TaskCanceledException("The operation was canceled."));
        client.BeforeUpdate = (_, _) => { client.ThrowAt = 0; return Task.CompletedTask; };

        var events = await Run(assistant, "go");

        var notice = Assert.IsType<TurnEvent.Notice>(events[^1]);
        Assert.True(notice.IsError);
        Assert.Equal("Model error: The server sent nothing for 5s (LLM request timeout (s), LLM tab) — the context was at 65% of the window; a model near its limit can run away (/compact, /clear)", notice.Text);
        // The round trip that succeeded stays: the work is there for the next message.
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool }, history.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task RequestTimeout_OnTheFirstRequest_NamesNoShare_AndOtherFailuresReadAsBefore()
    {
        var (client, _, assistant) = Build();
        assistant.ContextGuard = new Assistant.TurnContextGuard(1000, 80, ToolCompactMode.Prune);
        client.EnqueueText("never");
        client.ThrowAt = 0;
        client.Failure = new TaskCanceledException(SdkTimeoutMessage);
        Assert.Equal("Model error: The server sent nothing for 5s (LLM request timeout (s), LLM tab)", Assert.IsType<TurnEvent.Notice>((await Run(assistant, "a"))[^1]).Text);

        client.EnqueueText("never");
        client.Failure = new HttpRequestException("refused");
        Assert.Equal("Model error: HttpRequestException: refused", Assert.IsType<TurnEvent.Notice>((await Run(assistant, "b"))[^1]).Text);

        Assert.Equal("HttpRequestException: refused", assistant.ExplainFailure(new HttpRequestException("refused")));
        Assert.Equal("The server sent nothing for 5s (LLM request timeout (s), LLM tab)", assistant.ExplainFailure(new TaskCanceledException(SdkTimeoutMessage)));
        Assert.Equal(
            "The server sent nothing for 5s (LLM request timeout (s), LLM tab) — the context was at 65% of the window; a model near its limit can run away (/compact, /clear)",
            assistant.ExplainFailure(new TaskCanceledException(SdkTimeoutMessage), new TokenUsage(600, 50, 650, 1, TimeSpan.Zero, TimeSpan.Zero)));
    }

    // ── Thinking kept for the wire (2026-09-28) ─────────────────────────────

    [Fact]
    public async Task AThinkBlockStreamedAsContent_StaysInTheHistoryAsThinking()
    {
        var (client, history, assistant) = Build();
        client.Enqueue(FakeChatClient.Text("<think>hmm</think>"), FakeChatClient.Text("The answer."));

        await foreach (var _ in assistant.RunTurnAsync("q")) { }

        var reply = history.Messages[^1];
        Assert.Equal("The answer.", reply.Text);
        Assert.Equal("hmm", Assert.Single(reply.Contents.OfType<TextReasoningContent>()).Text);
    }

    [Fact]
    public async Task PreserveThinking_RidesTheRequestsOptions()
    {
        var (client, _, assistant) = Build();
        client.Enqueue(FakeChatClient.Text("one"));
        await foreach (var _ in assistant.RunTurnAsync("q")) { }
        Assert.Null(client.Options[^1]!.AdditionalProperties);

        assistant.PreserveThinking = true;
        client.Enqueue(FakeChatClient.Text("two"));
        await foreach (var _ in assistant.RunTurnAsync("q2")) { }
        Assert.True(OpenAICompatibleChatClient.PreservesThinking(client.Options[^1]));
    }

    // ── RequestAsync (the side loop's primitive, 2026-09-17) ────────────────

    [Fact]
    public async Task RequestAsync_StreamsOneRequest_WithTheToolsAndTheEffort_TheHistoryUntouched()
    {
        var time = new ManualTimeProvider();
        var (client, history, assistant) = Build(time: time);
        history.AddUser("earlier");
        var echo = new EchoTool();
        client.Enqueue(
            FakeChatClient.Text("<think>hm</think>Let me "),
            FakeChatClient.Text("check."),
            FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = "x" }),
            FakeChatClient.Usage(50, 10));
        client.BeforeUpdate = (i, _) => { time.Advance(TimeSpan.FromSeconds(i == 0 ? 0.5 : 1)); return Task.CompletedTask; };
        var request = new List<ChatMessage> { new(ChatRole.System, "side"), new(ChatRole.User, "go") };

        var response = await assistant.RequestAsync(request, [echo], ReasoningEffort.High, CancellationToken.None);

        Assert.Equal("Let me check.", response.Text);
        var call = Assert.Single(response.Calls);
        Assert.Equal("c1", call.CallId);
        Assert.NotNull(response.Usage);
        Assert.Equal(60, response.Usage!.Value.Total);
        Assert.Equal(TimeSpan.FromSeconds(0.5), response.Usage.Value.ToFirstToken);
        Assert.Equal(TimeSpan.FromSeconds(3), response.Usage.Value.Generating);
        Assert.Contains(response.Messages, m => m.Contents.OfType<FunctionCallContent>().Any());
        Assert.DoesNotContain("<think>", string.Concat(response.Messages.Select(m => m.Text)), StringComparison.Ordinal);   // the leaked block is stripped from what goes back

        var sent = Assert.Single(client.Requests);
        Assert.Equal(["side", "go"], sent.Select(m => m.Text));
        var options = Assert.Single(client.Options)!;
        Assert.Equal(["echo"], options.Tools!.Select(t => t.Name));
        Assert.Equal(ReasoningEffort.High, options.Reasoning!.Effort);
        Assert.Equal(["earlier"], history.Messages.Select(m => m.Text));   // untouched
        Assert.Equal(2, request.Count);                                     // the caller's list untouched too
    }

    [Fact]
    public async Task RequestAsync_NoCalls_NoUsage_AndAFailurePropagates()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("nothing");

        var response = await assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [], ReasoningEffort.None, CancellationToken.None);
        Assert.Equal("nothing", response.Text);
        Assert.Empty(response.Calls);
        Assert.Null(response.Usage);
        Assert.Null(Assert.Single(client.Options)!.Tools);   // an empty tool list is sent as null

        client.Enqueue(FakeChatClient.Text("x"));
        client.ThrowAt = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [], ReasoningEffort.None, CancellationToken.None));
    }

    /// <summary>
    /// RequestAsync's textToolCalls (2026-09-30, code review: the picture writer's own copy of the turn's handling): a call
    /// written out as text is a real call, the markup out of the text and the messages, its id nine alphanumerics; the line form
    /// uses the tool's parameter names; left null, the text is as written.
    /// </summary>
    [Fact]
    public async Task RequestAsync_TextToolCalls_AWrittenCallIsACall_TheMarkupGone()
    {
        var (client, _, assistant) = Build();
        var echo = new EchoTool();
        client.EnqueueText("Here.\n<tool_call>\n<function=echo>\n<parameter=text>\nb\n</parameter>\n</function>\n</tool_call>");
        var request = new List<ChatMessage> { new(ChatRole.User, "go"), new(ChatRole.Assistant, "before"), new(ChatRole.User, "again") };

        var response = await assistant.RequestAsync(request, [echo], ReasoningEffort.None, CancellationToken.None, textToolCalls: Assistant.WrittenCallNames([echo]));

        Assert.Equal("Here.", response.Text);
        var call = Assert.Single(response.Calls);
        Assert.Equal(Assistant.WrittenCallId(2, 1), call.CallId);
        Assert.Equal("b", call.Arguments!["text"]?.ToString());
        Assert.DoesNotContain("<tool_call>", string.Concat(response.Messages.Select(m => m.Text)), StringComparison.Ordinal);
        Assert.Equal("echo", Assert.Single(response.WrittenCalls).Name);
        Assert.Equal("", response.AfterWritten);

        client.EnqueueText("echo text: c\nDone.");
        var line = await assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [echo], ReasoningEffort.None, CancellationToken.None, textToolCalls: Assistant.WrittenCallNames([echo]));
        Assert.Equal("Done.", line.Text);
        Assert.Equal(Assistant.WrittenCallId(1, 1), Assert.Single(line.Calls).CallId);
        Assert.Equal("Done.", line.AfterWritten);

        client.EnqueueText("echo(text=\"d\")");
        var off = await assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [echo], ReasoningEffort.None, CancellationToken.None);
        Assert.Equal("echo(text=\"d\")", off.Text);
        Assert.Empty(off.Calls);
        Assert.Null(off.AfterWritten);
    }

    /// <summary>
    /// A written call's id (2026-09-30, code review: text-call-1-1 broke the Mistral-family templates' rule): nine alphanumerics,
    /// never an opening or pending call's, distinct across a round trip's calls and the round trips.
    /// </summary>
    [Fact]
    public void WrittenCallId_IsNineAlphanumerics_AndDistinct()
    {
        var ids = Enumerable.Range(1, 12).SelectMany(round => Enumerable.Range(1, 35).Select(n => Assistant.WrittenCallId(round, n))).ToList();
        Assert.All(ids, id =>
        {
            Assert.Equal(9, id.Length);
            Assert.True(id.All(char.IsAsciiLetterOrDigit), id);
            Assert.StartsWith(Assistant.WrittenCallIdPrefix, id, StringComparison.Ordinal);
            Assert.False(Assistant.IsOpeningCallId(id));
        });
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(9, Assistant.WrittenCallId(10000, 35).Length);
    }

    /// <summary>
    /// Calls written beside a native one (2026-09-30, code review: a server that parses the calls but leaves the markup in the
    /// content, and the copies' values spelled apart — 7.0 against 7 — so a comparison ran both): only taken out, never run.
    /// </summary>
    [Fact]
    public async Task RequestAsync_TextToolCalls_BesideANativeCall_TheWrittenOnesAreOnlyTakenOut()
    {
        var (client, _, assistant) = Build();
        var echo = new EchoTool();
        client.Enqueue(
            FakeChatClient.Text("<tool_call>\n<function=echo>\n<parameter=text>\n7.0\n</parameter>\n</function>\n</tool_call>"),
            FakeChatClient.Call("c1", "echo", new Dictionary<string, object?> { ["text"] = "7.0" }));

        var response = await assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [echo], ReasoningEffort.None, CancellationToken.None, textToolCalls: Assistant.WrittenCallNames([echo]));

        Assert.Equal("c1", Assert.Single(response.Calls).CallId);
        Assert.Equal("c1", Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()).CallId);
        Assert.Equal("", response.Text);
        Assert.Single(response.WrittenCalls);
    }

    /// <summary>Two calls written alike with no native one (2026-09-30, code review: the de-dupe ran the second against the first): both run.</summary>
    [Fact]
    public async Task RequestAsync_TextToolCalls_TwoCallsWrittenAlike_BothAreCalls()
    {
        var (client, _, assistant) = Build();
        var echo = new EchoTool();
        client.EnqueueText("echo(text=\"a castle\")\necho(text=\"a castle\")");

        var response = await assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [echo], ReasoningEffort.None, CancellationToken.None, textToolCalls: Assistant.WrittenCallNames([echo]));

        Assert.Equal([Assistant.WrittenCallId(1, 1), Assistant.WrittenCallId(1, 2)], response.Calls.Select(c => c.CallId));
    }

    /// <summary>
    /// A written call whose arguments do not parse (2026-09-30, code review: left out, the words beside it were the reply): a call
    /// all the same, carrying the parse error, so the model is answered it and asked again.
    /// </summary>
    [Fact]
    public async Task RequestAsync_TextToolCalls_UnparsedArguments_AreACallAnsweredTheError()
    {
        var (client, _, assistant) = Build();
        var echo = new EchoTool();
        client.EnqueueText("I'll echo it first.\necho(\"a\")");

        var response = await assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [echo], ReasoningEffort.None, CancellationToken.None, textToolCalls: Assistant.WrittenCallNames([echo]));

        var call = Assert.Single(response.Calls);
        Assert.NotNull(call.Exception);
        var (text, _) = await Assistant.InvokeToolAsync([echo], call, CancellationToken.None);
        Assert.Contains(Assistant.WrittenArgumentsUnparsed, text, StringComparison.Ordinal);
        Assert.StartsWith("Error:", text, StringComparison.Ordinal);
        Assert.Empty(echo.Received);
    }

    /// <summary>
    /// In a request that offers no tool, the answer round, a written call is taken out of the text and not made a call; the words
    /// after it are AfterWritten, and a lead-in before it is not (2026-09-30, code review).
    /// </summary>
    [Fact]
    public async Task RequestAsync_TextToolCalls_NoToolOffered_TheCallIsOnlyTakenOut()
    {
        var (client, _, assistant) = Build();
        var echo = new EchoTool();
        client.EnqueueText("<tool_call>\n<function=echo>\n<parameter=text>\nb\n</parameter>\n</function>\n</tool_call>\nscore_9, a dog");

        var response = await assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [], ReasoningEffort.None, CancellationToken.None, textToolCalls: Assistant.WrittenCallNames([echo]));

        Assert.Equal("score_9, a dog", response.Text);
        Assert.Equal("score_9, a dog", response.AfterWritten);
        Assert.Empty(response.Calls);
        Assert.Empty(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Single(response.WrittenCalls);

        client.EnqueueText("Let me check the tag list first.\necho text: tags");
        var lead = await assistant.RequestAsync([new ChatMessage(ChatRole.User, "go")], [], ReasoningEffort.None, CancellationToken.None, textToolCalls: Assistant.WrittenCallNames([echo]));
        Assert.Equal("Let me check the tag list first.", lead.Text);
        Assert.Equal("", lead.AfterWritten);
    }

    [Fact]
    public async Task InvokeToolAsync_Static_RunsAgainstTheListGiven()
    {
        var echo = new EchoTool();
        var (text, images) = await Assistant.InvokeToolAsync([echo], new FunctionCallContent("c1", "echo", new Dictionary<string, object?> { ["text"] = "hi" }), CancellationToken.None);
        Assert.Equal("echo: hi", text);
        Assert.Empty(images);
        Assert.Equal(["hi"], echo.Received);

        var (unknown, _) = await Assistant.InvokeToolAsync([echo], new FunctionCallContent("c2", "nope", null), CancellationToken.None);
        Assert.Equal("Error: unknown tool 'nope'.", unknown);
    }
    // ---- the --log lines of a turn (2026-09-19) ----

    private static (List<NeonSidekick.Diagnostics.DiagnosticEvent> Lines, Action<NeonSidekick.Diagnostics.DiagnosticEvent> Capture) LogCapture(params string[] categories)
    {
        var lines = new List<NeonSidekick.Diagnostics.DiagnosticEvent>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (categories.Contains(e.Category)) lines.Add(e); };
        return (lines, capture);
    }

    [Fact]
    public async Task Turn_LogsItsStartAndEnd_WithTheTally_AndEachRequest()
    {
        var echo = new EchoTool();
        var (client, _, assistant) = Build(new AIFunction[] { echo }, reasoning: ReasoningEffort.Medium);
        client.Enqueue(FakeChatClient.Call("call-1", "echo", new Dictionary<string, object?> { ["text"] = "hi" }));
        client.EnqueueText("done", " now");
        var (lines, capture) = LogCapture(Assistant.TurnCategory, "Llm");
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            await Run(assistant, "echo hi\nsecond line");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        var turn = lines.Where(e => e.Category == Assistant.TurnCategory).ToList();
        Assert.Equal(2, turn.Count);
        Assert.Equal((NeonSidekick.Diagnostics.DiagnosticLevel.Info, "Turn 1 started: \"echo hi\""), (turn[0].Level, turn[0].Message));
        Assert.Equal(NeonSidekick.Diagnostics.DiagnosticLevel.Info, turn[1].Level);
        Assert.Matches(@"^Turn 1 ended after \d+\.\d s: 2 requests, 1 tool call, 8 chars, completed$", turn[1].Message);

        var requests = lines.Where(e => e.Message.StartsWith("Request ", StringComparison.Ordinal)).Select(e => e.Message).ToList();
        Assert.Equal(["Request 1: 2 messages, 1 tool, reasoning medium", "Request 2: 4 messages, 1 tool, reasoning medium"], requests);
        Assert.All(lines.Where(e => e.Message.StartsWith("Request ", StringComparison.Ordinal)), e => Assert.Equal(NeonSidekick.Diagnostics.DiagnosticLevel.Debug, e.Level));

        var result = Assert.Single(lines, e => e.Message.StartsWith("Tool result ", StringComparison.Ordinal));
        Assert.Equal(NeonSidekick.Diagnostics.DiagnosticLevel.Debug, result.Level);
        Assert.Matches(@"^Tool result echo: 8 chars in \d+ ms — ""echo: hi""$", result.Message);
    }

    [Fact]
    public async Task Turn_Cancelled_LogsTheRequestCut_AndEndsCancelled()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("One", ". Two.");
        using var cts = new CancellationTokenSource();
        client.BeforeUpdate = (i, _) =>
        {
            if (i == 1)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        };
        var (lines, capture) = LogCapture(Assistant.TurnCategory, "Llm");
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(assistant, "hi", cts.Token));
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains(lines, e => e.Level == NeonSidekick.Diagnostics.DiagnosticLevel.Debug && System.Text.RegularExpressions.Regex.IsMatch(e.Message, @"^Request 1 cancelled after \d+\.\d s: 3 chars received$"));
        var ended = Assert.Single(lines, e => e.Message.StartsWith("Turn 1 ended", StringComparison.Ordinal));
        Assert.Matches(@"^Turn 1 ended after \d+\.\d s: 1 request, 0 tool calls, 3 chars, cancelled$", ended.Message);
    }

    [Fact]
    public async Task Turn_ThatFails_EndsFailed_AndTheFailureLineIsInfo()
    {
        var (client, _, assistant) = Build();
        client.EnqueueText("never");
        client.ThrowAt = 0;
        client.Failure = new HttpRequestException("boom");
        var (lines, capture) = LogCapture(Assistant.TurnCategory, "Llm");
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            await Run(assistant, "hi");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        var failed = Assert.Single(lines, e => e.Message.StartsWith("Model call failed:", StringComparison.Ordinal));
        Assert.Equal(NeonSidekick.Diagnostics.DiagnosticLevel.Info, failed.Level);
        Assert.Matches(@"^Turn 1 ended after \d+\.\d s: 1 request, 0 tool calls, 0 chars, failed$", Assert.Single(lines, e => e.Message.StartsWith("Turn 1 ended", StringComparison.Ordinal)).Message);
    }

    [Fact]
    public async Task ToolError_IsLoggedAtInfo_TheOpeningCallsCarryTheirSuffix()
    {
        var (client, _, assistant) = Build(new AIFunction[] { new ThrowingTool() });
        client.Enqueue(FakeChatClient.Call("c3", "boom"));
        client.EnqueueText("ok");
        var (lines, capture) = LogCapture("Llm");
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            await Run(assistant, "x");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        var error = Assert.Single(lines, e => e.Message.StartsWith("Tool boom answered", StringComparison.Ordinal));
        Assert.Equal((NeonSidekick.Diagnostics.DiagnosticLevel.Info, "Tool boom answered an error: Error: boom failed: kaboom"), (error.Level, error.Message));
        Assert.DoesNotContain(lines, e => e.Message.StartsWith("Tool result boom", StringComparison.Ordinal));
        Assert.Equal(" (opening)", Assistant.OpeningSuffix);
        Assert.True(Assistant.IsToolError("Error: x"));
        Assert.False(Assistant.IsToolError("error: x"));
    }

    [Fact]
    public void TurnLogLines_ArePinned()
    {
        Assert.Equal("Turn", Assistant.TurnCategory);
        Assert.Equal("Turn 3 started: \"why is the build red\" (2 images, 3 opening calls)", Assistant.TurnStartedLogLine(3, "why is the build red\nmore", 2, 3));
        Assert.Equal("Turn 1 started: \"hi\" (1 image)", Assistant.TurnStartedLogLine(1, "hi", 1, 0));
        Assert.Equal("Turn 1 started: \"hi\"", Assistant.TurnStartedLogLine(1, "hi", 0, 0));
        Assert.Equal("Turn 3 ended after 12.4 s: 2 requests, 3 tool calls, 512 chars, completed", Assistant.TurnEndedLogLine(3, TimeSpan.FromSeconds(12.41), 2, 3, 512, Assistant.TurnCompleted));
        Assert.Equal("Turn 1 ended after 0.0 s: 1 request, 1 tool call, 0 chars, stopped", Assistant.TurnEndedLogLine(1, TimeSpan.Zero, 1, 1, 0, Assistant.TurnStopped));
        Assert.Equal(["completed", "stopped", "failed", "cancelled"], new[] { Assistant.TurnCompleted, Assistant.TurnStopped, Assistant.TurnFailed, Assistant.TurnCancelled });
        Assert.Equal("Request 2: 14 messages, 19 tools, reasoning medium", Assistant.RequestLogLine(2, 14, 19, ReasoningEffort.Medium));
        Assert.Equal("Request 1: 1 message, no tools, reasoning default", Assistant.RequestLogLine(1, 1, 0, null));
        Assert.Equal("Request 2 cancelled after 3.2 s: 140 chars received", Assistant.RequestCancelledLogLine(2, TimeSpan.FromSeconds(3.24), 140));
        Assert.Equal("Tool result read_file: 1,234 chars, 2 pictures in 12 ms — \"line one\"", Assistant.ToolResultLogLine("read_file", "line one\nline two" + new string('x', 1217), 2, TimeSpan.FromMilliseconds(12.4)));
        Assert.Equal("Tool edit_file answered an error: Error: nothing matched", Assistant.ToolErrorLogLine("edit_file", "Error: nothing matched\ndetail"));
    }
}
