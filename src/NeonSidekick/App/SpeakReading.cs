using System.Globalization;
using NeonSidekick.Speech;

namespace NeonSidekick.App;

/// <summary>
/// The last <c>/speak</c> reading (2026-09-17): the file, its text split into the sentences the
/// speech chunks it into, the sentence the current playback started at and its speaker — so the
/// hint row can say where the reading is (<see cref="StatusLine"/>), a bare <c>/speak</c> can
/// resume (<see cref="Resume"/>) and <c>/speak &lt;n&gt;</c> can seek.
///
/// <para>The unit is the sentence because that is what the audio path can know: the speaker
/// records each sentence's byte range and the device reports what it still owes, so the play
/// head names a sentence (<see cref="SpeechOutput.PlayingChunk"/>, ±100 ms — one WinMM buffer);
/// neither engine returns word or character timing. A stopped reading resumes at the start of
/// the sentence that was sounding (<see cref="SpeechOutput.StoppedAtChunk"/>, taken before the
/// device buffer was cleared).</para>
///
/// <para>The screen keeps one instance across turns (the file is remembered until another
/// <c>/speak &lt;file&gt;</c>, <c>/clear</c>, <c>/new</c>, a profile switch or a <c>/cwd</c> change)
/// and decides itself whether the status is on the hint row. <see cref="StatusLine"/> reads the
/// speaker's counters alone, so the pane's tick may call it from the timer thread.</para>
/// </summary>
internal sealed class SpeakReading
{
    /// <summary>One sentence as the chunker cut it: a trimmed, verbatim slice of the file's text, and where it starts.</summary>
    public sealed record Sentence(string Text, int Offset);

