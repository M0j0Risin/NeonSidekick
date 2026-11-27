using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/usage sysprompt [to=&lt;path&gt;]</c> (2026-11-23, the user's ask): the next request written to a Markdown file in the working folder or at
/// <c>to=</c>, never over a file and never outside the sandbox; it waits for a reply under way; the completion offers the word and its <c>to=</c>.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task UsageSysprompt_WritesTheNextRequest_InTheWorkingFolder_OrAtTo_NeverOverAFile()
    {
        _settings.Update(d => d.TtsOutput = false);
        string folder = Path.Combine(_dir, "work");
        Directory.CreateDirectory(Path.Combine(folder, "notes"));
        _settings.Update(d => d.WorkingDirectory = folder);
        string outside = Path.Combine(_dir, "outside.md");
        _chat.EnqueueText("Hello there.");
        PushLine("hi");
        PushLine("/usage sysprompt");
        PushLine("/usage sysprompt conversation");
        PushLine("/usage sysprompt to=notes/");
        PushLine("/usage sysprompt to=notes");
        PushLine("/usage sysprompt to=ctx");
        PushLine("/usage sysprompt to=ctx.md");
        PushLine("/usage sysprompt to=out.pdf");
        PushLine("/usage sysprompt to=.neon/x.md");
        PushLine("/usage sysprompt \"to=" + outside + "\"");
        PushLine("/usage sysprompt foo");
        PushLine("/exit");

        string output = await RunAsync();

        var now = _time.GetLocalNow();
        string first = Path.Combine(folder, UsageText.ExportFileName(now, 1));
        Assert.True(File.Exists(first), output);
        Assert.True(File.Exists(Path.Combine(folder, UsageText.ExportFileName(now, 2))));   // the same instant: -2, never over the first
        Assert.True(File.Exists(Path.Combine(folder, "notes", UsageText.ExportFileName(now, 1))));
        Assert.True(File.Exists(Path.Combine(folder, "notes", UsageText.ExportFileName(now, 2))));   // an existing folder named bare
        Assert.True(File.Exists(Path.Combine(folder, "ctx.md")));
        string bare = File.ReadAllText(first);   // the system message and the tools alone (2026-11-23, the user's ask)
        Assert.StartsWith(UsageExport.PromptHeading + "\n", bare);
        Assert.Contains("## System message\n~", bare);
        Assert.Contains(UsageExport.PromptContentsLine + "\n", bare);
        Assert.DoesNotContain("## Conversation", bare);
        Assert.DoesNotContain("Hello there.", bare);
        Assert.Contains("- **Size:** ~", bare);
        Assert.Contains("wrote the system prompt to " + UsageText.ExportFileName(now, 1), output);
        string text = File.ReadAllText(Path.Combine(folder, UsageText.ExportFileName(now, 2)));
        Assert.StartsWith(UsageExport.Heading + "\n", text);
        Assert.Matches("### \\d+ · user\n```text\nhi\n```", text);   // after the app's own opening calls
        Assert.Matches("### \\d+ · assistant\n```text\nHello there.\n```", text);
        Assert.Contains(UsageExport.ContentsLine, text);
        Assert.DoesNotContain("\r", text);
        Assert.Contains("wrote the next request's context to " + UsageText.ExportFileName(now, 2), output);
        Assert.Contains(UsageText.ExportExists("ctx.md"), output);
        Assert.Contains(OutputNames.Refusal("out.pdf", UsageText.ExportExtension, _ => false)!, output);
        Assert.False(File.Exists(Path.Combine(folder, ".neon", "x.md")));
        Assert.False(File.Exists(outside));
        Assert.Contains(UsageText.SyspromptUsage, output);
        Assert.Equal(5, Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories).Length);   // two here, two in notes, ctx.md once
    }

    [Fact]
    public void UsageSysprompt_WaitsForAReply_ItsTabsStayPanes()
    {
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Usage, "sysprompt"));
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Usage, "SYSPROMPT to=x"));
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Usage, "sysprompt conversation"));
        Assert.Equal(ChatScreen.MidTurnPolicy(SlashCommand.Usage, hasArgs: true), ChatScreen.MidTurnPolicy(SlashCommand.Usage, "prompt"));
        Assert.Equal(ChatScreen.MidTurnPolicy(SlashCommand.Usage, hasArgs: false), ChatScreen.MidTurnPolicy(SlashCommand.Usage, ""));
    }

    [Fact]
    public void UsageSysprompt_TheCompletion_OffersTheWord_ThenItsTo()
    {
        Assert.Contains(new CompletionItem(UsageText.SyspromptWord, UsageText.SyspromptWordNote), ChatScreen.ArgumentItems("/usage", "sys", Sources()));
        Assert.Equal(
            [new CompletionItem("sysprompt conversation", UsageText.ConversationWordNote), new CompletionItem("sysprompt to=", UsageText.SyspromptToNote)],
            ChatScreen.ArgumentItems("/usage", "sysprompt ", Sources()));
        Assert.Equal([new CompletionItem("sysprompt conversation to=", UsageText.SyspromptToNote)], ChatScreen.ArgumentItems("/usage", "sysprompt conversation ", Sources()));
        Assert.Empty(ChatScreen.ArgumentItems("/usage", "sysprompt to=x", Sources()));
        Assert.Equal([new CompletionItem("sysprompt to=x conversation", UsageText.ConversationWordNote)], ChatScreen.ArgumentItems("/usage", "sysprompt to=x ", Sources()));   // the last-7 review's #31
        Assert.Empty(ChatScreen.ArgumentItems("/usage", "sysprompt to=x conversation ", Sources()));
        Assert.Equal([new CompletionItem(UsageText.PromptWord, UsageText.PromptWordNote)], ChatScreen.ArgumentItems("/usage", "pro", Sources()));
    }

    /// <summary>
    /// A quoted to= path with a space in it (2026-11-24, review #51): the word ends at its first space outside quotes, so conversation is offered
    /// after it, whole or half typed, and kept as typed; an open quote is still the to= word.
    /// </summary>
    [Fact]
    public void UsageSysprompt_TheCompletion_ReadsAQuotedTo_AsOneWord()
    {
        var offered = new CompletionItem("sysprompt to=\"my notes.md\" conversation", UsageText.ConversationWordNote);
        Assert.Equal([offered], ChatScreen.ArgumentItems("/usage", "sysprompt to=\"my notes.md\" ", Sources()));
        Assert.Equal([offered], ChatScreen.ArgumentItems("/usage", "sysprompt to=\"my notes.md\" co", Sources()));
        Assert.Empty(ChatScreen.ArgumentItems("/usage", "sysprompt to=\"my ", Sources()));
        Assert.Equal([new CompletionItem("sysprompt to=x conversation", UsageText.ConversationWordNote)], ChatScreen.ArgumentItems("/usage", "sysprompt to=x co", Sources()));
        Assert.Equal(-1, ChatScreen.FirstSpaceOutsideQuotes("to=\"a b"));
        Assert.Equal(12, ChatScreen.FirstSpaceOutsideQuotes("to=\"my a.md\" x"));
    }

    /// <summary>
    /// The write never lands over a name already taken (2026-11-24, review #49): a folder at to=x's x.md is "already there", a parent that is a
    /// file fails, and a folder at the first made name moves on to the -2 name.
    /// </summary>
    [Fact]
    public async Task UsageSysprompt_ANameTakenByAFolder_IsAlreadyThere_OrTheNextName()
    {
        _settings.Update(d => d.TtsOutput = false);
        string folder = Path.Combine(_dir, "work");
        Directory.CreateDirectory(Path.Combine(folder, "x.md"));
        File.WriteAllText(Path.Combine(folder, "f.txt"), "a file");
        var now = _time.GetLocalNow();
        Directory.CreateDirectory(Path.Combine(folder, "d", UsageText.ExportFileName(now, 1)));
        _settings.Update(d => d.WorkingDirectory = folder);
        PushLine("/usage sysprompt to=x");
        PushLine("/usage sysprompt to=f.txt/y.md");
        PushLine("/usage sysprompt to=d/");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(UsageText.ExportExists("x.md"), output);
        Assert.Contains("Could not write f.txt", output);
        Assert.True(File.Exists(Path.Combine(folder, "d", UsageText.ExportFileName(now, 2))), output);
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, "x.md")));
        Assert.Single(Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories));   // the -2 name alone, no temp left
    }

    /// <summary>
    /// A to= of separators alone names the root, never "here" (2026-11-24, review #55): outside the sandbox it is refused; a blank to= names
    /// nothing. No export lands in the working folder either way.
    /// </summary>
    [Fact]
    public async Task UsageSysprompt_ToTheRoot_IsJudgedAsTheRoot_ABlankToIsTheUsage()
    {
        _settings.Update(d => d.TtsOutput = false);
        string folder = Path.Combine(_dir, "work");
        Directory.CreateDirectory(folder);
        _settings.Update(d => d.WorkingDirectory = folder);
        PushLine("/usage sysprompt to=/");
        PushLine("/usage sysprompt to=\\");
        PushLine("/usage sysprompt \"to= \"");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain("(wrote the", output);
        Assert.Contains(UsageText.SyspromptUsage, output);
        Assert.Empty(Directory.GetFiles(folder, "neon-sysprompt-*", SearchOption.AllDirectories));
    }
}
