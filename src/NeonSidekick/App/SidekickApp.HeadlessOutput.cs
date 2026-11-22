using NeonSidekick.Diagnostics;

namespace NeonSidekick.App;

// ── The headless run's output (2026-11-21, feature ideas #41 and #43) ──────────────────────────────────────────────────────────────────
// Every headless write goes through this file: the reply to stdout, everything else (the banner, You:, [tool], [notice], [error], the
// diagnostics) to the info writer, which is stdout too unless --quiet or --prompt sends it to stderr. Each writer keeps its own line-start
// flag. Every line written passes the one check that notes an [error] line as the input line's failure (exit 4), so no command can print
// a failure that exits 0; HeadlessFailuresTests.NoErrorLine_BypassesTheCounter holds that no other file reaches a writer.

public sealed partial class SidekickApp
{
    /// <summary>One of the headless run's two writers and whether its cursor is at a line's start.</summary>
    private sealed class HeadlessWriter(TextWriter writer)
    {
        public TextWriter Writer { get; } = writer;

        public bool AtLineStart { get; set; } = true;
    }

    /// <summary>stdout: the replies, and in the normal mode everything else too.</summary>
    private HeadlessWriter _replyOut = null!;

    /// <summary>Where everything but the replies goes: <see cref="_replyOut"/>, or stderr under <see cref="SidekickOptions.RepliesOnly"/>.</summary>
    private HeadlessWriter _infoOut = null!;

    /// <summary>The stderr writer <c>Program</c> made (UTF-8, no BOM); null in tests that pass none.</summary>
    private TextWriter? _headlessErrorWriter;

    /// <summary>Whether stdout carries the replies alone, for this run.</summary>
    private bool _repliesOnly;

    /// <summary>Whether this is a one-shot <c>--prompt</c> run: no <c>You: </c> prompt.</summary>
    private bool _oneShot;

    /// <summary>The input line being answered (counted up at each) and the last whose answer wrote text, for the blank line between replies.</summary>
    private int _replyIndex;

    private int _replyWritten;

    /// <summary>What the run's failures were, for the summary and exit 4.</summary>
    private HeadlessFailures _headlessFailures = new();

    /// <summary>What the input line was, for a failure's entry: its command word, or <see cref="HeadlessText.AMessage"/>.</summary>
    private string _headlessLineWord = HeadlessText.AMessage;

    /// <summary>The longest why a failure's entry keeps, in cells.</summary>
    private const int FailureWhyCells = 100;

    /// <summary>The writers as the constructor was given them.</summary>
    private void InitHeadlessWriters(TextWriter? output, TextWriter? error)
    {
        _replyOut = new HeadlessWriter(output ?? Console.Out);
        _infoOut = _replyOut;
        _headlessErrorWriter = error;
    }

    /// <summary>The run's writers chosen from its flags, at the start of a headless run.</summary>
    private void StartHeadlessOutput()
    {
        _repliesOnly = _options.RepliesOnly;
        _oneShot = _options.Prompt is not null || _options.PromptFile is not null;
        _infoOut = _repliesOnly ? new HeadlessWriter(_headlessErrorWriter ?? Console.Error) : _replyOut;
        _headlessFailures = new HeadlessFailures();
        _headlessLineWord = HeadlessText.AMessage;
        _replyIndex = 0;
        _replyWritten = 0;
    }

    /// <summary>A new input line: what it is, for its failure's entry.</summary>
    private void HeadlessNextLine(string text, bool message = false)
    {
        _headlessFailures.NextLine();
        _replyIndex++;   // what this line answers is one reply on stdout
        _headlessLineWord = !message && text.StartsWith('/') ? text.Split(' ', 2)[0].ToLowerInvariant() : HeadlessText.AMessage;
    }

    /// <summary>A failure the run names itself (a loop that stopped, a review, <c>/compact</c>), as the input line's entry.</summary>
    private void HeadlessFail(string what, string why) => _headlessFailures.Add(what, ShortWhy(why));

    private static string ShortWhy(string why)
    {
        string first = why.Split('\n', 2)[0].Trim();
        if (first.StartsWith("Error: ", StringComparison.Ordinal))
        {
            first = first["Error: ".Length..];
        }

        return UI.TextCells.Width(first) <= FailureWhyCells ? first : UI.TextCells.Cut(first, FailureWhyCells);
    }

