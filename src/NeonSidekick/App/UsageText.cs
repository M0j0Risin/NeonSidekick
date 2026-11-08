using System.Globalization;
using System.Text;
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

    internal const string Sep = " · ";

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
    /// <see cref="SectionRule"/> with its note (the source, the replies that reported no usage…) dim on the rule, the <c>/usage</c> Prompt and Tools tabs' shape,
    /// and its rows <see cref="SystemPromptSummary.BodyIndent"/> cells in under it; one label width for every section
    /// (<see cref="LabelWidth"/>), so the value column sits at the same place under every heading as the one grid kept it until then.
    /// </summary>
    public static IRenderable StatisticsTab(TokenTally tally, ContextLength? window)
    {
        var shown = Sections(tally, window);
        if (shown.Nothing)
        {
            return new Text(NothingCounted, Theme.DimText);
        }

        int width = LabelWidth(shown.Sections.SelectMany(s => s.Rows).Concat(shown.Apart));
        var parts = new List<IRenderable>();
        foreach (var section in shown.Sections)
        {
            if (parts.Count > 0)
            {
                parts.Add(new Text(" "));
            }

            parts.Add(new SectionRule(SectionRule.Markup(section.Heading, note: section.Note)));
            if (section.Rows.Count > 0)
            {
                parts.Add(Body(section.Rows, width));
            }
        }

        if (shown.NothingYet)
        {
            // Nothing counted yet: said under the Context section, the /claude runs, the bots and the advisors (if any) under it.
            parts.Add(new Text(" "));
            parts.Add(new Indented(new Text(NothingCounted, Theme.DimText), SystemPromptSummary.BodyIndent));
            if (shown.Apart.Count > 0)
            {
                parts.Add(Body(shown.Apart, width));
            }
        }

        return new Rows(parts);
    }

    /// <summary>
    /// What the Statistics tab shows, before it is drawn (2026-10-16, so the Web UI's usage card shows the same and its words can't drift): the
    /// Context section first (its note the window's source), then, once anything was counted, the last reply, this conversation and the launch
    /// with the extras (skill learning, <c>/claude</c>, the bots, the advisors) under it. While nothing was counted (<see cref="UsageSections.NothingYet"/>)
    /// the extras stand apart (<see cref="UsageSections.Apart"/>); with no window and no extras either, <see cref="UsageSections.Nothing"/>. Pure.
    /// </summary>
    public static UsageSections Sections(TokenTally tally, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(tally);
        var apart = ApartRows(tally);
        var extra = new List<(string, string)>(4);
        if (tally.LearningRequests > 0 && !tally.Session.IsEmpty)
        {
            extra.Add((LearningLabel, LearningValue(tally)));
        }

        extra.AddRange(apart);
        // The Context section first: the window is known before anything was counted, and its note is the source — where the figure came from.
        var sections = new List<UsageSection> { new(ContextHeading, window?.Source ?? WindowUnknown, ContextRows(tally, window)) };
        if (!tally.Session.IsEmpty)
        {
            sections.Add(new(LastReplyHeading, tally.LastReply.IsEmpty ? NoReportNote : "", RowsOf(tally.LastReply, averaged: false)));
            // No SummedNote on the pane's two summed headings since 2026-10-12 (the user's call); the plain lines keep it.
            sections.Add(new(ConversationHeading, tally.UnreportedReplies > 0 ? Unreported(tally.UnreportedReplies) : "", RowsOf(tally.Conversation, averaged: true)));
            sections.Add(new(SessionHeading, "", [.. RowsOf(tally.Session, averaged: true), .. extra]));
        }

        bool nothingYet = tally.Session.IsEmpty;
        return new UsageSections(sections, nothingYet ? extra : [], nothingYet, nothingYet && window is null && apart.Count == 0);

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
    /// In the plain lines' conversation and launch totals: they add every request up, the re-sent history counted each time — a
    /// cost figure, not the context (that is the Context section). Beside the pane's two headings too until 2026-10-12, when the
    /// user had it taken off there.
    /// </summary>
    public const string SummedNote = "every request summed";

    // ── The Context tab (2026-11-01) ────────────────────────────────────────

    /// <summary>The second tab's title (2026-11-01, feature idea #1): what fills the context window. <c>/usage context</c> opens the pane on it.</summary>
    public const string ContextTabTitle = "Context";

    /// <summary>The Context tab's index among <c>/usage</c>'s tabs.</summary>
    public const int ContextTabIndex = 1;

    /// <summary>The word <c>/usage context</c> takes. Pinned.</summary>
    public const string ContextWord = "context";

    /// <summary>The argument list's note beside <see cref="ContextWord"/>.</summary>
    public const string ContextWordNote = "what fills the context window";

    // ── The tabs from /sys (2026-11-07, the user's ask: one pane for what the model is sent) ──

    /// <summary>The Prompt tab's index among <c>/usage</c>'s tabs (2026-11-07): the system message, <c>/sys</c>' first tab until then.</summary>
    public const int PromptTabIndex = 2;

    /// <summary>The Tools tab's index (2026-11-07): the tools the next turn offers, <c>/sys</c>' second tab until then.</summary>
    public const int ToolsTabIndex = 3;

    /// <summary>The Skills tab's index (2026-11-07): the skills catalog, a section of <c>/sys</c>' Prompt tab until then.</summary>
    public const int SkillsTabIndex = 4;

    /// <summary>The word <c>/usage prompt</c> takes (2026-11-07). Pinned.</summary>
    public const string PromptWord = "prompt";

    /// <summary>The argument list's note beside <see cref="PromptWord"/>.</summary>
    public const string PromptWordNote = "the system prompt the next turn sends";

    /// <summary>The word <c>/usage tools</c> takes (2026-11-07). Pinned.</summary>
    public const string ToolsWord = "tools";

    /// <summary>The argument list's note beside <see cref="ToolsWord"/>.</summary>
    public const string ToolsWordNote = "the tools the next turn offers";

    /// <summary>The word <c>/usage skills</c> takes (2026-11-07). Pinned.</summary>
    public const string SkillsWord = "skills";

    /// <summary>The argument list's note beside <see cref="SkillsWord"/>.</summary>
    public const string SkillsWordNote = "the skills the next turn offers";

    /// <summary>The refusal of any other argument (the three words from <c>/sys</c> since 2026-11-07). Pinned.</summary>
    public const string UsageError = "/usage takes context, prompt, tools or skills, or nothing for the pane.";

    /// <summary>The tab's one line on the Claude Code server, which keeps the conversation itself. Pinned.</summary>
    public const string ClaudeCodeContext = "The Claude Code server manages its own context, so there is nothing here to break down.";

    /// <summary>The Context tab's rows, in order (each in its piece's color, so the rows are the bar's legend).</summary>
    public const string PromptLabel = "System prompt";
    public const string ToolsLabel = "Tools";
    public const string HistoryLabel = "History";
    public const string PicturesLabel = "Pictures";
    public const string ThinkingLabel = "Thinking";
    public const string FreeLabel = "Free";
    public const string OverLabel = "Over";

    /// <summary>
    /// The History row's note mid-reply with nothing of the turn's sent yet: the row is the last count less the prompt and tools. Pinned.
    /// </summary>
    public const string HistoryFromCount = "from the server's count";

    /// <summary>The History row's note mid-reply (2026-11-02, the review): the history as the turn's last request carried it. Pinned.</summary>
    public const string HistoryAsSent = "as last sent";

    /// <summary>What a mid-reply note adds while the wait is a reply's (not a model load's or a <c>/print</c>'s). Pinned.</summary>
    public const string ReplyRunningNote = "a reply is running";

    /// <summary>The Thinking row's note with <c>LLM preserve thinking</c> off: the next request sends none of it back. Pinned.</summary>
    public const string ThinkingNotSent = "not sent back (LLM preserve thinking is off)";

    /// <summary>The Thinking row's note with it on. Pinned.</summary>
    public const string ThinkingSent = "every turn's sent back (LLM preserve thinking is on)";

    /// <summary>
    /// The Thinking row's note with it on where the server takes back only the turn in flight's (2026-11-02, the review: the Anthropic
    /// and OpenAI APIs). Pinned.
    /// </summary>
    public const string ThinkingInFlightOnly = "only the turn in flight's sent back on this server";

    /// <summary>The note under the headline before anything was sent: the first request also carries the opening calls' results, not estimated here. Pinned.</summary>
    public const string NothingSentYet = "nothing sent yet; the first request also carries the opening calls (the clock, the folder, the memory list)";

    /// <summary>The note under the headline once a compaction or a <c>/rewind</c> forgot the count (2026-11-02, the review). Pinned.</summary>
    public const string CountedAgain = "the server counts the history again at the next request";

    /// <summary>The headline's reference line: the server's own count of the last request, which a <c>/tools</c> change since has made stale. Pinned.</summary>
    public static string ServerCounted(long tokens) => "the server counted " + N0(tokens) + " at the last request";

    /// <summary>The reference line after a connect, before the new model's first count (2026-11-02, the review): the count is the previous model's. Pinned.</summary>
    public static string PreviousModelCounted(long tokens) => "the previous model counted " + N0(tokens) + " at the last request";

    /// <summary>The Tools row's note: <c>41 tools in 9 groups</c>.</summary>
    public static string ToolsNote(int tools, int groups) => Plural(tools, "tool", "tools") + " in " + Plural(groups, "group", "groups");

    /// <summary>A switchable group's note: <c>off saves 12%</c> of the window (its tools and its prompt sentences), <c>off saves &lt;1%</c> for a sliver.</summary>
    public static string OffSaves(int percent) => "off saves " + (percent < 1 ? "<1%" : PercentText(percent));

    /// <summary>The headline: <c>~21,400 of 32,768 (65%)</c>, or <c>~21,400 tokens; window unknown</c>. Pinned.</summary>
    public static string ContextHeadline(ContextFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        long estimate = facts.Estimate;
        return facts.Window is { } w
            ? EstimateMark + N0(estimate) + " of " + N0(w.Tokens) + " (" + PercentText(Percent(estimate, w) ?? 0) + ")"
            : EstimateMark + N0(estimate) + " tokens; window " + WindowUnknown;
    }

    /// <summary>
    /// The dim line under the headline: the server's last count (the previous model's after a connect, 2026-11-02), else
    /// <see cref="CountedAgain"/> once something was sent (a compaction or a <c>/rewind</c> forgot the count), else <see cref="NothingSentYet"/>.
    /// </summary>
    public static string ContextReference(ContextFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.LastCounted > 0 ? facts.PreviousModel ? PreviousModelCounted(facts.LastCounted) : ServerCounted(facts.LastCounted)
            : facts.Sent ? CountedAgain
            : NothingSentYet;
    }

    private static string Approx(long tokens) => tokens > 0 ? EstimateMark + CompactNumber(tokens) : "0";

    /// <summary>A mid-reply note: <paramref name="note"/>, then <see cref="ReplyRunningNote"/> while the wait is a reply's.</summary>
    private static string MidReply(string note, bool replyRunning) => replyRunning ? note + "; " + ReplyRunningNote : note;

    /// <summary>
    /// The Context tab's rows (2026-11-01), pure: the system prompt (its parts largest first), the tools with a row per offered group
    /// (largest first, ties by name; <c>off saves</c> for a group with a switch while the window is known, its prompt sentences counted), the
    /// history (its turns), the pictures and the thinking as the next request carries them, then what is free (or over). Mid-reply they are
    /// the turn's last request's, marked <see cref="HistoryAsSent"/>; before it, the history is one row from the server's count, with no
    /// picture or thinking rows. The rows add up to <see cref="ContextHeadline"/>'s estimate.
    /// </summary>
    public static IReadOnlyList<ContextRow> ContextRows(ContextFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var rows = new List<ContextRow>(12);
        var parts = facts.PromptParts.Where(p => p.Tokens > 0).OrderByDescending(p => p.Tokens).ThenBy(p => p.Label, StringComparer.Ordinal);
        rows.Add(new(PromptLabel, Approx(facts.PromptTokens), string.Join(Sep, parts.Select(p => p.Label + " " + CompactNumber(p.Tokens))), ContextPiece.Prompt));
        rows.Add(new(ToolsLabel, Approx(facts.ToolTokens), facts.Groups.Count > 0 ? ToolsNote(facts.Groups.Sum(g => g.Tools), facts.Groups.Count) : facts.NoToolsNote ?? "", ContextPiece.Tools));
        foreach (var group in facts.Groups.OrderByDescending(g => g.Tokens).ThenBy(g => g.Label, StringComparer.Ordinal))
        {
            string note = group.Switchable && Percent(group.Tokens + group.PromptTokens, facts.Window) is { } share ? OffSaves(share) : "";
            rows.Add(new(group.Label, Approx(group.Tokens), note, ContextPiece.Tools, Sub: true));
        }

        if (facts.History is { } history)
        {
            string note = Plural(facts.Turns, "turn", "turns") + (history.ToolResultTokens > 0 ? Sep + "tool results " + CompactNumber(history.ToolResultTokens) : "")
                + (facts.AsLastSent ? Sep + MidReply(HistoryAsSent, facts.ReplyRunning) : "");
            rows.Add(new(HistoryLabel, Approx(history.HistoryTokens), note, ContextPiece.History));
            if (history.Pictures > 0)
            {
                rows.Add(new(PicturesLabel, Approx(history.PictureTokenCount), Plural(history.Pictures, "picture", "pictures"), ContextPiece.Pictures));
            }

            string thinking = !facts.PreserveThinking ? ThinkingNotSent : facts.InFlightOnly ? ThinkingInFlightOnly : ThinkingSent;
            rows.Add(new(ThinkingLabel, Approx(history.ThinkingTokens), thinking, ContextPiece.Thinking));
        }
        else if (facts.HistoryTokens is { } counted)
        {
            rows.Add(new(HistoryLabel, Approx(counted), MidReply(HistoryFromCount, facts.ReplyRunning), ContextPiece.History));
        }

        if (facts.Window is { } window)
        {
            long free = window.Tokens - facts.Estimate;
            // Free is the window less an estimate, so an estimate too (2026-11-02, review P1-3), marked as Over is.
            rows.Add(free >= 0
                ? new(FreeLabel, EstimateMark + CompactNumber(free), "", ContextPiece.Free)
                : new(OverLabel, EstimateMark + CompactNumber(-free), "", ContextPiece.Over));
        }

        return rows;
    }

    /// <summary>
    /// The Context tab (2026-11-01): a <see cref="SectionRule"/> with the window's source, the headline and its reference line, the bar
    /// (<see cref="ContextBar"/>, only with a window) and the rows as three columns — label in its piece's color, figure, dim note. The
    /// Claude Code server's one line instead. <c>Text</c> cells, never <c>Markup</c>; a label past <see cref="LabelCap"/> cells ends in an
    /// ellipsis (<see cref="CutToCells"/>, 2026-11-02, the review: a long MCP server's name left the notes a few cells at 80 columns).
    /// </summary>
    public static IRenderable ContextTab(ContextFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.ClaudeCode)
        {
            return new Text(ClaudeCodeContext, Theme.DimText);
        }

        var parts = new List<IRenderable>
        {
            new SectionRule(SectionRule.Markup(ContextTabTitle, note: facts.Window?.Source ?? WindowUnknown)),
            new Indented(new Text(ContextHeadline(facts), Theme.Body), SystemPromptSummary.BodyIndent),
            new Indented(new Text(ContextReference(facts), Theme.DimText), SystemPromptSummary.BodyIndent),
        };
        if (facts.Window is { } window)
        {
            parts.Add(new Indented(new ContextBar(BarPieces(facts), window.Tokens, SystemPromptSummary.BodyIndent), SystemPromptSummary.BodyIndent));
        }

        var rows = ContextRows(facts);
        int labels = Math.Min(LabelCap, rows.Select(r => TextCells.Width(r.Label) + (r.Sub ? SubIndent : 0)).DefaultIfEmpty(0).Max());
        int values = rows.Select(r => TextCells.Width(r.Value)).DefaultIfEmpty(0).Max();
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().Width(labels).PadRight(SlashCommands.HelpColumnGap))
            .AddColumn(new GridColumn().NoWrap().Width(values).RightAligned().PadRight(SlashCommands.HelpColumnGap))
            .AddColumn(new GridColumn().PadRight(0));
        foreach (var row in rows)
        {
            grid.AddRow(
                new Text(CutToCells((row.Sub ? new string(' ', SubIndent) : "") + row.Label, labels), row.Sub ? Theme.Body : PieceStyle(row.Piece)),
                new Text(row.Value, row.Piece == ContextPiece.Over ? new Style(Theme.Warn) : Theme.Body),
                new Text(row.Note, Theme.DimText));
        }

        parts.Add(new Text(" "));
        parts.Add(new Indented(grid, SystemPromptSummary.BodyIndent));
        return new Rows(parts);
    }

    /// <summary>The cells a group's row sits in under the Tools row.</summary>
    public const int SubIndent = 2;

    /// <summary>The label column's widest (2026-11-02, the review), its indent included; a longer label ends in an ellipsis. The plain lines keep it whole.</summary>
    public const int LabelCap = 24;

    /// <summary>
    /// <paramref name="text"/> in at most <paramref name="cells"/> cells, a cut one ending in <c>…</c> (2026-11-02, the review): by cells, so a
    /// wide glyph in an MCP server's name counts two. Spectre's own ellipsis drops a word too long for its column rather than cutting it.
    /// </summary>
    public static string CutToCells(string text, int cells) => TextCells.Cut(text, cells);   // the one cut (2026-11-02, review P2-9)

    /// <summary>The bar's pieces in order with their tokens: prompt, tools, history, pictures, thinking (the free part is what the window has left).</summary>
    public static IReadOnlyList<(ContextPiece Piece, long Tokens)> BarPieces(ContextFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var pieces = new List<(ContextPiece, long)>(5) { (ContextPiece.Prompt, facts.PromptTokens), (ContextPiece.Tools, facts.ToolTokens) };
        if (facts.History is { } history)
        {
            pieces.Add((ContextPiece.History, history.HistoryTokens));
            pieces.Add((ContextPiece.Pictures, history.PictureTokenCount));
            pieces.Add((ContextPiece.Thinking, history.ThinkingTokens));
        }
        else
        {
            pieces.Add((ContextPiece.History, facts.HistoryTokens ?? 0));
        }

        return pieces;
    }

    /// <summary>
    /// The bar's cells per piece over <paramref name="width"/> cells (2026-11-01), pure: each piece its share of the window (of the
    /// estimate once that is past the window, so the bar is full), at least one cell for a piece over zero, the boundaries rounded
    /// cumulatively so the last piece takes the rounding; the free cells are what is left. Since 2026-11-02 (the review) a piece never takes
    /// the cells the later pieces over zero need, nor the last free cell while some of the window is free; past the window the pieces share
    /// <c>width − 1</c> cells, the last one the warning's. A bar too narrow for every piece drops the latest.
    /// </summary>
    public static IReadOnlyList<int> BarCells(IReadOnlyList<(ContextPiece Piece, long Tokens)> pieces, long window, int width)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        var cells = new int[pieces.Count];
        long total = pieces.Sum(p => p.Tokens);
        long whole = Math.Max(window, total);
        if (whole <= 0 || width <= 0)
        {
            return cells;
        }

        // Past the window, the warning cell; short of it, one free cell.
        int room = total != window && width > 1 ? width - 1 : width;
        long sum = 0;
        int used = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Tokens <= 0 || used >= room)
            {
                continue;
            }

            int later = 0;
            for (int j = i + 1; j < pieces.Count; j++)
            {
                later += pieces[j].Tokens > 0 ? 1 : 0;
            }

            sum += pieces[i].Tokens;
            int cap = Math.Max(used + 1, room - later);
            int end = Math.Min(cap, Math.Max(used + 1, (int)Math.Round(sum * (double)room / whole)));
            cells[i] = end - used;
            used = end;
        }

        return cells;
    }

    /// <summary>
    /// The bar's glyph for a piece (2026-11-02, the review): <c>█</c> in its color; with no color (<c>NO_COLOR</c>, a legacy console) each piece
    /// its own shade so the pieces still tell apart: prompt <c>█</c>, tools <c>▓</c>, history <c>▒</c>, pictures and thinking <c>▚</c>.
    /// </summary>
    public static char PieceGlyph(ContextPiece piece, bool noColor) => !noColor ? '█' : piece switch
    {
        ContextPiece.Prompt => '█',
        ContextPiece.Tools => '▓',
        ContextPiece.History => '▒',
        _ => '▚',
    };

    /// <summary>A piece's color: the prompt Primary, tools Secondary, history Tertiary, pictures Warm, thinking Highlight, free Dimmer, over Warn.</summary>
    public static Color PieceColor(ContextPiece piece) => piece switch
    {
        ContextPiece.Prompt => Theme.Primary,
        ContextPiece.Tools => Theme.Secondary,
        ContextPiece.History => Theme.Tertiary,
        ContextPiece.Pictures => Theme.Warm,
        ContextPiece.Thinking => Theme.Highlight,
        ContextPiece.Over => Theme.Warn,
        _ => Theme.Dimmer,
    };

    private static Style PieceStyle(ContextPiece piece) => new(PieceColor(piece));

    /// <summary>
    /// The Context tab as plain lines (2026-11-01), for a console with no pane: <c>Context: ~21,400 of 32,768 tokens (65%, n_ctx on /props)</c>,
    /// the reference line, then a line per row (<c>system prompt: ~3.1k (Rules 1.4k · Project 0.9k)</c>, a group's two spaces in).
    /// </summary>
    public static IReadOnlyList<string> ContextLines(ContextFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.ClaudeCode)
        {
            return [ClaudeCodeContext];
        }

        long estimate = facts.Estimate;
        var lines = new List<string>(12)
        {
            facts.Window is { } w
                ? $"Context: {EstimateMark}{N0(estimate)} of {N0(w.Tokens)} tokens ({PercentText(Percent(estimate, w) ?? 0)}, {w.Source})"
                : $"Context: {EstimateMark}{N0(estimate)} tokens; window {WindowUnknown}",
            "(" + ContextReference(facts) + ")",
        };
        foreach (var row in ContextRows(facts))
        {
            string label = row.Sub ? new string(' ', SubIndent) + row.Label : row.Label.ToLowerInvariant();
            lines.Add(label + ": " + row.Value + (row.Note.Length > 0 ? " (" + row.Note + ")" : ""));
        }

        return lines;
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

    // ── The reply footer (2026-11-01, feature idea #4) ──────────────────────

    /// <summary>What stands between a footer's parts, and before a slow tool's time. Pinned.</summary>
    public const string FooterSeparator = " · ";

    /// <summary>A tool that took this long or more shows its time on its result line, with <see cref="Settings.AppSettingsData.ReplyFooter"/> on. Pinned.</summary>
    public static readonly TimeSpan SlowTool = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The dim line under a reply (<see cref="Settings.AppSettingsData.ReplyFooter"/>): <c>12.4s · 812 tok · 48 tok/s · 3 tool calls · qwen3-32b</c>, a
    /// part with no data left out (no usage report: the time, the tool calls and the model; "tool calls" since 2026-11-02, the review, as the
    /// run's summary counts them). The time as the thinking fold writes it
    /// (<see cref="UI.ThinkingFoldText.Duration"/>), the speed whole, the model its id's last path segment. Pinned.
    /// </summary>
    public static string Footer(FooterFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var parts = new List<string>(5) { UI.ThinkingFoldText.Duration(facts.Elapsed) };
        if (facts.OutputTokens > 0)
        {
            parts.Add(CompactNumber(facts.OutputTokens) + " tok");
        }

        if (facts.TokensPerSecond is double speed && speed > 0)
        {
            parts.Add(Speed(speed, 0));
        }

        if (facts.ToolCalls > 0)
        {
            parts.Add(Plural(facts.ToolCalls, "tool call", "tool calls"));
        }

        if (ChatScreen.ModelLabel(facts.Model) is { Length: > 0 } model)
        {
            parts.Add(model);
        }

        return string.Join(FooterSeparator, parts);
    }

    /// <summary>A slow tool's suffix on its result line, <c> · 2.1s</c>; null under <see cref="SlowTool"/>. Pinned.</summary>
    public static string? ToolTime(TimeSpan took) => took < SlowTool ? null : FooterSeparator + UI.ThinkingFoldText.Duration(took);

    /// <summary><c>0.8 s</c>.</summary>
    public static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// The hint row's part, on one base: the context in use over the window, its share, and the last
    /// known speed (<c>4.6k / 151.4k · 3% · 42 tok/s</c>; <see cref="TokenTally.LastKnownSpeed"/> since 2026-10-26, the user's
    /// choice: a reply of untimed requests alone keeps the one before's); <c>4.6k tokens · 42 tok/s</c> while the
    /// window is unknown; null while nothing is in the context (before the first reply, after
    /// <c>/clear</c>) and while <see cref="TokenTally.HintHidden"/> (an LLM connect, 2026-10-03, until the new
    /// model reports), so the row reads as before. Never the conversation's summed total: that
    /// counts the re-sent history once per request and sits several times past the real context.
    /// </summary>
    public static string? HintPart(TokenTally tally, ContextLength? window) => HintParts(tally, window)?.Joined;

    /// <summary>
    /// <see cref="HintPart"/> in its three figures (2026-10-15, the Web UI's status row, which drops them one by one on a narrow phone): the
    /// context in use (over the window when known), its share, and the speed. Null as <see cref="HintPart"/> is.
    /// </summary>
    public static UsageParts? HintParts(TokenTally tally, ContextLength? window)
    {
        ArgumentNullException.ThrowIfNull(tally);
        long inUse = tally.LastRequest.Total;
        if (inUse == 0 || tally.HintHidden)
        {
            return null;
        }

        return ContextParts(inUse, window, "", tally.LastKnownSpeed is { } speed ? Speed(speed, 0) : null);
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
    /// load's spinner); the live estimate still shows then, being the new model's own count. Before the live rate is given
    /// (<see cref="StreamMeter.MinRateSpan"/>, 2026-10-26) the speed is the last known one, unmarked, and none while hidden.
    /// </summary>
    public static string? BusyHintPart(TokenTally tally, ContextLength? window, MidTurnUsage mode, StreamMeter.Reading live) =>
        BusyHintParts(tally, window, mode, live)?.Joined;

    /// <summary><see cref="BusyHintPart"/> in its three figures (2026-10-15), each estimated one with its <see cref="EstimateMark"/>.</summary>
    public static UsageParts? BusyHintParts(TokenTally tally, ContextLength? window, MidTurnUsage mode, StreamMeter.Reading live)
    {
        ArgumentNullException.ThrowIfNull(tally);
        long known = tally.LastRequest.Total;
        if (mode == MidTurnUsage.Estimate && live is { Streaming: true, Chunks: > 0 })
        {
            string? streaming = live.ChunksPerSecond is { } rate ? EstimateMark + Speed(rate, 0)
                : !tally.HintHidden && tally.LastKnownSpeed is { } last ? Speed(last, 0)
                : null;
            return ContextParts(known + live.Chunks, window, EstimateMark, streaming);
        }

        if (known == 0 || tally.HintHidden)
        {
            return null;
        }

        return ContextParts(known, window, "", tally.LastKnownSpeed is { } speed ? Speed(speed, 0) : null);
    }

    /// <summary>
    /// The context in use over the window (<c>4.6k / 151.4k</c>) and its share (<c>3%</c>), or <c>4.6k tokens</c> and no share while the window
    /// is unknown; <paramref name="mark"/> ahead of each figure, then <paramref name="speed"/> as given.
    /// </summary>
    private static UsageParts ContextParts(long inUse, ContextLength? window, string mark, string? speed) =>
        Percent(inUse, window) is { } percent
            ? new UsageParts(mark + CompactNumber(inUse) + " / " + CompactNumber(window!.Value.Tokens), mark + PercentText(percent), speed)
            : new UsageParts(mark + Compact(inUse), null, speed);

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
    private static TimeSpan? Wait(TokenUsage usage, bool averaged) =>
        averaged ? usage.AverageToFirstToken : usage.WaitUnknown ? null : usage.ToFirstToken;   // a burst's wait is unknown, never 0 s (2026-10-26)

    private static string N0(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary><c>1 reply</c>, <c>2 replies</c>.</summary>
    public static string Plural(long count, string one, string many) =>
        count == 1 ? "1 " + one : N0(count) + " " + many;
}

/// <summary>
/// The usage figures the hint row joins (2026-10-15): the tokens in use (over the window when known), the share, the speed — each with its own
/// estimate mark. <see cref="Joined"/> is the hint row's text; the Web UI shows them apart so a narrow phone can drop one.
/// </summary>
public sealed record UsageParts(string Tokens, string? Percent, string? Speed)
{
    /// <summary>The figures as the hint row reads them: <c>4.6k / 151.4k · 3% · 42 tok/s</c>.</summary>
    public string Joined => Tokens + (Percent is null ? "" : UsageText.Sep + Percent) + (Speed is null ? "" : UsageText.Sep + Speed);
}

/// <summary>One section of the <c>/usage</c> pane (2026-10-16): its heading, its note on the rule, its label and value rows.</summary>
public sealed record UsageSection(string Heading, string Note, IReadOnlyList<(string Label, string Value)> Rows);

/// <summary>
/// The <c>/usage</c> pane's sections as <see cref="UsageText.Sections"/> gives them (2026-10-16): in order, the Context one first; the extras
/// standing apart while nothing was counted (<see cref="NothingYet"/>); and <see cref="Nothing"/> when there is nothing to show at all.
/// </summary>
public sealed record UsageSections(IReadOnlyList<UsageSection> Sections, IReadOnlyList<(string Label, string Value)> Apart, bool NothingYet, bool Nothing);

/// <summary>
/// What the reply footer says (2026-11-01, feature idea #4, <see cref="UsageText.Footer"/>): the turn's time less the app's asks inside its tools,
/// the reply's output tokens (0: none reported), its decode speed (null: untimed), the model's own tool calls and the model's id.
/// </summary>
public sealed record FooterFacts(TimeSpan Elapsed, long OutputTokens, double? TokensPerSecond, int ToolCalls, string? Model);

/// <summary>
/// One offered tool group on the <c>/usage</c> Context tab (2026-11-01): its label, its tools, their estimated weight, whether a setting switches
/// it, and (2026-11-02, the review) the prompt's tokens that switch also takes out.
/// </summary>
public sealed record ContextToolGroup(string Label, int Tools, long Tokens, bool Switchable, long PromptTokens = 0);

/// <summary>The pieces of the Context tab, each with its color on the bar and its row.</summary>
public enum ContextPiece
{
    Prompt,
    Tools,
    History,
    Pictures,
    Thinking,
    Free,
    Over,
}

/// <summary>A row of the Context tab: its label, figure, dim note, piece (its color) and whether it is a group's row under Tools.</summary>
public sealed record ContextRow(string Label, string Value, string Note, ContextPiece Piece, bool Sub = false);

/// <summary>
/// What the <c>/usage</c> Context tab is built from (2026-11-01), gathered by the screen without side effects: the window, the server's last
/// count (0 for none), the system prompt's sections and the offered tool groups as estimated tokens, the history's breakdown (mid-reply the
/// turn's last request's, <see cref="AsLastSent"/>; null before it), its turns, the preserve-thinking switch and whether the server is Claude Code.
/// </summary>
public sealed record ContextFacts(
    ContextLength? Window,
    long LastCounted,
    IReadOnlyList<(string Label, long Tokens)> PromptParts,
    IReadOnlyList<ContextToolGroup> Groups,
    string? NoToolsNote,
    ConversationCompactor.TokenEstimate? History,
    int Turns,
    bool PreserveThinking,
    bool ClaudeCode = false)
{
    /// <summary>Whether the server takes back only the turn in flight's thinking whatever <see cref="PreserveThinking"/> says (2026-11-02, the review).</summary>
    public bool InFlightOnly { get; init; }

    /// <summary>Whether <see cref="History"/> is the turn's last request's, read mid-reply (2026-11-02, the review).</summary>
    public bool AsLastSent { get; init; }

    /// <summary>Whether the wait is a reply's, not a model load's or a <c>/print</c>'s (2026-11-02, the review).</summary>
    public bool ReplyRunning { get; init; }

    /// <summary>Whether this conversation sent anything, so a zero <see cref="LastCounted"/> is a forgotten count (2026-11-02, the review).</summary>
    public bool Sent { get; init; }

    /// <summary>Whether <see cref="LastCounted"/> is the previous model's: a connect since, and no count of the new one (2026-11-02, the review).</summary>
    public bool PreviousModel { get; init; }

    /// <summary>The system prompt's estimate: its parts summed.</summary>
    public long PromptTokens => PromptParts.Sum(p => p.Tokens);

    /// <summary>The tools' estimate: the groups summed.</summary>
    public long ToolTokens => Groups.Sum(g => g.Tokens);

    /// <summary>The history's figure: the breakdown's, or mid-reply the last count less the prompt and tools (none before a count).</summary>
    public long? HistoryTokens => History is { } h ? h.HistoryTokens : LastCounted > 0 ? Math.Max(0, LastCounted - PromptTokens - ToolTokens) : null;

    /// <summary>The headline: every piece summed, as the next request would carry them.</summary>
    public long Estimate => PromptTokens + ToolTokens + (History is { } h ? h.Total : HistoryTokens ?? 0);
}

/// <summary>
/// The Context tab's bar (2026-11-01): one <c>█</c> run per piece in its color (<see cref="UsageText.BarCells"/>), then <c>░</c> in the dimmer
/// color for what is free; once the estimate is past the window the bar is full and its last cell is in the warning color. Drawn at the width
/// it is given less <paramref name="margin"/>, so its right end matches the indent on its left.
/// </summary>
internal sealed class ContextBar(IReadOnlyList<(ContextPiece Piece, long Tokens)> pieces, long window, int margin) : IRenderable
{
    public Measurement Measure(RenderOptions options, int maxWidth) => new(1, Math.Max(1, maxWidth - margin));

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        int width = Math.Max(1, maxWidth - margin);
        var cells = UsageText.BarCells(pieces, window, width);
        bool over = pieces.Sum(p => p.Tokens) > window;
        bool noColor = !Theme.HasBackgrounds(options);   // 16 colors too (2026-11-02, review P1-6): its shades can't be told apart
        var segments = new List<Segment>(pieces.Count + 3);
        int used = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (cells[i] > 0)
            {
                segments.Add(new Segment(new string(UsageText.PieceGlyph(pieces[i].Piece, noColor), cells[i]), new Style(UsageText.PieceColor(pieces[i].Piece))));
                used += cells[i];
            }
        }

        if (over && used < width)
        {
            // The warning cell, past the window: BarCells left it.
            segments.Add(new Segment(noColor ? "!" : "█", new Style(Theme.Warn)));
            used++;
        }

        if (used < width)
        {
            segments.Add(new Segment(new string('░', width - used), new Style(Theme.Dimmer)));
        }

        segments.Add(Segment.LineBreak);
        return segments;
    }
}
