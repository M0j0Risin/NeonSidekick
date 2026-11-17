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
    public static string BadName(string name) => $"skipped: \"{name}\" is not a theme name (lower-case letters, digits, - and _, up to 32, and not {Quoted(RefusedOnLoad)})";
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
    public static string ExportBadName(string name) => $"\"{name}\" is not a theme name: lower-case letters, digits, - and _, up to 32, and not {Quoted(SubcommandWords)}.";

    /// <summary>The default name of a theme's copy (<c>/theme export</c>'s and <c>/theme edit</c>'s, 2026-11-17, #109).</summary>
    public static string CopyName(string name) => name + "-custom";

    // ── /theme's sub-commands (2026-11-17, the admin-themes-render review's #217: one list for the handler, completion and the name rules) ──

    /// <summary><c>/theme</c>'s sub-command words, in completion's order; no new theme may take one (<see cref="ThemeFile.IsValidNewName"/>).</summary>
    public static readonly IReadOnlyList<string> SubcommandWords = [ExportWord, EditWord];

    /// <summary>The notes beside <see cref="SubcommandWords"/>, in the same order.</summary>
    public static readonly IReadOnlyList<string> SubcommandNotes = [ExportNote, EditNote];

    /// <summary>
    /// The words added after themes could already carry them: a file named so before still loads and is picked from the list
    /// (<see cref="ThemeFile.IsValidName"/>), while typed it is the verb.
    /// </summary>
    public static readonly IReadOnlyList<string> LoadableSubcommandWords = [EditWord];

    /// <summary>The words <see cref="ThemeFile.IsValidName"/> refuses: <see cref="SubcommandWords"/> less <see cref="LoadableSubcommandWords"/>.</summary>
    public static IReadOnlyList<string> RefusedOnLoad => [.. SubcommandWords.Except(LoadableSubcommandWords, StringComparer.Ordinal)];

    /// <summary>
    /// The one of <see cref="SubcommandWords"/> <paramref name="request"/> is or starts with plus a space (any case), and the text after it in
    /// <paramref name="rest"/>; null (and <paramref name="rest"/> the whole request) for none. Pure.
    /// </summary>
    public static string? Subcommand(string request, out string rest)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (string word in SubcommandWords)
        {
            if (request.Equals(word, StringComparison.OrdinalIgnoreCase) || request.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase))
            {
                rest = request[word.Length..];
                return word;
            }
        }

        rest = request;
        return null;
    }

    private static string Quoted(IReadOnlyList<string> words) => string.Join(" or ", words.Select(w => "\"" + w + "\""));
    public static string ExportFailed(string message) => $"Could not write the theme: {message}";

    // ── /theme edit (2026-11-16, plan sharded-hugging-puppy #54) ──────────────

    /// <summary><c>/theme</c>'s sub-command that opens the theme editor.</summary>
    public const string EditWord = "edit";

    /// <summary>
    /// The theme repaint's line above the first turn drawn (2026-11-17, the admin-themes-render review's #40, the user's call: about a screenful
    /// redrawn), naming the earlier turns left out.
    /// </summary>
    public static string EarlierTurns(int count) => count == 1
        ? "(1 earlier turn isn't redrawn; /sessions shows it)"
        : string.Create(CultureInfo.InvariantCulture, $"({count} earlier turns aren't redrawn; /sessions shows them)");

    /// <summary>
    /// A light theme put in force with Themed background off (2026-11-17, #179): the app can't see the terminal's own page, so the line allows
    /// for a light one.
    /// </summary>
    public const string LightNeedsBackground =
        "A light theme on the terminal's own page can be hard to read: turn on Themed background in /settings › Client › Chat, unless the terminal is light too.";

    /// <summary>The note beside <see cref="EditWord"/> in <c>/theme</c>'s argument list.</summary>
    public const string EditNote = "edit a theme's colors";

    /// <summary>The editor's title.</summary>
    public static string EditTitle(string name) => $"Edit theme · {name}";

    /// <summary>The editor's hint row.</summary>
    public const string EditHint = "↑↓ = color · ←→ = lighter/darker (Shift ×5) · Enter = picker · s = save · r = reset · d = discard · Esc = close";

    /// <summary>The editor's buttons, with their keys.</summary>
    public const string SaveButton = "save";
    public const string ResetButton = "reset";
    public const string DiscardButton = "discard";

    /// <summary>
    /// What each color role colors, in <see cref="ThemeColorSlot"/> order: <c>COMMANDS.md</c>'s table in the editor's rows (2026-11-17, #119: the
    /// docs are the fact's home), lower-case and without the Markdown marks a menu row would draw as typed.
    /// </summary>
    public static readonly IReadOnlyList<string> RoleNotes =
    [
        "the main accent: headings, borders, keywords",
        "the counter-accent: your lines, the spinner, table headers, inline code, the selection",
        "the third accent: section headings, bullets, the quote bar, function calls",
        "structure: the pane rule, table borders, the slab under a switched-off toolbar item",
        "string literals",
        "numbers",
        "a soft accent: $variables",
        "body text",
        "dim text, the status bar and the hints bar",
        "a step quieter than dim (darker on a dark theme, lighter on a light one): the input row's ghost text",
        "the background (unless Themed background is off), the picture viewer",
        "code blocks, the highlighted menu row",
        "success, enabled, connected",
        "failure, error",
        "a warning",
    ];

    /// <summary>The contrast pairs' names, in the theme file's words (2026-11-17, the admin-themes-render review's #39, #195).</summary>
    public const string PanelPair = "panelBg on bg";
    public const string DimmerPair = "dimmer on bg";
    public const string DeepPair = "deep on bg";
    public const string InkPair = "ink on bg";
    public const string InkPanelPair = "ink on panelBg";
    public const string DimPanelPair = "dim on panelBg";
    public const string BgPrimaryPair = "bg on primary";

    public static string PanelRolePair(string role) => role + " on panelBg";

    /// <summary>A code style's words on a diff slab: "codeKeyword on the good diff".</summary>
    public static string SlabPair(string key, string slab) => $"{key} on the {slab} diff";

    /// <summary>A style's words on its own ground: "markdownHeading1 on its own ground".</summary>
    public static string OwnGroundPair(string key) => key + " on its own ground";

    /// <summary>How many problems the band lists before "and N more" (2026-11-17, #146).</summary>
    public const int ChecksShown = 3;

    /// <summary>The editor's band when every contrast bar is met.</summary>
    public const string ChecksOk = "Contrast OK: every pair meets its bar.";

    /// <summary>
    /// The editor's band listing the pairs under their bars (<see cref="ThemeChecks"/>); warnings, never a block on saving.
    /// The first <see cref="ChecksShown"/>, then "and N more" (#146); a ratio is floored to one decimal, so a pair just under its bar never
    /// reads as meeting it (#106: 4.46 showed as 4.5).
    /// </summary>
    public static string ChecksWarning(IReadOnlyList<ContrastProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        string shown = string.Join(" · ", problems.Take(ChecksShown).Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"{p.Pair} {Math.Floor(p.Ratio * 10) / 10:0.0}:1, wants {p.Target:0.##}")));
        return "⚠ " + shown + (problems.Count > ChecksShown ? string.Create(CultureInfo.InvariantCulture, $" · and {problems.Count - ChecksShown} more") : "");
    }

    /// <summary>A built-in named: the copy to make and edit instead.</summary>
    public static string EditBuiltInQuestion(string copy) => $"Built-in themes can't be changed. Save a copy as {copy} and edit it?";

    /// <summary>The copy made for the editor (2026-11-17, #194: it said "Edit it, then /theme X-custom", while the editor was the edit).</summary>
    public static string EditCopied(string name, string copy, string path) => $"Copied {name} to {copy} ({path}).";

    /// <summary>The file was gone at save time (2026-11-17, #214).</summary>
    public static string EditGoneQuestion(string path) =>
        $"{path} is gone. Write it again as it was when the editor opened, with your colors? Changes made to it since then aren't in that copy.";

    /// <summary>The theme file couldn't be read when the editor opened.</summary>
    public static string EditUnreadable(string message) => $"Could not read the theme: {message}";

    /// <summary>A theme file whose top is not an object (<see cref="NotJson"/>'s detail).</summary>
    public const string NotAnObject = "not a JSON object";

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
    public const string PickerHint = "↑↓ = bar · ←→ = move (Shift ×5) · Enter = use, or type on Hex value · Esc = cancel";

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
