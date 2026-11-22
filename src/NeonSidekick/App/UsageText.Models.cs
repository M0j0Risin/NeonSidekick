using System.Globalization;
using NeonSidekick.Sessions;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

// ── The Models tab (2026-11-21, feature idea #7: a scorecard from real use) ─────────────────────────────────────────────────────────────
// One row per model over the saved turns (SessionStore.ModelStats), the seven figures right-aligned at fixed widths as the Statistics tab's
// numbers are; the model column takes what is left, a long id cut in the middle so its size and quant suffix stay, and written whole on a
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

    /// <summary>Added to <see cref="ModelsEmpty"/> while Session logging is off.</summary>
    public const string ModelsLoggingOff = "Session logging is off, so new turns aren't saved.";

    /// <summary>The caption above the table.</summary>
    public const string ModelsCaption = "From the saved sessions: purged or rewound turns drop out. Errors follow the task as much as the model, so compare like with like.";

    /// <summary>The note under the table for a model with fewer than <see cref="ModelsFewTurns"/> turns, which shows dim.</summary>
    public static string ModelsFewNote => "Dim rows: fewer than " + ModelsFewTurns.ToString(CultureInfo.InvariantCulture) + " turns, too few to judge by.";

    /// <summary>The note under the table once a turn from before the per-turn model counted under its session's model.</summary>
    public const string ModelsUntaggedNote = "Turns saved before this version count under their session's model.";

    /// <summary>A row whose session stored no model.</summary>
    public const string ModelsNoModel = "(no model)";

    /// <summary>Fewer turns than this show dim.</summary>
    public const int ModelsFewTurns = 20;

    /// <summary>The figure columns: heading and width, each right-aligned, two cells apart.</summary>
    internal static readonly (string Heading, int Width)[] ModelsColumns =
    [
        ("Turns", 6), ("Sessions", 8), ("Calls/turn", 10), ("Errors", 7), ("Cancelled", 9), ("Out/turn", 8), ("tok/s", 6),
    ];

    /// <summary>The model column's heading, and its narrowest: the table fits 80 columns and up (<see cref="ModelsFiguresWidth"/> plus this).</summary>
    public const string ModelsModelHeading = "Model";

    public const int ModelsModelMin = 12;

    private const int ModelsGap = 2;

    /// <summary>The cells the figure columns take, gaps included.</summary>
    internal static int ModelsFiguresWidth => ModelsColumns.Sum(c => c.Width + ModelsGap);

    /// <summary>The pane's tab: the caption, the table and its notes, or <see cref="ModelsEmpty"/>; <paramref name="width"/> the cells the body has.</summary>
    public static IRenderable ModelsTab(IReadOnlyList<ModelStat> stats, bool logging, int width)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var lines = ModelsRows(stats, logging, width - SystemPromptSummary.BodyIndent);
        var parts = new List<IRenderable>();
        foreach (var (text, dim) in lines)
        {
            parts.Add(new Indented(new Text(text, dim ? Theme.DimText : Theme.Body), SystemPromptSummary.BodyIndent));
        }

        return new Rows(parts);
    }

    /// <summary>The tab's lines with no pane (a redirected console), at the transcript's width.</summary>
    public static IEnumerable<string> ModelsLines(IReadOnlyList<ModelStat> stats, bool logging, int width) =>
        ModelsRows(stats, logging, width).Select(r => r.Text);

    /// <summary>
    /// The tab's lines and which show dim: the caption, the heading, a row a model (dim under <see cref="ModelsFewTurns"/>), then the notes and
    /// each cut id in full. Each line fits <paramref name="width"/> cells (the model column never narrower than <see cref="ModelsModelMin"/>). Pure.
    /// </summary>
    public static IReadOnlyList<(string Text, bool Dim)> ModelsRows(IReadOnlyList<ModelStat> stats, bool logging, int width)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (stats.Count == 0)
        {
            return logging ? [(ModelsEmpty, true)] : [(ModelsEmpty, true), (ModelsLoggingOff, true)];
        }

        int modelWidth = Math.Max(ModelsModelMin, width - ModelsFiguresWidth);
        var rows = new List<(string, bool)> { (ModelsCaption, true), ("", false), (Row(ModelsModelHeading, ModelsColumns.Select(c => c.Heading), modelWidth), true) };
        var cut = new List<string>();
        foreach (var stat in stats)
        {
            string name = stat.Model.Length == 0 ? ModelsNoModel : stat.Model;
            string shown = CutMiddle(name, modelWidth);
            if (shown != name)
            {
                cut.Add(name);
            }

            rows.Add((Row(shown, Figures(stat), modelWidth), stat.Turns < ModelsFewTurns));
        }

        rows.Add(("", false));
        if (stats.Any(s => s.Turns < ModelsFewTurns))
        {
            rows.Add((ModelsFewNote, true));
        }

        if (stats.Any(s => s.Untagged > 0))
        {
            rows.Add((ModelsUntaggedNote, true));
        }

        if (!logging)
        {
            rows.Add((ModelsLoggingOff, true));
        }

        foreach (string name in cut)
        {
            rows.Add((name, true));
        }

        return rows;
    }

    /// <summary>A model's seven figures in <see cref="ModelsColumns"/>' order.</summary>
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

    private static string Row(string model, IEnumerable<string> figures, int modelWidth)
    {
        string head = model + new string(' ', Math.Max(0, modelWidth - TextCells.Width(model)));
        return head + string.Concat(figures.Zip(ModelsColumns, (f, c) => new string(' ', ModelsGap) + f.PadLeft(c.Width)));
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
