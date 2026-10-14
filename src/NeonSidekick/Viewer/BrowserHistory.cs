using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Viewer;

/// <summary>One page the Browser window went to: its address, its title when it had one, and when.</summary>
public sealed record BrowserHistoryEntry(string Url, string Title, DateTimeOffset At);

/// <summary>The profile's <c>browser-history.json</c>: a version and the visits, newest first.</summary>
public sealed class BrowserHistoryFile
{
    public int Version { get; set; } = BrowserHistory.Version;

    public List<BrowserHistoryEntry> Visits { get; set; } = [];
}

/// <summary>
/// The source-generated context for <c>browser-history.json</c> (later on 2026-10-13) and <c>browser-links.json</c> (later still, the
/// saved links). Reflection serialisation is off.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BrowserHistoryFile))]
[JsonSerializable(typeof(BrowserLinksFile))]
public sealed partial class BrowserJsonContext : JsonSerializerContext;

/// <summary>
/// The Browser window's own list of visits (later on 2026-10-13, the user's ask: history in the window): WebView2 records Chromium's
/// history but cannot read it back, so the window keeps this beside it for its history menu (🕘, Ctrl+H). Newest first, one entry per
/// address (a <c>#fragment</c> is the same page), web addresses only, at most <see cref="Cap"/>. Under <c>Web browser privacy</c>
/// <c>keep-history</c> it lives in the profile's <see cref="FileName"/>; under <c>clear-history</c> only while the window is open.
/// <see cref="MenuItems"/> turns it into the menu's rows. Pure but for <see cref="Load"/> and <see cref="Save"/>; pinned.
/// </summary>
public static class BrowserHistory
{
    /// <summary>The file in the profile's folder.</summary>
    public const string FileName = "browser-history.json";

    /// <summary>The file's version; another reads as empty and is replaced at the next save.</summary>
    public const int Version = 1;

    /// <summary>The most visits kept: the oldest go first.</summary>
    public const int Cap = 1000;

    /// <summary>The menu: the newest rows on the root, the day submenus, the rows in each, and how long a title may be.</summary>
    public const int NewestRows = 15;
    public const int MaxDays = 7;
    public const int DayRows = 25;
    public const int OlderRows = 25;
    public const int TitleLength = 60;

    /// <summary>The menu's Clear history command: past every row's (a row's is its index plus one).</summary>
    public const int ClearCommand = 10_000;

    public const string ClearLabel = "Clear history";
    public const string EmptyLabel = "No history yet";
    public const string EarlierTodayLabel = "Earlier today";
    public const string YesterdayLabel = "Yesterday";
    public const string OlderLabel = "Older";

    private const string Category = "Browser";

    /// <summary>
    /// <paramref name="list"/> with a visit to <paramref name="url"/> on top: the same page (by <see cref="Key"/>) moved up with the new
    /// address and title, a new one added, the oldest past <see cref="Cap"/> dropped. Anything but a web address leaves the list as it is.
    /// </summary>
    public static IReadOnlyList<BrowserHistoryEntry> Record(IReadOnlyList<BrowserHistoryEntry> list, string? url, string? title, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (!VideoPage.IsWebLink(url))
        {
            return list;
        }

        string key = Key(url!);
        var entry = new BrowserHistoryEntry(url!, (title ?? "").Trim(), at);
        return [entry, .. list.Where(e => !string.Equals(Key(e.Url), key, StringComparison.Ordinal)).Take(Cap - 1)];
    }

