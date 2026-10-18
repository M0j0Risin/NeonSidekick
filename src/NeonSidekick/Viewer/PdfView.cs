using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Viewer;

/// <summary>
/// A local PDF shown in the Browser window (2026-10-14, the PDF plan's phase 4a): WebView2 has its own PDF viewer, but the window refuses
/// <c>file:</c> addresses (its address box and its navigation rule, <see cref="BrowserPage.Allows"/>). So <c>/view a.pdf</c> copies the file
/// into a folder of this process's own (<see cref="Folder"/>, <c>%TEMP%\NeonSidekick\pdfview\&lt;pid&gt;</c>), under a random name, and the
/// window serves that folder on a virtual host (<see cref="HostName"/>, <c>SetVirtualHostNameToFolderMapping</c>, as the video window serves
/// its player), so the viewer opens <c>https://files.neonsidekick.example/&lt;16 hex&gt;/report.pdf</c>. Only the copy is reachable, never
/// the PDF's own folder. The copies go when the window closes (<see cref="Clear"/>); a folder left by a copy of the app that is gone is
/// swept at the next stage (<see cref="Sweep"/>), never one whose process still runs (a second instance's window may be showing it, gap 63).
/// The PDF's host is no web page: the window keeps it out of its history, its visited hosts and its saved links, and its open button opens
/// the original file (<see cref="OriginalOf"/>). Pure apart from the copy, the delete and the process look-up.
/// </summary>
public static class PdfView
{
    /// <summary>The virtual host the staged PDFs are served on (<c>.example</c>: never a real name, as the video page's).</summary>
    public const string HostName = "files.neonsidekick.example";

    /// <summary>Where every process's staging folder lives.</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "NeonSidekick", "pdfview");

    /// <summary>The staging folder of process <paramref name="pid"/>.</summary>
    public static string StageFolder(string root, int pid) => Path.Combine(root, pid.ToString(CultureInfo.InvariantCulture));

    /// <summary>This process's staging folder, the one the window maps.</summary>
    public static string Folder => StageFolder(Root, Environment.ProcessId);

    /// <summary>The original file behind each staged copy, by its random folder's name, for the window's open button.</summary>
    private static readonly ConcurrentDictionary<string, string> s_originals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each staged copy's own path, by its random folder's name (2026-10-18, the Mac Browser plan): WebKit cannot serve a folder on a host,
    /// so the Mac's window loads the copy as a file (<see cref="StagedFileOf"/>).
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> s_staged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="full"/> copied into <paramref name="folder"/> (default <see cref="Folder"/>) under a new random folder, and the address
    /// the window opens it at, its name escaped. Sweeps the folders of processes that are gone first. Throws as <see cref="File.Copy(string, string)"/> does.
    /// </summary>
    public static Uri Stage(string full, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(full);
        folder ??= Folder;
        Sweep(Path.GetDirectoryName(folder) ?? Root, Environment.ProcessId);
        string random = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        string name = Path.GetFileName(full);
        string target = Path.Combine(folder, random);
        Directory.CreateDirectory(target);
        File.Copy(full, Path.Combine(target, name), overwrite: true);
        s_originals[random] = full;
        s_staged[random] = Path.Combine(target, name);
        return UrlOf(random, name);
    }

    /// <summary>The address of a staged copy: the host, the random folder, the name escaped (spaces, <c>#</c>, <c>%</c>). Pure.</summary>
    public static Uri UrlOf(string random, string name) => new("https://" + HostName + "/" + random + "/" + Uri.EscapeDataString(name));

    /// <summary>Whether <paramref name="url"/> is a staged PDF's address (this host, https). Pure.</summary>
    public static bool IsOurs(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps
        && string.Equals(parsed.Host, HostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The original file a staged address stands for; null for another address or a copy this process did not make.</summary>
    public static string? OriginalOf(string? url)
    {
        if (!IsOurs(url))
        {
            return null;
        }

        string first = new Uri(url!).Segments.Skip(1).FirstOrDefault()?.Trim('/') ?? "";
        return s_originals.TryGetValue(first, out var original) ? original : null;
    }

    /// <summary>
    /// The staged copy a staged address stands for (2026-10-18, the Mac's Browser window, which loads it as a file); null for another
    /// address, a copy this process did not make, or one already cleared.
    /// </summary>
    public static string? StagedFileOf(string? url)
    {
        if (!IsOurs(url))
        {
            return null;
        }

        string first = new Uri(url!).Segments.Skip(1).FirstOrDefault()?.Trim('/') ?? "";
        return s_staged.TryGetValue(first, out var staged) && File.Exists(staged) ? staged : null;
    }

    /// <summary>Every copy in <paramref name="folder"/> (default <see cref="Folder"/>) deleted; the window closed. Never throws.</summary>
    public static void Clear(string? folder = null)
    {
        folder ??= Folder;
        s_originals.Clear();
        s_staged.Clear();
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug("Viewer", $"The staged PDFs in {folder} stay for now: {ex.Message}");
        }
    }

    /// <summary>
    /// The staging folders under <paramref name="root"/> whose process is gone deleted (a crash, an exit before the window closed), never
    /// <paramref name="self"/>'s nor any whose process still runs. <paramref name="alive"/> is the look-up (the process table in the app). How
    /// many went. Never throws.
    /// </summary>
    public static int Sweep(string root, int self, Func<int, bool>? alive = null)
    {
        alive ??= IsRunning;
        int gone = 0;
        try
        {
            if (!Directory.Exists(root))
            {
                return 0;
            }

            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                if (!int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid == self || alive(pid))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(folder, recursive: true);
                    gone++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Debug("Viewer", $"A staged PDF folder stays: {folder} ({ex.Message})");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug("Viewer", $"The staged PDF folders could not be swept: {ex.Message}");
        }

        return gone;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
