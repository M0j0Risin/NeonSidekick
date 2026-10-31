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

    /// <summary>How <see cref="UnreadableFolderProblem"/> starts, which tells a copy's unreadable folders apart.</summary>
    public const string UnreadableFolderHead = "the folder can't be read: ";

    public static string UnreadableFolderProblem(string detail) => UnreadableFolderHead + detail;

    /// <summary>A <c>*.md</c> that is a pipe, socket or device (2026-10-31, review P5-2): never opened.</summary>
    public const string NotAFileProblem = "a pipe, socket or device, not a file: not read";

    public static string UnreadableFolderLogLine(string folder, string detail) => $"Could not read the commands folder {folder}: {detail}";

    public static string ShadowedByBuiltInLogLine(string file, string builtIn) => $"The custom command file {file} is never run: {builtIn} is a built-in.";

    /// <summary>
    /// Why <paramref name="name"/> (as typed after the <c>/</c>, or a file's raw name) is no command name, the one reason that applies
    /// (2026-10-31, review P7-3, P2-14, P6-3: one wording for "the characters" left a device name, a long part or too many parts unexplained);
    /// null when it is a valid one.
    /// </summary>
    public static string? NameProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (CustomCommandName.IsValid(name))
        {
            return null;
        }

        string[] parts = name.Split(CustomCommandName.Separator);
        if (parts.Length > CustomCommandName.MaxDepth + 1)
        {
            return "more than " + (CustomCommandName.MaxDepth + 1).ToString(CultureInfo.InvariantCulture) + " parts joined by :";
        }

        if (parts.FirstOrDefault(CustomCommandName.IsDeviceName) is { } device)
        {
            return device + " is a Windows device name";
        }

        if (parts.Any(p => p.Length > CustomCommandName.MaxSegmentLength))
        {
            return "a part is over " + CustomCommandName.MaxSegmentLength.ToString(CultureInfo.InvariantCulture) + " characters";
        }

        if (name.Length > CustomCommandName.MaxLength)
        {
            return "over " + CustomCommandName.MaxLength.ToString(CultureInfo.InvariantCulture) + " characters";
        }

        return "lowercase letters, digits, _ and -, each part starting with a letter or digit";
    }

    /// <summary>
    /// A file whose name is no command name: the reason (<see cref="NameProblem"/>), then the rename that fixes it when there is one — the
    /// folder's when the bad part is a folder (<c>My Folder/review.md</c>: rename the folder <c>My Folder</c> to <c>my-folder</c>), else the
    /// file's (<c>my review.md</c>: rename it <c>my-review.md</c>). A <c>:</c> (or a Mac's <c>\</c>) in a part is named (review P1-4).
    /// </summary>
    public static string BadNameProblem(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var parts = CustomCommandName.Parts(relativePath);
        foreach (char bad in new[] { CustomCommandName.Separator, '\\' })
        {
            if (parts.Any(p => p.Contains(bad, StringComparison.Ordinal)))
            {
                return "a " + bad + " in a file or folder name can't be typed in a command: rename it";
            }
        }

        string raw = CustomCommandName.RawName(relativePath);
        string head = "not a command name (" + NameProblem(raw) + ")";
        string normalized = CustomCommandName.Normalize(raw);
        string[] fixedParts = normalized.Split(CustomCommandName.Separator);
        if (!CustomCommandName.IsValid(normalized) || fixedParts.Length != parts.Count)
        {
            return head;
        }

        for (int i = 0; i < parts.Count - 1; i++)
        {
            if (!CustomCommandName.IsValidSegment(parts[i].ToLowerInvariant()))
            {
                return head + ": rename the folder " + parts[i] + " to " + fixedParts[i];
            }
        }

        return head + ": rename it " + Path.GetFileName(CustomCommandName.RelativePath(normalized));
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

    /// <summary>A typed name refused, with its one reason (<see cref="NameProblem"/>, 2026-10-31, review P7-3) and the name it could be.</summary>
    public static string BadNameError(string typed, string suggestion, string verb = NewWord) =>
        "\"" + typed + "\" is no command name (" + (NameProblem(typed.Trim().TrimStart('/')) ?? "lowercase letters, digits, _ and -") + ")"
        + (suggestion.Length > 0 ? ": try " + Word + " " + verb + " " + suggestion : "");

    public static string BuiltInError(string name) => "/" + name + " is a built-in command: pick another name";

    public static string ExistsError(string name, CustomCommandScope scope) =>
        "/" + name + " is in " + ScopeWords(scope) + " already: rename or delete the old one on " + Word;

    public static string LinkedError(string name) => "/" + name + "'s folder is a link: the app never writes through one below the commands folder";

    /// <summary>A file that is itself a link (2026-10-31, review P4-3: it was named by the folder wording).</summary>
    public static string LinkedFileError(string path) => path + " is a link: the app never writes or deletes through one";

    /// <summary>
    /// A move or copy between two folders that are one (2026-10-31, review P7-2), the pane's and <c>/profile push|pull commands</c>' one
    /// wording: <paramref name="profile"/> names the other profile, null the global folder.
    /// </summary>
    public static string SharedFolderNotice(string? profile) =>
        "nothing to do: " + (profile is null ? "the global commands folder" : "\"" + profile + "\"'s commands folder") + " is this profile's";

    /// <summary>A copy into another profile that holds the name already (2026-10-31, review P2-13): that profile named.</summary>
    public static string ExistsInProfileError(string name, string profile) => "/" + name + " is in \"" + profile + "\"'s commands already";

    public static string FailedError(string detail) => "the command file couldn't be written: " + detail;

    public static string CreatedNotice(string name, string path) => Glyph + " /" + name + " started from the template: " + path;

    public static string SavedNotice(string name, string path, string firstLine, bool sendAgain = true) =>
        Glyph + " /" + name + " saved from \"" + firstLine + "\": " + path + "." + (sendAgain ? " Type /" + name + " to send it again." : "");

    public const string SavedMayReadDifferently = "A \\$ in the text may read differently when the command runs.";

    public const string PicturesNotKept = "Its pictures and PDFs weren't kept: a command is text.";

    public static string OverridesGlobalNote(string name) => "It now runs instead of the global /" + name + ".";

    public static string ShadowedHereNote(string name) => "This profile's /" + name + " runs instead of it here.";

    public const string NothingToSaveError = "no message sent in this conversation yet; " + Word + " new <name> starts from the template";

    public static string NotAMessageError(string word) => "the last turn was a " + word + " line, not a message";

    /// <summary>The last message was the app's own (2026-10-31, review P8-1: <c>/help</c>'s question, a plan's or audit's pass, a Ralph pass).</summary>
    public const string AppWrittenError = "the last message was written by the app, not you";

    /// <summary>The last message was a custom command's prompt (2026-10-31, review P1-5, the user's call: refuse).</summary>
    public static string FromCustomCommandError(string word) => "the last message came from " + word + ": copy that command on " + Word + " instead";

    /// <summary><c>/commands</c>' completion notes (2026-10-31, review P2-10: they were inline).</summary>
    public const string NewWordNote = "start a command from the template in your editor: " + Word + " new <name> [--global]";
    public const string SaveWordNote = "save your last message as a command: " + Word + " save <name> [--global]";
    public const string GlobalSwitchNote = "in the home's commands folder, every profile's";

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

    /// <summary>A Skipped folder row's Enter (2026-10-31, review P2-5): its problem, as a notice.</summary>
    public static string FolderProblemNotice(string shown, string problem) => Glyph + " " + shown + ": " + problem;

    /// <summary>A Skipped row whose file is gone since the scan (2026-10-31, review P2-5); the list is read again.</summary>
    public static string GoneNotice(string shown) => Glyph + " " + shown + " is gone: the list is read again";

    public static string ProfileWhere(string profile) => "to profile " + profile;

    // ── Rows, lines (no pane, headless) and help ────────────────────────────

    /// <summary>
    /// The rows every list of the commands draws (2026-10-31, review P6-7: the pane, the <c>/help</c> tab and the plain lines each walked the
    /// scan their own way): the commands, then the Shadowed files, then the Skipped ones, in the scan's order. Each field is cleaned the diff
    /// view's way (review P2-1, P3-4), and a Skipped file is named by its path under its folder (review P4-8), from <c>scan.Roots</c>. Pure.
    /// </summary>
    public static IReadOnlyList<CustomCommandRow> Rows(CustomCommandScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var rows = new List<CustomCommandRow>(scan.Commands.Count + scan.Shadowed.Count + scan.Problems.Count);
        rows.AddRange(scan.Commands.Select(c => new CustomCommandRow(CustomCommandRowKind.Command, Clean(c.Word), c.ArgumentHint is { Length: > 0 } hint ? Clean(hint) : null,
            Clean(c.Description ?? ""), c.Scope, Clean(ShownPath(scan.Roots, c.Scope, c.FilePath))) { Command = c }));
        rows.AddRange(scan.Shadowed.Select(s => new CustomCommandRow(CustomCommandRowKind.Shadow, Clean(s.Command.Word), null, Clean(ShadowReason(s)), s.Command.Scope,
            Clean(ShownPath(scan.Roots, s.Command.Scope, s.Command.FilePath))) { Shadow = s }));
        rows.AddRange(scan.Problems.Select(p => new CustomCommandRow(CustomCommandRowKind.Problem, Clean(ShownPath(scan.Roots, p.Scope, p.FilePath)), null, Clean(p.Problem), p.Scope,
            Clean(ShownPath(scan.Roots, p.Scope, p.FilePath))) { Problem = p }));
        return rows;
    }

    /// <summary>
    /// A file as the lists name it (2026-10-31, review P4-8): its path under its folder (<c>tools/review.md</c>), or the whole path when it
    /// isn't under it (or the roots are unknown).
    /// </summary>
    public static string ShownPath(CustomCommandRoots roots, CustomCommandScope scope, string path)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(path);
        string root = roots.Of(scope);
        if (root.Length == 0)
        {
            return path;
        }

        try
        {
            string relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) || relative == "." || Path.IsPathRooted(relative) ? path : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string Clean(string text) => UI.DiffView.Clean(text);

    /// <summary>A note's start in the plain lists and the pane's band (2026-10-31, review P2-10: written inline).</summary>
    public const string NotePrefix = "Note: ";

    /// <summary>
    /// The pane's text where there is no pane (headless, a redirected console): each command's word, hint, description and scope, then the
    /// Shadowed and Skipped rows with their reasons, the scan's note, then both folders and the new form, which is all an empty list shows.
    /// </summary>
    public static IReadOnlyList<string> Lines(CustomCommandScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var rows = Rows(scan);
        var lines = new List<string>();
        var commands = rows.Where(r => r.Kind == CustomCommandRowKind.Command).ToList();
        if (commands.Count == 0)
        {
            lines.Add(HelpEmptyRow);
        }
        else
        {
            lines.Add(ListHeading + ":");
            lines.AddRange(commands.Select(r => "  " + CommandLine(r)));
        }

        if (rows.Any(r => r.Kind == CustomCommandRowKind.Shadow))
        {
            lines.Add(ShadowedHeading + ":");
            lines.AddRange(rows.Where(r => r.Kind == CustomCommandRowKind.Shadow).Select(r => "  " + r.Word + " (" + ScopeName(r.Scope) + "): " + r.Detail));
        }

        if (rows.Any(r => r.Kind == CustomCommandRowKind.Problem))
        {
            lines.Add(SkippedHeading + ":");
            lines.AddRange(rows.Where(r => r.Kind == CustomCommandRowKind.Problem).Select(r => "  " + r.Word + " (" + ScopeName(r.Scope) + "): " + r.Detail));
        }

        if (scan.Note is { } note)
        {
            lines.Add(NotePrefix + note);
        }

        lines.Add("Folders: " + scan.Roots.Profile + " (profile), " + scan.Roots.Global + " (global)");
        lines.Add("New: " + Word + " new <name> [--global]");
        return lines;
    }

    /// <summary>The plain lists' heading over the commands.</summary>
    public const string ListHeading = "Custom commands";

    /// <summary>One command row as a plain line: word, hint, description, scope, a warning.</summary>
    public static string CommandLine(CustomCommandRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var sb = new StringBuilder(row.Word);
        if (row.Hint is { } hint)
        {
            sb.Append(' ').Append(hint);
        }

        if (row.Detail.Length > 0)
        {
            sb.Append("  ").Append(row.Detail);
        }

        sb.Append(" · ").Append(ScopeName(row.Scope));
        if (row.Command?.Warning is { } warning)
        {
            sb.Append(" (").Append(Clean(warning)).Append(')');
        }

        return sb.ToString();
    }

    /// <summary>The <c>/help</c> Custom tab as plain lines (the no-pane fallback): the commands, then the Shadowed and Skipped blocks, the note.</summary>
    public static IReadOnlyList<string> HelpLines(CustomCommandScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var rows = Rows(scan);
        var lines = new List<string>();
        if (rows.Count == 0)
        {
            lines.Add(HelpEmptyRow);
        }
        else
        {
            lines.Add(ListHeading + ":");
            lines.AddRange(rows.Where(r => r.Kind == CustomCommandRowKind.Command).Select(r => "  " + CommandLine(r)));
            if (rows.Any(r => r.Kind == CustomCommandRowKind.Shadow))
            {
                lines.Add("  " + ShadowedHeading + ": " + string.Join(", ", rows.Where(r => r.Kind == CustomCommandRowKind.Shadow).Select(r => r.Word + " (" + r.Detail + ")")));
            }

            if (rows.Any(r => r.Kind == CustomCommandRowKind.Problem))
            {
                lines.Add("  " + SkippedHeading + ": " + string.Join(", ", rows.Where(r => r.Kind == CustomCommandRowKind.Problem).Select(r => r.Word + " (" + r.Detail + ")")));
            }
        }

        if (scan.Note is { } note)
        {
            lines.Add("  " + NotePrefix + note);
        }

        return lines;
    }

    /// <summary>
    /// The <c>/help &lt;word&gt;</c> card (2026-10-31, review P3-6, P4-7, P2-3, P4-6). A command's: the word and hint, profile or global, the
    /// description, the file, the arguments, a warning, a note when it hides a global file of its name; a Skipped file's: why it doesn't run
    /// and its file. Both end with the way to the manual's answer (the user's call, 2026-10-31). <paramref name="markdown"/> for the screen and
    /// the Web UI, which draw it as a reply: the file's own words escaped and the path in a code span, so <c>__init__</c> reads as written;
    /// headless prints it plain.
    /// </summary>
    public static string Card(CustomHelpCard card, bool markdown)
    {
        ArgumentNullException.ThrowIfNull(card);
        string Said(string text) => markdown ? App.SessionExport.MarkdownText(Clean(text)) : Clean(text);
        string PathOf(string path) => markdown ? CodeSpan(Clean(path)) : Clean(path);
        var sb = new StringBuilder();
        string word;
        if (card.Command is { } command)
        {
            word = command.Word;
            sb.Append(Said(command.Word));
            if (command.ArgumentHint is { Length: > 0 } hint)
            {
                sb.Append(' ').Append(Said(hint));
            }

            sb.Append(" — your custom command, from ").Append(ScopeWords(command.Scope)).Append('.');
            if (command.Description is { Length: > 0 } description)
            {
                sb.Append("\n\n").Append(Said(description));
            }

            sb.Append("\n\nFile: ").Append(PathOf(command.FilePath));
            if (command.Arguments.Count > 0)
            {
                sb.Append("\nArguments: ").Append(Said(string.Join(", ", command.Arguments.Select(a => "$" + a))));
            }

            if (command.Warning is { } warning)
            {
                sb.Append('\n').Append(NotePrefix).Append(Said(warning));
            }

            if (card.Hides is { } hides)
            {
                sb.Append('\n').Append(NotePrefix).Append(HidesNote).Append(PathOf(hides.Command.FilePath));
            }
        }
        else
        {
            var problem = card.Problem ?? throw new ArgumentException("A card names a command or a skipped file.", nameof(card));
            word = "/" + (problem.Name ?? "");
            sb.Append(Said(word)).Append(" — ").Append(SkippedCardHead).Append(ScopeWords(problem.Scope)).Append(": ").Append(Said(problem.Problem)).Append('.');
            sb.Append("\n\nFile: ").Append(PathOf(problem.FilePath));
        }

        sb.Append("\n\n").Append(Said(ManualWayOut(word)));
        return sb.ToString();
    }

    /// <summary>A Skipped file's card, after its word (review P4-6).</summary>
    public const string SkippedCardHead = "a file the app skipped, in ";

    /// <summary>A card's note for a profile command that hides a global one (review P2-3), before that file's path.</summary>
    public const string HidesNote = "it runs instead of the global command of its name, ";

    /// <summary>The card's last line (the user's call, 2026-10-31): several words always ask the manual.</summary>
    public static string ManualWayOut(string word) => "For the manual's answer, ask in more words: /help about " + word.TrimStart('/');

    /// <summary>
    /// <paramref name="text"/> as a Markdown code span, fenced one backtick longer than its longest run (and padded when it starts or ends with
    /// one), so a path holding a backtick reads whole (review P4-7).
    /// </summary>
    public static string CodeSpan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        string fence = new('`', longest + 1);
        string pad = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return fence + pad + text + pad + fence;
    }

    /// <summary>The Custom tab's line for an empty list.</summary>
    public const string HelpEmptyRow = "No custom commands yet: " + Word + " makes one.";

    // ── The template ────────────────────────────────────────────────────────

    /// <summary>The template's how-to lines: frontmatter comments the reader skips, so running it unedited never sends them.</summary>
    public static readonly IReadOnlyList<string> TemplateHowTo =
    [
        "# $ARGUMENTS is everything typed after the command; $0, $1 … are single words (the first is $0),",
        "# \"quoted words\" count as one; a \\ right before a placeholder keeps it as written. With no",
        "# placeholder, what you type is added at the end as \"ARGUMENTS: …\".",
        "# Named words: a line \"arguments: [subject, style]\" makes $subject the first word and $style the",
        "# second, each empty when not typed; type \"\" to skip one and still fill the next.",
    ];

    /// <summary>The template's body line (a command still holding it carries <see cref="StillTemplateWarning"/>).</summary>
    public static string TemplateBody(string name) => "Write the prompt for /" + name + " here.";

    private const string TemplateDescriptionHead = "say what /";
    private const string TemplateDescriptionTail = " does (shown in the / list and /help)";

    /// <summary>The template's description (2026-10-31, review P6-5: lifted, so the template and the still-the-template check can't drift).</summary>
    public static string TemplateDescription(string name) => TemplateDescriptionHead + name + TemplateDescriptionTail;

    /// <summary>
    /// Whether <paramref name="description"/> (as the frontmatter reader gives it, unquoted and flattened) is still the template's, for any
    /// name: a renamed command keeps the old name in it.
    /// </summary>
    public static bool IsTemplateDescription(string? description) =>
        description is not null && description.StartsWith(TemplateDescriptionHead, StringComparison.Ordinal) && description.EndsWith(TemplateDescriptionTail, StringComparison.Ordinal);

    /// <summary>The template's argument hint.</summary>
    public const string TemplateHint = "<what to type after it>";

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

        sb.Append("description: ").Append(TemplateDescription(name)).Append('\n');
        sb.Append("argument-hint: ").Append(TemplateHint).Append('\n');
        sb.Append("---\n");
        sb.Append(TemplateBody(name)).Append('\n');
        return sb.ToString();
    }
}
