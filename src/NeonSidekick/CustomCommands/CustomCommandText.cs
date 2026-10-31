using System.Globalization;
using System.Text;

namespace NeonSidekick.CustomCommands;

/// <summary>
/// Every user string of the custom commands (2026-10-30, the custom commands plan): the pane's words, the notices, the problems and warnings,
/// the help card and the new-command template. Pinned by tests where a test reads them.
/// </summary>
public static class CustomCommandText
{
    /// <summary>The area's glyph (the user's pick, 2026-10-30: 📜 is the code block's fold and the Home Assistant script chip, 🪄 is used nowhere).</summary>
    public const string Glyph = "🪄";

    /// <summary>The <c>/commands</c> word.</summary>
    public const string Word = "/commands";

    /// <summary>The pane's label and its one tab's title.</summary>
    public const string Label = "Commands";

    /// <summary><c>/help</c>'s row for <c>/commands</c>.</summary>
    public const string HelpSummary = "manage your own slash commands";

    /// <summary>The scope's word: <c>profile</c> or <c>global</c>.</summary>
    public static string ScopeName(CustomCommandScope scope) => scope == CustomCommandScope.Profile ? "profile" : "global";

    /// <summary>The scope's folder in words.</summary>
    public static string ScopeWords(CustomCommandScope scope) => scope == CustomCommandScope.Profile ? "the profile's commands" : "the global commands";

    // ── Problems (Skipped rows) and warnings ────────────────────────────────

    public const string UnclosedFrontmatterWarning = "the frontmatter has no closing --- line, so the whole file after line 1 is the prompt";

    public static string UnreadFrontmatterWarning(string problem) => "the frontmatter didn't read (" + problem + "), so no field is set";

    public const string ShellLinesWarning = "its !`…` lines are sent as written, never run";

    public const string StillTemplateWarning = "still the template: edit it";

    public const string NotUserInvocableProblem = "user-invocable: false keeps it from being typed";

    public const string EmptyBodyProblem = "the prompt is empty";

    public const string TooLargeProblem = "over 64 KB";

    public const string LinkProblem = "a link: not followed (only a commands folder itself may be one)";

    public static readonly string TooDeepProblem = "more than " + CustomCommandName.MaxDepth.ToString(CultureInfo.InvariantCulture) + " folders deep: not read";

    public const string SameFolderNote = "the global commands folder is this profile's folder: read once, as the profile's";

    public static string UnreadableFolderProblem(string detail) => "the folder can't be read: " + detail;

    public static string UnreadableFolderLogLine(string folder, string detail) => $"Could not read the commands folder {folder}: {detail}";

    public static string ShadowedByBuiltInLogLine(string file, string builtIn) => $"The custom command file {file} is never run: {builtIn} is a built-in.";

    /// <summary>A file whose name is no command name, the name it could be when there is one (<c>my review.md</c> → rename it <c>my-review.md</c>).</summary>
    public static string BadNameProblem(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string normalized = CustomCommandName.Normalize(CustomCommandName.RawName(relativePath));
        string head = "not a command name (lowercase letters, digits, _ and -; " + CustomCommandName.MaxSegmentLength.ToString(CultureInfo.InvariantCulture) + " at most)";
        if (CustomCommandName.IsValid(normalized))
        {
            return head + ": rename it " + Path.GetFileName(CustomCommandName.RelativePath(normalized));
        }

        return CustomCommandName.RawName(relativePath).Split(CustomCommandName.Separator).Any(CustomCommandName.IsDeviceName)
            ? "a Windows device name can't be a command: rename it"
            : head;
    }

    public static string CaseDuplicateProblem(string word) => "another file here is " + word + " already (names differ only by case)";

    public static string GlobalRunsInsteadNote(string word) => "; the global " + word + " runs instead";

