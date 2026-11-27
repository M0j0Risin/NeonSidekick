using System.Globalization;
using System.Text;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

// ── The Context tab, merged (2026-11-27, the user's calls) ─────────────────────────────────────────────────────────────────────────────
// One tab, titled Context, opens on the meter (the window's pieces in their colors, the headline at its right), the legend and the reference
// line with no rule, then the breakdown's rows (a bar each, the cursor's stops), then Trend (four sparklines over the conversation's replies),
// Tokens (the three scopes as one table), Records (this conversation's bests beside the model's and any model's) and Other runs. Every line is
// one line (cut, never wrapped), so a row's index is its line and the info pane's cursor can stop on it.

public static partial class UsageText
{
    /// <summary>The meter's cells (2026-11-27): a piece's share filled, what is free empty.</summary>
    public const char MeterFull = '▰';

    public const char MeterEmpty = '▱';

    /// <summary>The cells between the meter and the headline at its right.</summary>
    public const int MeterGap = 3;

    /// <summary>The legend's words, in the meter's order.</summary>
    public const string LegendPrompt = "prompt";
    public const string LegendTools = "tools";
    public const string LegendHistory = "history";
    public const string LegendPictures = "pictures";
    public const string LegendThinking = "thinking";
    public const string LegendFree = "free";
    public const string LegendOver = "over";

    /// <summary>Between the legend's parts on the pane.</summary>
    public const string LegendGap = "   ";

    /// <summary>A row bar's cells (2026-11-27): scaled to the largest row's tokens, Free and Over left out.</summary>
    public const int RowBarCells = 20;

    /// <summary>Below this body width the bar column goes, before any note is cut.</summary>
    public const int RowBarMinWidth = 90;

    /// <summary>The eighth blocks a bar ends in, one eighth to seven.</summary>
    private const string Eighths = "▏▎▍▌▋▊▉";

    /// <summary>A group switched off from the tab during the visit: its note.</summary>
    public const string GroupOff = "off";

    /// <summary>Space on a group whose switch is more than on and off (2026-11-27): <c>Shell has more than on and off; Enter opens it in /tools</c>.</summary>
    public static string NotASwitchNotice(string group) => group + " has more than on and off; Enter opens it in /tools";

    /// <summary>Space on a group with no switch of its own: <c>Clock has no switch of its own; Enter opens /tools</c> (or <c>/mcp</c>, for mcp_read's).</summary>
    public static string NoSwitchNotice(string group, string where) => group + " has no switch of its own; Enter opens " + where;

    /// <summary>History's Enter (2026-11-27): the question before <c>/compact</c>.</summary>
    public const string CompactQuestion = "Compact the conversation now?";

    /// <summary>The command words Enter's jumps name.</summary>
    public const string ToolsCommand = "/tools";
    public const string McpCommand = "/mcp";
    public const string CompactCommand = "/compact";

    /// <summary>A group's note (2026-11-27, the user's ask: "off saves 4%" was overkill): its share of the window, <c>&lt;1%</c> for a sliver.</summary>
    public static string GroupShare(int percent) => percent < 1 ? "<1%" : PercentText(percent);

    /// <summary>
    /// A row's bar (2026-11-27): <c>█</c> per whole cell of <paramref name="cells"/>, an eighth block for the rest, scaled to
    /// <paramref name="largest"/>; at least <c>▏</c> for a row over zero, empty for zero. Pure.
    /// </summary>
    public static string RowBar(long tokens, long largest, int cells)
    {
        if (tokens <= 0 || largest <= 0 || cells <= 0)
        {
            return "";
        }

        long eighths = Math.Max(1, (long)Math.Round(Math.Min(tokens, largest) * cells * 8d / largest));
        return new string('█', (int)(eighths / 8)) + (eighths % 8 > 0 ? Eighths[(int)(eighths % 8) - 1].ToString() : "");
    }

    // ── Trend ──────────────────────────────────────────────────────────────

    public const string TrendHeading = "Trend";
    public const string TrendSpeedLabel = "Speed";
    public const string TrendFirstTokenLabel = "First token";
    public const string TrendContextLabel = "Context";
    public const string TrendOutputLabel = "Output";

    /// <summary>Before a reply whose model differs from the one before.</summary>
    public const char ModelChangeMark = '│';

    /// <summary>Before a reply a compaction or a <c>/rewind</c> came before.</summary>
    public const char CompactedMark = '┆';

    public const string ModelChangeNote = "│ model switch";
    public const string CompactedNote = "┆ compacted";

    /// <summary>The sparkline's levels, lowest first.</summary>
    public const string SparkCells = "▁▂▃▄▅▆▇█";

    /// <summary>The rule's count: <c>31 replies</c>, or <c>last 40 of 80</c> when only the newest fit.</summary>
    public static string TrendCount(int shown, int total) => shown < total
        ? "last " + N0(shown) + " of " + N0(total)
        : Plural(total, "reply", "replies");

