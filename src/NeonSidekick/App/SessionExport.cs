using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
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

        return OutputNames.Stamped(title.ToString(), TimeZoneInfo.ConvertTime(summary.StartedAt, zone), SessionText.ExportStem);
    }

    /// <summary>
    /// <paramref name="text"/> as plain Markdown text: a backslash before each character that would make it something else (<c>\ ` * _ [ ] &lt; &gt; # |</c>,
    /// and since 2026-10-30 <c>$ = ~ &amp; %</c>, review F50: Obsidian read <c>$…$</c> as math, <c>==</c> and <c>~~</c> as marks, <c>&amp;amp;</c> as an
    /// entity and <c>%%</c> as a comment), for the app's own lines — the title and the facts — so "Fix the *bug* in [x]" shows as typed. Pure.
    /// </summary>
    public static string MarkdownText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var escaped = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '#' or '|' or '$' or '=' or '~' or '&' or '%')
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
    /// that won't parse exports without pictures (logged); the text, from the rows, is whole either way. The whole document as a string, for the
    /// tests and small callers; <see cref="Write"/> streams the same text (<see cref="Emit"/>). Pure but for the log lines.
    /// </summary>
    public static SessionExportDocument Build(SessionRecord record, SessionExportFormat format, TimeZoneInfo zone, ThemePalette palette, string pictureFolder)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(pictureFolder);
        var pictures = new List<ExportPicture>();
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        Emit(record, format, zone, palette, pictureFolder, text, (name, bytes) =>
        {
            pictures.Add(new ExportPicture(name, bytes.ToArray()));
            return null;
        });
        return new SessionExportDocument(text.ToString(), pictures);
    }

    /// <summary>
    /// The export written into <paramref name="output"/> a turn at a time (2026-10-30, review F84: the whole document, its base64 copies and its bytes
    /// were built in memory, about three times a picture-heavy session's pictures on top of the loaded record): a Markdown note's pictures handed to
    /// <paramref name="picture"/> as they come, an HTML page's encoded straight into the output in slices. Null, or the error <paramref name="picture"/>
    /// answered, which stops the export there.
    /// </summary>
    private static string? Emit(SessionRecord record, SessionExportFormat format, TimeZoneInfo zone, ThemePalette palette, string pictureFolder, TextWriter output,
        Func<string, ReadOnlyMemory<byte>, string?> picture)
    {
        var byRow = Pictures(record);

        // The export counts what it writes (2026-10-30, review F9): the front matter and the facts line both say the rows exported, where the
        // stored count (the highest ordinal) could differ from them.
        var summary = record.Summary with { Turns = record.Turns.Count };
        if (summary.Turns != record.Summary.Turns)
        {
            DiagnosticLog.Info(ChatScreen.AppCategory, "session export: " + SessionText.Id(summary.Id) + " stores " + SessionText.Turns(record.Summary.Turns)
                + " but loaded " + record.Turns.Count.ToString(CultureInfo.InvariantCulture) + " rows; the export says the rows");
        }

        return format == SessionExportFormat.Html ? Html(record, summary, zone, palette, byRow, output) : Markdown(record, summary, zone, byRow, pictureFolder, output, picture);
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
        // A part the chat types refuse (a stored media type that isn't one, 2026-10-30, review F10) is an ArgumentException: no pictures either.
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or ArgumentException)
        {
            DiagnosticLog.Warn(ChatScreen.AppCategory, "session export: " + SessionText.Id(record.Summary.Id) + "'s history wouldn't parse, so it goes without pictures: " + ex.Message);
            return [];
        }
    }

    /// <summary>
    /// The facts line as the pane's footer says it: <c>#12 · started … · last … · 14 turns · qwen3</c>, the model left out when none; without the id
    /// when <paramref name="withId"/> is false, which is the footer's own line (2026-10-30, review F68: the two lists were kept in step by hand).
    /// </summary>
    public static string Facts(SessionSummary summary, TimeZoneInfo zone, bool withId = true)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var facts = new List<string>(5);
        if (withId)
        {
            facts.Add(SessionText.Id(summary.Id));
        }

        facts.Add(SessionText.ExportStarted + " " + SessionText.Moment(summary.StartedAt, zone));
        facts.Add(SessionText.ExportLast + " " + SessionText.Moment(summary.UpdatedAt, zone));
        facts.Add(SessionText.Turns(summary.Turns));
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

    /// <summary>
    /// <paramref name="text"/> as one Markdown code span (2026-10-30, review F89: a tool name with a backtick closed its span, and what followed read as
    /// Markdown): fenced by one backtick more than its longest run, padded with a space where CommonMark would otherwise eat one or join the fence,
    /// its line breaks spaces. Pure.
    /// </summary>
    public static string CodeSpan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.ReplaceLineEndings(" ");
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        string fence = new('`', longest + 1);
        bool pad = text.Length > 0 && (text[0] == '`' || text[^1] == '`' || (text[0] == ' ' && text[^1] == ' ' && text.Trim().Length > 0));
        return pad ? fence + " " + text + " " + fence : fence + text + fence;
    }

    /// <summary>An ISO 8601 moment with its offset, in <paramref name="zone"/>.</summary>
    private static string Iso(DateTimeOffset at, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>A front-matter value: one line, quoted as YAML needs it.</summary>
    private static string Scalar(string value) => SkillFrontmatter.Scalar(value.ReplaceLineEndings(" "));

    /// <summary>One picture as the export shows it: its file name (<c>turn-3-2.png</c>), the media type it is embedded as, its alt text and its bytes.</summary>
    private sealed record ShownPicture(string Name, string MediaType, string Alt, ReadOnlyMemory<byte> Bytes);

    /// <summary>
    /// One turn's pictures named as both writers name them (2026-10-30, review F69: each writer had its own copy of the rule): <c>turn-N-k</c> with
    /// <c>k</c> counting across the turn in the order shown (the imagined, the user's own, then the tools' after the tool line), the extension from a
    /// media type <see cref="ImageFile"/> knows, else from the bytes (review F48: a stored type went into the page as it was). A picture that won't
    /// read is left out, logged, and the turn says so (review F10), never the export failing whole.
    /// </summary>
    private sealed class TurnPictureNames(long session, int number)
    {
        private int _k;

        /// <summary>Whether a picture of this turn was left out.</summary>
        public bool Dropped { get; private set; }

        public ShownPicture? Next(DataContent picture)
        {
            try
            {
                var bytes = picture.Data;
                string extension = ImageFile.ExtensionFor(picture.MediaType ?? "", bytes.Span);
                string name = "turn-" + number.ToString(CultureInfo.InvariantCulture) + "-" + (_k + 1).ToString(CultureInfo.InvariantCulture) + extension;

                // The alt text is a file name at most, never a folder (a dropped picture's path is full), split on either OS's separator (review F39).
                string alt = ConversationHistory.PicturePath(picture) is { } path ? FileNames.LastPart(path) : name;
                var shown = new ShownPicture(name, PdfHtml.ImageMediaType(extension) ?? "image/png", alt, bytes);
                _k++;
                return shown;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Dropped = true;
                DiagnosticLog.Warn(ChatScreen.AppCategory, "session export: " + SessionText.Id(session) + " turn " + number.ToString(CultureInfo.InvariantCulture)
                    + " leaves a picture out that wouldn't read: " + ex.Message);
                return null;
            }
        }
    }

    // ── Markdown ─────────────────────────────────────────────────────────────

    private static string? Markdown(SessionRecord record, SessionSummary summary, TimeZoneInfo zone, Dictionary<int, TurnPictures> byRow, string folder, TextWriter md,
        Func<string, ReadOnlyMemory<byte>, string?> write)
    {
        string title = SessionText.DisplayTitle(summary);
        md.Write("---\n");
        md.Write("title: " + Scalar(title) + "\n");
        md.Write("session: " + summary.Id.ToString(CultureInfo.InvariantCulture) + "\n");
        md.Write("started: " + Iso(summary.StartedAt, zone) + "\n");
        md.Write("updated: " + Iso(summary.UpdatedAt, zone) + "\n");
        md.Write("turns: " + summary.Turns.ToString(CultureInfo.InvariantCulture) + "\n");
        if (!string.IsNullOrWhiteSpace(summary.Model))
        {
            md.Write("model: " + Scalar(summary.Model) + "\n");
        }

        md.Write("tags: [" + Tag + "]\n---\n\n");
        md.Write("# " + MarkdownText(title.ReplaceLineEndings(" ")) + "\n\n");
        md.Write(MarkdownText(Facts(summary, zone)) + "\n");
        for (int i = 0; i < record.Turns.Count; i++)
        {
            var turn = record.Turns[i];
            int number = i + 1;
            var names = new TurnPictureNames(summary.Id, number);
            string? Links(IReadOnlyList<DataContent> pictures)
            {
                foreach (var picture in pictures)
                {
                    if (names.Next(picture) is not { } shown)
                    {
                        continue;
                    }

                    if (write(shown.Name, shown.Bytes) is { } error)
                    {
                        return error;
                    }

                    md.Write("\n![](<" + folder + "/" + shown.Name + ">)\n");
                }

                return null;
            }

            var shown = byRow.GetValueOrDefault(turn.Ordinal);
            md.Write(i == 0 ? "\n" : "\n---\n\n");
            md.Write("## " + TurnHeading(number, turn, zone) + "\n\n");
            md.Write(ChatLog.Quote(PasteBlocks.WithoutSavedPaths(turn.UserText)) + "\n");
            if (shown is not null && (Links(shown.Imagined) ?? Links(shown.Own)) is { } ownError)
            {
                return ownError;
            }

            if (turn.ToolCalls > 0)
            {
                md.Write("\n" + ToolLine(turn, CodeSpan) + "\n");
            }

            if (shown is not null && Links(shown.Tools) is { } toolError)
            {
                return toolError;
            }

            if (names.Dropped)
            {
                md.Write("\n_" + SessionText.ExportPictureDroppedNote + "_\n");
            }

            if (turn.ReplyText.Trim().Length > 0)
            {
                md.Write("\n" + turn.ReplyText.ReplaceLineEndings("\n").TrimEnd() + "\n");
            }

            if (turn.Cancelled)
            {
                md.Write("\n_" + SessionText.ExportCancelledNote + "_\n");
            }
        }

        return null;
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

    /// <summary>The bytes a picture's base64 is encoded from at a time: a multiple of 3, so the slices join into one clean run (review F84).</summary>
    internal const int Base64Slice = 48 * 1024;

    /// <summary><paramref name="bytes"/> as base64 into <paramref name="output"/>, <see cref="Base64Slice"/> bytes at a time through one reused buffer.</summary>
    private static void WriteBase64(TextWriter output, ReadOnlySpan<byte> bytes)
    {
        char[] buffer = new char[Base64Slice / 3 * 4];
        for (int at = 0; at < bytes.Length; at += Base64Slice)
        {
            Convert.TryToBase64Chars(bytes.Slice(at, Math.Min(Base64Slice, bytes.Length - at)), buffer, out int written);
            output.Write(buffer, 0, written);
        }
    }

    private static string? Html(SessionRecord record, SessionSummary summary, TimeZoneInfo zone, ThemePalette palette, Dictionary<int, TurnPictures> byRow, TextWriter html)
    {
        string title = SessionText.DisplayTitle(summary).ReplaceLineEndings(" ");
        // Opened as PdfHtml.Document opens its page: the charset first (the file is UTF-8 with no BOM, so a page read from disk never guesses).
        html.Write("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Write(PdfHtml.PolicyMeta + "\n");
        html.Write("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Write("<title>" + WebUtility.HtmlEncode(title) + "</title>\n");
        html.Write("<style>\n" + ThemeSheet(palette) + Stylesheet + "\n</style>\n</head>\n<body>\n");
        html.Write("<header>\n<h1>" + WebUtility.HtmlEncode(title) + "</h1>\n<p class=\"facts\">" + WebUtility.HtmlEncode(Facts(summary, zone)) + "</p>\n</header>\n");
        for (int i = 0; i < record.Turns.Count; i++)
        {
            var turn = record.Turns[i];
            var shown = byRow.GetValueOrDefault(turn.Ordinal);
            int number = i + 1;
            var names = new TurnPictureNames(summary.Id, number);
            void Images(IReadOnlyList<DataContent> pictures)
            {
                foreach (var picture in pictures)
                {
                    if (names.Next(picture) is not { } image)
                    {
                        continue;
                    }

                    // The media type is one of the page's own (F48), and base64 needs no encoding; the type goes through the encoder all the same.
                    html.Write("<img src=\"data:" + WebUtility.HtmlEncode(image.MediaType) + ";base64,");
                    WriteBase64(html, image.Bytes.Span);
                    html.Write("\" alt=\"" + WebUtility.HtmlEncode(image.Alt) + "\">\n");
                }
            }

            html.Write("<section class=\"turn\">\n<h2>" + WebUtility.HtmlEncode(TurnHeading(number, turn, zone)) + "</h2>\n");
            html.Write("<div class=\"user\">" + WebUtility.HtmlEncode(PasteBlocks.WithoutSavedPaths(turn.UserText).ReplaceLineEndings("\n")) + "</div>\n");
            if (shown is not null)
            {
                Images(shown.Imagined);
                Images(shown.Own);
            }

            if (turn.ToolCalls > 0)
            {
                html.Write("<p class=\"tools\">" + WebUtility.HtmlEncode(ToolLine(turn, n => n)) + "</p>\n");
            }

            if (shown is not null)
            {
                Images(shown.Tools);
            }

            if (names.Dropped)
            {
                html.Write("<p class=\"note\">" + WebUtility.HtmlEncode(SessionText.ExportPictureDroppedNote) + "</p>\n");
            }

            if (turn.ReplyText.Trim().Length > 0)
            {
                html.Write("<div class=\"reply\">\n" + PdfHtml.Markdown(turn.ReplyText, _ => null, WebUiMarkdown.SafeLink) + "</div>\n");
            }

            if (turn.Cancelled)
            {
                html.Write("<p class=\"note\">" + WebUtility.HtmlEncode(SessionText.ExportCancelledNote) + "</p>\n");
            }

            html.Write("</section>\n");
        }

        html.Write("</body>\n</html>\n");
        return null;
    }

    // ── The writer ───────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="record"/> exported into <paramref name="files"/>' working folder: the written file's path relative to it, else the error
    /// sentence. Named <see cref="Stem"/> + the extension, the note and its pictures folder sharing one free stem (<see cref="FreeStem"/>: a
    /// re-export is <c>-2</c>, never a replace). The app's own write (<see cref="WriteGuard.AsApp"/>): UTF-8 with no BOM, no character cap,
    /// streamed into a temporary file a turn at a time (review F84) and put in place last; each picture written as the note reaches it. When
    /// anything fails, the pictures just written go again and the note never lands.
    /// </summary>
    public static (string? Path, string? Error) Write(WorkingDirectory files, SessionRecord record, SessionExportFormat format, TimeZoneInfo zone, ThemePalette palette) =>
        Write(files, record, format, zone, palette, null);

    /// <summary><see cref="Write(WorkingDirectory, SessionRecord, SessionExportFormat, TimeZoneInfo, ThemePalette)"/>, the document's commit a seam for the tests.</summary>
    internal static (string? Path, string? Error) Write(WorkingDirectory files, SessionRecord record, SessionExportFormat format, TimeZoneInfo zone, ThemePalette palette,
        Func<PendingWrite, WriteResult>? commit, Func<Stream, TextWriter>? writer = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(palette);
        string extension = Extension(format);
        var (stem, refused) = FreeStem(files, Stem(record.Summary, zone), extension, format == SessionExportFormat.Markdown);
        if (stem is null)
        {
            return (null, FileText.Error(refused, Stem(record.Summary, zone) + extension, "write", ""));
        }

        string document = stem + extension;
        string folder = format == SessionExportFormat.Markdown ? stem + PicturesSuffix : "";
        PendingWrite? pending = null;
        var begun = WriteGuard.AsApp(() => files.BeginWrite(document, overwrite: false, out pending));
        if (begun.Outcome != FileOutcome.Ok || pending is null)
        {
            return (null, FileText.Error(begun.Outcome, document, "write", begun.Detail));
        }

        var written = new List<string>();
        long pictureBytes = 0;
        string? Picture(string name, ReadOnlyMemory<byte> bytes)
        {
            string path = folder + "/" + name;
            var result = WriteGuard.AsApp(() => files.WriteBytes(path, ArrayOf(bytes), overwrite: false));
            if (result.Outcome != FileOutcome.Ok)
            {
                return FileText.Error(result.Outcome, path, "write", result.Detail);
            }

            written.Add(path);
            pictureBytes += bytes.Length;
            return null;
        }

        using (pending)
        {
            WriteResult saved;
            try
            {
                // Left open: Commit reads the stream's length and closes it itself.
                string? error;
                using (var text = writer?.Invoke(pending.Stream) ?? new StreamWriter(pending.Stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: true))
                {
                    error = Emit(record, format, zone, palette, folder, text, Picture);
                    text.Flush();
                }

                if (error is not null)
                {
                    Undo(files, written, folder);
                    return (null, error);
                }

                saved = commit?.Invoke(pending) ?? pending.Commit();
            }
            catch
            {
                Undo(files, written, folder);
                throw;
            }

            // A file that appeared at the name meanwhile is a failure too: the export never writes over one.
            if (saved.Outcome != FileOutcome.Ok)
            {
                Undo(files, written, folder);
                return (null, FileText.Error(saved.Outcome, document, "write", saved.Detail));
            }

            DiagnosticLog.Info(ChatScreen.AppCategory, "session export: " + SessionText.Id(record.Summary.Id) + " " + (format == SessionExportFormat.Html ? "html" : "md") + " → " + document
                + ", " + written.Count.ToString(CultureInfo.InvariantCulture) + (written.Count == 1 ? " picture, " : " pictures, ")
                + ((saved.Bytes + pictureBytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KB");
            return (document, null);
        }
    }

    /// <summary><paramref name="bytes"/>' own array when it is one whole, else a copy (the stored pictures are whole arrays, so no copy as a rule).</summary>
    private static byte[] ArrayOf(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out var segment) && segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length ? array : bytes.ToArray();

    /// <summary>
    /// The first stem free for both the note and, with <paramref name="withPictures"/>, its <c>&lt;stem&gt;-pictures</c> folder (2026-10-30, review F7:
    /// each was made free on its own, through a numbering that split a dotted title at its last dot): <paramref name="stem"/>, then <c>-2</c>, <c>-3</c>…;
    /// null and the refusal when the working folder refuses the name.
    /// </summary>
    private static (string? Stem, FileOutcome Refused) FreeStem(WorkingDirectory files, string stem, string extension, bool withPictures)
    {
        for (int n = 1; ; n++)
        {
            string candidate = n == 1 ? stem : stem + "-" + n.ToString(CultureInfo.InvariantCulture);
            string note = "";
            var outcome = WriteGuard.AsApp(() => files.Resolve(candidate + extension, forWrite: true, out note));
            if (outcome != FileOutcome.Ok)
            {
                return (null, outcome);
            }

            if (File.Exists(note) || Directory.Exists(note))
            {
                continue;
            }

            if (withPictures)
            {
                string folder = "";
                outcome = WriteGuard.AsApp(() => files.Resolve(candidate + PicturesSuffix, forWrite: true, out folder));
                if (outcome != FileOutcome.Ok)
                {
                    return (null, outcome);
                }

                if (File.Exists(folder) || Directory.Exists(folder))
                {
                    continue;
                }
            }

            return (candidate, FileOutcome.Ok);
        }
    }

    /// <summary>
    /// The pictures written so far removed again, and their folder when it is left empty; resolved as the app's own, as they were written (2026-10-30,
    /// review F6: a place only the app may write refused the undo, and the pictures stayed).
    /// </summary>
    private static void Undo(WorkingDirectory files, List<string> written, string folder)
    {
        foreach (string path in written)
        {
            string full = "";
            if (WriteGuard.AsApp(() => files.Resolve(path, forWrite: true, out full)) == FileOutcome.Ok)
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

        string dir = "";
        if (folder.Length > 0 && WriteGuard.AsApp(() => files.Resolve(folder, forWrite: true, out dir)) == FileOutcome.Ok)
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
