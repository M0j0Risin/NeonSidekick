using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.App;

// ── /usage's Context tab (2026-11-01, feature idea #1) ───────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The characters the wire wraps around one tool's name, description and schema in a request's <c>tools</c> list:
    /// <c>{"type":"function","function":{"name":"","description":"","parameters":}}</c>. A tool's weight on the Context tab is its
    /// three parts plus these, over <see cref="ConversationCompactor.CharsPerToken"/>.
    /// </summary>
    internal const int ToolWrapperChars = 73;

    /// <summary>A tool's estimated tokens as the wire sends it: name, description and schema plus <see cref="ToolWrapperChars"/>.</summary>
    internal static long ToolTokens(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        long chars = tool.Name.Length + (tool.Description?.Length ?? 0) + tool.JsonSchema.GetRawText().Length + ToolWrapperChars;
        return chars / ConversationCompactor.CharsPerToken;
    }

    /// <summary>
    /// What the <c>/usage</c> Context tab shows (2026-11-01), read as the next turn would build it and with none of a turn's side effects:
    /// the system prompt's sections (<see cref="SystemPromptSummary.PromptSections"/>, what <c>/sys</c> shows), the offered tool groups
    /// (<see cref="ToolGroups"/>, <c>/sys</c>' Tools tab), and the history's breakdown with the thinking the next request sends back
    /// (none of an earlier turn's unless <c>LLM preserve thinking</c> is on, since the next request starts a turn). While a reply runs the
    /// history is the turn's own list, appended to without a lock, so it is not read: the tab works the history out from the server's count.
    /// </summary>
    private ContextFacts ContextFacts()
    {
        var effective = _effective();
        if (ClaudeCliEndpoint.IsClaudeCli(_session.Endpoint?.BaseUrl))
        {
            return new ContextFacts(_session.ContextLength, 0, [], [], null, null, 0, effective.LlmPreserveThinking, ClaudeCode: true);
        }

        var prompt = SystemPromptSummary.PromptSections(SystemPromptFacts())
            .Where(s => s.InPrompt)
            .Select(s => (s.Label, (long)(s.Body.Length / ConversationCompactor.CharsPerToken)))
            .ToList();
        var groups = ToolGroups()
            .Select(g => new ContextToolGroup(g.Label, g.Tools.Count, g.Tools.Sum(ToolTokens), g.Switch is not null))
            .ToList();
        string? noTools = groups.Count == 0 ? SystemPromptSummary.NoToolsLine(effective.LlmOfferTools) : null;
        ConversationCompactor.TokenEstimate? history = null;
        int messages = 0;
        if (!_turnRunning)
        {
            var list = _session.History.Messages;
            messages = list.Count;
            history = ConversationCompactor.Estimate(list, effective.LlmPreserveThinking ? 0 : list.Count);
        }

        return new ContextFacts(_session.ContextLength, _session.Usage.LastRequest.Total, prompt, groups, noTools, history, messages, effective.LlmPreserveThinking);
    }

    /// <summary>The tab <c>/usage</c>'s argument names: 0 for none (Statistics), <see cref="UsageText.ContextTabIndex"/> for <c>context</c>, null for anything else.</summary>
    internal static int? UsageTab(string args)
    {
        string word = args.Trim();
        return word.Length == 0 ? 0
            : string.Equals(word, UsageText.ContextWord, StringComparison.OrdinalIgnoreCase) ? UsageText.ContextTabIndex
            : null;
    }
}

// ── The reply footer (2026-11-01, feature idea #4) ───────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// A tool's own time, for its result line (2026-11-01, the reply footer): stamped at the call and read at the result, less the menu pane's
    /// asks between them (an approval, <c>ask_user</c>), which also go to the turn's <see cref="_turnToolAsks"/>. With the footer on, a tool
    /// that took <see cref="UsageText.SlowTool"/> or more hands the renderer its suffix; never the shell family (its note has its time),
    /// <c>ask_user</c> (the time is the user's) or the advisor (its own view). Each call clears a suffix no writer took.
    /// </summary>
    private void TimeTool(TurnEvent evt)
    {
        if (evt is TurnEvent.ToolCall)
        {
            _transcript.ToolTime(null);
            _toolCalledAt = _time.GetTimestamp();
            _toolAskedAt = _menuPane.AskedFor;
            return;
        }

        if (evt is not TurnEvent.ToolResult result || _toolCalledAt is not long called)
        {
            return;
        }

        _toolCalledAt = null;
        var asked = _menuPane.AskedFor - _toolAskedAt;
        _turnToolAsks += asked;
        var took = _time.GetElapsedTime(called) - asked;
        bool timed = _effective().ReplyFooter
            && !ShellToolNames.Contains(result.Name)
            && !string.Equals(result.Name, AskUserTool.ToolName, StringComparison.Ordinal)
            && !string.Equals(result.Name, NeonAdvisorTool.ToolName, StringComparison.Ordinal);
        _transcript.ToolTime(timed ? UsageText.ToolTime(took) : null);
    }

    /// <summary>
    /// The reply footer's line for a turn's end (2026-11-01), or null: only with <see cref="Settings.AppSettingsData.ReplyFooter"/> on, for a
    /// main-chat turn that stands (<see cref="TurnOutcome.Continue"/>, cancelled ones included, or <see cref="TurnOutcome.Interrupted"/>) and
    /// isn't an error alone. The time is the turn's wall time less the asks inside its tools.
    /// </summary>
    private string? ReplyFooter(bool mainChat, TurnOutcome outcome, bool errorOnly, long started, TurnTrace trace)
    {
        _transcript.ToolTime(null);
        if (!mainChat || errorOnly || outcome is not (TurnOutcome.Continue or TurnOutcome.Interrupted) || !_effective().ReplyFooter)
        {
            return null;
        }

        var usage = _session.Usage.LastReply;
        var elapsed = _time.GetElapsedTime(started) - _turnToolAsks;
        return UsageText.Footer(new FooterFacts(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed, usage.Output, usage.TokensPerSecond, trace.ToolCalls, _session.Endpoint?.ModelId));
    }
}
