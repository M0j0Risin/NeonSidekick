using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The append-only transcript: what the user said, the assistant's streamed reply, and the
/// notices, tool activity and diagnostics that land between them. Everything renders to the
/// injected <see cref="IAnsiConsole"/>; nothing is ever repainted — except the reply in progress
/// when it is <em>styled</em>: with the pane on the screen and <c>Transcript markdown</c> on, the
/// reply accumulates in the pane's live slot as a <see cref="ReplyBlock"/> (the whole text every
/// time, the pane lays it out on its tick) and becomes transcript when it ends or a line-shaped
/// write needs the flow (<see cref="ScreenPane.CommitLive"/>). The plain path streams tokens as
/// they come and is the only path without the pane (headless, a redirected console).
///
/// <para>Streamed text goes through <see cref="RawText"/>, never <c>Markup</c>: model output is
/// data, so there is nothing to escape and nothing to fold. Every line-shaped write escapes its
/// text and is built by a public static so tests pin the wording. Tool text is truncated
/// <em>before</em> escaping: escaping expands <c>[</c> to <c>[[</c>, and truncating afterwards can
/// cut that pair in half and emit exactly the malformed markup the escape exists to prevent.</para>
///
/// <para>Three cursor states drive line discipline: at the start of a line, after the assistant
/// glyph with nothing said yet, and in the middle of streamed text. A notice starts a new line only
/// in the last case; after a bare glyph it continues the glyph's line (<c>● (cancelled)</c>).</para>
///
/// <para>Whitespace at the end of a streamed stretch is <em>held</em>, not written: it goes out
/// ahead of the next token when more text follows, and is dropped when a line-shaped write or the
/// end of the reply comes first. LM Studio + Gemma 4 streams a newline content delta beside each
/// tool call it generates, and seventeen quiet <c>move</c> calls after a sentence were seventeen
/// blank rows before the first <c>🛠️</c> line (2026-09-14). A paragraph break inside a reply is
/// untouched: its newlines are written before the word that follows them.</para>
///
/// <para>Tool runs (2026-09-22, the user's ask): with the pane on the screen, consecutive tool lines
/// (<see cref="Tool"/>, <see cref="ToolResult"/> and the notes) are one run in the pane's store,
/// which folds it under a summary line past <see cref="ToolCollapseCount"/> — the last lines kept
/// while it runs, the summary alone once anything else is said (<see cref="ScreenPane.WriteToolLine"/>).
/// Every other write ends the run: a notice, the user's line, pictures, the reply speaking again,
/// the reply's end.</para>
///
/// <para>Thinking (2026-09-26, the user's ask): on the styled path the model's thinking takes the
/// slot as a <see cref="ThinkingBlock"/> while it streams (<see cref="AppendThinking"/>), and the next
/// write of any kind — the answer, a tool line, a notice, the reply's end — commits it, which folds it
/// to one <c>▸ 💭 thought for 4.2s</c> line (<see cref="ScreenPane.CommitLive"/>). A reply that had said
/// nothing yet keeps its glyph for the answer after it. The plain path shows no thinking: its glyph
/// is already in the flow, and printed text cannot fold.</para>
/// </summary>
public sealed class TranscriptRenderer : INoticeSink
{
    public const string AssistantGlyph = "● ";
    public const string NoticeGlyph = "  · ";
    public const string WarningGlyph = "  ! ";
    public const string ErrorGlyph = "  ✗ ";
    // The hammer and wrench (2026-09-21, the user's call; the gear ⚙ before it): U+1F6E0 with the
    // U+FE0F selector is a true two-cell emoji, so one space after it keeps the five-cell indent the
    // gear had with two — the gear was Neutral, one cell to the terminal, and the font overdrew the next.
    public const string ToolGlyph = "  🛠️ ";

    /// <summary>
    /// A skill line's glyph (later on 2026-09-21, the user's call): the mortarboard the toolbar and the
    /// Skills pane wear, so <c>loaded skill 'x'</c> and the skill editor's <c>created skill 'x'</c> read as
    /// the skills' and not as any other tool's. A surrogate pair, two cells, the indent <see cref="ToolGlyph"/>'s.
    /// </summary>
    public const string SkillGlyph = "  🎓 ";

