using System.Globalization;
using System.Text;

namespace NeonSidekick.Files;

/// <summary>
/// The sentences the file tools answer with: pure statics, every string pinned. A result is what
/// the model reads and, verbatim, the one dim <c>🛠️</c> line the transcript shows (flattened to
/// its first 200 characters, so a multi-line answer starts with a header line that stands on its
/// own). Every error starts with <c>Error:</c>. Invariant culture throughout.
/// </summary>
public static class FileText
{
    public const string RootName = "the working directory";
    /// <summary>What <see cref="Describe"/> adds after the quoted path when the root is the profile's own folder. Pinned.</summary>
    public const string DefaultNote = " (the profile's default folder)";
    /// <summary>How <see cref="Describe"/> ends: the rule the file tools' <c>path</c> follows. Pinned.</summary>
    public const string RelativeNote = "; every path you pass to a file tool is relative to it";

    /// <summary><c>patch_file</c> with an empty or whitespace-only <c>old_text</c> (the re-send warning is Hermes' lesson: a model loops on it otherwise). Pinned.</summary>
    public const string EditEmpty = "Error: old_text is empty or only whitespace; give the exact text to replace, as read_file shows it, and do not send this call again unchanged";
    /// <summary><c>patch_file</c> with <c>new_text</c> = <c>old_text</c> (2026-09-18; a model probed a file's tail with a no-op edit). Pinned.</summary>
    public const string EditSame = "Error: old_text and new_text are the same; nothing to change";
    public const string RootItself = "Error: that is the working directory itself";
    /// <summary>A word argument outside its choices (<c>mode</c>, <c>output</c>, <c>order</c>; 2026-09-19). Pinned.</summary>
    public static string BadChoice(string argument, string raw, string choices) => $"Error: '{raw.Trim()}' is not one of {choices} for '{argument}'";
    /// <summary>A required path argument left out (2026-09-18: a `write_file` with `content` alone was told `'' is a folder`). Pinned.</summary>
    public static string PathRequired(string argument) => $"Error: {argument} is required: the file or folder, relative to the working directory";
    public static readonly string TooLong =
        "Error: the content is over " + WorkingDirectory.MaxWriteChars.ToString("N0", CultureInfo.InvariantCulture) + " characters; write it in parts";

    /// <summary>
    /// <c>get_working_directory</c>'s answer: a sentence with the path quoted, so a note after it is
    /// never read as part of it (a model copied the bare <c>… (profile default)</c> into
    /// <c>list_directory</c>, 2026-09-17), the default-folder note as its own clause, then the
    /// relative rule.
    /// </summary>
    public static string Describe(string path, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(path);
        return $"The working directory is '{path}'{(isDefault ? DefaultNote : "")}{RelativeNote}.";
    }

    /// <summary>A relative path as a sentence names it; the root has a name of its own.</summary>
    public static string Name(string relative) => string.IsNullOrEmpty(relative) ? RootName : relative;

    // ---- errors ----

    public static string OutsideRoot(string path) => $"Error: '{path}' is outside the working directory; every path must stay inside it";
    public static string Missing(string path) => $"Error: nothing is at '{path}'";
    public static string IsDirectory(string path) => $"Error: '{path}' is a folder, not a file";
    public static string IsAFile(string path) => $"Error: '{path}' is a file, not a folder";
    /// <summary>A binary file; an image by extension is pointed at <c>view_image</c> (a small model tries <c>read_file</c> on a PNG first).</summary>
    public static string NotText(string path) => $"Error: '{path}' is not a text file" + (ImageFile.IsImagePath(path) ? ViewImageHint : "");

    /// <summary>What <see cref="NotText"/> adds for an image path. Pinned.</summary>
    public const string ViewImageHint = "; use view_image to look at it";

