using NeonSidekick.Shortcuts;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>shortcut:shelllink</c> (2026-10-07, <c>/shortcut</c>): the shell's <c>ShellLink</c> made, set and saved through
    /// <see cref="WindowsShortcutWriter"/> in the published binary — a shortcut to this exe with a log in its command line, written to
    /// the temp folder (never the desktop), loaded back and its target, arguments and Start-in compared, then deleted. Skipped off Windows.
    /// </summary>
    public static SmokeCheck ProbeShortcut()
    {
        const string name = "shortcut:shelllink";
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
}
