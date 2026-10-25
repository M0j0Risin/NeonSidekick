using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Help;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>neon_help(query?, kind?)</c> (2026-10-02, the user's ask): the app's own manual, for "how do I…" questions about NeonSidekick
/// itself — its slash commands (every form), its settings (what each does, its default, and where it lives as pane › tab › row),
/// its panes and its keys (<see cref="NeonHelp"/>); since 2026-10-25 (the neon_help plan) its tools, its bars and toolbar icons, its
/// flags, variables and files too. Documentation, with one live line (2026-10-25, the user's call, reversing 2026-10-02's rule): a tool's
/// answer ends "Right now:" with whether the next turn offers it and why not, from <see cref="HelpLive"/>; never a setting's value. The
/// live-less instance (<see cref="AsRedacted"/>) is the advisors' copy.
/// A standing tool beside the clock, offered everywhere (headless too) and in plan mode while <c>Help tools</c> is on (2026-10-18, the
/// Help tab on <c>/tools</c> › Core; no tab of its own until then) — <c>/tools</c>' Offered tab lists it under <see cref="HelpText.GroupTitle"/>
/// and switches it off by name too.
/// </summary>
public sealed class NeonHelpTool : AIFunction, IRedactedTwin
{
    public const string ToolName = "neon_help";

    public const string QueryArgument = "query";
    public const string KindArgument = "kind";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "What to look up: a slash command (/camera), a setting's name (Camera watch interval), a pane or tab (/tools camera), a key (Ctrl+H), a tool (web_fetch), a toolbar icon, a --flag, a file (sql.json), or plain words (how to change the voice). Leave it out for the overview of panes and commands."
            },
            "kind": {
              "type": "string",
              "enum": ["any", "command", "setting", "pane", "keys", "tool", "interface", "startup", "files"],
              "description": "Narrow the search to one sort of answer; any (the default) tries them all."
            }
          }
        }
        """);

    private readonly Func<HelpLive>? _live;

    /// <param name="live">The running app's view of its tools, for a tool answer's "Right now:" line; null leaves that line out.</param>
    public NeonHelpTool(Func<HelpLive>? live = null)
    {
        _live = live;
    }

    public override string Name => ToolName;

    public override string Description =>
        "NeonSidekick's own manual (the terminal chat app you are running in): its slash commands with every form, its settings " +
        "(what each does, its default, and where it lives as pane › tab › row), its panes, keys, tools, bars, flags and files. Call it before " +
        "answering any question about how to do something in NeonSidekick, instead of guessing; a tool's answer says whether it is offered right now.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(NeonHelp.Answer(ToolArguments.ReadString(arguments, QueryArgument), ToolArguments.ReadString(arguments, KindArgument), _live));
    }

    /// <summary>The live-less copy (2026-10-25): what a tool is offered here is the main chat's alone; itself when it already has none.</summary>
    public AIFunction AsRedacted() => _live is null ? this : new NeonHelpTool();
}
