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
    /// The Statistics tab: a heading per scope over its rows. The heading in the section style, the rows in the label style: two
    /// tiers, not one cyan column. Since 2026-10-10 (the user's ask: the <c>/settings</c> look on every pane) each heading is a
    /// <see cref="SectionRule"/> with its note (the source, <see cref="SummedNote"/>…) dim on the rule, the <c>/sys</c> tabs' shape,
    /// and its rows <see cref="SystemPromptSummary.BodyIndent"/> cells in under it; one label width for every section
    /// (<see cref="LabelWidth"/>), so the value column sits at the same place under every heading as the one grid kept it until then.
    /// </summary>
    public static IRenderable StatisticsTab(TokenTally tally, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(tally);
        var apart = ApartRows(tally);
        if (tally.Session.IsEmpty && window is null && apart.Count == 0)
        {
            return new Text(NothingCounted, Theme.DimText);
        }

        // The sections as they will stand, so the label column is sized once over all of them.
        var sections = new List<(string Heading, string Note, IReadOnlyList<(string Label, string Value)> Rows)>
        {
            // The Context section first: the window is known before anything was counted, and its note is the source — where the figure came from.
            (ContextHeading, window?.Source ?? WindowUnknown, ContextRows(tally, window)),
        };
        var extra = new List<(string, string)>(4);
        if (tally.LearningRequests > 0 && !tally.Session.IsEmpty)
        {
            extra.Add((LearningLabel, LearningValue(tally)));
        }

        extra.AddRange(apart);

        if (!tally.Session.IsEmpty)
        {
            sections.Add((LastReplyHeading, tally.LastReply.IsEmpty ? NoReportNote : "", RowsOf(tally.LastReply, averaged: false)));
            sections.Add((ConversationHeading, Join(SummedNote, tally.UnreportedReplies > 0 ? Unreported(tally.UnreportedReplies) : ""), RowsOf(tally.Conversation, averaged: true)));
            sections.Add((SessionHeading, SummedNote, [.. RowsOf(tally.Session, averaged: true), .. extra]));
        }

        int width = LabelWidth(sections.SelectMany(s => s.Rows).Concat(extra));
        var parts = new List<IRenderable>();
        foreach (var (heading, note, rows) in sections)
        {
            if (parts.Count > 0)
            {
                parts.Add(new Text(" "));
            }

            parts.Add(new SectionRule(SectionRule.Markup(heading, note: note)));
            if (rows.Count > 0)
            {
                parts.Add(Body(rows, width));
            }
        }

        if (tally.Session.IsEmpty)
        {
            // Nothing counted yet: said under the Context section, the /claude runs, the bots and the advisors (if any) under it.
            parts.Add(new Text(" "));
            parts.Add(new Indented(new Text(NothingCounted, Theme.DimText), SystemPromptSummary.BodyIndent));
            if (extra.Count > 0)
            {
                parts.Add(Body(extra, width));
            }
        }

        return new Rows(parts);

        static IReadOnlyList<(string, string)> RowsOf(TokenUsage usage, bool averaged) => usage.IsEmpty ? [] : Rows(usage, averaged);
    }

    /// <summary>The label column's width on the Statistics tab: the widest label any section shows (2026-10-10). Pure.</summary>
    public static int LabelWidth(IEnumerable<(string Label, string Value)> rows) => rows.Select(r => r.Label.Length).DefaultIfEmpty(0).Max();

    /// <summary>A section's rows as two columns, the labels <paramref name="width"/> wide, indented under its rule.</summary>
    private static IRenderable Body(IReadOnlyList<(string Label, string Value)> rows, int width)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(width).PadRight(SlashCommands.HelpColumnGap))
            .AddColumn(new GridColumn().PadRight(0));
        foreach (var (label, value) in rows)
        {
            grid.AddRow(new Text(label, Theme.AccentSecondary), new Text(value, Theme.Body));
        }

        return new Indented(grid, SystemPromptSummary.BodyIndent);
    }

    /// <summary>The row of the prompt tokens the cache served and took (2026-09-27, the Anthropic API), shown once a request reported either. Pinned.</summary>
    public const string CacheLabel = "Cache";

    /// <summary>The row of what the scope cost (2026-09-27, the Anthropic API at its list price), shown once a request was priced. Pinned.</summary>
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

    /// <summary>
    /// The rows of what ran apart from this chat's figures, each shown once it counted something: the <c>/claude</c> runs, the
    /// <c>/botchat</c> bots and the advisors (the last two 2026-10-10). Pure.
    /// </summary>
    private static List<(string Label, string Value)> ApartRows(TokenTally tally)
    {
        var rows = new List<(string, string)>(3);
        if (tally.ClaudeRuns > 0)
        {
            rows.Add((ClaudeLabel, ClaudeValue(tally)));
        }

        if (tally.BotRequests > 0)
        {
            rows.Add((BotsLabel, ApartValue(tally.BotRequests, tally.Bots)));
        }

        if (tally.AdvisorRequests > 0)
        {
            rows.Add((AdvisorsLabel, ApartValue(tally.AdvisorRequests, tally.Advisors)));
        }

        return rows;
    }

    /// <summary>The row that says what the <c>/botchat</c> bots used since launch (2026-10-10). Pinned.</summary>
    public const string BotsLabel = "Botchat";

    /// <summary>The row that says what the advisors used since launch (2026-10-10). Pinned.</summary>
    public const string AdvisorsLabel = "Advisors";

    /// <summary><c>3 requests · 12,506 in · 250 out</c>: requests on servers of their own, so in and out apart and no cost (2026-10-10). Pinned.</summary>
    public static string ApartValue(int requests, TokenUsage usage) =>
        Plural(requests, "request", "requests") + Sep + N0(usage.Input) + " in" + Sep + N0(usage.Output) + " out";

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
            nothing.AddRange(ApartLines(tally));
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

        lines.AddRange(ApartLines(tally));
        return lines;
    }

    /// <summary>The plain lines of <see cref="ApartRows"/>: <c>claude: …</c>, <c>botchat: …</c>, <c>advisors: …</c>.</summary>
    private static IEnumerable<string> ApartLines(TokenTally tally) => ApartRows(tally).Select(row => row.Label.ToLowerInvariant() + ": " + row.Value);

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