    public static string NotAnImage(string path) => $"Error: '{path}' could not be read as an image";
    public static string ImageTooBig(string path) => $"Error: '{path}' is over {ImageFile.MaxFileBytes / 1_000_000} MB or {ImageFile.MaxPixels / 1_000_000} megapixels; too large to view";
    public static string NotAnArchive(string path) => $"Error: '{path}' is not a zip archive";
    public static string Exists(string path) => $"Error: '{path}' already exists; call again with overwrite true to replace it";
    /// <summary><c>write_file</c>'s form of <see cref="Exists"/> (2026-09-19): the tool has a <c>mode</c>, not an <c>overwrite</c>. Pinned.</summary>
    public static string WriteExists(string path) => $"Error: '{path}' already exists; call again with mode overwrite to replace it, or mode append to add to its end";
    /// <summary>No strategy of the chain found <c>old_text</c> (2026-09-19). Pinned.</summary>
    public static string EditNotFound(string path) =>
        $"Error: old_text was not found in '{path}', even with spacing, indentation, quotes and dashes matched loosely; read the file again and copy the text as it is{GutterHint}";
    public static string EditAmbiguous(string path, int count) =>
        $"Error: old_text appears {count.ToString(CultureInfo.InvariantCulture)} times in '{path}'; include enough surrounding text to make it unique, or pass replace_all true";