    /// <summary>
    /// The police's line (2026-09-22, the user's call): a shell tool's result the outside-paths police refused
    /// (<see cref="Shell.ShellText.IsOutside"/>) wears the officer in place of the tools' glyph, so a refusal
    /// reads as the police's and not as the command's own error. A surrogate pair, two cells, the indent <see cref="ToolGlyph"/>'s.
    /// </summary>
    public const string PoliceGlyph = "  👮 ";
    public const string AlertGlyph = "  ⏰ ";

    /// <summary>A background process's exit (2026-09-21): its own glyph, so it never reads as a timer.</summary>
    public const string ProcessGlyph = "  ⚡ ";
    public const string NoReplyText = "(no reply)";

    /// <summary>Tool arguments and results are cut to this many characters on the transcript.</summary>
    public const int ToolTextLimit = 200;

    /// <summary>The rule's width on a console that reports none (a redirected stdout).</summary>
    public const int DefaultRuleWidth = 80;

    /// <summary>
    /// The width of a sunset rule on a console <paramref name="consoleWidth"/> cells wide: the
    /// window's whole width (no cap since 2026-09-16: the pane's rules span it, and a shorter
    /// banner rule over them looked cut), <see cref="DefaultRuleWidth"/> when the console reports
    /// none. The banner's rule and <c>/new</c>'s (<see cref="Rule"/>) share it, so the two never
    /// differ. A rule already on the transcript keeps its printed width after a resize: the
    /// transcript is append-only and the terminal owns its scrollback. Pure; pinned by tests.
    /// </summary>
    public static int RuleWidth(int consoleWidth) => consoleWidth > 0 ? consoleWidth : DefaultRuleWidth;

    private enum LineState
    {
        AtLineStart,
        GlyphOnly,
        MidText,
    }

    private readonly IAnsiConsole _console;
    private readonly ScreenPane? _pane;
    private LineState _state = LineState.AtLineStart;
    private bool _assistantOpen;
    private bool _skipLeadingWhitespace;

    // Trailing whitespace of the streamed text so far, written only ahead of more text.
    private readonly System.Text.StringBuilder _heldWhitespace = new();

    // The styled reply: it lives in the pane's slot until it is committed. _glyph says the open
    // block carries the assistant glyph (the reply's first stretch; a stretch after a tool line is flush-left).
    private bool _slot;
    private bool _glyph;
    private readonly System.Text.StringBuilder _reply = new();

    // The thinking block in the slot (2026-09-26): its text so far, how long it has streamed, and
    // whether the reply had said nothing before it (the answer after it then opens with the glyph).
    private bool _thinkingOpen;
    private readonly System.Text.StringBuilder _thinking = new();
    private TimeSpan _thinkingElapsed;
    private bool _glyphAfterThinking;

    /// <param name="console">Where the lines go; a <see cref="ScreenPane"/> on the screen also takes the spinner into its hint row.</param>
    public TranscriptRenderer(IAnsiConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _pane = console as ScreenPane;
    }

    /// <summary>True when the next write starts a fresh line.</summary>
    public bool AtLineStart => _state == LineState.AtLineStart;

    // ── Markup builders (pinned) ────────────────────────────────────────────

    public static string UserMarkup(string text) => InputLine.SubmittedMarkup(text);

    /// <summary>The glyph ahead of a <c>/botchat</c> speaker's name (2026-09-24). Pinned.</summary>
    public const string SpeakerGlyph = "◆ ";

    /// <summary>A <c>/botchat</c> speaker's name line: the glyph and the name in bold, in its colour. The name is escaped.</summary>
    public static string SpeakerMarkup(string name, Color color) =>
        string.Concat("[", Theme.ToHex(color), " bold]", Markup.Escape(SpeakerGlyph + name), "[/]");

    public static string NoticeMarkup(string text) => Theme.ColorMarkup(Theme.Dim, NoticeGlyph + text);

    public static string WarningMarkup(string text) => Theme.ColorMarkup(Theme.Warn, WarningGlyph + text);

    public static string ErrorMarkup(string text) => Theme.ColorMarkup(Theme.Bad, ErrorGlyph + text);

    /// <summary>A timer alert: the warning colour behind its own glyph, so it stands out from a diagnostic.</summary>
    public static string AlertMarkup(string text) => Theme.ColorMarkup(Theme.Warn, AlertGlyph + text);

