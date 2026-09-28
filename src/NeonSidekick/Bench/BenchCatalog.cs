namespace NeonSidekick.Bench;

/// <summary>
/// The nine <c>/test</c> tests and the words that pick them (2026-09-28). The order is the run's: the reasoning tests (one
/// small prompt each), the structured-output ones, then the long-context ones, the saturation test last as the heaviest —
/// so <c>/test all</c> shows the quick verdicts first and ESC before the last loses least.
/// </summary>
public static class BenchCatalog
{
    /// <summary>Every test, in the run's order.</summary>
    public static readonly IReadOnlyList<BenchTest> All =
    [
        new ComplexGridTest(),
        new SyntheticRuleTest(),
        new TheoryOfMindTest(),
        new SycophancyTest(),
        new NestedJsonTest(),
        new StateTrackingTest(),
        new NeedleTest(),
        new MultiHopTest(),
        new SaturationTest(),
    ];

    /// <summary>The word that runs every test.</summary>
    public const string AllWord = "all";

    /// <summary>The word that lists the saved runs instead of running anything.</summary>
    public const string HistoryWord = "history";

    /// <summary>The group words, as <c>/test</c> takes them.</summary>
    public static readonly IReadOnlyList<(string Word, BenchCategory Category)> CategoryWords =
    [
        ("reasoning", BenchCategory.Reasoning),
        ("structured", BenchCategory.StructuredOutput),
        ("long", BenchCategory.LongContext),
    ];

    /// <summary>
    /// The tests <paramref name="word"/> names, in the run's order: a test's id, a group word, or <see cref="AllWord"/>,
    /// matched ignoring case; empty for anything else.
    /// </summary>
    public static IReadOnlyList<BenchTest> Resolve(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        string key = word.Trim();
        if (key.Equals(AllWord, StringComparison.OrdinalIgnoreCase))
        {
            return All;
        }

        foreach (var (group, category) in CategoryWords)
        {
            if (key.Equals(group, StringComparison.OrdinalIgnoreCase))
            {
                return All.Where(t => t.Category == category).ToList();
            }
        }

        return All.Where(t => t.Id.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The test whose id is <paramref name="id"/>, ignoring case; null for none.</summary>
    public static BenchTest? Find(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