    /// <summary>
    /// One measure's cell for a value: its level between the shown values' lowest and highest, the middle when they are all one; null is a gap.
    /// Pure.
    /// </summary>
    public static char SparkCell(double? value, double low, double high)
    {
        if (value is not { } v)
        {
            return ' ';
        }

        int level = high > low ? (int)Math.Round((v - low) / (high - low) * (SparkCells.Length - 1)) : SparkCells.Length / 2 - 1;
        return SparkCells[Math.Clamp(level, 0, SparkCells.Length - 1)];
    }

    private sealed record Measure(string Label, Func<ReplyPoint, double?> Value, Color Color, Func<IReadOnlyList<double>, string> Right);

    private static IReadOnlyList<Measure> Measures() =>
    [
        new(TrendSpeedLabel, p => p.TokensPerSecond, Theme.Secondary, v => Span(v, x => x.ToString("F0", CultureInfo.InvariantCulture)) + " tok/s"),
        new(TrendFirstTokenLabel, p => p.FirstToken?.TotalSeconds, Theme.Highlight, v => Span(v, x => x.ToString("F1", CultureInfo.InvariantCulture)) + " s"),
        new(TrendContextLabel, p => p.Context, Theme.Tertiary, v => Span(v, x => CompactNumber((long)x))),
        new(TrendOutputLabel, p => p.Output, Theme.Primary, v => v.Count == 0 ? "—"
            : N0((long)v[^1]) + Sep + N0((long)Math.Round(v.Average())) + " avg"),
    ];

    /// <summary><c>first → latest</c>, the one value alone, or <c>—</c> with none.</summary>
    private static string Span(IReadOnlyList<double> values, Func<double, string> format) => values.Count switch
    {
        0 => "—",
        1 => format(values[0]),
        _ => format(values[0]) + " → " + format(values[^1]),
    };

    /// <summary>The mark before reply <paramref name="i"/>: a model change, else a compaction, else none.</summary>
    private static char? MarkBefore(IReadOnlyList<ReplyPoint> replies, int i)
    {
        if (i == 0)
        {
            return null;
        }

        if (replies[i].Model is { } now && replies[i - 1].Model is { } before && !string.Equals(now, before, StringComparison.OrdinalIgnoreCase))
        {
            return ModelChangeMark;
        }

        return replies[i].Marked ? CompactedMark : null;
    }

    /// <summary>
    /// The newest replies whose cells and marks fit <paramref name="room"/> cells: the index of the first shown. A mark is drawn only between two
    /// shown replies, so a reply taken in brings the mark after it.
    /// </summary>
    private static int FirstShown(IReadOnlyList<ReplyPoint> replies, int room)
    {
        int used = 0;
        for (int i = replies.Count - 1; i >= 0; i--)
        {
            int need = 1 + (i < replies.Count - 1 && MarkBefore(replies, i + 1) is not null ? 1 : 0);
            if (used + need > room)
            {
                return i + 1;
            }

            used += need;
        }

        return 0;
    }

    private static Color Blend(Color a, Color b) => new((byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));

    /// <summary>The Trend section's lines (2026-11-27): none with fewer than 2 replies.</summary>
    private static List<IRenderable> TrendSection(IReadOnlyList<ReplyPoint> replies, int width)
    {
        var lines = new List<IRenderable>();
        if (replies.Count < 2)
        {
            return lines;
        }

        var measures = Measures();
        int labelWidth = measures.Max(m => m.Label.Length);
        var rights = measures.Select(m => m.Right([.. replies.Select(m.Value).OfType<double>()])).ToList();
        int rightWidth = rights.Max(TextCells.Width);
        int room = Math.Max(2, width - labelWidth - 2 * SlashCommands.HelpColumnGap - rightWidth);
        int first = FirstShown(replies, room);
        var shown = replies.Skip(first).ToList();
        var marks = Enumerable.Range(first, replies.Count - first).Select(i => i == first ? null : MarkBefore(replies, i)).ToList();
        var notes = new List<string>(2);
        if (marks.Contains(ModelChangeMark))
        {
            notes.Add(ModelChangeNote);
        }

        if (marks.Contains(CompactedMark))
        {
            notes.Add(CompactedNote);
        }

        lines.Add(new SectionRule(SectionRule.Markup(TrendHeading, TrendCount(shown.Count, replies.Count), notes.Count > 0 ? string.Join(Sep, notes) : null)));
        string gap = new(' ', SlashCommands.HelpColumnGap);
        string dimMarkup = Theme.DimText.ToMarkup();
        for (int m = 0; m < measures.Count; m++)
        {
            var measure = measures[m];
            var values = shown.Select(measure.Value).ToList();
            var known = values.OfType<double>().ToList();
            double low = known.Count > 0 ? known.Min() : 0;
            double high = known.Count > 0 ? known.Max() : 0;
            string dim = new Style(Blend(measure.Color, Theme.Dim)).ToMarkup();
            string bright = new Style(measure.Color).ToMarkup();
            var line = new StringBuilder(new string(' ', SystemPromptSummary.BodyIndent))
                .Append('[').Append(Theme.AccentSecondary.ToMarkup()).Append(']').Append(Markup.Escape(measure.Label.PadRight(labelWidth))).Append("[/]").Append(gap);
            for (int i = 0; i < values.Count; i++)
            {
                if (marks[i] is { } mark)
                {
                    line.Append('[').Append(dimMarkup).Append(']').Append(mark).Append("[/]");
                }

                char cell = SparkCell(values[i], low, high);
                line.Append('[').Append(i == values.Count - 1 ? bright : dim).Append(']').Append(cell).Append("[/]");
            }

            line.Append(gap).Append(Markup.Escape(rights[m]));
            lines.Add(new FittedMarkup(line.ToString()));
        }

        return lines;
    }

