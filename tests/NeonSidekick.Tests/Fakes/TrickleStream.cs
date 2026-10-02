using System.Net;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A response body that is not seekable, so it carries no <c>Content-Length</c> (2026-10-01, <c>download_file</c> streamed to
/// disk): <paramref name="body"/> in reads of at most 64 KB, <paramref name="firstRead"/> set once the first is handed over, then
/// the end — or, under <paramref name="hang"/>, a read that waits until its token is cancelled, a server gone quiet.
/// </summary>
public sealed class TrickleStream(byte[] body, bool hang = false, TaskCompletionSource? firstRead = null) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>An OK answer of <paramref name="mediaType"/> over a stream, no length declared.</summary>
    public static HttpResponseMessage Response(Stream body, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return response;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < body.Length)
        {
            int count = Math.Min(buffer.Length, Math.Min(64 * 1024, body.Length - _position));
            body.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            firstRead?.TrySetResult();
            return count;
        }

        if (hang)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
