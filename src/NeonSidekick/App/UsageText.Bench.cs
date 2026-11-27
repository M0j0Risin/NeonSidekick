using System.Globalization;
using NeonSidekick.Bench;
using NeonSidekick.Sessions;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

// ── The Bench tab (2026-11-27, the user's idea: the Models tab named "Bench", with the /bench results) ─────────────────────────────────────
// One tab answers how each model does in use and on the tests: the caption, In use (the Models table as it was), Tests (a grid, one row per
// model and reasoning level, each test's latest verdict) and Recent runs. Every row is one line, so the Recent runs rule's line is its row's
// index and /bench history can open the pane on it.

public static partial class UsageText
{
    /// <summary>The tab's title and index; <c>/usage bench</c> (and <c>/usage models</c>) opens the pane on it.</summary>
    public const string BenchTabTitle = "Bench";

    public const int BenchTabIndex = 5;

    /// <summary>The word <c>/usage bench</c> takes (2026-11-27); <see cref="ModelsWord"/> still opens the tab.</summary>
    public const string BenchWord = "bench";

    /// <summary>The argument list's note beside <see cref="BenchWord"/>.</summary>
    public const string BenchWordNote = "each model in use and on /bench";

    /// <summary>The tab's section headings.</summary>
    public const string BenchInUseHeading = "In use";

    public const string BenchTestsHeading = "Tests";

    public const string BenchRecentHeading = "Recent runs";

    /// <summary>A grid row's level for a run saved before the level was.</summary>
    public const string BenchNoLevel = "—";

    /// <summary>The grid's two last columns' headings.</summary>
    public const string BenchScoreHeading = "score";

    public const string BenchLastRunHeading = "last run";

    /// <summary>Your setup's row in the grid (2026-11-27, the user's pick): the connected model at the current level.</summary>
    public const string BenchYouMark = "▸ ";

    /// <summary>The legend under the grid: the marks and the short headings.</summary>
    public const string BenchLegend = "✓ pass · ✗ fail · ! error · syco = sycophancy · multi = multihop · satur = saturation";

    /// <summary>
    /// A grid column's heading: the test's id, the three long ones short so the grid fits a 108-column window (2026-11-27).
    /// </summary>
    public static string BenchGridHeading(string id) => id switch
    {
        "sycophancy" => "syco",
        "multihop" => "multi",
        "saturation" => "satur",
        _ => id,
    };

    /// <summary>The recent runs' note (2026-11-27): <c>12 runs saved · /bench history lists them with their sampling without the pane</c>.</summary>
    public static string BenchRunsSaved(int count) =>
        Plural(count, "run", "runs") + " saved · /bench history lists them with their sampling without the pane";

    /// <summary>How many runs Recent runs shows, newest first.</summary>
    public const int BenchRecentShown = 8;

    private const int BenchGap = 2;

    private sealed record BenchRow(string Plain, IRenderable Shown, bool Recent = false);

    /// <summary>The pane's tab (2026-11-27, <see cref="BenchLayout"/>'s renderable).</summary>
    public static IRenderable BenchTab(IReadOnlyList<ModelStat> stats, IReadOnlyList<BenchRun> runs, (string? Model, string Reasoning)? current,
        bool logging, int width, bool isPrivate = false) => BenchLayout(stats, runs, current, logging, width, isPrivate).Content;

    /// <summary>
    /// The tab and the Recent runs rule's line (null without runs), for <c>/bench history</c>'s pane to open on it. Every row is one line,
    /// so the line is the row's index. <paramref name="current"/> marks your setup's grid row; null marks none.
    /// </summary>
    public static (IRenderable Content, int? RecentLine) BenchLayout(IReadOnlyList<ModelStat> stats, IReadOnlyList<BenchRun> runs,
        (string? Model, string Reasoning)? current, bool logging, int width, bool isPrivate = false)
    {
        var rows = BenchRows(stats, runs, current, logging, width, isPrivate);
        int recent = rows.FindIndex(r => r.Recent);
        return (new Rows(rows.Select(r => (IRenderable)new Indented(r.Shown, SystemPromptSummary.BodyIndent))), recent >= 0 ? recent : null);
    }