    /// <summary>
    /// The trend as plain lines (2026-11-27, no pane): <c>Speed: ▁▂▃… (143 → 118 tok/s)</c> a measure; none with fewer than 2 replies. The newest
    /// <see cref="PlainTrendCells"/> replies, marks included.
    /// </summary>
    public static IReadOnlyList<string> TrendLines(IReadOnlyList<ReplyPoint> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);
        if (replies.Count < 2)
        {
            return [];
        }

        int first = FirstShown(replies, PlainTrendCells);
        var lines = new List<string>(4);
        foreach (var measure in Measures())
        {
            var values = replies.Skip(first).Select(measure.Value).ToList();
            var known = values.OfType<double>().ToList();
            double low = known.Count > 0 ? known.Min() : 0;
            double high = known.Count > 0 ? known.Max() : 0;
            var cells = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0 && MarkBefore(replies, first + i) is { } mark)
                {
                    cells.Append(mark);
                }

                cells.Append(SparkCell(values[i], low, high));
            }

            lines.Add(measure.Label.ToLowerInvariant() + ": " + cells + " (" + measure.Right([.. replies.Select(measure.Value).OfType<double>()]) + ")");
        }

        return lines;
    }

    /// <summary>The cells the plain trend lines take at most.</summary>
    public const int PlainTrendCells = 60;

    // ── Tokens ─────────────────────────────────────────────────────────────

    public const string TokensHeading = "Tokens";
    public const string LastReplyColumn = "Last reply";
    public const string ConversationColumn = "Conversation";
    public const string SinceLaunchColumn = "Since launch";

    /// <summary>A scope's note under the table: <c>Conversation: 1 reply reported no usage</c>.</summary>
    public static string ScopeNote(string column, string note) => column + ": " + note;

    /// <summary>The Tokens section's lines (2026-11-27): one table of the three scopes, the notes under it.</summary>
    private static List<IRenderable> TokensSection(TokenTally tally)
    {
        var lines = new List<IRenderable> { new SectionRule(SectionRule.Markup(TokensHeading)) };
        var scopes = new (string Head, IReadOnlyList<(string Label, string Value)> Rows)[]
        {
            (LastReplyColumn, tally.LastReply.IsEmpty ? [] : Rows(tally.LastReply, averaged: false)),
            (ConversationColumn, tally.Conversation.IsEmpty ? [] : Rows(tally.Conversation, averaged: true)),
            (SinceLaunchColumn, Rows(tally.Session, averaged: true)),
        };
        var labels = new List<string>();
        foreach (var scope in scopes.Reverse())
        {
            foreach (var (label, _) in scope.Rows)
            {
                if (!labels.Contains(label))
                {
                    labels.Add(label);
                }
            }
        }

        // The rows' order is Rows' own; a label only one scope has keeps its place among them.
        var order = Rows(tally.Session, averaged: true).Select(r => r.Label).ToList();
        labels = [.. labels.OrderBy(l => order.IndexOf(l) is int i and >= 0 ? i : int.MaxValue)];
        int labelWidth = labels.Select(TextCells.Width).DefaultIfEmpty(0).Max();
        var widths = scopes.Select(s => Math.Max(TextCells.Width(s.Head), s.Rows.Select(r => TextCells.Width(r.Value)).DefaultIfEmpty(0).Max())).ToList();
        string indent = new(' ', SystemPromptSummary.BodyIndent);
        string gap = new(' ', SlashCommands.HelpColumnGap * 2);
        static string Right(string text, int cells) => new string(' ', Math.Max(0, cells - TextCells.Width(text))) + text;
        var head = new StringBuilder(indent + new string(' ', labelWidth));
        for (int i = 0; i < scopes.Length; i++)
        {
            head.Append(gap).Append(Right(scopes[i].Head, widths[i]));
        }

        lines.Add(new FittedMarkup("[" + Theme.DimText.ToMarkup() + "]" + Markup.Escape(head.ToString()) + "[/]"));
        string bold = new Style(Theme.Body.Foreground, decoration: Decoration.Bold).ToMarkup();
        foreach (string label in labels)
        {
            var line = new StringBuilder(indent).Append('[').Append(Theme.AccentSecondary.ToMarkup()).Append(']')
                .Append(Markup.Escape(label + new string(' ', labelWidth - TextCells.Width(label)))).Append("[/]");
            for (int i = 0; i < scopes.Length; i++)
            {
                string value = scopes[i].Rows.FirstOrDefault(r => r.Label == label).Value ?? "";
                line.Append(gap).Append('[').Append(i == 0 ? bold : Theme.Body.ToMarkup()).Append(']').Append(Markup.Escape(Right(value, widths[i]))).Append("[/]");
            }

            lines.Add(new FittedMarkup(line.ToString()));
        }

        foreach (string note in TokenNotes(tally))
        {
            lines.Add(new FittedMarkup(indent + Theme.DimMarkup(note)));
        }

        return lines;
    }

    /// <summary>The scopes' notes under the table, in the columns' order.</summary>
    private static IEnumerable<string> TokenNotes(TokenTally tally)
    {
        if (tally.LastReply.IsEmpty)
        {
            yield return ScopeNote(LastReplyColumn, NoReportNote);
        }

        if (tally.UnreportedReplies > 0)
        {
            yield return ScopeNote(ConversationColumn, Unreported(tally.UnreportedReplies));
        }
    }

    // ── Records ────────────────────────────────────────────────────────────

    public const string RecordsHeading = "Records";
    public const string RecordsNote = "this conversation · all time";
    public const string ThisConversationColumn = "This conversation";
    public const string AnyModelColumn = "Any model ever";
    public const string FastestLabel = "Fastest reply";
    public const string LongestLabel = "Longest answer";
    public const string SlowestLabel = "Slowest start";
    public const string WrittenLabel = "Written";

    /// <summary>The best this conversation holds when it is the model's best too.</summary>
    public const string RecordStar = "★";

    /// <summary>In the model's column when this conversation holds its best.</summary>
    public const string NewRecord = "★ new";

    /// <summary>A rough page of prose, in tokens (2026-11-27): the Written row's pages, always shown with <c>≈</c>.</summary>
    public const int TokensPerPage = 650;

    /// <summary>A model's column: <c>Qwen3.8-27B ever</c>.</summary>
    public static string ModelEverColumn(string model) => model + " ever";

    /// <summary><c>reply 3</c>.</summary>
    public static string ReplyRef(int number) => "reply " + number.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>≈ 19 pages</c>.</summary>
    public static string Pages(long tokens) => "≈ " + Plural((long)Math.Round(tokens / (double)TokensPerPage), "page", "pages");

    /// <summary>The widest a model's name is in the Records' heads and cells, cut in the middle past it.</summary>
    public const int RecordModelCap = 28;

    private static string Tps(double speed) => speed.ToString("F0", CultureInfo.InvariantCulture) + " tok/s";

    private static string Tokens(long tokens) => N0(tokens) + " tokens";

    private static string Secs(long ms) => (ms / 1000d).ToString("F1", CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// The Records' cells (2026-11-27): a row each, this conversation's, the model's and any model's (null where there is none). Pure.
    /// </summary>
    internal static IReadOnlyList<(string Label, string? Conversation, string? Model, string? Any)> RecordCells(IReadOnlyList<ReplyPoint> replies, ModelBests bests, string? model)
    {
        ArgumentNullException.ThrowIfNull(replies);
        ArgumentNullException.ThrowIfNull(bests);
        var mine = bests.ThisModel;
        const double Tolerance = 0.05;
        var rows = new List<(string, string?, string?, string?)>(4);

        ReplyPoint? fastest = Best(replies, r => r.TokensPerSecond);
        double? modelSpeed = mine?.Speed is { } s ? Math.Max(s, fastest?.TokensPerSecond ?? 0) : null;
        bool speedNew = fastest?.TokensPerSecond is { } f && modelSpeed is { } ms && f >= ms - Tolerance;
        rows.Add((FastestLabel,
            fastest is { TokensPerSecond: { } fs } ? Tps(fs) + Sep + ReplyRef(fastest.Value.Number) + (speedNew ? " " + RecordStar : "") : null,
            modelSpeed is { } msp ? Tps(msp) + (speedNew ? Sep + NewRecord : "") : null,
            bests.Speed is { } any ? Tps(any.Value) + Sep + CutMiddle(any.Model, RecordModelCap) : null));

        ReplyPoint? longest = Best(replies, r => r.Output is > 0 ? r.Output : null);
        long? modelLongest = mine?.LongestOutput is { } l ? Math.Max(l, longest?.Output ?? 0) : null;
        bool longNew = longest?.Output is { } lo && modelLongest is { } ml && lo >= ml;
        rows.Add((LongestLabel,
            longest is { Output: { } lt } ? Tokens(lt) + Sep + ReplyRef(longest.Value.Number) + (longNew ? " " + RecordStar : "") : null,
            modelLongest is { } mlg ? Tokens(mlg) + (longNew ? Sep + NewRecord : "") : null,
            bests.LongestOutput is { } anyLong ? Tokens(anyLong.Value) + Sep + CutMiddle(anyLong.Model, RecordModelCap) : null));

        ReplyPoint? slowest = Best(replies, r => r.FirstToken?.TotalMilliseconds);
        rows.Add((SlowestLabel,
            slowest is { FirstToken: { } st } ? Secs((long)st.TotalMilliseconds) + Sep + ReplyRef(slowest.Value.Number) + (slowest.Value.Context is { } c ? Sep + CompactNumber(c) : "") : null,
            mine?.SlowestStartMs is { } sm ? Secs(sm) + (mine.SlowestStartContext is { } sc ? Sep + CompactNumber(sc) : "") : null,
            bests.SlowestStart is { } anySlow ? Secs(anySlow.Ms) + Sep + CutMiddle(anySlow.Model, RecordModelCap) : null));

        long written = replies.Sum(r => r.Output ?? 0);
        rows.Add((WrittenLabel,
            written > 0 ? Tokens(written) + Sep + Pages(written) : null,
            mine is { Written: > 0 } ? CompactNumber(mine.Written) + " tokens" + Sep + Pages(mine.Written) : null,
            bests.Written > 0 ? CompactNumber(bests.Written) + " tokens" + Sep + Pages(bests.Written) : null));
        return rows;
    }

    /// <summary>The reply with the highest <paramref name="value"/>, the earliest on a tie; null when none has one.</summary>
    private static ReplyPoint? Best<T>(IReadOnlyList<ReplyPoint> replies, Func<ReplyPoint, T?> value) where T : struct, IComparable<T>
    {
        ReplyPoint? best = null;
        T top = default;
        foreach (var reply in replies)
        {
            if (value(reply) is { } v && (best is null || v.CompareTo(top) > 0))
            {
                (best, top) = (reply, v);
            }
        }

        return best;
    }

    /// <summary>Whether any model's column says something the model's doesn't: its holder another model, or its figure another.</summary>
    private static bool AnyDiffers(ModelBests bests, string? model) =>
        (bests.Speed is { } s && (!string.Equals(s.Model, model, StringComparison.OrdinalIgnoreCase)))
        || (bests.LongestOutput is { } l && (!string.Equals(l.Model, model, StringComparison.OrdinalIgnoreCase)))
        || (bests.SlowestStart is { } w && (!string.Equals(w.Model, model, StringComparison.OrdinalIgnoreCase)))
        || (bests.ThisModel is { } mine && bests.Written != mine.Written);

    /// <summary>The Records section's lines (2026-11-27): three columns, two when narrow or the any-model column says nothing new; none with nothing to show.</summary>
    private static List<IRenderable> RecordsSection(IReadOnlyList<ReplyPoint> replies, ModelBests bests, string? model, int width)
    {
        var lines = new List<IRenderable>();
        var cells = RecordCells(replies, bests, model);
        if (cells.All(c => c.Conversation is null && c.Model is null))
        {
            return lines;
        }

        var heads = new List<string> { ThisConversationColumn };
        bool withModel = bests.ThisModel is not null && model is not null;
        if (withModel)
        {
            heads.Add(ModelEverColumn(CutMiddle(model!, RecordModelCap)));
        }

        bool withAny = withModel && bests.Any && AnyDiffers(bests, model);
        if (withAny)
        {
            heads.Add(AnyModelColumn);
        }

        int labelWidth = cells.Max(c => TextCells.Width(c.Label));
        string gap = new(' ', SlashCommands.HelpColumnGap * 2);
        List<int> Widths(int columns) => [.. Enumerable.Range(0, columns).Select(i => Math.Max(TextCells.Width(heads[i]),
            cells.Max(c => TextCells.Width((i == 0 ? c.Conversation : i == 1 ? c.Model : c.Any) ?? ""))))];
        int columns = heads.Count;
        while (columns > 1 && SystemPromptSummary.BodyIndent + labelWidth + Widths(columns).Sum(w => w + gap.Length) > width)
        {
            columns--;
        }

        var widths = Widths(columns);
        lines.Add(new SectionRule(SectionRule.Markup(RecordsHeading, note: withModel ? RecordsNote : null)));
        string indent = new(' ', SystemPromptSummary.BodyIndent);
        static string Pad(string text, int cells) => text + new string(' ', Math.Max(0, cells - TextCells.Width(text)));
        var head = new StringBuilder(indent + new string(' ', labelWidth));
        for (int i = 0; i < columns; i++)
        {
            head.Append(gap).Append(Pad(heads[i], widths[i]));
        }

        lines.Add(new FittedMarkup(Theme.DimMarkup(head.ToString().TrimEnd())));
        foreach (var c in cells)
        {
            var line = new StringBuilder(indent).Append('[').Append(Theme.AccentSecondary.ToMarkup()).Append(']').Append(Markup.Escape(Pad(c.Label, labelWidth))).Append("[/]");
            for (int i = 0; i < columns; i++)
            {
                string cell = (i == 0 ? c.Conversation : i == 1 ? c.Model : c.Any) ?? "";
                line.Append(gap).Append(Markup.Escape(Pad(cell, widths[i])));
            }

            lines.Add(new FittedMarkup(line.ToString().TrimEnd()));
        }

        return lines;
    }

    /// <summary>
    /// The records as plain lines (2026-11-27, no pane): <c>fastest reply: 187 tok/s · reply 3 (Qwen ever 187 tok/s · any model 412 tok/s · Qwen-4B)</c>;
    /// the all-time parts only with saved figures. None with nothing to show.
    /// </summary>
    public static IReadOnlyList<string> RecordLines(IReadOnlyList<ReplyPoint> replies, ModelBests bests, string? model)
    {
        var cells = RecordCells(replies, bests, model);
        if (cells.All(c => c.Conversation is null && c.Model is null))
        {
            return [];
        }

        bool withAny = bests.Any && AnyDiffers(bests, model);
        var lines = new List<string>(4);
        foreach (var c in cells)
        {
            var extra = new List<string>(2);
            if (c.Model is { } m && model is not null)
            {
                extra.Add(ModelEverColumn(model) + " " + m);
            }

            if (withAny && c.Any is { } a)
            {
                extra.Add(AnyModelColumn.ToLowerInvariant() + " " + a);
            }

            lines.Add(c.Label.ToLowerInvariant() + ": " + (c.Conversation ?? NoReasoningReport) + (extra.Count > 0 ? " (" + string.Join(Sep, extra) + ")" : ""));
        }

        return lines;
    }

    // ── Other runs ─────────────────────────────────────────────────────────

    /// <summary>The section of what ran apart from this chat's figures (skill learning, <c>/claude</c>, the bots, the advisors).</summary>
    public const string OtherRunsHeading = "Other runs";

    private static List<(string Label, string Value)> OtherRows(TokenTally tally)
    {
        var rows = new List<(string, string)>(4);
        if (tally.LearningRequests > 0 && !tally.Session.IsEmpty)
        {
            rows.Add((LearningLabel, LearningValue(tally)));
        }

        rows.AddRange(ApartRows(tally));
        return rows;
    }

    // ── The tab ────────────────────────────────────────────────────────────

    /// <summary>
    /// The merged tab and its stops (2026-11-27): the content, the line of each row the cursor visits (every breakdown row but Free and Over),
    /// those rows, and the stop the cursor starts on (the first tool group, else the first stop; -1 with none).
    /// </summary>
    public sealed record StatisticsLayoutResult(IRenderable Content, IReadOnlyList<int> Stops, IReadOnlyList<ContextRow> StopRows, int InitialStop);

    /// <summary>The merged Context tab's renderable (<see cref="StatisticsLayout"/>'s).</summary>
    public static IRenderable StatisticsTab(TokenTally tally, ContextFacts facts, IReadOnlyList<ReplyPoint> replies, ModelBests bests, string? model, int width) =>
        StatisticsLayout(tally, facts, replies, bests, model, width).Content;

    /// <summary>
    /// The merged tab (2026-11-27, G7): the top and the breakdown (<see cref="ContextParts"/>) with no rule, then, once anything was counted,
    /// Trend, Tokens and Records; with nothing counted the Tokens rule over <see cref="NothingCounted"/>; Other runs last when any ran.
    /// <paramref name="width"/> is the body's cells, its indent included.
    /// </summary>
    public static StatisticsLayoutResult StatisticsLayout(TokenTally tally, ContextFacts facts, IReadOnlyList<ReplyPoint> replies, ModelBests bests, string? model, int width)
    {
        ArgumentNullException.ThrowIfNull(tally);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(replies);
        ArgumentNullException.ThrowIfNull(bests);
        var (lines, stops, stopRows) = ContextParts(facts, width);
        void Section(List<IRenderable> section)
        {
            if (section.Count > 0)
            {
                lines.Add(new Text(" "));
                lines.AddRange(section);
            }
        }

        if (tally.Session.IsEmpty)
        {
            Section([new SectionRule(SectionRule.Markup(TokensHeading)), new FittedMarkup(new string(' ', SystemPromptSummary.BodyIndent) + Theme.DimMarkup(NothingCounted))]);
        }
        else
        {
            Section(TrendSection(replies, width));
            Section(TokensSection(tally));
            Section(RecordsSection(replies, bests, model, width));
        }

        var other = OtherRows(tally);
        if (other.Count > 0)
        {
            int labels = other.Max(r => TextCells.Width(r.Label));
            var section = new List<IRenderable> { new SectionRule(SectionRule.Markup(OtherRunsHeading)) };
            foreach (var (label, value) in other)
            {
                section.Add(new FittedMarkup(new string(' ', SystemPromptSummary.BodyIndent) + "[" + Theme.AccentSecondary.ToMarkup() + "]"
                    + Markup.Escape(label + new string(' ', labels - TextCells.Width(label))) + "[/]" + new string(' ', SlashCommands.HelpColumnGap) + Markup.Escape(value)));
            }

            Section(section);
        }

        int initial = stopRows.Count == 0 ? -1 : Math.Max(0, stopRows.ToList().FindIndex(r => r.Sub));
        return new StatisticsLayoutResult(new Rows(lines), stops, stopRows, initial);
    }

    /// <summary>
    /// The top and the breakdown as lines (2026-11-27): the meter with the headline at its right (the headline alone without a window), the
    /// legend, the reference line, a blank line, then a line per row; the stops are the rows' lines. The Claude Code server's one line instead.
    /// </summary>
    private static (List<IRenderable> Lines, List<int> Stops, List<ContextRow> StopRows) ContextParts(ContextFacts facts, int width)
    {
        string indent = new(' ', SystemPromptSummary.BodyIndent);
        if (facts.ClaudeCode)
        {
            return ([new FittedMarkup(indent + Theme.DimMarkup(ClaudeCodeContext))], [], []);
        }

        var lines = new List<IRenderable>
        {
            facts.Window is { } window
                ? new ContextMeter(BarPieces(facts), window.Tokens, ContextHeadline(facts), SystemPromptSummary.BodyIndent)
                : new FittedMarkup(indent + "[" + Theme.Body.ToMarkup() + "]" + Markup.Escape(ContextHeadline(facts)) + "[/]"),
            new ContextLegend(LegendParts(facts), SystemPromptSummary.BodyIndent),
            new FittedMarkup(indent + Theme.DimMarkup(ContextReference(facts))),
            new Text(" "),
        };
        var rows = ContextRows(facts);
        int labels = Math.Min(LabelCap, rows.Select(r => TextCells.Width(r.Label) + (r.Sub ? SubIndent : 0)).DefaultIfEmpty(0).Max());
        int values = rows.Select(r => TextCells.Width(r.Value)).DefaultIfEmpty(0).Max();
        bool bars = width >= RowBarMinWidth;
        long largest = rows.Where(r => r.Piece is not (ContextPiece.Free or ContextPiece.Over) && !r.Off).Select(r => r.Tokens).DefaultIfEmpty(0).Max();
        string gap = new(' ', SlashCommands.HelpColumnGap);
        int noteRoom = Math.Max(1, width - SystemPromptSummary.BodyIndent - labels - values - 2 * gap.Length - (bars ? RowBarCells + gap.Length : 0));
        var stops = new List<int>();
        var stopRows = new List<ContextRow>();
        foreach (var row in rows)
        {
            string label = CutToCells((row.Sub ? new string(' ', SubIndent) : "") + row.Label, labels);
            var labelStyle = row.Off ? Theme.DimText : row.Sub ? Theme.Body : PieceStyle(row.Piece);
            var valueStyle = row.Off ? Theme.DimText : row.Piece == ContextPiece.Over ? new Style(Theme.Warn) : Theme.Body;
            var line = new StringBuilder(indent)
                .Append('[').Append(labelStyle.ToMarkup()).Append(']').Append(Markup.Escape(label + new string(' ', labels - TextCells.Width(label)))).Append("[/]")
                .Append(gap)
                .Append('[').Append(valueStyle.ToMarkup()).Append(']').Append(Markup.Escape(new string(' ', values - TextCells.Width(row.Value)) + row.Value)).Append("[/]");
            if (bars)
            {
                string bar = row.Off || row.Piece is ContextPiece.Free or ContextPiece.Over ? "" : RowBar(row.Tokens, largest, RowBarCells);
                line.Append(gap);
                if (bar.Length > 0)
                {
                    line.Append('[').Append(new Style(PieceColor(row.Piece)).ToMarkup()).Append(']').Append(bar).Append("[/]");
                }

                line.Append(new string(' ', RowBarCells - TextCells.Width(bar)));
            }

            if (row.Note.Length > 0)
            {
                line.Append(gap).Append(Theme.DimMarkup(TextCells.Cut(row.Note, noteRoom)));
            }

            if (row.Piece is not (ContextPiece.Free or ContextPiece.Over))
            {
                stops.Add(lines.Count);
                stopRows.Add(row);
            }

            lines.Add(new FittedMarkup(line.ToString()));
        }

        return (lines, stops, stopRows);
    }

    /// <summary>The top and the breakdown alone (the merged tab's start), at <paramref name="width"/> cells.</summary>
    public static IRenderable ContextTab(ContextFacts facts, int width)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return new Rows(ContextParts(facts, width).Lines);
    }

    /// <summary>The legend's parts in the meter's order: a piece, its word and its figure; pictures only when there are some, free or over with a window.</summary>
    public static IReadOnlyList<(ContextPiece Piece, string Word, string Value)> LegendParts(ContextFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var parts = new List<(ContextPiece, string, string)>(6)
        {
            (ContextPiece.Prompt, LegendPrompt, CompactNumber(facts.PromptTokens)),
            (ContextPiece.Tools, LegendTools, CompactNumber(facts.ToolTokens)),
            (ContextPiece.History, LegendHistory, CompactNumber(facts.HistoryTokens ?? 0)),
        };
        if (facts.History is { } history)
        {
            if (history.Pictures > 0)
            {
                parts.Add((ContextPiece.Pictures, LegendPictures, CompactNumber(history.PictureTokenCount)));
            }

            parts.Add((ContextPiece.Thinking, LegendThinking, CompactNumber(history.ThinkingTokens)));
        }

        if (facts.Window is { } window)
        {
            long free = window.Tokens - facts.Estimate;
            parts.Add(free >= 0 ? (ContextPiece.Free, LegendFree, CompactNumber(free)) : (ContextPiece.Over, LegendOver, CompactNumber(-free)));
        }

        return parts;
    }

    /// <summary>The plain legend (no pane): <c>prompt 4.1k · tools 24.7k · history 155 · thinking 0 · free 122.5k</c>, no marks.</summary>
    public static string PlainLegend(ContextFacts facts) => string.Join(Sep, LegendParts(facts).Select(p => p.Word + " " + p.Value));
}

