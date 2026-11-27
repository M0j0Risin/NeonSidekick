using System.Globalization;
using System.Text;

namespace NeonSidekick.Bench;

/// <summary>
/// The words for <c>/bench</c> (2026-09-28): the usage, the spinner, one line per finished test, the run's markdown table,
/// the listing and the saved runs. Pure statics; the screen and the headless REPL say the same things.
/// </summary>
public static class BenchText
{
    /// <summary>The <c>--log</c> category of every <c>/bench</c> line, the runs' and the saved file's (2026-11-26, "Test" until then).</summary>
    public const string LogCategory = "Bench";

    /// <summary>The argument words, as the usage names them.</summary>
    public const string Usage = "Usage: /bench <test | reasoning | structured | long | all> | history — /bench alone lists the tests";

    /// <summary>The reason on a structured-output test skipped over the Anthropic API.</summary>
    public const string SkippedOnClaudeApi = "skipped: the Anthropic API takes no response_format";

    /// <summary>The notice when <c>/bench history</c> finds nothing saved.</summary>
    public const string NoRuns = "No saved test runs yet: /bench <name> runs one.";

    /// <summary>The line under the listing: how to run them.</summary>
    public const string ListingHint = "Run one with /bench <id>, a group with /bench reasoning | structured | long, or every test with /bench all; /bench history lists the saved runs.";

    /// <summary>
    /// The argument list's note on a group word: <c>every reasoning test (4)</c>. <see cref="BenchCategory.WindowCapacity"/> has no group
    /// word (2026-11-27), so it never reaches here.
    /// </summary>
    public static string GroupNote(BenchCategory category)
    {
        string group = category switch
        {
            BenchCategory.Reasoning => "reasoning",
            BenchCategory.StructuredOutput => "structured-output",
            BenchCategory.LongContext => "long-context",
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
        return string.Create(CultureInfo.InvariantCulture, $"every {group} test ({BenchCatalog.All.Count(t => t.Category == category)})");
    }

    /// <summary>
    /// A group's name, as a test's <see cref="BenchTest.Name"/> brackets it and the run's table heads its rows (2026-11-27):
    /// <c>Reasoning</c>, <c>Structured Output</c>, <c>Long Context</c>, <c>Window Capacity</c>.
    /// </summary>
    public static string GroupName(BenchCategory category) => category switch
    {
        BenchCategory.Reasoning => "Reasoning",
        BenchCategory.StructuredOutput => "Structured Output",
        BenchCategory.LongContext => "Long Context",
        BenchCategory.WindowCapacity => "Window Capacity",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    /// <summary>The argument list's note on <c>all</c>.</summary>
    public static string AllNote => string.Create(CultureInfo.InvariantCulture, $"every test ({BenchCatalog.All.Count}), the context saturation last");

    /// <summary>The argument list's note on <c>history</c>.</summary>
    public const string HistoryNote = "the saved runs";

    /// <summary>The error for a word that names no test, group or verb.</summary>
    public static string UnknownTest(string word) => "No test named '" + word + "'. " + Usage;

    /// <summary>
    /// The spinner while a test runs: <c>test 2/9 · mind</c>. The id, as <c>/bench &lt;id&gt;</c> takes it, not the long
    /// name (2026-09-28, the user's call: "Needle in a Haystack (Long Context)" crowded the hint row); the result lines
    /// and the table keep the name.
    /// </summary>
    public static string Label(int index, int count, BenchTest test) =>
        string.Create(CultureInfo.InvariantCulture, $"test {index}/{count} · {test.Id}");

    /// <summary>The mark before a verdict.</summary>
    public static string Mark(BenchVerdict verdict) => verdict switch
    {
        BenchVerdict.Pass => "✓",
        BenchVerdict.Fail => "✗",
        BenchVerdict.Error => "!",
        _ => "–",
    };

    /// <summary>The verdict's word in the table.</summary>
    public static string Word(BenchVerdict verdict) => verdict switch
    {
        BenchVerdict.Pass => "pass",
        BenchVerdict.Fail => "fail",
        BenchVerdict.Error => "error",
        _ => "skipped",
    };

    /// <summary>
    /// One finished test: <c>✓ mind — Answer tracked Alice's belief …</c>, the mark, the id and the reason. The time, output and
    /// tok/s left the line on 2026-11-27 (the user's call: the table under the run holds them).
    /// </summary>
    public static string ResultLine(BenchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Mark(result.Verdict) + " " + result.Test + " — " + result.Reason;
    }

    /// <summary>The answer under a failed test's line, cut to one line: <c>  answered: brit</c>. Null when there is none to show.</summary>
    public static string? AnswerLine(BenchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Verdict != BenchVerdict.Fail || result.Answer.Length == 0)
        {
            return null;
        }

        string flat = string.Join(' ', result.Answer.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return "  answered: " + (flat.Length <= 160 ? flat : flat[..160] + "…");
    }

    /// <summary>
    /// What the window-sized tests of the run were sized to, each prompt's estimated tokens (reworded 2026-09-28, the user's call: a
    /// bare <c>saturation 2,451</c> read as tokens, so the estimates say tokens): <c>Window 151,427 tokens · needle and multihop ~66.3k
    /// each · saturation ~150.4k</c>. Cut on 2026-11-27 (the user's call: the line was long): the paragraph counts went, and only the
    /// tests in the run are named. Unknown: <c>Window unknown · each long prompt ~X · saturation skipped</c>, the same rule.
    /// </summary>
    public static string ContextLine(BenchContext context, IReadOnlyList<BenchTest> tests)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tests);
        var parts = new List<string>(3);
        var longOnes = tests.Where(t => t.Category == BenchCategory.LongContext).Select(t => t.Id).ToList();
        bool saturation = tests.Any(t => t.Category == BenchCategory.WindowCapacity);
        if (context.ContextWindowTokens is { } window)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Window {window:N0} tokens"));
            if (longOnes.Count > 0)
            {
                parts.Add(string.Join(" and ", longOnes) + " ~" + Thousands(ContextBudget.PromptEstimate(context.HaystackUnits)) + (longOnes.Count > 1 ? " each" : ""));
            }

            if (saturation)
            {
                parts.Add("saturation ~" + Thousands(ContextBudget.SaturationPromptEstimate(context.SaturationUnits)));
            }
        }
        else
        {
            parts.Add(WindowUnknown);
            if (longOnes.Count > 0)
            {
                parts.Add("each long prompt ~" + Thousands(ContextBudget.PromptEstimate(ContextBudget.FallbackUnits)));
            }

            if (saturation)
            {
                parts.Add(SaturationSkippedPart);
            }
        }

