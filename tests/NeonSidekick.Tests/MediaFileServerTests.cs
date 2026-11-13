using Microsoft.AspNetCore.Http;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The video files' server (2026-11-12, the video-files plan): the <c>Range</c> header read (one range, a suffix, Safari's probe, past the
/// end, several, a malformed one, an <c>If-Range</c> that isn't the ETag), and the answers over a real file: the slice and its headers, the
/// whole file, the 416, the 304, a HEAD with no body, and a file gone. Pure over <see cref="DefaultHttpContext"/>.
/// </summary>
public sealed class MediaFileServerTests : IDisposable
{
    private const string Tag = "\"f64-1-v\"";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "neon-media-" + Guid.NewGuid().ToString("N"));

    public MediaFileServerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // a reader still closing: the temp folder's own cleanup takes it
        }
    }

    [Theory]
    [InlineData(null, null, ByteRangeKind.Whole, 0, 0)]
    [InlineData("bytes=0-1", null, ByteRangeKind.Partial, 0, 1)]   // Safari's probe
    [InlineData("bytes=10-19", null, ByteRangeKind.Partial, 10, 19)]
    [InlineData("bytes=10-", null, ByteRangeKind.Partial, 10, 99)]
    [InlineData("bytes=90-500", null, ByteRangeKind.Partial, 90, 99)]   // held to the end
    [InlineData("bytes=-30", null, ByteRangeKind.Partial, 70, 99)]
    [InlineData("bytes=-500", null, ByteRangeKind.Partial, 0, 99)]
    [InlineData("BYTES=5-6", null, ByteRangeKind.Partial, 5, 6)]
    [InlineData("bytes=100-", null, ByteRangeKind.Unsatisfiable, 0, 0)]
    [InlineData("bytes=-0", null, ByteRangeKind.Unsatisfiable, 0, 0)]
    [InlineData("bytes=0-1,5-6", null, ByteRangeKind.Whole, 0, 0)]   // several: the whole file
    [InlineData("bytes=9-3", null, ByteRangeKind.Whole, 0, 0)]
    [InlineData("bytes=a-3", null, ByteRangeKind.Whole, 0, 0)]
    [InlineData("bytes=+1-3", null, ByteRangeKind.Whole, 0, 0)]
    [InlineData("items=0-3", null, ByteRangeKind.Whole, 0, 0)]
    [InlineData("bytes=10-19", Tag, ByteRangeKind.Partial, 10, 19)]
    [InlineData("bytes=10-19", "\"f64-2-v\"", ByteRangeKind.Whole, 0, 0)]   // the file changed since: whole
    [InlineData("bytes=10-19", "Wed, 21 Oct 2015 07:28:00 GMT", ByteRangeKind.Whole, 0, 0)]
    public void ARange_IsRead_AgainstTheLength(string? range, string? ifRange, ByteRangeKind kind, long start, long end)
    {
        var read = MediaFileServer.Parse(range, ifRange, Tag, 100);

        Assert.Equal(kind, read.Kind);
        if (kind == ByteRangeKind.Partial)
        {
            Assert.Equal((start, end), (read.Start, read.End));
        }
    }

    [Fact]
    public void AnEmptyFile_HasNoRange()
    {
        Assert.Equal(ByteRangeKind.Unsatisfiable, MediaFileServer.Parse("bytes=0-", null, Tag, 0).Kind);
        Assert.Equal(ByteRangeKind.Unsatisfiable, MediaFileServer.Parse("bytes=-5", null, Tag, 0).Kind);
        Assert.Equal(ByteRangeKind.Whole, MediaFileServer.Parse(null, null, Tag, 0).Kind);
    }

    private string Clip(string name = "clip.mp4", int length = 100)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Enumerable.Range(0, length).Select(i => (byte)i).ToArray());
        return path;
    }

    private static DefaultHttpContext Context(string method = "GET", string? range = null, string? ifRange = null, string? ifNoneMatch = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (range is not null)
        {
            context.Request.Headers.Range = range;
        }

        if (ifRange is not null)
        {
            context.Request.Headers.IfRange = ifRange;
        }

        if (ifNoneMatch is not null)
        {
            context.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        context.Response.Body = new MemoryStream();
        return context;
    }

    private static byte[] Body(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();

    [Fact]
    public async Task ARange_IsServedAsA206_WithItsHeaders()
    {
        string clip = Clip();
        var context = Context(range: "bytes=10-19");

        await MediaFileServer.ServeAsync(context, clip, "my clip?.mp4", CancellationToken.None);

        var response = context.Response;
        Assert.Equal(StatusCodes.Status206PartialContent, response.StatusCode);
        Assert.Equal("bytes 10-19/100", response.Headers.ContentRange.ToString());
        Assert.Equal(10, response.ContentLength);
        Assert.Equal(Enumerable.Range(10, 10).Select(i => (byte)i), Body(context));
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
        Assert.Equal("video/mp4", response.ContentType);
        Assert.Equal("same-origin", response.Headers["Cross-Origin-Resource-Policy"].ToString());
        Assert.Equal("private, no-cache", response.Headers.CacheControl.ToString());
        Assert.Equal("inline; filename*=UTF-8''my%20clip%3F.mp4", response.Headers.ContentDisposition.ToString());
        Assert.StartsWith("\"f64-", response.Headers.ETag.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRange_OrAChangedFile_GetsTheWholeFile()
    {
        string clip = Clip("clip.webm");
        var plain = Context();
        await MediaFileServer.ServeAsync(plain, clip, "clip.webm", CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, plain.Response.StatusCode);
        Assert.Equal(100, Body(plain).Length);
        Assert.Equal("video/webm", plain.Response.ContentType);

        var changed = Context(range: "bytes=10-19", ifRange: "\"f64-0-v\"");
        await MediaFileServer.ServeAsync(changed, clip, "clip.webm", CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, changed.Response.StatusCode);
        Assert.Equal(100, Body(changed).Length);

        var same = Context(range: "bytes=10-19", ifRange: plain.Response.Headers.ETag.ToString());
        await MediaFileServer.ServeAsync(same, clip, "clip.webm", CancellationToken.None);
        Assert.Equal(StatusCodes.Status206PartialContent, same.Response.StatusCode);
    }

    [Fact]
    public async Task PastTheEnd_IsA416_AMatchingTag_A304_AndAHead_HasNoBody()
    {
        string clip = Clip();
        var past = Context(range: "bytes=500-");
        await MediaFileServer.ServeAsync(past, clip, "clip.mp4", CancellationToken.None);
        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, past.Response.StatusCode);
        Assert.Equal("bytes */100", past.Response.Headers.ContentRange.ToString());
        Assert.Empty(Body(past));

        var again = Context(ifNoneMatch: past.Response.Headers.ETag.ToString());
        await MediaFileServer.ServeAsync(again, clip, "clip.mp4", CancellationToken.None);
        Assert.Equal(StatusCodes.Status304NotModified, again.Response.StatusCode);
        Assert.Empty(Body(again));

        var head = Context("HEAD", range: "bytes=0-1");
        await MediaFileServer.ServeAsync(head, clip, "clip.mp4", CancellationToken.None);
        Assert.Equal(StatusCodes.Status206PartialContent, head.Response.StatusCode);
        Assert.Equal(2, head.Response.ContentLength);
        Assert.Empty(Body(head));
    }

    [Fact]
    public async Task AFileGone_IsA404_WithNoTagOrName()
    {
        var context = Context(range: "bytes=0-1");

        await MediaFileServer.ServeAsync(context, Path.Combine(_dir, "gone.mp4"), "gone.mp4", CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Empty(context.Response.Headers.ETag.ToString());
        Assert.Empty(context.Response.Headers.ContentDisposition.ToString());
        Assert.Empty(Body(context));
    }

    [Fact]
    public async Task AFileAWriterHolds_IsStillRead()
    {
        string clip = Clip();
        using var writer = new FileStream(clip, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var context = Context(range: "bytes=0-9");

        await MediaFileServer.ServeAsync(context, clip, "clip.mp4", CancellationToken.None);

        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal(10, Body(context).Length);
    }

    [Fact]
    public async Task AFileThatGrew_MissesItsIfRange_AndIsSentWhole()
    {
        string clip = Clip();
        var first = Context(range: "bytes=0-9");
        await MediaFileServer.ServeAsync(first, clip, "clip.mp4", CancellationToken.None);
        string tag = first.Response.Headers.ETag.ToString();

        await using (var grow = new FileStream(clip, FileMode.Append, FileAccess.Write))
        {
            grow.Write(new byte[50]);
        }

        File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(1));
        var later = Context(range: "bytes=10-19", ifRange: tag);
        await MediaFileServer.ServeAsync(later, clip, "clip.mp4", CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, later.Response.StatusCode);
        Assert.Equal(150, Body(later).Length);
        Assert.NotEqual(tag, later.Response.Headers.ETag.ToString());
    }

    // A response stream whose connection was reset mid-copy.
    private sealed class ResetStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The connection was reset."));
    }

    [Fact]
    public async Task AResetMidCopy_AndAStop_EndQuietly_WithNoErrorLine()
    {
        string clip = Clip(length: MediaFileServer.BufferSize * 3);
        var errors = new List<string>();
        void Record(Diagnostics.DiagnosticEvent e)
        {
            if (e.Level >= Diagnostics.DiagnosticLevel.Warning)
            {
                lock (errors)
                {
                    errors.Add(e.Message);
                }
            }
        }

        Diagnostics.DiagnosticLog.Emitted += Record;
        try
        {
            var reset = Context();
            reset.Response.Body = new ResetStream();
            await MediaFileServer.ServeAsync(reset, clip, "clip.mp4", CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, reset.Response.StatusCode);

            var stopped = Context();
            using var stop = new CancellationTokenSource();
            await stop.CancelAsync();
            await MediaFileServer.ServeAsync(stopped, clip, "clip.mp4", stop.Token);
            Assert.True(Body(stopped).Length < MediaFileServer.BufferSize * 3);
        }
        finally
        {
            Diagnostics.DiagnosticLog.Emitted -= Record;
        }

        Assert.Empty(errors);
    }
}
