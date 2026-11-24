using System.Globalization;
using NeonSidekick.Sessions;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

// ── The Models tab (2026-11-21, feature idea #7: a scorecard from real use) ─────────────────────────────────────────────────────────────
// One row per model over the saved turns (SessionStore.ModelStats), the seven figures right-aligned, each column as wide as its widest figure
// (2026-11-22, the review's #94), the right-most dropped when even the narrowest model column wouldn't fit; the model column takes what is left, a long id cut in the middle so its size and quant suffix stay, and written whole on a
// dim line under the table (the pane scrolls text; it has no row to select). The PC's alone: the page's usage card stays the Statistics.

public static partial class UsageText
{
    /// <summary>The sixth tab's title and index; <c>/usage models</c> opens the pane on it.</summary>
    public const string ModelsTabTitle = "Models";

    public const int ModelsTabIndex = 5;

    /// <summary>The word <c>/usage models</c> takes. Pinned.</summary>
    public const string ModelsWord = "models";

    /// <summary>The argument list's note beside <see cref="ModelsWord"/>.</summary>
    public const string ModelsWordNote = "each model's figures from the saved sessions";

    /// <summary>The tab with nothing saved. Pinned.</summary>
    public const string ModelsEmpty = "No saved turns yet. These figures come from saved sessions.";

    /// <summary>The tab when every saved turn is a command's (2026-11-23): "No saved turns yet" would contradict the side-turn note's count.</summary>
    public const string ModelsNoModelTurns = "No turns a model answered are saved yet.";

    /// <summary>The label over the cut ids written whole (2026-11-23, the user's ask: a bare id at the end read as stray).</summary>
    public const string ModelsFullNamesLabel = "Full names:";

    /// <summary>
    /// The note under the table for the turns a command answered (2026-11-23, the user's ask): <c>/cmd</c>, <c>/advisor</c> and <c>/claude</c> are
    /// left out of the table and counted here, each command as saved, in the table's order.
    /// </summary>
    public static string ModelsSideTurnsNote(int turns, IReadOnlyList<string> commands) =>
        Plural(turns, "turn", "turns") + " answered by a command (" + string.Join(", ", commands) + (turns == 1 ? ") isn't counted." : ") aren't counted.");

    /// <summary>Added to <see cref="ModelsEmpty"/> while Session logging is off.</summary>
    public const string ModelsLoggingOff = "Session logging is off, so new turns aren't saved.";

    /// <summary>The caption above the table, word-wrapped to the pane (2026-11-22, the review's #65); Errors are tool calls' (#85).</summary>
    public const string ModelsCaption = "From the saved sessions: purged or rewound turns drop out. Errors are the share of tool calls that failed; " +
        "a turn that failed outright isn't counted. They follow the task as much as the model, so compare like with like.";

    /// <summary>The note under the table for a model with fewer than <see cref="ModelsFewTurns"/> turns, which shows dim.</summary>
    public static string ModelsFewNote => "Dim rows: fewer than " + ModelsFewTurns.ToString(CultureInfo.InvariantCulture) + " turns, too few to judge by.";

    /// <summary>The note under the table once a turn from before the per-turn model counted under its session's model.</summary>
    public const string ModelsUntaggedNote = "Turns saved without a model count under their session's model.";   // no version named (2026-11-22, #140, #71)

    /// <summary>A row whose session stored no model.</summary>
    public const string ModelsNoModel = "(no model)";

    /// <summary>Fewer turns than this show dim.</summary>
    public const int ModelsFewTurns = 20;

    /// <summary>The figure columns' headings, each column right-aligned, two cells apart, as wide as its widest figure (#94).</summary>
    internal static readonly string[] ModelsHeadings = ["Turns", "Sessions", "Calls/turn", "Errors", "Canceled", "Out/turn", "tok/s"];

    /// <summary>The model column's heading, and its narrowest.</summary>
    public const string ModelsModelHeading = "Model";

    public const int ModelsModelMin = 12;

