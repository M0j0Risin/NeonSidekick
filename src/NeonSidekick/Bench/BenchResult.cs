namespace NeonSidekick.Bench;

/// <summary>
/// One test's outcome (2026-09-28): the verdict and its reason, the answer as it came, the request's wall time and the
/// usage the server reported. Also the saved shape in <c>tests.json</c> (<see cref="BenchHistory"/>), hence the plain
/// settable properties.
/// </summary>
public sealed class BenchResult
{
    /// <summary>The test's id (<see cref="BenchTest.Id"/>).</summary>
    public string Test { get; set; } = "";

    public BenchVerdict Verdict { get; set; }

    public string Reason { get; set; } = "";

    /// <summary>The reply's text, thinking filtered out; empty for an error or a skip.</summary>
    public string Answer { get; set; } = "";

    /// <summary>From the request sent to the stream's end, in seconds; 0 for a skip.</summary>
    public double Seconds { get; set; }

    public long? PromptTokens { get; set; }

    public long? CompletionTokens { get; set; }

    public long? ReasoningTokens { get; set; }

    /// <summary>Whether <see cref="ReasoningTokens"/> is the app's estimate, not the server's count (2026-09-29, <c>LLM reasoning estimate</c>).</summary>
    public bool ReasoningEstimated { get; set; }

    /// <summary>Completion tokens per second of streaming (<see cref="Llm.TokenUsage.TokensPerSecond"/>): the decode speed, not LLMTester's end-to-end rate.</summary>
    public double? TokensPerSecond { get; set; }
}

/// <summary>One <c>/test</c> run as <c>tests.json</c> keeps it (2026-09-28): when, against what, and each test's result in the run's order.</summary>
public sealed class BenchRun
{
    public DateTimeOffset At { get; set; }

    /// <summary>The model id as the server reported it.</summary>
    public string Model { get; set; } = "";

    /// <summary>The endpoint's base URL.</summary>
    public string Server { get; set; } = "";

    /// <summary>The context window the long-context tests were sized to; null when it was unknown.</summary>
    public int? ContextWindow { get; set; }

    /// <summary>
    /// The reasoning the requests went out with (2026-09-28, the user's ask): a level's name (<c>none</c> … <c>xhigh</c>),
    /// <see cref="BenchText.ReasoningDefault"/> when none was sent; null in a run saved before the field was.
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// The sampling the requests went out with (2026-09-28, the user's ask): <c>temperature 0.6 · top_k 20 · seed=7</c>
    /// (<see cref="BenchText.SamplingWords"/>), empty for the server's defaults; null in a run saved before the field was.
    /// </summary>
    public string? Sampling { get; set; }

    /// <summary>True when ESC stopped the run: <see cref="Results"/> holds the tests that finished.</summary>
    public bool Cancelled { get; set; }

    public List<BenchResult> Results { get; set; } = new();

    /// <summary>How many results passed.</summary>
    public int Passed => Results.Count(r => r.Verdict == BenchVerdict.Pass);

    /// <summary>How many results were graded or failed to be asked — every result but a skip.</summary>
    public int Counted => Results.Count(r => r.Verdict != BenchVerdict.Skipped);
}

/// <summary>The file: the runs, oldest first.</summary>
public sealed class BenchHistoryFile
{
    public List<BenchRun> Runs { get; set; } = new();
}
