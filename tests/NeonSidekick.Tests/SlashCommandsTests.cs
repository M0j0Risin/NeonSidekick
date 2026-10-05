using NeonSidekick.App;

namespace NeonSidekick.Tests;

public class SlashCommandsTests
{
    [Theory]
    [InlineData("/help", SlashCommand.Help)]
    [InlineData("/HELP", SlashCommand.Help)]
    [InlineData("  /clear  ", SlashCommand.Clear)]
    [InlineData("/new", SlashCommand.New)]
    [InlineData("/NEW", SlashCommand.New)]
    [InlineData("  /new  ", SlashCommand.New)]
    [InlineData("/splash", SlashCommand.Splash)]   // 2026-09-19
    [InlineData("/SPLASH", SlashCommand.Splash)]
    [InlineData("/server", SlashCommand.Server)]
    [InlineData("/SERVER", SlashCommand.Server)]
    [InlineData("/model", SlashCommand.Model)]
    [InlineData("/reasoning", SlashCommand.Reasoning)]
    [InlineData("/REASONING", SlashCommand.Reasoning)]
    [InlineData("/settings", SlashCommand.Settings)]
    [InlineData("//", SlashCommand.Settings)]
    [InlineData("/loop", SlashCommand.Loop)]     // 2026-09-21
    [InlineData("/LOOP", SlashCommand.Loop)]
    [InlineData("/expand", SlashCommand.Expand)]       // later on 2026-09-22
    [InlineData("/COLLAPSE", SlashCommand.Collapse)]
    [InlineData("/tts", SlashCommand.Tts)]
    [InlineData("/stt", SlashCommand.Voice)]
    [InlineData("/wake", SlashCommand.Wake)]
    [InlineData("/interrupt", SlashCommand.Interrupt)]
    [InlineData("/remember", SlashCommand.Remember)]
    [InlineData("/memory", SlashCommand.Memory)]
    [InlineData("/MEMORY", SlashCommand.Memory)]
    [InlineData("/cmdcopy", SlashCommand.CmdCopy)]
    [InlineData("/cmdclear", SlashCommand.CmdClear)]
    [InlineData("/CmdCopy", SlashCommand.CmdCopy)]
    [InlineData("/keycopy", SlashCommand.KeyCopy)]   // 2026-09-28
    [InlineData("/keycheck", SlashCommand.KeyCheck)]   // 2026-10-04
    [InlineData("/KeyCopy", SlashCommand.KeyCopy)]
    [InlineData("/persona", SlashCommand.Persona)]
    [InlineData("/operata", SlashCommand.Operata)]
    [InlineData("/OPERATA", SlashCommand.Operata)]
    [InlineData("/vocalia", SlashCommand.Vocalia)]
    [InlineData("/VOCALIA", SlashCommand.Vocalia)]
    [InlineData("/sys", SlashCommand.Sys)]   // the name since 2026-09-21; /sysprompt before
    [InlineData("/SYS", SlashCommand.Sys)]
    [InlineData("/test", SlashCommand.Test)]   // 2026-09-28
    [InlineData("/window", SlashCommand.Window)]
    [InlineData("/sessions", SlashCommand.Session)]   // the plural since later on 2026-09-21
    [InlineData("/SESSIONS", SlashCommand.Session)]
    [InlineData("/usage", SlashCommand.Usage)]
    [InlineData("/perfbar", SlashCommand.Perf)]
    [InlineData("/toolbar", SlashCommand.Toolbar)]   // later on 2026-09-30
    [InlineData("/about", SlashCommand.About)]
    [InlineData("/ABOUT", SlashCommand.About)]
    [InlineData("/skills", SlashCommand.Skills)]
    [InlineData("/tools", SlashCommand.Tools)]
    [InlineData("/TOOLS", SlashCommand.Tools)]
    [InlineData("/mcp", SlashCommand.Mcp)]
    [InlineData("/MCP", SlashCommand.Mcp)]
    [InlineData("/compact", SlashCommand.Compact)]
    [InlineData("/COMPACT", SlashCommand.Compact)]
    [InlineData("/Usage", SlashCommand.Usage)]
    [InlineData("/profile", SlashCommand.Profile)]
    [InlineData("/timer", SlashCommand.Timer)]
    [InlineData("/TIMER", SlashCommand.Timer)]
    [InlineData("/cwd", SlashCommand.Cwd)]
    [InlineData("/tree", SlashCommand.Tree)]
    [InlineData("/TREE", SlashCommand.Tree)]
    [InlineData("/explore", SlashCommand.Explore)]
    [InlineData("/EXPLORE", SlashCommand.Explore)]
    [InlineData("/terminal", SlashCommand.Terminal)]   // 2026-10-03
    [InlineData("/TERMINAL", SlashCommand.Terminal)]
    [InlineData("/speak", SlashCommand.Speak)]
    [InlineData("/SPEAK", SlashCommand.Speak)]
    [InlineData("/view", SlashCommand.View)]
    [InlineData("/VIEW", SlashCommand.View)]
    [InlineData("/echo", SlashCommand.Echo)]
    [InlineData("/ECHO", SlashCommand.Echo)]
    [InlineData("/CWD", SlashCommand.Cwd)]
    [InlineData("/copy", SlashCommand.Copy)]
    [InlineData("/COPY", SlashCommand.Copy)]
    [InlineData("/draft", SlashCommand.Draft)]
    [InlineData("/gituser", SlashCommand.GitUser)]
    [InlineData("/GITUSER", SlashCommand.GitUser)]
    [InlineData("/DRAFT", SlashCommand.Draft)]
    [InlineData("/PERSONA", SlashCommand.Persona)]
    [InlineData("/exit", SlashCommand.Exit)]
    public void Parse_KnownWords(string line, SlashCommand expected)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(expected, command);
        Assert.Equal("", args);
    }

    [Fact]
    public void ExpandAndCollapse_TakeNothing_ToolsTakesAGroup()
    {
        // Later on 2026-09-22 (the user's ask): /tools expand | collapse became the root /expand and /collapse. /tools takes a
        // word again since 2026-10-03 (/tools <group>, the toolbar's tool switches): the handler judges it, expand its usage error.
        Assert.Equal((SlashCommand.Overloaded, "now"), SlashCommands.Parse("/expand now"));
        Assert.Equal((SlashCommand.Overloaded, "all"), SlashCommands.Parse("/collapse all"));
        Assert.Equal((SlashCommand.Tools, "expand"), SlashCommands.Parse("/tools expand"));
        Assert.Equal((SlashCommand.Tools, "web"), SlashCommands.Parse("/tools web"));
    }

    [Fact]
    public void Parse_SkillsTakesTheRestOfTheLine_TheHandlerJudgesIt()
    {
        // /skill <name> [message] loaded a skill from 2026-09-16 until later on 2026-09-18 (the user's
        // call: the #-mention covers it); a bare /skills is the pane (the plural since 2026-09-19, beside /tools;
        // /skill from later on 2026-09-18 until then, unknown now). Anything after it was Overloaded until
        // 2026-09-21, when /skills edit <name> came, and is again since 2026-09-23, when the scope page's edit row took its place.
        Assert.Equal((SlashCommand.Skills, ""), SlashCommands.Parse("/skills"));
        Assert.Equal((SlashCommand.Skills, ""), SlashCommands.Parse("  /SKILLS  "));
        // Since 2026-09-26 the argument is the handler's again: /skills add <source> installs, any other word is its usage error.
        Assert.Equal((SlashCommand.Skills, "add anthropics/skills/pdf --yes"), SlashCommands.Parse("/skills add anthropics/skills/pdf --yes"));
        Assert.Equal((SlashCommand.Skills, "haiku write one about rain"), SlashCommands.Parse("/SKILLS  haiku write one about rain "));
        Assert.Equal((SlashCommand.Skills, "list"), SlashCommands.Parse("/skills list"));   // /skill list was the pane for part of 2026-09-18
        Assert.Equal((SlashCommand.Loop, "3 hi there"), SlashCommands.Parse("/loop 3 hi there"));   // 2026-09-21: the count and the message are the handler's
        Assert.Equal((SlashCommand.Loop, ""), SlashCommands.Parse("/loop"));
    }

    [Fact]
    public void Parse_MemoryTakesTheRestOfTheLine()
    {
        // 2026-09-22: forget that morning, then copy <profile> [overwrite] — the grammar /memcopy had, folded in; the words are the handler's to judge.
        Assert.Equal((SlashCommand.Memory, ""), SlashCommands.Parse("/memory"));
        Assert.Equal((SlashCommand.Memory, "forget"), SlashCommands.Parse("/memory  forget "));
        Assert.Equal((SlashCommand.Memory, "copy work"), SlashCommands.Parse("/memory copy work"));
        Assert.Equal((SlashCommand.Memory, "copy work overwrite"), SlashCommands.Parse("/MEMORY  copy work overwrite "));
    }

    [Fact]
    public void Parse_CmdCopyTakesTheRestOfTheLine()
    {
        // 2026-09-21: the grammar /memcopy had then (/memory copy since 2026-09-22), for the allowed shell commands.
        Assert.Equal((SlashCommand.CmdCopy, "work"), SlashCommands.Parse("/cmdcopy work"));
        Assert.Equal((SlashCommand.CmdCopy, "work overwrite"), SlashCommands.Parse("/cmdcopy  work overwrite "));
        Assert.Equal((SlashCommand.CmdCopy, ""), SlashCommands.Parse("/cmdcopy"));
        Assert.Equal((SlashCommand.CmdCopy, "work --history overwrite"), SlashCommands.Parse("/cmdcopy work --history overwrite"));   // 2026-09-25
        Assert.Equal((SlashCommand.CmdClear, ""), SlashCommands.Parse("/cmdclear"));   // 2026-09-25: no argument
        Assert.Equal((SlashCommand.KeyCopy, "work"), SlashCommands.Parse("/keycopy  work "));   // 2026-09-28
        Assert.Equal((SlashCommand.KeyCopy, ""), SlashCommands.Parse("/keycopy"));
        Assert.Equal((SlashCommand.Overloaded, "all"), SlashCommands.Parse("/cmdclear all"));
    }

    [Fact]
    public void Parse_PromptFileCopyIsTheRestOfTheLine()
    {
        // 2026-09-21: copy <profile> [force] rides on the argument the three took for reset.
        Assert.Equal((SlashCommand.Persona, "copy work force"), SlashCommands.Parse("/persona copy work force"));
        Assert.Equal((SlashCommand.Operata, "copy  work"), SlashCommands.Parse("/operata  copy  work "));   // trimmed, not collapsed: the handler splits
        Assert.Equal((SlashCommand.Vocalia, "copy work"), SlashCommands.Parse("/vocalia copy work"));
    }

    [Fact]
    public void Parse_ModelTakesAnArgument()
    {
        var (command, args) = SlashCommands.Parse("/model   qwen3-8b  ");
        Assert.Equal(SlashCommand.Model, command);
        Assert.Equal("qwen3-8b", args);
        Assert.Equal((SlashCommand.Model, "qwen3-8b"), SlashCommands.Parse("/model   qwen3-8b  "));
    }

    [Theory]
    [InlineData("/reasoning high", "high")]
    [InlineData("/REASONING  XHigh ", "XHigh")]
    [InlineData("/reasoning lots", "lots")]   // the menu decides whether it is a level
    [InlineData("/REASONING  low ", "low")]
    public void Parse_ReasoningTakesAnArgument(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Reasoning, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/compact", "")]
    [InlineData("/compact keep the file list", "keep the file list")]
    [InlineData("/Compact   what we decided about the icon  ", "what we decided about the icon")]
    public void Parse_CompactTakesAFocus(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Compact, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/server http://127.0.0.1:5000", "http://127.0.0.1:5000")]
    [InlineData("/Server   localhost:9  ", "localhost:9")]   // the screen decides whether it is a URL
    [InlineData("/SERVER  localhost:9 ", "localhost:9")]
    public void Parse_ServerTakesAnArgument(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Server, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/tts on", "on")]
    [InlineData("/TTS off", "off")]
    [InlineData("/tts maybe", "maybe")]   // the screen decides what the argument means
    public void Parse_TtsTakesAnArgument(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Tts, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/stt on", "on")]
    [InlineData("/STT off", "off")]
    [InlineData("/stt maybe", "maybe")]
    public void Parse_VoiceTakesAnArgument(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Voice, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/wake on", "on")]
    [InlineData("/WAKE off", "off")]
    [InlineData("/wake maybe", "maybe")]
    public void Parse_WakeTakesAnArgument(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Wake, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/interrupt on", "on")]
    [InlineData("/INTERRUPT off", "off")]
    [InlineData("/interrupt maybe", "maybe")]
    [InlineData("/interrupt off", "off")]
    [InlineData("/interrupt", "")]
    public void Parse_InterruptTakesAnArgument(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Interrupt, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/remember my name is Chris", "my name is Chris")]
    [InlineData("/REMEMBER   I live in Leeds  ", "I live in Leeds")]
    [InlineData("/remember what does /forget do?", "what does /forget do?")]
    public void Parse_RememberTakesTheRestOfTheLine(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Remember, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/profile", "")]
    [InlineData("/profile work", "work")]
    [InlineData("/PROFILE  add  work ", "add  work")]
    [InlineData("/profile delete work", "delete work")]
    public void Parse_ProfileTakesTheRestOfTheLine(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Profile, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/timer", "")]
    [InlineData("/timer 10m cooking", "10m cooking")]
    [InlineData("/TIMER  stop  all ", "stop  all")]
    [InlineData("/timer 1h 30m the big pot", "1h 30m the big pot")]
    public void Parse_TimerTakesTheRestOfTheLine(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Timer, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/cwd", "")]
    [InlineData("/cwd default", "default")]
    [InlineData("/cwd ~", "~")]
    [InlineData("/CWD  D:/my files/notes ", "D:/my files/notes")]
    public void Parse_CwdTakesTheRestOfTheLine(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Cwd, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/tree", "")]
    [InlineData("/tree docs", "docs")]
    [InlineData("/TREE  sub folder/deeper ", "sub folder/deeper")]
    [InlineData("/tree  docs/deep ", "docs/deep")]
    public void Parse_TreeTakesTheRestOfTheLine(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Tree, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/speak", "")]
    [InlineData("/speak notes.md", "notes.md")]
    [InlineData("/SPEAK  docs/a b.md ", "docs/a b.md")]
    public void Parse_SpeakTakesTheRestOfTheLine(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Speak, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/explore", "")]
    [InlineData("/explore docs", "docs")]
    [InlineData("/Explore  sub folder ", "sub folder")]
    [InlineData("/EXPLORE  sub folder ", "sub folder")]
    public void Parse_ExploreTakesTheRestOfTheLine(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Explore, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/terminal", "")]
    [InlineData("/terminal docs", "docs")]
    [InlineData("/Terminal  sub folder ", "sub folder")]
    public void Parse_TerminalTakesTheRestOfTheLine(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Terminal, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/copy", "")]
    [InlineData("/copy 2", "2")]
    [InlineData("/copy all", "all")]
    [InlineData("/COPY  -1 ", "-1")]
    [InlineData("/COPY  2 ", "2")]
    public void Parse_CopyTakesTheRestOfTheLine(string line, string expectedArgs)
    {
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(SlashCommand.Copy, command);
        Assert.Equal(expectedArgs, args);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/bogus")]
    [InlineData("/////")]
    public void Parse_AnUnknownWord_IsUnknown(string line)
    {
        Assert.Equal(SlashCommand.Unknown, SlashCommands.Parse(line).Command);
    }

    // A command we know, given an argument it does not take: its own case since 2026-09-17, the
    // argument kept, so the screen says the command takes nothing instead of calling it unknown.
    [Theory]
    [InlineData("/exit the program please", "the program please")]
    [InlineData("/clear everything", "everything")]
    [InlineData("/new everything", "everything")]
    [InlineData("/splash again", "again")]   // 2026-09-19
    [InlineData("/sys tools", "tools")]
    [InlineData("/usage reset", "reset")]
    [InlineData("/about x", "x")]
    [InlineData("/help me", "me")]
    [InlineData("/window 80", "80")]
    [InlineData("/draft notes.txt", "notes.txt")]   // 2026-09-19: the editor is the argument, never a file
    public void Parse_AKnownCommandWithAnArgumentItDoesNotTake_IsOverloaded(string line, string args)
    {
        Assert.Equal((SlashCommand.Overloaded, args), SlashCommands.Parse(line));
    }

    [Fact]
    public void TakesArgument_IsTheOneList()
    {
        SlashCommand[] withArgument =
        [
            SlashCommand.Compact, SlashCommand.Server, SlashCommand.Model, SlashCommand.Reasoning, SlashCommand.Sampling, SlashCommand.Theme,   // /theme [name] 2026-09-23; /sampling [field value] 2026-09-28
            SlashCommand.Tts, SlashCommand.Voice, SlashCommand.Wake, SlashCommand.Interrupt, SlashCommand.Speak, SlashCommand.View, SlashCommand.Imagine, SlashCommand.Comfy, SlashCommand.Echo,   // /imagine 2026-09-24
            SlashCommand.Learn,
            SlashCommand.Persona, SlashCommand.Operata, SlashCommand.Vocalia,
            SlashCommand.Remember, SlashCommand.Memory, SlashCommand.CmdCopy, SlashCommand.KeyCopy, SlashCommand.Profile, SlashCommand.Timer,   // /cmdcopy 2026-09-21; /keycopy 2026-09-28; /memory 2026-09-22 (forget, then copy <profile> [overwrite], the folded /memcopy)
            SlashCommand.Cwd, SlashCommand.Tree, SlashCommand.Vault, SlashCommand.Explore, SlashCommand.Terminal, SlashCommand.Copy, SlashCommand.Session, SlashCommand.Rename, SlashCommand.GitUser,   // /vault [path] 2026-09-23; /terminal [folder] 2026-10-03; /rename [name] 2026-10-05
            SlashCommand.Loop, SlashCommand.Plan, SlashCommand.BotChat, SlashCommand.Claude, SlashCommand.Queue, SlashCommand.Skills, SlashCommand.Test, SlashCommand.HomeAssistant, SlashCommand.Docker, SlashCommand.Camera, SlashCommand.Screen, SlashCommand.Print, SlashCommand.Pdf, SlashCommand.Perf, SlashCommand.Toolbar, SlashCommand.Header, SlashCommand.Rewind, SlashCommand.Log, SlashCommand.Process, SlashCommand.Tools, SlashCommand.Settings,   // /process [id] 2026-10-05; /settings <words> | changed 2026-10-04; /pdf later on 2026-10-03; /tools <group> 2026-10-03; /log [--file] later on 2026-10-02; /camera and /docker 2026-10-02; /header [on|off] later still on 2026-10-01; /toolbar [on|off] later on 2026-09-30; /rewind [n] 2026-09-30; /perfbar later on 2026-09-29; /print 2026-09-28; /ha 2026-09-28; /test 2026-09-28; /claude 2026-09-27; /skills add 2026-09-26; /plan 2026-09-26; /botchat 2026-09-24; 2026-09-21 (/queue clear later that day; /skills with edit <name> from then until 2026-09-23); /tools off the list later on 2026-09-22, its expand and collapse root words
        ];
        foreach (var command in Enum.GetValues<SlashCommand>())
        {
            Assert.Equal(withArgument.Contains(command), SlashCommands.TakesArgument(command));
        }
    }

    [Theory]
    [InlineData("/persona reset", SlashCommand.Persona, "reset")]
    [InlineData("/PERSONA  Reset ", SlashCommand.Persona, "Reset")]
    [InlineData("/persona pirate", SlashCommand.Persona, "pirate")]     // the handler judges the word (the usage line)
    [InlineData("/operata reset", SlashCommand.Operata, "reset")]
    [InlineData("/operata strict", SlashCommand.Operata, "strict")]
    [InlineData("/vocalia reset", SlashCommand.Vocalia, "reset")]
    [InlineData("/vocalia loud", SlashCommand.Vocalia, "loud")]
    public void Parse_PromptFilesTakeTheRestOfTheLine(string line, SlashCommand expected, string expectedArgs)
    {
        // Since 2026-09-16: `reset` removes the file; before, any argument made the line unknown.
        var (command, args) = SlashCommands.Parse(line);
        Assert.Equal(expected, command);
        Assert.Equal(expectedArgs, args);
    }

    [Fact]
    public void Completions_AreTheBaseCommandsSorted_WithTheirSummaries_NoAlias()
    {
        var items = SlashCommands.Completions;

        Assert.Equal(SlashCommands.HelpEntries.Count, items.Count);
        Assert.Equal(items.Select(i => i.Text).OrderBy(t => t, StringComparer.Ordinal), items.Select(i => i.Text));
        Assert.Equal("/about", items[0].Text);
        Assert.Equal("/window", items[^1].Text);
        Assert.DoesNotContain(items, i => i.Text is "//" or "///" or "////");   // the alias is never a row (nor the two that came and went on 2026-09-21)
        Assert.Contains(items, i => i.Text == "/loop");   // 2026-09-21
        Assert.All(SlashCommands.HelpEntries, e => Assert.Contains(new NeonSidekick.UI.CompletionItem(e.Command, e.Summary), items));
        Assert.Equal(["/sampling", "/screen", "/server", "/sessions", "/settings", "/skills", "/speak", "/splash", "/stt", "/sys"], items.Where(i => i.Text.StartsWith("/s", StringComparison.Ordinal)).Select(i => i.Text));
        Assert.Equal(["/terminal", "/test", "/theme", "/timer", "/toolbar", "/tools", "/tree", "/tts"], items.Where(i => i.Text.StartsWith("/t", StringComparison.Ordinal)).Select(i => i.Text));   // /tools among them since 2026-09-19
    }

    [Fact]
    public void Police_IsABareCommand_TheOfficersWord()
    {
        // /police (2026-09-22): the Shell police page; no argument, so one given is the overloaded error.
        Assert.Equal((SlashCommand.Police, ""), SlashCommands.Parse("/police"));
        Assert.Equal((SlashCommand.Overloaded, "off"), SlashCommands.Parse("/police off"));
        Assert.False(SlashCommands.TakesArgument(SlashCommand.Police));
        Assert.Equal("/police", SlashCommands.PoliceWord);
        Assert.DoesNotContain(" on or off", SlashCommands.HelpEntries.Single(e => e.Command == "/police").Summary);   // 2026-10-05: it takes no on|off, so the description never says it does
    }

    [Fact]
    public void Vault_IsABareCommand()
    {
        // /vault (2026-09-22): the vault's tree; a folder under it since 2026-09-23, as /tree takes one.
        Assert.Equal((SlashCommand.Vault, ""), SlashCommands.Parse("/vault"));
        Assert.Equal((SlashCommand.Vault, ""), SlashCommands.Parse("  /VAULT "));
        Assert.Equal((SlashCommand.Vault, "Notes"), SlashCommands.Parse("/vault Notes"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Vault));
        Assert.Contains(SlashCommands.Completions, i => i.Text == "/vault");
    }

    [Fact]
    public void Log_IsACommand_InAnyRun_AndTakesTheFileSwitch()
    {
        // /log (2026-09-22, the user's ask): only with --log until 2026-10-02, when the bare word took to the log window and
        // the file moved to /log --file; a command in every run since, its argument the handler's to judge.
        Assert.Equal((SlashCommand.Log, ""), SlashCommands.Parse("/log"));
        Assert.Equal((SlashCommand.Log, ""), SlashCommands.Parse("  /LOG  "));
        Assert.Equal((SlashCommand.Log, "--file"), SlashCommands.Parse("/log --file"));
        Assert.Equal((SlashCommand.Log, "now"), SlashCommands.Parse("/log now"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Log));
    }

    [Fact]
    public void Process_IsACommand_WithAnOptionalId_OnTheAdvancedTab()
    {
        // /process (2026-10-05, the user's pick): the list bare, the process window with an id; the handler judges the id.
        Assert.Equal((SlashCommand.Process, ""), SlashCommands.Parse("/process"));
        Assert.Equal((SlashCommand.Process, "proc_3f"), SlashCommands.Parse("  /PROCESS proc_3f "));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Process));
        Assert.Contains("/process", SlashCommands.Words);
        var entries = SlashCommands.HelpEntries.ToList();
        int process = entries.FindIndex(e => e.Command == "/process");
        Assert.Equal("/print", entries[process - 1].Command);
        Assert.Equal("/profile", entries[process + 1].Command);
        Assert.False(SlashCommands.IsBasic(entries[process]));
        Assert.Contains(SlashCommands.Completions, i => i.Text == "/process");
    }

    [Fact]
    public void Help_HasTheLogRow_InItsSortedPlace()
    {
        var entries = SlashCommands.HelpEntries.ToList();
        int log = entries.FindIndex(e => e.Command == "/log");
        Assert.True(log > 0);
        Assert.Equal("/learn", entries[log - 1].Command);   // A to Z since 2026-09-27 (directly above /help until then)
        Assert.Equal("/loop", entries[log + 1].Command);
        Assert.Contains("/log", SlashCommands.Words);
        Assert.Contains(Row(entries[log - 1]) + Row(entries[log]) + Row(entries[log + 1]), SlashCommands.HelpText);
        Assert.Contains(Row("/log", entries[log].Summary, "/log", "/log --file"), SlashCommands.HelpText);   // both forms, stacked
        Assert.Contains(new NeonSidekick.UI.CompletionItem("/log", entries[log].Summary), SlashCommands.Completions);
        Assert.Contains(SlashCommands.CompletionsWithoutExit, i => i.Text == "/log");
        Assert.False(SlashCommands.IsBasic(entries[log]));
    }

    [Fact]
    public void CompletionsWithoutExit_IsCompletionsLessExit_InTheSameOrder()
    {
        // The list under Hide /exit autocomplete (2026-09-18): /exit is a word to type in full, never a row to pick by mistake.
        var items = SlashCommands.CompletionsWithoutExit;

        Assert.Equal(SlashCommands.Completions.Count - 1, items.Count);
        Assert.Equal(SlashCommands.Completions.Where(i => i.Text != "/exit"), items);
        Assert.DoesNotContain(items, i => i.Text == "/exit");
        Assert.Contains(SlashCommands.Completions, i => i.Text == "/exit");
        Assert.Equal(SlashCommand.Exit, SlashCommands.Parse("/exit").Command);   // typed in full it still exits
    }

    /// <summary>
    /// Every word that was a command once and went, with and without an argument: unknown, never
    /// silently some other command. One fact over the list rather than a theory row each
    /// (2026-09-24): the per-word history lives in git, the list here only has to grow.
    /// </summary>
    [Fact]
    public void Parse_RetiredWords_AreUnknown()
    {
        string[] retired =
        [
            // The tool switches, later on 2026-09-18: the settings rows are the one switch.
            "/ask", "/ASK on", "/files", "/files off", "/web", "/web on",
            // Every alias but //, 2026-09-16; /// and //// came and went on 2026-09-21.
            "/?", "/cls", "/cls everything", "/comp keep it", "/srv", "/mod", "/reason high", "/int on", "/rem x", "/mem", "/mem 2",
            "/use", "/use reset", "/prof", "/tim 5m", "/cd ~", "/dir", "/ls docs", "/ex", "/cp all", "/win", "/win 80x24", "/ab", "/ab x",
            "/quit", "///", "////", "/config", "/config x",
            // Renamed or folded into another command's words.
            "/sysprompt tools", "/windowsize", "/session", "/forget", "/forget everything", "/memcopy", "/memcopy work",
            "/skill", "/skill now", "/git", "/git user", "/git user force",   // /gituser since 2026-09-26
            "/perf", "/perf gauge",   // /perfbar since 2026-10-04
            "/thumbs", "/thumbs docs",   // /view <path> --thumbs since later on 2026-10-04
        ];

        Assert.All(retired, line => Assert.Equal(SlashCommand.Unknown, SlashCommands.Parse(line).Command));
    }

    [Theory]
    [InlineData("what does /clear do?")]
    [InlineData("quit")]
    [InlineData("")]
    [InlineData("  hello  ")]
    public void Parse_NotStartingWithSlash_IsAMessage(string line)
    {
        Assert.Equal(SlashCommand.None, SlashCommands.Parse(line).Command);
    }

    /// <summary>
    /// An entry's lines of <see cref="SlashCommands.HelpText"/> built the way the text builds them — through the measured widths,
    /// never a literal run of spaces: the label, the description, the first form, and each further form under it (2026-10-05).
    /// </summary>
    private static string Row(string label, string summary, params string[] forms)
    {
        string head = "  " + label.PadRight(SlashCommands.LabelWidth + SlashCommands.HelpColumnGap);
        if (forms.Length == 0)
        {
            return head + summary + "\n";
        }

        string under = new(' ', head.Length + SlashCommands.DescriptionWidth + SlashCommands.HelpColumnGap);
        return head + summary.PadRight(SlashCommands.DescriptionWidth + SlashCommands.HelpColumnGap) + forms[0] + "\n" + string.Concat(forms.Skip(1).Select(f => under + f + "\n"));
    }

    private static string Row(SlashCommands.HelpEntry entry) => Row(entry.Label, entry.Summary, [.. Help.HelpSyntax.Forms(entry.Command)]);

    [Fact]
    public void HelpText_NamesEveryCommandAndTheKeys()
    {
        foreach (var word in SlashCommands.Words)
        {
            Assert.Contains(word, SlashCommands.HelpText);
        }

        Assert.Contains("ESC", SlashCommands.HelpText);
        Assert.DoesNotContain("Ctrl+Q", SlashCommands.HelpText);
        Assert.DoesNotContain("(also", SlashCommands.HelpText);
        string Summary(string command) => SlashCommands.HelpEntries.Single(e => e.Command == command).Summary;
        Assert.StartsWith("Commands:\n" + Row("/about", Summary("/about")) + Row(SlashCommands.HelpEntries.Single(e => e.Command == "/botchat")), SlashCommands.HelpText);   // A to Z since 2026-09-27 (the user's call); /settings led the grouped list until then
        Assert.Contains(Row("/settings, //", Summary("/settings"), "/settings", "/settings <words>", "/settings changed"), SlashCommands.HelpText);
        Assert.Contains(
            Row("/profile", Summary("/profile"), "/profile [<name>]", "/profile add|delete <name>", "/profile rename <name> <new-name>", "/profile reset [<name>] [--all]", "/profile push|pull <name>", "/profile edit|reload"),
            SlashCommands.HelpText);   // edit and reload 2026-09-21, push and pull 2026-09-28; one form a line since 2026-10-05
        Assert.Contains(Row("/clear", Summary("/clear")), SlashCommands.HelpText);   // a bare command: no forms column
        Assert.Contains(Row("/timer", Summary("/timer"), "/timer [<duration> [<name>]]", "/timer stop <name>|all"), SlashCommands.HelpText);
        Assert.All(SlashCommands.HelpEntries, e => Assert.Contains(Row(e), SlashCommands.HelpText));
        Assert.DoesNotContain("M5", SlashCommands.HelpText);
        Assert.EndsWith("push-to-talk key (F4 unless changed) = talk", SlashCommands.HelpText);

        // No blank lines anywhere: one list since 2026-09-27 (a blank line ahead of every group but the first until then).
        Assert.DoesNotContain("\n\n", SlashCommands.HelpText);
    }

    [Fact]
    public void HelpEntry_Label_IsTheCommandAndItsAliases()
    {
        Assert.Equal("/tree, /dir, /ls", new SlashCommands.HelpEntry("/tree", "x", "/dir", "/ls").Label);   // the record still joins them; the table lists none but //
        Assert.Equal("/settings, //", new SlashCommands.HelpEntry("/settings", "x", "//").Label);
        Assert.Equal("/tools", SlashCommands.HelpEntries.Single(e => e.Command == "/tools").Label);     // "/tools, ///" for part of 2026-09-21
        Assert.Equal("/skills", SlashCommands.HelpEntries.Single(e => e.Command == "/skills").Label);   // "/skills, ////" the same day
        Assert.Equal("/usage", new SlashCommands.HelpEntry("/usage", "x").Label);
    }

    [Fact]
    public void BasicCommands_AreRealCommands_AndTheTabTitlesArePinned()
    {
        // The basic tab's list (later on 2026-09-27, the user's): a rename or a typo would drop a row silently.
        Assert.Equal(31, SlashCommands.BasicCommands.Count);   // /rename 2026-10-05   // /terminal later on 2026-10-03; /about, /explore, /perfbar, /stt, /toolbar, /tts and /wake 2026-10-03; /rewind 2026-09-30, the user's picks
        Assert.All(SlashCommands.BasicCommands, c => Assert.Contains(SlashCommands.HelpEntries, e => e.Command == c));
        Assert.Equal("Basic", SlashCommands.BasicTabTitle);
        Assert.Equal("Advanced", SlashCommands.AdvancedTabTitle);
    }

    [Fact]
    public void HelpEntries_AreTheCommandLines_AToZ()
    {
        // One list, A to Z by the command (ordinal), no groups (2026-09-27, the user's call: the Commands tab had grown
        // cramped; nine hand-ordered groups until then — their history is in git).
        Assert.Equal(72, SlashCommands.HelpEntries.Count);   // /rename later on 2026-10-05   // /process 2026-10-05   // /thumbs later still on 2026-10-04, folded into /view --thumbs after it   // /keycheck later on 2026-10-04   // /screen 2026-10-04   // /terminal later on 2026-10-03   // /pdf 2026-10-03   // /log in every run later on 2026-10-02   // /camera and /docker 2026-10-02   // /header later still on 2026-10-01   // /emptytrash went 2026-10-01   // /toolbar later on 2026-09-30   // /rewind 2026-09-30   // /keycopy, then /sampling, then /test, then /ha, then /print, since 2026-09-28; /perfbar later on 2026-09-29
        Assert.Equal(
        [
            "/about", "/botchat", "/camera", "/claude", "/clear", "/cmdclear", "/cmdcopy", "/cmdlist", "/collapse", "/comfy", "/compact", "/copy", "/cwd",
            "/docker", "/draft", "/echo", "/exit", "/expand", "/explore", "/gituser", "/ha", "/header", "/help", "/imagine", "/interrupt", "/keycheck", "/keycopy", "/learn", "/log",
            "/loop", "/mcp", "/memory", "/model", "/new", "/operata", "/pdf", "/perfbar", "/persona", "/plan", "/police", "/print", "/process", "/profile", "/queue", "/reasoning",
            "/remember", "/rename", "/rewind", "/sampling", "/screen", "/server", "/sessions", "/settings", "/skills", "/speak", "/splash", "/stt", "/sys", "/terminal", "/test", "/theme", "/timer", "/toolbar", "/tools",
            "/tree", "/tts", "/usage", "/vault", "/view", "/vocalia", "/wake", "/window",
        ], SlashCommands.HelpEntries.Select(e => e.Command));
        Assert.Equal(SlashCommands.HelpEntries.Select(e => e.Command).OrderBy(c => c, StringComparer.Ordinal), SlashCommands.HelpEntries.Select(e => e.Command));

        // Each description says what the command covers in a few words (2026-10-05, the user's ask: the summaries had grown
        // too tight with every form folded in): lowercase first, no full stop, no command word or form in it, 40 cells at most.
        // The forms are HelpCommands' (HelpSyntaxTests holds them to the notation).
        Assert.All(SlashCommands.HelpEntries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Summary), e.Command);
            Assert.True(char.IsLower(e.Summary[0]), e.Command + ": " + e.Summary);
            Assert.False(e.Summary.EndsWith('.'), e.Command + ": " + e.Summary);
            Assert.DoesNotContain("/", e.Summary);
            Assert.True(e.Summary.Length <= 40, e.Command + ": " + e.Summary);
        });
        Assert.Equal(SlashCommands.HelpEntries.Max(e => e.Summary.Length), SlashCommands.DescriptionWidth);

        // Every word is one entry's command or one of its aliases — never in a summary.
        var labels = SlashCommands.HelpEntries.SelectMany(e => e.Aliases.Prepend(e.Command)).ToList();
        Assert.Equal(labels, labels.Distinct());
        Assert.Equal(SlashCommands.Words.OrderBy(w => w), labels.OrderBy(w => w));
        Assert.All(SlashCommands.HelpEntries, e => Assert.DoesNotContain("(also", e.Summary));
        Assert.All(SlashCommands.HelpEntries, e => Assert.Equal(SlashCommands.Parse(e.Command).Command, SlashCommands.Parse(e.Label.Split(", ")[^1]).Command));

        // The column is measured, not written down: the widest label today.
        Assert.Equal(SlashCommands.HelpEntries.Max(e => e.Label.Length), SlashCommands.LabelWidth);
        Assert.Equal("/settings, //".Length, SlashCommands.LabelWidth);   // 13 since every other alias went (2026-09-16); 22 while it was /settings, /config, //; "/skills, ////" (part of 2026-09-21) was 13 too

        // The plain text is the heading, each entry's lines in the same order (its forms stacked under the first), and the key
        // line — no blank lines.
        Assert.Equal("Commands:\n" + string.Concat(SlashCommands.HelpEntries.Select(Row)) + SlashCommands.KeysLine, SlashCommands.HelpText);
    }

    /// <summary>The words the hint row and the toolbar send through the line hooks (2026-09-18, 2026-09-21): each parses to its command, so the pane opens as the typed command's does.</summary>
    [Fact]
    public void HookWords_ArePinned_AndParseToTheirCommands()
    {
        Assert.Equal("/queue", SlashCommands.QueueWord);
        Assert.Equal("/usage", SlashCommands.UsageWord);
        Assert.Equal("/settings", SlashCommands.SettingsWord);
        Assert.Equal("/skills", SlashCommands.SkillsWord);
        Assert.Equal("/tools", SlashCommands.ToolsWord);
        Assert.Equal("/mcp", SlashCommands.McpWord);
        Assert.Equal("/sys", SlashCommands.SysWord);
        Assert.Equal((SlashCommand.Queue, ""), SlashCommands.Parse(SlashCommands.QueueWord));
        Assert.Equal((SlashCommand.Usage, ""), SlashCommands.Parse(SlashCommands.UsageWord));
        Assert.Equal((SlashCommand.Settings, ""), SlashCommands.Parse(SlashCommands.SettingsWord));
        Assert.Equal((SlashCommand.Skills, ""), SlashCommands.Parse(SlashCommands.SkillsWord));
        Assert.Equal((SlashCommand.Tools, ""), SlashCommands.Parse(SlashCommands.ToolsWord));
        Assert.Equal((SlashCommand.Mcp, ""), SlashCommands.Parse(SlashCommands.McpWord));
        Assert.Equal((SlashCommand.Sys, ""), SlashCommands.Parse(SlashCommands.SysWord));
    }

    /// <summary><c>/rewind</c> (2026-09-30): a command with an optional count, on the basic tab, and it stops a running reply.</summary>
    [Fact]
    public void Rewind_ParsesWithItsCount_AndStopsTheReply()
    {
        Assert.Equal((SlashCommand.Rewind, ""), SlashCommands.Parse("/rewind"));
        Assert.Equal((SlashCommand.Rewind, "2"), SlashCommands.Parse(" /REWIND 2 "));
        Assert.Contains("/rewind", SlashCommands.Words);
        Assert.True(SlashCommands.IsBasic(SlashCommands.HelpEntries.Single(e => e.Command == "/rewind")));
        Assert.Equal(MidTurnClass.Cancel, ChatScreen.MidTurnPolicy(SlashCommand.Rewind, ""));
        Assert.Equal(MidTurnClass.Cancel, ChatScreen.MidTurnPolicy(SlashCommand.Rewind, "3"));
    }
}
