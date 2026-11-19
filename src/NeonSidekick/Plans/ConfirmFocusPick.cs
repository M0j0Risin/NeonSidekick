namespace NeonSidekick.Plans;

/// <summary>
/// Which focus a confirming pass looks hardest at when it is picked by what changed (2026-11-18, Phase 3 item 3, focus selection, the user's call:
/// behind <see cref="Settings.AppSettingsData.ConfirmFocusByChange"/>). The rotation it replaces restarted at the first focus in almost every review,
/// so a plan's confirming pass was <i>order</i> 30 times in 38 and <i>assumptions</i> and <i>size</i> never ran. Pure.
/// </summary>
public static class ConfirmFocusPick
{
    /// <summary>
    /// The focus to look at, an index into <paramref name="used"/>: among the focuses with a weight above 0 (<paramref name="weights"/>, what the
    /// change touched; a shorter list reads as 0 past its end), the ones used least this review, then the heaviest of those; with nothing weighed,
    /// the least used. Ties go by the focuses' own order. So a second confirming pass on the same change takes the next area it touched.
    /// </summary>
    /// <param name="weights">What the change touched, per focus.</param>
    /// <param name="used">How many confirming passes of this review looked at each focus; its length is the number of focuses.</param>
    public static int Pick(IReadOnlyList<int> weights, IReadOnlyList<int> used)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(used);
        if (used.Count == 0)
        {
            throw new ArgumentException("No focuses to pick from.", nameof(used));
        }

        int best = -1;
        for (int i = 0; i < used.Count; i++)
        {
            int weight = Weight(weights, i);
            if (weight <= 0)
            {
                continue;
            }

            if (best < 0 || used[i] < used[best] || (used[i] == used[best] && weight > Weight(weights, best)))
            {
                best = i;
            }
        }

        if (best >= 0)
        {
            return best;
        }

        best = 0;
        for (int i = 1; i < used.Count; i++)
        {
            if (used[i] < used[best])
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>Whether any focus has a weight above 0: a pick by change, not the least used.</summary>
    public static bool AnyWeighed(IReadOnlyList<int> weights) => weights is not null && weights.Any(w => w > 0);

    private static int Weight(IReadOnlyList<int> weights, int index) => index < weights.Count ? weights[index] : 0;
}
