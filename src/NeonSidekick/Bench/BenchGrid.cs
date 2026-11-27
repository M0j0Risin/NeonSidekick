namespace NeonSidekick.Bench;

/// <summary>
/// One row of the Bench tab's grid (2026-11-27, the user's call): a model at one reasoning level, each test's latest verdict among that
/// model's runs at that level (a skip never replacing one), and that level's newest run's time. <see cref="Reasoning"/> null is the runs
/// saved before the level was.
/// </summary>
public sealed record BenchGridRow(string Model, string? Reasoning, IReadOnlyDictionary<string, (BenchResult Result, DateTimeOffset At)> Latest, DateTimeOffset LastRun)
{
    /// <summary>Passes over the tests with a verdict.</summary>
    public int Passed => Latest.Values.Count(v => v.Result.Verdict == BenchVerdict.Pass);

    /// <summary>The tests with a verdict.</summary>
    public int Counted => Latest.Count;
}

/// <summary>
/// The grid's rows from one read of the saved runs (2026-11-27), so the tab doesn't read the file a row at a time: one row per model and
/// level, the models by their newest run, newest first, a model's rows together, each level's newest first. Models match ignoring case, as
/// the server reported them; levels as saved, ignoring case.
/// </summary>
public static class BenchGrid
{
    public static IReadOnlyList<BenchGridRow> Rows(IReadOnlyList<BenchRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var rows = new List<BenchGridRow>();
        foreach (var model in runs.GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Max(r => r.At)))
        {
            foreach (var level in model.GroupBy(r => r.Reasoning ?? "", StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Max(r => r.At)))
            {
                var newest = level.MaxBy(r => r.At)!;
                rows.Add(new BenchGridRow(newest.Model, newest.Reasoning, BenchHistory.LatestOf(level.OrderBy(r => r.At)), newest.At));
            }
        }

        return rows;
    }

    /// <summary>
    /// What a run ran, for the recent runs (2026-11-27): <c>all</c>, a group's word, or its test ids; read from its results, since a run saves
    /// no command words. A canceled run always lists the ids it finished, then <c>(canceled)</c>: a group word would name a command nobody typed.
    /// </summary>
    public static string WhatRan(BenchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var ids = run.Results.Select(r => r.Test).ToList();
        string list = string.Join(", ", ids);
        if (run.Cancelled)
        {
            return list + " " + BenchText.CanceledMark;
        }

        bool Same(IEnumerable<BenchTest> tests) => tests.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(ids) && ids.Count == tests.Count();
        if (Same(BenchCatalog.All))
        {
            return BenchCatalog.AllWord;
        }

        foreach (var (word, category) in BenchCatalog.CategoryWords)
        {
            if (Same(BenchCatalog.All.Where(t => t.Category == category)))
            {
                return word;
            }
        }

        // A run from before saturation stood alone as Window Capacity (2026-11-27, the last-3 review's #40): its long group held it.
        if (Same(BenchCatalog.All.Where(t => t.Category is BenchCategory.LongContext or BenchCategory.WindowCapacity)))
        {
            return BenchCatalog.CategoryWords.First(c => c.Category == BenchCategory.LongContext).Word;
        }

        return list;
    }
}
