using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using NeonSidekick.Shell;

namespace NeonSidekick.Docker;

/// <summary>
/// One of the engine's log timestamps (2026-10-23, the Docker log window): whole seconds since the Unix epoch and the
/// nanoseconds after them, ordered. The engine writes RFC3339Nano, which drops trailing zeros, so the text is never compared
/// as a string; <see cref="Since"/> is the <c>since=</c> value the engine reads back (<c>seconds.nanoseconds</c>).
/// </summary>
public readonly record struct DockerStamp(long Seconds, int Nanos) : IComparable<DockerStamp>
{
    public int CompareTo(DockerStamp other) => Seconds != other.Seconds ? Seconds.CompareTo(other.Seconds) : Nanos.CompareTo(other.Nanos);

    public static bool operator <(DockerStamp a, DockerStamp b) => a.CompareTo(b) < 0;

    public static bool operator >(DockerStamp a, DockerStamp b) => a.CompareTo(b) > 0;

    public static bool operator <=(DockerStamp a, DockerStamp b) => a.CompareTo(b) <= 0;

    public static bool operator >=(DockerStamp a, DockerStamp b) => a.CompareTo(b) >= 0;

    /// <summary>The engine's <c>since=</c> form: <c>1761228131.123450000</c>.</summary>
    public string Since => Seconds.ToString(CultureInfo.InvariantCulture) + "." + Nanos.ToString("D9", CultureInfo.InvariantCulture);
}

