using System.Diagnostics;

namespace NeonSidekick.Tests;

/// <summary>
/// A directory junction for the link tests (2026-10-03): unlike a symbolic link it needs no privilege, so these tests run on
/// every Windows machine. .NET has no call for one; cmd's <c>mklink /J</c> makes it.
/// </summary>
internal static class Junction
{
    public static void Make(string link, string target)
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string error = child.StandardError.ReadToEnd();
        child.StandardOutput.ReadToEnd();
        child.WaitForExit();
        Assert.True(child.ExitCode == 0, $"mklink /J failed ({child.ExitCode}): {error}");
    }

    /// <summary>Takes every junction and symlink under <paramref name="folder"/> away, never what they lead to, then the folder: a recursive delete is refused on a junction.</summary>
    public static void DeleteTree(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).OrderByDescending(e => e.Length))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry); else File.Delete(entry);
            }
        }

        Directory.Delete(folder, recursive: true);
    }
}

/// <summary>
/// Whether this process may make a symbolic link (2026-10-03, the link tests): Windows allows it only with Developer Mode on
/// or as an administrator, so one is tried once per assembly in the temp folder and taken away again.
/// </summary>
internal static class Symlinks
{
    public static readonly string Unavailable;

    static Symlinks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "neon-symlink-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateSymbolicLink(Path.Combine(dir, "link"), dir);
            Unavailable = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Unavailable = "This process may not make a symbolic link (Developer Mode off, not elevated): " + ex.Message;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>Skips unless the process may make a symbolic link. A local check, never a CI safety net.</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (Symlinks.Unavailable.Length > 0)
        {
            Skip = Symlinks.Unavailable;
        }
    }
}
