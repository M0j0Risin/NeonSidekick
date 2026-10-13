using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The setting <c>Hint bar</c> (2026-10-12, the user's ask: the other bars' checklist for the hint bar, its sections on or off and
/// in any order): one id per section of the standing hint row, saved in <see cref="Settings.AppSettingsData.HintBarItems"/> (null
/// is every one, the row as it always was; an empty list leaves the row to the alerts and the busy row) and ordered by
/// <see cref="Settings.AppSettingsData.HintBarOrder"/> (<see cref="ItemOrder"/>, null <see cref="Names"/>' order). The model's name
/// is free in the flow (the user's pick): last among the checked sections it keeps the row's right edge, as it always had
/// (<see cref="ModelPinned"/>); anywhere else it is drawn in its place. The whole-row prompts (the drag, an alert, the armed exit,
/// rewind and splash delete) and the busy row's spinner, label and stop hint are no sections: they show whatever is checked. Under a
/// reply the checks hold (no strip, queued count, tally or model while off) and the busy row keeps its own order. A display
/// setting, so an unknown word is dropped without a warning. Pure.
/// </summary>
public static class HintBarItems
{
    public const string Status = "status";
    public const string Queued = "queued";
    public const string Keys = "keys";
    public const string Timers = "timers";
    public const string Reading = "reading";
    public const string Usage = "usage";
    public const string Model = "model";

    /// <summary>Every section in the row's own order, the one it always had. Pinned.</summary>
    public static readonly string[] Names = [Status, Queued, Keys, Timers, Reading, Usage, Model];

    /// <summary>A section's name on the checklist and in the row's value. Pinned.</summary>
    public static string Title(string id) => id switch
    {
        Status => "Status",
        Queued => "Queued",
        Keys => "Keys",
        Timers => "Timers",
        Reading => "Reading",
        Usage => "Usage",
        Model => "Model",
        _ => id,
    };

    /// <summary>The checklist's dim note beside a section: what it shows. Pinned.</summary>
    public static string Describe(string id) => id switch
    {
        Status => "the switches and jobs at the start: 🔊 🎤 👂 ✋, 🧠, 🏷️…",
        Queued => "the lines waiting to be sent (/queue)",
        Keys => "the arrows' keys over the splash or the pictures, /read's keys",
        Timers => "the timers while any run",
        Reading => "/read's reading",
        Usage => "the token tally (/usage), while no timer or reading stands",
        Model => "the model's name and its reasoning mark; last, at the right edge",
        _ => "",
    };

    /// <summary>The checklist's name column: "Reading" (7) plus three.</summary>
    public const int TitleWidth = 10;

    /// <summary>One row of the checklist: the mark, the name and <see cref="Describe"/> dimmed. Pinned.</summary>
    public static string Label(string id, bool on) =>
        Markup.Escape((on ? "[x] " : "[ ] ") + Title(id).PadRight(TitleWidth)) + Theme.DimMarkup(Describe(id));

    /// <summary>The sections <paramref name="saved"/> names: every one when null, the known ids (trimmed, any case) otherwise.</summary>
    public static IReadOnlySet<string> Resolve(IReadOnlyList<string>? saved)
    {
        if (saved is null)
        {
            return Names.ToHashSet(StringComparer.Ordinal);
        }

        // A null in a hand-edited list is skipped: this runs on the pane's tick, where a throw repeats.
        var wanted = saved.OfType<string>().Select(w => w.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return Names.Where(wanted.Contains).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>What saves for <paramref name="on"/>: null for every section (so a later one shows too), else the ids in <see cref="Names"/> order, empty for none.</summary>
    public static List<string>? Save(IReadOnlySet<string> on)
    {
        ArgumentNullException.ThrowIfNull(on);
        var chosen = Names.Where(on.Contains).ToList();
        return chosen.Count == Names.Length ? null : chosen;
    }

    /// <summary>The checklist's and the row's order for <paramref name="saved"/>: every section, <see cref="Names"/>' order for null (<see cref="ItemOrder.Resolve"/>).</summary>
    public static List<string> Order(IReadOnlyList<string>? saved) => ItemOrder.Resolve(saved, Names);

    /// <summary>What saves for <paramref name="order"/>: null for <see cref="Names"/>' own order (<see cref="ItemOrder.Save"/>).</summary>
    public static List<string>? SaveOrder(IReadOnlyList<string> order) => ItemOrder.Save(order, Names);

    /// <summary>
    /// Whether the model keeps the row's right edge: it is checked and no checked section comes after it in <paramref name="order"/>
    /// (the default, the row as it always was). Elsewhere it is drawn in the flow at its place. Pure.
    /// </summary>
    public static bool ModelPinned(IReadOnlyList<string> order, IReadOnlySet<string> on)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(on);
        return on.Contains(Model) && order.LastOrDefault(on.Contains) == Model;
    }

    /// <summary>The <c>Hint bar</c> row's value: <see cref="AllValue"/> with every section in the default order, <see cref="NoneValue"/> with none, else the checked names in order. Pinned.</summary>
    public static string Value(IReadOnlyList<string>? saved, IReadOnlyList<string>? order)
    {
        var on = Resolve(saved);
        if (on.Count == 0)
        {
            return NoneValue;
        }

        var ordered = Order(order);
        return on.Count == Names.Length && SaveOrder(ordered) is null ? AllValue : string.Join(", ", ordered.Where(on.Contains).Select(Title));
    }

    /// <summary>The row's value with every section in the default order. Pinned.</summary>
    public const string AllValue = "all";

    /// <summary>The row's value with none checked. Pinned.</summary>
    public const string NoneValue = "none";
}