    private const int ModelsGap = 2;

    /// <summary>Each figure column's width: its heading's or its widest figure's (2026-11-22, the review's #94: 100,000 turns stay in line).</summary>
    internal static int[] FigureWidths(IReadOnlyList<ModelStat> stats) =>
        ModelsHeadings.Select((heading, i) => Math.Max(TextCells.Width(heading), stats.Select(s => TextCells.Width(Figures(s).ElementAt(i))).DefaultIfEmpty(0).Max())).ToArray();

    /// <summary>The cells every figure column takes, gaps included.</summary>
    internal static int FiguresWidth(IReadOnlyList<ModelStat> stats) => FigureWidths(stats).Sum(w => w + ModelsGap);

    /// <summary>The pane's tab: the caption, the table and its notes, or <see cref="ModelsEmpty"/>; <paramref name="width"/> the cells the body has, its indent taken off already (#109).</summary>
    public static IRenderable ModelsTab(IReadOnlyList<ModelStat> stats, bool logging, int width)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var lines = ModelsRows(stats, logging, width);
        var parts = new List<IRenderable>();
        foreach (var (text, dim, rule) in lines)
        {
            // A rule row (2026-11-23) is the pane's bare rule; a redirected console's is the run of ─ the row carries.
            parts.Add(new Indented(rule ? new SectionRule(SectionRule.Bare) : new Text(text, dim ? Theme.DimText : Theme.Body), SystemPromptSummary.BodyIndent));
        }

