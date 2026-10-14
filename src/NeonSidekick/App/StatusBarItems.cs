using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The setting <c>Status bar</c> (<c>Hint bar</c> until 2026-10-14, when the hint row split into the Status bar and a Hints bar of
/// its own, <see cref="UI.HintsBarMode"/>, the user's ask; 2026-10-12, the user's ask: the other bars' checklist for the hint bar, its
/// sections on or off and in any order): one id per section of the standing row, saved in <see cref="Settings.AppSettingsData.StatusBarItems"/> (null
/// is <see cref="Defaults"/>, the row as it always was; an empty list leaves the row to the alerts and the busy row) and ordered by
/// <see cref="Settings.AppSettingsData.StatusBarOrder"/> (<see cref="ItemOrder"/>, null <see cref="Names"/>' order). The model's name
/// is free in the flow (the user's pick). Working directory (later that day, the user's ask: the tool bar's path on the hint row,
/// after Model, off by default) is free the same way; until 2026-10-13 the two shared the right edge while they ended the checked order.
/// Since 2026-10-13 (the user's ask: any section pinned left or right, the sort free within each side) every section is pinned to a
/// side (<see cref="Sides"/> over <see cref="Settings.AppSettingsData.StatusBarRight"/>; <see cref="RightDefaults"/> the model and the
/// working directory, the row as it was), and the busy row follows the sides too. Later that day (the user's ask: "could the
/// spinner in the hint row also be one of the moveable options? … mandatory and can't be disabled, but it can be moved") the busy
/// row's spinner, its label and clock and the stop hint after them are the <see cref="Spinner"/> section: after Status by default (first until later that day, the user's ask), always
/// on (<see cref="Fixed"/>), on either side in any place, drawn under a reply alone. The whole-row prompts (the drag, an alert, the
/// armed exit, rewind and splash delete) are no sections: they show whatever is checked. Under a reply the checks hold (no strip,
/// queued count, tally or model while off). The Keys section (the arrows' and the <c>/read</c> keys' lead) went to the Hints bar on
/// 2026-10-14, with the armed prompts; a saved <c>keys</c> is dropped as any unknown word. A display setting, so an unknown word is
/// dropped without a warning. Pure.
/// </summary>
public static class StatusBarItems
{
    public const string Spinner = "spinner";   // 2026-10-13, the user's ask: always on, movable
    public const string Status = "status";
    public const string Queued = "queued";
    public const string Timers = "timers";
    public const string Reading = "reading";
    public const string Usage = "usage";
    public const string Model = "model";
    public const string WorkingDirectory = "cwd";   // later on 2026-10-12, the user's ask

    /// <summary>
    /// Every section in the row's own order, the one it always had, the working directory after the model; the spinner second
    /// (2026-10-13; first until later that day, the user's ask: Status first by default, the spinner after it). Pinned.
    /// </summary>
    public static readonly string[] Names = [Status, Spinner, Queued, Timers, Reading, Usage, Model, WorkingDirectory];

    /// <summary>What a profile that never chose shows: every section but the working directory (the user's pick), the row as it was. Pinned.</summary>
    public static readonly string[] Defaults = [Status, Spinner, Queued, Timers, Reading, Usage, Model];

    /// <summary>The sections that are always on (2026-10-13, the user's ask): the spinner, which moves but never goes. Pinned.</summary>
    public static readonly IReadOnlySet<string> Fixed = new HashSet<string>(StringComparer.Ordinal) { Spinner };

    /// <summary>The notice when the checklist is asked to turn a <see cref="Fixed"/> section off (2026-10-13). Pinned.</summary>
    public const string SpinnerFixedNotice = "The spinner is always on: Shift+↑↓ and Shift+←→ move it";

    /// <summary>The sections pinned right by default (2026-10-13): the model and the working directory, the row as it was. Pinned.</summary>
    public static readonly string[] RightDefaults = [Model, WorkingDirectory];

    /// <summary>A section's name on the checklist and in the row's value. Pinned.</summary>
    public static string Title(string id) => id switch
    {
        Spinner => "Spinner",
        Status => "Status",
        Queued => "Queued",
        Timers => "Timers",
        Reading => "Reading",
        Usage => "Usage",
        Model => "Model",
        WorkingDirectory => "Working directory",
        _ => id,
    };

    /// <summary>The checklist's dim note beside a section: what it shows (the user's words since 2026-10-13). Pinned.</summary>
    public static string Describe(string id) => id switch
    {
        Spinner => "turn activity spinner, info and stop hint: always on",
        Status => "status icons: 🔊 🎤 👂 ✋, 🧠, 🏷️…",
        Queued => "queue status when messages are queued",
        Timers => "timer info while timer(s) active",
        Reading => "reading info while reading is active (/read or /echo)",
        Usage => "context and usage info",
        Model => "the model's name and its reasoning mark",
        WorkingDirectory => "the working directory, as the tool bar shows it (/cwd browse)",
        _ => "",
    };

    /// <summary>The checklist's name column: "Working directory" (17) plus two.</summary>
    public const int TitleWidth = 19;

    /// <summary>One row of the checklist: the mark, the name and <see cref="Describe"/> dimmed. Pinned.</summary>
    public static string Label(string id, bool on) =>
        Markup.Escape((on ? "[x] " : "[ ] ") + Title(id).PadRight(TitleWidth)) + Theme.DimMarkup(Describe(id));

    /// <summary>
    /// The sections <paramref name="saved"/> names: <see cref="Defaults"/> when null, the known ids (trimmed, any case) otherwise —
    /// and the <see cref="Fixed"/> ones always (2026-10-13), whatever the list says.
    /// </summary>
    public static IReadOnlySet<string> Resolve(IReadOnlyList<string>? saved)
    {
        if (saved is null)
        {
            return Defaults.ToHashSet(StringComparer.Ordinal);
        }

        // A null in a hand-edited list is skipped: this runs on the pane's tick, where a throw repeats.
        var wanted = saved.OfType<string>().Select(w => w.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return Names.Where(name => wanted.Contains(name) || Fixed.Contains(name)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>What saves for <paramref name="on"/>: null for exactly <see cref="Defaults"/> (so the profile follows a later default), else the ids in <see cref="Names"/> order, empty for none.</summary>
    public static List<string>? Save(IReadOnlySet<string> on)
    {
        ArgumentNullException.ThrowIfNull(on);
        var chosen = Names.Where(on.Contains).ToList();
        return chosen.SequenceEqual(Defaults, StringComparer.Ordinal) ? null : chosen;
    }

    /// <summary>The checklist's and the row's order for <paramref name="saved"/>: every section, <see cref="Names"/>' order for null (<see cref="ItemOrder.Resolve"/>).</summary>
    public static List<string> Order(IReadOnlyList<string>? saved) => ItemOrder.Resolve(saved, Names);

    /// <summary>What saves for <paramref name="order"/>: null for <see cref="Names"/>' own order (<see cref="ItemOrder.Save"/>).</summary>
    public static List<string>? SaveOrder(IReadOnlyList<string> order) => ItemOrder.Save(order, Names);

    /// <summary>The sections <paramref name="saved"/> (<see cref="Settings.AppSettingsData.StatusBarRight"/>) pins right: <see cref="RightDefaults"/> for null.</summary>
    public static IReadOnlySet<string> ResolveRight(IReadOnlyList<string>? saved) => ItemOrder.ResolveRight(saved, Names, RightDefaults);

    /// <summary>What saves for the right group: null for exactly <see cref="RightDefaults"/>.</summary>
    public static List<string>? SaveRight(IEnumerable<string> right) => ItemOrder.SaveRight(right, Names, RightDefaults);

    /// <summary>The row's two groups for the saved <paramref name="order"/> and <paramref name="right"/> (2026-10-13): every section, checked or not.</summary>
    public static ItemOrder.Sided Sides(IReadOnlyList<string>? order, IReadOnlyList<string>? right) => ItemOrder.Split(Order(order), ResolveRight(right));

    /// <summary>
    /// The <c>Status bar</c> row's value: <see cref="DefaultValue"/> for the defaults in the default order and sides, <see cref="AllValue"/>
    /// for every section in them, <see cref="NoneValue"/> with none but the always-on spinner, else the checked names in order — since
    /// 2026-10-13 the left group's, then <see cref="SidesSeparator"/> and the right group's while any is checked there; the spinner
    /// named only away from its own place (later that day): on the left, right after Status or leading the group. Pinned.
    /// </summary>
    public static string Value(IReadOnlyList<string>? saved, IReadOnlyList<string>? order, IReadOnlyList<string>? right = null)
    {
        var on = Resolve(saved);
        if (on.All(Fixed.Contains))
        {
            return NoneValue;
        }

        var sides = Sides(order, right);
        bool defaultOrder = SaveOrder(Order(order)) is null && SaveRight(sides.Right) is null;
        if (defaultOrder && on.SetEquals(Defaults))
        {
            return DefaultValue;
        }

        if (defaultOrder && on.Count == Names.Length)
        {
            return AllValue;
        }

        int spinnerAt = sides.Left.ToList().IndexOf(Spinner);
        bool spinnerHome = spinnerAt == 0 || (spinnerAt == 1 && sides.Left[0] == Status);
        string left = string.Join(", ", sides.Left.Where(id => on.Contains(id) && !(id == Spinner && spinnerHome)).Select(Title));
        string pinned = string.Join(", ", sides.Right.Where(on.Contains).Select(Title));
        return pinned.Length == 0 ? left : left.Length == 0 ? SidesSeparator.TrimStart() + pinned : left + SidesSeparator + pinned;
    }

    /// <summary>Between the left group's names and the right group's in the row's value (2026-10-13): <c>Status, Queued | Usage, Model</c>. Pinned.</summary>
    public const string SidesSeparator = " | ";

    /// <summary>The row's value for the defaults in the default order (later on 2026-10-12; <see cref="AllValue"/> was until then). Pinned.</summary>
    public const string DefaultValue = "default";

    /// <summary>The row's value with every section in the default order. Pinned.</summary>
    public const string AllValue = "all";

    /// <summary>The row's value with none checked. Pinned.</summary>
    public const string NoneValue = "none";
}
