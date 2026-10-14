using NeonSidekick.Settings;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The Browser window's privacy (later on 2026-10-13): the setting (<see cref="BrowserPrivacy"/>) and the window's own list of visits
/// behind its history menu (<see cref="BrowserHistory"/>). The window that keeps and clears them is the smoke's.
/// </summary>
public sealed class BrowserHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ── The setting ─────────────────────────────────────────────────────────

    [Fact]
    public void Privacy_TheNames_TheDefault_AndABadValue()
    {
        Assert.Equal(["keep-history", "clear-history"], BrowserPrivacy.Names);
        Assert.Equal("clear-history", BrowserPrivacy.Default);
        Assert.Equal("clear-history", new AppSettingsData().BrowserPrivacy);   // the user's default
        Assert.Equal(BrowserPrivacyKind.KeepHistory, BrowserPrivacy.Resolve(new AppSettingsData { BrowserPrivacy = " Keep-History " }));
        Assert.Equal(BrowserPrivacyKind.ClearHistory, BrowserPrivacy.Resolve(new AppSettingsData { BrowserPrivacy = "clear-history" }));
        Assert.Equal(BrowserPrivacyKind.ClearHistory, BrowserPrivacy.Resolve(new AppSettingsData { BrowserPrivacy = "forget" }));
        Assert.False(BrowserPrivacy.TryParse("forget", out _));
        Assert.Equal(["keep-history", "clear-history"], new[] { BrowserPrivacyKind.KeepHistory, BrowserPrivacyKind.ClearHistory }.Select(BrowserPrivacy.Name));
        Assert.Equal("clears its history when the window closes; cookies and sign-ins stay", BrowserPrivacy.Describe("clear-history"));
        Assert.Equal("", BrowserPrivacy.Describe("forget"));
    }

    // ── Recording ───────────────────────────────────────────────────────────

    [Fact]
    public void Record_PutsTheNewestOnTop_AndARepeatMovesUp_AFragmentTheSamePage()
    {
        IReadOnlyList<BrowserHistoryEntry> list = [];
        list = BrowserHistory.Record(list, "https://a.example/", "A", Now);
        list = BrowserHistory.Record(list, "https://b.example/", "B", Now.AddMinutes(1));
        list = BrowserHistory.Record(list, "https://a.example/#top", " A again ", Now.AddMinutes(2));

        Assert.Equal(["https://a.example/#top", "https://b.example/"], list.Select(e => e.Url));
        Assert.Equal("A again", list[0].Title);
        Assert.Equal(Now.AddMinutes(2), list[0].At);
    }

    [Fact]
    public void Record_KeepsWebPagesOnly_AndTheCap()
    {
        IReadOnlyList<BrowserHistoryEntry> list = [];
        Assert.Same(list, BrowserHistory.Record(list, "about:blank", "", Now));
        Assert.Same(list, BrowserHistory.Record(list, "file:///C:/", "", Now));
        Assert.Same(list, BrowserHistory.Record(list, null, null, Now));

        for (int i = 0; i < BrowserHistory.Cap + 5; i++)
        {
            list = BrowserHistory.Record(list, $"https://example.com/{i}", null, Now.AddSeconds(i));
        }

        Assert.Equal(1000, BrowserHistory.Cap);
        Assert.Equal(BrowserHistory.Cap, list.Count);
        Assert.Equal($"https://example.com/{BrowserHistory.Cap + 4}", list[0].Url);
        Assert.Equal("https://example.com/5", list[^1].Url);   // the oldest five dropped
        Assert.Equal("", list[0].Title);
    }

    [Fact]
    public void Retitle_ChangesTheNewestEntry_OnlyForItsPage()
    {
        IReadOnlyList<BrowserHistoryEntry> list = BrowserHistory.Record([], "https://a.example/", "", Now);

        Assert.Equal("Later title", BrowserHistory.Retitle(list, "https://a.example/#x", " Later title ")[0].Title);
        Assert.Same(list, BrowserHistory.Retitle(list, "https://b.example/", "Other"));
        Assert.Same(list, BrowserHistory.Retitle(list, "https://a.example/", "  "));
        Assert.Empty(BrowserHistory.Retitle([], "https://a.example/", "T"));
    }

    // ── The file ────────────────────────────────────────────────────────────

    [Fact]
    public void SaveAndLoad_RoundTrip_AndAMissingBrokenOrOtherVersionFileReadsEmpty()
    {
        string file = Path.Combine(_dir, BrowserHistory.FileName);
        Assert.Empty(BrowserHistory.Load(file));

        IReadOnlyList<BrowserHistoryEntry> list = BrowserHistory.Record(BrowserHistory.Record([], "https://a.example/", "A", Now), "https://b.example/", "B", Now.AddHours(1));
        Assert.True(BrowserHistory.Save(file, list));
        Assert.Equal(list, BrowserHistory.Load(file));
        string json = File.ReadAllText(file);
        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"visits\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"url\": \"https://b.example/\"", json, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        File.WriteAllText(file, "{ not json");
        Assert.Empty(BrowserHistory.Load(file));
        File.WriteAllText(file, "{ \"version\": 2, \"visits\": [{ \"url\": \"https://a.example/\", \"title\": \"A\", \"at\": \"2026-10-13T18:00:00+00:00\" }] }");
        Assert.Empty(BrowserHistory.Load(file));
        File.WriteAllText(file, "{ \"version\": 1, \"visits\": [{ \"url\": \"file:///C:/\", \"title\": \"x\", \"at\": \"2026-10-13T18:00:00+00:00\" }, { \"url\": \"https://a.example/\", \"title\": \"A\", \"at\": \"2026-10-13T18:00:00+00:00\" }] }");
        Assert.Equal(["https://a.example/"], BrowserHistory.Load(file).Select(e => e.Url));   // only web pages come back

        Assert.True(BrowserHistory.Delete(file));
        Assert.False(File.Exists(file));
    }

    // ── The menu ────────────────────────────────────────────────────────────

    [Fact]
    public void MenuItems_Empty_IsOneDimRow_AndClearHistoryOff()
    {
        var items = BrowserHistory.MenuItems([], Now, TimeZoneInfo.Utc);

        Assert.Equal(["No history yet", "", "Clear history"], items.Select(i => i.Label));
        Assert.False(items[0].Enabled);
        Assert.True(items[1].IsSeparator);
        Assert.False(items[2].Enabled);
    }

    [Fact]
    public void MenuItems_TheNewestRows_ThenOneSubmenuPerDay_ThenOlder_ThenClear()
    {
        // 20 today (from 17:40 back), 3 yesterday, then one a day for 10 days: 7 day submenus at most, the rest under Older.
        var visits = new List<BrowserHistoryEntry>();
        for (int i = 0; i < 20; i++)
        {
            visits.Add(new BrowserHistoryEntry($"https://today.example/{i}", $"Today {i}", Now.AddMinutes(-20 - i)));
        }

        for (int i = 0; i < 3; i++)
        {
            visits.Add(new BrowserHistoryEntry($"https://yesterday.example/{i}", "", Now.AddDays(-1).AddMinutes(-i)));
        }

        for (int d = 2; d < 12; d++)
        {
            visits.Add(new BrowserHistoryEntry($"https://day{d}.example/", $"Day {d}", Now.AddDays(-d)));
        }

        var items = BrowserHistory.MenuItems(visits, Now, TimeZoneInfo.Utc);

        Assert.Equal(15, items.TakeWhile(i => i.Children is null && !i.IsSeparator).Count());
        Assert.Equal("Today 0 · today.example", items[0].Label);
        Assert.Equal(1, items[0].Command);
        Assert.Equal(["Earlier today", "Yesterday", "Sun 11 Oct", "Sat 10 Oct", "Fri 9 Oct", "Thu 8 Oct", "Wed 7 Oct", "Older"],
            items.Where(i => i.Children is not null).Select(i => i.Label));
        var earlier = items.Single(i => i.Label == "Earlier today").Children!;
        Assert.Equal(5, earlier.Count);
        Assert.Equal(16, earlier[0].Command);                                    // index 15 plus one
        Assert.Equal("https://yesterday.example/0", items.Single(i => i.Label == "Yesterday").Children![0].Label);   // no title: the address
        Assert.Equal(["Day 7 · day7.example", "Day 8 · day8.example", "Day 9 · day9.example", "Day 10 · day10.example", "Day 11 · day11.example"],
            items.Single(i => i.Label == "Older").Children!.Select(i => i.Label));
        Assert.True(items[^2].IsSeparator);
        Assert.Equal(("Clear history", BrowserHistory.ClearCommand, true), (items[^1].Label, items[^1].Command, items[^1].Enabled));
        Assert.True(items.Count <= 25);
    }

    [Fact]
    public void MenuItems_ADaysRows_StopAt25_AndTheDayIsTheZonesLocalOne()
    {
        var visits = Enumerable.Range(0, 15).Select(i => new BrowserHistoryEntry($"https://new.example/{i}", "N", Now.AddMinutes(-i)))
            .Concat(Enumerable.Range(0, 30).Select(i => new BrowserHistoryEntry($"https://old.example/{i}", "O", Now.AddDays(-3).AddMinutes(-i))))
            .ToList();

        var day = BrowserHistory.MenuItems(visits, Now, TimeZoneInfo.Utc).Single(i => i.Children is not null);
        Assert.Equal("Sat 10 Oct", day.Label);
        Assert.Equal(25, day.Children!.Count);

        // 23:30 on the 12th in UTC is the 13th at UTC+2: today, not yesterday.
        var late = new BrowserHistoryEntry("https://late.example/", "L", new DateTimeOffset(2026, 10, 12, 23, 30, 0, TimeSpan.Zero));
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var withLate = Enumerable.Range(0, 15).Select(i => new BrowserHistoryEntry($"https://new.example/{i}", "N", Now.AddMinutes(-i))).Append(late).ToList();
        Assert.Equal("Earlier today", BrowserHistory.MenuItems(withLate, Now, zone).Single(i => i.Children is not null).Label);
        Assert.Equal("Yesterday", BrowserHistory.MenuItems(withLate, Now, TimeZoneInfo.Utc).Single(i => i.Children is not null).Label);
    }

    [Fact]
    public void RowLabel_CutsALongTitle_AndDayLabelIsInvariant()
    {
        string title = new string('a', 70);
        Assert.Equal(new string('a', 59) + "… · example.com", BrowserHistory.RowLabel(new BrowserHistoryEntry("https://example.com/x", title, Now)));
        Assert.Equal("Short · example.com", BrowserHistory.RowLabel(new BrowserHistoryEntry("https://example.com/x", "Short", Now)));
        var today = new DateOnly(2026, 10, 13);
        Assert.Equal(["Earlier today", "Yesterday", "Sun 11 Oct"], new[] { today, today.AddDays(-1), today.AddDays(-2) }.Select(d => BrowserHistory.DayLabel(d, today)));
    }
}
