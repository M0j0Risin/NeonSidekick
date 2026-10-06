using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

public class FolderTreeTests
{
    private static readonly string C = FakeFolders.RootPath;
    private static readonly string D = FakeFolders.SecondRootPath;

    private static string P(params string[] parts) => Path.Combine(parts);

    /// <summary>C:\ with Users (alice, bob) and Windows (System32), D:\ with Repo; alice holds .neonsidekick, which only a source that shows hidden folders lists.</summary>
    private static FakeFolders Disks(bool hidden = false)
    {
        var folders = new FakeFolders()
            .Root(C).Root(D)
            .Add(C, "Users", "Windows")
            .Add(P(C, "Users"), "alice", "bob")
            .Add(P(C, "Windows"), "System32")
            .Add(D, "Repo");
        if (hidden)
        {
            folders.Add(P(C, "Users", "alice"), ".neonsidekick");
        }
        else
        {
            // Exists, but never listed: the profile's home under the default mode.
            folders.Add(P(C, "Users", "alice", ".neonsidekick"));
        }

        return folders;
    }

    private static string[] Names(FolderTree tree) => tree.Visible.Select(n => n.Name).ToArray();

    /// <summary>The bare roots: two drives on Windows, the one <c>/</c> elsewhere.</summary>
    private static string[] Roots => OperatingSystem.IsWindows() ? [C, D] : [C];

    [Fact]
    public void Opens_OnTheRootsAlone_EachAtDepthZero_NothingRead()
    {
        var folders = Disks();
        var tree = new FolderTree(folders);

        Assert.Equal(Roots, Names(tree));
        Assert.All(tree.Visible, n => Assert.Equal(0, n.Depth));
        Assert.All(tree.Visible, n => Assert.False(n.Expanded));
        Assert.All(tree.Visible, n => Assert.Null(n.Children));
        Assert.Equal(0, folders.Reads);
        Assert.Equal(C, tree.Visible[0].Path);
    }

    [Fact]
    public void Expand_ReadsTheChildrenOnce_ListsThemUnderTheNode_AndCollapseKeepsThem()
    {
        var folders = Disks();
        var tree = new FolderTree(folders);

        Assert.True(tree.Expand(0));
        Assert.True(tree.Visible[0].Expanded);
        Assert.Equal(["Users", "Windows"], tree.Visible.Skip(1).Take(2).Select(n => n.Name));
        Assert.Equal(1, tree.Visible[1].Depth);
        Assert.Equal(P(C, "Users"), tree.Visible[1].Path);
        Assert.Equal(1, folders.Reads);

        Assert.True(tree.Expand(1));                  // Users
        Assert.Equal(["alice", "bob"], tree.Visible.Skip(2).Take(2).Select(n => n.Name));
        Assert.Equal(2, tree.Visible[2].Depth);
        Assert.Equal("Windows", tree.Visible[4].Name);

        tree.Collapse(1);
        Assert.Equal(["Users", "Windows"], tree.Visible.Skip(1).Take(2).Select(n => n.Name));
        Assert.False(tree.Visible[1].Expanded);
        Assert.NotNull(tree.Visible[1].Children);     // kept
        Assert.True(tree.Expand(1));
        Assert.Equal(2, folders.Reads);               // not read again
        Assert.True(tree.Expand(1));                  // already open: nothing changes
        Assert.Equal(2, folders.Reads);
    }

    [Fact]
    public void Expand_AnEmptyFolder_IsALeaf_NeverOpen()
    {
        var tree = new FolderTree(Disks());
        tree.Expand(0);
        tree.Expand(1);                               // Users
        int bob = 3;
        Assert.Equal("bob", tree.Visible[bob].Name);

        Assert.False(tree.Expand(bob));
        Assert.False(tree.Visible[bob].Expanded);
        Assert.True(tree.Visible[bob].IsLeaf);
        Assert.Equal(FolderText.LeafGlyph, FolderText.Glyph(tree.Visible[bob]));
        Assert.False(tree.Toggle(bob));
    }

    [Fact]
    public void Expand_ADeniedFolder_MarksIt_AndStaysClosed()
    {
        var folders = Disks().Deny(P(C, "Windows"));
        var tree = new FolderTree(folders);
        tree.Expand(0);
        int windows = 2;
        Assert.Equal("Windows", tree.Visible[windows].Name);
        Assert.False(tree.Visible[windows].Denied);

        Assert.False(tree.Expand(windows));
        Assert.True(tree.Visible[windows].Denied);
        Assert.True(tree.Visible[windows].IsLeaf);
        Assert.False(tree.Visible[windows].Expanded);
        Assert.Null(tree.Visible[windows].Children);
        int reads = folders.Reads;
        Assert.False(tree.Expand(windows));           // not tried again
        Assert.Equal(reads, folders.Reads);
    }

