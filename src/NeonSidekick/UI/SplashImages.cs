using System.Reflection;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.UI;

/// <summary>
/// The screen's splash seam (2026-09-19): the pictures' names in the order Left / Right walk them and
/// the loader for one of them. <see cref="SplashImages.Source"/> in the app; a test hands a list of
/// names over generated pictures; null = no splash whatever the setting says. <see cref="Directory"/>
/// (2026-09-24) is the folder the names are files in — set by <see cref="SplashImages.FromDirectory"/>
/// alone, so Delete Delete may remove a picture of the profile's own and never an embedded one.
/// </summary>
public sealed record SplashSource(IReadOnlyList<string> Names, Func<string, ImageAttachment?> Load, string? Directory = null);

/// <summary>
/// The welcome splash pictures (2026-09-18): the files under the repo's <c>assets\splash</c>,
/// embedded by the csproj as the manifest resources <c>splash/&lt;name&gt;</c>. One is drawn under
/// the banner at startup behind the General switch <c>Welcome splash</c>, filling the transcript
/// region until the first sent line (<c>ChatScreen.ShowSplash</c>); Left / Right at the empty line
/// walk the others in name order (2026-09-19, <see cref="Next"/>). Pure over the manifest: the
/// names are read once, the pick takes the screen's own <see cref="Random"/> so a test seeds it,
/// and a load goes through <see cref="ImageFile.TryLoad(byte[], string, out ImageAttachment?, out ImageLoadFailure)"/>
/// — the same caps and decode as a picture put on the line.
/// </summary>
public static class SplashImages
{
    /// <summary>What every splash resource's name starts with (the csproj's <c>LogicalName</c>).</summary>
    public const string ResourcePrefix = "splash/";

    /// <summary>
    /// The embedded pictures' names, ordinal order: every manifest resource under
    /// <see cref="ResourcePrefix"/> whose extension is one of <see cref="ImageFile.Extensions"/>
    /// (a stray file in the folder is embedded but never offered). Empty when the folder was.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = ReadNames(typeof(SplashImages).Assembly);

    /// <summary>The names under <see cref="ResourcePrefix"/> with an image extension, ordinal order; the manifest read once.</summary>
    public static IReadOnlyList<string> ReadNames(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && ImageFile.IsImagePath(n))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>One of <paramref name="names"/> at random, or null over an empty list.</summary>
    public static string? Pick(Random random, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(names);
        return names.Count == 0 ? null : names[random.Next(names.Count)];
    }

