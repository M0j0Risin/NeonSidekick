using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The transcript's find (2026-10-07, the user's ask, phase 5 of the UI round): <c>/find</c> and Ctrl+Shift+F over the scrollback.</summary>
public class TranscriptFindTests : IDisposable
{
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly ManualTimeProvider _time = new();

    public TranscriptFindTests()
    {
        _console.Profile.Width = 100;
        _console.Profile.Height = 14;
    }

    public void Dispose() => _console.Dispose();

    private ScreenPane Pane() => new(_console, new ScreenGeometry(() => null), _time) { Hint = () => "idle" };

    /// <summary>Forty lines, "apple" on the 5th and the 30th (store rows 4 and 29).</summary>
    private static void Fill(ScreenPane pane)
    {
        for (int i = 1; i <= 40; i++)
        {
            pane.Write(new Markup((i is 5 or 30 ? "apple " : "line ") + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"));
        }
    }

    /// <summary>A reader that pushes each step's keys at the next wait, noting where the region's top was before it.</summary>
    private static ScriptedInput Steps(ScreenPane pane, List<int> tops, params ConsoleKeyInfo[][] steps)
    {
        var input = new ScriptedInput();
        var queue = new Queue<ConsoleKeyInfo[]>(steps);
        input.OnWait = () =>
        {
            tops.Add(pane.ScrollTop);
            if (queue.TryDequeue(out var keys))
            {
                input.Push(keys);
            }
        };

        return input;
    }

    /// <summary>
    /// Typing finds newest first: the view goes up to the match nearest the bottom, Enter to the one above it, Shift+Enter back
    /// down; the hint counts from the newest; ESC ends it with the transcript at the bottom and the input row back.
    /// </summary>
    [Fact]
    public async Task NewestFirst_EnterGoesOlder_ShiftEnterNewer_AndEscIsBackAtTheBottom()
    {
        using var pane = Pane();
        pane.Show();
        Fill(pane);
        var tops = new List<int>();
        var input = Steps(pane, tops,
            [Keys.Char('A'), Keys.Char('p'), Keys.Char('p')],   // case ignored
            [Keys.Enter],
            [Keys.Shift(ConsoleKey.Enter)],
            [Keys.Escape]);

        await new TranscriptFind(pane, new KeySource(input, TimeSpan.FromMilliseconds(1))).RunAsync("", CancellationToken.None);

        Assert.Equal(-1, tops[0]);                       // nothing typed: the bottom
        Assert.InRange(tops[1], 0, 29);                  // "apple 30" in view
        Assert.InRange(tops[2], 0, 4);                   // Enter: "apple 5", older, higher up
        Assert.Equal(tops[1], tops[3]);                  // Shift+Enter: back down to the newest
        int newest = _console.Output.IndexOf(TranscriptFind.Hint(1, 2), StringComparison.Ordinal);
        int older = _console.Output.IndexOf(TranscriptFind.Hint(2, 2), newest, StringComparison.Ordinal);
        Assert.True(newest > 0 && older > newest, _console.Output);
        Assert.Contains(TranscriptFind.EmptyHint, _console.Output);
        Assert.False(pane.Scrolled);
        Assert.False(pane.OverlayOpen);
    }

    [Fact]
    public async Task NoMatch_SaysSo_AndTheViewStaysPut()
    {
        using var pane = Pane();
        pane.Show();
        Fill(pane);
        var tops = new List<int>();
        var input = Steps(pane, tops, [Keys.Char('z')], [Keys.Enter], [Keys.Backspace], [Keys.Backspace], [Keys.Escape]);

        await new TranscriptFind(pane, new KeySource(input, TimeSpan.FromMilliseconds(1))).RunAsync("", CancellationToken.None);

        Assert.All(tops, top => Assert.Equal(-1, top));
        Assert.Contains(TranscriptFind.Hint(0, 0), _console.Output);
        Assert.Equal("no match · Backspace = erase · ESC = done", TranscriptFind.Hint(0, 0));
        Assert.Equal("1 of 2 · Enter = older · Shift+Enter = newer · PgUp/PgDn = scroll · ESC = done", TranscriptFind.Hint(1, 2));
        Assert.Equal("type = find · ESC = done", TranscriptFind.EmptyHint);
    }

    /// <summary>The words given up front open the folded run that holds them at once, and the end of the find folds it again.</summary>
    [Fact]
    public async Task GivenWords_OpenTheFoldHoldingThem_AndTheEndFoldsItAgain()
    {
        using var pane = Pane();
        pane.Show();
        pane.Write(new Markup("before\n"));
        pane.BeginToolGroup(1);
        pane.SetToolGroupSummary(new Markup("S"), new Markup("E"));
        pane.WriteToolLine(new Markup("m1\n"));
        pane.WriteToolLine(new Markup("hidden needle\n"));
        pane.WriteToolLine(new Markup("m3\n"));
        pane.EndToolGroup();
        Assert.DoesNotContain(pane.TranscriptRows(), row => TextFind.LineText(row).Contains("needle", StringComparison.Ordinal));
        int rowsBefore = pane.StoredRows;
        var rowsDuring = new List<int>();
        var input = new ScriptedInput();
        input.OnWait = () =>
        {
            rowsDuring.Add(pane.StoredRows);
            input.Push(Keys.Escape);
        };

        await new TranscriptFind(pane, new KeySource(input, TimeSpan.FromMilliseconds(1))).RunAsync("needle", CancellationToken.None);

        Assert.True(rowsDuring[0] > rowsBefore);                      // unfolded while finding
        Assert.Contains(TranscriptFind.Hint(1, 1), _console.Output);
        Assert.Equal(rowsBefore, pane.StoredRows);                    // folded again
    }

    [Fact]
    public async Task WithoutThePane_ItIsNothing()
    {
        using var pane = new ScreenPane(_console, null, _time);
        var input = new ScriptedInput();
        input.Push(Keys.Char('x'));

        await new TranscriptFind(pane, new KeySource(input, TimeSpan.FromMilliseconds(1))).RunAsync("x", CancellationToken.None);

        Assert.True(input.IsAvailable);   // nothing read
    }

    /// <summary>The typed /find line that opened the find is not searched (it holds the words); with no such line, every row is.</summary>
    [Fact]
    public void TheOwnFindLine_IsLeftOut()
    {
        static IReadOnlyList<Segment> Row(string text) => [new Segment(text)];
        IReadOnlyList<IReadOnlyList<Segment>> typed = [Row("apple"), Row(InputLine.PromptGlyph + "/find apple"), Row(" ")];
        IReadOnlyList<IReadOnlyList<Segment>> chord = [Row("apple"), Row("pear")];

        Assert.Equal(1, TranscriptFind.OwnLine(typed));
        Assert.Equal(2, TranscriptFind.OwnLine(chord));
        Assert.Equal(0, TranscriptFind.OwnLine([]));
        Assert.Equal("the end of the transcript", ScreenPane.FindAtTheEnd);
    }

    /// <summary>Ctrl+Shift+F is /find; Ctrl+F stays /perfbar.</summary>
    [Fact]
    public void CtrlShiftF_IsFind_AndCtrlFStaysPerfbar()
    {
        Assert.Equal("/find", Keys.ShortcutLine(new ConsoleKeyInfo('\x06', ConsoleKey.F, shift: true, alt: false, control: true)));
        Assert.Equal("/perfbar", Keys.ShortcutLine(Keys.CtrlF));
    }

    [Fact]
    public void UnfoldMatching_OpensOnlyTheFoldsHidingTheText_AndRefoldPutsThemBack()
    {
        var store = new Scrollback();
        store.Append([new Segment("before\n")], 40);
        store.BeginGroup(1);
        store.SetGroupSummary([new Segment("  S")], [new Segment("  E")]);
        store.Append([new Segment("  m1\n")], 40, member: true);
        store.Append([new Segment("  Needle\n")], 40, member: true);
        store.EndGroup();
        Assert.Equal(2, store.Rows(40).Count);   // before, S

        Assert.Empty(store.UnfoldMatching("before"));   // shown already
        Assert.Empty(store.UnfoldMatching("nowhere"));
        var opened = store.UnfoldMatching("needle");    // case ignored
        Assert.Single(opened);
        Assert.Equal(4, store.Rows(40).Count);   // before, E, m1, Needle
        Assert.True(store.TakeReshaped());

        store.Refold(opened);
        Assert.Equal(2, store.Rows(40).Count);
    }
}
