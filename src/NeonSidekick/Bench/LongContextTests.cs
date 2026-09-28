using System.Globalization;

namespace NeonSidekick.Bench;

/// <summary>
/// Needle in a Haystack (2026-09-28, from LLMTester's <c>NeedleInHaystackTest</c>): one fact in the middle of a haystack
/// sized to the window (<see cref="BenchContext.HaystackUnits"/>); the answer must carry the password.
/// </summary>
public sealed class NeedleTest : BenchTest
{
    public const string Needle = "The secret password to override the mainframe is: QUANTUM_BANANA_77.";

    public const string Question = "What is the secret password to override the mainframe? Format your answer as a single word (the secret password).";

    private const string ExpectedToken = "QUANTUM_BANANA_77";

    internal const string SystemPrompt = "You are a precise data extraction assistant.";

    public override string Id => "needle";

    public override string Name => "Needle in a Haystack (Long Context)";

    public override BenchCategory Category => BenchCategory.LongContext;

    public override string Expected => ExpectedToken;

    public override string Description => "Hides one fact mid-way through a context sized to the model's window; the model must retrieve it.";

    public override BenchRequest BuildRequest(BenchContext context)
    {
        string haystack = Haystack.Build(context.HaystackUnits, (Needle, BenchContext.NeedleDepth));
        return new([System(SystemPrompt), User(string.Join("\n", "Context:", haystack, "", "Question: " + Question))]);
    }

    public override BenchJudgement Judge(string answer, BenchContext context)
    {
        string depth = (BenchContext.NeedleDepth * 100).ToString("0", CultureInfo.InvariantCulture);
        return answer.Contains(ExpectedToken, StringComparison.OrdinalIgnoreCase)
            ? BenchJudgement.Pass("Retrieved '" + ExpectedToken + "' at " + depth + "% depth.")
            : BenchJudgement.Fail("Answer did not contain '" + ExpectedToken + "' (needle depth " + depth + "%).");
    }
}

/// <summary>
/// Multi-Hop Synthesis (2026-09-28, from LLMTester's <c>MultiHopSynthTest</c>): two facts at 10% and 90% depth that only
/// together give the password (<c>mangoes1998</c>); the answer must carry <c>mango</c> and <c>1998</c>.
/// </summary>
public sealed class MultiHopTest : BenchTest
{
    public const string Needle1 = "The mainframe override password is the CEO's favorite fruit followed by their graduation year.";

    public const string Needle2 = "The CEO graduated in 1998 and is fiercely allergic to everything except mangoes.";

    public const string Question = "What is the exact mainframe override password? Combine the clues to find the specific string. Format your answer as a single word (the mainframe override password)";

    private static readonly string[] RequiredFragments = ["mango", "1998"];

    public override string Id => "multihop";

    public override string Name => "Multi-Hop Synthesis (Long Context)";

    public override BenchCategory Category => BenchCategory.LongContext;

    public override string Expected => "mangoes1998";

    public override string Description => "Two facts hidden at different depths; the model must combine them to derive the password (mangoes1998).";

    public override BenchRequest BuildRequest(BenchContext context)
    {
        string haystack = Haystack.Build(context.HaystackUnits, (Needle1, 0.10), (Needle2, 0.90));
        return new([System(NeedleTest.SystemPrompt), User("Context:\n" + haystack + "\n\nQuestion: " + Question)]);
    }

    public override BenchJudgement Judge(string answer, BenchContext context)
    {
        var missing = RequiredFragments.Where(f => !answer.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        return missing.Count == 0
            ? BenchJudgement.Pass("Answer combined both clues (mangoes + 1998).")
            : BenchJudgement.Fail("Answer did not combine both clues; missing: " + string.Join(", ", missing));
    }
}

/// <summary>
/// Context Saturation (2026-09-28, from LLMTester's <c>FullContextWindowTest</c>): a prompt filling the whole window
/// (<see cref="BenchContext.SaturationUnits"/>); the server taking it is the pass, the answer is not read. A refusal (a
/// context-length 400) is the transport's error, so an <see cref="BenchVerdict.Error"/>: the usable window is smaller than
/// the rated one. The heaviest test by far, so the catalog runs it last.
/// </summary>
public sealed class SaturationTest : BenchTest
{
    public const string ProbeNeedle = "Context saturation probe: no fact to retrieve.";

    internal const string SystemPrompt = "You are a context saturation probe target.";

    internal const string Task = "Task: This context is a full-context-window saturation probe. Respond with exactly one word: OK";

    public override string Id => "saturation";

    public override string Name => "Context Saturation (Long Context)";

    public override BenchCategory Category => BenchCategory.LongContext;

    public override string Expected => "the server accepts a prompt filling the whole context window";

    public override string Description => "Fills the model's entire context window and checks the server accepts the request; a refusal means the usable window is smaller than the rated one.";

    public override BenchRequest BuildRequest(BenchContext context)
    {
        string haystack = Haystack.Build(context.SaturationUnits, (ProbeNeedle, 0.5));
        return new([System(SystemPrompt), User(string.Join("\n", "Context:", haystack, "", Task))]);
    }

    public override BenchJudgement Judge(string answer, BenchContext context) =>
        BenchJudgement.Pass(string.Create(CultureInfo.InvariantCulture,
            $"Accepted a prompt sized to fill the context window ({ContextBudget.SaturationEstimate(context.SaturationUnits):N0} estimated tokens)."));
}
