using System.Text;

namespace NeonSidekick.Speech;

/// <summary>
/// Drops fenced code blocks — the fence lines and everything between them — from text on its way to the voice. The
/// <see cref="Llm.ThinkTagFilter"/> shape: one instance per reply, fed deltas as they stream, pure, no console, no log.
///
/// <para>Why it exists (2026-09-25, the user's ask): speech used to drop only the fence lines (<see cref="SpeakableText.StripMarkdown"/>)
/// and read the code between them, because the stream is cut into sentences before it is cleaned and the cleaner never sees a
/// whole block. The user's call: skip a block silently, always — code read aloud helps nobody. Only speech changes; the
/// transcript still shows the block.</para>
///
/// <para>Rules, CommonMark's for fences: a fence line is up to three spaces, then three or more backticks or tildes, then an info
/// string to the end of the line (for backticks, one with no backtick in it, so <c>```x``` is</c> stays inline code). A block runs
/// to a closing fence of the same character, at least as long, with nothing after it but spaces; an unclosed block runs to the
/// end of the reply. The whole block becomes one <c>\n</c>, so the sentence before it ends there (<see cref="SentenceChunker"/>
/// breaks on a newline) and the text after it starts afresh. A line is held back only while it could still be a fence; everything
/// else passes through at once. Indented code blocks and inline code are not touched.</para>
/// </summary>
public sealed class CodeBlockFilter
{
    private const int MaxIndent = 3;
    private const int MinFence = 3;

    // Outside a block: the current line's start, held while it could still be a fence (_holding).
    private readonly StringBuilder _held = new();
    private bool _holding = true;

    // Inside a block: its fence, and the current line so far (to test for the closing fence).
    private char _fence;
    private int _fenceLength;
    private readonly StringBuilder _blockLine = new();

    /// <summary>Feeds one delta and returns the text that is safe to speak now, possibly empty.</summary>
    public string Push(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        StringBuilder? output = null;
        foreach (char c in delta)
        {
            if (_fence != '\0')
            {
                if (c != '\n')
                {
                    _blockLine.Append(c);
                    continue;
                }

                if (IsClosingFence(_blockLine.ToString(), _fence, _fenceLength))
                {
                    _fence = '\0';
                    _holding = true;
                }

                _blockLine.Clear();
                continue;
            }

            if (!_holding)
            {
                (output ??= new StringBuilder()).Append(c);
                _holding = c == '\n';
                continue;
            }

            if (c == '\n')
            {
                string line = _held.ToString();
                _held.Clear();
                if (OpeningFence(line) is { } open)
                {
                    (_fence, _fenceLength) = open;
                    (output ??= new StringBuilder()).Append('\n');
                }
                else
                {
                    (output ??= new StringBuilder()).Append(line).Append('\n');
                }

                continue;
            }

            _held.Append(c);
            if (!CouldBeFence(_held.ToString()))
            {
                (output ??= new StringBuilder()).Append(_held);
                _held.Clear();
                _holding = false;
            }
        }

        return output?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// The end of the reply: a held line start that never became a fence is released. A block still open stays dropped (it was
    /// code to the end), and a fence line with nothing after it is one too.
    /// </summary>
    public string Flush()
    {
        string held = _held.ToString();
        _held.Clear();
        _holding = true;
        _blockLine.Clear();
        if (_fence != '\0')
        {
            _fence = '\0';
            return string.Empty;
        }

        return OpeningFence(held) is not null ? "\n" : held;
    }

    /// <summary><paramref name="text"/> without its fenced code blocks, each one a single <c>\n</c>. Pure.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var filter = new CodeBlockFilter();
        return filter.Push(text) + filter.Flush();
    }

    /// <summary>
    /// Where <paramref name="text"/>'s fenced code blocks are: each from its opening fence line's start to just past its closing
    /// fence line's newline (or the end of the text), in order — what <see cref="Strip"/> replaces with a <c>\n</c>. Pure.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> Spans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<(int Start, int End)>();
        int pos = 0;
        while (pos < text.Length)
        {
            int newline = text.IndexOf('\n', pos);
            int lineEnd = newline < 0 ? text.Length : newline;
            if (OpeningFence(text[pos..lineEnd]) is not { } open)
            {
                pos = newline < 0 ? text.Length : newline + 1;
                continue;
            }

            int start = pos;
            int end = text.Length;
            pos = newline < 0 ? text.Length : newline + 1;
            while (pos < text.Length)
            {
                int next = text.IndexOf('\n', pos);
                int stop = next < 0 ? text.Length : next;
                bool closes = IsClosingFence(text[pos..stop], open.Fence, open.Length);
                pos = next < 0 ? text.Length : next + 1;
                if (closes)
                {
                    end = pos;
                    break;
                }
            }

            spans.Add((start, end));
        }

        return spans;
    }

    /// <summary>The fence <paramref name="line"/> opens — its character and length — or null when it is no opening fence.</summary>
    private static (char Fence, int Length)? OpeningFence(string line)
    {
        int i = Indent(line);
        if (i < 0 || i >= line.Length || line[i] is not ('`' or '~'))
        {
            return null;
        }

        char fence = line[i];
        int run = Run(line, i, fence);
        if (run < MinFence)
        {
            return null;
        }

        // A backtick fence's info string has no backtick: "```x``` is" is inline code, not a fence.
        if (fence == '`' && line.IndexOf('`', i + run) >= 0)
        {
            return null;
        }

        return (fence, run);
    }

    /// <summary>Whether <paramref name="line"/> closes a block opened by <paramref name="length"/> of <paramref name="fence"/>.</summary>
    private static bool IsClosingFence(string line, char fence, int length)
    {
        int i = Indent(line);
        if (i < 0 || i >= line.Length || line[i] != fence)
        {
            return false;
        }

        int run = Run(line, i, fence);
        return run >= length && line[(i + run)..].Trim().Length == 0;
    }

    /// <summary>Whether a line that starts with <paramref name="prefix"/> (no newline yet) may still turn out to be an opening fence.</summary>
    private static bool CouldBeFence(string prefix)
    {
        int spaces = 0;
        while (spaces < prefix.Length && prefix[spaces] == ' ')
        {
            spaces++;
        }

        if (spaces > MaxIndent)
        {
            return false;
        }

        if (spaces == prefix.Length)
        {
            return true;
        }

        char fence = prefix[spaces];
        if (fence is not ('`' or '~'))
        {
            return false;
        }

        int run = Run(prefix, spaces, fence);
        if (spaces + run == prefix.Length)
        {
            return true;   // only fence characters so far: the next one may make three
        }

        return run >= MinFence && (fence == '~' || prefix.IndexOf('`', spaces + run) < 0);
    }

    /// <summary>The line's indent (up to three spaces), or -1 when it is indented further.</summary>
    private static int Indent(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        return i > MaxIndent ? -1 : i;
    }

    private static int Run(string text, int from, char c)
    {
        int i = from;
        while (i < text.Length && text[i] == c)
        {
            i++;
        }

        return i - from;
    }
}
