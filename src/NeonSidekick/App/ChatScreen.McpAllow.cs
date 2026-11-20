using NeonSidekick.Mcp;

namespace NeonSidekick.App;

// ── MCP calls that ask (2026-11-19, feature idea #28) ─────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The MCP tools "Allow for this session" was picked for: <see cref="McpSession.McpAllowKey"/>'s, ordinal (the catalog keeps <c>GitHub</c>
    /// and <c>github</c> as two servers, and a case-blind key would carry one's allow to the other); forgotten with the session
    /// (<see cref="ForgetAllows"/>). Since 2026-11-20 (the mcp-ask review's #3, #51, #116) a key holds the server's config and the tool's
    /// definition as they were approved, so a reload to a new command, a changed <c>ask</c> or a redefined tool asks again.
    /// </summary>
    private readonly HashSet<string> _mcpAllowed = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether <i>Allow for this session</i> holds for the MCP tool the model calls <paramref name="name"/> (2026-11-20, the mcp-ask review's
    /// #6, #25): its key from the live connection and the tool as listed, read under the set's lock. <c>neon_help</c> and <c>/mcp</c>'s
    /// footer read it.
    /// </summary>
    private bool McpHeldForSession(string name)
    {
        if (_mcp.AllowKeyFor(name) is not { } key)
        {
            return false;
        }

        lock (_mcpAllowed)
        {
            return _mcpAllowed.Contains(key);
        }
    }

    /// <summary>
    /// The session's approver for an MCP call that asks, on the call's task: a tool allowed for the session runs at once; else the allow
    /// pane, at the PC and on the page (<see cref="AllowWithSessionAsync"/>), titled with the server, its caption the tool and what the
    /// server says it changes, its view the arguments (cut, and the caption says so). Without a pane, or a pane never shown, nobody could be
    /// asked; Esc, a pane shown that didn't answer (#48) and the turn's stop (#23: audited as a Deny, not lost to the cancel) are a Deny.
    /// An unattended Ralph pass is refused before any pane (#2): its turn leaves these tools out, and this is the backstop.
    /// </summary>
    private async Task<McpVerdict> AllowMcpAsync(McpAsk ask, CancellationToken turnToken)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (RalphAsksBarred())
        {
            return new McpVerdict.NotAsked(McpText.RalphTail);
        }

        var (whole, cut) = McpText.ArgumentLines(ask.Arguments);
        string title = McpText.AllowTitle(ask.Server);
        string caption = McpText.AllowCaption(ask.Server, ask.ServerTool, ask.Note, cut);
        AllowSession allow;
        try
        {
            allow = await AllowWithSessionAsync(_mcpAllowed, McpSession.McpAllowKey(ask.Server, ask.ServerTool, ask.Fingerprint, ask.Definition), title, caption, whole, turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turnToken.IsCancellationRequested)
        {
            return McpVerdict.No;
        }

        return allow switch
        {
            AllowSession.HeldEarlier => new McpVerdict.Allowed(McpText.AllowedEarlier),
            AllowSession.Session => new McpVerdict.Allowed(McpText.AllowedForSession),
            AllowSession.Once => new McpVerdict.Allowed(McpText.AllowedOnce),
            AllowSession.Denied => McpVerdict.No,
            _ => McpVerdict.Nobody,
        };
    }

    /// <summary>
    /// What a turn leaves out for want of someone to ask (2026-11-20, the mcp-ask review's #29, #55, #69: one place for the turn and
    /// <c>TurnDisabled</c>, so <c>/usage</c> and <c>neon_help</c> match it): an unattended Ralph pass, <see cref="Plans.PlanTools.WithoutAskingOrConfirming"/>
    /// with every MCP tool that would ask under its server's <c>ask</c>, allowed for the session or not; a gap or audit pass that may not
    /// wait, <see cref="Plans.PlanTools.WithoutAsking"/> (they offer no MCP tools); and over any of them, a screen with no pane leaves out
    /// those MCP tools too, as the database writes are left out there.
    /// </summary>
    private IReadOnlySet<string> WithheldForAsking(IReadOnlySet<string> set, Settings.AppSettingsData effective)
    {
        var turn = RalphAsksBarred() ? Plans.PlanTools.WithoutAskingOrConfirming(set, _mcp.AskingNames())
            : GapAsksBarred(effective) ? Plans.PlanTools.WithoutAsking(set)
            : set;
        if (_pane.Enabled)
        {
            return turn;
        }

        var asking = _mcp.AskingNames();
        if (asking.Count == 0)
        {
            return turn;
        }

        var widened = new HashSet<string>(turn, StringComparer.Ordinal);
        widened.UnionWith(asking);
        return widened;
    }
}
