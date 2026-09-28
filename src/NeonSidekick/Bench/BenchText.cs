using System.Globalization;
using System.Text;

namespace NeonSidekick.Bench;

/// <summary>
/// The words for <c>/test</c> (2026-09-28): the usage, the spinner, one line per finished test, the run's markdown table,
/// the listing and the saved runs. Pure statics; the screen and the headless REPL say the same things.
/// </summary>
public static class BenchText
{
    /// <summary>The argument words, as the usage names them.</summary>
    public const string Usage = "Usage: /test <test | reasoning | structured | long | all> | history — /test alone lists the tests";

    /// <summary>The reason on a structured-output test skipped over the Claude API.</summary>
    public const string SkippedOnClaudeApi = "skipped: the Claude API takes no response_format";

    /// <summary>The notice when <c>/test history</c> finds nothing saved.</summary>
    public const string NoRuns = "No saved test runs yet: /test <name> runs one.";

    /// <summary>The line under the listing: how to run them.</summary>
    public const string ListingHint = "Run one with /test <id>, a group with /test reasoning | structured | long, or every test with /test all; /test history lists the saved runs.";

    /// <summary>The argument list's note on a group word: <c>every reasoning test (4)</c>.</summary>
    public static string GroupNote(BenchCategory category)
    {
        string group = category switch
        {
            BenchCategory.Reasoning => "reasoning",
            BenchCategory.StructuredOutput => "structured-output",
            _ => "long-context",
        };
        return string.Create(CultureInfo.InvariantCulture, $"every {group} test ({BenchCatalog.All.Count(t => t.Category == category)})");
    }

    /// <summary>The argument list's note on <c>all</c>.</summary>
    public static string AllNote => string.Create(CultureInfo.InvariantCulture, $"every test ({BenchCatalog.All.Count}), the context saturation last");

    /// <summary>The argument list's note on <c>history</c>.</summary>
    public const string HistoryNote = "the saved runs";

    /// <summary>The error for a word that names no test, group or verb.</summary>
    public static string UnknownTest(string word) => "No test named '" + word + "'. " + Usage;

