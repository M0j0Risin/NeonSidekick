using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The <c>/cwd browse</c> picker (2026-09-21) on a 40×12 console: the strip and the path row at
/// buffer rows 100 and 101, the tree from 102, six rows in view. The disks are a fake's.
/// </summary>
public class FolderPaneTests : IDisposable
{
    private static readonly string C = FakeFolders.RootPath;
    private static readonly string D = FakeFolders.SecondRootPath;

    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly ManualTimeProvider _time = new();

    public FolderPaneTests()
    {
        _console.Profile.Width = 40;
        _console.Profile.Height = 12;
    }

    public void Dispose() => _console.Dispose();

    private static string P(params string[] parts) => Path.Combine(parts);

    private static bool Windows => OperatingSystem.IsWindows();

    /// <summary>C:\ with Users (alice, bob) and Windows (System32), D:\ with Repo.</summary>
    private static FakeFolders Disks() => new FakeFolders()
        .Root(C).Root(D)
        .Add(C, "Users", "Windows")
        .Add(P(C, "Users"), "alice", "bob")
        .Add(P(C, "Windows"), "System32")
        .Add(D, "Repo");

    private (FolderPane Pane, ScreenPane Screen, ScriptedInput Input) Picker()
    {
        var screen = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), _time) { Hint = () => "idle" };
        screen.Show();
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        return (new FolderPane(screen, keys), screen, input);
    }

    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    private static string Titled(string row, int width = 40) => row + new string(' ', width - 2 - TextCells.Width(row)) + ScreenPane.CloseGlyph;

    /// <summary>The strip as drawn: the label, then the button as a dim tab with a space either side.</summary>
    private static string Strip => Titled(FolderText.Title + "   " + FolderText.CollapseAllButton + " ");

    /// <summary>A tree row: the pointer or its blank, the depth's indent, the glyph, the name.</summary>
    private static string Row(bool active, int depth, string glyph, string name) => (active ? MenuPane.Pointer : MenuPane.NoPointer) + new string(' ', depth * 2) + glyph + " " + name;

    private static string Closed(bool active, int depth, string name) => Row(active, depth, FolderText.CollapsedGlyph, name);

    private static string Open(bool active, int depth, string name) => Row(active, depth, FolderText.ExpandedGlyph, name);

    private static string Leaf(bool active, int depth, string name) => Row(active, depth, FolderText.LeafGlyph, name);

    /// <summary>The second root's row, or nothing off Windows (one root there).</summary>
    private static string SecondRoot(bool active = false) => Windows ? Closed(active, 0, D) + "\n" : "";

    private string Output => _console.Output;

    [Fact]
    public async Task Opens_OnTheRow_SpaceOpensANode_DownAndEnterChooseAChild()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        input.Push(Keys.Char(' '), Keys.Down, Keys.Enter);

        string? picked = await picker.PickAsync(new FolderTree(Disks()), 0, CancellationToken.None);

        Assert.Equal(P(C, "Users"), picked);
        // The first draw: the strip, the highlighted node's path, the roots with the cursor on the first, the hint row.
        Assert.Contains(Rule(40) + "\n" + Strip + "\n" + C + "\n" + Closed(true, 0, C) + "\n" + SecondRoot() + Rule(40) + "\nEnter = choose", Output);
        // Space: C:\ open, its two folders under it; Down: the cursor and the path row on Users.
        Assert.Contains("\n" + C + "\n" + Open(true, 0, C) + "\n" + Closed(false, 1, "Users") + "\n" + Closed(false, 1, "Windows") + "\n" + SecondRoot(), Output);
        Assert.Contains("\n" + P(C, "Users") + "\n" + Open(false, 0, C) + "\n" + Closed(true, 1, "Users") + "\n", Output);
        Assert.False(screen.OverlayOpen);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
    }

    /// <summary>A command chord (2026-10-01, the user's ask): nothing chosen, the chord kept for the screen; one done in place (<c>/perf</c>) leaves the tree reading.</summary>
    [Fact]
    public async Task AChord_ChoosesNothing_AndKeepsItsCommand_UnlessDoneInPlace()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        screen.ChordInPlace = line => line == "/perf";
        input.Push(Keys.CtrlAlt(ConsoleKey.E), Keys.Char(' '), Keys.Down, Keys.Enter);
        Assert.Equal(P(C, "Users"), await picker.PickAsync(new FolderTree(Disks()), 0, CancellationToken.None));
        Assert.Null(screen.TakeDismissHit());

        input.Push(Keys.CtrlAltC, Keys.Enter);
        Assert.Null(await picker.PickAsync(new FolderTree(Disks()), 0, CancellationToken.None));
        Assert.Equal(new ScreenPane.OffPaneHit(null, null, "/clear"), screen.TakeDismissHit());
        Assert.False(screen.OverlayOpen);
    }

    [Fact]
    public async Task RightOpensThenStepsIn_LeftClosesThenStepsOut_EscChoosesNothing()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        input.Push(Keys.Key(ConsoleKey.RightArrow));     // C:\ open
        input.Push(Keys.Key(ConsoleKey.RightArrow));     // onto Users
        input.Push(Keys.Key(ConsoleKey.RightArrow));     // Users open
        input.Push(Keys.Key(ConsoleKey.RightArrow));     // onto alice
        input.Push(Keys.Key(ConsoleKey.LeftArrow));      // alice is closed: onto Users
        input.Push(Keys.Key(ConsoleKey.LeftArrow));      // Users closed
        input.Push(Keys.Key(ConsoleKey.LeftArrow));      // onto C:\
        input.Push(Keys.Key(ConsoleKey.LeftArrow));      // C:\ closed
        input.Push(Keys.Key(ConsoleKey.LeftArrow));      // a root: nothing
        input.Push(Keys.Escape);

        Assert.Null(await picker.PickAsync(new FolderTree(Disks()), 0, CancellationToken.None));

        Assert.Contains("\n" + P(C, "Users", "alice") + "\n" + Open(false, 0, C) + "\n" + Open(false, 1, "Users") + "\n" + Closed(true, 2, "alice") + "\n" + Closed(false, 2, "bob") + "\n" + Closed(false, 1, "Windows") + "\n", Output);
        Assert.Contains("\n" + P(C, "Users") + "\n" + Open(false, 0, C) + "\n" + Closed(true, 1, "Users") + "\n" + Closed(false, 1, "Windows") + "\n", Output);
        // The last draw: the roots again, the hint row cut to the window with an ellipsis; then the pane closed.
        Assert.Contains(Rule(40) + "\n" + Strip + "\n" + C + "\n" + Closed(true, 0, C) + "\n" + SecondRoot() + Rule(40) + "\nEnter = choose · Space = expand/collap…\n", Output);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
        Assert.False(screen.OverlayOpen);
    }

    [Fact]
    public async Task Minus_CollapsesAll_TheCursorOnTheRootItWasUnder_AndALetterJumps()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        var tree = new FolderTree(Disks());
        int bob = tree.ExpandTo(P(C, "Users", "bob"));
        input.Push(Keys.Char('w'));                      // Windows
        input.Push(Keys.Char('-'));                      // everything closed, the cursor on C:\
        input.Push(Keys.Enter);

        Assert.Equal(C, await picker.PickAsync(tree, bob, CancellationToken.None));

        Assert.Contains("\n" + P(C, "Users", "bob") + "\n" + Open(false, 0, C) + "\n" + Open(false, 1, "Users") + "\n" + Closed(false, 2, "alice") + "\n" + Closed(true, 2, "bob") + "\n" + Closed(false, 1, "Windows") + "\n", Output);
        Assert.Contains("\n" + P(C, "Windows") + "\n" + Open(false, 0, C) + "\n", Output);
        Assert.Contains("\n" + C + "\n" + Closed(true, 0, C) + "\n" + SecondRoot() + Rule(40), Output);
    }

    [Fact]
    public async Task ADeniedFolder_StaysClosed_WithTheNoticeOnThePathRow_ForOneDraw()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        var tree = new FolderTree(Disks().Deny(P(C, "Windows")));
        tree.Expand(0);
        input.Push(Keys.Char(' '));                      // refused
        input.Push(Keys.Up);                             // the path row again
        input.Push(Keys.Escape);

        Assert.Null(await picker.PickAsync(tree, 2, CancellationToken.None));

        Assert.Contains("\n" + TranscriptRenderer.ErrorGlyph + FolderText.DeniedNotice(P(C, "Windows")) + "\n" + Open(false, 0, C) + "\n" + Closed(false, 1, "Users") + "\n" + Leaf(true, 1, "Windows") + "\n", Output);
        Assert.Contains("\n" + P(C, "Users") + "\n" + Open(false, 0, C) + "\n" + Closed(true, 1, "Users") + "\n" + Leaf(false, 1, "Windows") + "\n", Output);
    }

    [Fact]
    public async Task ATreeTallerThanTheWindow_ScrollsWithTheCursor_UnderTheMoreRow()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        var tree = new FolderTree(Disks());
        tree.ExpandTo(P(C, "Users", "bob"));
        tree.Expand(4);                                  // Windows: seven rows on Windows (C:\, Users, alice, bob, Windows, System32, D:\), six off it
        if (!Windows)
        {
            tree.Expand(tree.Visible.Count - 1);         // nothing more to add off Windows: the six rows fit
        }

        input.Push(Keys.End, Keys.Enter);

        string? picked = await picker.PickAsync(tree, 0, CancellationToken.None);

        if (Windows)
        {
            Assert.Equal(D, picked);
            // Six slots: five rows and the more row (the menu's viewport); End brings the last row in with the first two cut. System32 was never read: not a leaf yet.
            Assert.Contains("\n" + C + "\n" + Open(true, 0, C) + "\n" + Open(false, 1, "Users") + "\n" + Closed(false, 2, "alice") + "\n" + Closed(false, 2, "bob") + "\n" + Open(false, 1, "Windows") + "\n" + MenuPane.NoPointer + MenuPane.MoreHint + "\n" + Rule(40), Output);
            Assert.Contains("\n" + D + "\n" + Closed(false, 2, "alice") + "\n" + Closed(false, 2, "bob") + "\n" + Open(false, 1, "Windows") + "\n" + Closed(false, 2, "System32") + "\n" + Closed(true, 0, D) + "\n" + MenuPane.NoPointer + MenuPane.MoreHint + "\n" + Rule(40), Output);
        }
        else
        {
            Assert.Equal(P(C, "Windows", "System32"), picked);
        }
    }

    /// <summary>A shortcut root (later on 2026-09-21) draws as the house and its label above the drives, opens like a drive, and Enter on it answers its folder.</summary>
    [Fact]
    public async Task AShortcut_HeadsTheTree_WithTheHouse_AndOpensLikeADrive()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        string files = P(C, "Users", "alice", "files");
        var tree = new FolderTree(Disks().Add(P(C, "Users", "alice"), "files").Add(files, "docs"), [new FolderShortcut(FolderText.ProfileLabel, files, FolderText.ShortcutGlyph)]);
        input.Push(Keys.Char(' '), Keys.Enter);            // the shortcut open, then chosen

        Assert.Equal(files, await picker.PickAsync(tree, 0, CancellationToken.None));

        string profile = FolderText.ShortcutGlyph + " " + FolderText.ProfileLabel;
        Assert.Contains("\n" + files + "\n" + Closed(true, 0, profile) + "\n" + Closed(false, 0, C) + "\n" + SecondRoot() + Rule(40), Output);
        Assert.Contains("\n" + files + "\n" + Open(true, 0, profile) + "\n" + Closed(false, 1, "docs") + "\n" + Closed(false, 0, C) + "\n" + SecondRoot() + Rule(40), Output);
    }

    // ── The mouse ───────────────────────────────────────────────────────────

    /// <summary>A click on the glyph's side toggles the node; one on the name moves the cursor, a second there within the interval toggles it too (Explorer's double-click, the user's call later on 2026-09-21) — only Enter chooses.</summary>
    [Fact]
    public async Task AClickOnTheGlyph_TogglesTheNode_OneOnTheName_MovesTheCursor_ASecondTogglesIt_OnlyEnterChooses()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        input.PushClick(2, 102);                         // C:\'s glyph: open
        input.PushClick(12, 103);                        // Users' name: the cursor
        input.PushClick(4, 103);                         // Users' glyph: open, the cursor stays
        input.PushClick(14, 104);                        // alice's name: the cursor
        input.PushClick(15, 104);                        // again: the pair opens alice — empty, so nothing shows under it
        input.PushClick(14, 103);                        // Users' name: the cursor
        input.PushClick(14, 103);                        // the pair: Users closed
        input.PushClick(14, 103);                        // a third within the interval: a first again
        input.PushClick(14, 103);                        // the pair: Users open
        input.Push(Keys.Enter);

        Assert.Equal(P(C, "Users"), await picker.PickAsync(new FolderTree(Disks()), 0, CancellationToken.None));

        Assert.Contains("\n" + C + "\n" + Open(true, 0, C) + "\n" + Closed(false, 1, "Users") + "\n", Output);
        Assert.Contains("\n" + P(C, "Users") + "\n" + Open(false, 0, C) + "\n" + Closed(true, 1, "Users") + "\n", Output);
        Assert.Contains("\n" + P(C, "Users") + "\n" + Open(false, 0, C) + "\n" + Open(true, 1, "Users") + "\n" + Closed(false, 2, "alice") + "\n", Output);
        Assert.Contains("\n" + P(C, "Users", "alice") + "\n" + Open(false, 0, C) + "\n" + Open(false, 1, "Users") + "\n" + Closed(true, 2, "alice") + "\n", Output);
        Assert.Contains("\n" + P(C, "Users", "alice") + "\n" + Open(false, 0, C) + "\n" + Open(false, 1, "Users") + "\n" + Leaf(true, 2, "alice") + "\n", Output);
        Assert.Contains("\n" + P(C, "Users") + "\n" + Open(false, 0, C) + "\n" + Closed(true, 1, "Users") + "\n" + Closed(false, 1, "Windows") + "\n", Output);
        Assert.EndsWith(Rule(40) + "\n› \n" + Rule(40) + "\nidle", Output);
        Assert.False(screen.OverlayOpen);
    }

    [Fact]
    public async Task AClickOnTheButton_CollapsesAll_TheGapsAndARightClickAreNothing_TheWheelMoves()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        var tree = new FolderTree(Disks());
        int bob = tree.ExpandTo(P(C, "Users", "bob"));
        input.PushClick(1, 100);                         // the label: nothing
        input.PushClick(11, 100);                        // the gap before the button: nothing (the label 📂 Folders since 2026-09-29)
        input.PushClick(10, 101);                        // the path row: nothing
        input.PushClick(14, 104, MouseButton.Right);     // a right click: nothing
        input.PushClick(12, 100);                        // the button: everything closed, the cursor on C:\
        input.PushWheel(-1, 5, 102);                     // a notch toward the user: down a row
        input.Push(Keys.Enter);

        string? picked = await picker.PickAsync(tree, bob, CancellationToken.None);

        Assert.Equal(Windows ? D : C, picked);
        Assert.Contains("\n" + C + "\n" + Closed(true, 0, C) + "\n" + SecondRoot() + Rule(40), Output);
        Assert.True(FolderPane.ButtonAt(12));
        Assert.True(FolderPane.ButtonAt(27));
        Assert.False(FolderPane.ButtonAt(11));
        Assert.False(FolderPane.ButtonAt(28));
    }

    [Fact]
    public async Task TheCloseGlyph_AndTwoClicksOffThePane_ChooseNothing()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        input.PushClick(38, 100);                        // the ×

        Assert.Null(await picker.PickAsync(new FolderTree(Disks()), 0, CancellationToken.None));
        Assert.False(screen.OverlayOpen);

        screen.Show();
        input.PushClick(5, 50);                          // the transcript: a first
        input.Push(Keys.Down);                           // a key ends the pair
        input.PushClick(5, 50);                          // a first again
        input.PushClick(20, 106);                        // the hint row: the pair
        input.Push(Keys.Enter);                          // never read

        Assert.Null(await picker.PickAsync(new FolderTree(Disks()), 0, CancellationToken.None));
        Assert.False(screen.OverlayOpen);
        Assert.True(input.IsAvailable);
    }

    [Fact]
    public async Task TheToken_AndADryRead_ChooseNothing_AndThePaneNeedsTheScreen()
    {
        var (picker, screen, input) = Picker();
        using var _ = screen;
        using var cts = new CancellationTokenSource();
        input.OnWait = cts.Cancel;

        Assert.Null(await picker.PickAsync(new FolderTree(Disks()), 0, cts.Token));
        Assert.False(screen.OverlayOpen);

        using var plain = new ScreenPane(_console, null, _time);
        var none = new FolderPane(plain, new KeySource(new ScriptedInput(), TimeSpan.FromMilliseconds(1)));
        Assert.False(none.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.PickAsync(new FolderTree(Disks()), 0, CancellationToken.None));
        Assert.Equal(InfoPane.TabStripMarkup(FolderText.Title, [FolderText.CollapseAllButton], -1), FolderPane.StripMarkup());
    }
}
