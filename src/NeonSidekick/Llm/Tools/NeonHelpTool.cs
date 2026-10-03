using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Help;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>neon_help(query?, kind?)</c> (2026-10-02, the user's ask): the app's own manual, for "how do I…" questions about NeonSidekick
/// itself — its slash commands (every form), its settings (what each does, its default, and where it lives as pane › tab › row),
/// its panes and its keys (<see cref="NeonHelp"/>). Documentation only: it never reads this profile's values (the user's call).
/// A standing tool beside the clock, offered everywhere (headless too) and in plan mode; no tab of its own — <c>/tools</c>'
/// Offered tab lists it under <see cref="HelpText.GroupTitle"/> and switches it off by name.
/// </summary>
public sealed class NeonHelpTool : AIFunction
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
              "description": "What to look up: a slash command (/camera), a setting's name (Camera watch interval), a pane or tab (/tools camera), a key (Ctrl+H), or plain words (how to change the voice). Leave it out for the overview of panes and commands."
            },
            "kind": {
              "type": "string",
              "enum": ["any", "command", "setting", "pane", "keys"],
              "description": "Narrow the search to one sort of answer; any (the default) tries them all."
            }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description =>
        "NeonSidekick's own manual (the terminal chat app you are running in): its slash commands with every form, its settings " +
        "(what each does, its default, and where it lives as pane › tab › row), its panes and its keys. Call it before answering " +
        "any question about how to do something in NeonSidekick, instead of guessing. It reads documentation only, never the user's current settings.";

    public override JsonElement JsonSchema => Schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(NeonHelp.Answer(ToolArguments.ReadString(arguments, QueryArgument), ToolArguments.ReadString(arguments, KindArgument)));
    }
}