    /// <param name="file">The file as the file tools name it (<c>ReadResult.Relative</c>).</param>
    /// <param name="text">The file's text as read (LF-joined, cut at the read cap).</param>
    public SpeakReading(string file, string text)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Sentences = Split(text);
    }

    public string File { get; }

    public string Text { get; }

    public IReadOnlyList<Sentence> Sentences { get; }

    public int Count => Sentences.Count;

    /// <summary>The read was cut at the read cap (<c>ReadResult.Truncated</c>): the block gets the cut notice on every start.</summary>
    public bool Cut { get; init; }

    /// <summary>What the text is: a file (<c>/speak</c>, resumable) or a typed line (<c>/echo</c>, 2026-09-17: the same block and voice, its own wording on the hint row, never resumed).</summary>
    public enum Kind
    {
        File,
        Echo,
    }

    public Kind Source { get; init; } = Kind.File;

    /// <summary>The sentence (1-based) the current or last playback started at.</summary>
    public int From { get; private set; } = 1;

    /// <summary>The speaker of the current or last playback; null while speech was off for it (nothing to position on).</summary>
    public SpeechOutput? Speaker { get; private set; }

    /// <summary>Audio is still owed: the reading is playing under the input line.</summary>
    public bool Playing => Speaker is { Completion.IsCompleted: false };

    /// <summary>The playback ran to its end unstopped — or never played (speech off): a bare <c>/speak</c> starts at the top again.</summary>
    public bool Done => Speaker is null || (Speaker.Completion.IsCompleted && !Speaker.StoppedEarly);

    /// <summary>
    /// The sentence the reading is at (1-based): the one at the play head while it plays, the one
    /// that was sounding when it was stopped, the last one once it ran through; never before
    /// <see cref="From"/>, never past <see cref="Count"/>.
    /// </summary>
    public int Position
    {
        get
        {
            if (Speaker is not { } speaker)
            {
                return Count;
            }

            if (speaker.Completion.IsCompleted && !speaker.StoppedEarly)
            {
                return Count;
            }

            int chunk = speaker.StoppedAtChunk > 0 ? speaker.StoppedAtChunk : speaker.PlayingChunk;
            return Math.Clamp(From - 1 + Math.Max(1, chunk), From, Math.Max(From, Count));
        }
    }

    /// <summary>Where a bare <c>/speak</c> starts: the stopped sentence again from its start, the top after a full read.</summary>
    public int Resume => Done ? 1 : Position;

    /// <summary>A playback began at <paramref name="from"/> with <paramref name="speaker"/> (null with speech off).</summary>
    public void Started(int from, SpeechOutput? speaker)
    {
        if (from < 1 || from > Math.Max(1, Count))
        {
            throw new ArgumentOutOfRangeException(nameof(from));
        }

        From = from;
        Speaker = speaker;
    }

    // ── The hint row's part (pinned) ────────────────────────────────────────

    /// <summary>The open book ahead of the count (U+1F4D6, a colour emoji two cells wide like the speech strip's). The user's shape, 2026-09-17.</summary>
    public const string Glyph = "📖";

    public static string ReadingLine(string file, int at, int total) => $"{Glyph} {N(at)}/{N(total)} reading {Name(file)}";

    public static string StoppedLine(string file, int at, int total) => $"{Glyph} {N(at)}/{N(total)} stopped {Name(file)}";

    public static string ReadLine(string file, int total) => $"{Glyph} {N(total)}/{N(total)} read {Name(file)}";

    /// <summary>The speech balloon ahead of an echo's count (U+1F4AC). Pinned.</summary>
    public const string EchoGlyph = "💬";

    public static string SpeakingLine(int at, int total) => $"{EchoGlyph} {N(at)}/{N(total)} speaking";

    public static string EchoStoppedLine(int at, int total) => $"{EchoGlyph} {N(at)}/{N(total)} stopped";

    public static string SpokenLine(int total) => $"{EchoGlyph} {N(total)}/{N(total)} spoken";

    /// <summary>
    /// The hint row's part: <c>📖 3/40 reading notes.md</c> while it plays, <c>📖 3/40 stopped notes.md</c>
    /// after a stop, <c>📖 40/40 read notes.md</c> once it ran through (or with speech off) — the
    /// file's name alone, never its folder; an echo reads <c>💬 1/2 speaking</c> / <c>💬 1/2 stopped</c> / <c>💬 2/2 spoken</c>.
    /// </summary>
    public string StatusLine() => Source == Kind.Echo
        ? Playing ? SpeakingLine(Position, Count) : Done ? SpokenLine(Count) : EchoStoppedLine(Position, Count)
        : Playing ? ReadingLine(File, Position, Count) : Done ? ReadLine(File, Count) : StoppedLine(File, Position, Count);

    /// <summary>The last segment of a relative path, either separator.</summary>
    public static string Name(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        int cut = file.TrimEnd('/', '\\').LastIndexOfAny(['/', '\\']);
        return cut < 0 ? file : file[(cut + 1)..];
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    // ── The split ───────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="text"/> as the speech chunks it (<see cref="SentenceChunker"/> over the
    /// whole text, then its flush): every sentence a trimmed, verbatim slice in order, so each
    /// one's offset is where it is next found after the previous one. A line of punctuation alone
    /// (<c>---</c>) yields no sentence, and nor does anything in a fenced code block. Pure.
    /// </summary>
    public static IReadOnlyList<Sentence> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chunker = new SentenceChunker();
        var pieces = new List<string>(chunker.Append(text));
        string tail = chunker.Flush();
        if (tail.Length > 0)
        {
            pieces.Add(tail);
        }

        // The fenced code blocks are never read (2026-09-25, CodeBlockFilter): a sentence starting inside one is dropped. None
        // straddles an edge — a block starts and ends on a line's edge, and the chunker breaks at every newline.
        var code = CodeBlockFilter.Spans(text);
        var sentences = new List<Sentence>(pieces.Count);
        int cursor = 0;
        foreach (var piece in pieces)
        {
            int at = text.IndexOf(piece, cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                // Cannot happen for a verbatim slice; keep the order rather than lose the sentence.
                at = cursor;
            }

            cursor = at + piece.Length;
            if (!code.Any(span => at >= span.Start && at < span.End))
            {
                sentences.Add(new Sentence(piece, at));
            }
        }

        return sentences;
    }

    /// <summary>Whether <paramref name="argument"/> is a sentence number (digits alone), and which.</summary>
    public static bool TryParsePosition(string argument, out int position)
    {
        ArgumentNullException.ThrowIfNull(argument);
        position = 0;
        string trimmed = argument.Trim();
        return trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit) && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out position);
    }

    /// <summary>
    /// Whether <paramref name="argument"/> is a path followed by a sentence number
    /// (<c>notes.txt 5</c>): the text after its last whitespace run is digits alone
    /// (<see cref="TryParsePosition"/>) and what is before it, trimmed, is not empty. The caller
    /// tries the whole argument as a path first, so a file named <c>notes 5</c> is still reached.
    /// </summary>
    public static bool TrySplitPosition(string argument, out string head, out int position)
    {
        ArgumentNullException.ThrowIfNull(argument);
        head = "";
        position = 0;
        string trimmed = argument.Trim();
        int cut = trimmed.LastIndexOfAny([' ', '\t']);
        if (cut <= 0 || !TryParsePosition(trimmed[(cut + 1)..], out position))
        {
            return false;
        }

        head = trimmed[..cut].Trim();
        return head.Length > 0;
    }
}
