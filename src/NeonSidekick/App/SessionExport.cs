using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Pdf;
using NeonSidekick.Sessions;
using NeonSidekick.Skills;
using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using NeonSidekick.WebUi;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>What <c>/sessions export</c> writes (2026-10-29): a Markdown note with Obsidian properties, or one HTML page in the theme.</summary>
public enum SessionExportFormat
{
    Markdown,
    Html,
}

/// <summary>One picture a Markdown export writes beside it: its file name in the pictures folder, and its bytes.</summary>
public sealed record ExportPicture(string Name, byte[] Bytes);

/// <summary>An export built: the document's text, and the pictures a Markdown one links (none for HTML, which embeds them).</summary>
public sealed record SessionExportDocument(string Text, IReadOnlyList<ExportPicture> Pictures);

/// <summary>
/// A stored session written out (2026-10-29, the feature ideas' second: nothing wrote one out, and the user wanted one to share or to keep in the
/// Obsidian vault). The turn rows are the text as the transcript showed it, every turn; the pictures come from the stored history, matched to the
/// rows by ordinal (<see cref="RestoredPictures.ByRow"/>), so a turn whose pictures a compact or the picture budget took out exports without them.
/// Never exported (the user's call): thinking, and the tools' arguments and results — the tool line names the tools. The app's own labels lose
/// their local paths (<see cref="PasteBlocks.WithoutSavedPaths"/>); what the user and the model wrote is kept as it is.
/// <list type="bullet">
/// <item>Markdown: YAML front matter (Obsidian's properties), a <c>#</c> title and the facts line, then a <c>## Turn n</c> section a turn; the
/// pictures in a <c>&lt;stem&gt;-pictures</c> folder beside the note, linked in angle brackets.</item>
/// <item>HTML: one self-contained page under the app's content policy, no script, the pictures as <c>data:</c> URIs, the replies rendered as the Web
/// UI renders them (links vetted), in the theme in force — the default one when the theme's ink doesn't read on its own grounds.</item>
/// </list>
/// Into the working folder, named after the title and the session's start, never over a file (<see cref="Write"/>).
/// </summary>
public static class SessionExport
{
    /// <summary>The pictures folder's ending after the export's stem.</summary>
    public const string PicturesSuffix = "-pictures";

    /// <summary>The property tag every Markdown export carries.</summary>
    public const string Tag = "neon-session";

    /// <summary>
    /// The characters a title loses before it names the files (2026-10-29): Obsidian reads <c>#</c>, <c>^</c>, <c>[</c>, <c>]</c>, <c>|</c> and
    /// <c>%</c> in a note's name as link syntax (a Markdown link takes <c>#</c> as a fragment), and <c>&lt;</c> and <c>&gt;</c> would end the
    /// angle-bracket link (a Mac's file system allows both). Each becomes a space, then <see cref="FileNames.Safe"/>'s hyphen.
    /// </summary>
    private const string NameUnsafe = "#^[]|%<>";

    /// <summary>The file extension of <paramref name="format"/>.</summary>
    public static string Extension(SessionExportFormat format) => format == SessionExportFormat.Html ? ".html" : ".md";

    /// <summary>
    /// The export's stem: the shown title, its <see cref="NameUnsafe"/> characters turned into spaces before <see cref="OutputNames.Stamped"/> makes it
    /// file-safe (so a title of nothing else falls back to <c>session</c>), and the session's start to the minute in <paramref name="zone"/>.
    /// </summary>
    public static string Stem(SessionSummary summary, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(zone);
        var title = new StringBuilder(SessionText.DisplayTitle(summary));
        foreach (char c in NameUnsafe)
        {
            title.Replace(c, ' ');
        }

        return OutputNames.Stamped(title.ToString(), TimeZoneInfo.ConvertTime(summary.StartedAt, zone), "session");
    }