    /// <summary><see cref="EditAmbiguous(string, int)"/> with the first matches named (2026-09-19): a <c>line N: text</c> row each, <see cref="AmbiguousMore"/> past <see cref="FuzzyMatch.MaxLocations"/>.</summary>
    public static string EditAmbiguous(string path, int count, IReadOnlyList<MatchLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (locations.Count == 0)
        {
            return EditAmbiguous(path, count);
        }

        var sb = new StringBuilder(EditAmbiguous(path, count)).Append(':');
        foreach (var location in locations)
        {
            sb.Append("\n  line ").Append(location.Line.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(Quote(location.Text));
        }

        if (count > locations.Count)
        {
            sb.Append('\n').Append(AmbiguousMore(count - locations.Count));
        }

        return sb.ToString();
    }

    /// <summary>The last row of an ambiguous refusal past its named locations. Pinned.</summary>
    public static string AmbiguousMore(int rest) => "  … and " + rest.ToString(CultureInfo.InvariantCulture) + " more";

    /// <summary><c>replace_all</c> over matches only an approximate strategy found (2026-09-19). Pinned.</summary>
    public static string ApproximateAll(string path, int count) =>
        $"Error: old_text matched {count.ToString(CultureInfo.InvariantCulture)} places in '{path}' only approximately; replace_all needs the text as it is in the file — copy it exactly, spacing included";

    /// <summary>The arguments hold <c>\'</c> or <c>\"</c> and the matched text does not (2026-09-19). Pinned.</summary>
    public static string EscapeDriftQuote(string path, char quote) =>
        $"Error: old_text and new_text hold \\{quote} but the matched text in '{path}' does not; the backslash is a serialisation artefact — read the file again and pass both without it";

    /// <summary>Every backslash run in <c>old_text</c> is twice the file's (2026-09-19). Pinned.</summary>
    public static string EscapeDriftDoubled(string path) =>
        $"Error: every backslash run in old_text is twice as long as in '{path}'; read the file again and pass old_text and new_text with the backslashes as the file has them";

    /// <summary>A <c>patch_file</c> whose <c>new_text</c> is in the file and whose <c>old_text</c> is not: done before, nothing written (2026-09-19). Not an error. Pinned.</summary>
    public static string AlreadyApplied(string path) =>
        $"nothing changed in {path}: it already holds new_text and old_text is not in it, so the edit appears to be applied; do not send it again";

    /// <summary>What <see cref="EditNotFound"/> ends with: an edit result shows its region numbered, and the gutter is not text (2026-09-17; a read was numbered too until 2026-09-19). Pinned.</summary>
    public const string GutterHint = " (the line numbers an edit result shows are not part of the file)";

    // ---- the strategy notes (2026-09-19): what an edit's sentence says of a non-exact match ----

    public const string LineTrimmedNote = " (old_text matched with each line's leading and trailing spaces ignored)";
    public const string WhitespaceNote = " (old_text matched with runs of spaces collapsed)";
    public const string IndentNote = " (old_text matched with its indentation ignored)";
    public const string EscapeNote = " (old_text matched with its \\n, \\t and \\r escapes read as line breaks and tabs)";
    public const string BoundaryNote = " (old_text matched with its first and last lines' spaces ignored)";
    public const string UnicodeNote = " (old_text matched with quotes, dashes and spaces read as their plain forms; the file keeps its own)";
    public const string BlockAnchorNote = " (old_text matched by its first and last lines, the lines between similar; check the result)";
    public const string ContextNote = " (old_text matched line by line by similarity; check the result)";

    /// <summary>The note an edit's sentence carries for the strategy that found <c>old_text</c>; nothing for an exact match.</summary>
    public static string StrategyNote(MatchStrategy strategy) => strategy switch
    {
        MatchStrategy.LineTrimmed => LineTrimmedNote,
        MatchStrategy.WhitespaceNormalized => WhitespaceNote,
        MatchStrategy.IndentationFlexible => IndentNote,
        MatchStrategy.EscapeNormalized => EscapeNote,
        MatchStrategy.TrimmedBoundary => BoundaryNote,
        MatchStrategy.UnicodeNormalized => UnicodeNote,
        MatchStrategy.BlockAnchor => BlockAnchorNote,
        MatchStrategy.ContextAware => ContextNote,
        _ => "",
    };

    /// <summary>
    /// A folder at a <c>move</c> / <c>copy</c> destination under <c>overwrite</c> (2026-09-20): never replaced. Pinned.
    /// </summary>
    public static string FolderInTheWay(string path) =>
        $"Error: '{path}' is a folder in the way — move it aside first";

    /// <summary>A <c>delete</c> of <c>.git</c>, of anything in it, or of a folder holding one (2026-09-23, the user's call). Pinned.</summary>
    public static string GitProtected(string path) =>
        $"Error: '{path}' is or holds a {WorkingDirectory.GitFolderName} folder, which delete never removes";

    /// <summary>A line as a refusal quotes it: trimmed, cut at <see cref="WorkingDirectory.MaxQuotedChars"/> with an ellipsis.</summary>
    public static string Quote(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string trimmed = line.Trim();
        return trimmed.Length <= WorkingDirectory.MaxQuotedChars ? trimmed : trimmed[..(WorkingDirectory.MaxQuotedChars - 1)] + "…";
    }
    public static string IntoItself(string from, string to) => $"Error: cannot put '{Name(from)}' inside itself ('{to}')";
    public static string TooBig(string path) => $"Error: '{path}' is too large to handle as text";
    public static string BadPattern(string detail) => $"Error: the regular expression is invalid: {detail}";
    public static string BadBoolean(string argument, string raw) => $"Error: '{raw.Trim()}' is not true or false for '{argument}'";
    public static string BadStringList(string argument, string raw) => $"Error: '{raw.Trim()}' is not a list of paths for '{argument}'";
    public static string ZipSlip(string archive, string entry) => $"Error: '{archive}' holds an entry that would land outside the destination ('{entry}'); nothing was extracted";
    public static string CouldNot(string verb, string path, string detail) => $"Error: could not {verb} '{Name(path)}': {detail}";

    /// <summary>The sentence for a non-<see cref="FileOutcome.Ok"/> outcome; <paramref name="verb"/> names the operation for a failure.</summary>
    public static string Error(FileOutcome outcome, string path, string verb, string detail = "", string other = "")
    {
        ArgumentNullException.ThrowIfNull(path);
        return outcome switch
        {
            FileOutcome.OutsideRoot => OutsideRoot(path),
            FileOutcome.Missing => Missing(path),
            FileOutcome.IsDirectory => IsDirectory(path),
            FileOutcome.IsAFile => IsAFile(path),
            FileOutcome.NotText => NotText(path),
            FileOutcome.NotAnArchive => NotAnArchive(path),
            FileOutcome.Exists => Exists(path),
            FileOutcome.Empty => EditEmpty,
            FileOutcome.Same => EditSame,
            FileOutcome.EditNotFound => EditNotFound(path),
            FileOutcome.EditAmbiguous => EditAmbiguous(path, int.TryParse(detail, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0),
            FileOutcome.IntoItself => IntoItself(path, other),
            FileOutcome.TooLong => TooLong,
            FileOutcome.TooBig => TooBig(path),
            FileOutcome.BadPattern => BadPattern(detail),
            FileOutcome.NotAnImage => NotAnImage(path),
            FileOutcome.ImageTooBig => ImageTooBig(path),
            // The patch_file refusals of 2026-09-19: detail carries the count, or the drift's name.
            FileOutcome.ApproximateAll => ApproximateAll(path, Int(detail)),
            FileOutcome.EscapeDrift => EscapeDriftSentence(path, detail),
            FileOutcome.AlreadyApplied => AlreadyApplied(path),
            FileOutcome.FolderInTheWay => FolderInTheWay(path),
            FileOutcome.GitProtected => GitProtected(path),
            _ => CouldNot(verb, path, detail),
        };

        static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    /// <summary>The <see cref="FileOutcome.EscapeDrift"/> sentence for a drift named by <paramref name="drift"/> (an <see cref="EscapeDrift"/> member's name).</summary>
    public static string EscapeDriftSentence(string path, string drift) => drift switch
    {
        nameof(EscapeDrift.QuoteSingle) => EscapeDriftQuote(path, '\''),
        nameof(EscapeDrift.QuoteDouble) => EscapeDriftQuote(path, '"'),
        _ => EscapeDriftDoubled(path),
    };

    // ---- read side ----

    public static string Listing(ListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "list", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Name(result.Relative)).Append(" (").Append(Count(result.Entries.Count, "entry", "entries")).Append("):");
        if (result.Entries.Count == 0)
        {
            sb.Append(" (empty)");
        }

        foreach (var entry in result.Entries)
        {
            sb.Append('\n');
            if (entry.IsDirectory)
            {
                sb.Append(entry.Name).Append(Path.DirectorySeparatorChar);
            }
            else
            {
                sb.Append(entry.Name).Append("  ").Append(Size(entry.Length));
            }
        }

        if (result.Truncated)
        {
            sb.Append('\n').Append(OnlyFirst(result.Entries.Count, "entries"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The nested listing (<c>search_files</c> with no <c>text</c> and a <c>depth</c> over 1 since 2026-09-19; <c>list_directory</c>'s
    /// from 2026-09-18 until then): <c>docs\ (12 entries, 3 levels):</c> then every folder and file, indented two spaces per level
    /// under the first, folders with a trailing separator and files with their size — the flat listing's rows,
    /// nested. <paramref name="depth"/> is the levels asked for (the header's figure).
    /// </summary>
    public static string Listing(FileTreeResult result, int depth)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "list", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Name(result.Relative)).Append(" (").Append(Count(result.Entries.Count, "entry", "entries"))
          .Append(", ").Append(Count(depth, "level", "levels")).Append("):");
        if (result.Entries.Count == 0)
        {
            sb.Append(" (empty)");
        }

        foreach (var entry in result.Entries)
        {
            sb.Append('\n').Append(' ', 2 * (entry.Depth - 1));
            if (entry.IsDirectory)
            {
                sb.Append(entry.Name).Append(Path.DirectorySeparatorChar);
            }
            else
            {
                sb.Append(entry.Name).Append("  ").Append(Size(entry.Length));
            }
        }

        if (result.Truncated)
        {
            sb.Append('\n').Append(OnlyFirst(result.Entries.Count, "entries"));
        }

        return sb.ToString();
    }

    public static string Found(FindResult result, string pattern)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(pattern);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "search", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Count(result.Paths.Count, "file matches", "files match")).Append(" '").Append(pattern).Append("' under ").Append(Name(result.Relative)).Append(':');
        if (result.Paths.Count == 0)
        {
            sb.Append(" (none)");
        }

        foreach (var path in result.Paths)
        {
            sb.Append('\n').Append(path);
        }

        if (result.Truncated)
        {
            sb.Append('\n').Append(OnlyFirst(result.Paths.Count, "matches"));
        }

        return sb.ToString();
    }

    /// <summary>The row between two files' hits in a search with context. Pinned.</summary>
    public const string ContextSeparator = "--";

    /// <summary>What a cut search's tail adds after <c>only the first N matches are shown</c>. Pinned.</summary>
    public const string NarrowHint = "; narrow the search or the folder";

    public static string SearchHits(SearchResult result, string text)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(text);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "search", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Count(result.Hits.Count, "match", "matches")).Append(" for '").Append(text).Append("' in ");
        SearchScope(sb, result);
        if (result.Hits.Count == 0)
        {
            sb.Append(" (no matches)");
        }

        // With context (2026-09-17) the hits take grep's shape: `file-11- text` around `file:12: text`,
        // a `--` between hits of different files.
        string? lastFile = null;
        foreach (var hit in result.Hits)
        {
            bool withContext = hit.Before.Count > 0 || hit.After.Count > 0;
            if (withContext && lastFile is not null && !string.Equals(lastFile, hit.RelativePath, StringComparison.Ordinal))
            {
                sb.Append('\n').Append(ContextSeparator);
            }

            lastFile = hit.RelativePath;
            for (int i = 0; i < hit.Before.Count; i++)
            {
                sb.Append('\n').Append(hit.RelativePath).Append('-').Append((hit.Line - hit.Before.Count + i).ToString(CultureInfo.InvariantCulture)).Append("- ").Append(hit.Before[i]);
            }

            sb.Append('\n').Append(hit.RelativePath).Append(':').Append(hit.Line.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(hit.Text);
            for (int i = 0; i < hit.After.Count; i++)
            {
                sb.Append('\n').Append(hit.RelativePath).Append('-').Append((hit.Line + 1 + i).ToString(CultureInfo.InvariantCulture)).Append("- ").Append(hit.After[i]);
            }
        }

        if (result.Truncated)
        {
            sb.Append('\n').Append(OnlyFirst(result.Hits.Count, "matches")).Append(NarrowHint);
        }

        SearchTimedOut(sb, result);
        return sb.ToString();
    }

    /// <summary>
    /// <c>search_files</c> with <c>output</c> files (2026-09-19): <c>3 files hold 'x' (searched 40 files under the working directory in 0.02 s):</c>
    /// then <c>path  2 matches</c> per file, the cut tail counting files.
    /// </summary>
    public static string SearchFiles(SearchResult result, string text)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(text);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "search", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Count(result.Files.Count, "file holds", "files hold")).Append(" '").Append(text).Append("' ");
        if (result.SingleFile)
        {
            sb.Append("in ");
        }

        SearchScope(sb, result, countFiles: false);
        if (result.Files.Count == 0)
        {
            sb.Append(" (no matches)");
        }

        foreach (var file in result.Files)
        {
            sb.Append('\n').Append(file.RelativePath).Append("  ").Append(Count(file.Matches, "match", "matches"));
        }

        if (result.Truncated)
        {
            sb.Append('\n').Append(OnlyFirst(result.Files.Count, "files")).Append(NarrowHint);
        }

        SearchTimedOut(sb, result);
        return sb.ToString();
    }

