using NeonSidekick.App;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Which slash commands the Web UI's page may run (later on 2026-10-14): every command classed, the never list, the forms that open a PC pane
/// refused with the form that works, the pick and list forms, the settings that gate them, and an unknown word's offers.
/// </summary>
public sealed class WebCommandsTests
{
    private static readonly WebCommandSettings On = new(Commands: true, FileTools: true, ObsidianTools: true);

    public static TheoryData<SlashCommand> Commands()
    {
        var data = new TheoryData<SlashCommand>();
        foreach (var command in Enum.GetValues<SlashCommand>())
        {
            data.Add(command);
        }

        return data;
    }

    private static string LineOf(SlashCommand command) => command switch
    {
        SlashCommand.None => "hello",
        SlashCommand.Unknown => "/nosuch",
        SlashCommand.Overloaded => "/about me",
        _ => SlashCommands.Completions.FirstOrDefault(c => SlashCommands.Parse(c.Text).Command == command)?.Text ?? "/" + command.ToString().ToLowerInvariant(),
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void EveryCommand_HasAVerdict_AndARefusalSaysWhy(SlashCommand command)
    {
        string line = LineOf(command);
        var verdict = WebCommands.Check(command, "", line, On);

        if (verdict.Allowed)
        {
            Assert.Null(verdict.Reason);
            Assert.True(command == SlashCommand.None || WebCommands.Offers(command), command + " runs from the page but the dropdown does not offer it");
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        }

        if (command == SlashCommand.YouTube)
        {
            Assert.Equal(WebUiText.YouTubeTabOff, verdict.Reason);   // the bare word is the saved list (2026-10-18): the tab's, here off
            Assert.False(WebCommands.Offers(command));
        }
        else if (WebCommands.Never.Contains(command))
        {
            Assert.Equal(WebUiText.PcOnly(WebCommands.Word(line)), verdict.Reason);
            Assert.False(WebCommands.Offers(command));
        }
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void WithCommandsOff_EveryCommandNamesTheSetting(SlashCommand command)
    {
        if (command == SlashCommand.None)
        {
            return;
        }

        var verdict = WebCommands.Check(command, "x", LineOf(command), On with { Commands = false });

        Assert.Equal(WebCommandKind.Refused, verdict.Kind);
        Assert.Equal(WebUiText.CommandsOff, verdict.Reason);
    }

    [Theory]
    // Runs
    [InlineData("/new", WebCommandKind.Run)]
    [InlineData("/clear", WebCommandKind.Run)]
    [InlineData("/compact keep the plan", WebCommandKind.Run)]
    [InlineData("/help keys", WebCommandKind.Run)]
    [InlineData("/imagine a cat", WebCommandKind.Run)]
    [InlineData("/learn keep the steps", WebCommandKind.Run)]
    [InlineData("/test grid", WebCommandKind.Run)]
    [InlineData("/pdf notes.md", WebCommandKind.Run)]
    [InlineData("/pdf merge a.pdf b.pdf", WebCommandKind.Run)]
    [InlineData("/docx notes.md", WebCommandKind.Run)]   // 2026-10-30, review F99
    [InlineData("/docx reply", WebCommandKind.Run)]
    [InlineData("/print reply", WebCommandKind.Run)]
    [InlineData("/avatar 🦊", WebCommandKind.Run)]
    [InlineData("/avatar clear", WebCommandKind.Run)]
    [InlineData("/avatar user clear", WebCommandKind.Run)]
    [InlineData("/remember the cat is Tom", WebCommandKind.Run)]
    [InlineData("/timer 10m tea", WebCommandKind.Run)]
    [InlineData("/rename Trip notes", WebCommandKind.Run)]
    [InlineData("/tts off", WebCommandKind.Run)]
    [InlineData("/stt on", WebCommandKind.Run)]
    [InlineData("/memory read-only", WebCommandKind.Run)]
    [InlineData("/cwd", WebCommandKind.Run)]
    [InlineData("/model qwen3", WebCommandKind.Run)]
    [InlineData("/reasoning high", WebCommandKind.Run)]
    [InlineData("/ha", WebCommandKind.Run)]
    [InlineData("/ha on den 40%", WebCommandKind.Run)]
    [InlineData("/ha say good night", WebCommandKind.Run)]
    [InlineData("/docker", WebCommandKind.Run)]
    [InlineData("/docker stop mysql_dev", WebCommandKind.Run)]
    [InlineData("/shopping add milk", WebCommandKind.Run)]
    [InlineData("/shopping clear all", WebCommandKind.Run)]
    [InlineData("/prompts 2", WebCommandKind.Run)]
    [InlineData("/prompts list", WebCommandKind.Run)]
    [InlineData("/prompts save hello", WebCommandKind.Run)]
    [InlineData("/browser save https://example.com Example", WebCommandKind.Run)]
    [InlineData("/browser unsave 2", WebCommandKind.Run)]
    [InlineData("/plan add the tests", WebCommandKind.Run)]
    [InlineData("/plan open old-plan", WebCommandKind.Run)]
    [InlineData("/claude hi", WebCommandKind.Run)]
    // Picks and lists
    [InlineData("/model", WebCommandKind.Pick)]
    [InlineData("/reasoning", WebCommandKind.Pick)]
    [InlineData("/rewind", WebCommandKind.Pick)]
    [InlineData("/rewind 2", WebCommandKind.Pick)]
    [InlineData("/ha states light", WebCommandKind.List)]
    [InlineData("/docker logs mysql_dev", WebCommandKind.List)]
    [InlineData("/plan open", WebCommandKind.Pick)]   // the plans picker since 2026-10-18, a list until then
    [InlineData("/plan open --chat", WebCommandKind.Pick)]
    [InlineData("/plan open x --chat", WebCommandKind.Run)]
    [InlineData("/plan template", WebCommandKind.Run)]                     // 2026-10-20
    [InlineData("/plan edit the steps to add tests", WebCommandKind.Run)]   // several words: detail or a requirement, as on the PC
    [InlineData("/print", WebCommandKind.List)]
    [InlineData("/print printers", WebCommandKind.List)]
    [InlineData("/camera list", WebCommandKind.List)]
    [InlineData("/screen list", WebCommandKind.List)]
    [InlineData("/monitor list", WebCommandKind.List)]   // 2026-10-23, the monitors plan: the list alone from the page
    [InlineData("/tree", WebCommandKind.List)]
    [InlineData("/vault", WebCommandKind.List)]
    [InlineData("/process", WebCommandKind.List)]
    [InlineData("/comfy status", WebCommandKind.List)]   // the bare word's until 2026-10-18
    [InlineData("/comfy offered", WebCommandKind.List)]
    // Refused
    [InlineData("/help", WebCommandKind.Refused)]
    [InlineData("/avatar", WebCommandKind.Refused)]
    [InlineData("/avatar cat", WebCommandKind.Refused)]
    [InlineData("/rename", WebCommandKind.Refused)]
    [InlineData("/tts", WebCommandKind.Refused)]
    [InlineData("/stt wake on", WebCommandKind.Refused)]
    [InlineData("/memory", WebCommandKind.Refused)]
    [InlineData("/memory forget", WebCommandKind.Refused)]
    [InlineData("/cwd D:\\work", WebCommandKind.Refused)]
    [InlineData("/plan edit", WebCommandKind.Refused)]   // 2026-10-20: an editor on the PC
    [InlineData("/plan edit my-plan", WebCommandKind.Refused)]
    [InlineData("/learn sessions", WebCommandKind.Refused)]
    [InlineData("/shopping", WebCommandKind.Refused)]
    [InlineData("/shopping add", WebCommandKind.Refused)]
    [InlineData("/prompts", WebCommandKind.Page)]   // later on 2026-10-15: the page's own saved prompts sheet
    [InlineData("/prompts list --clear", WebCommandKind.Refused)]
    [InlineData("/browser", WebCommandKind.Refused)]
    [InlineData("/browser list", WebCommandKind.Refused)]
    [InlineData("/browser clear all", WebCommandKind.Refused)]
    [InlineData("/browser 2", WebCommandKind.Refused)]
    [InlineData("/browser https://example.com", WebCommandKind.Refused)]
    [InlineData("/camera", WebCommandKind.Refused)]
    [InlineData("/camera snap", WebCommandKind.Refused)]
    [InlineData("/screen", WebCommandKind.Refused)]
    [InlineData("/monitor", WebCommandKind.Refused)]
    [InlineData("/monitor 2 input hdmi-1", WebCommandKind.Refused)]
    [InlineData("/monitor extend", WebCommandKind.Refused)]
    [InlineData("/screen view", WebCommandKind.Refused)]
    [InlineData("/process abc", WebCommandKind.Refused)]
    [InlineData("/comfy view", WebCommandKind.Refused)]
    [InlineData("/comfy purge", WebCommandKind.Refused)]
    [InlineData("/comfy", WebCommandKind.Refused)]   // 2026-10-18: /tools comfy, a page on the PC
    [InlineData("/splash", WebCommandKind.Run)]
    [InlineData("/splash view", WebCommandKind.Refused)]   // 2026-10-18: a window or a question on the PC
    [InlineData("/splash purge", WebCommandKind.Refused)]
    [InlineData("/pasted view", WebCommandKind.Refused)]
    [InlineData("/pasted purge", WebCommandKind.Refused)]
    [InlineData("/ha stills purge", WebCommandKind.Refused)]   // 2026-10-27: the PC's yes before a delete
    [InlineData("/ha stills view", WebCommandKind.Refused)]
    [InlineData("/ha STILLS", WebCommandKind.Refused)]
    [InlineData("/camera purge", WebCommandKind.Refused)]
    [InlineData("/screen purge", WebCommandKind.Refused)]
    [InlineData("/settings", WebCommandKind.Refused)]
    [InlineData("/theme", WebCommandKind.Refused)]
    [InlineData("/view a.png", WebCommandKind.Refused)]
    [InlineData("/exit", WebCommandKind.Refused)]
    [InlineData("/webui", WebCommandKind.Refused)]
    [InlineData("/cmd dir", WebCommandKind.Refused)]
    [InlineData("/profile work", WebCommandKind.Refused)]
    [InlineData("/msgclear", WebCommandKind.Run)]   // 2026-10-16: the page shares the message history, and may clear it
    [InlineData("/msgclear all", WebCommandKind.Refused)]
    [InlineData("/sessions", WebCommandKind.Refused)]
    [InlineData("/skills", WebCommandKind.Refused)]
    [InlineData("/youtube", WebCommandKind.Refused)]
    [InlineData("/queue clear", WebCommandKind.Refused)]
    [InlineData("/botchat", WebCommandKind.Refused)]
    [InlineData("/loop 5m hi", WebCommandKind.Run)]   // later on 2026-10-15: the page's too
    [InlineData("/loop 3 hi", WebCommandKind.Run)]
    [InlineData("/loop infinite 5m check the queue", WebCommandKind.Run)]
    [InlineData("/loop 3 /imagine a cat", WebCommandKind.Run)]
    [InlineData("/loop 3 /read notes.txt", WebCommandKind.Refused)]
    [InlineData("/loop", WebCommandKind.Run)]            // the PC's usage error lands on the page
    [InlineData("/loop many hi", WebCommandKind.Run)]
    [InlineData("/loop 3 /foo", WebCommandKind.Run)]     // the PC's unknown command
    [InlineData("/loop 3 /model", WebCommandKind.Run)]   // the PC's "can't be looped"
    [InlineData("/loop 3 /help", WebCommandKind.Run)]
    [InlineData("/server", WebCommandKind.Refused)]
    [InlineData("/usage", WebCommandKind.Refused)]
    [InlineData("/sampling", WebCommandKind.Refused)]
    public void EachForm_HasItsVerdict(string line, WebCommandKind kind)
    {
        Assert.Equal(kind, WebCommands.Check(line, On).Kind);
    }

    [Fact]
    public void APaneForm_NamesTheFormThatWorks()
    {
        Assert.Equal(WebUiText.OpensPane("/rename", "/rename <name>"), WebCommands.Check("/rename", On).Reason);
        Assert.Equal(WebUiText.OpensPane("/settings", null), WebCommands.Check("/settings", On).Reason);
        Assert.Equal(WebUiText.OpensPane("/ha stills", null), WebCommands.Check("/ha stills purge", On).Reason);   // never the bare /ha
        Assert.Equal("/ha stills opens a pane or a window at the PC, so it runs there.", WebCommands.Check("/ha stills view", On).Reason);
        Assert.Equal(WebCommandKind.Run, WebCommands.Check("/ha on den", On).Kind);
        Assert.Equal(WebUiText.UseShoppingPage, WebCommands.Check("/shopping", On).Reason);
        // The Shopping tab off (later on 2026-10-15): the setting that turns it on.
        Assert.Equal(WebUiText.ShoppingTabOff, WebCommands.Check("/shopping", On with { ShoppingTab = false }).Reason);
        Assert.Equal(WebUiText.ShoppingTabOff, WebCommands.Check("/shopping add", On with { ShoppingTab = false }).Reason);
        Assert.Equal(WebCommandKind.Run, WebCommands.Check("/shopping add milk", On with { ShoppingTab = false }).Kind);   // adds from the chat whatever the tab
        Assert.Equal(WebUiText.PcOnly("/cwd <path>"), WebCommands.Check("/cwd D:\\work", On).Reason);

        // /youtube list (later on 2026-10-16): the YouTube tab's saved list, or the setting while the tab is off; every other form the PC's.
        Assert.Equal(WebUiText.UseYouTubePage, WebCommands.Check("/youtube list", On with { YouTubeTab = true }).Reason);
        Assert.Equal(WebUiText.UseYouTubePage, WebCommands.Check("/youtube list --clear", On with { YouTubeTab = true }).Reason);
        Assert.Equal(WebUiText.YouTubeTabOff, WebCommands.Check("/youtube list", On).Reason);
        // The bare /youtube (2026-10-18): the saved list as list is, so the same pointer.
        Assert.Equal(WebUiText.UseYouTubePage, WebCommands.Check("/youtube", On with { YouTubeTab = true }).Reason);
        Assert.Equal(WebUiText.YouTubeTabOff, WebCommands.Check("/youtube", On).Reason);
        Assert.Equal(WebUiText.PcOnly("/youtube"), WebCommands.Check("/youtube status", On with { YouTubeTab = true }).Reason);
        Assert.Equal(WebUiText.PcOnly("/youtube"), WebCommands.Check("/youtube play aqz-KE-bpKQ", On with { YouTubeTab = true }).Reason);
        Assert.Contains(SlashCommand.YouTube, WebCommands.Never);
    }

    [Fact]
    public void ALoopedCommand_IsRefusedWithItsOwnReason_AndNeverAsText()
    {
        var read = WebCommands.Check("/loop 3 1s /read notes.txt", On);

        Assert.Equal(WebUiText.LoopRefused(WebCommands.Check("/read notes.txt", On).Reason!), read.Reason);
        Assert.False(read.AsText);
        Assert.Equal(WebUiText.CommandsOff, WebCommands.Check("/loop 3 hi", On with { Commands = false }).Reason);
        Assert.True(WebCommands.Offers(App.SlashCommand.Loop));
    }

    [Fact]
    public void TheTrees_FollowTheirToolSwitches()
    {
        Assert.Equal(WebUiText.Needs("/tree", "File tools"), WebCommands.Check("/tree", On with { FileTools = false }).Reason);
        Assert.Equal(WebUiText.Needs("/vault", "Obsidian tools"), WebCommands.Check("/vault", On with { ObsidianTools = false }).Reason);
    }

    [Fact]
    public void AnUnknownWord_MayGoAsAMessage_AndDoubleSlashesNameTheCommandMeant()
    {
        var path = WebCommands.Check("/home/me/notes", On);
        var typo = WebCommands.Check("//tools web", On);

        Assert.Equal((WebCommandKind.Refused, true, (string?)null), (path.Kind, path.AsText, path.Use));
        Assert.Equal(WebUiText.UnknownCommand("/home/me/notes"), path.Reason);
        Assert.True(typo.AsText);
        Assert.Equal("/tools web", typo.Use);
    }
}
