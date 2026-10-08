using NeonSidekick.Shortcuts;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>shortcut:shelllink</c> (2026-10-07, <c>/shortcut</c>): the shell's <c>ShellLink</c> made, set and saved through
    /// <see cref="WindowsShortcutWriter"/> in the published binary — a shortcut to this exe with a log in its command line, written to
    /// the temp folder (never the desktop), loaded back and its target, arguments and Start-in compared, then deleted. On a Mac
    /// (2026-10-08) <c>shortcut:command</c> instead; skipped elsewhere.
    /// </summary>
    public static SmokeCheck ProbeShortcut()
    {
        const string name = "shortcut:shelllink";
        if (OperatingSystem.IsMacOS())
        {
            return ProbeShortcutCommand();
        }

        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        string folder = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var writer = new WindowsShortcutWriter();
            string exe = writer.Executable ?? throw new InvalidOperationException(ShortcutText.NoOwnExecutable);
            var spec = DesktopShortcut.For(exe, folder, "smoke", log: true);
            writer.Write(spec);
            var (target, arguments, start) = WindowsShortcutWriter.Read(spec.LinkPath);
            bool same = string.Equals(target, spec.Target, StringComparison.OrdinalIgnoreCase)
                && string.Equals(arguments, spec.Arguments, StringComparison.Ordinal)
                && string.Equals(start, spec.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
            return new SmokeCheck(name, same, same
                ? "ole32 ShellLink wrote and read back a shortcut: " + arguments
                : $"read back \"{target}\" {arguments} in {start}, not \"{spec.Target}\" {spec.Arguments} in {spec.WorkingDirectory}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }

    /// <summary>
    /// <c>shortcut:command</c> (2026-10-08, <c>/shortcut</c> on a Mac): <see cref="MacShortcutWriter"/> in the published binary — a
    /// <c>.command</c> file to this exe with a log in its command line, written to the temp folder (never the desktop) for Terminal,
    /// read back (its text <see cref="DesktopShortcut.CommandScript"/>'s, mode 0755), the embedded icon decoded by ImageIO and set,
    /// and Finder's opener for that file Terminal within five seconds (the binding settles asynchronously), then deleted.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static SmokeCheck ProbeShortcutCommand()
    {
        const string name = "shortcut:command";
        string folder = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var writer = new MacShortcutWriter();
            string exe = writer.Executable ?? throw new InvalidOperationException(ShortcutText.NoOwnExecutable);
            var spec = DesktopShortcut.For(exe, folder, "smoke", log: true, ShortcutKind.Command);
            var terminal = new Viewer.TerminalApp(null, Viewer.TerminalPick.TerminalBundle);
            var (iconSet, bound) = MacShortcutWriter.WriteFile(spec, terminal);
            const UnixFileMode Executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (File.ReadAllText(spec.LinkPath) != DesktopShortcut.CommandScript(spec)
                || (File.GetUnixFileMode(spec.LinkPath) & Executable) != Executable)
            {
                return new SmokeCheck(name, false, "the .command file's text or mode is not what was written");
            }

            if (!iconSet || bound is null)
            {
                return new SmokeCheck(name, false, iconSet ? "Terminal was not found to bind the file to" : "the app's icon was not set (app.ico not read by ImageIO?)");
            }

            string? opener = null;
            for (int i = 0; i < 50 && !string.Equals(opener = MacShortcutWriter.OpenerOf(spec.LinkPath), bound, StringComparison.Ordinal); i++)
            {
                Thread.Sleep(100);
            }

            bool same = string.Equals(opener, bound, StringComparison.Ordinal);
            return new SmokeCheck(name, same, same
                ? "NSWorkspace set the icon and bound a .command file to " + Path.GetFileName(bound) + ": " + spec.Arguments
                : $"Finder opens it with {opener ?? "nothing"}, not {bound}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }
}
