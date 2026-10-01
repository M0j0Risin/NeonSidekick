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
    [InlineData("/perf", SlashCommand.Perf)]
    [InlineData("/tb", SlashCommand.Tb)]   // later on 2026-09-30
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
    public void ExpandAndCollapse_TakeNothing_NorDoesTools()
    {
        // Later on 2026-09-22 (the user's ask): /tools expand | collapse became the root /expand and /collapse.
        Assert.Equal((SlashCommand.Overloaded, "now"), SlashCommands.Parse("/expand now"));
        Assert.Equal((SlashCommand.Overloaded, "all"), SlashCommands.Parse("/collapse all"));
        Assert.Equal((SlashCommand.Overloaded, "expand"), SlashCommands.Parse("/tools expand"));
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
    [InlineData("/settings x", "x")]
    [InlineData("// x", "x")]
    [InlineData("/emptytrash now", "now")]
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
            SlashCommand.Cwd, SlashCommand.Tree, SlashCommand.Vault, SlashCommand.Explore, SlashCommand.Copy, SlashCommand.Session, SlashCommand.GitUser,   // /vault [path] 2026-09-23
            SlashCommand.Loop, SlashCommand.Plan, SlashCommand.BotChat, SlashCommand.Claude, SlashCommand.Queue, SlashCommand.Skills, SlashCommand.Test, SlashCommand.HomeAssistant, SlashCommand.Print, SlashCommand.Perf, SlashCommand.Tb, SlashCommand.Rewind,   // /tb [on|off] later on 2026-09-30; /rewind [n] 2026-09-30; /perf later on 2026-09-29; /print 2026-09-28; /ha 2026-09-28; /test 2026-09-28; /claude 2026-09-27; /skills add 2026-09-26; /plan 2026-09-26; /botchat 2026-09-24; 2026-09-21 (/queue clear later that day; /skills with edit <name> from then until 2026-09-23); /tools off the list later on 2026-09-22, its expand and collapse root words
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
        Assert.Equal(["/sampling", "/server", "/sessions", "/settings", "/skills", "/speak", "/splash", "/stt", "/sys"], items.Where(i => i.Text.StartsWith("/s", StringComparison.Ordinal)).Select(i => i.Text));
        Assert.Equal(["/tb", "/test", "/theme", "/timer", "/tools", "/tree", "/tts"], items.Where(i => i.Text.StartsWith("/t", StringComparison.Ordinal)).Select(i => i.Text));   // /tools among them since 2026-09-19
    }

    [Fact]
    public void Police_IsABareCommand_TheOfficersWord()
    {
        // /police (2026-09-22): the Shell police outside paths page; no argument, so one given is the overloaded error.
        Assert.Equal((SlashCommand.Police, ""), SlashCommands.Parse("/police"));
        Assert.Equal((SlashCommand.Overloaded, "off"), SlashCommands.Parse("/police off"));
        Assert.False(SlashCommands.TakesArgument(SlashCommand.Police));
        Assert.Equal("/police", SlashCommands.PoliceWord);
        Assert.Equal("switch shell police on or off", SlashCommands.HelpEntries.Single(e => e.Command == "/police").Summary);   // the shorter wording since 2026-09-26
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
    public void Log_IsACommand_OnlyUnderTheFlag()
    {
        // /log (2026-09-22, the user's ask): only with --log; without it the word, argument or not, is unknown.
        Assert.Equal((SlashCommand.Unknown, ""), SlashCommands.Parse("/log"));
        Assert.Equal(SlashCommand.Unknown, SlashCommands.Parse("/log now").Command);
        Assert.Equal((SlashCommand.Log, ""), SlashCommands.Parse("/log", log: true));
        Assert.Equal((SlashCommand.Log, ""), SlashCommands.Parse("  /LOG  ", log: true));
        Assert.Equal((SlashCommand.Overloaded, "now"), SlashCommands.Parse("/log now", log: true));
        Assert.False(SlashCommands.TakesArgument(SlashCommand.Log));
        Assert.Equal(SlashCommand.Help, SlashCommands.Parse("/help", log: true).Command);   // the flag changes nothing else
    }

    [Fact]
    public void HelpWithLog_HasTheLogRow_InItsSortedPlace_AndNothingElseChanges()
    {
        Assert.Equal("/log", SlashCommands.LogEntry.Command);
        Assert.Equal("open the diagnostic log file (--log) in your editor", SlashCommands.LogEntry.Summary);
        Assert.Equal(SlashCommands.HelpEntries.Count + 1, SlashCommands.HelpEntriesWithLog.Count);
        Assert.Equal(SlashCommands.HelpEntries, SlashCommands.HelpEntriesWithLog.Where(e => e.Command != "/log"));
        Assert.Equal(SlashCommands.HelpEntriesWithLog.Select(e => e.Command).OrderBy(c => c, StringComparer.Ordinal), SlashCommands.HelpEntriesWithLog.Select(e => e.Command));
        int log = SlashCommands.HelpEntriesWithLog.ToList().IndexOf(SlashCommands.LogEntry);
        Assert.Equal("/learn", SlashCommands.HelpEntriesWithLog[log - 1].Command);   // A to Z since 2026-09-27 (directly above /help until then)
        Assert.Equal("/loop", SlashCommands.HelpEntriesWithLog[log + 1].Command);

        Assert.Same(SlashCommands.HelpEntries, SlashCommands.HelpEntriesFor(false));
        Assert.Same(SlashCommands.HelpEntriesWithLog, SlashCommands.HelpEntriesFor(true));
        Assert.DoesNotContain(SlashCommands.HelpEntries, e => e.Command == "/log");
        Assert.DoesNotContain("/log", SlashCommands.Words);
        Assert.DoesNotContain("/log ", SlashCommands.HelpText);
        Assert.Contains(Row("/learn", SlashCommands.HelpEntries.Single(e => e.Command == "/learn").Summary) + Row("/log", SlashCommands.LogEntry.Summary) + Row("/loop", SlashCommands.HelpEntries.Single(e => e.Command == "/loop").Summary), SlashCommands.HelpTextWithLog);
        Assert.Equal(SlashCommands.HelpText.Length + Row("/log", SlashCommands.LogEntry.Summary).Length, SlashCommands.HelpTextWithLog.Length);
    }

    [Fact]
    public void CompletionsWithLog_AreCompletionsAndLog_Sorted()
    {
        var items = SlashCommands.CompletionsWithLog;

        Assert.Equal(SlashCommands.Completions.Count + 1, items.Count);
        Assert.Equal(items.Select(i => i.Text).OrderBy(t => t, StringComparer.Ordinal), items.Select(i => i.Text));
        Assert.Equal(SlashCommands.Completions, items.Where(i => i.Text != "/log"));
        Assert.Contains(new NeonSidekick.UI.CompletionItem("/log", SlashCommands.LogEntry.Summary), items);
        Assert.Equal(items.Where(i => i.Text != "/exit"), SlashCommands.CompletionsWithoutExitWithLog);
        Assert.DoesNotContain(SlashCommands.Completions, i => i.Text == "/log");
        Assert.DoesNotContain(SlashCommands.CompletionsWithoutExit, i => i.Text == "/log");
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

    /// <summary>A line of <see cref="SlashCommands.HelpText"/> built the way the text builds it — through the measured width, never a literal run of spaces.</summary>
    private static string Row(string label, string summary) =>
        "  " + label.PadRight(SlashCommands.LabelWidth + SlashCommands.HelpColumnGap) + summary + "\n";

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
        Assert.StartsWith("Commands:\n" + Row("/about", "show general information about the app and profile") + Row("/botchat", SlashCommands.HelpEntries.Single(e => e.Command == "/botchat").Summary), SlashCommands.HelpText);   // A to Z since 2026-09-27 (the user's call); /settings led the grouped list until then
        Assert.Contains(Row("/gituser", "write the GitLib email and GitLib name into the working directory's repository") + Row("/ha", SlashCommands.HelpEntries.Single(e => e.Command == "/ha").Summary) + Row("/help", "show help") + Row("/imagine", SlashCommands.HelpEntries.Single(e => e.Command == "/imagine").Summary), SlashCommands.HelpText);   // neighbours by the alphabet since 2026-09-27
        Assert.Contains(Row("/settings, //", "edit and save settings"), SlashCommands.HelpText);
        Assert.Contains(Row("/profile", "switch profiles, or /profile <name> | add <name> | delete <name> | rename <name> <new-name> | reset [name] | push <name> | pull <name> | edit | reload"), SlashCommands.HelpText);   // edit and reload 2026-09-21, push and pull 2026-09-28
        Assert.Contains(Row("/exit", "exit/quit the application"), SlashCommands.HelpText);
        Assert.Contains(Row("/server", "pick an LLM server found on the usual ports, or /server <url>"), SlashCommands.HelpText);
        Assert.Contains(Row("/model", "pick a model from the LLM server, or /model <id>"), SlashCommands.HelpText);
        Assert.Contains(Row("/reasoning", "pick the LLM reasoning effort, or /reasoning <level>"), SlashCommands.HelpText);
        Assert.Contains(Row("/sys", "show the system prompt and tools sent to the model"), SlashCommands.HelpText);
        Assert.Contains(Row("/usage", "show token usage and performance statistics"), SlashCommands.HelpText);
        Assert.Contains(Row("/compact", "shrink the current context, or /compact <focus> to steer the summary"), SlashCommands.HelpText);
        Assert.Contains(Row("/clear", "start a new conversation and clear the screen"), SlashCommands.HelpText);
        Assert.Contains(Row("/new", "start a new conversation but do not clear the screen"), SlashCommands.HelpText);
        Assert.Contains(Row("/splash", "start a new conversation, clear and show the splash screen"), SlashCommands.HelpText);   // 2026-09-19; this wording since 2026-09-26
        Assert.Contains(Row("/copy", "copy the last reply to the clipboard as markdown, or /copy <n> | all; --thinking for the model's thinking too"), SlashCommands.HelpText);
        Assert.Contains(Row("/tts", "toggle speech output, or /tts on|off"), SlashCommands.HelpText);
        Assert.Contains(Row("/stt", "toggle speech input, or /stt on|off"), SlashCommands.HelpText);
        Assert.Contains(Row("/wake", "toggle the speech input wake word, or /wake on|off"), SlashCommands.HelpText);
        Assert.Contains(Row("/interrupt", "toggle the speech input wake word interrupt, or /interrupt on|off"), SlashCommands.HelpText);
        Assert.Contains(Row("/remember", "add a memory: /remember <text>"), SlashCommands.HelpText);
        Assert.Contains(Row("/memory", "list and prune memory items, or /memory forget | edit | copy <profile> [overwrite]"), SlashCommands.HelpText);   // the forget word folded in 2026-09-22 and /forget's row went, the copy word later that day and /memcopy's row with it
        Assert.Contains(Row("/cmdcopy", "copy this profile's allowed shell commands into another, or with --history its command history: /cmdcopy <profile> [--history] [overwrite]"), SlashCommands.HelpText);   // 2026-09-21
        Assert.Contains(Row("/cwd", "show or change the working directory, or /cwd <path> | ~ | browse"), SlashCommands.HelpText);
        Assert.Contains(Row("/tree", "print a tree of the working directory's folders and files, or /tree <path>"), SlashCommands.HelpText);
        Assert.Contains(Row("/explore", "open the working directory in your file browser, or /explore <path>"), SlashCommands.HelpText);
        Assert.Contains(Row("/emptytrash", "empty the working directory's .trash for good (asks first)"), SlashCommands.HelpText);
        Assert.Contains(Row("/gituser", "write the GitLib email and GitLib name into the working directory's repository"), SlashCommands.HelpText);   // 2026-09-21 (/git until 2026-09-26)
        Assert.Contains(Row("/timer", "list timers, or /timer <duration> [name] (10m, 90s, 1h30m) | stop <name> | stop all"), SlashCommands.HelpText);
        Assert.Contains(Row("/window", "show the terminal window's width and height"), SlashCommands.HelpText);
        Assert.Contains(Row("/persona", "export and manage persona.md (the personality) in your editor, or /persona reset to go back to the default, or /persona copy <profile> [force] to copy it into another profile"), SlashCommands.HelpText);   // copy 2026-09-21
        Assert.Contains(Row("/operata", "export and manage operata.md (the operating rules) in your editor, or /operata reset to go back to the default, or /operata copy <profile> [force] to copy it into another profile"), SlashCommands.HelpText);
        Assert.Contains(Row("/vocalia", "export and manage vocalia.md (the spoken-reply directive) in your editor, or /vocalia reset to go back to the default, or /vocalia copy <profile> [force] to copy it into another profile"), SlashCommands.HelpText);
        Assert.Contains(Row("/about", "show general information about the app and profile"), SlashCommands.HelpText);
        Assert.DoesNotContain("M5", SlashCommands.HelpText);
        Assert.EndsWith("F4 = talk (push-to-talk key)", SlashCommands.HelpText);

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
        Assert.Equal(22, SlashCommands.BasicCommands.Count);   // /rewind 2026-09-30, the user's pick
        Assert.All(SlashCommands.BasicCommands, c => Assert.Contains(SlashCommands.HelpEntries, e => e.Command == c));
        Assert.False(SlashCommands.IsBasic(SlashCommands.LogEntry));
        Assert.Equal("Commands (basic)", SlashCommands.BasicTabTitle);
        Assert.Equal("Commands (advanced)", SlashCommands.AdvancedTabTitle);
    }

    [Fact]
    public void HelpEntries_AreTheCommandLines_AToZ()
    {
        // One list, A to Z by the command (ordinal), no groups (2026-09-27, the user's call: the Commands tab had grown
        // cramped; nine hand-ordered groups until then — their history is in git).
        Assert.Equal(63, SlashCommands.HelpEntries.Count);   // /tb later on 2026-09-30   // /rewind 2026-09-30   // /keycopy, then /sampling, then /test, then /ha, then /print, since 2026-09-28; /perf later on 2026-09-29
        Assert.Equal(
        [
            "/about", "/botchat", "/claude", "/clear", "/cmdclear", "/cmdcopy", "/cmdlist", "/collapse", "/comfy", "/compact", "/copy", "/cwd",
            "/draft", "/echo", "/emptytrash", "/exit", "/expand", "/explore", "/gituser", "/ha", "/help", "/imagine", "/interrupt", "/keycopy", "/learn",
            "/loop", "/mcp", "/memory", "/model", "/new", "/operata", "/perf", "/persona", "/plan", "/police", "/print", "/profile", "/queue", "/reasoning",
            "/remember", "/rewind", "/sampling", "/server", "/sessions", "/settings", "/skills", "/speak", "/splash", "/stt", "/sys", "/tb", "/test", "/theme", "/timer", "/tools",
            "/tree", "/tts", "/usage", "/vault", "/view", "/vocalia", "/wake", "/window",
        ], SlashCommands.HelpEntries.Select(e => e.Command));
        Assert.Equal(SlashCommands.HelpEntries.Select(e => e.Command).OrderBy(c => c, StringComparer.Ordinal), SlashCommands.HelpEntries.Select(e => e.Command));

        string Summary(string command) => SlashCommands.HelpEntries.Single(e => e.Command == command).Summary;
        Assert.Equal("list, restore, rename and purge sessions: /sessions [<id> | purge <id> | purge older <age> | purge all | title [<text>]]", Summary("/sessions"));
        Assert.Equal("switch the model's tools on or off and edit the Options, Ask, Files and Web settings on a pane", Summary("/tools"));   // expand | collapse came and went on 2026-09-22 (the root /expand and /collapse now)
        Assert.Equal("connect external MCP servers and switch their tools on or off on a pane", Summary("/mcp"));
        Assert.Equal("list the skills (Enter on one moves, renames, edits or deletes it), edit the skill settings and the project file on a pane; /skills add <search words | owner/repo[/skill] | url> installs one from skills.sh or GitHub", Summary("/skills"));   // add 2026-09-26; edit 2026-09-21, the scope page's edit row in its place 2026-09-23
        Assert.Equal("write or improve a skill from the last turn or the stored sessions, in the background: /learn [what to keep] | sessions [N | what to search]", Summary("/learn"));   // the sessions form 2026-09-19
        Assert.Equal("start a new conversation, clear and show the splash screen", Summary("/splash"));   // this wording since 2026-09-26
        Assert.Equal("switch the colour theme, starting a new conversation with the splash screen, or /theme <name>", Summary("/theme"));
        Assert.Equal("list and prune the messages queued while a reply runs, or /queue clear", Summary("/queue"));   // the clear word since 2026-09-21
        Assert.Equal("copy the last reply to the clipboard as markdown, or /copy <n> | all; --thinking for the model's thinking too", Summary("/copy"));
        Assert.Equal("write the next message in your editor: a temporary file, sent when it is saved and closed", Summary("/draft"));
        Assert.Equal("repeat a message, each reply waited for: /loop <count> [delay] <message> | infinite [delay] <message> (ESC ends it)", Summary("/loop"));
        Assert.Equal("plan before doing: /plan <requirement> — read-only research and questions until you approve the plan (saved under .neon/plans/); then /plan approve [--fresh] | cancel | show | save [name]; /plan open [name] picks one up", Summary("/plan"));
        Assert.Equal("expand all items in the transcript", Summary("/expand"));   // the shorter wording since 2026-09-26
        Assert.Equal("collapse all items in the transcript", Summary("/collapse"));
        Assert.Equal("list and prune memory items, or /memory forget | edit | copy <profile> [overwrite]", Summary("/memory"));   // the forget word folded in 2026-09-22, the copy one later that day
        Assert.Equal("add a memory: /remember <text>", Summary("/remember"));
        Assert.Equal("copy this profile's allowed shell commands into another, or with --history its command history: /cmdcopy <profile> [--history] [overwrite]", Summary("/cmdcopy"));
        Assert.Equal("clear this profile's command history (the Up/Down recall), stored and in memory (asks first)", Summary("/cmdclear"));
        Assert.Equal("list this profile's allowed shell commands on a pane, Enter removes one", Summary("/cmdlist"));
        Assert.Equal("print a tree of the Obsidian vault's folders and notes, or /vault <path>", Summary("/vault"));   // the path since 2026-09-23
        Assert.Equal("read a text file from the working directory aloud, as a reply: /speak <file> [n], or /speak to resume, or /speak <n> from sentence n", Summary("/speak"));
        Assert.Equal("print a line as a reply and read it aloud when speech is on: /echo <text>", Summary("/echo"));
        Assert.Equal("open an image, or a folder of images, from the working directory in the picture viewer; --chat draws the image in the transcript instead: /view <image or folder> [--chat]", Summary("/view"));
        Assert.Equal("show the terminal window's width and height", Summary("/window"));
        Assert.Equal("write the GitLib email and GitLib name into the working directory's repository", Summary("/gituser"));   // /git until 2026-09-26
        Assert.Equal("let the profiles talk to each other, each in its own persona, until ESC: /botchat [profile ...] [[--] topic], or /botchat --resume [line] to carry on the last one, or /botchat --kill to stop the extra embedded servers", Summary("/botchat"));   // --resume 2026-09-25, --kill later on 2026-09-29
        Assert.Equal("list timers, or /timer <duration> [name] (10m, 90s, 1h30m) | stop <name> | stop all", Summary("/timer"));
        Assert.Equal("send a message to Claude Code and add its reply to the conversation: /claude <message>, or /claude new to start a new Claude conversation", Summary("/claude"));   // 2026-09-27
        Assert.Equal("run LLM benchmark tests against the connected model and save the results: /test <id | reasoning | structured | long | all> | history, or /test to list them", Summary("/test"));   // 2026-09-28

        // Every word is one entry's command or one of its aliases — never in a summary.
        var labels = SlashCommands.HelpEntries.SelectMany(e => e.Aliases.Prepend(e.Command)).ToList();
        Assert.Equal(labels, labels.Distinct());
        Assert.Equal(SlashCommands.Words.OrderBy(w => w), labels.OrderBy(w => w));
        Assert.All(SlashCommands.HelpEntries, e => Assert.DoesNotContain("(also", e.Summary));
        Assert.All(SlashCommands.HelpEntries, e => Assert.Equal(SlashCommands.Parse(e.Command).Command, SlashCommands.Parse(e.Label.Split(", ")[^1]).Command));

        // The column is measured, not written down: the widest label today.
        Assert.Equal(SlashCommands.HelpEntries.Max(e => e.Label.Length), SlashCommands.LabelWidth);
        Assert.Equal("/settings, //".Length, SlashCommands.LabelWidth);   // 13 since every other alias went (2026-09-16); 22 while it was /settings, /config, //; "/skills, ////" (part of 2026-09-21) was 13 too

        // The plain text is the heading, one line per entry in the same order, and the key line — no blank lines.
        string[] lines = SlashCommands.HelpText.Split('\n');
        Assert.Equal(1 + SlashCommands.HelpEntries.Count + 1, lines.Length);
        Assert.Equal("Commands:", lines[0]);
        Assert.Equal(SlashCommands.KeysLine, lines[^1]);
        for (var i = 0; i < SlashCommands.HelpEntries.Count; i++)
        {
            Assert.Equal(Row(SlashCommands.HelpEntries[i].Label, SlashCommands.HelpEntries[i].Summary).TrimEnd('\n'), lines[i + 1]);
        }
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