    /// <summary>
    /// The spinner while a test runs: <c>test 2/9 · mind</c>. The id, as <c>/test &lt;id&gt;</c> takes it, not the long
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
    /// One finished test: <c>✓ mind — Answer tracked Alice's belief … · 1.2 s · 14 tok · 11.6 tok/s</c>. The figures the
    /// server did not report are left out; a skip is its reason alone.
    /// </summary>
    public static string ResultLine(BenchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var line = new StringBuilder();
        line.Append(Mark(result.Verdict)).Append(' ').Append(result.Test).Append(" — ").Append(result.Reason);
        if (result.Verdict != BenchVerdict.Skipped)
        {
            line.Append(" · ").Append(Seconds(result.Seconds));
            if (result.CompletionTokens is { } output)
            {
                line.Append(" · ").Append(output.ToString("N0", CultureInfo.InvariantCulture)).Append(" tok");
            }

            if (result.TokensPerSecond is { } rate)
            {
                line.Append(" · ").Append(rate.ToString("0.0", CultureInfo.InvariantCulture)).Append(" tok/s");
            }
        }

        return line.ToString();
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
    /// What the long-context tests were sized to: each prompt's estimated tokens first, its paragraphs after —
    /// <c>Context window 128,768 tokens: needle/multi-hop prompt ~64.6k tokens (1,463 paragraphs), saturation prompt ~127.7k
    /// tokens (2,451 paragraphs).</c> (reworded 2026-09-28, the user's call: a bare <c>saturation 2,451</c> read as tokens) —
    /// or the fallback when the window is unknown.
    /// </summary>
    public static string ContextLine(BenchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ContextWindowTokens is { } window
            ? string.Create(CultureInfo.InvariantCulture,
                $"Context window {window:N0} tokens: needle/multi-hop prompt ~{Thousands(ContextBudget.PromptEstimate(context.HaystackUnits))} tokens ({context.HaystackUnits:N0} paragraphs), saturation prompt ~{Thousands(ContextBudget.SaturationPromptEstimate(context.SaturationUnits))} tokens ({context.SaturationUnits:N0} paragraphs).")
            : string.Create(CultureInfo.InvariantCulture,
                $"Context window unknown: every long-context prompt is ~{Thousands(ContextBudget.PromptEstimate(ContextBudget.FallbackUnits))} tokens ({ContextBudget.FallbackUnits:N0} paragraphs).");
    }

    /// <summary><c>64.6k</c>: tokens in thousands, one decimal.</summary>
    private static string Thousands(int tokens) => (tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";

    /// <summary>The line after ESC: how far the run got.</summary>
    public static string Cancelled(int done, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"Test run cancelled after {done}/{count}.");

    /// <summary>The line after a save that failed.</summary>
    public const string NotSaved = "The run could not be saved to tests.json; see the log.";

    /// <summary>
    /// The run's table, markdown: a row per test (verdict, time, prompt and output tokens, tok/s), then the tally —
    /// <c>**3/4 passed** · qwen3-30b</c> — the skips left out of the count.
    /// </summary>
    public static string Summary(BenchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var md = new StringBuilder();
        md.Append("| Test | Verdict | Time | Prompt | Output | tok/s |\n");
        md.Append("|---|---|---:|---:|---:|---:|\n");
        foreach (var result in run.Results)
        {
            string name = BenchCatalog.Find(result.Test)?.Name ?? result.Test;
            bool skipped = result.Verdict == BenchVerdict.Skipped;
            md.Append("| ").Append(name)
                .Append(" | ").Append(Mark(result.Verdict)).Append(' ').Append(Word(result.Verdict))
                .Append(" | ").Append(skipped ? "" : Seconds(result.Seconds))
                .Append(" | ").Append(Count(result.PromptTokens))
                .Append(" | ").Append(Count(result.CompletionTokens))
                .Append(" | ").Append(result.TokensPerSecond is { } rate ? rate.ToString("0.0", CultureInfo.InvariantCulture) : "")
                .Append(" |\n");
        }

        md.Append('\n').Append(Tally(run));
        return md.ToString();
    }

    /// <summary>
    /// <c>**3/4 passed** · qwen3-30b · reasoning high · temperature 0.6</c>: the model, then the settings the run went out with
    /// (<see cref="Settings"/>), <c> · cancelled</c> after a stopped run.
    /// </summary>
    public static string Tally(BenchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        string settings = Settings(run);
        return string.Create(CultureInfo.InvariantCulture, $"**{run.Passed}/{run.Counted} passed** · {run.Model}")
            + (settings.Length > 0 ? " · " + settings : "")
            + (run.Cancelled ? " · cancelled" : "");
    }

    /// <summary>What <see cref="BenchRun.Reasoning"/> says when the requests carried no reasoning effort: the server chose.</summary>
    public const string ReasoningDefault = "default";

    /// <summary>What the history and the tally say for sampling left to the server (an empty <see cref="BenchRun.Sampling"/>).</summary>
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
    /// A run's settings in words: <c>reasoning high · temperature 0.6</c>, <c>reasoning none · server sampling</c>; empty
    /// for a run saved before they were (both null).
    /// </summary>
    public static string Settings(BenchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var parts = new List<string>(2);
        if (run.Reasoning is { } reasoning)
        {
            parts.Add("reasoning " + reasoning);
        }

        if (run.Sampling is { } sampling)
        {
            parts.Add(sampling.Length > 0 ? sampling : ServerSampling);
        }

        return string.Join(Llm.LlmSampling.DescribeSeparator, parts);
    }

    /// <summary>
    /// <c>/test</c> alone: every test with its id, group and what it expects, and its latest verdict against
    /// <paramref name="model"/> from the saved runs, markdown.
    /// </summary>
    public static string Listing(IReadOnlyDictionary<string, (BenchResult Result, DateTimeOffset At)> latest, string? model)
    {
        ArgumentNullException.ThrowIfNull(latest);
        var md = new StringBuilder();
        md.Append("| Id | Test | Expects | Last").Append(model is null ? "" : " (" + model + ")").Append(" |\n");
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

    /// <summary>How many saved runs <c>/test history</c> shows, newest first.</summary>
    public const int HistoryShown = 15;

    /// <summary>
    /// <c>/test history</c>: the newest <see cref="HistoryShown"/> runs, markdown — when, the model, the reasoning and
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
                .Append(" | ").Append(string.Create(CultureInfo.InvariantCulture, $"{run.Passed}/{run.Counted}")).Append(run.Cancelled ? " (cancelled)" : "")
                .Append(" | ").Append(tests)
                .Append(" |\n");
        }

        md.Append('\n').Append(string.Create(CultureInfo.InvariantCulture, $"{runs.Count} run{(runs.Count == 1 ? "" : "s")} saved in {filePath}"));
        return md.ToString();
    }

    private static string Seconds(double seconds) => seconds.ToString(seconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";

    private static string Count(long? count) => count is { } n ? n.ToString("N0", CultureInfo.InvariantCulture) : "";

    /// <summary>A table cell's text, its pipes escaped.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
