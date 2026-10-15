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
            "settings", "profile", "themes", "webui", "tools", "mcp", "skills", "sys", "sessions", "usage", "memory", "prompts", "queue", "ask", "cmdlist", "police",
            "shell", "files", "image", "web", "advisors", "docker", "eventlog", "obsidian", "sql", "oracle", "mysql", "sqlite", "postgres", "unc", "ha", "comfy", "camera", "print",
            "log", "process", "comfyview", "comfythumbs", "youtube", "browserlinks", "shoppinglist", "path", "model",
        ], ToolbarItems.Names);   // the model's name after the path (2026-10-12, the user's ask); "perf" before the path until it was retired (2026-10-14)
        Assert.Equal("⚙️ 🪪 🧮 📱 🛠️ 🔌 🎓 🎭 💬 📊 💾 📣 ⏳ 🙋 🔒 👮 🐚 📁 🌄 🌐 🦉 🐳 🪵 💎 🛢️ 🔮 🐬 🪶 🐘 🔗 🏠 🎨 📸 🖨️ 📄 ⚡ 🎞️ 🪟 📺 ⭐ 🛒 📂", string.Join(" ", ToolbarItems.Names.SkipLast(1).Select(ToolbarItems.Glyph)));   // the folder since 2026-09-29
        Assert.Equal("", ToolbarItems.Glyph(ToolbarItems.Model));   // no glyph: the name itself
        Assert.Equal(ChatScreen.ToolbarStrip, string.Join(" ", ToolbarItems.Names.Where(id => id is not ("cmdlist" or "police" or "path" or "model")).Select(ToolbarItems.Glyph)));   // one source for the glyphs
        Assert.Equal(FolderText.FolderGlyph, ToolbarItems.Glyph(ToolbarItems.Path));   // the Folders pane's, one source
        Assert.All(ToolbarItems.Names.SkipLast(2), id => Assert.Equal(ChatScreen.ToolbarWord(ToolbarItems.Glyph(id)), ToolbarItems.Describe(id)));   // the note is the line the glyph runs
        Assert.Equal(ChatScreen.CwdBrowseLine, ToolbarItems.Describe(ToolbarItems.Path));
        Assert.Equal("/server", ToolbarItems.Describe(ToolbarItems.Model));
        Assert.Equal("Model name", ToolbarItems.Title(ToolbarItems.Model));
        Assert.Equal("Model name: Qwen3-30B · double-click to open", ToolbarItems.ModelNote("Qwen3-30B"));
        Assert.DoesNotContain(ToolbarItems.Model, ToolbarItems.Defaults);
        Assert.Equal(ToolsText.SwitchWords.Where(w => w != ToolsText.ShoppingSwitchWord && w != ToolsText.TimersSwitchWord), ToolbarItems.Names.Where(id => ToolsText.SwitchField(id) is not null));
        Assert.Equal([ToolbarItems.Files, ToolbarItems.ShoppingList], ToolbarItems.Resolve(["files", "Shopping"]).Order(StringComparer.Ordinal));   // a saved "shopping" (🛒's id for a few hours) is the list's item   // each switch's id is its /tools word; shopping's 🛒 opens the list instead (2026-10-11)
    }

    [Fact]
    public void Resolve_NullIsTheDefaults_EmptyIsNone_UnknownWordsGo()
    {
        // Settings, Tools, Skills, Memory, the lock, the officer, the Shell, Files and Web switches and the path since 2026-10-05
        // (the user's pick; the last seven alone from 2026-10-03; the lock, the officer and the path from 2026-10-02; Settings, Tools, Skills, Sessions and the path from later on 2026-09-29, every
        // item before).
        Assert.Equal(["settings", "tools", "skills", "memory", "prompts", "queue", "cmdlist", "police", "shell", "files", "image", "web", "path"], ToolbarItems.Defaults);   // 🌄 since 2026-10-10, ⏳ since 2026-10-13, 📣 since 2026-10-14
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

        Assert.Equal(["on", "off", "edit"], ToolbarItems.Words);   // edit since 2026-10-12
        Assert.Equal("show or hide the toolbar", ToolbarItems.HelpSummary);   // its forms are /help's third column since 2026-10-05
        Assert.Equal("(toolbar off)", ToolbarItems.Notice(false));
        Assert.Equal("(toolbar on)", ToolbarItems.Notice(true));
        Assert.Equal("/toolbar takes on, off or edit, or nothing to toggle.", ToolbarItems.UsageError);   // edit since 2026-10-12
    }

    [Fact]
    public void Save_TheDefaultsAreNull_ElseStripOrder_EveryItemAFullList()
    {
        Assert.Null(ToolbarItems.Save(new HashSet<string> { "path", "web", "image", "files", "shell", "police", "cmdlist", "queue", "prompts", "memory", "skills", "tools", "settings" }));
        Assert.Equal(ToolbarItems.Names, ToolbarItems.Save(ToolbarItems.Names.ToHashSet()));
        Assert.Equal([], ToolbarItems.Save(new HashSet<string>()));
        Assert.Equal(["usage", "path"], ToolbarItems.Save(new HashSet<string> { "path", "usage" }));
    }

    /// <summary>
    /// ⭐ Saved links (later on 2026-10-13, the user's ask and pick): after 📺 in the default order, beside 📺 wherever a saved order moved
    /// it, unchecked in a saved checklist and not among the defaults, double-clicked to <c>/browser list</c>.
    /// </summary>
    [Fact]
    public void TheSavedLinksItem_FollowsYouTube_OffByDefault()
    {
        var saved = ToolbarItems.Names.Where(n => n != ToolbarItems.BrowserLinks && n != ToolbarItems.YouTube).Prepend(ToolbarItems.YouTube).ToList();
        Assert.Equal([ToolbarItems.YouTube, ToolbarItems.BrowserLinks], ToolbarItems.Order(saved).Take(2));
        Assert.DoesNotContain(ToolbarItems.BrowserLinks, ToolbarItems.Defaults);
        Assert.Equal("⭐", ToolbarItems.Glyph(ToolbarItems.BrowserLinks));
        Assert.Equal("Saved links", ToolbarItems.Title(ToolbarItems.BrowserLinks));
        Assert.Equal("/browser list", ToolbarItems.Describe(ToolbarItems.BrowserLinks));
        Assert.Equal("/browser list", ChatScreen.ToolbarWord(ChatScreen.BrowserLinksToolGlyph));
    }

    [Fact]
    public void Value_AndLabels_ArePinned()
    {
        Assert.Equal("13 of 43", ToolbarItems.Value(null));   // the defaults (of 42 until 🪵, 2026-10-15; of 41 since 📈 went, 2026-10-14; 📣 2026-10-14; ⏳ 2026-10-13, of 40 with it; 🌄 2026-10-10; 2026-10-05; 7 from 2026-10-03, 3 from 2026-10-02, of 13 until 2026-10-03; of 37 since 🙋, 2026-10-09; of 39 since Model name, 2026-10-12)
        Assert.Equal("all", ToolbarItems.Value([.. ToolbarItems.Names]));
        Assert.Equal("off", ToolbarItems.Value([]));
        Assert.Equal("2 of 43", ToolbarItems.Value(["usage", "path"]));
        Assert.Equal("[[x]] ⚙️  Settings                " + Theme.DimMarkup("/settings"), ToolbarItems.Label("settings", true));
        Assert.Equal("[[ ]] 📂  Working directory path  " + Theme.DimMarkup("/cwd browse"), ToolbarItems.Label("path", false));
        Assert.Equal("[[x]] 💾  Memory                  " + Theme.DimMarkup("/memory"), ToolbarItems.Label("memory", true));
        Assert.Equal("[[ ]] 🙋  Ask                     " + Theme.DimMarkup("/tools ask"), ToolbarItems.Label("ask", false));   // 2026-10-09, the user's ask
        Assert.Equal("[[x]] 🪪  Profile                 " + Theme.DimMarkup("/profile"), ToolbarItems.Label("profile", true));
        Assert.DoesNotContain("perf", ToolbarItems.Resolve(["perf", "shell"]));   // 📈 Performance until it was retired (2026-10-14): a saved one is dropped
        Assert.DoesNotContain("perf", ToolbarItems.Order(["perf", "shell"]));
        // The tool switches, the log and the viewers (2026-10-03, the user's names).
        Assert.Equal("[[x]] 🐚  Shell                   " + Theme.DimMarkup("/tools shell"), ToolbarItems.Label("shell", true));
        Assert.DoesNotContain("claude", ToolbarItems.Resolve(["claude", "shell"]));   // ✴️ the Claude Code advisor until it was retired (2026-10-10): a saved one is dropped
        Assert.Equal("[[ ]] 🛢️  SQL                     " + Theme.DimMarkup("/tools sql"), ToolbarItems.Label("sql", false));   // 🪟 until 2026-10-04
        // Themes and the Comfy thumb viewer (2026-10-04, the user's ask).
        Assert.Equal("[[ ]] 🧮  Themes                  " + Theme.DimMarkup("/theme"), ToolbarItems.Label("themes", false));
        Assert.Equal("[[ ]] 🪟  Comfy thumb viewer      " + Theme.DimMarkup("/comfy thumbs"), ToolbarItems.Label("comfythumbs", false));
        Assert.Equal("[[ ]] 🏠  Home                    " + Theme.DimMarkup("/tools ha"), ToolbarItems.Label("ha", false));   // "HA" until 2026-10-09 (the user's ask)
        Assert.Equal("[[ ]] 🎨  ComfyUI                 " + Theme.DimMarkup("/tools comfy"), ToolbarItems.Label("comfy", false));
        Assert.Equal("[[ ]] 📄  Log                     " + Theme.DimMarkup("/log"), ToolbarItems.Label("log", false));
        Assert.Equal("[[ ]] ⚡  Process                 " + Theme.DimMarkup("/process"), ToolbarItems.Label("process", false));   // 2026-10-05, the user's ask
        Assert.Equal("[[ ]] 📺  YouTube                 " + Theme.DimMarkup("/youtube list"), ToolbarItems.Label("youtube", false));   // the Camera live viewer's place until 2026-10-08
        Assert.Equal("[[ ]] 🎞️  Comfy viewer            " + Theme.DimMarkup("/comfy view"), ToolbarItems.Label("comfyview", false));
    }
}
