namespace NeonSidekick.Memory;

/// <summary>
/// The memory area's user-visible strings (2026-11-21, feature idea #6, project memories): the folder's words. The older memory texts stay
/// where they were written.
/// </summary>
public static class MemoryText
{
    /// <summary>A duplicate whose saved copy is for another folder: it names where it applies, so a save never reads as done while the fact stays out.</summary>
    public static string DuplicateElsewhere(string text, string folder) =>
        $"already saved, for the {Leaf(folder)} folder only (/memory › Enter › everywhere widens it): {text}";

    /// <summary>
    /// The model's twin of <see cref="DuplicateElsewhere"/> (2026-11-22, the review's #113): no pane to name, but the way it can widen the fact
    /// itself. Pinned.
    /// </summary>
    public static string DuplicateElsewhereForModel(string text, string folder) =>
        $"already saved, for the {Leaf(folder)} folder only; saving it again without here makes it apply everywhere: {text}";

    /// <summary>
    /// A save for everywhere of a fact kept for another folder (2026-11-22, the review's #34, #88, the user's call): widened, and said so, the
    /// model's words and the user's.
    /// </summary>
    public static string Widened(string text, string folder) => $"remembered everywhere now (it was kept for the {Leaf(folder)} folder only): {text}";

    public static string WidenedNotice(string text, string folder) => $"({App.NoticeGlyphs.Memory}remembered everywhere now, no longer for the {Leaf(folder)} folder only: {text})";

    /// <summary>A save for this folder of a fact kept everywhere (2026-11-22, the review's #20): never narrowed; everywhere covers this folder.</summary>
    public static string CoversHere(string text) => "already remembered everywhere, which covers this folder: " + text;

    public static string CoversHereNotice(string text) => $"({App.NoticeGlyphs.Memory}already remembered everywhere, which covers this folder: {text})";

    /// <summary>A folder that isn't a full path of this system's, never saved as everywhere instead (2026-11-22, the review's #107).</summary>
    public static string FolderRefused(string folder) => $"not saved: {folder} isn't a full path on this computer";

    /// <summary>
    /// The full answers (2026-11-22, the review's #26): which ceiling, the one a folder is offered (<see cref="MemoryStore.MaxEntries"/>) or the
    /// file's (<see cref="MemoryStore.MaxStoredEntries"/>), the user's words and the model's. Pinned.
    /// </summary>
    public const string FullFolderError = "Memory is full (200 memories apply in a folder); /memory forget clears it.";

    public const string FullFileError = "Memory is full (the memory file holds 1,000); /memory forget clears it.";

    public const string FullFolderForModel = "memory is full (200 entries apply in a folder); the user can clear it with /memory forget";

    public const string FullFileForModel = "memory is full (the memory file holds 1,000 entries); the user can clear it with /memory forget";

    /// <summary>The pane's action words (2026-11-22, the review's #16: lower case, one width with edit, disable and forget).</summary>
    public const string FolderWord = "folder";

    public const string EverywhereWord = "everywhere";

    /// <summary>The pane's action rows' dim notes (2026-11-22, the review's #15: out of the menu's code). Pinned through the rows.</summary>
    public const string EditNote = "change its text";

    public const string EnableNote = "offer it to the model again";

    public const string DisableNote = "keep it, but don't offer it to the model";

    public const string ForgetNote = "remove it";

    public const string FolderNote = "offer it only while working in this folder";

    public const string EverywhereNote = "offer it in every folder";

    /// <summary>The model's answer for a save under <c>here</c>.</summary>
    public static string RememberedHere(string text) => "remembered for this folder only: " + text;

    /// <summary>The pane's dim mark on a memory for this folder, and on one for another.</summary>
    public static string HereMark(string folder) => Leaf(folder) + " folder";

    public const string OtherFolderMark = "another folder";

    /// <summary>The footer's line under a folder memory's text.</summary>
    public static string FolderFooter(string folder) => "Only in " + folder;

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
