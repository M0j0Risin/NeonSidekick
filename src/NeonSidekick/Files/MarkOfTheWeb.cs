using NeonSidekick.Diagnostics;

namespace NeonSidekick.Files;

/// <summary>
/// The "came from the internet" mark carried from an archive to what is unzipped from it (2026-10-22, review O5): Windows' Mark of the
/// Web (the <c>Zone.Identifier</c> stream SmartScreen, Office's Protected View and the shell read) and a Mac's <c>com.apple.quarantine</c>
/// (Gatekeeper's), as Explorer and Archive Utility carry them. Unzip wrote neither, so a downloaded archive's program or document opened
/// from its folder with no prompt. An archive with no mark gives none. The mark is the app's write, straight through the file API, never
/// <see cref="WorkingDirectory.Resolve"/> (which refuses a stream name). Best effort: a volume with no streams (FAT, a share) or no extended
/// attributes keeps the files unmarked, said in the log.
/// </summary>
public static class MarkOfTheWeb
{
    /// <summary>The stream that carries the mark on Windows.</summary>
    public const string ZoneStream = ":Zone.Identifier";

    /// <summary>The extended attribute that carries it on a Mac.</summary>
    public const string QuarantineAttribute = "com.apple.quarantine";

    /// <summary>The mark on <paramref name="source"/>, or null for none (or on a system that has none).</summary>
    public static byte[]? Read(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string stream = source + ZoneStream;
                return File.Exists(stream) ? File.ReadAllBytes(stream) : null;
            }

            return OperatingSystem.IsMacOS() ? MacQuarantine.Read(source, QuarantineAttribute) : null;
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>The app that marks what it downloads, as the Mac's quarantine names its agent.</summary>
    public const string Agent = "NeonSidekick";

    /// <summary>
    /// Whether <paramref name="path"/> came from the internet by its mark (2026-11-15, the web-tools plan's §3: <c>read_pdf</c> and
    /// <c>read_office</c> fence a downloaded document's text): <see cref="IsInternetZone"/> on Windows, <see cref="IsInternetQuarantine"/> on a
    /// Mac. No mark, or one that won't read (a volume with no streams), is the user's own file. Read on every call.
    /// </summary>
    public static bool IsFromInternet(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (ReadCapped(path) is not { } mark)
        {
            return false;
        }

        string text = Decode(mark);
        return OperatingSystem.IsWindows() ? IsInternetZone(text) : IsInternetQuarantine(text);
    }

    /// <summary>
    /// Whether the file <paramref name="path"/> names in <paramref name="files"/> came from the internet (2026-11-16, the web-tools review's #58:
    /// moved here from <c>ReadPdfTool.Marked</c>): false when it doesn't resolve.
    /// </summary>
    public static bool IsFromInternet(WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && IsFromInternet(full);
    }

    /// <summary>The most of a mark <see cref="IsFromInternet"/> reads (2026-11-16, the web-tools review's #50): a zone or a quarantine value is a few lines.</summary>
    public const int MaxVerdictBytes = 4096;

    /// <summary>
    /// The first <see cref="MaxVerdictBytes"/> of the mark, for a verdict only (2026-11-16, the web-tools review's #50: a stream of any size was read
    /// whole on every read_pdf call). <see cref="Read"/> stays whole: it is the copy path, and a cut mark written onto another file is a broken one.
    /// </summary>
    private static byte[]? ReadCapped(string source)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string stream = source + ZoneStream;
                if (!File.Exists(stream))
                {
                    return null;
                }

                using var read = new FileStream(stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[MaxVerdictBytes];
                int total = 0;
                for (int got; total < buffer.Length && (got = read.Read(buffer, total, buffer.Length - total)) > 0;)
                {
                    total += got;
                }

                return buffer[..total];
            }

            return OperatingSystem.IsMacOS() && MacQuarantine.Read(source, QuarantineAttribute) is { } value ? value[..Math.Min(value.Length, MaxVerdictBytes)] : null;
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// A mark's bytes as text (2026-11-16, the web-tools review's #129): UTF-16 LE when it starts with that byte-order mark (some tools write
    /// <c>Zone.Identifier</c> so), else UTF-8, a UTF-8 mark dropped. Pure.
    /// </summary>
    public static string Decode(byte[] mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (mark.Length >= 2 && mark[0] == 0xFF && mark[1] == 0xFE)
        {
            return System.Text.Encoding.Unicode.GetString(mark, 2, (mark.Length - 2) & ~1);
        }

        int skip = mark.Length >= 3 && mark[0] == 0xEF && mark[1] == 0xBB && mark[2] == 0xBF ? 3 : 0;
        return System.Text.Encoding.UTF8.GetString(mark, skip, mark.Length - skip);
    }

