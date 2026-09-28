using Microsoft.Extensions.AI;

namespace NeonSidekick.Bench;

/// <summary>A <c>/test</c> test's group (2026-09-28): the argument that runs every test in it, and the order they run in.</summary>
public enum BenchCategory
{
    Reasoning,
    StructuredOutput,
    LongContext,
}

/// <summary>What a test's run came to: the answer met its criteria, did not, could not be asked (the transport's error), or was not asked at all.</summary>
public enum BenchVerdict
{
    Pass,
    Fail,
    Error,

    /// <summary>Not sent: the structured-output tests over the Claude API, which takes no <c>response_format</c> (2026-09-28).</summary>
    Skipped,
}

/// <summary>
/// What a test is built from beyond its own constants (2026-09-28): the connected model's context window, which sizes the
/// long-context tests' haystack (<see cref="ContextBudget"/>); null while unknown, and the haystack is then
/// <see cref="ContextBudget.FallbackUnits"/>. The sampling is the profile's, as the chat's is (the user's call), so it is
/// not here.
/// </summary>
public sealed record BenchContext(int? ContextWindowTokens)
{
    /// <summary>The needle's depth in the one-needle haystack: the middle, as LLMTester fixes it.</summary>
    public const double NeedleDepth = 0.5;

    /// <summary>The haystack the retrieval tests build: half the window at most (<see cref="ContextBudget.UnitsFor"/>), else the fallback.</summary>
    public int HaystackUnits => ContextWindowTokens is { } window ? ContextBudget.UnitsFor(window) : ContextBudget.FallbackUnits;

    /// <summary>The saturation test's haystack: the whole window less the answer and the frame (<see cref="ContextBudget.SaturationUnits"/>), else the fallback.</summary>
    public int SaturationUnits => ContextWindowTokens is { } window ? ContextBudget.SaturationUnits(window) : ContextBudget.FallbackUnits;
}

/// <summary>The one request a test sends: its messages (a system message first when it has one) and the <c>response_format</c> it asks for, if any.</summary>
public sealed record BenchRequest(IReadOnlyList<ChatMessage> Messages, ChatResponseFormat? Format = null);

/// <summary>A judge's verdict on an answer and the one line that says why.</summary>
public readonly record struct BenchJudgement(BenchVerdict Verdict, string Reason)
{
    public static BenchJudgement Pass(string reason) => new(BenchVerdict.Pass, reason);

    public static BenchJudgement Fail(string reason) => new(BenchVerdict.Fail, reason);
}

/// <summary>
/// One <c>/test</c> test (2026-09-28, the user's ask: LLMTester's nine tests run against the connected model), ported from
/// LLMTester's <c>LlmTest</c> with its prompts and judges as they are there — the two copies may drift, the user's call.
/// Pure: <see cref="BuildRequest"/> and <see cref="Judge"/> touch nothing, so each is tested without a client;
/// <see cref="BenchRunner"/> sends the request and times it.
/// </summary>
public abstract class BenchTest
{
    /// <summary>The short word <c>/test</c> takes (<c>needle</c>, <c>mind</c>…), lower case.</summary>
    public abstract string Id { get; }

    /// <summary>The name LLMTester shows, its group in brackets: <c>Needle in a Haystack (Long Context)</c>.</summary>
    public abstract string Name { get; }

    public abstract BenchCategory Category { get; }

    /// <summary>What a passing answer looks like, for the listing.</summary>
    public abstract string Expected { get; }

    /// <summary>One line on what the test checks.</summary>
    public abstract string Description { get; }

    /// <summary>Whether the test needs the server to honour <c>response_format</c>: the structured-output ones.</summary>
    public bool NeedsResponseFormat => Category == BenchCategory.StructuredOutput;

    public abstract BenchRequest BuildRequest(BenchContext context);

    /// <summary>The verdict on <paramref name="answer"/>, the reply's text with any thinking already filtered out.</summary>
    public abstract BenchJudgement Judge(string answer, BenchContext context);

    /// <summary>The first <paramref name="max"/> characters of <paramref name="text"/>, an ellipsis after a cut: what a failing judge quotes.</summary>
    protected static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    protected static ChatMessage System(string text) => new(ChatRole.System, text);

    protected static ChatMessage User(string text) => new(ChatRole.User, text);
}