    /// <summary>The text after an <c>[error] </c> tag (after the reply prefix or not), or null for any other line.</summary>
    internal static string? HeadlessErrorOf(string line)
    {
        const string Tag = "[error] ";
        string rest = line.StartsWith(HeadlessReplyPrefix, StringComparison.Ordinal) ? line[HeadlessReplyPrefix.Length..] : line;
        return rest.StartsWith(Tag, StringComparison.Ordinal) ? rest[Tag.Length..] : null;
    }

    /// <summary>Whether a line is the app's own (a bracketed tag after the reply prefix or not) rather than an answer.</summary>
    private static bool HeadlessTagged(string line)
    {
        string rest = line.StartsWith(HeadlessReplyPrefix, StringComparison.Ordinal) ? line[HeadlessReplyPrefix.Length..] : line;
        return rest.StartsWith('[');
    }

    private static string Unprefixed(string line) =>
        line.StartsWith(HeadlessReplyPrefix, StringComparison.Ordinal) ? line[HeadlessReplyPrefix.Length..]
        : line.StartsWith(HeadlessClaudePrefix, StringComparison.Ordinal) ? line[HeadlessClaudePrefix.Length..]
        : line;

    private void NoteHeadlessError(string line)
    {
        if (HeadlessErrorOf(line) is { } why)
        {
            _headlessFailures.Add(_headlessLineWord, ShortWhy(why));
        }
    }

    /// <summary>
    /// A whole line: an answer (<c>Neon: …</c>, a command's own rows) to stdout, its prefix dropped when stdout carries replies alone; a tagged
    /// line to the info writer. An <c>[error]</c> line is the input line's failure.
    /// </summary>
    private async Task HeadlessLineAsync(string line)
    {
        NoteHeadlessError(line);
        if (_repliesOnly && !HeadlessTagged(line))
        {
            await StartReplyTextAsync().ConfigureAwait(false);
            await _replyOut.Writer.WriteLineAsync(Unprefixed(line)).ConfigureAwait(false);
            _replyOut.AtLineStart = true;
            return;
        }

        if (_repliesOnly)
        {
            await EnsureLineStartAsync(_infoOut).ConfigureAwait(false);
        }

        await _infoOut.Writer.WriteLineAsync(line).ConfigureAwait(false);
        _infoOut.AtLineStart = true;
    }

    /// <summary>A line of the run's own (the banner, the start's MCP lines): never an answer, so stderr when stdout carries replies alone.</summary>
    private async Task HeadlessInfoLineAsync(string line)
    {
        NoteHeadlessError(line);
        if (_repliesOnly)
        {
            await EnsureLineStartAsync(_infoOut).ConfigureAwait(false);
        }

        await _infoOut.Writer.WriteLineAsync(line).ConfigureAwait(false);
        _infoOut.AtLineStart = true;
    }

    /// <summary>
    /// A notice line, on a line of its own; since 2026-10-17 spaced after a selector sequence the terminal draws one cell wide
    /// (<see cref="UI.TextCells.SpaceSelectorSequences"/> under <c>headlessNarrow</c>, Terminal.app's main screen, never a pipe). A reply never is.
    /// </summary>
    private async Task HeadlessNoticeLineAsync(string line)
    {
        await EnsureLineStartAsync(_infoOut).ConfigureAwait(false);
        await HeadlessInfoLineAsync(UI.TextCells.SpaceSelectorSequences(line, _headlessNarrow)).ConfigureAwait(false);
    }

    /// <summary>Both writers at a line's start: the end of a turn, of the run.</summary>
    private async Task EnsureHeadlessLineStartAsync()
    {
        await EnsureLineStartAsync(_replyOut).ConfigureAwait(false);
        await EnsureLineStartAsync(_infoOut).ConfigureAwait(false);
    }

    private static async Task EnsureLineStartAsync(HeadlessWriter writer)
    {
        if (!writer.AtLineStart)
        {
            await writer.Writer.WriteLineAsync().ConfigureAwait(false);
            writer.AtLineStart = true;
        }
    }

