namespace NeonSidekick.Memory;

/// <summary>
/// The memory area's user-visible strings (2026-11-21, feature idea #6, project memories): the folder's words. The older memory texts stay
/// where they were written.
/// </summary>
public static class MemoryText
{
    /// <summary>A duplicate whose saved copy is for another folder: it names where it applies, so a save never reads as done while the fact stays out.</summary>
    public static string DuplicateElsewhere(string text, string folder) =>
        $"already saved, for the {Leaf(folder)} folder only (/memory › Enter › Everywhere widens it): {text}";

    /// <summary>The model's answer for a save under <c>here</c>.</summary>
    public static string RememberedHere(string text) => "remembered for this folder only: " + text;

    /// <summary>The pane's dim mark on a memory for this folder, and on one for another.</summary>
    public static string HereMark(string folder) => Leaf(folder) + " folder";

    public const string OtherFolderMark = "another folder";

    /// <summary>The footer's line under a folder memory's text.</summary>
    public static string FolderFooter(string folder) => "Only in " + folder;

    /// <summary>The action page's two choices.</summary>
    public const string ThisFolderOnly = "This folder only";

    public const string Everywhere = "Everywhere";

    /// <summary>What <c>/memory remember --here</c> says it did.</summary>
    public static string RememberedHereNotice(string text) => $"({App.NoticeGlyphs.Memory}remembered for this folder only: {text})";

    /// <summary>The <c>--here</c> word of <c>/memory remember</c>.</summary>
    public const string HereFlag = "--here";

    /// <summary>The scope moved, from the pane.</summary>
    public static string MovedHere(string text) => $"({App.NoticeGlyphs.Memory}this folder only: {text})";

    public static string MovedEverywhere(string text) => $"({App.NoticeGlyphs.Memory}everywhere: {text})";

    /// <summary>The folder's last part, for a mark that names no full path; the path itself for a root.</summary>
    public static string Leaf(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return name.Length > 0 ? name : folder;
    }
}
