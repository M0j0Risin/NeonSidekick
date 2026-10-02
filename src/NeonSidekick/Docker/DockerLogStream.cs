using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.Docker;

/// <summary>Which of a container's two streams a log read keeps.</summary>
public enum DockerLogStreams
{
    Both,
    Stdout,
    Stderr,
}

/// <summary>
/// A container's log as the engine sends it (2026-10-02), turned into lines. A container without a TTY answers
/// <c>application/vnd.docker.multiplexed-stream</c>: frames of an eight-byte header — the stream (1 stdout, 2 stderr), three
/// zeros, the length big-endian — then that many bytes; one with a TTY answers the raw bytes. The read is capped
/// (<see cref="MaxBytes"/>) and a frame cut by the cap keeps what arrived. Colour codes go (a model reads them as noise), as
/// do carriage returns. Pure.
/// </summary>
public static partial class DockerLogStream
{
    /// <summary>The most bytes one log read takes from the engine.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The multiplexed stream's content type.</summary>
    public const string MultiplexedType = "application/vnd.docker.multiplexed-stream";

    /// <summary>
    /// Whether <paramref name="data"/> is framed: the content type says so, or — an older engine that labels both kinds
    /// raw — it opens with a frame header (a stream byte of 0 to 2, then three zeros).
    /// </summary>
    public static bool IsMultiplexed(string? contentType, ReadOnlySpan<byte> data)
    {
        if (contentType is not null && contentType.StartsWith(MultiplexedType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (contentType is not null && contentType.Contains("raw-stream", StringComparison.OrdinalIgnoreCase) && data.Length > 0 && data[0] > 2)
        {
            return false;
        }

        return data.Length >= 8 && data[0] <= 2 && data[1] == 0 && data[2] == 0 && data[3] == 0;
    }

    /// <summary>The text of the frames of <paramref name="data"/> that belong to <paramref name="streams"/>.</summary>
    public static string Demultiplex(ReadOnlySpan<byte> data, DockerLogStreams streams)
    {
        var text = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        int at = 0;
        while (at + 8 <= data.Length)
        {
            byte kind = data[at];
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at + 4, 4)), int.MaxValue);
            at += 8;
            int take = Math.Min(length, data.Length - at);
            bool keep = streams switch
            {
                DockerLogStreams.Stdout => kind != 2,
                DockerLogStreams.Stderr => kind == 2,
                _ => true,
            };
            if (keep && take > 0)
            {
                var chunk = data.Slice(at, take);
                char[] chars = new char[decoder.GetCharCount(chunk, flush: false)];
                int count = decoder.GetChars(chunk, chars, flush: false);
                text.Append(chars, 0, count);
            }

            at += take;
        }

        return text.ToString();
    }

    /// <summary>
    /// The lines of a log read: framed or raw as <see cref="IsMultiplexed"/> says, the colour codes and carriage returns
    /// gone, the empty tail after the last newline dropped.
    /// </summary>
    public static IReadOnlyList<string> Lines(ReadOnlySpan<byte> data, string? contentType, DockerLogStreams streams)
    {
        string text = IsMultiplexed(contentType, data) ? Demultiplex(data, streams) : Encoding.UTF8.GetString(data);
        return SplitLines(text);
    }

    /// <summary><paramref name="text"/> cut into lines, colour codes and carriage returns gone, a final empty line dropped.</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string clean = Ansi().Replace(text, "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = clean.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> of <paramref name="stream"/>: the bytes, and whether more were left.</summary>
    public static async Task<(byte[] Data, bool Capped)> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (buffer.Length < maxBytes)
        {
            int want = (int)Math.Min(chunk.Length, maxBytes - buffer.Length);
            int read = await stream.ReadAsync(chunk.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (buffer.ToArray(), false);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), true);
    }

    /// <summary>CSI sequences (<c>ESC [ … letter</c>) and OSC sequences (<c>ESC ] … BEL</c>).</summary>
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.CultureInvariant)]
    private static partial Regex Ansi();
}