/// <summary>
/// The Context tab's meter (2026-11-27): <c>▰</c> per piece in its color (<see cref="UsageText.BarCells"/>), <c>▱</c> in the dimmer color for
/// what is free, the warning cell past the window, then the headline at its right. With no color each piece keeps its own shade
/// (<see cref="UsageText.PieceGlyph"/>) and free reads <c>░</c>, since <c>▰</c> has none. One line, indented <paramref name="indent"/>.
/// </summary>
internal sealed class ContextMeter(IReadOnlyList<(ContextPiece Piece, long Tokens)> pieces, long window, string headline, int indent) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(1, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        int headWidth = TextCells.Width(headline);
        int width = Math.Max(4, maxWidth - 2 * indent - UsageText.MeterGap - headWidth);
        var cells = UsageText.BarCells(pieces, window, width);
        bool over = pieces.Sum(p => p.Tokens) > window;
        bool noColor = !Theme.HasBackgrounds(options);
        var segments = new List<Segment>(pieces.Count + 5) { new(new string(' ', indent)) };
        int used = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (cells[i] > 0)
            {
                char glyph = noColor ? UsageText.PieceGlyph(pieces[i].Piece, noColor: true) : UsageText.MeterFull;
                segments.Add(new Segment(new string(glyph, cells[i]), new Style(UsageText.PieceColor(pieces[i].Piece))));
                used += cells[i];
            }
        }

        if (over && used < width)
        {
            segments.Add(new Segment(noColor ? "!" : UsageText.MeterFull.ToString(), new Style(Theme.Warn)));
            used++;
        }

        if (used < width)
        {
            segments.Add(new Segment(new string(noColor ? '░' : UsageText.MeterEmpty, width - used), new Style(Theme.Dimmer)));
        }

        if (indent + width + UsageText.MeterGap + headWidth <= maxWidth)
        {
            segments.Add(new Segment(new string(' ', UsageText.MeterGap) + headline, Theme.Body));
        }

        return segments;
    }
}

/// <summary>
/// The Context tab's legend (2026-11-27): each piece's mark in its color then its word and figure, <c>▱</c> for free; with no color the marks
/// are the meter's shades, so each word still names its cells. One line, cut to the room.
/// </summary>
internal sealed class ContextLegend(IReadOnlyList<(ContextPiece Piece, string Word, string Value)> parts, int indent) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(1, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        bool noColor = !Theme.HasBackgrounds(options);
        var markup = new StringBuilder(new string(' ', indent));
        for (int i = 0; i < parts.Count; i++)
        {
            var (piece, word, value) = parts[i];
            char mark = piece is ContextPiece.Free ? (noColor ? '░' : UsageText.MeterEmpty)
                : noColor ? piece == ContextPiece.Over ? '!' : UsageText.PieceGlyph(piece, noColor: true)
                : UsageText.MeterFull;
            markup.Append(i > 0 ? UsageText.LegendGap : "")
                .Append('[').Append(new Style(UsageText.PieceColor(piece)).ToMarkup()).Append(']').Append(mark).Append("[/] ")
                .Append(Markup.Escape(word + " " + value));
        }

        return ((IRenderable)new FittedMarkup(markup.ToString())).Render(options, maxWidth);
    }
}
