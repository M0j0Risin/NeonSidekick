using NeonSidekick.Mcp;

namespace NeonSidekick.App;

// ── MCP calls that ask (2026-11-19, feature idea #28) ─────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The MCP tools "Allow for this session" was picked for: <see cref="McpAllowKey"/>'s, ordinal (the catalog keeps <c>GitHub</c> and
    /// <c>github</c> as two servers, and a case-blind key would carry one's allow to the other); forgotten with the session
    /// (<see cref="ForgetAllows"/>).
    /// </summary>
    private readonly HashSet<string> _mcpAllowed = new(StringComparer.Ordinal);

    /// <summary>A session allow's key: the server's name as the catalog holds it and the tool's as the server names it. Pure.</summary>
    public static string McpAllowKey(string server, string serverTool) => server + "\0" + serverTool;

    /// <summary>
    /// The session's approver for an MCP call that asks, on the call's task: a tool allowed for the session runs at once; else the allow
    /// pane, at the PC and on the page (<see cref="AllowBothAsync"/>), titled with the server, its caption the tool and what the server says
    /// it changes, its view the arguments (cut, and the caption says so). Without a pane, or a pane that never asked, nobody could be asked;
    /// Esc and the turn's stop are a Deny.
    /// </summary>
    private async Task<McpVerdict> AllowMcpAsync(McpAsk ask, CancellationToken turnToken)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (!_pane.Enabled)
        {
            return McpVerdict.Nobody;
        }

        string key = McpAllowKey(ask.Server, ask.ServerTool);
        lock (_mcpAllowed)
        {
            if (_mcpAllowed.Contains(key))
            {
                return new McpVerdict.Allowed(McpText.AllowedEarlier);
            }
        }

        var (whole, cut) = McpText.ArgumentLines(ask.Arguments);
        string title = McpText.AllowTitle(ask.Server);
        string caption = McpText.AllowCaption(ask.Server, ask.Name, ask.Note, cut);
        var allow = await AllowBothAsync(title, caption, whole, token => _cameraMenu.AllowAsync(caption, token, title, title, whole), turnToken).ConfigureAwait(false);
        switch (allow)
        {
            case null:
                return McpVerdict.Nobody;
            case CameraAllow.Session:
                lock (_mcpAllowed)
                {
                    _mcpAllowed.Add(key);
                }

                return new McpVerdict.Allowed(McpText.AllowedForSession);
            case CameraAllow.Once:
                return new McpVerdict.Allowed(McpText.AllowedOnce);
            default:
                return McpVerdict.No;
        }
    }

    /// <summary>
    /// What an unattended Ralph pass withholds (2026-11-19): <see cref="Plans.PlanTools.WithoutAskingOrConfirming"/>'s names, widened by every
    /// offered MCP tool that would ask under its server's <c>ask</c>, allowed for the session or not. One helper for the turn and
    /// <c>TurnDisabled</c>, so <c>/usage</c> and <c>neon_help</c> match the pass.
    /// </summary>
    private IReadOnlySet<string> RalphWithheld(IReadOnlySet<string> set)
    {
        var withheld = Plans.PlanTools.WithoutAskingOrConfirming(set);
        var asking = _mcp.AskingNames();
        if (asking.Count == 0)
        {
            return withheld;
        }

        var widened = new HashSet<string>(withheld, StringComparer.Ordinal);
        widened.UnionWith(asking);
        return widened;
    }
}