        return new Rows(parts);
    }

    /// <summary>The tab's lines with no pane (a redirected console), at the transcript's width.</summary>
    public static IEnumerable<string> ModelsLines(IReadOnlyList<ModelStat> stats, bool logging, int width) =>
        ModelsRows(stats, logging, width).Select(r => r.Text);

    /// <summary>
    /// The tab's lines, which show dim and which are rules: the caption, the heading, a rule, a row a model (dim under <see cref="ModelsFewTurns"/>),
    /// a rule, then the notes and each cut id in full under <see cref="ModelsFullNamesLabel"/>. Every line fits <paramref name="width"/> cells
    /// (2026-11-22, the review's #65): the caption, notes and ids word-wrapped, the table's right-most columns dropped when the model column couldn't
    /// keep <see cref="ModelsModelMin"/>. Since 2026-11-23 (the user's asks) the turns a command answered (<see cref="SessionText.IsSideTurnModel"/>)
    /// leave the table for one note, and the models go by turns, most first, then by last use. Pure.
    /// </summary>
    public static IReadOnlyList<(string Text, bool Dim, bool Rule)> ModelsRows(IReadOnlyList<ModelStat> stats, bool logging, int width)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var rows = new List<(string, bool, bool)>();
        void Wrapped(string text, int indent = 0) =>
            rows.AddRange(TextCells.WrapWords(text, Math.Max(1, width - indent)).Select(line => (new string(' ', indent) + line, true, false)));
        // The side note's models in the table's order too (2026-11-24, the last-7 review's #49): turns, most first, then last use, then the name.
        var sides = stats.Where(s => SessionText.IsSideTurnModel(s.Model))
            .OrderByDescending(s => s.Turns).ThenByDescending(s => s.LastUsed).ThenBy(s => s.Model, StringComparer.Ordinal).ToList();
        var models = stats.Where(s => !SessionText.IsSideTurnModel(s.Model))
            .OrderByDescending(s => s.Turns).ThenByDescending(s => s.LastUsed).ThenBy(s => s.Model, StringComparer.Ordinal).ToList();
        void SideNote()
        {
            if (sides.Count > 0)
            {
                Wrapped(ModelsSideTurnsNote(sides.Sum(s => s.Turns), sides.Select(s => s.Model).ToList()));
            }
        }

        if (models.Count == 0)
        {
            Wrapped(stats.Count == 0 ? ModelsEmpty : ModelsNoModelTurns);
            SideNote();
            if (!logging)
            {
                Wrapped(ModelsLoggingOff);
            }

            return rows;
        }

        stats = models;

        int[] widths = FigureWidths(stats);
        int columns = widths.Length;
        while (columns > 0 && ModelsModelMin + widths.Take(columns).Sum(w => w + ModelsGap) > width)
        {
            columns--;
        }

        widths = widths[..columns];
        int modelWidth = Math.Max(ModelsModelMin, width - widths.Sum(w => w + ModelsGap));
        Wrapped(ModelsCaption);
        rows.Add(("", false, false));
        string heading = Row(ModelsModelHeading, ModelsHeadings, widths, modelWidth);
        rows.Add((heading, true, false));
        // A rule under the heading and another before the notes (2026-11-23, the user's ask), as wide as the table.
        var rule = (new string(ScreenPane.RuleGlyph, TextCells.Width(heading)), true, true);
        rows.Add(rule);
        var cut = new List<string>();
        foreach (var stat in stats)
        {
            string name = stat.Model.Length == 0 ? ModelsNoModel : stat.Model;
            string shown = CutMiddle(name, modelWidth);
            if (shown != name)
            {
                cut.Add(name);
            }

            rows.Add((Row(shown, Figures(stat), widths, modelWidth), stat.Turns < ModelsFewTurns, false));
        }

        rows.Add(rule);
        if (stats.Any(s => s.Turns < ModelsFewTurns))
        {
            Wrapped(ModelsFewNote);
        }

        SideNote();

        if (stats.Any(s => s.Untagged > 0))
        {
            Wrapped(ModelsUntaggedNote);
        }

        if (!logging)
        {
            Wrapped(ModelsLoggingOff);
        }

        if (cut.Count > 0)
        {
            Wrapped(ModelsFullNamesLabel);
            foreach (string name in cut)
            {
                Wrapped(name, indent: 2);
            }
        }

        return rows;
    }

    /// <summary>A model's seven figures in <see cref="ModelsHeadings"/>' order.</summary>
    internal static IEnumerable<string> Figures(ModelStat stat) =>
    [
        Count(stat.Turns),
        Count(stat.Sessions),
        stat.CallsPerTurn.ToString("0.0", CultureInfo.InvariantCulture),
        stat.ErrorRate is { } errors ? Percent(errors) : Dash,
        Percent(stat.CancelledRate),
        Math.Round(stat.OutputPerTurn, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture),
        stat.TokensPerSecond is { } speed ? speed.ToString("0.0", CultureInfo.InvariantCulture) : Dash,
    ];

    private const string Dash = "—";

    private static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(double share) => Math.Round(share * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Row(string model, IEnumerable<string> figures, int[] widths, int modelWidth)
    {
        string head = model + new string(' ', Math.Max(0, modelWidth - TextCells.Width(model)));
        return head + string.Concat(figures.Zip(widths, (f, w) => new string(' ', ModelsGap) + new string(' ', Math.Max(0, w - TextCells.Width(f))) + f));
    }

    /// <summary><paramref name="text"/> in <paramref name="cells"/>, cut in the middle with <c>…</c> when longer, so both ends stay. Pure.</summary>
    public static string CutMiddle(string text, int cells)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (TextCells.Width(text) <= cells)
        {
            return text;
        }

        if (cells <= 1)
        {
            return TextCells.Cut("…", cells);
        }

        int tail = (cells - 1) / 2;
        int head = cells - 1 - tail;
        string start = TextCells.Cut(text, head + 1);   // the head's cells and its … (the text is longer, so it always cuts)
        // The tail's cells from the end, cluster by cluster.
        int end = text.Length;
        int taken = 0;
        while (end > 0)
        {
            int before = TextCells.ElementLengthBefore(text, end);
            int w = TextCells.Width(text.Substring(end - before, before));
            if (taken + w > tail)
            {
                break;
            }

            taken += w;
            end -= before;
        }

        return start + text[end..];
    }
}
