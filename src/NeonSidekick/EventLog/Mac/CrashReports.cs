using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeonSidekick.EventLog;

/// <summary>
/// One crash or diagnostic report as a listing names it (2026-10-18, the Mac event-log plan's phase 3): its file name (the handle, unique
/// in its folder), where it is, when it was written (the report's own time, else the file's), its kind (<c>crash</c>, <c>jetsam</c>,
/// <c>resource</c>…), the process it is about, and whose folder holds it.
/// </summary>
public sealed record CrashReportInfo(string Name, string FullPath, DateTimeOffset Time, string Kind, string Process, string Owner);

/// <summary>What one report says: a line for a listing, and the report in full as the answer shows it (both redacted when asked).</summary>
public sealed record CrashDetail(string Summary, string Text, int? Pid);

/// <summary>
/// The crash and diagnostic reports a Mac writes (2026-10-18, the Mac event-log plan's phase 3), read as files with no new process and no
/// sudo: <c>.ips</c> reports are a header JSON line then a body JSON object (a crash's exception, termination and threads, a jetsam's
/// processes), <c>.diag</c>/<c>.spin</c>/<c>.hang</c> are text with <c>Key: value</c> headings, a <c>.panic</c> a header and text. A listing
/// reads only each file's first 64 KB; a report in full reads at most <see cref="MaxBytes"/>. The faulting thread shows its top
/// <see cref="Frames"/> frames. Names are refused unless they are a plain file name with a report's extension, so a handle can never reach
/// outside the two folders. Pure but for the file reads.
/// </summary>
public static partial class CrashReports
{
    /// <summary>The extensions a report has.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".ips", ".diag", ".panic", ".spin", ".hang", ".crash"];

    /// <summary>The most of one report read in full (Phase 3: real reports are 15 to 500 KB).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>How much of a report a listing reads.</summary>
    public const int HeaderBytes = 64 * 1024;

    /// <summary>The faulting thread's frames shown.</summary>
    public const int Frames = 30;

    /// <summary>The most text lines of a text report shown.</summary>
    public const int TextLines = 160;

    /// <summary>The folder of older reports inside each.</summary>
    public const string Retired = "Retired";

    /// <summary>Whether <paramref name="name"/> is a report's plain file name: no folder, no dot first, a report's extension.</summary>
    public static bool IsReportName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 255 && name[0] != '.' && name.IndexOfAny(['/', '\\', '\0']) < 0 && name != ".." &&
        Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The reports in <paramref name="folders"/> (each with its <see cref="Retired"/> folder) written since <paramref name="since"/>, newest
    /// first; a folder that cannot be read is named with its refusal instead (the system's needs the <c>_analyticsusers</c> group).
    /// </summary>
    public static (List<CrashReportInfo> Reports, List<(MacCrashFolder Folder, string? Refused)> Folders) List(
        IReadOnlyList<MacCrashFolder> folders, DateTimeOffset since, DateTimeOffset? until, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var reports = new List<CrashReportInfo>();
        var states = new List<(MacCrashFolder, string?)>();
        foreach (var folder in folders)
        {
            string? refused = null;
            foreach (string path in new[] { folder.Path, Path.Combine(folder.Path, Retired) })
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        continue;
                    }

                    foreach (var file in new DirectoryInfo(path).EnumerateFiles())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsReportName(file.Name) || file.LastWriteTimeUtc < since.UtcDateTime)
                        {
                            continue;   // a report is written once, at its time or just after: an older file holds an older report
                        }