    /// <summary>The tab's lines with no pane (a redirected console), at the transcript's width.</summary>
    public static IEnumerable<string> BenchLines(IReadOnlyList<ModelStat> stats, IReadOnlyList<BenchRun> runs, (string? Model, string Reasoning)? current,
        bool logging, int width, bool isPrivate = false) =>
        BenchRows(stats, runs, current, logging, width, isPrivate).Select(r => r.Plain);

    private static List<BenchRow> BenchRows(IReadOnlyList<ModelStat> stats, IReadOnlyList<BenchRun> runs, (string? Model, string Reasoning)? current,
        bool logging, int width, bool isPrivate)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(runs);
        width = Math.Max(1, width);
        var rows = new List<BenchRow>();
        void Dim(string text) => rows.Add(new BenchRow(text, new Text(text, Theme.DimText)));
        void Blank() => rows.Add(new BenchRow("", new Text("")));
        void Heading(string heading, bool recent = false) =>
            rows.Add(new BenchRow(SectionRule.Lead + " " + heading, new SectionRule(SectionRule.Markup(heading)), recent));

        var inUse = ModelsRows(stats, logging, width, isPrivate);
        if (stats.Count == 0 && runs.Count == 0)
        {
            foreach (var (text, _, _) in inUse)
            {
                Dim(text);
            }

            Dim(BenchText.NoRuns);
            return rows;
        }

        foreach (string line in TextCells.WrapWords(ModelsCaption, width))
        {
            Dim(line);
        }

        Blank();
        Heading(BenchInUseHeading);
        foreach (var (text, dim, rule) in inUse)
        {
            rows.Add(new BenchRow(text, rule ? new SectionRule(SectionRule.Bare) : new Text(text, dim ? Theme.DimText : Theme.Body)));
        }

        Blank();
        Heading(BenchTestsHeading);
        if (runs.Count == 0)
        {
            Dim(BenchText.NoRuns);
            return rows;
        }

