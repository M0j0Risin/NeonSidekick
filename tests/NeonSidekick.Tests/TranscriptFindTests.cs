using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The transcript's find (2026-10-07, the user's ask, phase 5 of the UI round): <c>/find</c> and Ctrl+F (Ctrl+Shift+F until 2026-10-18) over the scrollback.</summary>
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

    /// <summary>Two clicks off the find close it, as every pane's do (2026-10-14, the user's ask); one is nothing, and ESC ends that one.</summary>
    [Fact]
    public async Task TwoClicksOffTheFind_CloseIt_OneIsNothing()
    {
        using var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), _time) { Hint = () => "idle" };
        pane.Show();
        Fill(pane);
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        input.PushClick(5, 60).Push(Keys.Escape);

        await new TranscriptFind(pane, keys).RunAsync("", CancellationToken.None);

        Assert.Null(pane.TakeDismissHit());
        bool waited = false;
        input.OnWait = () => { waited = true; input.Push(Keys.Escape); };   // still open after the pair: ESC, and the test fails
        input.PushClick(5, 60).PushClick(5, 60);

        await new TranscriptFind(pane, keys).RunAsync("", CancellationToken.None);

        Assert.False(waited);
        Assert.False(pane.OverlayOpen);
    }

    /// <summary>A paste and a right-click go onto the find (2026-10-14, the user's call): the first line of each; a clipboard with no text is nothing.</summary>
    [Fact]
    public async Task APaste_AndARightClick_GoOntoTheFind()
    {
        using var pane = Pane();
        pane.Show();
        Fill(pane);
        var input = new ScriptedInput();
        string? clip = "le 3\nmore";
        input.PushPaste("app\nzzz");                    // "app": both apples
        input.PushClick(5, 5, MouseButton.Right);        // "apple 3": the 30th alone
        input.Push(Keys.Escape);

        await new TranscriptFind(pane, new KeySource(input, TimeSpan.FromMilliseconds(1)), () => clip).RunAsync("", CancellationToken.None);

        int both = _console.Output.IndexOf(TranscriptFind.Hint(1, 2), StringComparison.Ordinal);
        int one = _console.Output.IndexOf(TranscriptFind.Hint(1, 1), both, StringComparison.Ordinal);
        Assert.True(both > 0 && one > both, _console.Output);
        Assert.DoesNotContain("zzz", _console.Output);
        clip = null;
        input.PushClick(5, 5, MouseButton.Right).Push(Keys.Escape);   // a picture alone: nothing
        await new TranscriptFind(pane, new KeySource(input, TimeSpan.FromMilliseconds(1)), () => clip).RunAsync("", CancellationToken.None);
        Assert.False(pane.OverlayOpen);
    }

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
        Assert.Equal("no match · Backspace = erase · Esc = done", TranscriptFind.Hint(0, 0));
        Assert.Equal($"1 of 2 · Enter = older · {InfoPane.FindBack} = newer · PgUp/PgDn = scroll · Esc = done", TranscriptFind.Hint(1, 2));
        Assert.Equal("type = find · Esc = done", TranscriptFind.EmptyHint);
    }

    /// <summary>
    /// A resize under the find (2026-10-29): the rows move, so the find bar finds its places again and walks on from the one shown —
    /// Enter goes to the next older match, not to a row number from before the resize (where other text is now).
    /// </summary>
    [Fact]
    public async Task AfterAResize_EnterGoesOlder_FromTheMatchOnShow()
    {
        using var pane = Pane();
        pane.Show();
        for (int i = 1; i <= 40; i++)
        {
            string word = i is 5 or 20 or 30 ? "apple" : "line";
            pane.Write(new Markup($"{word} {i:00} with words enough to wrap at thirty cells" + Environment.NewLine));
        }

        var shown = new List<string>();
        var steps = new Queue<ConsoleKeyInfo[]>([[Keys.Char('a'), Keys.Char('p'), Keys.Char('p'), Keys.Char('l'), Keys.Char('e')], [Keys.Enter], [Keys.Enter], [Keys.Escape]]);
        var input = new ScriptedInput();
        input.OnWait = () =>
        {
            var place = pane.FindPlace;
            shown.Add(place.Line >= 0 ? TextFind.LineText(pane.TranscriptRows()[place.Line]) : "");
            if (steps.Count == 2)
            {
                _console.Profile.Width = 30;   // between the two Enters
                _time.Advance(ScreenPane.Tick);   // the tick rebuilds for it, as it does within a tick of any resize
            }

            if (steps.TryDequeue(out var keys))
            {
                input.Push(keys);
            }
        };

        await new TranscriptFind(pane, new KeySource(input, TimeSpan.FromMilliseconds(1))).RunAsync("", CancellationToken.None);

        Assert.StartsWith("apple 30", shown[1]);   // typed: the newest
        Assert.StartsWith("apple 20", shown[2]);   // Enter: older
        Assert.StartsWith("apple 05", shown[3]);   // the resize, then Enter: older again, from 20
    }

    [Fact]
    public void Nearest_IsTheMatchAtThePlace_ElseTheLastBefore_ElseTheFirst()
    {
        List<TextFind.Hit> hits = [new(2, 0), new(5, 3), new(5, 9), new(8, 1)];
        Assert.Equal(2, TranscriptFind.Nearest(hits, new TextFind.Hit(5, 9)));
        Assert.Equal(1, TranscriptFind.Nearest(hits, new TextFind.Hit(5, 4)));
        Assert.Equal(0, TranscriptFind.Nearest(hits, new TextFind.Hit(1, 0)));
        Assert.Equal(3, TranscriptFind.Nearest(hits, new TextFind.Hit(-1, -1)));   // no place: the newest
        Assert.Equal(-1, TranscriptFind.Nearest([], new TextFind.Hit(5, 9)));
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

    /// <summary>Ctrl+F is /find since 2026-10-18 (the user's pick; /perfbar until then); Ctrl+Shift+F, /find until then, is the terminal's again.</summary>
    [Fact]
    public void CtrlF_IsFind_AndCtrlShiftF_IsNoChord()
    {
        Assert.Equal("/find", Keys.ShortcutLine(Keys.CtrlF));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\x06', ConsoleKey.F, shift: true, alt: false, control: true)));
    }

    /// <summary>
    /// The step back the hints name (2026-10-07): Shift+Enter, Shift+F3 on a Mac, whose terminal sends Shift+Enter as a plain
    /// Enter; both step back, and the Mac's plain Enter steps on.
    /// </summary>
    [Fact]
    public void TheHintsStepBack_ShiftEnter_ShiftF3OnAMac()
    {
        Assert.Equal("Shift+Enter", InfoPane.FindBackKey(mac: false));
        Assert.Equal("Shift+F3", InfoPane.FindBackKey(mac: true));
        Assert.Contains(" · " + InfoPane.FindBack + " = newer · ", TranscriptFind.Hint(1, 2));
        Assert.Contains(" · " + InfoPane.FindBack + " = back · ", InfoPane.FindHint("b", 0, 1));
        Assert.Equal(-1, InfoPane.FindStep(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: true, alt: false, control: false)));
        Assert.Equal(-1, InfoPane.FindStep(new ConsoleKeyInfo('\0', ConsoleKey.F3, shift: true, alt: false, control: false)));
        Assert.Equal(1, InfoPane.FindStep(new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false)));
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