    [Fact]
    public void Toggle_OpensThenCloses()
    {
        var tree = new FolderTree(Disks());

        Assert.True(tree.Toggle(0));
        Assert.True(tree.Visible[0].Expanded);
        Assert.Equal(FolderText.ExpandedGlyph, FolderText.Glyph(tree.Visible[0]));
        Assert.False(tree.Toggle(0));
        Assert.False(tree.Visible[0].Expanded);
        Assert.Equal(FolderText.CollapsedGlyph, FolderText.Glyph(tree.Visible[0]));
        Assert.False(tree.Toggle(99));
    }

    [Fact]
    public void ExpandTo_OpensEveryFolderAbove_AndAnswersTheRow()
    {
        var folders = Disks();
        var tree = new FolderTree(folders);

        int row = tree.ExpandTo(P(C, "Users", "bob"));

        Assert.Equal(3, row);
        Assert.Equal(P(C, "Users", "bob"), tree.Visible[row].Path);
        Assert.True(tree.Visible[0].Expanded);
        Assert.True(tree.Visible[1].Expanded);
        Assert.False(tree.Visible[row].Expanded);     // the target itself stays closed
        Assert.Equal(["Users", "alice", "bob", "Windows"], Names(tree).Skip(1).Take(4));
        Assert.Equal(2, folders.Reads);
        Assert.Equal(-1, tree.ParentOf(0));
        Assert.Equal(1, tree.ParentOf(row));
        Assert.Equal(0, tree.ParentOf(1));
    }

    [Fact]
    public void ExpandTo_AFolderTheSourceHides_AddsItToItsParentsList_WhenItExists()
    {
        var tree = new FolderTree(Disks());

        int row = tree.ExpandTo(P(C, "Users", "alice", ".neonsidekick"));

        Assert.Equal(P(C, "Users", "alice", ".neonsidekick"), tree.Visible[row].Path);
        Assert.Equal(".neonsidekick", tree.Visible[row].Name);
        Assert.Equal(3, tree.Visible[row].Depth);
        Assert.Equal("alice", tree.Visible[tree.ParentOf(row)].Name);
        Assert.True(tree.Visible[tree.ParentOf(row)].Expanded);
    }

    [Fact]
    public void ExpandTo_StopsAtTheDeepestFolderFound_OrTheFirstRootForAnUnknownDrive()
    {
        var tree = new FolderTree(Disks());

        int row = tree.ExpandTo(P(C, "Users", "carol", "docs"));
        Assert.Equal(P(C, "Users"), tree.Visible[row].Path);
        Assert.False(tree.Visible[row].Expanded);     // the deepest found, closed like a target
        Assert.True(tree.Visible[0].Expanded);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(0, tree.ExpandTo(@"Z:\nowhere"));
        }

