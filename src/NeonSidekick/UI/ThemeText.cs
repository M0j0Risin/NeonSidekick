using System.Globalization;

namespace NeonSidekick.UI;

/// <summary>
/// The user-visible wording of the theme files (2026-10-01, the user's ask: themes of their own in
/// <c>&lt;home&gt;/themes</c>; the built-ins are files too since 2026-10-05): what a file that fails to load says, and
/// <c>/theme export</c>'s lines.
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
    /// <summary>A file that leaves colour roles unset or unusable (2026-10-05: a theme stands alone, every role its own).</summary>
    public static string MissingColors(IReadOnlyList<string> roles) =>
        string.Create(CultureInfo.InvariantCulture, $"skipped: it does not set {string.Join(", ", roles)} (a theme sets all {ThemeKeys.Colors.Count} colors; /theme export writes a full file)");

    /// <summary>An embedded theme file not in exactly one category folder of <c>assets/themes</c> (<see cref="ThemeLibrary"/>).</summary>
    public const string NotInCategory = "skipped: a built-in theme sits in one category folder of assets/themes";

    /// <summary>The built-in default did not load (a build that skipped the tests); <see cref="ThemePalette.Emergency"/> stands in.</summary>
    public static string NoDefault(string name) => $"The default theme \"{name}\" did not load; a plain stand-in is in force.";

    /// <summary>A file that still names a base (read until 2026-10-05).</summary>
    public static string BaseIgnored(string baseName) => $"\"base\": \"{baseName}\" is no longer read, ignored (a theme sets every color itself)";
    public static string UnknownColor(string key) => $"\"{key}\" is not a color role, ignored (roles: {string.Join(", ", ThemeKeys.Colors)})";
    public static string UnknownStyle(string key) => $"\"{key}\" is not a style, ignored";
    public static string BadColor(string where, string? value) => $"{where}: \"{value}\" is not a color (#RRGGBB, #RGB or a role), ignored";
    public static string BadGradient(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"the gradient has {count} stops; it takes 2 to {ThemeFile.MaxGradientStops}, ignored");

    public static string ExportDone(string name, string path) => $"Theme {name} written to {path}. Edit it, then /theme {name}.";
    public static string ExportOverrides(string name, string path) => $"Theme {name} written to {path}; it now replaces the built-in {name}. Edit it, then /theme {name}.";
    public static string ExportExists(string path) => $"{path} already exists; give a new name: /theme export <name> <new-name>.";
    public static string ExportNameTaken(string name) => $"A theme named \"{name}\" already exists; give another new name.";
    public static string ExportBadName(string name) => $"\"{name}\" is not a theme name: lower-case letters, digits, - and _, up to 32, and not \"{ExportWord}\" or \"{EditWord}\".";
    public static string ExportFailed(string message) => $"Could not write the theme: {message}";

    // ── /theme edit (2026-11-16, plan sharded-hugging-puppy #54) ──────────────

    /// <summary><c>/theme</c>'s sub-command that opens the theme editor.</summary>
    public const string EditWord = "edit";

    /// <summary>The note beside <see cref="EditWord"/> in <c>/theme</c>'s argument list.</summary>
    public const string EditNote = "edit a theme's colors";

    /// <summary>The editor's title.</summary>
    public static string EditTitle(string name) => $"Edit theme · {name}";

    /// <summary>The editor's hint row.</summary>
    public const string EditHint = "↑↓ = color · ←→ = lighter/darker (Shift ×5) · Enter = picker · s = save · r = reset · Esc = close";

    /// <summary>The editor's buttons, with their keys.</summary>
    public const string SaveButton = "save";
    public const string ResetButton = "reset";
    public const string DiscardButton = "discard";

    /// <summary>What each color role colors, in <see cref="ThemeColorSlot"/> order: the editor's row notes and <c>COMMANDS.md</c>'s table say the same.</summary>
    public static readonly IReadOnlyList<string> RoleNotes =
    [
        "the main accent: headings, borders, keywords",
        "the counter-accent: your lines, table headers, the selection",
        "the third accent: section headings, bullets, function calls",
        "structure: the pane rule, the slab under an off toolbar item",
        "string literals",
        "numbers",
        "a soft accent: $variables",
        "body text",
        "dim text, the status and hints bars",
        "a step quieter than dim: the input row's ghost text",
        "the background",
        "code blocks, the highlighted menu row",
        "success, enabled, connected",
        "failure, error",
        "a warning",
    ];

    /// <summary>The editor's band when every contrast bar is met.</summary>
    public const string ChecksOk = "Contrast OK: every pair meets its bar.";

    /// <summary>The editor's band listing the pairs under their bars (<see cref="ThemeChecks"/>); warnings, never a block on saving.</summary>
    public static string ChecksWarning(IReadOnlyList<ContrastProblem> problems) =>
        "⚠ " + string.Join(" · ", problems.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Pair} {p.Ratio:0.0}:1, wants {p.Target:0.##}")));

    /// <summary>A built-in named: the copy to make and edit instead.</summary>
    public static string EditBuiltInQuestion(string name, string copy) => $"Built-in themes can't be changed. Save a copy as {copy} and edit it?";

    /// <summary>A built-in named whose copy already exists: edit that one.</summary>
    public static string EditCopyQuestion(string name, string copy) => $"Built-in themes can't be changed. Edit your copy of {name}, {copy}?";

    /// <summary>Esc with changes not saved.</summary>
    public const string EditDiscardQuestion = "Close the editor and lose the changes?";

    /// <summary>The file changed on disk since the editor opened.</summary>
    public static string EditChangedQuestion(string path) => $"{path} changed since the editor opened. Save over it?";

    /// <summary>The save landed.</summary>
    public static string EditSaved(string name, string path) => $"Theme {name} saved to {path}.";

    /// <summary>The save landed on a theme not in force.</summary>
    public static string EditSavedNotInForce(string name, string path) => $"Theme {name} saved to {path}. /theme {name} puts it in force.";

    /// <summary>The save failed.</summary>
    public static string EditFailed(string message) => $"Could not save the theme: {message}";

    /// <summary>Nothing changed, nothing saved.</summary>
    public const string EditUnchanged = "No changes to save.";

    /// <summary>The editor has no pane to open in.</summary>
    public const string EditNeedsPane = "The theme editor needs the full screen and its bottom pane, which this terminal doesn't have.";

    /// <summary>The color picker's title: the role, its candidate value and the value it opened with.</summary>
    public static string PickerTitle(string themeName, string role, string was) => $"Edit theme · {themeName} › {role} (was {was})";

    /// <summary>The picker's bar labels, then the hex row's.</summary>
    public const string PickerHue = "Hue";
    public const string PickerSaturation = "Saturation";
    public const string PickerLightness = "Lightness";
    public const string PickerHex = "Hex value";

    /// <summary>The picker's hint row.</summary>
    public const string PickerHint = "↑↓ = bar · ←→ = move (Shift ×5) · Enter = use · Esc = cancel";

    /// <summary>A typed value that is no color.</summary>
    public static string PickerBadHex(string text) => $"\"{text}\" is not a color: #RRGGBB or #RGB.";

    // ── The pickers' preview (2026-10-02, the user's ask; ThemePreview) ──────
    // A small mock screen in the highlighted theme, the Theme Atlas's in brief: words that put every role on show.
    public const string PreviewUser = "› give the banner one color per letter";
    public const string PreviewHeading = "Letter-perfect gradients";
    public const string PreviewProse1 = "Each letter takes a ";
    public const string PreviewProseBold = "stop";
    public const string PreviewProse2 = " of the ";
    public const string PreviewProseCode = "gradient";
    public const string PreviewProse3 = ", the ";
    public const string PreviewProseItalic = "last";
    public const string PreviewProse4 = " one too.";
    public const string PreviewBullet = "stop 0 colors the rule's left end";
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
    public const string PreviewWarn = "! orphan.json: skipped, it does not set bg";
    public const string PreviewError = "✗ No theme named \"matrix\".";
    public const string PreviewPaste = "[Pasted text #1 +12 lines]";
    // The menu row's value (2026-10-16, the contrast review): a changed setting's value, as /settings draws it over the cursor's bar.
    public const string PreviewMenuValue = "on";
    public const string PreviewHint = "Esc to cancel · 1.2k tokens · ";
    public const string PreviewTrailer = " gemma-4";
    public const string PreviewPlaceholder = "Type a message or /help for more info";

    /// <summary>The preview's notice line, naming the theme shown.</summary>
    public static string PreviewNotice(string name) => "· Theme: " + name;
}
