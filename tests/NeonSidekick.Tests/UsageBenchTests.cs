using NeonSidekick.App;
using NeonSidekick.Bench;
using NeonSidekick.Sessions;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The <c>/usage</c> Bench tab (2026-11-27, the user's idea: the Models tab named "Bench", with <c>/bench</c>'s saved runs): the caption, In use,
/// a grid of each model and level's latest verdicts (yours marked) and the recent runs.
/// </summary>
public sealed class UsageBenchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static BenchRun Run(string model, string? reasoning, DateTimeOffset at, params (string Test, BenchVerdict Verdict)[] results) => new()
    {
        At = at,
        Model = model,
        Reasoning = reasoning,
        Results = results.Select(r => new BenchResult { Test = r.Test, Verdict = r.Verdict, Reason = "why", Seconds = 6 }).ToList(),
    };

    private static ModelStat Stat(string model) => new(model, 25, 3, 50, 5, 1, 2500, 2000, 40_000, DateTimeOffset.UnixEpoch, 0);

    private static List<string> Lines(IReadOnlyList<BenchRun> runs, (string? Model, string Reasoning)? current = null, int width = 140, IReadOnlyList<ModelStat>? stats = null) =>
        UsageText.BenchLines(stats ?? [Stat("qwen")], runs, current, logging: true, width).ToList();

    private static string Row(List<string> lines, string label) => lines.Single(l => l.Contains(label, StringComparison.Ordinal) && l.Contains('/') && !l.Contains(':'));

    [Fact]
    public void TheGrid_HasARowPerModelAndLevel_EachWithItsOwnVerdicts_NewestFirst()
    {
        var runs = new List<BenchRun>
        {
            Run("qwen", "none", T0, ("grid", BenchVerdict.Fail), ("rule", BenchVerdict.Pass)),
            Run("qwen", "xhigh", T0.AddDays(1), ("grid", BenchVerdict.Pass), ("json", BenchVerdict.Error)),
            Run("gemma", null, T0.AddHours(1), ("mind", BenchVerdict.Pass)),
            Run("qwen", "xhigh", T0.AddDays(2), ("grid", BenchVerdict.Skipped), ("rule", BenchVerdict.Fail)),   // a skip keeps the earlier pass
        };
        var rows = BenchGrid.Rows(runs);
        Assert.Equal([("qwen", "xhigh"), ("qwen", "none"), ("gemma", (string?)null)], rows.Select(r => (r.Model, r.Reasoning)));
        Assert.Equal(BenchVerdict.Pass, rows[0].Latest["grid"].Result.Verdict);
        Assert.Equal(BenchVerdict.Fail, rows[0].Latest["rule"].Result.Verdict);
        Assert.False(rows[0].Latest.ContainsKey("mind"));   // never run there: blank
        Assert.Equal((1, 3), (rows[0].Passed, rows[0].Counted));
        Assert.Equal(T0.AddDays(2), rows[0].LastRun);
        Assert.Equal(BenchVerdict.Fail, rows[1].Latest["grid"].Result.Verdict);   // the other level's own

        var lines = Lines(runs);
        string xhigh = Row(lines, "qwen" + BenchText.LevelSeparator + "xhigh");
        string legacy = Row(lines, "gemma" + BenchText.LevelSeparator + UsageText.BenchNoLevel);
        Assert.Contains("1/3", xhigh);
        Assert.Contains("✓", xhigh);
        Assert.Contains("✗", xhigh);
        Assert.Contains("!", xhigh);
        Assert.EndsWith(T0.AddDays(2).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), xhigh.TrimEnd());
        Assert.Contains("1/1", legacy);
        Assert.True(lines.FindIndex(l => l.Contains("qwen · xhigh", StringComparison.Ordinal)) < lines.FindIndex(l => l.Contains("qwen · none", StringComparison.Ordinal)));
        Assert.Contains(UsageText.BenchLegend, lines);
        Assert.Contains(lines, l => l.Contains(UsageText.BenchGridHeading("sycophancy"), StringComparison.Ordinal) && l.Contains(UsageText.BenchScoreHeading, StringComparison.Ordinal));
    }

    [Fact]
    public void YourSetup_IsMarked_AndNothingWithNoModel()
    {
        var runs = new List<BenchRun> { Run("qwen", "xhigh", T0, ("grid", BenchVerdict.Pass)), Run("qwen", "none", T0, ("grid", BenchVerdict.Pass)) };
        var lines = Lines(runs, ("QWEN", "XHigh"));
        Assert.StartsWith(UsageText.BenchYouMark + "qwen · xhigh", Row(lines, "qwen · xhigh"));
        Assert.StartsWith("  qwen · none", Row(lines, "qwen · none"));
        Assert.DoesNotContain(Lines(runs, null), l => l.StartsWith(UsageText.BenchYouMark, StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(runs, (null, "xhigh")), l => l.StartsWith(UsageText.BenchYouMark, StringComparison.Ordinal));
    }

    [Fact]
    public void Narrow_TheTestColumnsGo_TheScoreAndDateStay()
    {
        var runs = new List<BenchRun> { Run("qwen", "xhigh", T0, ("grid", BenchVerdict.Pass)) };
        var lines = Lines(runs, width: 50);
        string row = Row(lines, "qwen · xhigh");
        Assert.True(TextCells.Width(row) <= 50, row);   // the grid row fits; prose lines wrap in the pane
        Assert.DoesNotContain("✓", row);
        Assert.Contains("1/1", row);
        Assert.DoesNotContain(UsageText.BenchLegend, lines);
    }

    [Fact]
    public void RecentRuns_SayWhatRan_TheirTime_AndHowManyAreSaved()
    {
        var all = Run("qwen", "xhigh", T0.AddHours(3), BenchCatalog.All.Select(t => (t.Id, BenchVerdict.Pass)).ToArray());
        var group = Run("qwen", "xhigh", T0.AddHours(2), BenchCatalog.Resolve("long").Select(t => (t.Id, BenchVerdict.Pass)).ToArray());
        var ids = Run("qwen", null, T0.AddHours(1), ("grid", BenchVerdict.Pass), ("json", BenchVerdict.Fail));
        var canceled = Run("qwen", "none", T0, BenchCatalog.Resolve("reasoning").Select(t => (t.Id, BenchVerdict.Pass)).ToArray());
        canceled.Cancelled = true;
        Assert.Equal(BenchCatalog.AllWord, BenchGrid.WhatRan(all));
        Assert.Equal("long", BenchGrid.WhatRan(group));
        Assert.Equal("grid, json", BenchGrid.WhatRan(ids));
        Assert.Equal("grid, rule, mind, sycophancy (canceled)", BenchGrid.WhatRan(canceled));
        Assert.Equal("saturation", BenchGrid.WhatRan(Run("qwen", "x", T0, ("saturation", BenchVerdict.Pass))));

        var lines = Lines([canceled, ids, group, all]);
        int rule = lines.IndexOf(SectionRule.Lead + " " + UsageText.BenchRecentHeading);
        Assert.True(rule > 0);
        string newest = lines[rule + 1];
        Assert.StartsWith(all.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), newest);
        Assert.Contains("9/9", newest);
        Assert.EndsWith(BenchText.Elapsed(54), newest);   // nine results of 6 s
        Assert.Equal(UsageText.BenchRunsSaved(4), lines[^1]);
        Assert.Equal("1 run saved · /bench history lists them with their sampling without the pane", UsageText.BenchRunsSaved(1));
    }

    [Fact]
    public void TheCaption_ThenInUse_ThenTests_AndWithoutRunsNoRuns()
    {
        var lines = Lines([]);
        Assert.Equal(UsageText.ModelsCaption, lines[0]);
        Assert.Equal(SectionRule.Lead + " " + UsageText.BenchInUseHeading, lines[2]);
        Assert.StartsWith(UsageText.ModelsModelHeading, lines[3]);
        Assert.Equal(BenchText.NoRuns, lines[^1]);
        Assert.DoesNotContain(SectionRule.Lead + " " + UsageText.BenchRecentHeading, lines);

        var empty = UsageText.BenchLines([], [], null, logging: true, 120).ToList();
        Assert.Equal([UsageText.ModelsEmpty, BenchText.NoRuns], empty);
        Assert.Null(UsageText.BenchLayout([], [], null, true, 120).RecentLine);
    }

    [Fact]
    public void TheLayout_NamesTheRecentRunsLine_AsDrawn()
    {
        var runs = new List<BenchRun> { Run("qwen", "xhigh", T0, ("grid", BenchVerdict.Pass)) };
        var (content, line) = UsageText.BenchLayout([Stat("qwen")], runs, null, true, 100);
        using var console = new TestConsole();
        console.Profile.Width = 104;
        var drawn = ScreenPane.RenderLines(content, console, 104).Select(l => string.Concat(l.Select(s => s.Text))).ToList();
        Assert.NotNull(line);
        Assert.Contains(UsageText.BenchRecentHeading, drawn[line!.Value]);
    }
}