    /// <summary>
    /// A <c>Zone.Identifier</c>'s verdict (2026-11-15): its <c>[ZoneTransfer]</c> section's <c>ZoneId</c> is 3 (Internet) or 4 (Restricted). A
    /// mark with no readable zone counts as from the internet, since someone wrote it; zones 0–2 (this computer, the intranet, trusted) don't. Pure.
    /// </summary>
    public static bool IsInternetZone(string mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        bool inSection = false;
        foreach (string raw in mark.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.StartsWith('['))
            {
                inSection = line.Equals("[ZoneTransfer]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection && line.StartsWith("ZoneId", StringComparison.OrdinalIgnoreCase) && line.IndexOf('=') is var equals and > 0
                && line[..equals].Trim().Equals("ZoneId", StringComparison.OrdinalIgnoreCase))
            {
                return !int.TryParse(line[(equals + 1)..].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int zone)
                    || zone >= 3;
            }
        }

        return true;
    }

    /// <summary>
    /// A <c>com.apple.quarantine</c> value's verdict (2026-11-15): <c>flags;time;agent;event</c>, from the internet when its fourth field is an
    /// event UUID (what a browser or Mail records with the origin) or its agent is <see cref="Agent"/> (the app's own downloads). Sandboxed
    /// apps (Preview, TextEdit) write a short value with neither onto documents they only open or save, so those stay the user's own. Pure.
    /// </summary>
    public static bool IsInternetQuarantine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] fields = value.Trim('\0', ' ', '\n', '\r').Split(';');
        if (fields.Length >= 3 && fields[2].Equals(Agent, StringComparison.Ordinal))
        {
            return true;
        }

        return fields.Length >= 4 && Guid.TryParse(fields[3].Trim(), out _);
    }

    /// <summary>
    /// The mark for a file the app downloaded from <paramref name="url"/> at <paramref name="now"/> (2026-10-23, the Mac pass, the user's
    /// call: <c>download_file</c> wrote none, so what the model fetched and unzipped opened as the user's own): Windows' <c>Zone.Identifier</c>
    /// for the Internet zone with the address (any user name and password taken out), as Edge writes it; a Mac's quarantine as a browser
    /// writes it (<c>0081</c>, a web download, the time in hex seconds, the agent). Null elsewhere.
    /// </summary>
    public static byte[]? ForDownload(string url, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (OperatingSystem.IsWindows())
        {
            string host = SafeText.WithoutUserInfo(url).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
            return System.Text.Encoding.UTF8.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + host + "\r\n");
        }

        return OperatingSystem.IsMacOS()
            ? System.Text.Encoding.ASCII.GetBytes("0081;" + now.ToUnixTimeSeconds().ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ";" + Agent + ";")
            : null;
    }

    /// <summary>Puts <paramref name="mark"/> on <paramref name="target"/>; false when the volume took none (no streams, no extended attributes).</summary>
    public static bool Put(byte[] mark, string target)
    {
        ArgumentNullException.ThrowIfNull(mark);
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(target + ZoneStream, mark);
                return true;
            }

            return !OperatingSystem.IsMacOS() || MacQuarantine.Write(target, QuarantineAttribute, mark);
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Takes the mark off <paramref name="target"/> (2026-10-28, the checkpoints plan: a file restored to a state that had no mark loses the
    /// one a <c>download_file</c> over it left); true when none is left. Best effort, logged like <see cref="Put"/>.
    /// </summary>
    public static bool Remove(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.Delete(target + ZoneStream);
                return true;
            }

            return !OperatingSystem.IsMacOS() || MacQuarantine.Remove(target, QuarantineAttribute);
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            DiagnosticLog.Debug("Files", $"Could not take the internet mark off {target}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Puts <paramref name="mark"/> on each of <paramref name="targets"/>; a target that takes none is counted in the log.</summary>
    public static void Carry(byte[]? mark, IEnumerable<string> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (mark is null)
        {
            return;
        }

        int failed = targets.Count(target => !Put(mark, target));

        if (failed > 0)
        {
            DiagnosticLog.Warn("Files", $"{failed.ToString(System.Globalization.CultureInfo.InvariantCulture)} unzipped files took no internet mark (the volume keeps none)");
        }
    }
}