    /// <summary>The header's scope: <c>N files (searched K files under x in 0.00 s):</c>, or one file named as the path (2026-09-18): <c>notes.txt (0.01 s):</c>.</summary>
    private static void SearchScope(StringBuilder sb, SearchResult result, bool countFiles = true)
    {
        if (result.SingleFile)
        {
            sb.Append(result.Relative);
        }
        else
        {
            if (countFiles)
            {
                sb.Append(Count(result.FilesMatched, "file", "files")).Append(' ');
            }

            sb.Append("(searched ").Append(Count(result.FilesSearched, "file", "files")).Append(" under ").Append(Name(result.Relative));
        }

        sb.Append(result.SingleFile ? " (" : " in ").Append(result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s):");
    }

    private static void SearchTimedOut(StringBuilder sb, SearchResult result)
    {
        if (result.TimedOut > 0)
        {
            sb.Append('\n').Append(Count(result.TimedOut, "line", "lines")).Append(" skipped: the expression took too long on them");
        }
    }

    public static string Recent(RecentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "list", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append("most recently changed under ").Append(Name(result.Relative)).Append(", newest first:");
        if (result.Entries.Count == 0)
        {
            sb.Append(" (no files)");
        }

        foreach (var entry in result.Entries)
        {
            sb.Append('\n').Append(entry.RelativePath).Append("  ").Append(Moment(entry.Modified)).Append("  ").Append(Size(entry.Length));
        }

        return sb.ToString();
    }