    /// <summary>
    /// <paramref name="text"/> as plain Markdown text: a backslash before each character that would make it something else (<c>\ ` * _ [ ] &lt; &gt; # |</c>),
    /// for the app's own lines — the title and the facts — so "Fix the *bug* in [x]" shows as typed. Pure.
    /// </summary>
    public static string MarkdownText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var escaped = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    /// <summary>
    /// The export of <paramref name="record"/> in <paramref name="format"/>: times in <paramref name="zone"/>, an HTML page's colors from
    /// <paramref name="palette"/>, a Markdown note's picture links into <paramref name="pictureFolder"/> (the folder's name beside the note). A history
    /// that won't parse exports without pictures (logged); the text, from the rows, is whole either way. Pure but for that log line.
    /// </summary>
    public static SessionExportDocument Build(SessionRecord record, SessionExportFormat format, TimeZoneInfo zone, ThemePalette palette, string pictureFolder)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(pictureFolder);
        var byRow = Pictures(record);
        return format == SessionExportFormat.Html ? Html(record, zone, palette, byRow) : Markdown(record, zone, byRow, pictureFolder);
    }

    /// <summary>The stored history's pictures by row; none when the history is empty or won't parse.</summary>
    private static Dictionary<int, TurnPictures> Pictures(SessionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.HistoryJson))
        {
            return [];
        }

        try
        {
            return RestoredPictures.ByRow(RestoredPictures.Turns(SessionHistory.FromJsonWhole(record.HistoryJson, out _)));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            DiagnosticLog.Warn(ChatScreen.AppCategory, "session export: " + SessionText.Id(record.Summary.Id) + "'s history wouldn't parse, so it goes without pictures: " + ex.Message);
            return [];
        }
    }

    /// <summary>The facts line as the pane's footer says it: <c>#12 · started … · last … · 14 turns · qwen3</c>, the model left out when none.</summary>
    public static string Facts(SessionSummary summary, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var facts = new List<string>
        {
            SessionText.Id(summary.Id),
            SessionText.ExportStarted + " " + SessionText.Moment(summary.StartedAt, zone),
            SessionText.ExportLast + " " + SessionText.Moment(summary.UpdatedAt, zone),
            SessionText.Turns(summary.Turns),
        };
        if (!string.IsNullOrWhiteSpace(summary.Model))
        {
            facts.Add(summary.Model);
        }

        return string.Join(" · ", facts);
    }

    /// <summary>A turn's heading: <c>Turn 3 · 2026-10-29 14:05</c>.</summary>
    private static string TurnHeading(int number, SessionTurn turn, TimeZoneInfo zone) =>
        SessionText.ExportTurnWord + " " + number.ToString(CultureInfo.InvariantCulture) + " · " + SessionText.Moment(turn.At, zone);

    /// <summary>The tool line: <c>🛠️ 3 tool calls: read_file, write_file</c>, the names as code in Markdown.</summary>
    private static string ToolLine(SessionTurn turn, Func<string, string> name) =>
        UI.TranscriptRenderer.ToolGlyph.TrimStart() + SessionText.ToolCallsNote(turn.ToolCalls) + (turn.ToolNames.Count > 0 ? ": " + string.Join(", ", turn.ToolNames.Distinct(StringComparer.Ordinal).Select(name)) : "");

    /// <summary>An ISO 8601 moment with its offset, in <paramref name="zone"/>.</summary>
    private static string Iso(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>A front-matter value: one line, quoted as YAML needs it.</summary>
    private static string Scalar(string value) => SkillFrontmatter.Scalar(value.ReplaceLineEndings(" "));

    // ── Markdown ─────────────────────────────────────────────────────────────

    private static SessionExportDocument Markdown(SessionRecord record, TimeZoneInfo zone, Dictionary<int, TurnPictures> byRow, string folder)
    {
        var summary = record.Summary;
        string title = SessionText.DisplayTitle(summary);
        var md = new StringBuilder();
        md.Append("---\n")
            .Append("title: ").Append(Scalar(title)).Append('\n')
            .Append("session: ").Append(summary.Id.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("started: ").Append(Iso(summary.StartedAt, zone)).Append('\n')
            .Append("updated: ").Append(Iso(summary.UpdatedAt, zone)).Append('\n')
            .Append("turns: ").Append(record.Turns.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (!string.IsNullOrWhiteSpace(summary.Model))
        {
            md.Append("model: ").Append(Scalar(summary.Model)).Append('\n');
        }

        md.Append("tags: [").Append(Tag).Append("]\n---\n\n")
            .Append("# ").Append(MarkdownText(title.ReplaceLineEndings(" "))).Append("\n\n")
            .Append(MarkdownText(Facts(summary, zone))).Append('\n');
        var pictures = new List<ExportPicture>();
        for (int i = 0; i < record.Turns.Count; i++)
        {
            var turn = record.Turns[i];
            int number = i + 1;
            int k = 0;
            void Links(IReadOnlyList<DataContent> shown)
            {
                foreach (var picture in shown)
                {
                    byte[] bytes = picture.Data.ToArray();
                    string name = "turn-" + number.ToString(CultureInfo.InvariantCulture) + "-" + (++k).ToString(CultureInfo.InvariantCulture)
                        + ImageFile.ExtensionFor(picture.MediaType ?? "", bytes);
                    pictures.Add(new ExportPicture(name, bytes));
                    md.Append("\n![](<").Append(folder).Append('/').Append(name).Append(">)\n");
                }
            }

            var shown = byRow.GetValueOrDefault(turn.Ordinal);
            md.Append(i == 0 ? "\n" : "\n---\n\n").Append("## ").Append(TurnHeading(number, turn, zone)).Append("\n\n")
                .Append(ChatLog.Quote(PasteBlocks.WithoutSavedPaths(turn.UserText))).Append('\n');
            if (shown is not null)
            {
                Links(shown.Imagined);
                Links(shown.Own);
            }

            if (turn.ToolCalls > 0)
            {
                md.Append('\n').Append(ToolLine(turn, n => "`" + n + "`")).Append('\n');
            }

            if (shown is not null)
            {
                Links(shown.Tools);
            }

            if (turn.ReplyText.Trim().Length > 0)
            {
                md.Append('\n').Append(turn.ReplyText.ReplaceLineEndings("\n").TrimEnd()).Append('\n');
            }

            if (turn.Cancelled)
            {
                md.Append("\n_").Append(SessionText.ExportCancelledNote).Append("_\n");
            }
        }

        return new SessionExportDocument(md.ToString(), pictures);
    }

    // ── HTML ─────────────────────────────────────────────────────────────────

    /// <summary>The page's stylesheet over the tokens <see cref="ThemeSheet"/> fills: one column, readable on a phone, pictures never wider than it.</summary>
    public const string Stylesheet = """
        * { box-sizing: border-box; }
        body { margin: 0 auto; max-width: 52rem; padding: 1.5rem 1rem 3rem; background: var(--bg); color: var(--ink); font-family: "Segoe UI", system-ui, -apple-system, sans-serif; font-size: 16px; line-height: 1.5; }
        header h1 { color: var(--accent); margin: 0 0 0.25rem; font-size: 1.6rem; line-height: 1.25; overflow-wrap: anywhere; }
        .facts, .tools, .note, .turn h2 { color: var(--dim); }
        .facts { margin: 0 0 1.5rem; font-size: 0.9rem; }
        .turn { border-top: 1px solid var(--line); padding: 1rem 0; }
        .turn h2 { font-size: 0.85rem; font-weight: 600; margin: 0 0 0.75rem; }
        .user { white-space: pre-wrap; overflow-wrap: anywhere; background: var(--user-bg, var(--panel)); color: var(--user); font-weight: var(--user-weight); font-style: var(--user-font-style); text-decoration: var(--user-decoration); border-radius: 6px; padding: 0.6rem 0.8rem; }
        .tools { font-size: 0.9rem; margin: 0.75rem 0; }
        .note { font-style: italic; }
        img { display: block; max-width: 100%; height: auto; margin: 0.75rem 0; border-radius: 4px; }
        .reply { overflow-wrap: anywhere; }
        .reply h1, .reply h2, .reply h3, .reply h4 { color: var(--accent); line-height: 1.25; }
        a { color: var(--accent); }
        code { font-family: "Cascadia Code", Consolas, Menlo, monospace; font-size: 0.9em; }
        pre { background: var(--panel); padding: 0.75rem; border-radius: 6px; overflow-x: auto; }
        :not(pre) > code { background: var(--panel); padding: 0.1em 0.3em; border-radius: 3px; }
        blockquote { margin: 0.5rem 0; padding-left: 0.8rem; border-left: 3px solid var(--line); color: var(--dim); }
        table { border-collapse: collapse; display: block; overflow-x: auto; }
        th, td { border: 1px solid var(--line); padding: 0.3rem 0.6rem; }
        """;

    /// <summary>Each code token's theme slot, for its <c>tk-*</c> class (<see cref="PdfHtml.CodeClass"/>).</summary>
    private static readonly (CodeTokenKind Kind, ThemeStyleSlot Slot)[] CodeSlots =
    [
        (CodeTokenKind.Keyword, ThemeStyleSlot.CodeKeyword),
        (CodeTokenKind.Type, ThemeStyleSlot.CodeType),
        (CodeTokenKind.String, ThemeStyleSlot.CodeString),
        (CodeTokenKind.Number, ThemeStyleSlot.CodeNumber),
        (CodeTokenKind.Comment, ThemeStyleSlot.CodeComment),
        (CodeTokenKind.Punctuation, ThemeStyleSlot.CodePunctuation),
        (CodeTokenKind.Function, ThemeStyleSlot.CodeFunction),
        (CodeTokenKind.Variable, ThemeStyleSlot.CodeVariable),
        (CodeTokenKind.Attribute, ThemeStyleSlot.CodeAttribute),
        (CodeTokenKind.Tag, ThemeStyleSlot.CodeTag),
        (CodeTokenKind.Heading, ThemeStyleSlot.CodeHeading),
        (CodeTokenKind.Inserted, ThemeStyleSlot.CodeInserted),
        (CodeTokenKind.Deleted, ThemeStyleSlot.CodeDeleted),
    ];

    /// <summary>
    /// The theme as the page's tokens: the grounds, the inks, the accent, the user's style whole, and the code colors; light or dark by the ground. A
    /// theme whose ink doesn't read on its own grounds (<see cref="WebTheme.Readable"/>) gives way to the default theme, so a page is never unreadable.
    /// </summary>
    public static string ThemeSheet(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!WebTheme.Readable(palette))
        {
            palette = ThemeLibrary.Default;
        }

        var styles = Theme.StylesBuilt(palette);
        bool light = WebTheme.Contrast(palette.Bg, Color.Black) > WebTheme.Contrast(palette.Bg, Color.White);
        var css = new StringBuilder("/* ").Append(WebTheme.CommentSafe(palette.Name)).Append(" */\n:root {\n  color-scheme: ").Append(light ? "light" : "dark").Append(";\n");
        void Token(string name, Color color) => css.Append("  ").Append(name).Append(": ").Append(WebTheme.Hex(color)).Append(";\n");
        Token("--bg", palette.Bg);
        Token("--panel", palette.PanelBg);
        Token("--ink", palette.Ink);
        Token("--dim", palette.Dim);
        Token("--accent", palette.Primary);
        Token("--line", palette.Dim);
        WebTheme.StyleTokens(css, "--user", styles(ThemeStyleSlot.User));
        css.Append("}\n");
        foreach (var (kind, slot) in CodeSlots)
        {
            var style = styles(slot);
            if (PdfHtml.CodeClass(kind) is { } name && style.Foreground != Color.Default)
            {
                css.Append('.').Append(name).Append(" { color: ").Append(WebTheme.Hex(style.Foreground)).Append(';')
                    .Append(style.Decoration.HasFlag(Decoration.Italic) ? " font-style: italic;" : "")
                    .Append(style.Decoration.HasFlag(Decoration.Bold) ? " font-weight: 700;" : "")
                    .Append(" }\n");
            }
        }

        return css.ToString();
    }

    private static SessionExportDocument Html(SessionRecord record, TimeZoneInfo zone, ThemePalette palette, Dictionary<int, TurnPictures> byRow)
    {
        var summary = record.Summary;
        string title = SessionText.DisplayTitle(summary).ReplaceLineEndings(" ");
        var html = new StringBuilder(4096);
        // Opened as PdfHtml.Document opens its page: the charset first (the file is UTF-8 with no BOM, so a page read from disk never guesses).
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append(PdfHtml.PolicyMeta).Append('\n')
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n")
            .Append("<style>\n").Append(ThemeSheet(palette)).Append(Stylesheet).Append("\n</style>\n</head>\n<body>\n")
            .Append("<header>\n<h1>").Append(WebUtility.HtmlEncode(title)).Append("</h1>\n<p class=\"facts\">").Append(WebUtility.HtmlEncode(Facts(summary, zone))).Append("</p>\n</header>\n");
        for (int i = 0; i < record.Turns.Count; i++)
        {
            var turn = record.Turns[i];
            var shown = byRow.GetValueOrDefault(turn.Ordinal);
            int number = i + 1;
            int k = 0;
            void Images(IReadOnlyList<DataContent> pictures)
            {
                foreach (var picture in pictures)
                {
                    byte[] bytes = picture.Data.ToArray();
                    string mediaType = picture.MediaType is { Length: > 0 } type ? type : "image/png";
                    k++;
                    // The alt text is a file name at most, never a folder (a dropped picture's path is full).
                    string alt = ConversationHistory.PicturePath(picture) is { } path ? Path.GetFileName(path)
                        : "turn-" + number.ToString(CultureInfo.InvariantCulture) + "-" + k.ToString(CultureInfo.InvariantCulture) + ImageFile.ExtensionFor(mediaType, bytes);
                    html.Append("<img src=\"").Append(PdfHtml.DataUri(bytes, mediaType)).Append("\" alt=\"").Append(WebUtility.HtmlEncode(alt)).Append("\">\n");
                }
            }

            html.Append("<section class=\"turn\">\n<h2>").Append(WebUtility.HtmlEncode(TurnHeading(number, turn, zone))).Append("</h2>\n")
                .Append("<div class=\"user\">").Append(WebUtility.HtmlEncode(PasteBlocks.WithoutSavedPaths(turn.UserText).ReplaceLineEndings("\n"))).Append("</div>\n");
            if (shown is not null)
            {
                Images(shown.Imagined);
                Images(shown.Own);
            }

            if (turn.ToolCalls > 0)
            {
                html.Append("<p class=\"tools\">").Append(WebUtility.HtmlEncode(ToolLine(turn, n => n))).Append("</p>\n");
            }

            if (shown is not null)
            {
                Images(shown.Tools);
            }

            if (turn.ReplyText.Trim().Length > 0)
            {
                html.Append("<div class=\"reply\">\n").Append(PdfHtml.Markdown(turn.ReplyText, _ => null, WebUiMarkdown.SafeLink)).Append("</div>\n");
            }

            if (turn.Cancelled)
            {
                html.Append("<p class=\"note\">").Append(WebUtility.HtmlEncode(SessionText.ExportCancelledNote)).Append("</p>\n");
            }

            html.Append("</section>\n");
        }

        html.Append("</body>\n</html>\n");
        return new SessionExportDocument(html.ToString(), []);
    }

    // ── The writer ───────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="record"/> exported into <paramref name="files"/>' working folder: the written file's path relative to it, else the error
    /// sentence. Named <see cref="Stem"/> + the extension, made free (<see cref="WorkingDirectory.FirstFree"/>: a re-export is <c>-2</c>, never a
    /// replace); a Markdown note's pictures folder named after the final stem, made free too. The app's own write (<see cref="WriteGuard.AsApp"/>):
    /// UTF-8 bytes with no BOM, no character cap. The pictures first, the document last; when the document fails, the pictures just written go again.
    /// </summary>
    public static (string? Path, string? Error) Write(WorkingDirectory files, SessionRecord record, SessionExportFormat format, TimeZoneInfo zone, ThemePalette palette) =>
        Write(files, record, format, zone, palette, null);

    /// <summary><see cref="Write(WorkingDirectory, SessionRecord, SessionExportFormat, TimeZoneInfo, ThemePalette)"/>, the document's write a seam for the tests.</summary>
    internal static (string? Path, string? Error) Write(WorkingDirectory files, SessionRecord record, SessionExportFormat format, TimeZoneInfo zone, ThemePalette palette, Func<string, byte[], WriteResult>? writeDocument)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(record);
        string stem = Stem(record.Summary, zone);
        var (document, refused) = Free(files, stem + Extension(format));
        if (document is null)
        {
            return (null, FileText.Error(refused, stem + Extension(format), "write", ""));
        }

        string finalStem = Path.GetFileNameWithoutExtension(document);
        string folder = "";
        if (format == SessionExportFormat.Markdown)
        {
            string parent = Path.GetDirectoryName(document)?.Replace('\\', '/') ?? "";
            string wanted = (parent.Length > 0 ? parent + "/" : "") + finalStem + PicturesSuffix;
            folder = Free(files, wanted).Path ?? wanted;
        }

        var built = Build(record, format, zone, palette, Path.GetFileName(folder));
        var written = new List<string>();
        foreach (var picture in built.Pictures)
        {
            string path = folder + "/" + picture.Name;
            var result = WriteGuard.AsApp(() => files.WriteBytes(path, picture.Bytes, overwrite: false));
            if (result.Outcome != FileOutcome.Ok)
            {
                Undo(files, written, folder);
                return (null, FileText.Error(result.Outcome, path, "write", result.Detail));
            }

            written.Add(path);
        }

        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(built.Text);
        var write = writeDocument ?? ((path, data) => files.WriteBytes(path, data, overwrite: false));
        var saved = WriteGuard.AsApp(() => write(document, bytes));
        if (saved.Outcome != FileOutcome.Ok)
        {
            Undo(files, written, folder);
            return (null, FileText.Error(saved.Outcome, document, "write", saved.Detail));
        }

        DiagnosticLog.Info(ChatScreen.AppCategory, "session export: " + SessionText.Id(record.Summary.Id) + " " + (format == SessionExportFormat.Html ? "html" : "md") + " → " + document
            + ", " + built.Pictures.Count.ToString(CultureInfo.InvariantCulture) + (built.Pictures.Count == 1 ? " picture, " : " pictures, ")
            + ((bytes.Length + built.Pictures.Sum(p => (long)p.Bytes.Length) + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KB");
        return (document, null);
    }

    /// <summary>The first free relative path for <paramref name="relative"/>, or null and the refusal when the working folder refuses it.</summary>
    private static (string? Path, FileOutcome Refused) Free(WorkingDirectory files, string relative)
    {
        string full = "";
        var outcome = WriteGuard.AsApp(() => files.Resolve(relative, forWrite: true, out full));
        return outcome != FileOutcome.Ok ? (null, outcome) : (files.Relative(WorkingDirectory.FirstFree(full)).Replace('\\', '/'), FileOutcome.Ok);
    }

    /// <summary>The pictures written so far removed again, and their folder when it is left empty.</summary>
    private static void Undo(WorkingDirectory files, List<string> written, string folder)
    {
        foreach (string path in written)
        {
            if (files.Resolve(path, forWrite: true, out string full) == FileOutcome.Ok)
            {
                try
                {
                    File.Delete(full);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left beside the working folder's other files; the error says the export failed.
                }
            }
        }

        if (folder.Length > 0 && files.Resolve(folder, forWrite: true, out string dir) == FileOutcome.Ok)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An empty folder left behind.
            }
        }
    }
}
