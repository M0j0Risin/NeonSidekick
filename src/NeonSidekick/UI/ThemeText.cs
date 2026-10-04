using System.Globalization;

namespace NeonSidekick.UI;

/// <summary>
/// The user-visible wording of the user themes (2026-10-01, the user's ask: themes of their own in
/// <c>&lt;home&gt;/themes</c>): what a file that fails to load says, and <c>/theme export</c>'s lines.
/// </summary>
public static class ThemeText
{
    /// <summary>The note of a user theme whose file gives none.</summary>
    public const string CustomDescription = "custom theme";

    /// <summary><c>/theme</c>'s sub-command that writes a theme out as a starting file.</summary>
    public const string ExportWord = "export";

    /// <summary>The note beside <see cref="ExportWord"/> in <c>/theme</c>'s argument list.</summary>
    public const string ExportNote = "write a theme to the themes folder to edit";

    /// <summary>What <c>/theme export</c> with no name or too many words says.</summary>
    public const string ExportUsage = "/theme export <name> [new-name] writes the theme to the themes folder as a file to edit.";

    /// <summary>One problem of one file, as the log and the transcript show it; <paramref name="shown"/> names the file (its path under the themes folder since 2026-10-02, when subfolders came in).</summary>
    public static string Problem(string shown, string problem) =>
        string.Create(CultureInfo.InvariantCulture, $"{shown}: {problem}");

    public static string Unreadable(string message) => $"skipped: cannot read it ({message})";
    public static string NotJson(string message) => $"skipped: not a theme file ({message})";
    public static string BadName(string name) => $"skipped: \"{name}\" is not a theme name (lower-case letters, digits, - and _, up to 32, and not \"{ExportWord}\")";
    public static string Duplicate(string name, string otherShown) => $"skipped: {otherShown} already names a theme \"{name}\"";
    public static string NoBase(string baseName) => $"skipped: its base \"{baseName}\" is no theme";
    public static string BaseCycle(string baseName) => $"skipped: its base \"{baseName}\" leads back to itself";
    public static string BaseFailed(string baseName) => $"skipped: its base \"{baseName}\" did not load";
    public static string UnknownColor(string key) => $"\"{key}\" is no colour role, ignored (roles: {string.Join(", ", ThemeKeys.Colors)})";
    public static string UnknownStyle(string key) => $"\"{key}\" is no style, ignored";
    public static string BadColor(string where, string? value) => $"{where}: \"{value}\" is not a colour (#RRGGBB, #RGB or a role), ignored";
    public static string BadGradient(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"the gradient has {count} stops; it takes 2 to {ThemeFile.MaxGradientStops}, ignored");

    public static string ExportDone(string name, string path) => $"Theme {name} written to {path}. Edit it, then /theme {name}.";
    public static string ExportOverrides(string name, string path) => $"Theme {name} written to {path}; it now replaces the built-in {name}. Edit it, then /theme {name}.";
    public static string ExportExists(string path) => $"{path} already exists; give a new name: /theme export <name> <new-name>.";
    public static string ExportNameTaken(string name) => $"A theme named \"{name}\" already exists; give another new name.";
    public static string ExportBadName(string name) => $"\"{name}\" is not a theme name: lower-case letters, digits, - and _, up to 32.";
    public static string ExportFailed(string message) => $"Could not write the theme: {message}";

    // ── The pickers' preview (2026-10-02, the user's ask; ThemePreview) ──────
    // A small mock screen in the highlighted theme, the Theme Atlas's in brief: words that put every role on show.
    public const string PreviewUser = "› give the banner one colour per letter";
    public const string PreviewHeading = "Letter-perfect gradients";
    public const string PreviewProse1 = "Each letter takes a ";
    public const string PreviewProseBold = "stop";
    public const string PreviewProse2 = " of the ";
    public const string PreviewProseCode = "gradient";
    public const string PreviewProse3 = ", the ";
    public const string PreviewProseItalic = "last";
    public const string PreviewProse4 = " one too.";
    public const string PreviewBullet = "stop 0 colours the rule's left end";
    public const string PreviewQuote = "The rule samples five points.";
    public const string PreviewCodeLabel = "csharp";
    // A file edit's diff (2026-10-03, the user's ask): the note an edit_file of line 3 writes, then the code block's last line
    // as it was and as it is. The summary over the rows is FileText.DiffSummary's own.
    public const string PreviewDiffNote = "edited seal.cs (line 3; now 12 lines, 40 words)";
    public const string PreviewDiffFile = "seal.cs";
    public const int PreviewDiffLine = 3;
    public const string PreviewDiffOld = "\"crimson\"";
    public const string PreviewDiffNew = "\"inkwash\"";
    public const string PreviewThinking ="thinking: the seal should be the only red.";
    public const string PreviewGood = "✓ 40 user themes loaded";
    public const string PreviewWarn = "! orphan.json: skipped, its base is no theme";
    public const string PreviewError = "✗ No theme named \"matrix\".";
    public const string PreviewPaste = "[Pasted text #1 +12 lines]";
    public const string PreviewHint = "esc to cancel · 1.2k tokens · ";
    public const string PreviewTrailer = " gemma-4";
    public const string PreviewPlaceholder = "Type a message or /help for more info";

    /// <summary>The preview's notice line, naming the theme shown.</summary>
    public static string PreviewNotice(string name) => "· Theme: " + name;
}
