using System.Globalization;
using NeonSidekick.Llm;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

/// <summary>
/// The words for the token tally: the <c>/usage</c> info pane (a Statistics tab, three sections of
/// two-column rows), the same as plain lines for a console without the pane, and the hint-row part.
/// Pure statics, every string pinned; invariant culture throughout. <c>Text</c> cells, never
/// <c>Markup</c>, as the other panes. 2026-09-28, the user's call: the Notes tab (how the figures
/// are measured) is gone and the Tokens tab is named Statistics, the pane's one tab.
/// </summary>
public static class UsageText
{
    /// <summary>The info pane's strip label (the glyph since later on 2026-09-21; the toolbar wears it for the pane too since 2026-09-29).</summary>
    public const string Label = ChatScreen.UsageToolGlyph + " Usage";

    /// <summary>The tab's title.</summary>
    public const string StatisticsTabTitle = "Statistics";

    public const string NothingCounted = "(no tokens counted yet)";
    public const string LastReplyUnreported = "(last reply: no usage reported)";

    /// <summary>The four section headings, in pane order.</summary>
    public const string ContextHeading = "Context";
    public const string LastReplyHeading = "Last reply";
    public const string ConversationHeading = "This conversation";
    public const string SessionHeading = "Since launch";

    /// <summary>The Context section's rows.</summary>
    public const string WindowLabel = "Window";
    public const string InUseLabel = "In use";
    public const string UsedLabel = "Used";

    /// <summary>Beside the Context heading, and the Window row's value, when no server tier and no setting named the window.</summary>
    public const string WindowUnknown = "unknown";

    private const string Sep = " · ";

    // ── The Statistics tab ──────────────────────────────────────────────────

    /// <summary>
    /// The share of the window in use, rounded to a whole percent; null without a window or with
    /// nothing in use. Over 100 is possible after a server-side trim and shown as is.
    /// </summary>
    public static int? Percent(long inUse, ContextLength? window) =>
        window is { Tokens: > 0 } w && inUse > 0 ? (int)Math.Round(inUse * 100d / w.Tokens) : null;

    /// <summary><c>38%</c>.</summary>
    public static string PercentText(int percent) => percent.ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>The Context section's rows: the window (or <see cref="WindowUnknown"/>), the tokens in use when any, the share when both are known. Pinned.</summary>
    public static IReadOnlyList<(string Label, string Value)> ContextRows(TokenTally tally, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(tally);
        var rows = new List<(string, string)>(3)
        {
            (WindowLabel, window is { } w ? N0(w.Tokens) : WindowUnknown),
        };
        long inUse = tally.LastRequest.Total;
        if (inUse > 0)
        {
            rows.Add((InUseLabel, N0(inUse)));
        }

        if (Percent(inUse, window) is { } percent)
        {
            rows.Add((UsedLabel, PercentText(percent)));
        }

        return rows;
    }

    /// <summary>
    /// The reasoning count as shown: <c>1,234</c> when the server counted it, <c>~1,234</c> when the app estimated it or a
    /// part of the sum (2026-09-29, <c>LLM reasoning estimate</c>, the busy row's <see cref="EstimateMark"/>), null when neither.
    /// </summary>
    public static string? ReasoningValue(TokenUsage usage) =>
        usage.Reasoning is { } count ? (usage.ReasoningEstimated ? EstimateMark : "") + N0(count) : null;

    /// <summary>The Reasoning row's value when no request in the scope reported a count — the row is always there (the user's call 2026-09-14).</summary>
    public const string NoReasoningReport = "—";

    /// <summary>
    /// One scope's rows for the Statistics tab: label and value. The last reply's wait is the sum over its
    /// requests; an aggregate (<paramref name="averaged"/>) shows the wait per request, the way its
    /// speed is over every request's streaming (2026-09-17). Pinned.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> Rows(TokenUsage usage, bool averaged)
    {
        var rows = new List<(string, string)>(7)
        {
            ("Total", N0(usage.Total)),
            ("Prompt", N0(usage.Input)),
            ("Completion", N0(usage.Output)),
            ("Reasoning", ReasoningValue(usage) ?? NoReasoningReport),
            ("Requests", N0(usage.Requests)),
        };
        if (CacheValue(usage) is { } cache)
        {
            rows.Add((CacheLabel, cache));
        }

        if (usage.CostUsd is { } cost)
        {
            rows.Add((CostLabel, global::NeonSidekick.Claude.ClaudeText.Dollars(cost)));
        }

        if (Wait(usage, averaged) is { } wait)
        {
            rows.Add(("Time to first token", Seconds(wait)));
        }

        if (usage.TokensPerSecond is { } speed)
        {
            rows.Add(("Speed", Speed(speed, 1)));
        }

        return rows;
    }