        return string.Join(Llm.LlmSampling.DescribeSeparator, parts);
    }

    /// <summary>The window line's start when the window is unknown.</summary>
    public const string WindowUnknown = "Window unknown";

    /// <summary>The window line's part for saturation without a window: the test is skipped (<see cref="SaturationNoWindow"/>).</summary>
    public const string SaturationSkippedPart = "saturation skipped";

    /// <summary>The saturation test's reason when the window is unknown (2026-11-27): skipped, no request sent.</summary>
    public const string SaturationNoWindow = "Context window unknown, so there's no window to fill.";

    /// <summary>The saturation test's pass with the server's own count (2026-11-27): <c>The server took 144,443 tokens, 95% of the window.</c></summary>
    public static string SaturationTook(long tokens, int window) =>
        string.Create(CultureInfo.InvariantCulture, $"The server took {tokens:N0} tokens, {(int)Math.Round(tokens * 100.0 / window)}% of the window.");

    /// <summary><c>64.6k</c>: tokens in thousands, one decimal.</summary>
    private static string Thousands(int tokens) => (tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";

    /// <summary>The line after ESC: how far the run got.</summary>
    public static string Cancelled(int done, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"Test run canceled after {done}/{count}.");

    /// <summary>The line after a save that failed.</summary>
    public const string NotSaved = "The run could not be saved to tests.json; see the log.";

    /// <summary>
    /// The run's table, markdown: a bold heading row as the group changes (2026-11-27, the user's pick), then a row per test by its title
    /// (verdict, time, prompt and output tokens, tok/s) — not indented: the pipe-table parser trims a cell's no-break spaces, so the
    /// heading rows alone show the groups; then <see cref="ResultSummary"/>, a blank line and
    /// <see cref="RunFacts"/> against <paramref name="previous"/> (<see cref="BenchHistory.Previous(BenchRun)"/>) — the skips left out of the count.
    /// </summary>
    public static string Summary(BenchRun run, BenchRun? previous)
    {
        ArgumentNullException.ThrowIfNull(run);
        var md = new StringBuilder();
        md.Append("| Test | Verdict | Time | Prompt | Output | tok/s |\n");
        md.Append("|---|---|---:|---:|---:|---:|\n");
        BenchCategory? group = null;
        foreach (var result in run.Results)
        {
            var test = BenchCatalog.Find(result.Test);
            if (test is not null && test.Category != group)
            {
                group = test.Category;
                md.Append("| **").Append(GroupName(test.Category)).Append("** | | | | | |\n");
            }

            string name = test?.Title ?? result.Test;
            bool skipped = result.Verdict == BenchVerdict.Skipped;
            md.Append("| ").Append(name)
                .Append(" | ").Append(Mark(result.Verdict)).Append(' ').Append(Word(result.Verdict))
                .Append(" | ").Append(skipped ? "" : Seconds(result.Seconds))
                .Append(" | ").Append(Count(result.PromptTokens))
                .Append(" | ").Append(Count(result.CompletionTokens))
                .Append(" | ").Append(result.TokensPerSecond is { } rate ? rate.ToString("0.0", CultureInfo.InvariantCulture) : "")
                .Append(" |\n");
        }

        md.Append('\n').Append(ResultSummary(run)).Append("\n\n").Append(RunFacts(run, previous));
        return md.ToString();
    }

    /// <summary>
    /// The run's result (2026-11-27, the user's call: the one tally line said too much): <c>**9/9 passed** in 1m 48s</c>, the time its
    /// results' seconds summed, <c> · canceled</c> after a stopped run.
    /// </summary>
    public static string ResultSummary(BenchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return string.Create(CultureInfo.InvariantCulture, $"**{run.Passed}/{run.Counted} passed** in {Elapsed(run.Results.Sum(r => r.Seconds))}")
            + (run.Cancelled ? " · canceled" : "");
    }

    /// <summary>A run's time: <c>49 s</c> under a minute, else <c>1m 48s</c>.</summary>
    public static string Elapsed(double seconds)
    {
        int whole = (int)Math.Round(Math.Max(0, seconds));
        return whole < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{whole} s")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60}m {whole % 60:00}s");
    }

    /// <summary>The run facts' labels (2026-11-27).</summary>
    public const string ModelLabel = "Model";

    public const string ReasoningLabel = "Reasoning";

    public const string SamplingLabel = "Sampling";

    public const string LastRunLabel = "Last run";

    /// <summary>What the run facts say for sampling left to the server (an empty <see cref="BenchRun.Sampling"/>).</summary>
    public const string SamplingServerDefaults = "server defaults";

    /// <summary>
    /// The run's facts under its result, a line each (2026-11-27): <c>Model</c> (the id as saved), <c>Reasoning</c>, <c>Sampling</c> and
    /// <c>Last run</c> (<see cref="ChangeLine"/> against <paramref name="previous"/>), the labels padded with no-break spaces so the
    /// values line up; a line whose value the run lacks is left out.
    /// </summary>
    public static string RunFacts(BenchRun run, BenchRun? previous)
    {
        ArgumentNullException.ThrowIfNull(run);
        var lines = new List<(string Label, string Value)> { (ModelLabel, run.Model) };
        if (run.Reasoning is { } reasoning)
        {
            lines.Add((ReasoningLabel, reasoning));
        }

        if (run.Sampling is { } sampling)
        {
            lines.Add((SamplingLabel, sampling.Length > 0 ? sampling : SamplingServerDefaults));
        }

        if (previous is not null && ChangeLine(previous, run) is { } change)
        {
            lines.Add((LastRunLabel, change));
        }

        int width = lines.Max(l => l.Label.Length) + 2;
        return string.Join('\n', lines.Select(l => l.Label + new string('\u00A0', width - l.Label.Length) + l.Value));
    }

    /// <summary>
    /// What changed since <paramref name="previous"/> (2026-11-27): <c>2026-11-20: 8/9 · now passing: multihop · now failing: json</c>,
    /// or <c>· no change</c>. Only the tests both graded count (skips ignored), a fail or an error to a pass and back; null when they
    /// share none.
    /// </summary>
    public static string? ChangeLine(BenchRun previous, BenchRun run)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(run);
        var before = previous.Results.Where(r => r.Verdict != BenchVerdict.Skipped)
            .GroupBy(r => r.Test, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Verdict, StringComparer.OrdinalIgnoreCase);
        var passing = new List<string>();
        var failing = new List<string>();
        bool shared = false;
        foreach (var result in run.Results.Where(r => r.Verdict != BenchVerdict.Skipped))
        {
            if (!before.TryGetValue(result.Test, out var was))
            {
                continue;
            }

            shared = true;
            if (was != BenchVerdict.Pass && result.Verdict == BenchVerdict.Pass)
            {
                passing.Add(result.Test);
            }
            else if (was == BenchVerdict.Pass && result.Verdict != BenchVerdict.Pass)
            {
                failing.Add(result.Test);
            }
        }

        if (!shared)
        {
            return null;
        }

        var line = new StringBuilder(previous.At.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(": ").Append(string.Create(CultureInfo.InvariantCulture, $"{previous.Passed}/{previous.Counted}"));
        if (passing.Count == 0 && failing.Count == 0)
        {
            line.Append(" · no change");
        }

        if (passing.Count > 0)
        {
            line.Append(" · now passing: ").Append(string.Join(", ", passing));
        }

        if (failing.Count > 0)
        {
            line.Append(" · now failing: ").Append(string.Join(", ", failing));
        }

        return line.ToString();
    }

    /// <summary>What <see cref="BenchRun.Reasoning"/> says when the requests carried no reasoning effort: the server chose.</summary>
    public const string ReasoningDefault = "default";

    /// <summary>What <c>/bench history</c>'s table says for sampling left to the server (an empty <see cref="BenchRun.Sampling"/>).</summary>
    public const string ServerSampling = "server sampling";

    /// <summary>The saved word for the requests' reasoning: the level's name, or <see cref="ReasoningDefault"/> when none was sent.</summary>
    public static string ReasoningWord(Microsoft.Extensions.AI.ReasoningEffort? effort) =>
        effort is { } level ? Llm.ReasoningLevel.Name(level) : ReasoningDefault;

    /// <summary>
    /// The saved words for the requests' sampling: each set field in its wire name (<see cref="Llm.LlmSampling.Describe"/>'s
    /// form), then each extra-body field with its JSON — <c>temperature 0.6 · top_k 20 · seed=7</c> — so the run says what
    /// was sent, not only which keys; empty for none.
    /// </summary>
    public static string SamplingWords(Llm.LlmSampling? sampling)
    {
        if (sampling is null)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var field in Llm.SamplingField.All)
        {
            if (sampling.Value(field.Key) is { } value)
            {
                parts.Add(field.Wire + " " + Llm.SamplingField.Format(value));
            }
        }

        foreach (var (name, value) in sampling.Extra.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            parts.Add(name + "=" + value.GetRawText());
        }

        return string.Join(Llm.LlmSampling.DescribeSeparator, parts);
    }

    /// <summary>
    /// <c>/bench</c> alone: every test with its id, group and what it expects, and its latest verdict against
    /// <paramref name="model"/> from the saved runs, markdown. With <paramref name="reasoning"/> (2026-11-27) the verdicts are that
    /// level's, the one the next run would use, and the column's head names it: <c>Last (qwen · xhigh)</c>.
    /// </summary>
    public static string Listing(IReadOnlyDictionary<string, (BenchResult Result, DateTimeOffset At)> latest, string? model, string? reasoning = null)
    {
        ArgumentNullException.ThrowIfNull(latest);
        var md = new StringBuilder();
        md.Append("| Id | Test | Expects | Last").Append(model is null ? "" : " (" + model + (reasoning is null ? "" : LevelSeparator + reasoning) + ")").Append(" |\n");
        md.Append("|---|---|---|---|\n");
        foreach (var test in BenchCatalog.All)
        {
            string last = latest.TryGetValue(test.Id, out var seen)
                ? Mark(seen.Result.Verdict) + " " + Word(seen.Result.Verdict) + " " + seen.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "";
            md.Append("| ").Append(test.Id).Append(" | ").Append(test.Name).Append(" | ").Append(Cell(test.Expected)).Append(" | ").Append(last).Append(" |\n");
        }

        md.Append('\n').Append(ListingHint);
        return md.ToString();
    }

    /// <summary>How many saved runs <c>/bench history</c> shows, newest first.</summary>
    public const int HistoryShown = 15;

    /// <summary>
    /// <c>/bench history</c>: the newest <see cref="HistoryShown"/> runs, markdown — when, the model, the reasoning and
    /// sampling it ran with (blank for a run saved before they were), the tally and each test's mark by id — then where the
    /// file is.
    /// </summary>
    public static string History(IReadOnlyList<BenchRun> runs, string filePath)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var md = new StringBuilder();
        md.Append("| When | Model | Reasoning | Sampling | Passed | Tests |\n");
        md.Append("|---|---|---|---|---:|---|\n");
        foreach (var run in runs.Reverse().Take(HistoryShown))
        {
            string tests = string.Join(' ', run.Results.Select(r => Mark(r.Verdict) + r.Test));
            md.Append("| ").Append(run.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append(" | ").Append(Cell(run.Model))
                .Append(" | ").Append(run.Reasoning ?? "")
                .Append(" | ").Append(run.Sampling is { } sampling ? Cell(sampling.Length > 0 ? sampling : ServerSampling) : "")
                .Append(" | ").Append(string.Create(CultureInfo.InvariantCulture, $"{run.Passed}/{run.Counted}")).Append(run.Cancelled ? " (canceled)" : "")
                .Append(" | ").Append(tests)
                .Append(" |\n");
        }

        md.Append('\n').Append(string.Create(CultureInfo.InvariantCulture, $"{runs.Count} run{(runs.Count == 1 ? "" : "s")} saved in {filePath}"));
        return md.ToString();
    }

    /// <summary>Between a model and its reasoning level, in the listing's head and the Bench tab's grid rows (2026-11-27).</summary>
    public const string LevelSeparator = " · ";

    private static string Seconds(double seconds) => seconds.ToString(seconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";

    private static string Count(long? count) => count is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) : "";

    /// <summary>A table cell's text, its pipes escaped.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