    /// <summary>
    /// The named resource as an attachment (its bytes through the image caps and, over
    /// <see cref="ImageFile.MaxSide"/>, the same re-encode as a sent picture), or null when there is
    /// no such resource or the codecs refuse it — logged at Trace, never thrown: the splash is a courtesy.
    /// </summary>
    public static ImageAttachment? Load(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        try
        {
            using var stream = typeof(SplashImages).Assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                DiagnosticLog.Trace("Splash", $"No embedded picture named {name}.");
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (ImageFile.TryLoad(buffer.ToArray(), name, out var image, out var failure))
            {
                return image;
            }

            DiagnosticLog.Trace("Splash", $"The embedded picture {name} did not load: {failure}.");
            return null;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or FileLoadException)
        {
            DiagnosticLog.Trace("Splash", $"The embedded picture {name} did not load: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The neighbour of <paramref name="current"/> <paramref name="step"/> places on in
    /// <paramref name="names"/> (−1 the previous, +1 the next), wrapping at either end; a null or
    /// unknown current is the first; null over an empty list.
    /// </summary>
    public static string? Next(IReadOnlyList<string> names, string? current, int step)
    {
        ArgumentNullException.ThrowIfNull(names);
        int count = names.Count;
        if (count == 0)
        {
            return null;
        }

        for (int at = 0; current is not null && at < count; at++)
        {
            if (string.Equals(names[at], current, StringComparison.Ordinal))
            {
                return names[((at + step) % count + count) % count];
            }
        }

        return names[0];
    }

    /// <summary>The production splash: the embedded names over <see cref="Load"/>. What <c>SidekickApp</c> hands the screen.</summary>
    public static SplashSource Source { get; } = new(Names, Load);

    /// <summary>The folder under the loaded profile's directory whose pictures replace the embedded set (later on 2026-09-19, the user's ask).</summary>
    public const string ProfileFolderName = "splash";

    /// <summary>
    /// The profile's own splash (later on 2026-09-19): the files under <paramref name="directory"/>
    /// with an image extension (<see cref="ImageFile.Extensions"/>), their names in ordinal order
    /// as the walk's order, each loaded from disk through
    /// <see cref="ImageFile.TryLoad(string, out ImageAttachment?, out ImageLoadFailure)"/> — the
    /// same caps as a picture put on the line, a refusal logged at Trace and drawn as nothing. Null
    /// when the folder is missing, unreadable or holds no image file: the embedded set then stands.
    /// Read at each show and each arrow, so a picture dropped in mid-session joins the walk. The top
    /// level only: <see cref="TrashFolderName"/> (and any other subfolder) is never looked into.
    /// </summary>
    public static SplashSource? FromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string[] names;
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            names = Directory.EnumerateFiles(directory)
                .Where(ImageFile.IsImagePath)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Trace("Splash", $"The profile's splash folder could not be read: {e.GetType().Name}: {e.Message}");
            return null;
        }

        if (names.Length == 0)
        {
            return null;
        }

        return new SplashSource(names, name =>
        {
            string path = Path.Combine(directory, name);
            if (ImageFile.TryLoad(path, out var image, out var failure))
            {
                return image;
            }

            DiagnosticLog.Trace("Splash", $"The profile's splash picture {name} did not load: {failure}.");
            return null;
        }, directory);
    }

    /// <summary><c>Splash: 3 pictures from D:\…\profiles\default\splash</c> — the Debug line when the profile's folder stands in for the embedded set. Pinned.</summary>
    public static string FolderLogLine(int count, string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return $"Splash: {count.ToString(System.Globalization.CultureInfo.InvariantCulture)} picture{(count == 1 ? "" : "s")} from {directory}";
    }

    /// <summary>
    /// The subfolder of the profile's splash folder a Delete Delete moves a picture into (2026-09-24, the
    /// user's call over a <c>File.Delete</c>: recoverable by hand, no Recycle Bin P/Invoke).
    /// <see cref="FromDirectory"/> reads the folder's top level only, so nothing in here is ever offered.
    /// </summary>
    public const string TrashFolderName = ".trash";

    /// <summary>
    /// Moves <paramref name="name"/> out of <paramref name="directory"/> into its
    /// <see cref="TrashFolderName"/> subfolder (made if missing) and returns where it landed; a name
    /// already there gets <c> (2)</c>, <c> (3)</c>… before its extension, so an earlier one is never
    /// overwritten. Throws what <see cref="File.Move(string, string)"/> throws — the caller logs it.
    /// </summary>
    public static string SetAside(string directory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrEmpty(name);
        string bin = Path.Combine(directory, TrashFolderName);
        Directory.CreateDirectory(bin);
        string target = Path.Combine(bin, name);
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(bin, $"{stem} ({n.ToString(System.Globalization.CultureInfo.InvariantCulture)}){extension}");
        }

        File.Move(Path.Combine(directory, name), target);
        return target;
    }

    /// <summary><c>Splash: moved D:\…\splash\one.png to D:\…\splash\.trash\one.png</c> — the Info line after Delete Delete set a profile's picture aside (2026-09-24). Pinned.</summary>
    public static string DeletedLogLine(string path, string movedTo)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(movedTo);
        return $"Splash: moved {path} to {movedTo}";
    }

    /// <summary><c>Splash: could not move D:\…\one.png to .trash: IOException: …</c> — the Warning line when the file would not go (2026-09-24). Pinned.</summary>
    public static string DeleteFailedLogLine(string path, Exception error)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(error);
        return $"Splash: could not move {path} to {TrashFolderName}: {error.GetType().Name}: {error.Message}";
    }
}