        rows.AddRange(GridRows(BenchGrid.Rows(runs), current, width));
        Blank();
        Heading(BenchRecentHeading, recent: true);
        rows.AddRange(RecentRows(runs, width));
        Dim(BenchRunsSaved(runs.Count));
        return rows;
    }

    /// <summary>The grid: a dim heading row, a row per model and level (yours marked), then the legend; narrow, only the score and last run.</summary>
    private static IEnumerable<BenchRow> GridRows(IReadOnlyList<BenchGridRow> grid, (string? Model, string Reasoning)? current, int width)
    {
        var tests = BenchCatalog.All;
        var headings = tests.Select(t => BenchGridHeading(t.Id)).ToList();
        var scores = grid.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Passed}/{r.Counted}")).ToList();
        int scoreWidth = Math.Max(BenchScoreHeading.Length, scores.Max(s => s.Length));
        int lastWidth = Math.Max(BenchLastRunHeading.Length, "yyyy-MM-dd".Length);
        int tail = BenchGap + scoreWidth + BenchGap + lastWidth;
        int testsWidth = headings.Sum(h => BenchGap + TextCells.Width(h));
        int pointer = TextCells.Width(BenchYouMark);
        bool narrow = pointer + ModelsModelMin + testsWidth + tail > width;
        var labels = grid.Select(r => r.Model + BenchText.LevelSeparator + (r.Reasoning ?? BenchNoLevel)).ToList();
        int labelWidth = Math.Max(ModelsModelMin, Math.Min(labels.Max(TextCells.Width), width - pointer - (narrow ? 0 : testsWidth) - tail));

        static string Pad(string text, int cells) => text + new string(' ', Math.Max(0, cells - TextCells.Width(text)));
        static string Right(string text, int cells) => new string(' ', Math.Max(0, cells - TextCells.Width(text))) + text;
        string gap = new(' ', BenchGap);

        string headPlain = new string(' ', pointer) + Pad("", labelWidth) + (narrow ? "" : string.Concat(headings.Select(h => gap + h)))
            + gap + Right(BenchScoreHeading, scoreWidth) + gap + Pad(BenchLastRunHeading, lastWidth);
        yield return new BenchRow(headPlain, new FittedMarkup(Markup.Escape(headPlain).Insert(0, "[" + Theme.DimText.ToMarkup() + "]") + "[/]"));

        for (int i = 0; i < grid.Count; i++)
        {
            var row = grid[i];
            bool yours = current is { Model: { } model } you
                && row.Model.Equals(model, StringComparison.OrdinalIgnoreCase)
                && row.Reasoning is not null && row.Reasoning.Equals(you.Reasoning, StringComparison.OrdinalIgnoreCase);
            string label = Pad(CutMiddle(labels[i], labelWidth), labelWidth);
            var plain = new System.Text.StringBuilder(yours ? BenchYouMark : new string(' ', pointer)).Append(label);
            var markup = new System.Text.StringBuilder(yours ? "[" + Theme.Accent.ToMarkup() + "]" + Markup.Escape(BenchYouMark) + "[/]" : new string(' ', pointer))
                .Append(yours ? "[" + new Style(Theme.Body.Foreground, decoration: Decoration.Bold).ToMarkup() + "]" + Markup.Escape(label) + "[/]" : Markup.Escape(label));
            if (!narrow)
            {
                for (int t = 0; t < tests.Count; t++)
                {
                    int w = TextCells.Width(headings[t]);
                    int left = (w - 1) / 2;
                    string cell = row.Latest.TryGetValue(tests[t].Id, out var seen) ? BenchText.Mark(seen.Result.Verdict) : " ";
                    var style = seen.Result?.Verdict switch
                    {
                        BenchVerdict.Pass => Theme.GoodText,
                        BenchVerdict.Fail or BenchVerdict.Error => Theme.WarnText,
                        _ => Theme.DimText,
                    };
                    string before = gap + new string(' ', left);
                    string after = new(' ', Math.Max(0, w - left - 1));
                    plain.Append(before).Append(cell).Append(after);
                    markup.Append(before).Append('[').Append(style.ToMarkup()).Append(']').Append(Markup.Escape(cell)).Append("[/]").Append(after);
                }
            }

            string lastRun = row.LastRun.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string rest = gap + Right(scores[i], scoreWidth) + gap + Pad(lastRun, lastWidth);
            plain.Append(rest);
            markup.Append(Markup.Escape(rest));
            yield return new BenchRow(plain.ToString(), new FittedMarkup(markup.ToString()));
        }

        if (!narrow)
        {
            yield return new BenchRow(BenchLegend, new Text(BenchLegend, Theme.DimText));
        }
    }

    /// <summary>The newest <see cref="BenchRecentShown"/> runs, a line each: when, the model, the reasoning, what ran, the tally and the time.</summary>
    private static IEnumerable<BenchRow> RecentRows(IReadOnlyList<BenchRun> runs, int width)
    {
        var shown = runs.Reverse().Take(BenchRecentShown).ToList();
        var cells = shown.Select(run => (
            When: run.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            run.Model,
            Reasoning: run.Reasoning ?? "",
            What: BenchGrid.WhatRan(run),
            Score: string.Create(CultureInfo.InvariantCulture, $"{run.Passed}/{run.Counted}"),
            Time: BenchText.Elapsed(run.Results.Sum(r => r.Seconds)))).ToList();
        int reasoningWidth = cells.Max(c => TextCells.Width(c.Reasoning));
        int whatWidth = cells.Max(c => TextCells.Width(c.What));
        int scoreWidth = cells.Max(c => c.Score.Length);
        int fixedWidth = 16 + BenchGap + BenchGap + reasoningWidth + BenchGap + whatWidth + BenchGap + scoreWidth + BenchGap + cells.Max(c => c.Time.Length);
        int modelWidth = Math.Max(ModelsModelMin, Math.Min(cells.Max(c => TextCells.Width(c.Model)), width - fixedWidth));
        static string Pad(string text, int n) => text + new string(' ', Math.Max(0, n - TextCells.Width(text)));
        string gap = new(' ', BenchGap);
        foreach (var c in cells)
        {
            string line = (c.When + gap + Pad(CutMiddle(c.Model, modelWidth), modelWidth) + gap + Pad(c.Reasoning, reasoningWidth) + gap
                + Pad(c.What, whatWidth) + gap + new string(' ', scoreWidth - c.Score.Length) + c.Score + gap + c.Time).TrimEnd();
            yield return new BenchRow(line, new FittedMarkup(Markup.Escape(line)));
        }
    }
}
