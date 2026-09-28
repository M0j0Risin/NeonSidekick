namespace NeonSidekick.Bench;

/// <summary>
/// The long-context tests' haystack (2026-09-28, ported from LLMTester's <c>HaystackBuilder</c>): one low-entropy paragraph
/// repeated, a line each, with the needles put in at fractional depths.
/// </summary>
public static class Haystack
{
    /// <summary>The filler, as LLMTester (and the Python examples before it) have it.</summary>
    public const string FillerParagraph =
        "The server rack hummed quietly in the background, a steady drone " +
        "that masked the frantic typing of the engineers. System logs scrolled " +
        "by at an unreadable pace, highlighting the sheer volume of network " +
        "traffic passing through the nodes. Security protocols remained active. ";

    /// <summary>
    /// <paramref name="units"/> paragraphs with each needle inserted at its depth (0 the top, 1 the bottom), a line each.
    /// The deepest needle goes in first, so a shallower one's index still counts filler only — LLMTester's multi-hop order.
    /// </summary>
    public static string Build(int units, params (string Needle, double Depth)[] needles)
    {
        var parts = new List<string>(units + needles.Length);
        for (int i = 0; i < units; i++)
        {
            parts.Add(FillerParagraph);
        }

        foreach (var (needle, depth) in needles.OrderByDescending(n => n.Depth))
        {
            int index = (int)Math.Round(Math.Clamp(depth, 0.0, 1.0) * (units - 1));
            parts.Insert(Math.Clamp(index, 0, parts.Count), needle);
        }

        return string.Join("\n", parts);
    }
}