    /// <summary>A process's exit: the warning colour behind <see cref="ProcessGlyph"/>.</summary>
    public static string ProcessAlertMarkup(string text) => Theme.ColorMarkup(Theme.Warn, ProcessGlyph + text);

    public static string ToolMarkup(string name, string argumentsJson) =>
        Theme.ColorMarkup(Theme.Dim, $"{ToolGlyph}{name} {Truncate(argumentsJson, ToolTextLimit)}");

    public static string ToolResultMarkup(string name, string text) =>
        Theme.ColorMarkup(Theme.Dim, $"{ToolGlyph}{name} → {Truncate(text, ToolTextLimit)}");

    /// <summary>A tool's outcome on one line without its name or arguments (<c>🛠️ remembered: …</c>), for a tool whose result says it all.</summary>
    public static string ToolNoteMarkup(string text) =>
        Theme.ColorMarkup(Theme.Dim, ToolGlyph + Truncate(text, ToolTextLimit));

    /// <summary>A skill tool's outcome as <see cref="ToolNoteMarkup"/> is a tool's, behind <see cref="SkillGlyph"/>.</summary>
    public static string SkillNoteMarkup(string text) =>
        Theme.ColorMarkup(Theme.Dim, SkillGlyph + Truncate(text, ToolTextLimit));

    /// <summary>A shell tool's refusal by the police as <see cref="ToolNoteMarkup"/> is a tool's, behind <see cref="PoliceGlyph"/>.</summary>
    public static string PoliceNoteMarkup(string text) =>
        Theme.ColorMarkup(Theme.Dim, PoliceGlyph + Truncate(text, ToolTextLimit));

    public static string DiagnosticMarkup(DiagnosticEvent evt) =>
        Theme.ColorMarkup(DiagnosticColor(evt.Level), $"  [{evt.Category}] {evt.Message}");