                        var info = Info(file.FullName, file.Name, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), folder.Owner);
                        if (info.Time >= since && (until is not { } end || info.Time <= end))
                        {
                            reports.Add(info);
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    refused ??= path == folder.Path ? MacLogText.FolderRefused : null;
                }
            }

            states.Add((folder, refused));
        }

        reports.Sort(Newest);
        return (reports, states);
    }

    /// <summary>Newest first, then by name, so a page's cursor is stable.</summary>
    public static int Newest(CrashReportInfo a, CrashReportInfo b)
    {
        int byTime = b.Time.CompareTo(a.Time);
        return byTime != 0 ? byTime : string.CompareOrdinal(a.Name, b.Name);
    }

    /// <summary>The report named <paramref name="name"/> in one of <paramref name="folders"/> (or its Retired folder), or null.</summary>
    public static CrashReportInfo? Find(IReadOnlyList<MacCrashFolder> folders, string name)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (!IsReportName(name))
        {
            return null;
        }

        foreach (var folder in folders)
        {
            foreach (string path in new[] { Path.Combine(folder.Path, name), Path.Combine(folder.Path, Retired, name) })
            {
                var file = new FileInfo(path);
                if (file.Exists)
                {
                    return Info(file.FullName, file.Name, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), folder.Owner);
                }
            }
        }

        return null;
    }

    /// <summary>A report's listing facts from its first <see cref="HeaderBytes"/>: an unreadable or odd one keeps its file's time and name.</summary>
    public static CrashReportInfo Info(string fullPath, string name, DateTimeOffset written, string owner)
    {
        string head;
        try
        {
            head = ReadText(fullPath, HeaderBytes, out _);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            head = "";
        }

        var header = Header(head, name);
        return new CrashReportInfo(name, fullPath, header.Time ?? written, header.Kind, header.Process ?? ProcessFromName(name), owner);
    }

    /// <summary>What a report's first line (JSON) or text headings say: its time, process and kind.</summary>
    public static (DateTimeOffset? Time, string? Process, string Kind) Header(string head, string name)
    {
        ArgumentNullException.ThrowIfNull(head);
        string extension = Path.GetExtension(name).ToLowerInvariant();
        string first = head.Split('\n', 2)[0].Trim();
        if (first.StartsWith('{') && TryParse(first) is { } json)
        {
            using (json)
            {
                var root = json.RootElement;
                string? bugType = String(root, "bug_type");
                bool simulated = root.TryGetProperty("is_simulated", out var sim) && sim.ValueKind == JsonValueKind.Number && sim.GetInt32() == 1;
                string? process = String(root, "app_name") ?? String(root, "name") ?? String(root, "process_name");
                return (ParseTime(String(root, "timestamp")), Blank(process), KindOf(extension, bugType, simulated));
            }
        }

        var headings = Headings(head, 60);
        string? command = headings.GetValueOrDefault("Command") ?? headings.GetValueOrDefault("Process");
        if (command is not null && ProcessPid().Match(command) is { Success: true } m)
        {
            command = m.Groups["name"].Value;
        }

        return (ParseTime(headings.GetValueOrDefault("Date/Time")), Blank(command), KindOf(extension, null, false));
    }

    /// <summary>A report's kind word: by its <c>bug_type</c> for an <c>.ips</c>, else by its extension.</summary>
    public static string KindOf(string extension, string? bugType, bool simulated) => extension switch
    {
        ".diag" => "resource",
        ".spin" => "spin",
        ".hang" => "hang",
        ".panic" => "panic",
        ".crash" => "crash",
        _ => bugType switch
        {
            "309" or "109" => simulated ? "fault" : "crash",
            "298" => "jetsam",
            "210" => "panic",
            "288" => "stackshot",
            "385" or "409" => "hang",
            "145" => "resource",
            null => "report",
            _ => "report " + bugType,
        },
    };

    /// <summary>A report's file name without its time: <c>jamfRemoteAssistConnector-2026-10-14-021251.ips</c> → <c>jamfRemoteAssistConnector</c>.</summary>
    public static string ProcessFromName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (stem.StartsWith("ExcUserFault_", StringComparison.Ordinal))
        {
            stem = stem["ExcUserFault_".Length..];
        }

        return NameTime().Match(stem) is { Success: true } m ? m.Groups["name"].Value : stem;
    }

    /// <summary>
    /// The report in full: a summary line and the fields worth reading (the process, its parent, the times, the system, the exception,
    /// the termination, what the libraries said, the faulting thread's frames; a jetsam's killed and largest processes; a text report's
    /// first lines), with <paramref name="redact"/> applied to both.
    /// </summary>
    public static CrashDetail Describe(CrashReportInfo report, Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(redact);
        string whole = ReadText(report.FullPath, MaxBytes, out bool cut);
        var (summary, text, pid) = DescribeText(whole, report);
        if (cut)
        {
            text += "\n" + MacLogText.ReportCut(MaxBytes);
        }

        return new CrashDetail(redact(summary), redact(text), pid);
    }

    /// <summary><see cref="Describe"/> over a report's text already read (the tests' door).</summary>
    public static (string Summary, string Text, int? Pid) DescribeText(string whole, CrashReportInfo report)
    {
        ArgumentNullException.ThrowIfNull(whole);
        ArgumentNullException.ThrowIfNull(report);
        string[] parts = whole.Split('\n', 2);
        if (parts[0].TrimStart().StartsWith('{') && parts.Length == 2 && TryParse(parts[1]) is { } body)
        {
            using (body)
            {
                var root = body.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("exception", out _) || root.TryGetProperty("termination", out _) || root.TryGetProperty("threads", out _))
                    {
                        return DescribeCrash(root);
                    }

                    if (root.TryGetProperty("processes", out _) || root.TryGetProperty("largestProcess", out _))
                    {
                        return DescribeJetsam(root);
                    }

                    return (MacLogText.ReportOf(report.Kind), Scalars(root, 40), null);
                }
            }
        }

        return DescribeTextReport(whole, report);
    }

    private static (string, string, int?) DescribeCrash(JsonElement root)
    {
        var lines = new List<string>();
        int? pid = Int(root, "pid");
        string process = String(root, "procName") ?? "?";
        lines.Add("process: " + process + (pid is { } p ? "[" + p.ToString(CultureInfo.InvariantCulture) + "]" : "") + (String(root, "procPath") is { } path ? "  " + path : ""));
        AddWho(lines, "parent", root, "parentProc", "parentPid");
        if (String(root, "responsibleProc") is { } responsible && responsible != process)
        {
            AddWho(lines, "responsible", root, "responsibleProc", "responsiblePid");
        }

        Add(lines, "launched", String(root, "procLaunch"));
        Add(lines, "crashed", String(root, "captureTime"));
        if (root.TryGetProperty("osVersion", out var os) && os.ValueKind == JsonValueKind.Object)
        {
            Add(lines, "system", Join(" ", String(os, "train"), String(os, "build") is { } b ? "(" + b + ")" : null));
        }

        Add(lines, "cpu", String(root, "cpuType"));
        Add(lines, "user id", Int(root, "userID")?.ToString(CultureInfo.InvariantCulture));
        string? exception = null;
        if (root.TryGetProperty("exception", out var ex) && ex.ValueKind == JsonValueKind.Object)
        {
            exception = Join(" ", String(ex, "type"), String(ex, "signal") is { } s ? "(" + s + ")" : null, String(ex, "subtype"));
            Add(lines, "exception", Join("  ", exception, String(ex, "codes") is { } codes ? "codes " + codes : null, String(ex, "message")));
        }

        string? termination = null;
        if (root.TryGetProperty("termination", out var term) && term.ValueKind == JsonValueKind.Object)
        {
            termination = String(term, "indicator");
            Add(lines, "termination", Join("  ", String(term, "namespace"), Int(term, "code") is { } code ? "code " + code.ToString(CultureInfo.InvariantCulture) : null,
                termination, String(term, "byProc") is { } by ? "by " + by : null));
        }

        string? said = null;
        if (root.TryGetProperty("asi", out var asi) && asi.ValueKind == JsonValueKind.Object)
        {
            foreach (var library in asi.EnumerateObject())
            {
                string words = library.Value.ValueKind == JsonValueKind.Array
                    ? string.Join(" ", library.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()))
                    : library.Value.ToString();
                said ??= library.Name + ": " + words;
                lines.Add("said by " + library.Name + ": " + words);
            }
        }

        var images = Images(root);
        int faulting = Int(root, "faultingThread") ?? -1;
        if (root.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var thread in threads.EnumerateArray())
            {
                bool triggered = thread.TryGetProperty("triggered", out var t) && t.ValueKind == JsonValueKind.True;
                if (index == faulting || (faulting < 0 && triggered))
                {
                    lines.Add("faulting thread " + index.ToString(CultureInfo.InvariantCulture)
                        + (String(thread, "queue") is { } queue ? " (" + queue + ")" : String(thread, "name") is { } name ? " (" + name + ")" : "") + ":");
                    AddFrames(lines, thread, "frames", images);
                    break;
                }

                index++;
            }
        }

        if (root.TryGetProperty("lastExceptionBacktrace", out var last) && last.ValueKind == JsonValueKind.Array)
        {
            lines.Add("last exception backtrace:");
            AddFrames(lines, root, "lastExceptionBacktrace", images);
        }

        string summary = Join(" — ", exception, termination, said) ?? MacLogText.ReportOf("crash");
        return (summary, string.Join("\n", lines), pid);
    }

    private static (string, string, int?) DescribeJetsam(JsonElement root)
    {
        var lines = new List<string>();
        Add(lines, "time", String(root, "date"));
        string? largest = String(root, "largestProcess");
        Add(lines, "largest process", largest);
        var killed = new List<string>();
        var biggest = new List<(string Name, long Pages, int? Pid)>();
        if (root.TryGetProperty("processes", out var processes) && processes.ValueKind == JsonValueKind.Array)
        {
            foreach (var process in processes.EnumerateArray())
            {
                if (process.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string name = String(process, "name") ?? "?";
                int? pid = Int(process, "pid");
                if (String(process, "reason") is { } reason)
                {
                    killed.Add(name + (pid is { } p ? "[" + p.ToString(CultureInfo.InvariantCulture) + "]" : "") + " (" + reason + ")");
                }

                if (process.TryGetProperty("rpages", out var pages) && pages.TryGetInt64(out long n))
                {
                    biggest.Add((name, n, pid));
                }
            }
        }

        foreach (string one in killed.Take(20))
        {
            lines.Add("ended: " + one);
        }

        long pageSize = root.TryGetProperty("memoryStatus", out var status) && status.ValueKind == JsonValueKind.Object && Int(status, "pageSize") is { } size ? size : 16_384;
        foreach (var (name, pages, pid) in biggest.OrderByDescending(b => b.Pages).Take(10))
        {
            lines.Add("resident: " + name + (pid is { } p ? "[" + p.ToString(CultureInfo.InvariantCulture) + "]" : "") + "  "
                + (pages * pageSize / (1024 * 1024)).ToString("N0", CultureInfo.InvariantCulture) + " MB");
        }

        string summary = MacLogText.JetsamSummary(killed.Count, killed.FirstOrDefault(), largest);
        return (summary, string.Join("\n", lines), null);
    }

    private static (string, string, int?) DescribeTextReport(string whole, CrashReportInfo report)
    {
        string[] all = whole.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = all.Length > 1 && all[0].TrimStart().StartsWith('{') ? 1 : 0;   // a .panic's header line
        var lines = all.Skip(start).Take(TextLines).Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var headings = Headings(string.Join("\n", lines), 80);
        int? pid = headings.TryGetValue("PID", out string? pidText) && int.TryParse(pidText.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
        string? summary = Join(" — ", headings.GetValueOrDefault("Event"), headings.GetValueOrDefault("Action taken") is { } action ? "action taken: " + action : null,
            headings.GetValueOrDefault("CPU") ?? headings.GetValueOrDefault("Writes") ?? headings.GetValueOrDefault("Wakeups"));
        summary ??= lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? MacLogText.ReportOf(report.Kind);
        string text = string.Join("\n", lines) + (all.Length - start > TextLines ? "\n" + MacLogText.MoreLines(all.Length - start - TextLines) : "");
        return (summary, text, pid);
    }

    private static List<string> Images(JsonElement root)
    {
        var names = new List<string>();
        if (root.TryGetProperty("usedImages", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                names.Add(image.ValueKind == JsonValueKind.Object ? String(image, "name") ?? (String(image, "path") is { } p ? Path.GetFileName(p) : "???") : "???");
            }
        }

        return names;
    }

    private static void AddFrames(List<string> lines, JsonElement owner, string property, List<string> images)
    {
        if (!owner.TryGetProperty(property, out var frames) || frames.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int index = 0;
        int count = frames.GetArrayLength();
        foreach (var frame in frames.EnumerateArray())
        {
            if (index == Frames)
            {
                lines.Add("  " + MacLogText.MoreFrames(count - Frames));
                break;
            }

            string image = Int(frame, "imageIndex") is { } i && i >= 0 && i < images.Count ? images[i] : "???";
            string where = String(frame, "symbol") is { } symbol
                ? symbol + (Int(frame, "symbolLocation") is { } at && at != 0 ? " + " + at.ToString(CultureInfo.InvariantCulture) : "")
                : frame.TryGetProperty("imageOffset", out var offset) && offset.TryGetInt64(out long o) ? "0x" + o.ToString("x", CultureInfo.InvariantCulture) : "?";
            lines.Add("  " + index.ToString(CultureInfo.InvariantCulture).PadRight(3) + image + "  " + where);
            index++;
        }
    }

    private static void AddWho(List<string> lines, string label, JsonElement root, string name, string pid)
    {
        if (String(root, name) is { } who)
        {
            lines.Add(label + ": " + who + (Int(root, pid) is { } p ? "[" + p.ToString(CultureInfo.InvariantCulture) + "]" : ""));
        }
    }

    private static string Scalars(JsonElement root, int max)
    {
        var lines = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            if (lines.Count == max)
            {
                break;
            }

            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                lines.Add(property.Name + ": " + property.Value.ToString());
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>The <c>Key: value</c> headings of a text report's first <paramref name="lines"/> lines (the first of each key).</summary>
    public static Dictionary<string, string> Headings(string text, int lines)
    {
        var headings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n').Take(lines))
        {
            if (Heading().Match(line) is { Success: true } m)
            {
                headings.TryAdd(m.Groups["key"].Value, m.Groups["value"].Value.Trim());
            }
        }

        return headings;
    }

    /// <summary>A report's time: <c>2026-10-18 01:14:40.00 -0500</c> (a fraction and the offset's colon optional); null when it is not one.</summary>
    public static DateTimeOffset? ParseTime(string? text)
    {
        if (text is null || ReportTime().Match(text.Trim()) is not { Success: true } m)
        {
            return null;
        }

        if (!DateTime.TryParseExact(m.Groups["moment"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        int hours = int.Parse(m.Groups["h"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int minutes = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var offset = new TimeSpan(Math.Abs(hours), minutes, 0) * (hours < 0 || m.Groups["h"].Value[0] == '-' ? -1 : 1);
        if (m.Groups["fraction"].Success && double.TryParse("0" + m.Groups["fraction"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction))
        {
            local = local.AddTicks((long)(fraction * TimeSpan.TicksPerSecond));
        }

        try
        {
            return new DateTimeOffset(local, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The text of a file's first <paramref name="max"/> bytes (UTF-8, any partial character at the cut replaced).</summary>
    public static string ReadText(string path, int max, out bool cut)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        long length = stream.Length;
        var buffer = new byte[(int)Math.Min(max, Math.Max(0, length))];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        cut = length > max;
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => Blank(value.GetString()),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) ? n : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static void Add(List<string> lines, string label, string? value)
    {
        if (value is not null)
        {
            lines.Add(label + ": " + value);
        }
    }

    private static string? Join(string separator, params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return kept.Count == 0 ? null : string.Join(separator, kept);
    }

    [GeneratedRegex(@"^(?<moment>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})(?<fraction>\.\d+)? (?<h>[+-]\d{2}):?(?<m>\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ReportTime();

    [GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z /]{0,30}):\s+(?<value>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?<name>.+?)\s*\[\d+\]", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessPid();

    [GeneratedRegex(@"^(?<name>.+?)[-_]\d{4}-\d{2}-\d{2}-\d{6}", RegexOptions.CultureInvariant)]
    private static partial Regex NameTime();
}
