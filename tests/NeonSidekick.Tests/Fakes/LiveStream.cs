using System.Threading.Channels;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A response body that never ends until the test says so (2026-10-23, the Docker log window's follows and events): chunks
/// written with <see cref="Write"/> are read as they come, <see cref="End"/> is the engine closing the stream, and a read honors
/// its token, as a socket's does. <see cref="Disposed"/> says the reader let go.
/// </summary>
public sealed class LiveStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _at;
    private volatile bool _disposed;

    /// <summary>Whether the reader disposed the stream (its follow let go).</summary>
    public bool Disposed => _disposed;

    public void Write(byte[] chunk) => _chunks.Writer.TryWrite(chunk);

    public void Write(string text) => Write(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>The engine's close: the reader's next read past what was written gives 0.</summary>
    public void End() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_at >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_chunks.Reader.TryRead(out var chunk))
            {
                _current = chunk;
                _at = 0;
            }
        }

        int n = Math.Min(buffer.Length, _current.Length - _at);
        _current.AsMemory(_at, n).CopyTo(buffer);
        _at += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _chunks.Writer.TryComplete();
        base.Dispose(disposing);
    }
}