/// <summary>The engine's RFC3339Nano timestamps read (2026-10-23, the Docker log window). Pure.</summary>
public static class DockerTimestamp
{
    /// <summary>
    /// <c>2026-10-23T14:02:11.12345Z</c> read into a <see cref="DockerStamp"/>: up to nine fraction digits (more are cut), the
    /// fraction optional, <c>Z</c> or a numeric offset (folded to UTC; the engine writes <c>Z</c>). False for anything else.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out DockerStamp stamp)
    {
        stamp = default;
        if (text.Length < 20 || text[10] != 'T' || text[4] != '-' || text[13] != ':')
        {
            return false;
        }

        int at = 19;
        int nanos = 0;
        if (text[at] == '.')
        {
            at++;
            int digits = 0;
            while (at < text.Length && char.IsAsciiDigit(text[at]))
            {
                if (digits < 9)
                {
                    nanos = nanos * 10 + (text[at] - '0');
                    digits++;
                }

                at++;
            }

            if (digits == 0)
            {
                return false;
            }

            for (; digits < 9; digits++)
            {
                nanos *= 10;
            }
        }

        string whole = string.Concat(text[..19], text[at..]);
        if (!DateTimeOffset.TryParseExact(whole, "yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            || text[at..].IsEmpty)
        {
            return false;
        }

        stamp = new DockerStamp(parsed.ToUnixTimeSeconds(), nanos);
        return true;
    }
}

/// <summary>One line of a followed log: the text (colour codes gone), whether it came on stderr, and its engine timestamp.</summary>
public readonly record struct DockerLogLine(string Text, bool IsError, DockerStamp? Stamp);

/// <summary>How a log follow or an events read ended (2026-10-23, the Docker log window).</summary>
public enum DockerFollowOutcome
{
    /// <summary>The engine closed the stream: the container stopped, most likely (the feed checks).</summary>
    Ended,

    /// <summary>A 404: no such container.</summary>
    NotFound,

    /// <summary>Any other HTTP refusal (a logging driver that cannot be read, 501): <see cref="DockerFollowEnd.Error"/> says it.</summary>
    Refused,

    /// <summary>The engine could not be reached, or the stream broke: <see cref="DockerFollowEnd.Error"/> says it.</summary>
    Failed,
}

/// <summary>A follow's end and, for a refusal or a failure, the <see cref="DockerText"/> sentence.</summary>
public sealed record DockerFollowEnd(DockerFollowOutcome Outcome, string? Error = null)
{
    public static readonly DockerFollowEnd Ended = new(DockerFollowOutcome.Ended);

    public static readonly DockerFollowEnd NotFound = new(DockerFollowOutcome.NotFound);
}

/// <summary>
/// A followed log's bytes turned into lines as they come (2026-10-23, the Docker log window: <see cref="DockerLogStream"/>
/// reads a whole capped body; this reads a stream that never ends). Framed or raw is decided once, from the content type or
/// the first eight bytes (a first chunk can be shorter than a frame header, and API 1.41 labels its framed stream raw). Each
/// stream keeps its own UTF-8 decoder and its line in progress, so a frame, a character or a line cut across chunks joins up.
/// With <c>timestamps=1</c> the engine prefixes every message with its stamp and a space: framed, a frame is a message, so
/// the stamp is taken at each frame's start (a long message's second part joins its first without a stamp in the middle);
/// raw (a TTY), at each line's start. CR LF, a lone CR and LF end a line; colour codes go; a line past
/// <c>maxLineChars</c> goes out at once, never cut inside a surrogate pair. Not thread-safe: one reader feeds it. Pure.
/// </summary>
public sealed class DockerLogDecoder
{
    /// <summary>A frame header's length.</summary>
    public const int HeaderBytes = 8;

    /// <summary>The longest text taken for a stamp before it is read as the line's own.</summary>
    private const int MaxStampChars = 40;

    private readonly string? _contentType;
    private readonly byte[] _first = new byte[HeaderBytes];
    private readonly byte[] _header = new byte[HeaderBytes];
    private readonly Lane _out;
    private readonly Lane _err;
    private int _firstFill;
    private bool? _framed;
    private int _headerFill;
    private long _remaining;
    private Lane? _current;

    /// <param name="contentType">The answer's media type; null leaves the choice to the first bytes.</param>
    /// <param name="stamped">Whether the engine was asked for <c>timestamps=1</c>.</param>
    /// <param name="maxLineChars">The longest line handed over in one piece.</param>
    public DockerLogDecoder(string? contentType, bool stamped = true, int maxLineChars = OutputBuffer.MaxLineChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineChars, 2);
        _contentType = contentType;
        _out = new Lane(isError: false, stamped, maxLineChars);
        _err = new Lane(isError: true, stamped, maxLineChars);
        if (contentType is not null && contentType.StartsWith(DockerLogStream.MultiplexedType, StringComparison.OrdinalIgnoreCase))
        {
            Decide(framed: true);
        }
    }

    /// <summary>Whether the stream is framed; null until enough bytes came to tell.</summary>
    public bool? Framed => _framed;

    /// <summary>The lines <paramref name="data"/> completes, added to <paramref name="into"/>.</summary>
    public void Feed(ReadOnlySpan<byte> data, List<DockerLogLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (_framed is null)
        {
            int take = Math.Min(HeaderBytes - _firstFill, data.Length);
            data[..take].CopyTo(_first.AsSpan(_firstFill));
            _firstFill += take;
            data = data[take..];
            if (_firstFill < HeaderBytes)
            {
                return;
            }

            DecideFromFirst(into);
        }

        FeedDecided(data, into);
    }

    /// <summary>The stream's end: what is still held handed over as lines (a last line without its newline among them).</summary>
    public void Flush(List<DockerLogLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (_framed is null)
        {
            DecideFromFirst(into);
        }

        _out.Flush(into);
        _err.Flush(into);
    }

    private void DecideFromFirst(List<DockerLogLine> into)
    {
        byte[] first = _first[.._firstFill];
        _firstFill = 0;
        Decide(DockerLogStream.IsMultiplexed(_contentType, first));
        FeedDecided(first, into);
    }

    private void Decide(bool framed)
    {
        _framed = framed;
        _out.Framed = framed;
        _err.Framed = framed;
        if (!framed)
        {
            _out.ExpectStamp();
        }
    }

    private void FeedDecided(ReadOnlySpan<byte> data, List<DockerLogLine> into)
    {
        if (_framed != true)
        {
            _out.Bytes(data, into);
            return;
        }

        while (data.Length > 0)
        {
            if (_remaining == 0)
            {
                int take = Math.Min(HeaderBytes - _headerFill, data.Length);
                data[..take].CopyTo(_header.AsSpan(_headerFill));
                _headerFill += take;
                data = data[take..];
                if (_headerFill < HeaderBytes)
                {
                    return;
                }

                _headerFill = 0;
                _remaining = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4, 4));
                _current = _header[0] == 2 ? _err : _out;
                _current.ExpectStamp();
                continue;
            }

            int n = (int)Math.Min(_remaining, data.Length);
            _current!.Bytes(data[..n], into);
            _remaining -= n;
            data = data[n..];
        }
    }

    /// <summary>One stream's decoder, its line in progress and its stamp.</summary>
    private sealed class Lane(bool isError, bool stamped, int maxLineChars)
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _stamp = new();
        private char[] _chars = new char[1024];
        private bool _expectStamp;
        private bool _skipLf;
        private DockerStamp? _last;

        public bool Framed { get; set; }

        public void ExpectStamp()
        {
            if (stamped)
            {
                _expectStamp = true;
            }
        }

        public void Bytes(ReadOnlySpan<byte> data, List<DockerLogLine> into)
        {
            if (data.IsEmpty)
            {
                return;
            }

            int need = _decoder.GetCharCount(data, flush: false);
            if (_chars.Length < need)
            {
                _chars = new char[Math.Max(need, _chars.Length * 2)];
            }

            int count = _decoder.GetChars(data, _chars, flush: false);
            for (int i = 0; i < count; i++)
            {
                Char(_chars[i], into);
            }
        }

        public void Flush(List<DockerLogLine> into)
        {
            int need = _decoder.GetCharCount([], flush: true);
            if (need > 0)
            {
                char[] tail = new char[need];
                int count = _decoder.GetChars([], tail, flush: true);
                for (int i = 0; i < count; i++)
                {
                    Char(tail[i], into);
                }
            }

            if (_stamp.Length > 0)
            {
                _line.Append(_stamp);
                _stamp.Clear();
            }

            _expectStamp = false;
            if (_line.Length > 0)
            {
                EndLine(into);
            }
        }

        private void Char(char c, List<DockerLogLine> into)
        {
            // A CR's LF first: it belongs to the line the CR ended, never to the next line's stamp.
            if (_skipLf)
            {
                _skipLf = false;
                if (c == '\n')
                {
                    return;
                }
            }

            if (_expectStamp)
            {
                if (c == ' ')
                {
                    if (DockerTimestamp.TryParse(_stamp.ToString(), out var stamp))
                    {
                        _last = stamp;
                    }
                    else
                    {
                        _line.Append(_stamp).Append(' ');
                    }

                    _stamp.Clear();
                    _expectStamp = false;
                    return;
                }

                if (_stamp.Length < MaxStampChars && c is not ('\n' or '\r'))
                {
                    _stamp.Append(c);
                    return;
                }

                // Not a stamp after all: what was held is the line's own text.
                _line.Append(_stamp);
                _stamp.Clear();
                _expectStamp = false;
            }

            switch (c)
            {
                case '\r':
                    EndLine(into);
                    _skipLf = true;
                    return;
                case '\n':
                    EndLine(into);
                    return;
            }

            _line.Append(c);
            // One past the limit before the cut, so a pair whose second half has just come is never split.
            if (_line.Length > maxLineChars)
            {
                string text = _line.ToString();
                int keep = Files.TextCut.SafeLength(text, maxLineChars);
                into.Add(new DockerLogLine(DockerLogStream.Clean(text[..keep]), isError, _last));
                _line.Clear().Append(text, keep, text.Length - keep);
            }
        }

        private void EndLine(List<DockerLogLine> into)
        {
            into.Add(new DockerLogLine(DockerLogStream.Clean(_line.ToString()), isError, _last));
            _line.Clear();
            if (!Framed)
            {
                ExpectStamp();
            }
        }
    }
}