    public static string Info(InfoResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "inspect", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Name(result.Relative)).Append(" — ");
        if (result.IsDirectory)
        {
            sb.Append(Count(result.Files, "file", "files")).Append(" in ").Append(Count(result.Folders, "folder", "folders"))
              .Append(", ").Append(Size(result.Bytes)).Append(", last modified ").Append(Moment(result.Modified));
            if (result.Truncated)
            {
                sb.Append(" (counted the first ").Append(WorkingDirectory.MaxInfoEntries.ToString("N0", CultureInfo.InvariantCulture)).Append(" entries only)");
            }

            return sb.ToString();
        }

        sb.Append(Bytes(result.Bytes));
        if (result.Lines is { } lines)
        {
            sb.Append(", ").Append(Count(lines, "line", "lines"));
        }

        if (result.Words is { } words)
        {
            sb.Append(", ").Append(Count(words, "word", "words"));
        }

        // The line endings and a BOM (2026-09-17): what an edit will keep; nothing when the file has no line break.
        if (result.LineEnding.Length > 0)
        {
            sb.Append(", ").Append(result.LineEnding);
        }

        if (result.Bom)
        {
            sb.Append(", ").Append(BomNote);
        }

        sb.Append(", modified ").Append(Moment(result.Modified));
        return sb.ToString();
    }

    /// <summary>What <see cref="Info"/> says of a file with a byte-order mark. Pinned.</summary>
    public const string BomNote = "UTF-8 BOM";

    /// <summary>The read result: a header naming the window, then the text as it is (numbered on request until 2026-09-19).</summary>
    public static string Read(ReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "read", result.Detail);
        }

        string header = ReadHeader(result);
        if (result.Text.Length == 0)
        {
            return header;
        }

        return header + "\n" + result.Text;
    }

    /// <summary>
    /// Lines behind a gutter — <c>{n,w}: {text}</c>, the number right-aligned to the width of the last
    /// one — the shape an edit's region takes so the next edit is addressed (2026-09-17;
    /// a read shared it until 2026-09-19).
    /// </summary>
    public static string Numbered(IReadOnlyList<string> lines, int from, int last)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int width = Math.Max(last, from + lines.Count - 1).ToString(CultureInfo.InvariantCulture).Length;
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append((from + i).ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append(": ").Append(lines[i]);
        }

        return sb.ToString();
    }

    /// <summary>The tail of <see cref="Image"/>'s sentence: where the picture is, since a tool result cannot carry one. Pinned.</summary>
    public const string ImageFollows = "the picture is in the next message";

    /// <summary>
    /// The last line of a <c>view_image</c> given more than its cap (2026-09-18; 17 paths were refused whole
    /// and the model started over in batches): <c>13 more not shown; call again with paths: e.png, f.png</c>.
    /// </summary>
    public static string MorePictures(IReadOnlyList<string> rest)
    {
        ArgumentNullException.ThrowIfNull(rest);
        return rest.Count.ToString(CultureInfo.InvariantCulture) + " more not shown; call again with paths: " + string.Join(", ", rest);
    }

    /// <summary><c>ladybug.png (1024×768 image/png, 213.4 KB): the picture is in the next message</c> — the size the sent bytes, after any downscale.</summary>
    public static string Image(ImageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok || result.Image is null)
        {
            return Error(result.Outcome, result.Relative, "view", result.Detail);
        }

        var image = result.Image;
        return result.Relative + " (" + image.Width.ToString(CultureInfo.InvariantCulture) + "×" + image.Height.ToString(CultureInfo.InvariantCulture)
            + " " + image.MediaType + ", " + Size(image.Bytes.Length) + "): " + ImageFollows;
    }

    /// <summary><c>notes.txt (lines 1–40 of 48):</c>, or the empty and past-the-end forms.</summary>
    public static string ReadHeader(ReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string total = result.TotalLines.ToString(CultureInfo.InvariantCulture);
        if (result.TotalLines == 0)
        {
            return result.Relative + " (empty)";
        }

        if (result.ToLine < result.FromLine)
        {
            return result.Relative + " has only " + Count(result.TotalLines, "line", "lines") + "; nothing from line " + result.FromLine.ToString(CultureInfo.InvariantCulture);
        }

        string window = result.FromLine == 1 && result.ToLine == result.TotalLines && !result.Truncated
            ? Count(result.TotalLines, "line", "lines")
            : "lines " + result.FromLine.ToString(CultureInfo.InvariantCulture) + "–" + result.ToLine.ToString(CultureInfo.InvariantCulture) + " of " + total;
        string tail = result.Truncated ? "; cut at " + WorkingDirectory.MaxReadChars.ToString("N0", CultureInfo.InvariantCulture) + " characters" : "";
        string next = result.ToLine < result.TotalLines ? NextHint(result.ToLine + 1) : "";
        return result.Relative + " (" + window + tail + next + "):";
    }

    /// <summary>
    /// What a partial read's header ends with (2026-09-18, after a model read the top 200 lines of a
    /// 1,900-line file 22 times running): the call that continues it, spelled out.
    /// </summary>
    public static string NextHint(int startLine) => "; next: start_line " + startLine.ToString(CultureInfo.InvariantCulture);

    // ---- write side ----

    public static string Wrote(WriteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == FileOutcome.Exists)
        {
            return WriteExists(result.Relative);
        }

        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "write", result.Detail);
        }

        return (result.Replaced ? "replaced " : "wrote ") + result.Relative + " (" + Bytes(result.Bytes) + Counts(result.Lines, result.Words) + ")";
    }

    /// <summary>
    /// <c>, 9 lines, 2,182 words</c> — what every write-side sentence carries since 2026-09-18, so the
    /// model needs no <c>file_info</c> to count what it wrote (a log showed one after every write);
    /// empty when the counts are unknown (bytes, a file too big).
    /// </summary>
    public static string Counts(int? lines, int? words) =>
        lines is { } l && words is { } w ? ", " + Count(l, "line", "lines") + ", " + Count(w, "word", "words") : "";

    public static string Appended(WriteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "append to", result.Detail);
        }

        if (!result.Replaced)
        {
            return "created " + result.Relative + " (" + Bytes(result.Bytes) + Counts(result.Lines, result.Words) + ")";
        }

        string now = result.Lines is { } && result.Words is { } ? " (now" + Counts(result.Lines, result.Words)[1..] + ")" : "";
        return "appended " + Bytes(result.Bytes) + " to " + result.Relative + now;
    }

    /// <summary>
    /// An edit's sentence (2026-09-17): <c>edited x (line 12):</c> or <c>edited x (lines 12–16):</c> — the
    /// lines the new text holds — then the region around it numbered, so the model sees the
    /// new numbering without a second read. A deletion reads <c>edited x (removed at line 12):</c>. Over
    /// <see cref="WorkingDirectory.MaxEditRegionLines"/> the region is left to <c>read_file</c>; a
    /// <c>replace_all</c> lists the lines it touched instead.
    /// </summary>
    public static string Edited(EditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == FileOutcome.AlreadyApplied)
        {
            return AlreadyApplied(result.Relative);
        }

        if (result.Outcome == FileOutcome.EditAmbiguous)
        {
            return EditAmbiguous(result.Relative, result.Count, result.Locations);
        }

        if (result.Outcome != FileOutcome.Ok)
        {
            string detail = result.Outcome switch
            {
                FileOutcome.ApproximateAll => result.Count.ToString(CultureInfo.InvariantCulture),
                FileOutcome.EscapeDrift => result.Drift.ToString(),
                _ => result.Detail,
            };
            return Error(result.Outcome, result.Relative, "edit", detail);
        }

        var sb = new StringBuilder();
        if (result.Lines.Count > 1)
        {
            sb.Append("replaced ").Append(Count(result.Lines.Count, "occurrence", "occurrences")).Append(" of old_text in ").Append(result.Relative)
              .Append(" (lines ").Append(string.Join(", ", result.Lines.Take(MaxListedLines).Select(l => l.ToString(CultureInfo.InvariantCulture))));
            if (result.Lines.Count > MaxListedLines)
            {
                sb.Append(", …");
            }

            return sb.Append(Now(result)).Append(')').Append(StrategyNote(result.Strategy)).ToString();
        }

        sb.Append("edited ").Append(result.Relative).Append(" (").Append(Range(result)).Append(Now(result)).Append(')')
          .Append(StrategyNote(result.Strategy));
        if (result.Region.Count > 0)
        {
            sb.Append(":\n").Append(Numbered(result.Region, result.RegionFrom, result.RegionFrom + result.Region.Count - 1));
        }
        else if (result.NewTo - result.NewFrom + 1 > WorkingDirectory.MaxEditRegionLines - 2 * WorkingDirectory.EditContextLines)
        {
            // The region was left out for its size (a smaller one is empty only when the file is).
            sb.Append(RegionTooBig);
        }

        return sb.ToString();
    }

    /// <summary>Lines a <c>replace_all</c> sentence names before <c>…</c>.</summary>
    public const int MaxListedLines = 10;

    /// <summary>What an edit's sentence ends with when its region is over <see cref="WorkingDirectory.MaxEditRegionLines"/>. Pinned.</summary>
    public const string RegionTooBig = "; read_file to see the lines";

    /// <summary><c>; now 40 lines, 2,500 words</c> inside an edit's parentheses: the file's counts after it (2026-09-18).</summary>
    private static string Now(EditResult result) => "; now" + Counts(result.TotalLines, result.Words)[1..];

    private static string Range(EditResult result)
    {
        string from = result.NewFrom.ToString(CultureInfo.InvariantCulture);
        if (result.NewTo < result.NewFrom)
        {
            return "removed at line " + from;
        }

        return result.NewTo == result.NewFrom ? "line " + from : "lines " + from + "–" + result.NewTo.ToString(CultureInfo.InvariantCulture);
    }

    public static string Created(CreateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            FileOutcome.Ok => "created " + result.Relative,
            FileOutcome.Exists => result.Relative + " already exists",
            _ => Error(result.Outcome, result.Relative, "create", result.Detail),
        };
    }

    public static string Moved(MoveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Outcome is FileOutcome.Exists or FileOutcome.FolderInTheWay ? result.To : result.From, "move", result.Detail, result.To);
        }

        return (result.Renamed ? "renamed " : "moved ") + result.From + " to " + result.To;
    }

    public static string Copied(MoveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Outcome is FileOutcome.Exists or FileOutcome.FolderInTheWay ? result.To : result.From, "copy", result.Detail, result.To);
        }

        return "copied " + result.From + " to " + result.To;
    }

    /// <summary>
    /// A delete's sentence: what is gone, a folder with everything in it (2026-09-20). It names no setting (2026-09-21, the user's
    /// ask: the model reasoned about a switch it cannot reach); <c>Trashed</c> until 2026-10-01, when File safe edits, its
    /// <c>.trash</c> and <c>restore</c> went (the user's call) and every delete became this one.
    /// </summary>
    public static string Deleted(DeleteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == FileOutcome.IntoItself)
        {
            return RootItself;
        }

        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "delete", result.Detail);
        }

        return "deleted " + (result.IsDirectory ? "the folder " + result.Relative + " and everything in it" : result.Relative);
    }

    public static string Zipped(ZipResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == FileOutcome.IntoItself)
        {
            return string.IsNullOrEmpty(result.Relative) ? RootItself : IntoItself(result.Relative, result.Archive);
        }

        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Outcome is FileOutcome.Exists or FileOutcome.IsDirectory ? result.Archive : result.Relative, "zip", result.Detail);
        }

        return "zipped " + result.Relative + " into " + result.Archive + " (" + Count(result.Entries, "entry", "entries") + ", " + Size(result.Bytes) + ")";
    }

    public static string Unzipped(ZipResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == FileOutcome.OutsideRoot && result.Detail.Length > 0)
        {
            return ZipSlip(result.Relative, result.Detail);
        }

        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Outcome is FileOutcome.Exists or FileOutcome.IsAFile ? result.Archive : result.Relative, "unzip", result.Detail);
        }

        return "unzipped " + result.Relative + " into " + result.Archive + " (" + Count(result.Entries, "entry", "entries") + ")";
    }

    public static string Opened(OpenResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.Relative, "open", result.Detail);
        }

        return "opened " + Name(result.Relative) + (result.IsDirectory ? " in Explorer" : " in the user's editor");
    }

    // ---- formats ----

    /// <summary><c>512 B</c>, <c>1.2 KB</c>, <c>40.1 MB</c>, <c>2.3 GB</c>.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1_000)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        if (bytes < 1_000_000)
        {
            return (bytes / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        if (bytes < 1_000_000_000)
        {
            return (bytes / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        return (bytes / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary><c>1,234 bytes</c>: the exact count, for a file just written or inspected.</summary>
    public static string Bytes(long bytes) => bytes.ToString("N0", CultureInfo.InvariantCulture) + (bytes == 1 ? " byte" : " bytes");

    /// <summary><c>2026-09-12 14:05</c> in the local zone the result already carries.</summary>
    public static string Moment(DateTimeOffset local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Count(int n, string singular, string plural) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural);

    /// <summary>The tail of a cut list: <c>… only the first 50 matches are shown</c>. Pinned.</summary>
    public static string OnlyFirst(int n, string what) =>
        "… only the first " + n.ToString(CultureInfo.InvariantCulture) + " " + what + " are shown";
}