    /// <summary>
    /// The Statistics tab: a heading per scope over its rows. One grid for all three, so the value
    /// column sits at the same place under every heading (a grid per section sized each label
    /// column to its own widest label and the values jumped between sections). The heading in the
    /// section style, the rows in the label style: two tiers, not one cyan column.
    /// </summary>
    public static IRenderable StatisticsTab(TokenTally tally, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(tally);
        if (tally.Session.IsEmpty && window is null && tally.ClaudeRuns == 0)
        {
            return new Text(NothingCounted, Theme.DimText);
        }

        var grid = ChatScreen.TwoColumns();

        // The Context section first: the window is known before anything was counted, and its note
        // is the source — where the figure came from — dim in the value column like the others.
        grid.AddRow(new Text(ContextHeading, Theme.SectionHeading), new Text(window?.Source ?? WindowUnknown, Theme.DimText));
        foreach (var (label, value) in ContextRows(tally, window))
        {
            grid.AddRow(new Text(label, Theme.AccentSecondary), new Text(value, Theme.Body));
        }

        grid.AddRow(new Text(" "), new Text(""));
        if (tally.Session.IsEmpty)
        {
            grid.AddRow(new Text(NothingCounted, Theme.DimText), new Text(""));
            ClaudeRow(grid, tally);
            return grid;
        }

        Section(grid, LastReplyHeading, tally.LastReply.IsEmpty ? NoReportNote : "", tally.LastReply, averaged: false);
        Section(grid, ConversationHeading, Join(SummedNote, tally.UnreportedReplies > 0 ? Unreported(tally.UnreportedReplies) : ""), tally.Conversation, averaged: true);
        Section(grid, SessionHeading, SummedNote, tally.Session, averaged: true);
        if (tally.LearningRequests > 0)
        {
            grid.AddRow(new Text(LearningLabel, Theme.AccentSecondary), new Text(LearningValue(tally), Theme.Body));
        }

        ClaudeRow(grid, tally);
        return grid;
    }

    /// <summary>The <c>/claude</c> row, once a run reported (2026-09-27): its own figures, never part of the local model's sections above.</summary>
    private static void ClaudeRow(Grid grid, TokenTally tally)
    {
        if (tally.ClaudeRuns > 0)
        {
            grid.AddRow(new Text(ClaudeLabel, Theme.AccentSecondary), new Text(ClaudeValue(tally), Theme.Body));
        }
    }

    /// <summary>The row of the prompt tokens the cache served and took (2026-09-27, the Claude API), shown once a request reported either. Pinned.</summary>
    public const string CacheLabel = "Cache";

    /// <summary>The row of what the scope cost (2026-09-27, the Claude API at its list price), shown once a request was priced. Pinned.</summary>
    public const string CostLabel = "Cost";

    /// <summary><c>12,000 read · 1,500 written</c>: the prompt tokens the cache served and took; null while no request reported either. Pinned.</summary>
    public static string? CacheValue(TokenUsage usage) =>
        usage.CacheRead is null && usage.CacheWrite is null ? null : N0(usage.CacheRead ?? 0) + " read" + Sep + N0(usage.CacheWrite ?? 0) + " written";

    /// <summary>The row that says what the <c>/claude</c> runs used since launch. Pinned.</summary>
    public const string ClaudeLabel = "Claude";

