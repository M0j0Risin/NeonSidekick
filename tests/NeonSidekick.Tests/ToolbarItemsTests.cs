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
        // The ID card after the gear and the rising chart after the Usage chart (later on 2026-09-29, the user's ask); the tool
        // switches, the log and the two viewers in the user's order, the chart moved behind the log (2026-10-03) and behind the viewers later that day, the last glyph.
        Assert.Equal(
        [
            "settings", "profile", "tools", "mcp", "skills", "sys", "sessions", "usage", "memory", "cmdlist", "police",
            "shell", "files", "web", "claude", "docker", "obsidian", "sql", "oracle", "mysql", "sqlite", "postgres", "unc", "ha", "comfy", "camera", "print",
            "log", "liveview", "comfyview", "perf", "path",
        ], ToolbarItems.Names);
        Assert.Equal("⚙️ 🪪 🛠️ 🔌 🎓 🎭 💬 📊 💾 🔒 👮 🐚 📁 🌐 ✴️ 🐳 💎 🪟 🔮 🐬 🪶 🐘 🔗 🏠 🎨 📸 🖨️ 📄 📺 🎞️ 📈 📂", string.Join(" ", ToolbarItems.Names.Select(ToolbarItems.Glyph)));   // the folder since 2026-09-29
        Assert.Equal(ChatScreen.ToolbarStrip, string.Join(" ", ToolbarItems.Names.Where(id => id is not ("cmdlist" or "police" or "path")).Select(ToolbarItems.Glyph)));   // one source for the glyphs
        Assert.Equal(FolderText.FolderGlyph, ToolbarItems.Glyph(ToolbarItems.Path));   // the Folders pane's, one source
        Assert.All(ToolbarItems.Names.SkipLast(1), id => Assert.Equal(ChatScreen.ToolbarWord(ToolbarItems.Glyph(id)), ToolbarItems.Describe(id)));   // the note is the line the glyph runs
        Assert.Equal(ChatScreen.CwdBrowseLine, ToolbarItems.Describe(ToolbarItems.Path));
        Assert.Equal(ToolsText.SwitchWords, ToolbarItems.Names.Where(id => ToolsText.SwitchField(id) is not null));   // each switch's id is its /tools word
    }

    [Fact]
    public void Resolve_NullIsTheDefaults_EmptyIsNone_UnknownWordsGo()
    {
        // Tools, the lock, the officer, the Shell, Files and Web switches and the path since 2026-10-03 (the user's pick; the lock,
        // the officer and the path from 2026-10-02; Settings, Tools, Skills, Sessions and the path from later on 2026-09-29, every
        // item before).
        Assert.Equal(["tools", "cmdlist", "police", "shell", "files", "web", "path"], ToolbarItems.Defaults);
        Assert.Equal(ToolbarItems.Defaults, ToolbarItems.Names.Where(ToolbarItems.Resolve(null).Contains));
        Assert.Equal(ToolbarItems.Names, ToolbarItems.Names.Where(ToolbarItems.Resolve([.. ToolbarItems.Names]).Contains));
        Assert.Empty(ToolbarItems.Resolve([]));
        Assert.Equal(["tools", "usage", "path"], ToolbarItems.Names.Where(ToolbarItems.Resolve([" PATH ", "usage", "Tools", "nonsense"]).Contains));
    }

    [Fact]
    public void Toggle_HidesKeepingTheItems_ShowsThemAgain_TheDefaultsTheFirstTime()
    {
        // /toolbar (later on 2026-09-30, the user's ask), PerfBarMode.Toggle's shape.
        var hiddenDefaults = ToolbarItems.Toggle("", null, null)!.Value;
        Assert.Empty(hiddenDefaults.Items!);
        Assert.Null(hiddenDefaults.LastItems);                                                   // the defaults, kept as null
        Assert.Null(ToolbarItems.Toggle("", [], null)!.Value.Items);                             // shown again: the defaults
        var hidden = ToolbarItems.Toggle(" ", ["usage", "tools"], null)!.Value;
        Assert.Empty(hidden.Items!);
        Assert.Equal(["tools", "usage"], hidden.LastItems);                                      // strip order, as Save writes
        Assert.Equal(["tools", "usage"], ToolbarItems.Toggle("", hidden.Items, hidden.LastItems)!.Value.Items);
        Assert.Null(ToolbarItems.Toggle("", [], ["nonsense"])!.Value.Items);                     // a last list naming nothing: the defaults
        Assert.Equal(["usage"], ToolbarItems.Toggle("ON", ["usage"], ["tools"])!.Value.Items);  // already shown: as it is
        Assert.Empty(ToolbarItems.Toggle("off", [], ["tools"])!.Value.Items!);                   // already hidden: as it is
        Assert.Equal(["tools"], ToolbarItems.Toggle("off", [], ["tools"])!.Value.LastItems);
        Assert.Equal(["tools"], ToolbarItems.Toggle("on", [], ["tools"])!.Value.Items);
        Assert.Null(ToolbarItems.Toggle("sideways", null, null));

        Assert.Equal(["on", "off"], ToolbarItems.Words);
        Assert.Equal("show or hide the toolbar, or /toolbar on|off", ToolbarItems.HelpSummary);
        Assert.Equal("(toolbar off)", ToolbarItems.Notice(false));
        Assert.Equal("(toolbar on)", ToolbarItems.Notice(true));
        Assert.Equal("/toolbar takes on or off, or nothing to toggle.", ToolbarItems.UsageError);
    }

    [Fact]
    public void Save_TheDefaultsAreNull_ElseStripOrder_EveryItemAFullList()
    {
        Assert.Null(ToolbarItems.Save(new HashSet<string> { "path", "web", "files", "shell", "police", "cmdlist", "tools" }));
        Assert.Equal(ToolbarItems.Names, ToolbarItems.Save(ToolbarItems.Names.ToHashSet()));
        Assert.Equal([], ToolbarItems.Save(new HashSet<string>()));
        Assert.Equal(["usage", "path"], ToolbarItems.Save(new HashSet<string> { "path", "usage" }));
    }

    [Fact]
    public void Value_AndLabels_ArePinned()
    {
        Assert.Equal("7 of 32", ToolbarItems.Value(null));   // the defaults (2026-10-03; 3 from 2026-10-02, of 13 until 2026-10-03)
        Assert.Equal("all", ToolbarItems.Value([.. ToolbarItems.Names]));
        Assert.Equal("off", ToolbarItems.Value([]));
        Assert.Equal("2 of 32", ToolbarItems.Value(["usage", "path"]));
        Assert.Equal("[[x]] ⚙️  Settings                " + Theme.DimMarkup("/settings"), ToolbarItems.Label("settings", true));
        Assert.Equal("[[ ]] 📂  Working directory path  " + Theme.DimMarkup("/cwd browse"), ToolbarItems.Label("path", false));
        Assert.Equal("[[x]] 💾  Memory                  " + Theme.DimMarkup("/memory"), ToolbarItems.Label("memory", true));
        Assert.Equal("[[x]] 🪪  Profile                 " + Theme.DimMarkup("/profile"), ToolbarItems.Label("profile", true));
        Assert.Equal("[[ ]] 📈  Performance             " + Theme.DimMarkup("/perfbar"), ToolbarItems.Label("perf", false));   // the command alone (2026-10-02)
        // The tool switches, the log and the viewers (2026-10-03, the user's names).
        Assert.Equal("[[x]] 🐚  Shell                   " + Theme.DimMarkup("/tools shell"), ToolbarItems.Label("shell", true));
        Assert.Equal("[[ ]] ✴️  Claude                  " + Theme.DimMarkup("/tools claude"), ToolbarItems.Label("claude", false));
        Assert.Equal("[[ ]] 🪟  SQL                     " + Theme.DimMarkup("/tools sql"), ToolbarItems.Label("sql", false));
        Assert.Equal("[[ ]] 🏠  HA                      " + Theme.DimMarkup("/tools ha"), ToolbarItems.Label("ha", false));
        Assert.Equal("[[ ]] 🎨  ComfyUI                 " + Theme.DimMarkup("/tools comfy"), ToolbarItems.Label("comfy", false));
        Assert.Equal("[[ ]] 📄  Log                     " + Theme.DimMarkup("/log"), ToolbarItems.Label("log", false));
        Assert.Equal("[[ ]] 📺  Live viewer             " + Theme.DimMarkup("/camera live"), ToolbarItems.Label("liveview", false));
        Assert.Equal("[[ ]] 🎞️  Comfy viewer            " + Theme.DimMarkup("/comfy view"), ToolbarItems.Label("comfyview", false));
    }
}