    /// <summary>One line of at most <paramref name="max"/> characters; newlines become spaces, an overflow ends in an ellipsis.</summary>
    public static string Truncate(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        string flat = text.ReplaceLineEndings(" ");
        if (max <= 0)
        {
            return "";
        }

        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    // ── Lines ───────────────────────────────────────────────────────────────

    /// <summary>What the user said, in the same shape the input line leaves behind. Voice input uses this.</summary>
    public void User(string text)
    {
        EndRun();
        BreakIfMidText();
        _console.MarkupLine(UserMarkup(text));
        _state = LineState.AtLineStart;
    }

    /// <summary>
    /// Who speaks next in a <c>/botchat</c> (2026-09-24): the name on a line of its own above the reply, which
    /// opens with its glyph as any reply does — the reply's own rendering is untouched.
    /// </summary>
    public void Speaker(string name, Color color)
    {
        ArgumentNullException.ThrowIfNull(name);
        EndRun();
        BreakIfMidText();
        _console.MarkupLine(SpeakerMarkup(name, color));
        _state = LineState.AtLineStart;
    }

    /// <summary>
    /// The sent pictures under the user's line, tiled left to right (<see cref="ImageStrip"/>); nothing for none. With
    /// <paramref name="ids"/> (later on 2026-09-24) the pane keeps where each tile landed, so a double-click opens it.
    /// </summary>
    public void Images(IReadOnlyList<ImageThumbnail> thumbnails, IReadOnlyList<int>? ids = null)
    {
        ArgumentNullException.ThrowIfNull(thumbnails);
        if (thumbnails.Count == 0)
        {
            return;
        }

        EndRun();
        BreakIfMidText();
        var strip = new ImageStrip(thumbnails, ids);
        if (ids is not null && _pane is { Enabled: true } pane)
        {
            pane.WritePictures(strip);
        }
        else
        {
            _console.Write(strip);
        }

        _state = LineState.AtLineStart;
    }

    /// <summary>
    /// One picture on its own, centred in the transcript's width (<c>/view</c>, 2026-09-17): the
    /// canvas under Spectre's <see cref="Align"/>, which pads its rows to the render width — the
    /// console's, so the picture sits in the middle of the window as it is now. The strip under a
    /// sent line (<see cref="Images"/>) stays at the left, under the line it belongs to.
    /// </summary>
    public void Picture(ImageThumbnail thumbnail, int? id = null)
    {
        ArgumentNullException.ThrowIfNull(thumbnail);
        EndRun();
        BreakIfMidText();
        if (id is { } key && _pane is { Enabled: true } pane)
        {
            // Where it landed kept (later on 2026-09-24): a double-click opens it.
            pane.WritePictures(new CenteredPicture(thumbnail, key));
        }
        else
        {
            _console.Write(Align.Center(thumbnail.ToCanvas()));
        }

        _state = LineState.AtLineStart;
    }

    public void Notice(string text) => Line(NoticeMarkup(text), Theme.ColorMarkup(Theme.Dim, text));

    /// <summary>
    /// The banner's sunset rule across the transcript (<c>/new</c>, 2026-09-16): a divider between
    /// the conversation that ended and the one that starts under it. Drawn at <see cref="RuleWidth"/>
    /// of the console's width, on a line of its own — streamed text ahead of it is broken first.
    /// </summary>
    public void Rule()
    {
        EndRun();
        BreakIfMidText();
        _console.MarkupLine(Theme.Rule(RuleWidth(_console.Profile.Width)));
        _state = LineState.AtLineStart;
    }

    public void Warning(string text) => Line(WarningMarkup(text), Theme.ColorMarkup(Theme.Warn, text));

    public void Error(string text) => Line(ErrorMarkup(text), Theme.ColorMarkup(Theme.Bad, text));

    public void Alert(string text) => Line(AlertMarkup(text), Theme.ColorMarkup(Theme.Warn, text));

    public void ProcessAlert(string text) => Line(ProcessAlertMarkup(text), Theme.ColorMarkup(Theme.Warn, text));

    public void Tool(string name, string argumentsJson) => ToolLine(ToolMarkup(name, argumentsJson), ToolMarkup(name, argumentsJson).TrimStart());

    public void ToolResult(string name, string text) => ToolLine(ToolResultMarkup(name, text), ToolResultMarkup(name, text).TrimStart());

    public void ToolNote(string text) => ToolLine(ToolNoteMarkup(text), Theme.ColorMarkup(Theme.Dim, ToolGlyph.TrimStart() + Truncate(text, ToolTextLimit)));

    /// <summary>
    /// A tool note's detail line (later still on 2026-09-24, <c>ComfyUI show prompts</c>): the same dim line behind the tools'
    /// glyph, but whole — wrapped by the window, never cut at <see cref="ToolTextLimit"/> — since a prompt is read in full.
    /// </summary>
    public void ToolDetail(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string flat = text.ReplaceLineEndings(" ");
        ToolLine(Theme.ColorMarkup(Theme.Dim, ToolGlyph + flat), Theme.ColorMarkup(Theme.Dim, ToolGlyph.TrimStart() + flat));
    }

    /// <summary>
    /// An advisor's answer under its tool line (<c>claude_advisor</c>, 2026-09-27): each line whole — wrapped by the window,
    /// never cut — in the speaker's <paramref name="color"/>, indented under the tools' glyph; blank lines skipped. Part of
    /// the tool run, so a run folded by <c>Tool collapse count</c> folds it too.
    /// </summary>
    public void ToolAnswer(string text, Color color)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                ToolLine(Theme.ColorMarkup(color, ToolAnswerIndent + line.TrimEnd()), Theme.ColorMarkup(color, line.Trim()));
            }
        }
    }

    /// <summary>
    /// A file edit's note and its diff under it (2026-10-03, the user's ask: Claude Code's look): <paramref name="note"/> as
    /// <see cref="ToolNote"/> draws it, then <see cref="DiffView"/>'s rows, cut at <paramref name="maxLines"/>. One write, so a
    /// tool run counts it once and folds it whole past <c>Tool collapse count</c>, Ctrl+O and <c>/expand</c> opening it again.
    /// </summary>
    public void ToolDiff(string note, FileDiff diff, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(diff);
        var full = new DiffView(new Markup(ToolNoteMarkup(note)), diff, maxLines);
        var inline = new DiffView(new Markup(Theme.ColorMarkup(Theme.Dim, ToolGlyph.TrimStart() + Truncate(note, ToolTextLimit))), diff, maxLines);
        ToolWrite(full, () => WriteBlock(full, inline));
    }

    /// <summary>What stands before each line of a <see cref="ToolAnswer"/>: the width of <see cref="ToolGlyph"/>, blank.</summary>
    public const string ToolAnswerIndent = "     ";

    /// <summary><see cref="ToolNote"/> for a skill tool's result: the same dim line behind <see cref="SkillGlyph"/>.</summary>
    public void SkillNote(string text) => ToolLine(SkillNoteMarkup(text), Theme.ColorMarkup(Theme.Dim, SkillGlyph.TrimStart() + Truncate(text, ToolTextLimit)));

    /// <summary><see cref="ToolNote"/> for a result the police refused: the same dim line behind <see cref="PoliceGlyph"/>.</summary>
    public void PoliceNote(string text) => ToolLine(PoliceNoteMarkup(text), Theme.ColorMarkup(Theme.Dim, PoliceGlyph.TrimStart() + Truncate(text, ToolTextLimit)));

    /// <summary>
    /// How many lines of a tool run stay while it runs (<c>Tool collapse count</c>, 2026-09-22): read
    /// when a run opens; 0 (the default here, and every console without the pane) folds nothing.
    /// </summary>
    public Func<int> ToolCollapseCount { get; set; } = static () => 0;

    /// <summary>
    /// How many lines a top-level code block of a styled reply may have before the transcript folds
    /// it to its label line (<c>Code collapse count</c>, 2026-09-22): read when a reply opens and
    /// carried by its <see cref="ReplyBlock"/>; 0 (the default here) folds nothing.
    /// </summary>
    public Func<int> CodeCollapseCount { get; set; } = static () => 0;

    // The Code collapse count the open styled reply was begun with.
    private int _codeKeep;

    /// <summary>
    /// A tool call the turn made (<c>ChatScreen.Render</c>, every call, the quiet ones too): counted
    /// by name for the run's summary line (<see cref="ToolGroupText.Summary"/>). The count is the
    /// run's — the calls since anything else was said — whether or not its line is written yet.
    /// </summary>
    public void CountToolCall(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int at = _tally.FindIndex(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (at < 0)
        {
            _tally.Add((name, 1));
        }
        else
        {
            _tally[at] = (name, _tally[at].Count + 1);
        }

        if (_run)
        {
            PushSummary();
        }
    }

    /// <summary>A quiet tool's result as one <see cref="ToolNote"/> per line of <paramref name="text"/> (the question tool's answers), blank lines skipped; nothing for a blank text.</summary>
    public void ToolNotes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var line in text.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                ToolNote(line);
            }
        }
    }

    public void Diagnostic(DiagnosticEvent evt) => Line(DiagnosticMarkup(evt), DiagnosticMarkup(evt).TrimStart());

    // ── The assistant's reply ───────────────────────────────────────────────

    /// <summary>
    /// Opens a reply: the glyph, then nothing until the first token. <paramref name="markdown"/>
    /// styles the reply through the pane's live slot (see the class summary); without the pane it
    /// is the plain path whatever the flag says.
    /// </summary>
    public void BeginAssistant(bool markdown = false)
    {
        if (_assistantOpen)
        {
            return;
        }

        // The opening calls' lines come before the glyph: a run of their own, the reply's calls another.
        EndRun();
        BreakIfMidText();
        _slot = markdown && _pane is { Enabled: true };
        if (_slot)
        {
            _reply.Clear();
            _glyph = true;
            _codeKeep = CodeCollapseCount();
            _pane!.SetLive(new ReplyBlock("", glyph: true, codeKeep: _codeKeep));
        }
        else
        {
            _console.Write(new RawText(AssistantGlyph, Theme.Accent));
        }

        _state = LineState.GlyphOnly;
        _assistantOpen = true;
        _skipLeadingWhitespace = true;
    }

    /// <summary>The open reply is styled through the pane's live slot.</summary>
    public bool StyledReply => _assistantOpen && _slot;

    /// <summary>
    /// One streamed token, written as-is up to its last non-whitespace character; what trails it
    /// is held (see the class summary). Leading whitespace and blank lines of a reply are dropped
    /// (a thinking-style model streams two empty lines before it says anything).
    /// </summary>
    public void AppendDelta(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_thinkingOpen)
        {
            // The blank lines between the thinking and the answer are nobody's: the block stays until words come.
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            EndThinking();
        }

        if (_skipLeadingWhitespace)
        {
            text = text.TrimStart();
            if (text.Length == 0)
            {
                return;
            }

            _skipLeadingWhitespace = false;
        }

        if (_run && !string.IsNullOrWhiteSpace(text))
        {
            // The reply speaks again: the tool run above it is over.
            EndRun();
        }

        if (_slot)
        {
            // The whole reply again; the pane lays it out on its tick. A slot that was committed
            // behind this class's back (a flow write straight to the pane) starts a fresh, glyph-less block.
            if (_state == LineState.MidText && !_pane!.LiveOpen)
            {
                _reply.Clear();
                _glyph = false;
            }

            _reply.Append(text);
            _pane!.SetLive(new ReplyBlock(_reply.ToString(), _glyph, codeKeep: _codeKeep));
            _state = LineState.MidText;
            return;
        }

        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end > 0)
        {
            string written = _heldWhitespace.Length == 0 ? text[..end] : _heldWhitespace.Append(text, 0, end).ToString();
            _heldWhitespace.Clear();
            _console.Write(new RawText(written, Theme.Assistant));
            _state = LineState.MidText;
        }

        _heldWhitespace.Append(text, end, text.Length - end);
    }

    /// <summary>
    /// Closes the reply: a dim <c>(no reply)</c> if nothing was said, then the line is ended and a
    /// blank line separates it from what follows. Safe to call when no reply is open, and the
    /// caller's <c>finally</c> should always call it: a reply left open swallows every later line.
    /// </summary>
    public void EndAssistant()
    {
        EndThinking();
        EndRun();
        if (!_assistantOpen)
        {
            return;
        }

        _heldWhitespace.Clear();
        if (_slot)
        {
            // One lift and one draw for the block, its blank line, or the glyph line nothing followed.
            using (_pane!.Batch())
            {
                if (_state == LineState.GlyphOnly)
                {
                    DropSlot();
                    _console.Write(new RawText(AssistantGlyph, Theme.Accent));
                    _console.Markup(Theme.DimMarkup(NoReplyText));
                    _console.WriteLine();
                }
                else if (_state == LineState.MidText)
                {
                    CommitSlot();
                }

                _console.WriteLine();
            }

            _slot = false;
        }
        else
        {
            if (_state == LineState.GlyphOnly)
            {
                _console.Markup(Theme.DimMarkup(NoReplyText));
                _state = LineState.MidText;
            }

            if (_state != LineState.AtLineStart)
            {
                _console.WriteLine();
            }

            _console.WriteLine();
        }

        _state = LineState.AtLineStart;
        _assistantOpen = false;
    }

    /// <summary>
    /// A piece of the model's thinking, <paramref name="elapsed"/> since its first piece (2026-09-26):
    /// on the styled path it streams in the slot as a <see cref="ThinkingBlock"/> — the bare glyph or the
    /// reply said so far out of it first, a tool run ended — and folds when anything else is written
    /// (<see cref="EndThinking"/>). Leading whitespace is dropped; nothing on the plain path or outside
    /// a reply.
    /// </summary>
    public void AppendThinking(string text, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!_assistantOpen || !_slot)
        {
            return;
        }

        if (!_thinkingOpen)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            EndRun();
            using (_pane!.Batch())
            {
                _glyphAfterThinking = _state == LineState.GlyphOnly;
                if (_glyphAfterThinking)
                {
                    DropSlot();
                }
                else if (_state == LineState.MidText)
                {
                    CommitSlot();
                }
            }

            _heldWhitespace.Clear();
            _thinking.Clear();
            _thinkingOpen = true;
            _state = LineState.AtLineStart;
        }

        _thinking.Append(text);
        _thinkingElapsed = elapsed;
        _pane!.SetLive(new ThinkingBlock(_thinking.ToString()));
    }

    /// <summary>The thinking is streaming in the slot (<see cref="AppendThinking"/>).</summary>
    public bool ThinkingOpen => _thinkingOpen;

    /// <summary>
    /// The thinking block into the transcript, folded to its summary with the last elapsed time; the
    /// slot then holds the bare glyph again when the reply had said nothing before it. Nothing
    /// without one. Every other write calls it first.
    /// </summary>
    public void EndThinking()
    {
        if (!_thinkingOpen)
        {
            return;
        }

        _thinkingOpen = false;
        using (_pane!.Batch())
        {
            _pane.SetLive(new ThinkingBlock(_thinking.ToString(), _thinkingElapsed));
            _pane.CommitLive();
            if (_glyphAfterThinking)
            {
                _reply.Clear();
                _glyph = true;
                _pane.SetLive(new ReplyBlock("", glyph: true, codeKeep: _codeKeep));
                _state = LineState.GlyphOnly;
            }
            else
            {
                _state = LineState.AtLineStart;
            }
        }

        _thinking.Clear();
        _skipLeadingWhitespace = true;
    }

    // ── Spinner ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="work"/> behind a themed spinner. With the pane on the screen the
    /// spinner is the pane's hint row and the transcript is untouched. Otherwise it is Spectre's
    /// <c>Status</c>, which needs the start of a line and erases its own region on exit, so nothing
    /// may be written while it runs: callers open the assistant glyph <em>after</em> this returns,
    /// never before. On a non-interactive console the work simply runs.
    /// </summary>
    public Task<T> WithSpinnerAsync<T>(string label, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return WithSpinnerAsync(label, _ => work());
    }

    /// <summary>
    /// <see cref="WithSpinnerAsync{T}(string, Func{Task{T}})"/> whose work can change the label
    /// (a download's progress, "transcribing…") through the spinner's own API, which is the one
    /// write allowed while it runs. The setter may be called from any thread; on a non-interactive
    /// console it does nothing.
    /// </summary>
    public async Task<T> WithSpinnerAsync<T>(string label, Func<Action<string>, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_pane is { Enabled: true })
        {
            using (var busy = _pane.BeginBusy(label))
            {
                return await work(busy.SetLabel).ConfigureAwait(false);
            }
        }

        if (!_console.Profile.Capabilities.Interactive)
        {
            return await work(_ => { }).ConfigureAwait(false);
        }

        if (_state != LineState.AtLineStart)
        {
            _console.WriteLine();
            _state = LineState.AtLineStart;
        }

        return await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Theme.SpinnerStyle)
            .StartAsync(Theme.DimMarkup(label), ctx => work(text =>
            {
                // Refresh as well as set: the spinner repaints on its own tick, and a label that
                // changes and finishes between two ticks would never be seen.
                ctx.Status(Theme.DimMarkup(text));
                ctx.Refresh();
            }))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The pane's spinner (<see cref="ScreenPane.BeginBusy"/>: the label with its elapsed count in
    /// the hint row) held by the caller until the scope is disposed, with the transcript written
    /// under it meanwhile — the pane draws its own hint row under every write, so a reply can
    /// stream and its 🛠️ lines print while the spinner runs; the scope's <see cref="ScreenPane.BusyScope.SetLabel"/>
    /// renames it as the turn's stage changes. Null without the pane: Spectre's <c>Status</c>
    /// cannot span a write, and there <see cref="WithSpinnerAsync{T}(string, Func{Task{T}})"/>
    /// over the first wait is all a turn gets.
    /// </summary>
    public ScreenPane.BusyScope? BeginBusy(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return _pane is { Enabled: true } ? _pane.BeginBusy(label) : null;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// A tool run's line (2026-09-22): with the pane on the screen and a <see cref="ToolCollapseCount"/>
    /// above 0 it joins the open run — one is opened first, the reply's bare glyph becoming its lead
    /// (dropped from the slot, or taken back out of the flow on the plain path) — and past the count
    /// the pane folds the run under its summary. Otherwise the plain line it always was.
    /// </summary>
    private void ToolLine(string fullMarkup, string inlineMarkup) =>
        ToolWrite(new Markup(fullMarkup + "\n"), () => WriteLine(fullMarkup, inlineMarkup));

    /// <summary>
    /// <see cref="ToolLine"/> for any write (2026-10-03, an edit's diff of many rows): <paramref name="member"/> joins the run —
    /// it must end with a line break — or, with no run to join, <paramref name="alone"/> writes it the way it always was.
    /// </summary>
    private void ToolWrite(IRenderable member, Action alone)
    {
        EndThinking();
        bool open = _run && _pane!.ToolGroupOpen;
        int keep = open ? 0 : ToolCollapseCount();
        if (!open && (_pane is not { Enabled: true } || keep <= 0))
        {
            _run = false;
            alone();
            return;
        }

        _heldWhitespace.Clear();
        using (_pane!.Batch())
        {
            if (!open)
            {
                if (_state == LineState.GlyphOnly)
                {
                    bool plain = !_slot;
                    if (_slot)
                    {
                        DropSlot();
                    }

                    _pane.BeginToolGroup(keep, new RawText(AssistantGlyph, Theme.Accent), absorbOpenLine: plain);
                }
                else
                {
                    BreakIfMidText();
                    _pane.BeginToolGroup(keep);
                }

                _run = true;
                PushSummary();
            }
            else
            {
                BreakIfMidText();
            }

            _pane.WriteToolLine(member);
        }

        _state = LineState.AtLineStart;
    }

    /// <summary><see cref="WriteLine"/> for a block that renders its own rows and line breaks (<see cref="ToolDiff"/>).</summary>
    private void WriteBlock(IRenderable full, IRenderable inline)
    {
        EndThinking();
        _heldWhitespace.Clear();
        if (_state == LineState.GlyphOnly)
        {
            if (_slot)
            {
                using (_pane!.Batch())
                {
                    DropSlot();
                    _console.Write(new RawText(AssistantGlyph, Theme.Accent));
                    _console.Write(inline);
                }
            }
            else
            {
                _console.Write(inline);
            }
        }
        else
        {
            BreakIfMidText();
            _console.Write(full);
        }

        _state = LineState.AtLineStart;
    }

    /// <summary>The open run's summary from the tally, folded and unfolded, into the pane.</summary>
    private void PushSummary() =>
        _pane!.SetToolGroupSummary(new Markup(ToolGroupText.SummaryMarkup(_tally, expanded: false)), new Markup(ToolGroupText.SummaryMarkup(_tally, expanded: true)));

    /// <summary>
    /// Something other than a tool line is said: the open run is over (the pane shrinks a folded
    /// one to its summary) and the tally starts again.
    /// </summary>
    private void EndRun()
    {
        _tally.Clear();
        if (_run)
        {
            _run = false;
            _pane?.EndToolGroup();
        }
    }

    // The open tool run (the pane holds it) and the calls counted for its summary.
    private bool _run;
    private readonly List<(string Name, int Count)> _tally = new();

    /// <summary>A line-shaped write that is not a tool's: it ends the tool run, then <see cref="WriteLine"/>.</summary>
    private void Line(string fullMarkup, string inlineMarkup)
    {
        EndRun();
        WriteLine(fullMarkup, inlineMarkup);
    }

    /// <summary>A line-shaped write: its own line, except right after a bare glyph where it continues that line.</summary>
    private void WriteLine(string fullMarkup, string inlineMarkup)
    {
        EndThinking();
        _heldWhitespace.Clear();
        if (_state == LineState.GlyphOnly)
        {
            if (_slot)
            {
                // The bare glyph is in the slot: dropped, and written into the flow ahead of the line.
                using (_pane!.Batch())
                {
                    DropSlot();
                    _console.Write(new RawText(AssistantGlyph, Theme.Accent));
                    _console.MarkupLine(inlineMarkup);
                }
            }
            else
            {
                _console.MarkupLine(inlineMarkup);
            }
        }
        else
        {
            BreakIfMidText();
            _console.MarkupLine(fullMarkup);
        }

        _state = LineState.AtLineStart;
    }

    private void BreakIfMidText()
    {
        EndThinking();
        _heldWhitespace.Clear();
        if (_state == LineState.AtLineStart)
        {
            return;
        }

        if (_slot && _assistantOpen)
        {
            if (_state == LineState.GlyphOnly)
            {
                using (_pane!.Batch())
                {
                    DropSlot();
                    _console.Write(new RawText(AssistantGlyph, Theme.Accent));
                    _console.WriteLine();
                }
            }
            else
            {
                // The block's lines go into the flow, ended: the next write starts its own line.
                CommitSlot();
            }
        }
        else
        {
            _console.WriteLine();
        }

        _state = LineState.AtLineStart;
    }

    /// <summary>The slot's text becomes transcript; the next stretch of this reply is flush-left.</summary>
    private void CommitSlot()
    {
        _pane!.CommitLive();
        _reply.Clear();
        _glyph = false;
    }

    /// <summary>The slot is emptied without writing (nothing was said in it).</summary>
    private void DropSlot()
    {
        _pane!.DiscardLive();
        _reply.Clear();
        _glyph = false;
    }

    private static Color DiagnosticColor(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Error => Theme.Bad,
        DiagnosticLevel.Warning => Theme.Warn,
        _ => Theme.Dim,
    };
}
