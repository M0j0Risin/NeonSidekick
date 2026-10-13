namespace NeonSidekick.App;

/// <summary>
/// The verbs the turn spinner reads instead of <see cref="ChatScreen.ThinkingLabel"/> and
/// <see cref="TurnStages.WritingLabel"/> while <see cref="Settings.AppSettingsData.LlmUseFunVerbs"/>
/// is on: one is picked for every thinking and writing stage (<see cref="TurnStages"/>; a running
/// tool keeps its name). Lowercase like the other spinner labels; the words are the user's (the list of
/// 2026-09-15, replacing the first one whole), in the user's order. Since 2026-10-12 (the user's ask: themes' and voices' way)
/// they live in <c>assets/verbs/verbs.txt</c>, one verb per line, embedded as <see cref="ResourceName"/> and read once
/// (<see cref="Parse"/>): edit the file and the next build has the list. An edited file cannot break the spinner: an empty
/// list is the plain stage word, a one-verb list that verb every time.
/// </summary>
internal static class ThinkingVerbs
{
    /// <summary>The manifest name <c>assets/verbs/verbs.txt</c> is embedded under.</summary>
    public const string ResourceName = "verbs/verbs.txt";

    public static readonly string[] All = ReadEmbedded();

    private static string[] ReadEmbedded() => Parse(EmbeddedText.Read(ResourceName, "The fun verbs"));

    /// <summary>
    /// The verbs <paramref name="text"/> holds: one per line (CRLF or LF), each trimmed, blank lines skipped, a repeat dropped
    /// (the first kept), in the file's order. Nothing else is checked: the file is the user's. Pure.
    /// </summary>
    public static string[] Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EmbeddedText.Lines(text).Distinct(StringComparer.Ordinal).ToArray();   // Distinct keeps the first of a repeat, in order
    }

    /// <summary>One of <see cref="All"/>, drawn from <paramref name="random"/>; null when the list is empty (the stage's own word then).</summary>
    public static string? Pick(Random random) => Pick(All, random);

    /// <summary><see cref="Pick(Random)"/> over <paramref name="verbs"/> (2026-10-12: the tests' lists, <see cref="All"/> untouched).</summary>
    internal static string? Pick(IReadOnlyList<string> verbs, Random random)
    {
        ArgumentNullException.ThrowIfNull(verbs);
        ArgumentNullException.ThrowIfNull(random);
        return verbs.Count == 0 ? null : verbs[random.Next(verbs.Count)];
    }

    /// <summary>
    /// <see cref="Pick(Random)"/> but never <paramref name="except"/> (the verb shown just before —
    /// a stage change under the fun verbs reads as one only when the word changes): drawn again
    /// while it matches. Null excludes nothing. A list of one verb gives it whatever <paramref name="except"/> says (2026-10-12: an
    /// edited file of one line would draw forever); an empty one null.
    /// </summary>
    public static string? Pick(Random random, string? except) => Pick(All, random, except);

    /// <summary><see cref="Pick(Random, string?)"/> over <paramref name="verbs"/>.</summary>
    internal static string? Pick(IReadOnlyList<string> verbs, Random random, string? except)
    {
        string? verb = Pick(verbs, random);
        if (verbs.Count < 2)
        {
            return verb;
        }

        while (string.Equals(verb, except, StringComparison.Ordinal))
        {
            verb = Pick(verbs, random);
        }

        return verb;
    }
}