    /// <summary>A Shadowed row's reason.</summary>
    public static string ShadowReason(CustomCommandShadow shadow)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        return shadow.BuiltIn is { } builtIn ? builtIn + " is a built-in" : ShadowedByProfileNote(shadow.Command.Word);
    }

    public static string ShadowedByProfileNote(string word) => "this profile's " + word + " runs instead";

    // ── Notices ─────────────────────────────────────────────────────────────

    /// <summary>The line over a custom command's prompt: where it came from, never that it was sent (a later refusal still reads right).</summary>
    public static string SentNotice(CustomCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return "(" + Glyph + " " + command.Word + " from " + ScopeWords(command.Scope) + (command.HasShellLines ? "; " + ShellLinesWarning : "") + ")";
    }

    public static string EmptyPromptError(string word, string? hint) =>
        word + " made an empty prompt" + (hint is { Length: > 0 } ? ": try " + word + " " + hint : "");

    /// <summary>The one-time notice for files a built-in hides.</summary>
    public static string ShadowedByBuiltInNotice(IReadOnlyList<CustomCommandShadow> shadows)
    {
        ArgumentNullException.ThrowIfNull(shadows);
        string files = string.Join(", ", shadows.Select(s => Path.GetFileName(s.Command.FilePath) + " (" + s.BuiltIn + ")"));
        return Glyph + " " + (shadows.Count == 1 ? "A custom command is" : "Custom commands are") + " never run, a built-in having the name: " + files + ". Rename on " + Word + ".";
    }

    /// <summary>The <c>/</c> list's note for a command: its hint, its description, its scope.</summary>
    public static string CompletionNote(CustomCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var parts = new List<string>();
        if (command.ArgumentHint is { Length: > 0 } hint)
        {
            parts.Add(hint);
        }

        if (command.Description is { Length: > 0 } description)
        {
            parts.Add(description);
        }

        string head = string.Join("  ", parts);
        return (head.Length > 0 ? head + " · " : "") + ScopeName(command.Scope);
    }

    public static string NotFound(string word) => word + " is no longer a command (its file is gone)";

    /// <summary>The one log line a run writes: the word, the scope, the file and the prompt's length, never the prompt.</summary>
    public static string RunLogLine(CustomCommand command, int length)
    {
        ArgumentNullException.ThrowIfNull(command);
        return $"Custom command {command.Word} ({ScopeName(command.Scope)}, {command.FilePath}): a prompt of {length.ToString(CultureInfo.InvariantCulture)} chars";
    }

    /// <summary>A saved text's first non-blank line as its notice quotes it, cut to <see cref="FirstLineLength"/> characters with an ellipsis.</summary>
    public static string FirstLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length <= FirstLineLength ? line : line[..(FirstLineLength - 1)].TrimEnd() + "…";
    }

    /// <summary>How much of a saved text's first line its notice quotes.</summary>
    public const int FirstLineLength = 60;

    // ── /commands new|save ──────────────────────────────────────────────────

    public const string NewWord = "new";
    public const string SaveWord = "save";
    public const string GlobalSwitch = "--global";

    public static readonly string Usage = "Use " + Word + ", " + Word + " new <name> [--global] or " + Word + " save <name> [--global].";

    public static string BadNameError(string typed, string suggestion, string verb = NewWord) =>
        "\"" + typed + "\" is no command name (lowercase letters, digits, _ and -, folders joined by :)" + (suggestion.Length > 0 ? ": try " + Word + " " + verb + " " + suggestion : "");

    public static string BuiltInError(string name) => "/" + name + " is a built-in command: pick another name";

    public static string ExistsError(string name, CustomCommandScope scope) =>
        "/" + name + " is in " + ScopeWords(scope) + " already: rename or delete the old one on " + Word;

    public static string LinkedError(string name) => "/" + name + "'s folder is a link: the app never writes through one below the commands folder";

    public static string FailedError(string detail) => "the command file couldn't be written: " + detail;

    public static string CreatedNotice(string name, string path) => Glyph + " /" + name + " started from the template: " + path;

    public static string SavedNotice(string name, string path, string firstLine) =>
        Glyph + " /" + name + " saved from \"" + firstLine + "\": " + path + ". Type /" + name + " to send it again.";

    public const string SavedMayReadDifferently = "A \\$ in the text may read differently when the command runs.";

    public const string PicturesNotKept = "Its pictures and PDFs weren't kept: a command is text.";

    public static string OverridesGlobalNote(string name) => "It now runs instead of the global /" + name + ".";

    public static string ShadowedHereNote(string name) => "This profile's /" + name + " runs instead of it here.";

    public const string NothingToSaveError = "no message sent in this conversation yet; " + Word + " new <name> starts from the template";

    public static string NotAMessageError(string word) => "the last turn was a " + word + " line, not a message";

    public const string BlankError = "the text to save is blank";

    public const string TooLargeError = "the text is over 64 KB";

    public static string OpenFailedError(string detail) => "the editor couldn't open it: " + detail;

    // ── The pane ────────────────────────────────────────────────────────────

    public const string NewRow = "new command…";

    public const string ShadowedHeading = "Shadowed";

    public const string SkippedHeading = "Skipped";

    public const string EmptyRow = "No custom commands yet: pick new command… or drop a .md file in a commands folder.";

    public const string UseWord = "use";
    public const string MoveWord = "move";
    public const string CopyWord = "copy";
    public const string RenameWord = "rename";
    public const string EditWord = "edit";
    public const string DeleteWord = "delete";

    public const string ToGlobal = "to global";
    public const string ToThisProfile = "to this profile";

    public static string PageTitle(string word) => Label + " › " + word;

    public static string MoveTitle(string word) => Label + " › " + word + " › move";

    public static string CopyTitle(string word) => Label + " › " + word + " › copy to";

    public const string NewNameTitle = Label + " › new command: its name";

    public static string NewScopeTitle(string name) => Label + " › /" + name + " › where";

    public static string MovedNotice(string word, string where) => Glyph + " " + word + " moved " + where;

    public static string CopiedNotice(string word, string profile) => Glyph + " " + word + " copied to " + profile;

    public static string RenamedNotice(string from, string to) => Glyph + " " + from + " renamed /" + to;

    public static string DeletedNotice(string word, Files.DeleteWay way) => Glyph + " " + word + way switch
    {
        Files.DeleteWay.Recycled => OperatingSystem.IsMacOS() ? " moved to the Trash" : " moved to the Recycle Bin",
        Files.DeleteWay.DeletedNoBin => " deleted (the drive has no bin)",
        _ => " deleted",
    };

    public static string DeletePrompt(string word) => "Delete " + word + "'s file?";

    public static string MovePrompt(string word, string where) => "Move " + word + " " + where + "?";

    public static string EditOpenedNotice(string path) => Glyph + " opened " + path;

    public const string NoOtherProfile = "there is no other profile";

    public const string UnchangedNotice = "unchanged";

    public static string MissingError(string word) => word + "'s file is gone";

    public static string ProfileWhere(string profile) => "to profile " + profile;

    // ── Lines (no pane, headless) and help ──────────────────────────────────

    /// <summary>
    /// The pane's text where there is no pane (headless, a redirected console): each command's word, hint, description and scope, then the
    /// Shadowed and Skipped rows with their reasons, then both folders and the new form, which is all an empty list shows.
    /// </summary>
    public static IReadOnlyList<string> Lines(CustomCommandScan scan, CustomCommandRoots roots)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(roots);
        var lines = new List<string>();
        if (scan.Commands.Count == 0)
        {
            lines.Add("No custom commands.");
        }
        else
        {
            lines.Add("Custom commands:");
            foreach (var command in scan.Commands)
            {
                lines.Add("  " + CommandLine(command));
            }
        }

        if (scan.Shadowed.Count > 0)
        {
            lines.Add(ShadowedHeading + ":");
            lines.AddRange(scan.Shadowed.Select(s => "  " + s.Command.Word + " (" + ScopeName(s.Command.Scope) + "): " + ShadowReason(s)));
        }

        if (scan.Problems.Count > 0)
        {
            lines.Add(SkippedHeading + ":");
            lines.AddRange(scan.Problems.Select(p => "  " + p.FilePath + ": " + p.Problem));
        }

        lines.Add("Folders: " + roots.Profile + " (profile), " + roots.Global + " (global)");
        lines.Add("New: " + Word + " new <name> [--global]");
        return lines;
    }

    /// <summary>One command as a plain line: word, hint, description, scope.</summary>
    public static string CommandLine(CustomCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var sb = new StringBuilder(command.Word);
        if (command.ArgumentHint is { Length: > 0 } hint)
        {
            sb.Append(' ').Append(hint);
        }

        if (command.Description is { Length: > 0 } description)
        {
            sb.Append("  ").Append(description);
        }

        sb.Append(" · ").Append(ScopeName(command.Scope));
        if (command.Warning is { } warning)
        {
            sb.Append(" (").Append(warning).Append(')');
        }

        return sb.ToString();
    }

    /// <summary>The <c>/help</c> Custom tab as plain lines (the no-pane fallback): the commands, then the Shadowed and Skipped blocks.</summary>
    public static IReadOnlyList<string> HelpLines(CustomCommandScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.Commands.Count == 0 && scan.Shadowed.Count == 0 && scan.Problems.Count == 0)
        {
            return ["Custom commands: none (" + Word + " makes one)."];
        }

        var lines = new List<string> { "Custom commands:" };
        lines.AddRange(scan.Commands.Select(c => "  " + CommandLine(c)));
        if (scan.Shadowed.Count > 0)
        {
            lines.Add("  " + ShadowedHeading + ": " + string.Join(", ", scan.Shadowed.Select(s => s.Command.Word + " (" + ShadowReason(s) + ")")));
        }

        if (scan.Problems.Count > 0)
        {
            lines.Add("  " + SkippedHeading + ": " + string.Join(", ", scan.Problems.Select(p => Path.GetFileName(p.FilePath) + " (" + p.Problem + ")")));
        }

        return lines;
    }

    /// <summary>The <c>/help &lt;command&gt;</c> card: the word and hint, the description, profile or global, the file, a shadow or warning note.</summary>
    public static string Card(CustomCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var sb = new StringBuilder();
        sb.Append(command.Word);
        if (command.ArgumentHint is { Length: > 0 } hint)
        {
            sb.Append(' ').Append(hint);
        }

        sb.Append(" — your custom command, from ").Append(ScopeWords(command.Scope)).Append('.');
        if (command.Description is { Length: > 0 } description)
        {
            sb.Append("\n\n").Append(description);
        }

        sb.Append("\n\nFile: `").Append(command.FilePath).Append('`');
        if (command.Arguments.Count > 0)
        {
            sb.Append("\nArguments: ").Append(string.Join(", ", command.Arguments.Select(a => "$" + a)));
        }

        if (command.Warning is { } warning)
        {
            sb.Append("\nNote: ").Append(warning);
        }

        return sb.ToString();
    }

    /// <summary>The Custom tab's line for an empty list.</summary>
    public const string HelpEmptyRow = "No custom commands yet: " + Word + " makes one.";

    // ── The template ────────────────────────────────────────────────────────

    /// <summary>The template's how-to lines: frontmatter comments the reader skips, so running it unedited never sends them.</summary>
    public static readonly IReadOnlyList<string> TemplateHowTo =
    [
        "# $ARGUMENTS is everything typed after the command; $0, $1 … are single words (the first is $0),",
        "# \"quoted words\" count as one; \\$ writes a dollar sign before a digit. With no placeholder,",
        "# what you type is added at the end as \"ARGUMENTS: …\".",
    ];

    /// <summary>The template's body line (a command still holding it carries <see cref="StillTemplateWarning"/>).</summary>
    public static string TemplateBody(string name) => "Write the prompt for /" + name + " here.";

    /// <summary>The new-command template with <paramref name="name"/> filled in; <c>&lt;name&gt;</c> in <c>neon_help</c>'s format answer.</summary>
    public static string Template(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder();
        sb.Append("---\n");
        foreach (string line in TemplateHowTo)
        {
            sb.Append(line).Append('\n');
        }

        sb.Append("description: say what /").Append(name).Append(" does (shown in the / list and /help)\n");
        sb.Append("argument-hint: <what to type after it>\n");
        sb.Append("---\n");
        sb.Append(TemplateBody(name)).Append('\n');
        return sb.ToString();
    }
}
