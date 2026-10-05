using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class TranscriptRendererTests : IDisposable
{
    private readonly TestConsole _console = new();
    private readonly TranscriptRenderer _t;

    public TranscriptRendererTests()
    {
        _console.Profile.Width = 80;
        _t = new TranscriptRenderer(_console);
    }

    public void Dispose() => _console.Dispose();

    private string Output => _console.Output;

    [Fact]
    public void Reply_SkipsLeadingBlankLines_ThenStreamsVerbatim()
    {
        _t.BeginAssistant();
        _t.AppendDelta("\n");
        _t.AppendDelta("\n  ");
        _t.AppendDelta("Hel");
        _t.AppendDelta("lo\nthere");
        _t.EndAssistant();

        Assert.Equal(new[] { "● Hello", "there" }, _console.Lines);
        Assert.EndsWith("\n\n", Output);   // ended, then a blank line (TestConsole ends lines with \n)
        Assert.True(_t.AtLineStart);
    }

    [Fact]
    public void Rule_DrawsTheSunsetRuleAtTheConsoleWidth_OnItsOwnLine()
    {
        _t.BeginAssistant();
        _t.AppendDelta("partial");
        _t.Rule();
        _t.Notice("(new conversation)");

        // Streamed text ahead of it is broken first; the rule spans the console (80 here), the notice follows on its own line.
        Assert.Equal(new[] { "● partial", new string('─', 80), "  · (new conversation)" }, _console.Lines);
        Assert.True(_t.AtLineStart);
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(-1, 80)]
    [InlineData(50, 50)]
    [InlineData(120, 120)]
    [InlineData(240, 240)]
    public void RuleWidth_IsTheConsoleWidth_Or80WithoutOne(int consoleWidth, int expected)
    {
        // No cap: the pane's rules span the window, and the banner's and /new's match them.
        Assert.Equal(expected, TranscriptRenderer.RuleWidth(consoleWidth));
        Assert.Equal(80, TranscriptRenderer.DefaultRuleWidth);
    }

    [Fact]
    public void LongDelta_IsNotFolded()
    {
        string token = new('x', 200);
        _t.BeginAssistant();
        _t.AppendDelta(token);
        Assert.Contains(token, Output);
    }

    [Fact]
    public void MarkupInADelta_IsLiteral()
    {
        _t.BeginAssistant();
        _t.AppendDelta("use [bold]never[/] here");
        Assert.Contains("use [bold]never[/] here", Output);
    }

    [Fact]
    public void Images_MidText_BreaksTheLine_ThenTilesSideBySide()
    {
        var thumbnail = new ImageThumbnail(3, 4, Enumerable.Repeat(Color.Red, 12).ToArray());
        _t.BeginAssistant();
        _t.AppendDelta("partial");
        _t.Images([thumbnail, thumbnail]);

        Assert.Equal(new[] { "● partial", "  ▀▀▀  ▀▀▀", "  ▀▀▀  ▀▀▀" }, _console.Lines);   // one cell per pixel across, the half block stacks two down, a gap between
        Assert.True(_t.AtLineStart);
    }

    [Fact]
    public void Picture_BreaksTheLine_AndCentresTheCanvas()
    {
        var thumbnail = new ImageThumbnail(4, 4, Enumerable.Repeat(Color.Red, 16).ToArray());
        _t.BeginAssistant();
        _t.AppendDelta("partial");
        _t.Picture(thumbnail);

        // 80 columns, 4 across: 38 cells on the left (the trailing pad is trimmed by the test console).
        Assert.Equal("● partial", _console.Lines[0]);
        Assert.StartsWith(new string(' ', 38) + "▀▀▀▀", _console.Lines[1]);
        Assert.StartsWith(new string(' ', 38) + "▀▀▀▀", _console.Lines[2]);
        Assert.True(_t.AtLineStart);
    }

    [Fact]
    public void Images_None_WritesNothing()
    {
        _t.BeginAssistant();
        _t.AppendDelta("partial");
        _t.Images([]);

        Assert.Equal(new[] { "● partial" }, _console.Lines);
        Assert.False(_t.AtLineStart);
    }

    [Fact]
    public void Notice_MidText_GetsItsOwnLine()
    {
        _t.BeginAssistant();
        _t.AppendDelta("partial");
        _t.Notice("(cancelled)");
        _t.EndAssistant();

        Assert.Equal(new[] { "● partial", "  · (cancelled)" }, _console.Lines);
    }

    [Fact]
    public void Notice_AfterABareGlyph_ContinuesTheGlyphLine()
    {
        _t.BeginAssistant();
        _t.Notice("(cancelled)");
        _t.EndAssistant();

        Assert.Equal(new[] { "● (cancelled)" }, _console.Lines);
    }

    [Fact]
    public void EndAssistant_WithNothingSaid_SaysNoReply()
    {
        _t.BeginAssistant();
        _t.EndAssistant();
        Assert.Equal(new[] { "● (no reply)" }, _console.Lines);
    }

    [Fact]
    public void EndAssistant_WithoutBegin_WritesNothing_AndBeginIsIdempotent()
    {
        _t.EndAssistant();
        Assert.Equal("", Output);

        _t.BeginAssistant();
        _t.BeginAssistant();
        Assert.Equal("● ", Output);
    }

    [Fact]
    public void User_Notice_Warning_Error_HaveTheirGlyphs()
    {
        _t.User("hi");
        _t.Notice("n");
        _t.Warning("w");
        _t.Error("e");
        Assert.Equal(new[] { "› hi", "  · n", "  ! w", "  ✗ e" }, _console.Lines);
    }

    [Fact]
    public void Tool_TruncatesBeforeEscaping()
    {
        string payload = new string('a', 250) + "[" + new string('b', 100);
        string markup = TranscriptRenderer.ToolResultMarkup("echo", payload);

        Assert.Null(Record.Exception(() => new Markup(markup)));
        Assert.Contains("…", markup);
        Assert.DoesNotContain("[[", markup);   // the bracket fell past the limit

        _t.ToolResult("echo", "line1\nline2");
        _t.Tool("echo", "{\"x\":1}");
        Assert.Equal(new[] { "     → line1 line2", "  🛠️ echo 1" }, _console.Lines);   // the result under its call, the call's values (2026-10-04)
    }

    [Fact]
    public void Truncate_Contract()
    {
        Assert.Equal("abc", TranscriptRenderer.Truncate("abc", 3));
        Assert.Equal("ab…", TranscriptRenderer.Truncate("abcd", 3));
        Assert.Equal("", TranscriptRenderer.Truncate("abcd", 0));
        Assert.Equal(200, TranscriptRenderer.ToolTextLimit);
    }

    [Fact]
    public void Diagnostic_IsColouredByLevel_AndBracketsAreEscaped()
    {
        var evt = new DiagnosticEvent(DateTime.UtcNow, DiagnosticLevel.Warning, "Llm", "slow [x]", null);
        Assert.Equal("[#FFC832]  [[Llm]] slow [[x]][/]", TranscriptRenderer.DiagnosticMarkup(evt));

        _t.BeginAssistant();
        _t.AppendDelta("text");
        _t.Diagnostic(evt);
        Assert.Equal(new[] { "● text", "  [Llm] slow [x]" }, _console.Lines);
    }

    [Fact]
    public void ToolNote_IsOneDimLine_ContinuingABareGlyph()
    {
        _t.BeginAssistant();
        _t.ToolNote("remembered: Their name is Chris.");
        _t.AppendDelta("Nice to meet you.");
        _t.EndAssistant();
        Assert.Equal(new[] { "● 🛠️ remembered: Their name is Chris.", "Nice to meet you." }, _console.Lines);
    }

    /// <summary>A skill tool's line (later on 2026-09-21): the tool note's shape behind the mortarboard, a bare glyph continued the same way.</summary>
    [Fact]
    public void SkillNote_IsOneDimLine_BehindTheSkillGlyph_ContinuingABareGlyph()
    {
        _t.BeginAssistant();
        _t.SkillNote("loaded skill 'haiku' (120 characters)");
        _t.AppendDelta("Here is one.");
        _t.EndAssistant();
        Assert.Equal(new[] { "● 🎓 loaded skill 'haiku' (120 characters)", "Here is one." }, _console.Lines);

        _t.SkillNote("created skill 'rain-haiku' (profile, 200 bytes)");
        Assert.Equal("  🎓 created skill 'rain-haiku' (profile, 200 bytes)", _console.Lines[^1]);
    }

    /// <summary>The police's line (2026-09-22): a shell tool's refusal by the outside-paths police wears the officer, the tool note's shape otherwise.</summary>
    [Fact]
    public void PoliceNote_IsOneDimLine_BehindTheOfficer_ContinuingABareGlyph()
    {
        _t.BeginAssistant();
        _t.PoliceNote("Error: outside the working directory: 'C:\\Windows'");
        _t.AppendDelta("I will stay inside.");
        _t.EndAssistant();
        Assert.Equal(new[] { "● 👮 Error: outside the working directory: 'C:\\Windows'", "I will stay inside." }, _console.Lines);

        _t.PoliceNote("Error: outside the working directory: '~'");
        Assert.Equal("  👮 Error: outside the working directory: '~'", _console.Lines[^1]);
    }

    [Fact]
    public void ToolNotes_IsOneDimLinePerLine_BlankLinesSkipped()
    {
        _t.BeginAssistant();
        _t.ToolNotes("Which colour? — blue\n\nToppings? — cheese, olives\n");
        _t.AppendDelta("Blue it is.");
        _t.EndAssistant();
        Assert.Equal(new[] { "● 🛠️ Which colour? — blue", "  🛠️ Toppings? — cheese, olives", "Blue it is." }, _console.Lines);
    }

    /// <summary>
    /// The notice and error lines read their theme slots (2026-10-04, the UI review: SystemText and ErrorText had been unused, so a
    /// user theme's override did nothing); a colour-only style writes the plain colour form.
    /// </summary>
    [Fact]
    public void NoticeAndError_FollowTheirThemeSlots()
    {
        Assert.Equal(Theme.ColorMarkup(Theme.SystemText.Foreground, "  · n"), TranscriptRenderer.NoticeMarkup("n"));
        Assert.Equal("[" + Theme.ErrorText.ToMarkup() + "]  ✗ e[/]", TranscriptRenderer.ErrorMarkup("e"));
        Assert.Equal("[#112233]x[/]", Theme.StyleMarkup(new Spectre.Console.Style(new Spectre.Console.Color(0x11, 0x22, 0x33)), "x"));
        Assert.Equal("[italic #112233]x [[y]][/]", Theme.StyleMarkup(new Spectre.Console.Style(new Spectre.Console.Color(0x11, 0x22, 0x33), decoration: Spectre.Console.Decoration.Italic), "x [y]"));
    }

    /// <summary>Truncate cuts by cells and between clusters (2026-10-04): never half an emoji, never a base without its selector.</summary>
    [Fact]
    public void Truncate_CountsCells_AndNeverSplitsACluster()
    {
        Assert.Equal("abc", TranscriptRenderer.Truncate("abc", 3));
        Assert.Equal("ab…", TranscriptRenderer.Truncate("abcd", 3));
        Assert.Equal("a b", TranscriptRenderer.Truncate("a\nb", 5));
        Assert.Equal("", TranscriptRenderer.Truncate("abc", 0));
        Assert.Equal("日…", TranscriptRenderer.Truncate("日本語", 4));          // 2 + 2 + 2 cells: one wide character and the ellipsis
        Assert.Equal("a…", TranscriptRenderer.Truncate("a😀b", 3));          // the pair does not fit beside the ellipsis: dropped whole
        Assert.Equal("a😀…", TranscriptRenderer.Truncate("a😀bcd", 4));
        Assert.Equal("x…", TranscriptRenderer.Truncate("x⚙️yz", 3));        // gear and selector are two cells together, cut as one
        Assert.Equal("x⚙️…", TranscriptRenderer.Truncate("x⚙️yz", 4));
    }

    [Fact]
    public void MarkupBuilders_ArePinned()
    {
        Assert.Equal("[#9A8BB8]  · n[/]", TranscriptRenderer.NoticeMarkup("n"));
        Assert.Equal("[#FFC832]  ! w[/]", TranscriptRenderer.WarningMarkup("w"));
        Assert.Equal("[bold #FF4D6D]  ✗ e[/]", TranscriptRenderer.ErrorMarkup("e"));   // Theme.ErrorText, bold, since 2026-10-04 (the slot had been unused)
        // Two spaces after the gear: the terminal advances one cell for U+2699 and the font overdraws the next.
        Assert.Equal("[#9A8BB8]  🛠️ t[/]", TranscriptRenderer.ToolMarkup("t", "{}"));   // the values, not the JSON (2026-10-04)
        Assert.Equal("[#9A8BB8]  🛠️ read_file notes.md · 2[/]", TranscriptRenderer.ToolMarkup("read_file", "{\"path\":\"notes.md\",\"tail\":2,\"opts\":{\"a\":1}}"));
        Assert.Equal("[#9A8BB8]     → r[/]", TranscriptRenderer.ToolResultMarkup("t", "r"));
        Assert.Equal("[#9A8BB8]  🛠️ remembered: [[x]][/]", TranscriptRenderer.ToolNoteMarkup("remembered: [x]"));
        Assert.Equal("[#9A8BB8]  🎓 loaded skill '[[x]]'[/]", TranscriptRenderer.SkillNoteMarkup("loaded skill '[x]'"));   // later on 2026-09-21
        Assert.Equal("[#9A8BB8]  👮 Error: outside the working directory: '[[x]]' — a command or a script may only name paths under it[/]", TranscriptRenderer.PoliceNoteMarkup("Error: outside the working directory: '[x]' — a command or a script may only name paths under it"));   // 2026-09-22
        Assert.Equal(InputLine.SubmittedMarkup("u"), TranscriptRenderer.UserMarkup("u"));
    }

    [Fact]
    public async Task WithSpinner_NonInteractive_RunsTheWork()
    {
        int result = await _t.WithSpinnerAsync("thinking", () => Task.FromResult(42));
        Assert.Equal(42, result);
        Assert.Equal("", Output);
    }

    [Fact]
    public async Task WithSpinner_LabelSetter_ChangesTheLabel_AndIsANoOpWhenNotInteractive()
    {
        int result = await _t.WithSpinnerAsync("start", setLabel => { setLabel("later"); return Task.FromResult(1); });
        Assert.Equal(1, result);
        Assert.Equal("", Output);

        using var console = new TestConsole().Interactive();
        console.Profile.Width = 80;
        var t = new TranscriptRenderer(console);

        result = await t.WithSpinnerAsync("start", async setLabel =>
        {
            await Task.Delay(30);
            setLabel("downloading (3 MB)… 50%");
            await Task.Delay(30);
            return 2;
        });

        Assert.Equal(2, result);
        Assert.Contains("downloading (3 MB)… 50%", console.Output);   // the last frame; TestConsole keeps only that one
    }

    [Fact]
    public async Task WithSpinner_Interactive_ShowsTheLabel_ReturnsTheValue_AndPropagatesExceptions()
    {
        using var console = new TestConsole().Interactive();
        console.Profile.Width = 80;
        var t = new TranscriptRenderer(console);

        int result = await t.WithSpinnerAsync("thinking", () => Task.FromResult(7));

        Assert.Equal(7, result);
        Assert.Contains("thinking", console.Output);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            t.WithSpinnerAsync<int>("x", () => throw new InvalidOperationException("boom")));

        // The transcript is still usable afterwards.
        t.Notice("after");
        Assert.Contains("  · after", console.Output);
    }

    [Fact]
    public async Task WithSpinner_OnThePane_UsesTheHintRow_AndLeavesTheTranscriptAlone()
    {
        using var console = new TestConsole().Interactive();
        console.Profile.Width = 40;
        console.Profile.Height = 8;
        using var pane = new ScreenPane(console, new ScreenGeometry(() => null), new NeonSidekick.Tests.Fakes.ManualTimeProvider()) { Hint = () => "idle" };
        pane.Show();
        var t = new TranscriptRenderer(pane);
        t.BeginAssistant();
        t.AppendDelta("mid");

        int result = await t.WithSpinnerAsync("thinking", setLabel =>
        {
            Assert.EndsWith(Theme.SpinnerFrames[0] + " thinking 00:00", console.Output);
            setLabel("still thinking");
            Assert.EndsWith(Theme.SpinnerFrames[0] + " still thinking 00:00", console.Output);
            return Task.FromResult(7);
        });

        Assert.Equal(7, result);
        Assert.EndsWith("idle", console.Output);
        Assert.False(t.AtLineStart);   // no line break was forced for a Status region
        Assert.Equal(5, pane.FlowColumn);   // "● mid"
    }

    [Fact]
    public void BeginBusy_OnThePane_HoldsTheSpinnerOverTranscriptWrites()
    {
        using var console = new TestConsole().Interactive();
        console.Profile.Width = 40;
        console.Profile.Height = 8;
        var time = new NeonSidekick.Tests.Fakes.ManualTimeProvider();
        using var pane = new ScreenPane(console, new ScreenGeometry(() => null), time) { Hint = () => "idle" };
        pane.Show();
        var t = new TranscriptRenderer(pane);

        using (var busy = t.BeginBusy("thinking"))
        {
            Assert.NotNull(busy);
            Assert.EndsWith(Theme.SpinnerFrames[0] + " thinking 00:00", console.Output);
            t.BeginAssistant();
            t.AppendDelta("streamed");
            t.ToolNote("read a.txt");
            time.Advance(TimeSpan.FromSeconds(3));
            // Every write redrew the pane with the busy row; the count kept running.
            Assert.EndsWith(" thinking 00:03", console.Output);
            Assert.Contains("streamed", console.Output);
            Assert.Contains("read a.txt", console.Output);
            // The turn's stage renames the scope's own label; the count runs on.
            busy.SetLabel("writing");
            Assert.EndsWith(" writing 00:03", console.Output);
        }

        Assert.EndsWith("idle", console.Output);
    }

    [Fact]
    public void BeginBusy_WithoutThePane_IsNull()
    {
        using var console = new TestConsole();
        var plain = new TranscriptRenderer(console);
        Assert.Null(plain.BeginBusy("thinking"));

        using var interactive = new TestConsole().Interactive();
        using var disabled = new ScreenPane(interactive, geometry: null, new NeonSidekick.Tests.Fakes.ManualTimeProvider());
        Assert.Null(new TranscriptRenderer(disabled).BeginBusy("thinking"));
    }

    // ── Trailing whitespace is held ─────────────────────────────────────────

    [Fact]
    public void Reply_HoldsTrailingWhitespace_DroppedByAToolLine()
    {
        _t.BeginAssistant();
        _t.AppendDelta("All moved.");
        _t.AppendDelta("\n");
        _t.AppendDelta("\n");
        _t.AppendDelta("  \n");
        _t.ToolNote("moved a.txt to b\\a.txt");
        _t.ToolNote("moved c.txt to b\\c.txt");
        _t.EndAssistant();

        // No blank row between the sentence and the first 🛠️ line, none between the two.
        Assert.Equal("● All moved.\n  🛠️ moved a.txt to b\\a.txt\n  🛠️ moved c.txt to b\\c.txt\n\n", Output);
    }

    [Fact]
    public void Reply_ParagraphBreak_IsWrittenBeforeTheNextWord()
    {
        _t.BeginAssistant();
        _t.AppendDelta("one");
        _t.AppendDelta("\n\n");
        _t.AppendDelta("two ");
        _t.AppendDelta(" three\n");
        _t.AppendDelta("four");
        _t.EndAssistant();

        Assert.Equal("● one\n\ntwo  three\nfour\n\n", Output);
    }

    [Fact]
    public void Reply_TrailingWhitespace_AtTheEnd_IsDropped()
    {
        _t.BeginAssistant();
        _t.AppendDelta("done");
        _t.AppendDelta("  \n");
        _t.AppendDelta("\n\n");
        _t.EndAssistant();

        Assert.Equal("● done\n\n", Output);   // the line ended once, then the one blank separator
        Assert.True(_t.AtLineStart);
    }

    [Fact]
    public void Reply_TrailingWhitespace_ThenANotice_ContinuesOnItsOwnLine()
    {
        _t.BeginAssistant();
        _t.AppendDelta("cut");
        _t.AppendDelta("\n");
        _t.Notice("(cancelled)");
        _t.EndAssistant();

        Assert.Equal("● cut\n  · (cancelled)\n\n", Output);
    }

    [Fact]
    public void Reply_WhitespaceOnly_IsNoReply()
    {
        _t.BeginAssistant();
        _t.AppendDelta("\n");
        _t.AppendDelta(" ");
        _t.EndAssistant();

        Assert.Equal("● (no reply)\n\n", Output);
    }

    // ── The styled reply (the pane's live slot) ─────────────────────────────

    private sealed class Styled : IDisposable
    {
        public readonly TestConsole Console = new TestConsole().Interactive();
        public readonly ManualTimeProvider Time = new();
        public readonly ScreenPane Pane;
        public readonly TranscriptRenderer T;

        public Styled()
        {
            Console.Profile.Width = 80;
            Console.Profile.Height = 20;
            Pane = new ScreenPane(Console, new ScreenGeometry(() => null, () => null), Time);
            T = new TranscriptRenderer(Pane);
            Pane.Show();
        }

        public string Output => Console.Output;

        /// <summary>The pane under the flow: the padding for <paramref name="flowRows"/> rows of transcript, the rules, the empty input row.</summary>
        public string PaneAfter(int flowRows) =>
            new string('\n', 20 - 4 - flowRows) + new string(ScreenPane.RuleGlyph, 80) + "\n" + InputLine.PromptGlyph + "\n" + new string(ScreenPane.RuleGlyph, 80) + "\n";

        public int Draws => Count(Output, new string(ScreenPane.RuleGlyph, 80)) / 2;

        public void Tick() => Time.Advance(ScreenPane.Tick);

        public void Dispose()
        {
            Pane.Dispose();
            Console.Dispose();
        }
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Each part appears after the one before it (the pane is drawn between flow writes, so the flow is read in pieces).</summary>
    private static void InOrder(string output, params string[] parts)
    {
        int at = 0;
        foreach (string part in parts)
        {
            int found = output.IndexOf(part, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"missing in order: {part.Replace("\n", "\\n")}");
            at = found + part.Length;
        }
    }

    // ── Thinking (2026-09-26) ─────────────────────────────────────────────

    [Fact]
    public void Thinking_StreamsInTheSlot_ThenFolds_AndTheAnswerKeepsTheGlyph()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendThinking("\n", TimeSpan.Zero);   // leading whitespace: nothing yet
        Assert.False(s.T.ThinkingOpen);
        s.T.AppendThinking("Hmm, ", TimeSpan.FromSeconds(1));
        s.T.AppendThinking("the sky.", TimeSpan.FromSeconds(2.5));
        Assert.True(s.T.ThinkingOpen);
        s.Tick();
        Assert.Contains(ThinkingFoldText.LiveHeader + "\n    Hmm, the sky.\n", s.Output);

        s.T.AppendDelta("\n\n");   // the blank lines between: the block stays
        Assert.True(s.T.ThinkingOpen);
        s.T.AppendDelta("It is blue.");
        Assert.False(s.T.ThinkingOpen);
        s.T.EndAssistant();

        InOrder(s.Output, ThinkingFoldText.Summary(TimeSpan.FromSeconds(2.5), expanded: false) + "\n", "● It is blue.\n");
    }

    [Fact]
    public void Thinking_AfterReplyText_CommitsIt_AndTheAnswerAfterIsFlushLeft()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendDelta("Let me check.");
        s.T.AppendThinking("again", TimeSpan.FromSeconds(1));
        s.T.AppendDelta("Done.");
        s.T.EndAssistant();

        InOrder(s.Output, "● Let me check.\n", ThinkingFoldText.Summary(TimeSpan.FromSeconds(1), expanded: false) + "\n", "Done.\n");
        Assert.DoesNotContain("● Done.", s.Output);
    }

    [Fact]
    public void Thinking_ThenAToolLine_FoldsFirst_TheToolLineAfterIt()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendThinking("plan", TimeSpan.FromSeconds(1));
        s.T.Tool("read_file", "{}");
        s.T.EndAssistant();

        InOrder(s.Output, ThinkingFoldText.Summary(TimeSpan.FromSeconds(1), expanded: false) + "\n", "read_file");
    }

    [Fact]
    public void Thinking_WithNothingAfter_EndsAsTheFoldAndNoReply()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendThinking("only this", TimeSpan.FromSeconds(3));
        s.T.EndAssistant();

        InOrder(s.Output, ThinkingFoldText.Summary(TimeSpan.FromSeconds(3), expanded: false) + "\n", "● " + TranscriptRenderer.NoReplyText);
    }

    [Fact]
    public void Thinking_OnThePlainPath_ShowsNothing()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: false);
        s.T.AppendThinking("secret", TimeSpan.FromSeconds(1));
        Assert.False(s.T.ThinkingOpen);
        s.T.AppendDelta("Hi.");
        s.T.EndAssistant();

        Assert.DoesNotContain("secret", s.Output);
        Assert.DoesNotContain(ThinkingFoldText.Glyph, s.Output);
    }

    [Fact]
    public void ThinkingFoldText_Duration_SecondsThenMinutes()
    {
        Assert.Equal("0.0s", ThinkingFoldText.Duration(TimeSpan.Zero));
        Assert.Equal("4.2s", ThinkingFoldText.Duration(TimeSpan.FromSeconds(4.2)));
        Assert.Equal("59.9s", ThinkingFoldText.Duration(TimeSpan.FromSeconds(59.9)));
        Assert.Equal("1m 00s", ThinkingFoldText.Duration(TimeSpan.FromSeconds(59.96)));
        Assert.Equal("1m 15s", ThinkingFoldText.Duration(TimeSpan.FromSeconds(75)));
        Assert.Equal("0.0s", ThinkingFoldText.Duration(TimeSpan.FromSeconds(-1)));
        Assert.Equal("  ▾ 💭 thinking…", ThinkingFoldText.LiveHeader);
        Assert.Equal("  ▾ 💭 thought for 4.2s", ThinkingFoldText.Summary(TimeSpan.FromSeconds(4.2), expanded: true));
    }

    [Fact]
    public void Markdown_BeginAssistant_ShowsTheGlyphOnTheTick()
    {
        using var s = new Styled();
        int draws = s.Draws;

        s.T.BeginAssistant(markdown: true);

        Assert.True(s.T.StyledReply);
        Assert.True(s.Pane.LiveOpen);
        Assert.Equal(draws, s.Draws);
        s.Tick();
        Assert.Equal(1, s.Pane.LiveRows);
        Assert.EndsWith(TranscriptRenderer.AssistantGlyph + "\n" + s.PaneAfter(1), s.Output);
    }

    [Fact]
    public void Markdown_Deltas_AreRepaintedOnTheTick_NotPerToken()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.Tick();
        int draws = s.Draws;
        int mark = s.Output.Length;

        s.T.AppendDelta("Hel");
        s.T.AppendDelta("lo **wor");
        s.T.AppendDelta("ld**");
        Assert.Equal(draws, s.Draws);
        Assert.Equal("", s.Output[mark..]);

        s.Tick();
        Assert.Equal(draws + 1, s.Draws);
        Assert.EndsWith("● Hello world\n" + s.PaneAfter(1), s.Output);
        Assert.DoesNotContain("**", s.Output);
    }

    [Fact]
    public void Markdown_EndAssistant_CommitsTheStyledLines_ThenABlankLine()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendDelta("**Hello** there\n\n- one\n- two");

        s.T.EndAssistant();

        Assert.False(s.Pane.LiveOpen);
        Assert.False(s.T.StyledReply);
        Assert.True(s.T.AtLineStart);
        Assert.Equal(5, s.Pane.FlowRow);
        Assert.EndsWith("● Hello there\n   \n  • one\n  • two\n\n" + s.PaneAfter(5), s.Output);
    }

    [Fact]
    public void Markdown_LeadingBlankLines_AreDropped()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendDelta("\n");
        s.T.AppendDelta("\n  ");
        s.T.AppendDelta("Hi");
        s.T.EndAssistant();

        Assert.EndsWith("● Hi\n\n" + s.PaneAfter(2), s.Output);
    }

    [Fact]
    public void Markdown_AToolLineMidReply_CommitsFirst_TheRestContinuesWithoutTheGlyph()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendDelta("Looking.");
        s.T.Tool("get_time", "{}");
        Assert.False(s.Pane.LiveOpen);
        s.T.AppendDelta("It is **noon**.");
        s.T.EndAssistant();

        InOrder(s.Output, "● Looking.\n", TranscriptRenderer.ToolGlyph + "get_time\n", "It is noon.\n\n" + s.PaneAfter(4));
        Assert.Equal(4, s.Pane.FlowRow);
    }

    [Fact]
    public void Markdown_NothingSaid_IsTheNoReplyLine()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.Tick();

        s.T.EndAssistant();

        Assert.False(s.Pane.LiveOpen);
        Assert.EndsWith("● " + TranscriptRenderer.NoReplyText + "\n\n" + s.PaneAfter(2), s.Output);
    }

    [Fact]
    public void Markdown_ANoticeAfterTheBareGlyph_IsItsOwnLine_TheGlyphKeptForText()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.Tick();

        s.T.Notice("(cancelled)");
        s.T.EndAssistant();

        Assert.False(s.Pane.LiveOpen);
        InOrder(s.Output, "  · (cancelled)\n", "\n" + s.PaneAfter(2));   // the glyph only ever on the reply's text (2026-10-04)
        Assert.Equal(2, s.Pane.FlowRow);
    }

    [Fact]
    public void Markdown_ANoticeMidReply_CommitsTheBlock_AndTakesItsOwnLine()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendDelta("Half");

        s.T.Notice("(cancelled)");
        s.T.EndAssistant();

        InOrder(s.Output, "● Half\n", TranscriptRenderer.NoticeGlyph + "(cancelled)\n", "\n" + s.PaneAfter(3));
        Assert.Equal(3, s.Pane.FlowRow);
    }

    [Fact]
    public void Markdown_ASlotCommittedBehindTheRenderer_StartsAFreshBlockWithoutTheGlyph()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendDelta("One.");
        s.Pane.Write(new Markup("direct" + Environment.NewLine));   // a flow write straight to the pane commits the slot
        Assert.False(s.Pane.LiveOpen);

        s.T.AppendDelta("Two.");
        s.T.EndAssistant();

        InOrder(s.Output, "● One.\ndirect\n", "Two.\n\n" + s.PaneAfter(4));
        Assert.Equal(4, s.Pane.FlowRow);
    }

    [Fact]
    public void Markdown_WithoutThePane_IsThePlainPath()
    {
        _t.BeginAssistant(markdown: true);
        Assert.False(_t.StyledReply);
        _t.AppendDelta("Hel");
        Assert.Equal("● Hel", Output);
        _t.AppendDelta("lo **x**");
        _t.EndAssistant();

        Assert.Equal(new[] { "● Hello **x**" }, _console.Lines);
    }

    [Fact]
    public void Markdown_EndAssistant_Twice_IsSafe()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.T.AppendDelta("x");
        s.T.EndAssistant();
        int mark = s.Output.Length;
        s.T.EndAssistant();

        Assert.Equal("", s.Output[mark..]);
    }

    /// <summary>A failed tool result looks like one (2026-10-04, the UI review): its own ✗ in the warning colour, and the run's summary counts it.</summary>
    [Fact]
    public void AFailedToolResult_IsMarked_AndTheSummaryCountsIt()
    {
        Assert.True(TranscriptRenderer.IsToolFailure("Error: no such file"));
        Assert.True(TranscriptRenderer.IsToolFailure("  Error: x"));
        Assert.False(TranscriptRenderer.IsToolFailure("4 lines"));
        Assert.Equal("[#FFC832]  ✗  → Error: gone[/]", TranscriptRenderer.ToolResultMarkup("read_file", "Error: gone"));
        Assert.Equal("[#9A8BB8]     → 4 lines[/]", TranscriptRenderer.ToolResultMarkup("read_file", "4 lines"));
        Assert.Equal("[#FFC832]  ✗  Error: refused[/]", TranscriptRenderer.ToolNoteMarkup("Error: refused"));
        Assert.Equal(5, TextCells.Width(TranscriptRenderer.ToolFailedGlyph));
        Assert.Equal("  ▸ 🛠️ 3 tool calls · 1 failed — grep ×2, read_file", ToolGroupText.Summary([("grep", 2), ("read_file", 1)], expanded: false, failed: 1));
        Assert.Equal(" · 2 failed", ToolGroupText.FailedNote(2));
    }

    /// <summary>
    /// One left edge for a reply (2026-10-04, the UI review): a turn that calls a tool first draws no glyph over the tool's line; the
    /// reply's first text wears it, and a stretch after a tool line keeps the two cells; one blank row between the blocks.
    /// </summary>
    [Fact]
    public void AReplyAfterAToolLine_TakesTheGlyph_AndEveryStretchKeepsTheLeftEdge()
    {
        using var s = new Styled();
        s.T.BeginAssistant(markdown: true);
        s.Tick();
        s.T.Tool("read_file", "{}");
        s.T.ToolResult("read_file", "4 lines");
        s.T.AppendDelta("Here is the list:\n\n- one\n- two\n\nAnd code:\n\n```python\nprint(1)\n```\n\nDone.");
        s.Tick();
        s.T.Tool("read_file", "{}");
        s.T.AppendDelta("After it.");
        s.T.EndAssistant();

        Assert.DoesNotContain("● " + TranscriptRenderer.ToolGlyph.TrimStart(), s.Output);
        InOrder(s.Output, "read_file", "● Here is the list:\n   \n  • one\n  • two\n   \n  And code:\n   \n  " + NeonSidekick.UI.Markdown.MarkdownView.CodeHeading("python"), "  Done.\n", "  After it.\n");
    }

    /// <summary>A notice or tool line that wraps continues under its text (2026-10-04, the UI review), not at column 0; a span inside draws whole.</summary>
    [Fact]
    public void AWrappedLine_ContinuesUnderItsText()
    {
        var console = new TestConsole();
        console.Profile.Width = 20;
        console.Write(TranscriptRenderer.Hanging(TranscriptRenderer.NoticeMarkup("one two three four five six")));
        Assert.Equal(["  · one two three", "    four five six"], console.Output.TrimEnd().Split('\n').Select(l => l.TrimEnd()));
        Assert.IsType<Markup>(TranscriptRenderer.Hanging("[red]a [bold]b[/][/]"));
        Assert.IsType<Markup>(TranscriptRenderer.Hanging("plain"));
        Assert.IsType<Markup>(TranscriptRenderer.Hanging("[red]  ·[/]"));
    }

    /// <summary>A call's values (2026-10-04, the UI review): plain values in order, at most three; arrays, objects and nulls left out; no JSON, no brief.</summary>
    [Fact]
    public void ToolCallBrief_IsTheCallsValues()
    {
        Assert.Equal("notes.md", ToolCallText.Brief("{\"path\":\"notes.md\"}"));
        Assert.Equal("dir /b · 30 · true", ToolCallText.Brief("{\"command\":\"dir /b\",\"timeout\":30,\"wait\":true,\"extra\":\"x\"}"));
        Assert.Equal("a b", ToolCallText.Brief("{\"text\":\"a\\nb\",\"list\":[1],\"none\":null}"));
        Assert.Equal("", ToolCallText.Brief("{}"));
        Assert.Equal("", ToolCallText.Brief("not json"));
        Assert.Equal("", ToolCallText.Brief("[1,2]"));
        Assert.Equal("(+1 line)", ToolCallText.MoreLines(1));
    }

    /// <summary>A result under its call (2026-10-04): its first line, then the next ones, folding with the run; the call and its result one unit.</summary>
    [Fact]
    public void AToolResult_KeepsItsLines_AndFoldsWithItsRun()
    {
        var view = new ToolResultView("first\nsecond\nthird");
        Assert.Equal(2, view.MoreLines);
        Assert.NotNull(view.Fold);
        Assert.Null(new ToolResultView("only").Fold);
        var console = new TestConsole();
        console.Profile.Width = 40;
        console.Write(view);
        Assert.Equal(["     → first", "       second", "       third"], console.Output.TrimEnd().Split('\n').Select(l => l.TrimEnd()));
        var many = new ToolResultView(string.Join("\n", Enumerable.Range(1, 20).Select(i => "line" + i)));
        Assert.Equal(19, many.MoreLines);
        console = new TestConsole();
        console.Profile.Width = 40;
        console.Write(many);
        Assert.Contains("… 7 more lines", console.Output);
    }

    /// <summary>Tool collapse count counts calls (2026-10-04): a call and its result are one; two calls under a count of two stay, a third folds the run.</summary>
    [Fact]
    public void ToolCollapseCount_CountsCalls_NotLines()
    {
        using (var s = new Styled())
        {
            s.T.ToolCollapseCount = () => 2;
            s.T.BeginAssistant(markdown: true);
            for (int i = 0; i < 2; i++)
            {
                s.T.CountToolCall("read_file");
                s.T.Tool("read_file", "{}");
                s.T.ToolResult("read_file", "4 lines");
            }

            s.T.AppendDelta("Done.");
            s.T.EndAssistant();
            Assert.DoesNotContain(ToolGroupText.CollapsedGlyph + " ", s.Output);
        }

        using (var s = new Styled())
        {
            s.T.ToolCollapseCount = () => 2;
            s.T.BeginAssistant(markdown: true);
            for (int i = 0; i < 3; i++)
            {
                s.T.CountToolCall("read_file");
                s.T.Tool("read_file", "{}");
                s.T.ToolResult("read_file", "4 lines");
            }

            s.T.AppendDelta("Done.");
            s.T.EndAssistant();
            Assert.Contains(ToolGroupText.Summary([("read_file", 3)], expanded: false), s.Output);
        }
    }
}
