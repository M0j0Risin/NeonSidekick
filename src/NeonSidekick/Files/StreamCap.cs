namespace NeonSidekick.Files;

/// <summary>
/// A stream read under a byte cap (2026-10-27, the review): Docker's logs and pulls and Home Assistant's camera stills read their
/// bodies through it, so a server that sends more than asked for, or trickles it, never grows the buffer past the cap. Moved here
/// from <c>Docker.DockerLogStream</c>, where the Home Assistant client had borrowed it.
/// </summary>
public static class StreamCap
{
    /// <summary>Reads at most <paramref name="maxBytes"/> of <paramref name="stream"/>: the bytes, and whether more were left.</summary>
    public static async Task<(byte[] Data, bool Capped)> ReadAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
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
}