    /// <summary><c>2 runs · 12,506 in · 250 out · $0.0512</c>: the runs, their tokens and their cost as the CLI reported it. Pinned.</summary>
    public static string ClaudeValue(TokenTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);
        var usage = tally.Claude;
        return Plural(tally.ClaudeRuns, "run", "runs") + Sep + N0(usage.Input) + " in" + Sep + N0(usage.Output) + " out" + Sep + global::NeonSidekick.Claude.ClaudeText.Dollars(tally.ClaudeCostUsd);
    }

    /// <summary>The row under the launch section that says what the skill-learning reflections cost, shown only once one ran. Pinned.</summary>
    public const string LearningLabel = "Skill learning";

    /// <summary><c>2 requests · 4,120 tokens</c>: the reflections' requests and their total, part of the launch section's figures above. Pinned.</summary>
    public static string LearningValue(TokenTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);
        return Plural(tally.LearningRequests, "request", "requests") + Sep + N0(tally.Learning.Total) + " tokens";
    }

    /// <summary>Beside the last reply's heading when the server said nothing about it.</summary>
    public const string NoReportNote = "no usage reported";

    /// <summary>
    /// Beside the conversation and launch headings: their totals add every request up, the re-sent
    /// history counted each time — a cost figure, not the context (that is the Context section).
    /// </summary>
    public const string SummedNote = "every request summed";

    private static string Join(params string[] notes) => string.Join("; ", notes.Where(n => n.Length > 0));

    /// <summary>A heading row — the note, if any, dim in the value column so a long one never widens the label column — then the rows.</summary>
    private static void Section(Grid grid, string heading, string note, TokenUsage usage, bool averaged)
    {
        grid.AddRow(new Text(heading, usage.IsEmpty ? Theme.DimText : Theme.SectionHeading), new Text(note, Theme.DimText));
        if (!usage.IsEmpty)
        {
            foreach (var (label, value) in Rows(usage, averaged))
            {
                grid.AddRow(new Text(label, Theme.AccentSecondary), new Text(value, Theme.Body));
            }
        }

        // A space, not an empty cell: the grid keeps a row only when something in it rendered.
        grid.AddRow(new Text(" "), new Text(""));
    }

    // ── Plain lines and the hint row ────────────────────────────────────────

    /// <summary>A short count for the hint row: <c>980</c>, <c>12.5k</c>, <c>1.2M</c>.</summary>
    public static string CompactNumber(long tokens) => tokens switch
    {
        >= 1_000_000 => (tokens / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary><see cref="CompactNumber"/> with its unit: <c>980 tokens</c>, <c>12.5k tokens</c> — the hint row while no window frames the count.</summary>
    public static string Compact(long tokens) => CompactNumber(tokens) + " tokens";

    /// <summary><c>42.3 tok/s</c> with one decimal, <c>42 tok/s</c> with none.</summary>
    public static string Speed(double tokensPerSecond, int decimals) =>
        tokensPerSecond.ToString(decimals > 0 ? "F" + decimals.ToString(CultureInfo.InvariantCulture) : "F0", CultureInfo.InvariantCulture) + " tok/s";

    /// <summary><c>0.8 s</c>.</summary>
    public static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// The hint row's part, on one base: the context in use over the window, its share, and the last
    /// reply's speed (<c>4.6k / 151.4k · 3% · 42 tok/s</c>); <c>4.6k tokens · 42 tok/s</c> while the
    /// window is unknown; null while nothing is in the context (before the first reply, after
    /// <c>/clear</c>) and while <see cref="TokenTally.HintHidden"/> (an LLM connect, 2026-10-03, until the new
    /// model reports), so the row reads as before. Never the conversation's summed total: that
    /// counts the re-sent history once per request and sits several times past the real context.
    /// </summary>
    public static string? HintPart(TokenTally tally, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(tally);
        long inUse = tally.LastRequest.Total;
        if (inUse == 0 || tally.HintHidden)
        {
            return null;
        }

        string part = ContextPart(inUse, window, "");
        return tally.LastReply.TokensPerSecond is { } speed ? part + Sep + Speed(speed, 0) : part;
    }

    /// <summary>What marks a figure of the busy row's <see cref="BusyHintPart"/> as estimated. Pinned.</summary>
    public const string EstimateMark = "~";

    /// <summary>
    /// The busy row's part (2026-09-25, <c>LLM mid-turn usage</c>): the <see cref="HintPart"/> shape while a turn runs.
    /// <see cref="MidTurnUsage.LastKnown"/> is the server's figures as of the last completed request, the speed
    /// <see cref="TokenTally.LastKnownSpeed"/> (the turn before's until this one reports). <see cref="MidTurnUsage.Estimate"/>
    /// is the same until the request streaming now (<paramref name="live"/>) has a chunk, then every figure estimated and
    /// marked <see cref="EstimateMark"/>: the last request's total plus the chunks streamed, and the chunks' rate
    /// (<c>~5.1k / 151.4k · ~3% · ~41 tok/s</c>). The base leaves out what the request added before it streamed (the new
    /// message, the tool results) — the server's report puts it right when the request ends. Null while nothing is known,
    /// and the last-known figures null while <see cref="TokenTally.HintHidden"/> (2026-10-03: another model's, through a
    /// load's spinner); the live estimate still shows then, being the new model's own count.
    /// </summary>
    public static string? BusyHintPart(TokenTally tally, ContextLength? window, MidTurnUsage mode, StreamMeter.Reading live)
    {
        ArgumentNullException.ThrowIfNull(tally);
        long known = tally.LastRequest.Total;
        if (mode == MidTurnUsage.Estimate && live is { Streaming: true, Chunks: > 0 })
        {
            string part = ContextPart(known + live.Chunks, window, EstimateMark);
            return live.ChunksPerSecond is { } rate ? part + Sep + EstimateMark + Speed(rate, 0) : part;
        }

        if (known == 0 || tally.HintHidden)
        {
            return null;
        }

        string last = ContextPart(known, window, "");
        return tally.LastKnownSpeed is { } speed ? last + Sep + Speed(speed, 0) : last;
    }

    /// <summary>The context in use over the window and its share (<c>4.6k / 151.4k · 3%</c>), or <c>4.6k tokens</c> while the window is unknown; <paramref name="mark"/> ahead of each figure.</summary>
    private static string ContextPart(long inUse, ContextLength? window, string mark) =>
        Percent(inUse, window) is { } percent
            ? mark + CompactNumber(inUse) + " / " + CompactNumber(window!.Value.Tokens) + Sep + mark + PercentText(percent)
            : mark + Compact(inUse);

    /// <summary>
    /// The <c>/usage</c> lines: the context, the last reply, this conversation, since launch. The
    /// context line alone when nothing was counted but the window is known.
    /// </summary>
    public static IReadOnlyList<string> Lines(TokenTally tally, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(tally);
        if (tally.Session.IsEmpty)
        {
            var nothing = window is null ? new List<string> { NothingCounted } : [ContextLine(tally, window), NothingCounted];
            if (tally.ClaudeRuns > 0)
            {
                nothing.Add("claude: " + ClaudeValue(tally));
            }

            return nothing;
        }

        var lines = new List<string>(5)
        {
            ContextLine(tally, window),
            tally.LastReply.IsEmpty ? LastReplyUnreported : Line("last reply", tally.LastReply, averaged: false),
            Line("this conversation (" + SummedNote + ")", tally.Conversation, averaged: true),
            Line("since launch (" + SummedNote + ")", tally.Session, averaged: true),
        };
        if (tally.UnreportedReplies > 0)
        {
            lines.Add("(" + Unreported(tally.UnreportedReplies) + ")");
        }

        if (tally.LearningRequests > 0)
        {
            lines.Add("skill learning: " + LearningValue(tally));
        }

        if (tally.ClaudeRuns > 0)
        {
            lines.Add("claude: " + ClaudeValue(tally));
        }

        return lines;
    }

    private static string Unreported(int count) => Plural(count, "reply", "replies") + " reported no usage";

    /// <summary>
    /// <c>Context: 57,600 of 151,427 tokens (38%, max_model_len on /v1/models)</c>;
    /// <c>Context: 57,600 tokens; window unknown</c>; <c>Context: window 151,427 tokens (configured); nothing sent yet</c>. Pinned.
    /// </summary>
    public static string ContextLine(TokenTally tally, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(tally);
        long inUse = tally.LastRequest.Total;
        if (window is not { } w)
        {
            return inUse > 0 ? $"Context: {N0(inUse)} tokens; window {WindowUnknown}" : $"Context: window {WindowUnknown}";
        }

        return inUse > 0
            ? $"Context: {N0(inUse)} of {N0(w.Tokens)} tokens ({PercentText(Percent(inUse, w)!.Value)}, {w.Source})"
            : $"Context: window {N0(w.Tokens)} tokens ({w.Source}); nothing sent yet";
    }

    /// <summary>
    /// <c>Tokens — last reply: 1,240 (1,102 in, 138 out, 120 reasoning, 2 requests) · 0.8 s to first token · 46.0 tok/s</c>.
    /// The reasoning count only when one was reported: a sentence has no column to hold the dash the tab's row shows.
    /// </summary>
    private static string Line(string scope, TokenUsage usage, bool averaged)
    {
        string reasoning = ReasoningValue(usage) is { } count ? count + " reasoning, " : "";
        string cached = usage.CacheRead is { } read ? N0(read) + " cached, " : "";
        string line = $"Tokens — {scope}: {N0(usage.Total)} ({N0(usage.Input)} in, {cached}{N0(usage.Output)} out, {reasoning}{Plural(usage.Requests, "request", "requests")})";
        if (usage.CostUsd is { } cost)
        {
            line += Sep + global::NeonSidekick.Claude.ClaudeText.Dollars(cost);
        }
        if (Wait(usage, averaged) is { } wait)
        {
            line += Sep + Seconds(wait) + " to first token";
        }

        if (usage.TokensPerSecond is { } speed)
        {
            line += Sep + Speed(speed, 1);
        }

        return line;
    }

    /// <summary>The wait a row shows: the scope's whole wait, or per request for an aggregate — null for an aggregate with nothing counted (the conversation after <c>/clear</c>).</summary>
    private static TimeSpan? Wait(TokenUsage usage, bool averaged) => averaged ? usage.AverageToFirstToken : usage.ToFirstToken;

    private static string N0(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary><c>1 reply</c>, <c>2 replies</c>.</summary>
    public static string Plural(long count, string one, string many) =>
        count == 1 ? "1 " + one : N0(count) + " " + many;
}
