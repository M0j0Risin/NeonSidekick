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
    /// <summary>How the root is named when no other name is in force (<see cref="NamingRoot"/>).</summary>
    public const string DefaultRootName = "the working directory";

    private static readonly AsyncLocal<string?> s_rootName = new();

    /// <summary>
    /// How the sentences name the root: <see cref="DefaultRootName"/>, or a UNC share's name while <see cref="NamingRoot"/> holds (2026-10-22,
    /// review H2: a share's result had every "the working directory" replaced after it was built, the read file's own text included, so
    /// unc_read showed altered lines and a patch copied from them missed or wrote the change back).
    /// </summary>
    public static string RootName => s_rootName.Value ?? DefaultRootName;

    /// <summary>Names the root <paramref name="name"/> for the sentences built until the scope is disposed, on this flow and what it awaits.</summary>
    public static IDisposable NamingRoot(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string? before = s_rootName.Value;
        s_rootName.Value = name;
        return new RootNameScope(before);
    }

    private sealed class RootNameScope(string? before) : IDisposable
    {
        public void Dispose() => s_rootName.Value = before;
    }

    /// <summary>What <see cref="Describe"/> adds after the quoted path when the root is the profile's own folder. Pinned.</summary>
    public const string DefaultNote = " (the profile's default folder)";
    /// <summary>How <see cref="Describe"/> ends: the rule the file tools' <c>path</c> follows. Pinned.</summary>
    public const string RelativeNote = "; every path you pass to a file tool is relative to it";

    /// <summary><c>patch_file</c> with an empty or whitespace-only <c>old_text</c> (the re-send warning is Hermes' lesson: a model loops on it otherwise). Pinned.</summary>
    public const string EditEmpty = "Error: old_text is empty or only whitespace; give the exact text to replace, as read_file shows it, and do not send this call again unchanged";
    /// <summary><c>patch_file</c> with <c>new_text</c> = <c>old_text</c> (2026-09-18; a model probed a file's tail with a no-op edit). Pinned.</summary>
    public const string EditSame = "Error: old_text and new_text are the same; nothing to change";
    public static string RootItself => "Error: that is " + RootName + " itself";

    /// <summary>
    /// The bin's name on this system (later on 2026-10-15, <c>File deletes to Recycle bin</c>): "Recycle bin", or "Trash" on a Mac — the one
    /// place the word is chosen (the setting's own name, its help, the delete sentences). Pinned.
    /// </summary>
    public static string RecycleBinName => OperatingSystem.IsMacOS() ? "Trash" : "Recycle bin";

    /// <summary>A path the shell's bin cannot take (past <c>MAX_PATH</c>, or a long-path prefix): nothing deleted, the setting named. Pinned.</summary>
    public static string RecycleTooLong => "the " + RecycleBinName + " can't take a path that long: turn off File deletes to " + RecycleBinName + " to delete it directly";

    /// <summary>A path that is not a full one: the shell would delete it for good, so it is never called with one. Pinned.</summary>
    public static string RecycleNeedsFullPath => "the " + RecycleBinName + " needs a full path";

    /// <summary>The bin did not answer within its time: nothing deleted behind it. Pinned.</summary>
    /// <remarks>Since 2026-10-22 (review H18) it says the move may still finish: a delete reported failed, then the folder vanished.</remarks>
    public static string RecycleNoAnswer => "the " + RecycleBinName + " hasn't answered yet and may still finish the move: check before deleting again";

    /// <summary>The shell reported the move cancelled. Pinned.</summary>
    public static string RecycleCancelled => "the move to the " + RecycleBinName + " was cancelled";

    /// <summary>The bin refused with a code, which is logged and named, never explained (the shell's are partly pre-Win32). Pinned.</summary>
    public static string RecycleRefused(long code) => "the " + RecycleBinName + " refused it (code " + code.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>A drive or volume with no bin, as a purge's question and its notices say it (2026-10-20). Pinned.</summary>
    public static string RecycleNoBinHere => "no " + RecycleBinName + " on that drive";

    /// <summary>An item the bin can't take (2026-10-22, review J1, D11): left where it is, never deleted for good behind the user's back.</summary>
    public static string RecycleWouldDeleteForGood => "the " + RecycleBinName + " can't take it (too big for it, or set to delete at once), so nothing was deleted";

    /// <summary>A delete for good that stopped partway (2026-10-22, review O6): what is left, then why.</summary>
    public static string PartlyDeleted(int left, string why) =>
        "deleted partly: " + Count(left, "item", "items") + " could not be removed and are still there (" + why.Trim() + ")";


    /// <summary>A purge's folder whose path is already past what the shell's bin takes (2026-10-20, <see cref="IRecycleBin.Probe"/>). Pinned.</summary>
    public static string RecycleFolderTooLong => "the " + RecycleBinName + " can't take paths that long";

    /// <summary>A word argument outside its choices (<c>mode</c>, <c>output</c>, <c>order</c>; 2026-09-19). Pinned.</summary>
    public static string BadChoice(string argument, string raw, string choices) => $"Error: '{raw.Trim()}' is not one of {choices} for '{argument}'";
    /// <summary>A required path argument left out (2026-09-18: a `write_file` with `content` alone was told `'' is a folder`). Pinned.</summary>
    public static string PathRequired(string argument) => $"Error: {argument} is required: the file or folder, relative to {RootName}";

    /// <summary>
    /// <see cref="PathRequired"/> for a tool that takes one path or several (2026-11-15, <c>read_file</c>'s <c>paths</c>; "the file" since the
    /// tool-arguments review's #127, as <c>read_file</c> reads no folder). Pinned.
    /// </summary>
    public static string PathsRequired => $"Error: path (or paths, for several) is required: the file, relative to {RootName}";

    /// <summary>
    /// A <c>paths</c> sent that names nothing, <c>[]</c> or blanks alone (2026-11-15, the tool-arguments review's #143: <c>file_info</c> read it
    /// as the working directory). Pinned.
    /// </summary>
    public const string PathsNamedNothing = "Error: paths names nothing: list the files, or leave paths out";

    /// <summary>A window asked of a read of several files (2026-11-15): start_line and max_lines name one file's lines. Pinned.</summary>
    public const string WindowNeedsOneFile = "Error: start_line and max_lines go with one file; read the files one at a time for a window";

    /// <summary>
    /// A write with no <c>content</c> at all (2026-10-17, the toki-pona session: <c>write_file</c> sent with <c>old_text</c> and <c>new_text</c>
    /// and no <c>content</c> replaced a 27 KB file with nothing). <paramref name="sentEdit"/>, when old_text or new_text came instead, points
    /// at <paramref name="patchTool"/>. Pinned.
    /// </summary>
    public static string ContentRequired(string patchTool, bool sentEdit) =>
        "Error: content is required: the whole text to write (\"\" for an empty file)" + (sentEdit ? "; to change part of a file use " + patchTool : "");

    /// <summary>
    /// A write given <c>content</c> and an edit argument too (2026-11-15, the tool-arguments review's #1, #67): which was meant is unknown, so
    /// nothing is written. <paramref name="patchTool"/> is where an edit belongs.
    /// </summary>
    public static string ContentWithEdit(string patchTool) =>
        "Error: content replaces the whole file, and an edit belongs to " + patchTool + "; send one or the other. Nothing was written.";

    /// <summary>
    /// A write whose <paramref name="argument"/> is a pruned stub copied back from the history (2026-10-17, with call arguments pruned:
    /// <c>Llm.ConversationCompactor.IsPrunedStub</c>) — it would replace the text with the stub. Pinned.
    /// </summary>
    public static string PrunedStubSent(string argument) =>
        $"Error: {argument} is a pruned stub, not the text; read the file again (or load_skill) and send the whole text";

    // A to save's root (2026-10-15, the AD gaps plan; moved from DirectoryText 2026-10-29, the Office writing plan, already generic, when the
    // database exports took up SaveRoot).

    /// <summary>A <c>to</c> save with *File tools* off. Pinned.</summary>
    public const string SaveNeedsFileTools = "Error: to saves a file, which needs File tools on (/tools › Files › File).";

    /// <summary>A query's <c>to</c> with <c>convert_to_xlsx</c> turned off (2026-10-29, review P3-3). Pinned.</summary>
    public const string SaveNeedsXlsx = "Error: writing query results to a file needs convert_to_xlsx on (/tools › Offered).";

    /// <summary>A <c>to</c> save in plan mode. Pinned.</summary>
    public const string SaveInPlanMode = "Error: to saves a file, and plan mode writes no files.";

    /// <summary>A <c>to</c> save by a caller with no root at all (a redacted twin). Pinned.</summary>
    public const string SaveNotHere = "Error: to saves a file, which this caller may not do.";

    /// <summary>A <c>to</c> save found closed by the time the answer came (2026-10-23, F7). Pinned.</summary>
    public static string SaveClosedLate(string reason) => "Not saved: " + (reason ?? "").Replace("Error: ", "", StringComparison.Ordinal);

    /// <summary>
    /// A <c>to</c> save whose working folder changed while the rows were written (2026-10-29, review P7-7: the file went into the old folder
    /// under a path named for the new one). Pinned.
    /// </summary>
    public const string SaveMovedLate = "Not saved: the working folder changed while the rows were written; run the query again";

    /// <summary>
    /// <c>write_file</c> given an Office path it would only break (2026-10-29, the Office writing plan: XML text written into a <c>.docx</c> made
    /// a file Word can't open): a Word or Excel kind points to its converter. Pinned.
    /// </summary>
    public static string OfficeNeedsConverter(string path, string tool) =>
        $"Error: '{path}' is an Office file, which write_file would break; use {tool} to make it (from Markdown or a table)";

    /// <summary><c>unc_write</c> given a <c>.docx</c> or <c>.xlsx</c> path (2026-10-29, review P1-9): the converter writes locally only. Pinned.</summary>
    public static string OfficeOnShare(string path, string tool) =>
        $"Error: '{path}' is an Office file, which unc_write would break; make it in the working directory with {tool}, then unc_put it there";

    /// <summary><c>write_file</c> given a PowerPoint, legacy, macro, template, binary-workbook or OpenDocument path, which no tool writes. Pinned.</summary>
    public static string OfficeNotWritten(string path) =>
        $"Error: '{path}' is an office document no tool writes; convert_to_docx and convert_to_xlsx make .docx and .xlsx files";

    /// <summary>An edit with no <c>new_text</c> at all (2026-10-17): read as <c>""</c>, it removed old_text. Pinned.</summary>
    public const string NewTextRequired = "Error: new_text is required: the text to put in place of old_text (\"\" removes it)";
    /// <summary>A <c>replace_all</c> whose result would be too big to read back (2026-10-22, review V2).</summary>
    public static string EditResultTooBig(int matches) =>
        "the edit would make the file over " + FuzzyMatch.MaxResultChars.ToString("N0", CultureInfo.InvariantCulture) + " characters (" +
        Count(matches, "match", "matches") + "), more than read_file can open; nothing was changed";

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
    public static string Name(string relative) => string.IsNullOrEmpty(relative) ? RootName : Shown(relative);

    /// <summary>
    /// A name as a sentence shows it (2026-10-22, review P5, the fix plan's mechanism 6): bidi controls, zero-width and control characters
    /// written as escapes (<see cref="NameScrub.Display"/>), so a name never disguises itself, forges a line or reaches the terminal.
    /// </summary>
    public static string Shown(string? name) => NameScrub.Display(name);

    // ---- errors ----

    public static string OutsideRoot(string path) => $"Error: '{path}' is outside {RootName}; every path must stay inside it";

    /// <summary>
    /// A path refused as outside that lies in the profile's images folder (<see cref="FileOutcome.InImagesFolder"/>, 2026-10-09): where the
    /// way in is. It named six tools until code review on 2026-10-11, whatever this turn offered (the picture tools are off by default,
    /// <c>set_splash_image</c> was missing, and <c>open</c> built without the folder sent the model back to itself); now it points at the
    /// descriptions, which name the folder (or a saved picture's full path) only on the tools that reach it. Pinned.
    /// </summary>
    public static string InImagesFolder(string path) => $"Error: '{path}' is in the user's images folder, outside the working directory, and this tool cannot reach it; the tools that can say so in their descriptions (a full path in the user's images folder, or a saved picture's full path)";

    /// <summary>
    /// A path refused as outside that lies in a skill folder (<see cref="FileOutcome.InSkillFolder"/>, 2026-10-17, the toki-pona session: the
    /// model found <c>load_skill</c> on its third try). It names no parts: the path itself shows the skill. Pinned.
    /// </summary>
    public static string InSkillFolder(string path) =>
        $"Error: '{path}' is a skill's file, outside the working directory; read it with load_skill (name: the skill's folder name, file: the path inside that folder) and change it with skill_editor";

    /// <summary>
    /// A path refused as outside that lies in a trusted project skills folder above the working folder (<see cref="FileOutcome.InProjectSkillFolder"/>,
    /// 2026-11-23, feature idea #16): <see cref="InSkillFolder"/>'s pointer to <c>load_skill</c>, without its <c>skill_editor</c>, which never writes a
    /// repo's skill. Pinned.
    /// </summary>
    public static string InProjectSkillFolder(string path) =>
        $"Error: '{path}' is a file of this repo's skill, outside the working directory; read it with load_skill (name: the skill's name, file: the path inside its folder); it's the repo's skill and isn't changed from here";

    /// <summary><c>open</c> on a file in the images folder that is no picture (code review, 2026-10-11): the shell would run it. Pinned.</summary>
    /// <summary>
    /// <c>open</c> on a file it may not start (2026-10-22, review G1, the fix plan's D1: an allow-list of safe types): the kind, what does
    /// open, and the user's own way. Prompt text for the model; the kinds are <see cref="OpenRules"/>'.
    /// </summary>
    public static string OpenRefused(string path, OpenRefusal kind)
    {
        string what = kind switch
        {
            OpenRefusal.Program => "a program",
            OpenRefusal.Script => "a script",
            OpenRefusal.Shortcut => "a shortcut or link",
            OpenRefusal.Installer => "an installer",
            _ => "a kind of file open does not start",
        };
        return $"Error: '{path}' is {what}: open shows only folders, documents, pictures, PDFs, audio, video and plain text; the user can open it themselves with /open";
    }

    public static string OpenImagesFolderNotPicture(string path) => $"Error: '{path}' is not a picture: in the user's images folder open shows only pictures and folders";

    /// <summary>
    /// What <c>search_files</c>' description adds while the images folder is offered (2026-10-09, the user's call: a model asked for
    /// the newest pictures listed <c>images</c>, a guess, and then could not list the folder at all). Pinned.
    /// </summary>
    public static string ImagesFolderSearchSentence(string root) =>
        $"The user's images folder, {root}, where camera, screen, generate_image and pasted pictures are saved, is outside the working directory: " +
        "give its full path (or a subfolder's) as path to list or search it, with order modified for the newest; its results are full paths, which the picture tools (view_image, image_info, image_edit, while Image tools is on) take.";

    /// <summary><c>file_info</c>'s description's addition while the images folder is offered (2026-10-09). Pinned.</summary>
    public static string ImagesFolderInfoSentence(string root) => $"A full path in the user's images folder ({root}) works too.";
    public static string Missing(string path) => $"Error: nothing is at '{path}'";
    public static string IsDirectory(string path) => $"Error: '{path}' is a folder, not a file";
    public static string IsAFile(string path) => $"Error: '{path}' is a file, not a folder";
    /// <summary>
    /// A binary file; an image by extension is pointed at <c>view_image</c> (a small model tries <c>read_file</c> on a PNG first), and
    /// a PDF at <c>read_pdf</c> (2026-10-14, the PDF plan), an Office file at <c>read_office</c> and a legacy or OpenDocument one at a
    /// "save it as" (2026-10-28, the Office plan).
    /// </summary>
    public static string NotText(string path) =>
        $"Error: '{path}' is not a text file" + (ImageFile.IsImagePath(path) ? ViewImageHint : PdfFile.IsPdfPath(path) ? ReadPdfHint
            : OfficeFile.IsOfficePath(path) ? ReadOfficeHint : OfficeFile.IsAnyOfficePath(path) ? SaveAsOfficeHint : "");

    /// <summary>What <see cref="NotText"/> adds for an image path. Pinned.</summary>
    public const string ViewImageHint = "; the picture tools (view_image, while Image tools is on) look at it";

    /// <summary>What <see cref="NotText"/> adds for a PDF (2026-10-14). Pinned.</summary>
    public const string ReadPdfHint = "; use read_pdf to read its text";

    /// <summary>What <see cref="NotText"/> adds for a Word, Excel or PowerPoint file (2026-10-28, the Office plan). Pinned.</summary>
    public const string ReadOfficeHint = "; use read_office to read it";

    /// <summary>What <see cref="NotText"/> adds for a legacy, binary or OpenDocument office file, which <c>read_office</c> doesn't read (2026-10-28). Pinned.</summary>
    public const string SaveAsOfficeHint = "; read_office reads it once it is saved as .docx, .xlsx or .pptx";

    /// <summary>A file the picture codecs refuse; a PDF is pointed at <c>read_pdf</c> and its page pictures (2026-10-14, the PDF plan's gap 70).</summary>
    public static string NotAnImage(string path) => $"Error: '{path}' could not be read as an image" + (PdfFile.IsPdfPath(path) ? PdfPicturesHint : "");

    /// <summary>What <see cref="NotAnImage"/> adds for a PDF (2026-10-14). Pinned.</summary>
    public const string PdfPicturesHint = "; it is a PDF: use read_pdf (pictures: true for its pages)";
    public static string ImageTooBig(string path) =>
        $"Error: '{path}' is over {(ImageFile.MaxFileBytes / 1_000_000).ToString(CultureInfo.InvariantCulture)} MB or {(ImageFile.MaxPixels / 1_000_000).ToString(CultureInfo.InvariantCulture)} megapixels; too large to view";

    /// <summary>The sentences a picture left out of a message gives (2026-10-22, review N11: they lived in ImageFile.cs, outside a *Text.cs).</summary>
    public static string ImageNotFound(string path) => $"(image not attached: {Shown(path)} was not found)";

    public static string ImageTooLarge(string path) =>
        $"(image not attached: {Shown(path)} is over {(ImageFile.MaxFileBytes / 1_000_000).ToString(CultureInfo.InvariantCulture)} MB or {(ImageFile.MaxPixels / 1_000_000).ToString(CultureInfo.InvariantCulture)} megapixels)";

    public static string ImageCouldNotRead(string path) => $"(image not attached: {Shown(path)} could not be read as an image)";

    public static string ImageUnsupported(string path) => $"(image not attached: {Shown(path)}; {ImageCodecs.Unavailable})";

    public static string ImageUnreadable(string path) => $"(image not attached: {Shown(path)} could not be opened; it may be in use or not yours to read)";
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
        $"Error: old_text and new_text hold \\{quote} but the matched text in '{path}' does not; the backslash is a serialization artifact — read the file again and pass both without it";

    /// <summary>Every backslash run in <c>old_text</c> is twice the file's (2026-09-19). Pinned.</summary>
    public static string EscapeDriftDoubled(string path) =>
        $"Error: every backslash run in old_text is twice as long as in '{path}'; read the file again and pass old_text and new_text with the backslashes as the file has them";

    /// <summary>A <c>patch_file</c> whose <c>new_text</c> is in the file and whose <c>old_text</c> is not: done before, nothing written (2026-09-19). Not an error. Pinned.</summary>
    /// <summary>
    /// An <c>edits</c> list that leaves the file as it was (2026-11-15, the tool-arguments review's #61, #134): nothing saved or journaled. Not an
    /// error, as <see cref="AlreadyApplied"/> isn't. Pinned.
    /// </summary>
    public static string NoNetChange(string path) => $"nothing changed in {path}: the edits leave it as it was, so nothing was saved";

    /// <summary>
    /// An edit of a list whose <c>new_text</c> is in the file and whose <c>old_text</c> isn't, beside edits that still apply (2026-11-15, the
    /// tool-arguments review's #5, #137): skipped, it broke all or none. The "edit N of M" prefix names it. Pinned.
    /// </summary>
    public static string NewTextOnly(string path) =>
        $"Error: its new_text is in {path} but its old_text isn't, while other edits still apply; nothing changed. Read the file and send the edits that are left";

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
        $"Error: '{path}' is in, is or holds a {WorkingDirectory.GitFolderName} folder, which the file tools never change, move or delete; git's own tools change a repository";

    // ── the write guard's refusals (2026-10-22, the fix plan's mechanism 1 and D2: each names the kind and the user's own way) ──

    /// <summary>A write into <c>.neon</c> (review P4). Prompt text.</summary>
    public static string NeonFilesProtected(string path) =>
        $"Error: '{path}' is in {WriteGuard.NeonFolderName}, where the app keeps plans, audits and loop files it trusts; present_plan and present_audit write them, and the user edits a plan with /plan edit";

    /// <summary>A write into the app's home or a profile (review P2/W1). Prompt text.</summary>
    public static string AppFilesProtected(string path) =>
        $"Error: '{path}' is in or holds the app's own settings, configs or databases, which the file tools never change; the user changes them in /settings, /tools and the other panes";

    /// <summary>A write into a package's install folder (2026-11-04, self-update). Prompt text.</summary>
    public static string InstallFilesProtected(string path) =>
        $"Error: '{path}' is in or holds the folder the app is installed in, which the file tools never change; the user updates the app with /update";

    /// <summary>A write into a skill root (review P3). Prompt text.</summary>
    public static string SkillFilesProtected(string path) =>
        $"Error: '{path}' is in a skill folder; change a skill only with skill_editor";

    /// <summary>A write into a custom commands folder or its link's target (2026-10-31, review P2-2). Prompt text.</summary>
    public static string CommandFilesProtected(string path) =>
        $"Error: '{path}' is in the user's custom commands folder, which the file tools never change; the user changes commands with /commands";

    /// <summary>A write into <c>.claude</c> or a root <c>.mcp.json</c> (2026-11-02, review P12-2). Prompt text.</summary>
    public static string ClaudeFilesProtected(string path) =>
        $"Error: '{path}' is Claude Code's settings, hooks or servers, which /claude loads and the file tools never change; the user changes them";

    /// <summary>A write into a repo's <c>.agents/skills</c> (2026-11-23, review #25). Prompt text.</summary>
    public static string ProjectSkillsProtected(string path) =>
        $"Error: '{path}' is in the repo's own skills (.agents/skills), which the user changes; the file tools and skill_editor never do";

    /// <summary>The project file with nobody to ask (review P1, D2): headless, a bot, an advisor, a script, a pass that may not ask. Prompt text.</summary>
    public static string ProjectFileRefused(string path) =>
        $"Error: '{path}' is the project file the system prompt reads every turn; it changes only when the user allows it on the screen, and nobody can be asked now: tell the user the change, and they can make it";

    /// <summary>The project file the user was asked about and refused (review P1, D2). Prompt text.</summary>
    public static string ProjectFileDeclined(string path) =>
        $"Error: the user did not allow the change to '{path}', the project file the system prompt reads every turn; do not try another way";

    /// <summary>
    /// The clause the writer tools' descriptions end on (2026-10-22, the write guard), so the model doesn't spend calls finding the refusals out.
    /// Prompt text.
    /// </summary>
    public const string GuardedPathsSentence =
        " .git, .neon, .claude, .agents/skills, a root .mcp.json and the app's own settings are never written; the project file (NEON.md, AGENTS.md, CLAUDE.md) changes only when the user allows it.";

    /// <summary>The project file's allow pane's title (review P1, D2).</summary>
    public const string ProjectFileAllowTitle = "Change the project file?";

    /// <summary>The project file's allow pane's caption: the file, and why it asks.</summary>
    public static string ProjectFileAllowCaption(string name) => $"The model wants to change {name}, which the system prompt reads every turn.";

    /// <summary>
    /// New text the file's encoding can't hold (2026-10-22, review G4/Q1, <see cref="FileOutcome.NotEncodable"/>): refused rather than written as
    /// '?'. Prompt text.
    /// </summary>
    public static string CannotEncode(string path, string encoding) =>
        $"Error: '{path}' is in {encoding}, which can't hold every character of the new text (an emoji, a letter outside it); nothing was written: use only characters that file can hold, or rewrite it whole as UTF-8 with mode overwrite after deleting it";

    /// <summary>A search needle holding a line break (2026-10-22, review Q9). Prompt text.</summary>
    public const string SearchOneLine = "Error: search_files matches within one line, and the text holds a line break; search for one line of it (the most distinctive), then read_file around the hits";

    /// <summary>A patch or append whose file changed between its read and its write (2026-10-22, review J4): nothing written.</summary>
    public const string ChangedWhileEditing = "the file changed while it was being edited (another write landed), so nothing was written: read it again and redo the change";

    /// <summary>A write refused because its target is read-only (2026-10-22, review O2, mechanism 7).</summary>
    public const string ReadOnlyDetail = "the file is read-only, and the file tools never write over a read-only file";

    /// <summary>The detail of a write refused because a link on its way now leads outside (review G13, <see cref="WorkingDirectory.RecheckLinks"/>).</summary>
    public static string LinkMovedDetail => "a folder on the way was swapped for a link that leads outside " + RootName + ", so nothing was written";

    /// <summary>A path naming an NTFS stream (review M2). Prompt text.</summary>
    public static string StreamName(string path) =>
        $"Error: '{path}' names a file's hidden stream (the part after ':'), which the file tools never reach; use a plain file name";

    /// <summary>A Windows device name (review O10). Prompt text.</summary>
    public static string DeviceName(string path) =>
        $"Error: '{path}' is a name Windows keeps for a device (NUL, CON, PRN, AUX, COM1–9, LPT1–9), not a file; pick another name";

    /// <summary>A FIFO, socket or device file (review N1/N2). Prompt text.</summary>
    public static string NotARegularFile(string path) =>
        $"Error: '{path}' is a pipe, socket or device, not a file; the file tools read and write only plain files";

    /// <summary>A <c>~/</c> or <c>file:</c> path (review S5). Prompt text.</summary>
    public static string NotAPathHere(string path) =>
        $"Error: '{path}' is a home-folder or file: URL form, which paths here never are; give a path relative to the working directory (get_working_directory names it)";

    /// <summary>The working directory deleted or renamed this session (review S6). Prompt text.</summary>
    public const string RootGone =
        "Error: the working directory was deleted or moved during this session, and the file tools don't make it again; ask the user to restore it or pick another with /cwd";

    /// <summary>
    /// A file tool's change to a SQLite database file, or a delete or move of a folder holding one, while the SQLite tools are on
    /// (2026-10-05, the user's call): what to use instead, and not to go around it. A DuckDB database's too since 2026-10-25, while the
    /// DuckDB tools are on. Pinned.
    /// </summary>
    public static string DatabaseProtected(string path) =>
        $"Error: '{path}' is or holds a SQLite or DuckDB database, which the file tools never change while that database's tools are on; " +
        "change a database only through its own tools (sqlite_execute or duckdb_execute, in that one's read-write mode, when the user allows it), and do not try another way";

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
    /// <summary>
    /// A list argument that is no list of strings. The raw value is echoed short (2026-11-15: it is the array's whole JSON, so a broken 2 MB
    /// <c>paths</c> came back as a 2 MB answer). Pinned.
    /// </summary>
    public static string BadStringList(string argument, string raw) => $"Error: '{Llm.Tools.ArgumentText.Echo(raw.Trim())}' is not a list of paths for '{argument}'";
    public static string ZipSlip(string archive, string entry) => $"Error: '{archive}' holds an entry that would land outside the destination ('{entry}'); nothing was extracted";
    public static string CouldNot(string verb, string path, string detail) => $"Error: could not {verb} '{Name(path)}': {detail}";

    // A restore's details (2026-10-28, the checkpoints plan: /undo and /rewind … files), shown beside a file it couldn't put back.

    /// <summary>A path the guards refused to a restore, without the tools' <c>Error:</c>.</summary>
    public static string RestoreRefused(FileOutcome outcome, string path)
    {
        string sentence = Error(outcome, path, "put back");
        return sentence.StartsWith("Error: ", StringComparison.Ordinal) ? sentence["Error: ".Length..] : sentence;
    }

    public static string RestoreRootGone(string root) => $"its folder {Shown(root)} is gone";

    public const string RestoreBlobMissing = "its saved copy is missing";

    public const string RestoreAppeared = "a file appeared there meanwhile";

    public static string RestoreMovedGone(string to) => $"'{Shown(to)}' is no longer there to move back";

    /// <summary>
    /// A link or junction that can't be made again without the right to make links (2026-10-28, review P9-02, the user's pick: a clear failure,
    /// no junction code): Windows gives it to administrators and to Developer Mode.
    /// </summary>
    public const string RestoreLinkNeedsPrivilege = "making a link needs Developer Mode or an administrator here; make it again by hand";

    /// <summary>The sentence for a non-<see cref="FileOutcome.Ok"/> outcome; <paramref name="verb"/> names the operation for a failure.</summary>
    public static string Error(FileOutcome outcome, string path, string verb, string detail = "", string other = "")
    {
        // The names scrubbed as they enter every refusal (review P5).
        path = Shown(path);
        other = Shown(other);
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
            FileOutcome.NoNetChange => NoNetChange(path),
            FileOutcome.NewTextOnly => NewTextOnly(path),
            FileOutcome.FolderInTheWay => FolderInTheWay(path),
            FileOutcome.GitProtected => GitProtected(path),
            FileOutcome.DatabaseProtected => DatabaseProtected(path),
            FileOutcome.InImagesFolder => InImagesFolder(path),
            FileOutcome.InSkillFolder => InSkillFolder(path),
            FileOutcome.InProjectSkillFolder => InProjectSkillFolder(path),
            FileOutcome.NeonFilesProtected => NeonFilesProtected(path),
            FileOutcome.AppFilesProtected => AppFilesProtected(path),
            FileOutcome.InstallFilesProtected => InstallFilesProtected(path),
            FileOutcome.SkillFilesProtected => SkillFilesProtected(path),
            FileOutcome.CommandFilesProtected => CommandFilesProtected(path),
            FileOutcome.ClaudeFilesProtected => ClaudeFilesProtected(path),
            FileOutcome.ProjectSkillsProtected => ProjectSkillsProtected(path),
            FileOutcome.ProjectFileAsk => ProjectFileRefused(path),
            FileOutcome.ShoppingList => Shopping.ShoppingText.NotAFile(path),
            FileOutcome.StreamName => StreamName(path),
            FileOutcome.DeviceName => DeviceName(path),
            FileOutcome.NotARegularFile => NotARegularFile(path),
            FileOutcome.NotAPathHere => NotAPathHere(path),
            FileOutcome.RootGone => RootGone,
            FileOutcome.NotEncodable => CannotEncode(path, detail),
            FileOutcome.OneLineOnly => SearchOneLine,
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

    /// <summary>
    /// The note a read-side walk ends on when it left hidden entries out (2026-10-22, review Q10, D14): every listing, find, search and recent
    /// leaves Hidden and System entries (and a Mac's hidden ones) out, and says how many. Empty for none.
    /// </summary>
    public static string HiddenNote(int hidden) =>
        hidden <= 0 ? "" : "\n(" + Count(hidden, "hidden or system entry", "hidden or system entries") + " left out)";

    public static string Listing(ListResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, Shown(result.Relative), "list", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Name(Shown(result.Relative))).Append(" (").Append(Count(result.Entries.Count, "entry", "entries")).Append("):");
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

        sb.Append(HiddenNote(result.Hidden));
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
            return Error(result.Outcome, Shown(result.Relative), "list", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Name(Shown(result.Relative))).Append(" (").Append(Count(result.Entries.Count, "entry", "entries"))
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
            return Error(result.Outcome, Shown(result.Relative), "search", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Count(result.Paths.Count, "file matches", "files match")).Append(" '").Append(pattern).Append("' under ").Append(Name(Shown(result.Relative))).Append(':');
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

        sb.Append(HiddenNote(result.Hidden));
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
            return Error(result.Outcome, Shown(result.Relative), "search", result.Detail);
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
            if (withContext && lastFile is not null && !string.Equals(lastFile, Shown(hit.RelativePath), StringComparison.Ordinal))
            {
                sb.Append('\n').Append(ContextSeparator);
            }

            lastFile = Shown(hit.RelativePath);
            for (int i = 0; i < hit.Before.Count; i++)
            {
                sb.Append('\n').Append(Shown(hit.RelativePath)).Append('-').Append((hit.Line - hit.Before.Count + i).ToString(CultureInfo.InvariantCulture)).Append("- ").Append(hit.Before[i]);
            }

            sb.Append('\n').Append(Shown(hit.RelativePath)).Append(':').Append(hit.Line.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(hit.Text);
            for (int i = 0; i < hit.After.Count; i++)
            {
                sb.Append('\n').Append(Shown(hit.RelativePath)).Append('-').Append((hit.Line + 1 + i).ToString(CultureInfo.InvariantCulture)).Append("- ").Append(hit.After[i]);
            }
        }

        if (result.Truncated)
        {
            sb.Append('\n').Append(OnlyFirst(result.Hits.Count, "matches")).Append(NarrowHint);
        }

        SearchTimedOut(sb, result);
        return CutSearch(sb.ToString());
    }

    /// <summary>
    /// A search's answer held to read_file's cap (2026-10-22, review W2: context 5 over 200 hits made 400–900K characters in one result): cut
    /// at a line break before <see cref="WorkingDirectory.MaxReadChars"/>, with a note to narrow it.
    /// </summary>
    private static string CutSearch(string answer)
    {
        if (answer.Length <= WorkingDirectory.MaxReadChars)
        {
            return answer;
        }

        int at = answer.LastIndexOf('\n', WorkingDirectory.MaxReadChars - 200);
        string kept = at > 0 ? answer[..at] : TextCut.Safe(answer, WorkingDirectory.MaxReadChars - 200);
        return kept + "\n(cut at " + WorkingDirectory.MaxReadChars.ToString("N0", CultureInfo.InvariantCulture) + " characters: narrow it with path, files, a smaller context or limit)";
    }

    /// <summary>A one-file search that did not search the file (2026-10-22, review W3, V10): why, rather than "0 matches".</summary>
    public static string SearchFileTooBig(string path) => $"{path} was not searched: it is over {WorkingDirectory.MaxTextFileBytes / 1_000_000} MB; read_file reads it in parts";

    public static string SearchFileNotText(string path) => $"{path} was not searched: it is not a text file";

    public static string SearchFileUnreadable(string path, string why) => $"{path} was not searched: it could not be read ({why.Trim()})";

    public static string SearchFileCloudOnly(string path) => $"{path} was not searched: it is kept only in the cloud; read_file fetches it";

    public static string SearchFilterMissesFile(string path, string pattern) => $"{path} was not searched: it does not match files '{pattern}'";

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
            return Error(result.Outcome, Shown(result.Relative), "search", result.Detail);
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
            sb.Append(Shown(result.Relative));
        }
        else
        {
            if (countFiles)
            {
                sb.Append(Count(result.FilesMatched, "file", "files")).Append(' ');
            }

            sb.Append("(searched ").Append(Count(result.FilesSearched, "file", "files")).Append(" under ").Append(Name(Shown(result.Relative)));
        }

        sb.Append(result.SingleFile ? " (" : " in ").Append(result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)).Append(" s):");
    }

    private static void SearchTimedOut(StringBuilder sb, SearchResult result)
    {
        if (result.TimedOut > 0)
        {
            sb.Append('\n').Append(Count(result.TimedOut, "line", "lines")).Append(" skipped: the expression took too long on them");
        }

        // The hidden entries left out, counted (2026-10-22, D14), and why one file named alone was not searched (review W3, V10).
        sb.Append(HiddenNote(result.Hidden));
        if (result.Unsearched is { } why)
        {
            sb.Append('\n').Append(why);
        }

        // A cloud-only file is never fetched by a search (2026-10-22, review O1).
        if (result.CloudOnly > 0)
        {
            sb.Append('\n').Append(Count(result.CloudOnly, "file", "files")).Append(" left unread: kept only in the cloud (OneDrive and the like); read_file on one fetches it");
        }
    }

    public static string Recent(RecentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, Shown(result.Relative), "list", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append("most recently changed under ").Append(Name(Shown(result.Relative))).Append(", newest first:");
        if (result.Entries.Count == 0)
        {
            sb.Append(" (no files)");
        }

        foreach (var entry in result.Entries)
        {
            sb.Append('\n').Append(entry.RelativePath).Append("  ").Append(Moment(entry.Modified)).Append("  ").Append(Size(entry.Length));
        }

        sb.Append(HiddenNote(result.Hidden));
        return sb.ToString();
    }

    public static string Info(InfoResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, Shown(result.Relative), "inspect", result.Detail);
        }

        var sb = new StringBuilder();
        sb.Append(Name(Shown(result.Relative))).Append(" — ");
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

    /// <summary>
    /// The first line of a read of several files (2026-11-15, feature idea #31; the tool-arguments review's #95, #157): how many were read of
    /// how many named, and how many refused, so a refusal in the first file doesn't make the whole result read as failed; then the files
    /// read, by name, as many as keep the line within <see cref="UI.TranscriptRenderer.ToolTextLimit"/> cells (the quiet transcript note
    /// shows this line), the rest counted. <c>read 3 files: a.txt, b.txt, c.txt</c>, <c>read 1 of 3 files (2 refused): a.txt</c>.
    /// </summary>
    public static string ReadMany(int read, int named, int refused, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string head = "read " + (read == named ? Count(read, "file", "files") : read.ToString(CultureInfo.InvariantCulture) + " of " + Count(named, "file", "files"))
            + (refused > 0 ? " (" + refused.ToString(CultureInfo.InvariantCulture) + " refused)" : "") + ":";
        if (names.Count == 0)
        {
            return head;
        }

        var line = new StringBuilder(head);
        for (int i = 0; i < names.Count; i++)
        {
            string next = (i == 0 ? " " : ", ") + Shown(names[i]);
            int left = names.Count - i - 1;
            string more = left > 0 ? ", and " + left.ToString(CultureInfo.InvariantCulture) + " more" : "";
            if (i > 0 && UI.TextCells.Width(line + next + more) > UI.TranscriptRenderer.ToolTextLimit)
            {
                return line + ", and " + (names.Count - i).ToString(CultureInfo.InvariantCulture) + " more";
            }

            line.Append(next);
        }

        return line.ToString();
    }

    /// <summary>
    /// The line that opens each file's block in a read of several (2026-11-15, the tool-arguments review's #95): the file's name between rules,
    /// so one file's text never runs into the next file's header. Pinned.
    /// </summary>
    public static string BatchSeparator(string name) => "===== " + Shown(name) + " =====";

    /// <summary>
    /// The first line of a <c>file_info</c> of several paths (2026-11-15, the tool-arguments review's #78): how many were inspected of how many
    /// named, and how many answered an error. Never an <c>Error:</c> itself: a missing path is an answer here. Pinned.
    /// </summary>
    public static string InfoMany(int shown, int named, int errors) =>
        "inspected " + (shown == named ? Count(shown, "path", "paths") : shown.ToString(CultureInfo.InvariantCulture) + " of " + Count(named, "path", "paths"))
        + (errors > 0 ? " (" + errors.ToString(CultureInfo.InvariantCulture) + " with an error)" : "") + ":";

    /// <summary>The first line when none of several files could be read: an <c>Error:</c>, so the call counts as failed. Pinned.</summary>
    public static string NoneRead(int files) => "Error: none of the " + files.ToString(CultureInfo.InvariantCulture) + " files could be read:";

    /// <summary>
    /// The line after a read of several files that left some unread (2026-11-15; one line for both since the tool-arguments review's #45):
    /// those past the shared <paramref name="limit"/> and those past the <paramref name="cap"/> of a call, and the one call that reads them all.
    /// Names through <see cref="Names"/>. Pinned.
    /// </summary>
    public static string NotRead(IReadOnlyList<string> pastLimit, IReadOnlyList<string> pastCap, int limit, int cap)
    {
        ArgumentNullException.ThrowIfNull(pastLimit);
        ArgumentNullException.ThrowIfNull(pastCap);
        var why = new List<string>(2);
        if (pastLimit.Count > 0)
        {
            why.Add(pastLimit.Count.ToString(CultureInfo.InvariantCulture) + " past the " + limit.ToString("N0", CultureInfo.InvariantCulture) + "-character limit the files share");
        }

        if (pastCap.Count > 0)
        {
            why.Add(pastCap.Count.ToString(CultureInfo.InvariantCulture) + " past the " + cap.ToString(CultureInfo.InvariantCulture) + " files a call reads");
        }

        return (pastLimit.Count + pastCap.Count).ToString(CultureInfo.InvariantCulture) + " more not read (" + string.Join("; ", why)
            + "); call again with paths: " + Names([.. pastLimit, .. pastCap]);
    }

    /// <summary>
    /// Paths as a list line names them (2026-11-15, the tool-arguments review's #37, #113: one helper for every "call again with paths" line):
    /// each shown with its hidden characters escaped (<see cref="Shown"/>, never cut, so the next call can copy it), at most
    /// <see cref="MaxMoreNamed"/>, then <c>and N more</c>.
    /// </summary>
    public static string Names(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return string.Join(", ", names.Take(MaxMoreNamed).Select(name => Shown(name)))
            + (names.Count > MaxMoreNamed ? ", and " + (names.Count - MaxMoreNamed).ToString(CultureInfo.InvariantCulture) + " more" : "");
    }

    /// <summary>The read result: a header naming the window, then the text as it is (numbered on request until 2026-09-19).</summary>
    public static string Read(ReadResult result) => Read(result, inBatch: false);

    /// <summary>
    /// <see cref="Read(ReadResult)"/>, with <paramref name="inBatch"/> for a file in a read of several (2026-11-15, the tool-arguments review's
    /// #122): a cut file's header then says to read it alone, since a window goes with one file.
    /// </summary>
    public static string Read(ReadResult result, bool inBatch)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, Shown(result.Relative), "read", result.Detail);
        }

        string header = ReadHeader(result, inBatch);
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
    /// The last line of a tool given more paths than its cap (2026-09-18, a <c>view_image</c>: 17 paths were refused whole
    /// and the model started over in batches): <c>13 more not shown; call again with paths: e.png, f.png</c>.
    /// </summary>
    public static string MorePaths(IReadOnlyList<string> rest)
    {
        ArgumentNullException.ThrowIfNull(rest);
        // At most MaxMoreNamed names, the rest counted (2026-10-22, review T9: every remaining path was echoed).
        return rest.Count.ToString(CultureInfo.InvariantCulture) + " more not shown; call again with paths: " + Names(rest);
    }

    /// <summary>A picture read where no picture codecs are (review K11). Prompt text.</summary>
    public const string NoPictureCodecs = "no picture codecs are available on this system";

    /// <summary>The most remaining paths a "more pictures" line names (review T9, the fix plan's Limits).</summary>
    public const int MaxMoreNamed = 20;

    /// <summary><c>ladybug.png (1024×768 image/png, 213.4 KB): the picture is in the next message</c> — the size the sent bytes, after any downscale.</summary>
    public static string Image(ImageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok || result.Image is null)
        {
            return Error(result.Outcome, Shown(result.Relative), "view", result.Detail);
        }

        var image = result.Image;
        return Shown(result.Relative) + " (" + image.Width.ToString(CultureInfo.InvariantCulture) + "×" + image.Height.ToString(CultureInfo.InvariantCulture)
            + " " + image.MediaType + ", " + Size(image.Bytes.Length) + "): " + ImageFollows;
    }

    /// <summary><c>notes.txt (lines 1–40 of 48):</c>, or the empty and past-the-end forms.</summary>
    public static string ReadHeader(ReadResult result) => ReadHeader(result, inBatch: false);

    /// <summary><see cref="ReadHeader(ReadResult)"/>; in a read of several, a partial file's next step is to read it alone (#122).</summary>
    public static string ReadHeader(ReadResult result, bool inBatch)
    {
        ArgumentNullException.ThrowIfNull(result);
        string total = result.TotalLines.ToString(CultureInfo.InvariantCulture);
        if (result.TotalLines == 0)
        {
            return Shown(result.Relative) + " (empty)";
        }

        if (result.ToLine < result.FromLine)
        {
            return Shown(result.Relative) + " has only " + Count(result.TotalLines, "line", "lines") + "; nothing from line " + result.FromLine.ToString(CultureInfo.InvariantCulture);
        }

        string window = result.FromLine == 1 && result.ToLine == result.TotalLines && !result.Truncated
            ? Count(result.TotalLines, "line", "lines")
            : "lines " + result.FromLine.ToString(CultureInfo.InvariantCulture) + "–" + result.ToLine.ToString(CultureInfo.InvariantCulture) + " of " + total;
        // The read's own cap (2026-11-15: a file in a read of several gets what the shared limit has left).
        string tail = result.Truncated ? "; cut at " + result.Cap.ToString("N0", CultureInfo.InvariantCulture) + " characters" : "";
        // A cut line said (2026-10-22, review Q2: a one-line 50,000-character file read as complete), and the encoding when it is not UTF-8 (Q1).
        string cut = result.CutLine > 0
            ? "; line " + result.CutLine.ToString(CultureInfo.InvariantCulture) + " is " + result.CutLength.ToString("N0", CultureInfo.InvariantCulture) + " characters long, cut at " + result.Cap.ToString("N0", CultureInfo.InvariantCulture)
            : "";
        string encoding = result.Encoding is { } name ? "; read as " + name : "";
        string next = result.ToLine < result.TotalLines ? (inBatch ? AloneHint(result.ToLine + 1) : NextHint(result.ToLine + 1)) : "";
        return Shown(result.Relative) + " (" + window + tail + cut + encoding + next + "):";
    }

    /// <summary>
    /// What a partial read's header ends with (2026-09-18, after a model read the top 200 lines of a
    /// 1,900-line file 22 times running): the call that continues it, spelled out.
    /// </summary>
    public static string NextHint(int startLine) => "; next: start_line " + startLine.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// <see cref="NextHint"/> for a file cut in a read of several (2026-11-15, the tool-arguments review's #122): a window goes with one file,
    /// so the call that continues it reads the file alone.
    /// </summary>
    public static string AloneHint(int startLine) => "; next: read it alone with start_line " + startLine.ToString(CultureInfo.InvariantCulture);

    // ---- write side ----

    public static string Wrote(WriteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == FileOutcome.Exists)
        {
            return WriteExists(Shown(result.Relative));
        }

        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, Shown(result.Relative), "write", result.Detail);
        }

        return (result.Replaced ? "replaced " : "wrote ") + Shown(result.Relative) + " (" + Bytes(result.Bytes) + Counts(result.Lines, result.Words) + ")";
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
            return Error(result.Outcome, Shown(result.Relative), "append to", result.Detail);
        }

        if (!result.Replaced)
        {
            return "created " + Shown(result.Relative) + " (" + Bytes(result.Bytes) + Counts(result.Lines, result.Words) + ")";
        }

        string now = result.Lines is { } && result.Words is { } ? " (now" + Counts(result.Lines, result.Words)[1..] + ")" : "";
        return "appended " + Bytes(result.Bytes) + " to " + Shown(result.Relative) + now;
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
        // A patch_file edits list (2026-11-15): its refusal names the edit, its success every edit's lines.
        if (result.FailedEdit > 0)
        {
            return EditOfList(result.FailedEdit, result.EditCount, Edited(result with { FailedEdit = 0 }), shifted: !result.FailedBeforeRun);
        }

        if (result.Outcome == FileOutcome.NoNetChange)
        {
            return NoNetChange(Shown(result.Relative));
        }

        if (result.Edits.Count > 0 && result.Outcome == FileOutcome.Ok)
        {
            return EditedMany(result);
        }

        if (result.Outcome == FileOutcome.AlreadyApplied)
        {
            return AlreadyApplied(Shown(result.Relative));
        }

        if (result.Outcome == FileOutcome.EditAmbiguous)
        {
            return EditAmbiguous(Shown(result.Relative), result.Count, result.Locations);
        }

        if (result.Outcome != FileOutcome.Ok)
        {
            string detail = result.Outcome switch
            {
                FileOutcome.ApproximateAll => result.Count.ToString(CultureInfo.InvariantCulture),
                FileOutcome.EscapeDrift => result.Drift.ToString(),
                _ => result.Detail,
            };
            return Error(result.Outcome, Shown(result.Relative), "edit", detail);
        }

        var sb = new StringBuilder();
        if (result.Lines.Count > 1)
        {
            sb.Append("replaced ").Append(Count(result.Lines.Count, "occurrence", "occurrences")).Append(" of old_text in ").Append(Shown(result.Relative))
              .Append(" (lines ").Append(string.Join(", ", result.Lines.Take(MaxListedLines).Select(l => l.ToString(CultureInfo.InvariantCulture))));
            if (result.Lines.Count > MaxListedLines)
            {
                sb.Append(", …");
            }

            return sb.Append(Now(result)).Append(')').Append(StrategyNote(result.Strategy)).ToString();
        }

        sb.Append("edited ").Append(Shown(result.Relative)).Append(" (").Append(Range(result)).Append(Now(result)).Append(')')
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

    /// <summary>
    /// A refusal of edit <paramref name="number"/> of a list of <paramref name="count"/> (2026-11-15): <c>Error: edit 3 of 5 (lines as edits 1–2
    /// left them): …</c>, the line numbers it names being those of the text the earlier edits left. Nothing was saved. Without
    /// <paramref name="shifted"/> (2026-11-15, the tool-arguments review's #68: a refusal found before any edit ran) the clause is left out. Pinned.
    /// </summary>
    public static string EditOfList(int number, int count, string refusal, bool shifted = true)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        string body = refusal.StartsWith("Error: ", StringComparison.Ordinal) ? refusal["Error: ".Length..] : refusal;
        string earlier = !shifted ? "" : number switch
        {
            1 => "",
            2 => " (lines as edit 1 left them)",
            _ => " (lines as edits 1–" + (number - 1).ToString(CultureInfo.InvariantCulture) + " left them)",
        };
        return "Error: edit " + number.ToString(CultureInfo.InvariantCulture) + " of " + count.ToString(CultureInfo.InvariantCulture) + earlier + ": " + body;
    }

    /// <summary>
    /// A list's success (2026-11-15, feature idea #30): <c>edited x with 3 edits (lines 4–6, 18, 40–41; now N lines, W words)</c>, a note per edit
    /// matched other than exactly, per <c>replace_all</c> (how many places, #138) and per removal ("removed at line N"), then the regions around
    /// the edits numbered, merged where they touch, up to <see cref="WorkingDirectory.MaxEditRegionLines"/> in all; the edits past that are
    /// named (#129), and only then does it say to read the lines (#105). A <c>replace_all</c> edit over several places is in the line list alone,
    /// as the single form shows one.
    /// </summary>
    public static string EditedMany(EditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var merged = WorkingDirectory.MergeRanges(result.Edits.SelectMany(p => p.Ranges));
        string listed = string.Join(", ", merged.Take(MaxListedLines).Select(r => r.From == r.To
            ? r.From.ToString(CultureInfo.InvariantCulture)
            : r.From.ToString(CultureInfo.InvariantCulture) + "–" + r.To.ToString(CultureInfo.InvariantCulture))) + (merged.Count > MaxListedLines ? ", …" : "");
        var sb = new StringBuilder("edited ").Append(Shown(result.Relative)).Append(" with ").Append(Count(result.Count, "edit", "edits"))
            .Append(" (").Append(merged.Count == 1 && merged[0].From == merged[0].To ? "line " : "lines ").Append(listed).Append(Now(result)).Append(')');
        foreach (var part in result.Edits)
        {
            string number = part.Number.ToString(CultureInfo.InvariantCulture);
            if (StrategyNote(part.Strategy) is { Length: > 0 } note)
            {
                sb.Append(" (edit ").Append(number).Append(": ").Append(note.Trim().TrimStart('('));
            }

            if (part.Places > 0)
            {
                sb.Append(" (edit ").Append(number).Append(" replaced ").Append(Count(part.Places, "place", "places")).Append(')');
            }
            else if (part.Removed && part.Ranges.Count > 0)
            {
                sb.Append(" (edit ").Append(number).Append(" removed at line ").Append(part.Ranges[0].From.ToString(CultureInfo.InvariantCulture)).Append(')');
            }
        }

        if (result.NotShown.Count > 0)
        {
            sb.Append(RegionsNotShown(result.NotShown));
        }

        if (result.Blocks.Count > 0)
        {
            sb.Append(":\n").Append(string.Join("\n…\n", result.Blocks.Select(b => Numbered(b.Lines, b.From, b.From + b.Lines.Count - 1))));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The edits whose regions didn't fit (2026-11-15, the tool-arguments review's #129): named, with where to see them. Pinned.
    /// </summary>
    public static string RegionsNotShown(IReadOnlyList<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        string names = string.Join(", ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));
        return (numbers.Count == 1 ? "; edit " : "; edits ") + names + " not shown" + RegionTooBig;
    }

    /// <summary>An <c>edits</c> value that is no list of objects (2026-11-15), echoed short. Pinned.</summary>
    public static string BadObjectList(string argument, string raw) =>
        $"Error: '{Llm.Tools.ArgumentText.Echo(raw.Trim())}' is not a list of {{old_text, new_text}} edits for '{argument}'";

    /// <summary>An <c>edits</c> list with nothing in it (2026-11-15): never a silent "edited with 0 edits". Pinned.</summary>
    public const string EditsEmpty = "Error: edits is empty; give at least one {old_text, new_text}";

    /// <summary>An <c>edits</c> list over the cap (2026-11-15), refused whole before anything is read: applying the first ones would break all-or-none. Pinned.</summary>
    public static string TooManyEdits(int count, int max) =>
        "Error: edits holds " + count.ToString(CultureInfo.InvariantCulture) + " edits, more than the " + max.ToString(CultureInfo.InvariantCulture)
        + " one call takes; split them over several calls";

    /// <summary>A <c>patch_file</c> call with neither form (2026-11-15). Pinned.</summary>
    public const string EditFormRequired = "Error: give old_text and new_text for one change, or edits (a list of {old_text, new_text, replace_all?}) for several";

    /// <summary>A <c>patch_file</c> call with both forms (2026-11-15). Pinned.</summary>
    public const string EditFormsBoth = "Error: give either old_text and new_text, or edits, not both";

    /// <summary>
    /// The prefix an <c>edits</c> item's own refusal carries (2026-11-15): <c>edit 3 of 5: </c> before the item's error, its <c>Error:</c> first;
    /// <see cref="EditOfList"/>'s, nothing having run (#38). Pinned.
    /// </summary>
    public static string EditItem(int number, int count, string refusal) => EditOfList(number, count, refusal, shifted: false);

    /// <summary>
    /// An <c>edits</c> item holding a key it doesn't take (2026-11-15), with the likely one when there is one (<paramref name="hint"/>,
    /// <see cref="Llm.ToolArgumentCheck"/>'s: the nearest name, or another agent's name for it). Pinned.
    /// </summary>
    public static string EditUnknownKey(string key, string? hint) =>
        "Error: an edit takes old_text, new_text and replace_all, not '" + key + "'" + (hint is null ? "" : "; " + hint);

    /// <summary>An <c>edits</c> item's field as a refusal names it (2026-11-15, the tool-arguments review's #13): <c>edit 3's new_text</c>.</summary>
    public static string EditField(int number, string field) => "edit " + number.ToString(CultureInfo.InvariantCulture) + "'s " + field;

    /// <summary>An <c>edits</c> item that is no object of strings (2026-11-15). Pinned.</summary>
    public const string EditNotText = "Error: old_text and new_text must be text";

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
            FileOutcome.Ok => "created " + Shown(result.Relative),
            FileOutcome.Exists => Shown(result.Relative) + " already exists",
            _ => Error(result.Outcome, Shown(result.Relative), "create", result.Detail),
        };
    }

    public static string Moved(MoveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.AtDestination || result.Outcome is FileOutcome.Exists or FileOutcome.FolderInTheWay ? Shown(result.To) : Shown(result.From), "move", result.Detail, Shown(result.To));
        }

        return (result.Renamed ? "renamed " : "moved ") + Shown(result.From) + " to " + Shown(result.To) + Replaced(result) + LeftOut(result.LeftOut);
    }

    public static string Copied(MoveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.AtDestination || result.Outcome is FileOutcome.Exists or FileOutcome.FolderInTheWay or FileOutcome.IsAFile ? Shown(result.To) : Shown(result.From), "copy", result.Detail, Shown(result.To));
        }

        return "copied " + Shown(result.From) + " to " + Shown(result.To) + Replaced(result) + LeftOut(result.LeftOut);
    }

    /// <summary>What an overwrite replaced, said (2026-10-22, review J11): a file, or a folder merged into.</summary>
    private static string Replaced(MoveResult result) =>
        !result.Replaced ? "" : result.IsDirectory ? " (merged into the folder there)" : " (replacing the file that was there)";

    /// <summary>
    /// What a copy, fetch, put or unzip left out (2026-10-22, the fix plan's D17): every <c>.git</c> (no hooks or config planted in a new copy) and
    /// the files the write guard keeps, at most five named.
    /// </summary>
    public static string LeftOut(IReadOnlyList<string> leftOut)
    {
        ArgumentNullException.ThrowIfNull(leftOut);
        if (leftOut.Count == 0)
        {
            return "";
        }

        string named = string.Join(", ", leftOut.Take(5)) + (leftOut.Count > 5 ? ", … " + (leftOut.Count - 5).ToString(CultureInfo.InvariantCulture) + " more" : "");
        return "; left out " + named + " (repository data and files the file tools never write)";
    }

    /// <summary>A local folder copy that would leave less than 1 GB free (2026-10-22, D16).</summary>
    public static string NoRoomToCopy(long bytes, long free) =>
        "the copy is " + Size(bytes) + " and the drive has " + Size(Math.Max(0, free)) + " free; a copy always leaves " + Size(WorkingDirectory.FreeSpaceReserve) + " free, so nothing was copied";

    /// <summary>
    /// A delete's sentence: what is gone, a folder with everything in it (2026-09-20). It names no setting (2026-09-21, the user's
    /// ask: the model reasoned about a switch it cannot reach); <c>Trashed</c> until 2026-10-01, when File safe edits, its
    /// <c>.trash</c> and <c>restore</c> went (the user's call) and every delete became this one. Since later on 2026-10-15 it says
    /// where it went: the Recycle bin (<c>File deletes to Recycle bin</c>), or deleted on a drive with no bin.
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
            return Error(result.Outcome, Shown(result.Relative), "delete", result.Detail);
        }

        string what = result.IsDirectory ? "the folder " + Shown(result.Relative) + " and everything in it" : Shown(result.Relative);
        return result.Way switch
        {
            DeleteWay.Recycled => "moved " + what + " to the " + RecycleBinName,
            DeleteWay.DeletedNoBin => "deleted " + what + " (no " + RecycleBinName + " on that drive)",
            _ => "deleted " + what,
        };
    }

    public static string Zipped(ZipResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == FileOutcome.IntoItself)
        {
            return string.IsNullOrEmpty(Shown(result.Relative)) ? RootItself : IntoItself(Shown(result.Relative), Shown(result.Archive));
        }

        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.AtDestination || result.Outcome is FileOutcome.Exists or FileOutcome.IsDirectory ? Shown(result.Archive) : Shown(result.Relative), "zip", result.Detail);
        }

        return "zipped " + Shown(result.Relative) + " into " + Shown(result.Archive) + " (" + Count(result.Entries, "entry", "entries") + ", " + Size(result.Bytes) + ")"
            + (result.Replaced ? " (replacing the archive that was there)" : "") + LeftOut(result.LeftOut);
    }

    /// <summary>An entry with a zip bomb's ratio (2026-10-22, review G7): the whole archive refused, nothing extracted.</summary>
    public static string ZipBomb(string entry) =>
        $"'{entry}' expands over {WorkingDirectory.MaxCompressionRatio.ToString(CultureInfo.InvariantCulture)} times its packed size, a zip bomb's shape; nothing was extracted";

    /// <summary>Two entries on one path, or a file where a folder must go (2026-10-22, review G8): nothing extracted.</summary>
    public static string ZipConflict(string entry) =>
        $"the archive holds '{entry}' twice, or as both a file and a folder; nothing was extracted";

    public static string Unzipped(ZipResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome == FileOutcome.OutsideRoot && result.Detail.Length > 0)
        {
            return ZipSlip(Shown(result.Relative), result.Detail);
        }

        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, result.AtDestination || result.Outcome is FileOutcome.Exists or FileOutcome.IsAFile ? Shown(result.Archive) : Shown(result.Relative), "unzip", result.Detail);
        }

        return "unzipped " + Shown(result.Relative) + " into " + Shown(result.Archive) + " (" + Count(result.Entries, "entry", "entries") + ")"
            + (result.Replaced ? " (replacing files that were there)" : "") + LeftOut(result.LeftOut);
    }

    /// <summary>Where <c>open</c> showed a folder: Explorer, or Finder on macOS (2026-10-06, the macOS build). Pinned.</summary>
    public static string FolderBrowser => OperatingSystem.IsMacOS() ? " in Finder" : " in Explorer";

    /// <summary>Where <c>open</c> showed a picture the built-in viewer took (2026-10-11, the <c>Image viewer</c> setting empty). Pinned.</summary>
    public const string InPictureViewer = " in the picture viewer";

    /// <summary>Where <c>open</c> showed a picture the <c>Image viewer</c> setting sent elsewhere (2026-10-11): <c>system</c>, a command, or no viewer here. Pinned.</summary>
    public const string InImageApp = " in the user's image app";

    public static string Opened(OpenResult result) => Opened(result, null);

    /// <summary><see cref="Opened(OpenResult)"/> with the place a file went (2026-10-11, a picture: <see cref="InPictureViewer"/> or <see cref="InImageApp"/>); null = the user's editor.</summary>
    public static string Opened(OpenResult result, string? where)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FileOutcome.Ok)
        {
            return Error(result.Outcome, Shown(result.Relative), "open", result.Detail);
        }

        return "opened " + Name(Shown(result.Relative)) + (result.IsDirectory ? FolderBrowser : where ?? " in the user's editor");
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

    // ---- the transcript's diff under an edit (2026-10-03) ----

    /// <summary>
    /// The 🛠️ line above an edit's diff: the result's first line, its trailing colon dropped — <c>edited x.cs (lines 12–16, now 48
    /// lines, 210 words)</c> — since the numbered region under it is the model's to read and the diff shows the change.
    /// </summary>
    public static string DiffNote(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int end = result.IndexOf('\n', StringComparison.Ordinal);
        string first = (end < 0 ? result : result[..end]).TrimEnd();
        return first.EndsWith(':') ? first[..^1] : first;
    }

    /// <summary>
    /// The diff's own header, Claude Code's words: <c>Added 3 lines, removed 1 line</c>, either half alone when the other is
    /// nothing, and <c>Wrote 12 lines</c> for a file that was not there.
    /// </summary>
    public static string DiffSummary(FileDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (diff.Created)
        {
            return "Wrote " + Count(diff.Added, "line", "lines");
        }

        if (diff.Removed == 0)
        {
            return "Added " + Count(diff.Added, "line", "lines");
        }

        return diff.Added == 0
            ? "Removed " + Count(diff.Removed, "line", "lines")
            : "Added " + Count(diff.Added, "line", "lines") + ", removed " + Count(diff.Removed, "line", "lines");
    }

    /// <summary>The last row of a diff cut at <c>Diff max lines</c>: <c>… 12 more lines</c>.</summary>
    public static string DiffMore(int n) => "… " + Count(n, "more line", "more lines");
}