    /// <summary>A reply begins: <paramref name="prefix"/> (<c>Neon: </c>, <c>Claude: </c>) on stdout, or nothing when stdout carries replies alone.</summary>
    private async Task WriteHeadlessPrefixAsync(string prefix = HeadlessReplyPrefix)
    {
        if (_repliesOnly)
        {
            return;
        }

        await _replyOut.Writer.WriteAsync(prefix).ConfigureAwait(false);
        _replyOut.AtLineStart = false;
    }

    /// <summary>A piece of a streamed reply, flushed so a pipe sees tokens live.</summary>
    private async Task HeadlessReplyTextAsync(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (_repliesOnly)
        {
            await StartReplyTextAsync().ConfigureAwait(false);
        }

        await _replyOut.Writer.WriteAsync(text).ConfigureAwait(false);
        await _replyOut.Writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        _replyOut.AtLineStart = text.EndsWith('\n');
    }

    /// <summary>Replies alone on stdout: a line's answer after another line's starts on its own line, a blank line between them.</summary>
    private async Task StartReplyTextAsync()
    {
        if (_replyWritten == _replyIndex && _replyWritten != 0)
        {
            return;
        }

        if (_replyWritten != 0)
        {
            await EnsureLineStartAsync(_replyOut).ConfigureAwait(false);
            await _replyOut.Writer.WriteLineAsync().ConfigureAwait(false);
        }

        if (_replyIndex == 0)
        {
            _replyIndex = 1;   // a command's line with no prefix before it
        }

        _replyWritten = _replyIndex;
    }

    /// <summary>The <c>You: </c> prompt before a line is read; none in a one-shot run.</summary>
    private async Task HeadlessPromptAsync(CancellationToken cancellationToken)
    {
        if (_oneShot)
        {
            return;
        }

        await _infoOut.Writer.WriteAsync("You: ").ConfigureAwait(false);
        await _infoOut.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        _infoOut.AtLineStart = false;
    }

    /// <summary>A gap or Ralph pass's line, echoed as if typed after the prompt.</summary>
    private async Task HeadlessEchoAsync(string text)
    {
        if (_oneShot)
        {
            await EnsureLineStartAsync(_infoOut).ConfigureAwait(false);
        }

        await _infoOut.Writer.WriteAsync(text + Environment.NewLine).ConfigureAwait(false);
        _infoOut.AtLineStart = true;
    }

    /// <summary>A line was read: a real console echoed the user's Enter; a pipe did not, and that is the caller's rendering problem, not ours.</summary>
    private void HeadlessLineRead() => _infoOut.AtLineStart = true;

    /// <summary>A notice that goes to stderr whatever the mode (the closing <c>session #N</c>): the info writer when that is stderr already, else its own line there.</summary>
    private async Task HeadlessStderrNoticeAsync(string text)
    {
        if (_repliesOnly)
        {
            await HeadlessNoticeLineAsync("[notice] " + text).ConfigureAwait(false);
            return;
        }

        var error = _headlessErrorWriter ?? Console.Error;
        await error.WriteLineAsync("[notice] " + text).ConfigureAwait(false);
        await error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Both writers flushed.</summary>
    private async Task FlushHeadlessAsync()
    {
        await _replyOut.Writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        if (!ReferenceEquals(_infoOut, _replyOut))
        {
            await _infoOut.Writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>One line written at once, on a line of its own (the advisor's view, a Docker step, a diagnostic).</summary>
    private void HeadlessSyncLine(string line)
    {
        NoteHeadlessError(line);
        if (!_infoOut.AtLineStart)
        {
            _infoOut.Writer.WriteLine();
        }

        _infoOut.Writer.WriteLine(line);
        _infoOut.Writer.Flush();
        _infoOut.AtLineStart = true;
    }

    /// <summary>
    /// Warnings and errors reach the headless info writer as <c>[category] message</c> lines; anything
    /// quieter is dropped. Synchronous because <see cref="DiagnosticLog"/> calls subscribers
    /// inline; the turn loop awaits between writes, so the two never interleave mid-line.
    /// </summary>
    private void OnHeadlessDiagnostic(DiagnosticEvent evt)
    {
        if (evt.Level < DiagnosticLevel.Warning)
        {
            return;
        }

        HeadlessSyncLine($"[{evt.Category}] {evt.Message}");
    }
}
