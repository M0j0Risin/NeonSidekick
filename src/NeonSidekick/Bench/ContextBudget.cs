namespace NeonSidekick.Bench;

/// <summary>
/// A context window in tokens turned into a haystack in filler paragraphs (2026-09-28, ported from LLMTester's
/// <c>ContextBudget</c>, the numbers as they are there). There is no tokenizer: a paragraph is costed high on purpose, so a
/// haystack comes out a little short rather than over the window and refused. The retrieval tests stay well under the
/// window (retrieval plateaus long before the rated limit); the saturation test fills it.
/// </summary>
public static class ContextBudget
{
    /// <summary>One paragraph's cost for the retrieval tests. Observed from ~33 (a Qwen build) to ~51 (gemma4); an overestimate only shortens the haystack.</summary>
    public const int TokensPerUnit = 44;

    /// <summary>One paragraph's cost for the saturation test: higher, since there an underestimate overshoots the window and reads as "window too small".</summary>
    public const int SaturationTokensPerUnit = 52;

    /// <summary>Room kept for the answer; the app sends no max tokens, so the server decides and this is its allowance.</summary>
    public const int OutputReservation = 1024;

    /// <summary>The frame around the haystack: the system message, <c>Context:</c> and the question.</summary>
    public const int FrameOverhead = 256;

    /// <summary>The largest share of the window a retrieval prompt may take.</summary>
    public const double MaxPromptWindowFraction = 0.5;

    /// <summary>The smallest haystack at which the long-context tests still mean something.</summary>
    public const int MinUnits = 100;

    /// <summary>The largest retrieval haystack (~66k tokens), and the one built when the window is unknown.</summary>
    public const int MaxUnits = 1500;

    /// <summary>The haystack when the window is unknown: LLMTester's default.</summary>
    public const int FallbackUnits = MaxUnits;

    /// <summary>The retrieval haystack for a window: the smaller of half the window and what the answer and frame leave, <see cref="MinUnits"/> to <see cref="MaxUnits"/>.</summary>
    public static int UnitsFor(int contextWindowTokens)
    {
        int windowShare = (int)(contextWindowTokens * MaxPromptWindowFraction);
        int answerRoom = contextWindowTokens - OutputReservation - FrameOverhead;
        int budget = Math.Min(windowShare, answerRoom);
        return budget <= 0 ? MinUnits : Math.Clamp(budget / TokensPerUnit, MinUnits, MaxUnits);
    }

    /// <summary>The saturation haystack for a window: everything the answer and frame leave, no share and no cap, never under <see cref="MinUnits"/>.</summary>
    public static int SaturationUnits(int contextWindowTokens)
    {
        int room = contextWindowTokens - OutputReservation - FrameOverhead;
        return room <= 0 ? MinUnits : Math.Max(room / SaturationTokensPerUnit, MinUnits);
    }

    /// <summary>The tokens a retrieval prompt of <paramref name="units"/> is estimated at: the paragraphs and the frame, no answer.</summary>
    public static int PromptEstimate(int units) => units * TokensPerUnit + FrameOverhead;

    /// <summary>The tokens a saturation prompt of <paramref name="units"/> is estimated at: the paragraphs at the saturation cost and the frame, no answer.</summary>
    public static int SaturationPromptEstimate(int units) => units * SaturationTokensPerUnit + FrameOverhead;

    /// <summary>The tokens a saturation prompt of <paramref name="units"/> is estimated at, the answer's room included: what its pass line reports.</summary>
    public static int SaturationEstimate(int units) => units * SaturationTokensPerUnit + FrameOverhead + OutputReservation;
}
