using System.Buffers;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;

namespace NeonSidekick.WebUi;

/// <summary>What a <c>Range</c> asks of a file: all of it, one slice, or nothing it has.</summary>
public enum ByteRangeKind
{
    Whole,
    Partial,
    Unsatisfiable,
}

/// <summary>A <c>Range</c> header read against a file's length: <see cref="Start"/> to <see cref="End"/>, both inclusive, for a partial one.</summary>
public readonly record struct ByteRange(ByteRangeKind Kind, long Start = 0, long End = 0)
{
    public long Count => End - Start + 1;
}

/// <summary>
/// A video file to the Web UI's Video tab (2026-11-12, the video-files plan): <see cref="PictureServer"/>'s original-file answer plus
/// ranges, which every <c>&lt;video&gt;</c> asks for. One range (<c>bytes=a-b</c>, <c>a-</c>, <c>-n</c>, Safari's <c>bytes=0-1</c>
/// probe) is a <c>206</c>; past the end a <c>416</c> with <c>bytes */len</c>; several at once, a malformed one or an <c>If-Range</c> that
/// isn't the ETag the whole file (<c>200</c>). The ETag is the file's length and write time (<see cref="WebFileResponse.Stamp"/>), so a
/// growing recording's <c>If-Range</c> misses and gets the whole file; a matching <c>If-None-Match</c> is a <c>304</c>. Always
/// <c>Accept-Ranges</c>, the type by the extension (<see cref="VideoFile.ContentType"/>), <c>Cross-Origin-Resource-Policy: same-origin</c>,
/// and <c>private, no-cache</c> instead of <c>/api</c>'s <c>no-store</c>. Opened with <c>ReadWrite|Delete</c> sharing, so a writer's lock
/// never refuses it; HEAD sends no body.
///
/// <para>The copy is bounded to the slice and runs on the request's abort linked with the host's stop, so <c>/webui</c> off or the exit never
/// waits on a phone's open stream. A copy that ends early is routine: a browser drops a range at every seek, and Kestrel's minimum response
/// rate closes a stream a paused video stops reading. So the cancel and the connection's reset end it with no line; only the file side's
/// errors log (Debug), and a file that shrank mid-copy aborts the connection rather than send short. Pure over <see cref="HttpContext"/>.</para>
/// </summary>
internal static class MediaFileServer
{
    /// <summary>The read buffer and the copy's step.</summary>
    public const int BufferSize = 64 * 1024;

    /// <summary>
    /// <paramref name="range"/> (the <c>Range</c> header) against a file of <paramref name="length"/> bytes whose ETag is <paramref name="etag"/>,
    /// honored only when <paramref name="ifRange"/> is empty or that ETag. Pure.
    /// </summary>
    public static ByteRange Parse(string? range, string? ifRange, string etag, long length)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return new ByteRange(ByteRangeKind.Whole);
        }

        if (!string.IsNullOrEmpty(ifRange) && !string.Equals(ifRange.Trim(), etag, StringComparison.Ordinal))
        {
            return new ByteRange(ByteRangeKind.Whole);   // changed since (or a date): the whole file, as the RFC says
        }

        string text = range.Trim();
        const string Unit = "bytes=";
        if (!text.StartsWith(Unit, StringComparison.OrdinalIgnoreCase) || text.Contains(',', StringComparison.Ordinal))
        {
            return new ByteRange(ByteRangeKind.Whole);   // another unit, or several ranges: answered whole
        }

        string spec = text[Unit.Length..].Trim();
        int dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return new ByteRange(ByteRangeKind.Whole);
        }

        string first = spec[..dash].Trim();
        string last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            // The last n bytes.
            if (!Digits(last, out long suffix))
            {
                return new ByteRange(ByteRangeKind.Whole);
            }

            if (suffix == 0 || length == 0)
            {
                return new ByteRange(ByteRangeKind.Unsatisfiable);
            }

            return new ByteRange(ByteRangeKind.Partial, Math.Max(0, length - suffix), length - 1);
        }

        if (!Digits(first, out long start))
        {
            return new ByteRange(ByteRangeKind.Whole);
        }

        long end = length - 1;
        if (last.Length > 0)
        {
            if (!Digits(last, out end) || end < start)
            {
                return new ByteRange(ByteRangeKind.Whole);
            }
        }

        if (start >= length)
        {
            return new ByteRange(ByteRangeKind.Unsatisfiable);
        }

        return new ByteRange(ByteRangeKind.Partial, start, Math.Min(end, length - 1));
    }

    /// <summary>
    /// <paramref name="path"/> served as <paramref name="name"/> (the name only for the browser's display): the range asked, or the whole
    /// file, or the 304, 404 or 416. <paramref name="stopping"/> is the host's stop.
    /// </summary>
    public static async Task ServeAsync(HttpContext context, string path, string name, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "private, no-cache";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers.AcceptRanges = "bytes";
        bool head = HttpMethods.IsHead(request.Method);

        FileStream stream;
        long length;
        string tag;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            WebFileResponse.Gone(response, WebUiText.VideoFileGoneDebug(name, e.Message));
            return;
        }

        await using (stream.ConfigureAwait(false))
        {
            try
            {
                length = stream.Length;
                tag = WebFileResponse.Tag(WebFileResponse.Stamp(length, path), "v");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                WebFileResponse.Gone(response, WebUiText.VideoFileGoneDebug(name, e.Message));
                return;
            }

            if (WebFileResponse.NotModified(context, tag))
            {
                return;
            }

            var range = Parse(request.Headers.Range.ToString(), request.Headers.IfRange.ToString(), tag, length);
            if (range.Kind == ByteRangeKind.Unsatisfiable)
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = "bytes */" + length.ToString(CultureInfo.InvariantCulture);
                return;
            }

            long start = 0;
            long count = length;
            response.ContentType = VideoFile.ContentType(Path.GetExtension(path));
            WebFileResponse.Inline(response, name);
            if (range.Kind == ByteRangeKind.Partial)
            {
                start = range.Start;
                count = range.Count;
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = "bytes " + range.Start.ToString(CultureInfo.InvariantCulture) + "-" + range.End.ToString(CultureInfo.InvariantCulture)
                    + "/" + length.ToString(CultureInfo.InvariantCulture);
            }

            response.ContentLength = count;
            if (head || count == 0)
            {
                return;
            }

            using var mine = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            await CopyAsync(context, stream, start, count, name, mine.Token).ConfigureAwait(false);
        }
    }

    // Plain digits only (no sign, no blanks inside), as the RFC's range numbers are.
    private static bool Digits(string text, out long value)
    {
        value = 0;
        return text.Length > 0 && text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // The slice copied in steps; the reader's going (a seek, a pause, the stop) ends it quietly.
    private static async Task CopyAsync(HttpContext context, FileStream stream, long start, long count, string name, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long left = count;
            try
            {
                stream.Position = start;
            }
            catch (IOException e)
            {
                DiagnosticLog.Debug("WebUi", WebUiText.VideoFileReadDebug(name, e.Message));
                context.Abort();
                return;
            }

            while (left > 0)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, left)), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Debug("WebUi", WebUiText.VideoFileReadDebug(name, e.Message));
                    context.Abort();
                    return;
                }

                if (read == 0)
                {
                    // The file shrank under the copy: never a short answer the browser would take as the file.
                    DiagnosticLog.Debug("WebUi", WebUiText.VideoFileReadDebug(name, "it got shorter while it was sent"));
                    context.Abort();
                    return;
                }

                try
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    return;   // the browser left the range (a seek, a pause, the tab closed) or the host stops
                }

                left -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
