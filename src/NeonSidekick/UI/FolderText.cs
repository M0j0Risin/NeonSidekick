using NeonSidekick.Files;
using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>The <c>/cwd browse</c> pane's words (<see cref="FolderPane"/>, 2026-09-21), pinned by <c>FolderPaneTests</c>.</summary>
public static class FolderText
{
    /// <summary>The strip's label.</summary>
    public const string Title = "Folders";

    /// <summary>The one button on the strip, drawn as a dim tab: every node closed, the cursor on its root.</summary>
    public const string CollapseAllButton = "⊟ collapse all";

    /// <summary>The key that is the button.</summary>
    public const char CollapseAllKey = '-';

    /// <summary>The hint row under the pane.</summary>
    public const string Hint = "Enter = choose · Space = expand/collapse · - = collapse all · ESC = back";

    /// <summary>A node with subfolders to show, or not yet read.</summary>
    public const string CollapsedGlyph = "▸";

    /// <summary>A node whose subfolders are listed under it.</summary>
    public const string ExpandedGlyph = "▾";

    /// <summary>A node known to have nothing under it, or one the source could not read.</summary>
    public const string LeafGlyph = "·";

    /// <summary>Ahead of the <see cref="ProfileLabel"/> shortcut's label: the house.</summary>
    public const string ShortcutGlyph = "⌂";

    /// <summary>
    /// Ahead of the <see cref="SplashLabel"/> shortcut's label (2026-09-24): the framed square, a picture
    /// frame — the user's pick over ✦ ❖ ☼. One cell, like the house; an emoji (🖼) is two and would
    /// push the label off the house's column.
    /// </summary>
    public const string SplashGlyph = "▣";

    /// <summary>The profile's own <c>files</c> folder's label at the top of the tree (later on 2026-09-21, the user's word).</summary>
    public const string ProfileLabel = "profile";

    /// <summary>The profile's <c>splash</c> folder's label, the row under <see cref="ProfileLabel"/> (2026-09-24, the user's ask).</summary>
    public const string SplashLabel = "splash";

    /// <summary>Each depth's indent, ahead of the glyph.</summary>
    public const int IndentCells = 2;

    /// <summary>The transcript's line when the pane closes with nothing picked.</summary>
    public const string KeptNotice = App.NoticeGlyphs.Folder + "Working directory kept.";   // the folder since 2026-09-22

    /// <summary>The transcript's line for <c>/cwd browse</c> with no pane to open (a redirected console).</summary>
    public const string NeedsPaneNotice = "/cwd browse needs the interactive screen.";

    /// <summary>The <c>/cwd</c> completion's note on the browse word.</summary>
    public const string BrowseNote = "pick a folder on the screen";

    /// <summary>The status line after an expand the source refused.</summary>
    public static string DeniedNotice(string path) => "Cannot read " + path + ".";

    /// <summary>The glyph for <paramref name="node"/> as it stands.</summary>
    public static string Glyph(FolderNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.IsLeaf ? LeafGlyph : node.Expanded ? ExpandedGlyph : CollapsedGlyph;
    }

    /// <summary>
    /// A row's markup past the pointer: the depth's indent, the glyph, a space, the name (the
    /// shortcut's own glyph and a space ahead of a shortcut's label) — escaped, dim for a denied node. The
    /// pointer and the highlight are the menu's (<see cref="MenuPane.RowMarkup"/>).
    /// </summary>
    public static string RowMarkup(FolderNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string text = new string(' ', node.Depth * IndentCells) + Glyph(node) + " " + (node.ShortcutGlyph is { } mark ? mark + " " : "") + node.Name;
        // DimMarkup escapes for itself.
        return node.Denied ? Theme.DimMarkup(text) : Markup.Escape(text);
    }

    /// <summary>The first column past the glyph and its space on a row at <paramref name="depth"/>: a click before it toggles the node, one on it or after moves the cursor.</summary>
    public static int NameColumn(int depth) => MenuPane.NoPointer.Length + depth * IndentCells + 2;

    /// <summary>The row under the strip: the highlighted node's full path, dim — what Enter chooses.</summary>
    public static string PathMarkup(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Theme.DimMarkup(path);
    }
}
