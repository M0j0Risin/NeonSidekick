namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A file that can't be read for now, a scanner's hold (2026-11-04, self-update's review P8-8). On Windows a handle that shares nothing. On a Mac a read with <c>FileShare.ReadWrite</c> takes no lock, so no handle can stop it, and the file's mode does. A delete still works there, so a test about a file that won't delete stays Windows only.
/// </summary>
public sealed class FileHold : IDisposable
{
    private readonly string _path;
    private readonly FileStream? _handle;
    private readonly UnixFileMode _mode;
    private bool _gone;

    private FileHold(string path)
    {
        _path = path;
        if (OperatingSystem.IsWindows())
        {
            _handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        else
        {
            _mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, UnixFileMode.None);
        }
    }

    public static FileHold On(string path) => new(path);

    public void Dispose()
    {
        if (_gone)
        {
            return;
        }

        _gone = true;
        if (_handle is not null)
        {
            _handle.Dispose();
        }
        else if (File.Exists(_path) && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, _mode);
        }
    }
}
