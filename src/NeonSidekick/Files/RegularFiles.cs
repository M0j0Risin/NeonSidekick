namespace NeonSidekick.Files;

/// <summary>
/// Reads that open only a plain file (2026-10-22, review N1/N2): on a Mac a FIFO in the sandbox or the vault blocked <c>File.ReadAllBytes</c>
/// forever — on the turn's thread for <c>search_files</c>, <c>file_info</c> and <c>read_file</c>, inside the vault's lock for every vault tool,
/// and in the PDF layer for <c>convert_to_pdf</c> and <c>pdf_edit</c>. A Mac's read goes through <see cref="Platform.MacFiles.OpenRegular"/>
/// (<c>O_NONBLOCK | O_NOFOLLOW</c>, the type checked on the descriptor read), a link inside the sandbox read as its target; a pipe, socket or
/// device throws <see cref="NotARegularFileException"/>, an <see cref="IOException"/> every caller already answers. Windows has no such
/// files under a path (a device name is refused by <see cref="WorkingDirectory.Resolve(string, bool, out string)"/>), so it reads as before.
/// </summary>
public static class RegularFiles
{
    /// <summary>The whole of the plain file at <paramref name="full"/>.</summary>
    public static byte[] ReadAllBytes(string full)
    {
        ArgumentNullException.ThrowIfNull(full);
        if (!OperatingSystem.IsMacOS())
        {
            return File.ReadAllBytes(full);
        }

        using var stream = OpenRead(full);
        using var copy = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// The plain file at <paramref name="full"/> read whole when it holds at most <paramref name="max"/> bytes, else null — the bytes counted
    /// as they are read, not by a length looked up before (2026-10-22, review N10: a growing log passed the size check and was read whole).
    /// </summary>
    public static byte[]? ReadAtMost(string full, long max)
    {
        ArgumentNullException.ThrowIfNull(full);
        using var stream = OpenRead(full, FileShare.ReadWrite | FileShare.Delete);
        if (stream.CanSeek && stream.Length > max)
        {
            return null;
        }

        using var copy = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (copy.Length + read > max)
            {
                return null;
            }

            copy.Write(buffer, 0, read);
        }

        return copy.ToArray();
    }

    /// <summary>The plain file at <paramref name="full"/> opened for reading (<paramref name="share"/> and <paramref name="bufferSize"/> off a Mac).</summary>
    public static FileStream OpenRead(string full, FileShare share = FileShare.Read, int bufferSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(full);
        if (!OperatingSystem.IsMacOS())
        {
            return new FileStream(full, FileMode.Open, FileAccess.Read, share, bufferSize);
        }

        string target = Platform.MacFiles.KindOf(full) == Platform.MacFiles.Kind.Link && WorkingDirectory.RealLinkTarget(full) is { Length: > 0 } real ? real : full;
        return Platform.MacFiles.OpenRegular(target) ?? throw new NotARegularFileException(full);
    }
}

/// <summary>A read refused because the path is a pipe, socket or device (<see cref="RegularFiles"/>).</summary>
public sealed class NotARegularFileException(string path) : IOException($"{path} is a pipe, socket or device, not a file");