        Assert.Equal(0, new FolderTree(new FakeFolders()).ExpandTo(C));
    }

    [Fact]
    public void CollapseAll_ClosesEverything_AndAnswersTheRootTheCursorWasUnder()
    {
        var tree = new FolderTree(Disks());
        tree.ExpandTo(P(C, "Users", "bob"));
        int last = tree.Visible.Count - 1;
        if (OperatingSystem.IsWindows())
        {
            tree.Expand(last);                        // D:\ open too
            Assert.Equal("Repo", tree.Visible[^1].Name);
        }

        int root = tree.CollapseAll(3);               // bob's row

        Assert.Equal(0, root);
        Assert.Equal(Roots, Names(tree));
        Assert.All(tree.Visible, n => Assert.False(n.Expanded));
        Assert.NotNull(tree.Visible[0].Children);     // kept: the next expand is free
        if (OperatingSystem.IsWindows())
        {
            tree.Expand(1);
            Assert.Equal(1, tree.CollapseAll(2));      // Repo's row: D:\
        }

        Assert.Equal(0, tree.CollapseAll(-1));
    }

    [Fact]
    public void JumpFrom_FindsTheNextNameStartingWithTheLetter_Wrapping_CaseFolded()
    {
        var tree = new FolderTree(Disks());
        tree.ExpandTo(P(C, "Users", "bob"));         // C:\, Users, alice, bob, Windows[, D:\]

        Assert.Equal(4, tree.JumpFrom(0, 'w'));
        Assert.Equal(2, tree.JumpFrom(4, 'A'));       // wraps
        Assert.Equal(3, tree.JumpFrom(2, 'b'));
        Assert.Equal(-1, tree.JumpFrom(1, 'u'));      // the only match is the row itself: never
        Assert.Equal(-1, tree.JumpFrom(0, 'z'));
        Assert.Equal(-1, new FolderTree(new FakeFolders()).JumpFrom(0, 'c'));
    }

    [Fact]
    public void RowMarkup_IndentsByDepth_WithTheGlyph_DimForADenied()
    {
        var tree = new FolderTree(Disks().Deny(P(C, "Windows")));
        tree.ExpandTo(P(C, "Users", "bob"));
        tree.Expand(4);                               // Windows: refused

        Assert.Equal(FolderText.ExpandedGlyph + " " + Markup.Escape(C), FolderText.RowMarkup(tree.Visible[0]));
        Assert.Equal("  " + FolderText.ExpandedGlyph + " Users", FolderText.RowMarkup(tree.Visible[1]));
        Assert.Equal("    " + FolderText.CollapsedGlyph + " alice", FolderText.RowMarkup(tree.Visible[2]));
        Assert.Equal(Theme.DimMarkup("  " + FolderText.LeafGlyph + " Windows"), FolderText.RowMarkup(tree.Visible[4]));
        Assert.Equal(4, FolderText.NameColumn(0));
        Assert.Equal(8, FolderText.NameColumn(2));
    }

    /// <summary>A shortcut (later on 2026-09-21): a named root ahead of the drives, its folder read like any other; the path under it opens there, not under the drive, and only at a separator (files2 is not under files).</summary>
    [WindowsFact]
    public void AShortcut_HeadsTheTree_AndThePathUnderIt_OpensThere()
    {
        string files = P(C, "Users", "alice", ".neonsidekick", "profiles", "default", "files");
        var folders = Disks(hidden: true)
            .Add(P(C, "Users", "alice", ".neonsidekick"), "profiles")
            .Add(P(C, "Users", "alice", ".neonsidekick", "profiles"), "default")
            .Add(P(C, "Users", "alice", ".neonsidekick", "profiles", "default"), "files", "files2")
            .Add(files, "docs")
            .Add(files + "2", "other");
        var tree = new FolderTree(folders, [new FolderShortcut("profile", files, FolderText.ShortcutGlyph)]);

        Assert.Equal(["profile", .. Roots], Names(tree));
        Assert.True(tree.Visible[0].IsShortcut);
        Assert.False(tree.Visible[1].IsShortcut);
        Assert.Equal(files, tree.Visible[0].Path);
        Assert.Equal(FolderText.CollapsedGlyph + " " + FolderText.ShortcutGlyph + " profile", FolderText.RowMarkup(tree.Visible[0]));

        Assert.Equal(0, tree.ExpandTo(files));
        Assert.False(tree.Visible[0].Expanded);
        Assert.Equal(1, tree.ExpandTo(P(files, "docs")));
        Assert.Equal(["profile", "docs", .. Roots], Names(tree));
        Assert.Equal(1, tree.Visible[1].Depth);
        Assert.Equal(0, tree.ParentOf(1));
        Assert.Equal(0, tree.RootOf(1));

        // files2 lives beside the shortcut's folder: it opens under the drive, the shortcut left as it was.
        int other = tree.ExpandTo(P(files + "2", "other"));
        Assert.Equal("other", tree.Visible[other].Name);
        Assert.Equal(7, tree.Visible[other].Depth);   // C:\, Users, alice, .neonsidekick, profiles, default, files2, other
        Assert.Equal(C, tree.Visible[tree.RootOf(other)].Path);
        Assert.Equal(["profile", "docs"], Names(tree).Take(2));

        Assert.Equal(1, tree.CollapseAll(other));   // the drive's row is 1 with the shortcut ahead of it
        Assert.Equal(["profile", .. Roots], Names(tree));
        Assert.Equal(0, tree.JumpFrom(2, 'p'));
    }

    /// <summary>The Unix twin of <see cref="AShortcut_HeadsTheTree_AndThePathUnderIt_OpensThere"/> (2026-10-06, the macOS build): one root off Windows.</summary>
    [UnixFact]
    public void AShortcut_HeadsTheTree_AndThePathUnderIt_OpensThere_Unix()
    {
        string files = P(C, "Users", "alice", ".neonsidekick", "profiles", "default", "files");
        var folders = Disks(hidden: true)
            .Add(P(C, "Users", "alice", ".neonsidekick"), "profiles")
            .Add(P(C, "Users", "alice", ".neonsidekick", "profiles"), "default")
            .Add(P(C, "Users", "alice", ".neonsidekick", "profiles", "default"), "files", "files2")
            .Add(files, "docs")
            .Add(files + "2", "other");
        var tree = new FolderTree(folders, [new FolderShortcut("profile", files, FolderText.ShortcutGlyph)]);

        Assert.Equal(["profile", .. Roots], Names(tree));
        Assert.True(tree.Visible[0].IsShortcut);
        Assert.False(tree.Visible[1].IsShortcut);
        Assert.Equal(files, tree.Visible[0].Path);
        Assert.Equal(FolderText.CollapsedGlyph + " " + FolderText.ShortcutGlyph + " profile", FolderText.RowMarkup(tree.Visible[0]));

        Assert.Equal(0, tree.ExpandTo(files));
        Assert.False(tree.Visible[0].Expanded);
        Assert.Equal(1, tree.ExpandTo(P(files, "docs")));
        Assert.Equal(["profile", "docs", .. Roots], Names(tree));
        Assert.Equal(1, tree.Visible[1].Depth);
        Assert.Equal(0, tree.ParentOf(1));
        Assert.Equal(0, tree.RootOf(1));

        // files2 lives beside the shortcut's folder: it opens under the drive, the shortcut left as it was.
        int other = tree.ExpandTo(P(files + "2", "other"));
        Assert.Equal("other", tree.Visible[other].Name);
        Assert.Equal(7, tree.Visible[other].Depth);   // C:\, Users, alice, .neonsidekick, profiles, default, files2, other
        Assert.Equal(C, tree.Visible[tree.RootOf(other)].Path);
        Assert.Equal(["profile", "docs"], Names(tree).Take(2));

        Assert.Equal(1, tree.CollapseAll(other));   // the drive's row is 1 with the shortcut ahead of it
        Assert.Equal(["profile", .. Roots], Names(tree));
        Assert.Equal(0, tree.JumpFrom(1, 'p'));   // one root off Windows: it is row 1
    }

    /// <summary>Two shortcuts (2026-09-24): profile then splash, each behind its own glyph; a path in splash opens under the splash row, not under the drive.</summary>
    [Fact]
    public void TwoShortcuts_EachDrawItsOwnGlyph_AndThePathUnderTheSecond_OpensThere()
    {
        string profileDir = P(C, "Users", "alice", ".neonsidekick", "profiles", "default");
        string files = P(profileDir, "files");
        string splash = P(profileDir, "splash");
        var folders = Disks(hidden: true)
            .Add(P(C, "Users", "alice", ".neonsidekick"), "profiles")
            .Add(P(C, "Users", "alice", ".neonsidekick", "profiles"), "default")
            .Add(profileDir, "files", "splash")
            .Add(splash, "comfy");
        var tree = new FolderTree(folders,
            [new FolderShortcut(FolderText.ProfileLabel, files, FolderText.ShortcutGlyph), new FolderShortcut(FolderText.SplashLabel, splash, FolderText.SplashGlyph)]);

        Assert.Equal(["profile", "splash", .. Roots], Names(tree));
        Assert.Equal(FolderText.CollapsedGlyph + " ⌂ profile", FolderText.RowMarkup(tree.Visible[0]));
        Assert.Equal(FolderText.CollapsedGlyph + " ▣ splash", FolderText.RowMarkup(tree.Visible[1]));
        Assert.Null(tree.Visible[2].ShortcutGlyph);

        Assert.Equal(2, tree.ExpandTo(P(splash, "comfy")));
        Assert.Equal(["profile", "splash", "comfy", .. Roots], Names(tree));
        Assert.Equal(1, tree.RootOf(2));
    }

    [Fact]
    public void Words_ArePinned()
    {
        Assert.Equal('-', FolderText.CollapseAllKey);
        Assert.Equal("📂 Working directory kept.", FolderText.KeptNotice);
        Assert.Equal("Cannot read X.", FolderText.DeniedNotice("X"));
        Assert.Equal(Theme.DimMarkup(@"C:\x [y]"), FolderText.PathMarkup(@"C:\x [y]"));
    }
}
