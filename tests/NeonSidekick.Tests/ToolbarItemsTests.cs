using NeonSidekick.App;
using NeonSidekick.UI;
using Xunit;

namespace NeonSidekick.Tests;

/// <summary><c>Show toolbar</c>'s checklist (2026-09-29, the user's ask): the items, their glyphs, rows, and how a saved list reads.</summary>
public sealed class ToolbarItemsTests
{
    [Fact]
    public void TheItems_AreTheStripInOrder_ThePathLast()
    {
        Assert.Equal(["settings", "tools", "mcp", "skills", "sys", "sessions", "usage", "memory", "cmdlist", "police", "path"], ToolbarItems.Names);
        Assert.Equal(ChatScreen.ToolbarStrip, string.Join(" ", ToolbarItems.Names.Take(7).Select(ToolbarItems.Glyph)));   // one source for the glyphs
        Assert.Equal([ChatScreen.MemoryToolGlyph, ChatScreen.CmdAskToolGlyph, ChatScreen.PoliceToolGlyph, "📂"], ToolbarItems.Names.Skip(7).Select(ToolbarItems.Glyph));   // the folder since 2026-09-29
        Assert.Equal(FolderText.FolderGlyph, ToolbarItems.Glyph(ToolbarItems.Path));   // the Folders pane's, one source
        Assert.All(ToolbarItems.Names.Take(10), id => Assert.Equal(ChatScreen.ToolbarWord(ToolbarItems.Glyph(id)), ToolbarItems.Describe(id).Split(' ')[0]));   // the note names what the glyph opens
        Assert.Equal(ChatScreen.CwdBrowseLine, ToolbarItems.Describe(ToolbarItems.Path)[..ChatScreen.CwdBrowseLine.Length]);
    }

    [Fact]
    public void Resolve_NullIsTheDefaults_EmptyIsNone_UnknownWordsGo()
    {
        // The defaults narrowed later on 2026-09-29 (the user's pick; every item before).
        Assert.Equal(["settings", "tools", "skills", "sessions", "path"], ToolbarItems.Defaults);
        Assert.Equal(ToolbarItems.Defaults, ToolbarItems.Names.Where(ToolbarItems.Resolve(null).Contains));
        Assert.Equal(ToolbarItems.Names, ToolbarItems.Names.Where(ToolbarItems.Resolve([.. ToolbarItems.Names]).Contains));
        Assert.Empty(ToolbarItems.Resolve([]));
        Assert.Equal(["tools", "usage", "path"], ToolbarItems.Names.Where(ToolbarItems.Resolve([" PATH ", "usage", "Tools", "nonsense"]).Contains));
    }

    [Fact]
    public void Save_TheDefaultsAreNull_ElseStripOrder_EveryItemAFullList()
    {
        Assert.Null(ToolbarItems.Save(new HashSet<string> { "path", "sessions", "skills", "tools", "settings" }));
        Assert.Equal(ToolbarItems.Names, ToolbarItems.Save(ToolbarItems.Names.ToHashSet()));
        Assert.Equal([], ToolbarItems.Save(new HashSet<string>()));
        Assert.Equal(["usage", "path"], ToolbarItems.Save(new HashSet<string> { "path", "usage" }));
    }

    [Fact]
    public void Value_AndLabels_ArePinned()
    {
        Assert.Equal("5 of 11", ToolbarItems.Value(null));   // the defaults (2026-09-29)
        Assert.Equal("all", ToolbarItems.Value([.. ToolbarItems.Names]));
        Assert.Equal("off", ToolbarItems.Value([]));
        Assert.Equal("2 of 11", ToolbarItems.Value(["usage", "path"]));
        Assert.Equal("[[x]] ⚙️  Settings                " + Theme.DimMarkup("/settings"), ToolbarItems.Label("settings", true));
        Assert.Equal("[[ ]] 📂  Working directory path  " + Theme.DimMarkup("/cwd browse · at the row's right"), ToolbarItems.Label("path", false));
        Assert.Equal("[[x]] 💾  Memory                  " + Theme.DimMarkup("/memory · while Memory is on"), ToolbarItems.Label("memory", true));
    }
}