    /// <summary><paramref name="list"/> with the newest entry's title changed, when it is <paramref name="url"/>'s page (a title that came after the page).</summary>
    public static IReadOnlyList<BrowserHistoryEntry> Retitle(IReadOnlyList<BrowserHistoryEntry> list, string? url, string? title)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list.Count == 0 || url is null || string.IsNullOrWhiteSpace(title) || !string.Equals(Key(list[0].Url), Key(url), StringComparison.Ordinal))
        {
            return list;
        }

        return [list[0] with { Title = title.Trim() }, .. list.Skip(1)];
    }

    /// <summary>What makes two addresses one page: the address without its <c>#fragment</c>.</summary>
    public static string Key(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        int hash = url.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? url : url[..hash];
    }

    /// <summary>The visits in <paramref name="path"/>; none for a missing file, and none, logged, for a broken one or one of another version.</summary>
    public static IReadOnlyList<BrowserHistoryEntry> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), BrowserJsonContext.Default.BrowserHistoryFile);
            if (file is null || file.Version != Version)
            {
                DiagnosticLog.Warn(Category, $"{FileName} is not version {Version.ToString(CultureInfo.InvariantCulture)}; starting with no history (it is replaced at the next save).");
                return [];
            }

            // A null list or entry (a hand edit) is none, as BrowserLinks reads them (2026-10-14, the review: it threw on the window's thread).
            return (file.Visits ?? []).Where(v => v is not null && VideoPage.IsWebLink(v.Url)).Select(v => v with { Title = v.Title ?? "" }).Take(Cap).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}; starting with no history: {ex.Message}", ex);
            return [];
        }
    }

    /// <summary><paramref name="list"/> written to <paramref name="path"/> (a temp file moved over the old one); false, logged, when it could not be.</summary>
    public static bool Save(string path, IReadOnlyList<BrowserHistoryEntry> list)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(list);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(tempPath, JsonSerializer.Serialize(new BrowserHistoryFile { Visits = [.. list] }, BrowserJsonContext.Default.BrowserHistoryFile));
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* best effort */ }
            DiagnosticLog.Error(Category, $"Could not write {FileName}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>The file at <paramref name="path"/> removed (a clear, or <c>clear-history</c>'s close); false, logged, when it could not be.</summary>
    public static bool Delete(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not delete {FileName}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>A row's words: the title (cut at <see cref="TitleLength"/>) and the host, <c>Example Domain · example.com</c>; the address alone without a title.</summary>
    public static string RowLabel(BrowserHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string host = BrowserPage.HostOf(entry.Url) ?? entry.Url;
        if (string.IsNullOrWhiteSpace(entry.Title))
        {
            return Cut(entry.Url);
        }

        return Cut(entry.Title.Trim()) + " · " + host;
    }

    /// <summary>
    /// The history menu for <paramref name="list"/> at <paramref name="now"/> in <paramref name="zone"/>: the <see cref="NewestRows"/>
    /// newest as rows, then the rest by local day in a submenu each (<see cref="EarlierTodayLabel"/>, <see cref="YesterdayLabel"/>, then
    /// <c>Mon 12 Oct</c>) for at most <see cref="MaxDays"/> days of <see cref="DayRows"/> rows, then <see cref="OlderLabel"/> with the next
    /// <see cref="OlderRows"/>, a separator and <see cref="ClearLabel"/>. Empty: one disabled <see cref="EmptyLabel"/> row and Clear
    /// history off. A row's command is its index in <paramref name="list"/> plus one.
    /// </summary>
    public static IReadOnlyList<ContextMenuItem> MenuItems(IReadOnlyList<BrowserHistoryEntry> list, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(zone);
        if (list.Count == 0)
        {
            return [new ContextMenuItem(EmptyLabel, Enabled: false), ContextMenuItem.Separator, new ContextMenuItem(ClearLabel, ClearCommand, Enabled: false)];
        }

        var items = new List<ContextMenuItem>();
        for (int i = 0; i < Math.Min(NewestRows, list.Count); i++)
        {
            items.Add(new ContextMenuItem(RowLabel(list[i]), i + 1));
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        int index = NewestRows;
        int days = 0;
        while (index < list.Count && days < MaxDays)
        {
            var day = LocalDay(list[index], zone);
            var rows = new List<ContextMenuItem>();
            while (index < list.Count && LocalDay(list[index], zone) == day)
            {
                if (rows.Count < DayRows)
                {
                    rows.Add(new ContextMenuItem(RowLabel(list[index]), index + 1));
                }

                index++;
            }

            items.Add(new ContextMenuItem(DayLabel(day, today), Children: rows));
            days++;
        }

        if (index < list.Count)
        {
            var older = list.Skip(index).Take(OlderRows).Select((e, i) => new ContextMenuItem(RowLabel(e), index + i + 1)).ToList();
            items.Add(new ContextMenuItem(OlderLabel, Children: older));
        }

        items.Add(ContextMenuItem.Separator);
        items.Add(new ContextMenuItem(ClearLabel, ClearCommand));
        return items;
    }

    /// <summary>A day submenu's name: <see cref="EarlierTodayLabel"/>, <see cref="YesterdayLabel"/>, else <c>Mon 12 Oct</c> (invariant). Pinned.</summary>
    public static string DayLabel(DateOnly day, DateOnly today) =>
        day >= today ? EarlierTodayLabel
        : day == today.AddDays(-1) ? YesterdayLabel
        : day.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    private static DateOnly LocalDay(BrowserHistoryEntry entry, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.At, zone).DateTime);

    // At most TitleLength characters, never splitting a surrogate pair, with an ellipsis when cut.
    private static string Cut(string text)
    {
        if (text.Length <= TitleLength)
        {
            return text;
        }

        int end = TitleLength - 1;
        if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end].TrimEnd() + "…";
    }
}
