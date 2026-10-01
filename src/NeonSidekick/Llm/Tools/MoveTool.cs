using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>move(from, to, overwrite?)</c>: renames or moves a file or a folder. Named <c>move</c>, not
/// <c>move_file</c>, because it takes either; there is no separate rename tool — the description
/// leads with the word, and the result says <c>renamed</c> when only the last segment changed.
/// </summary>
public sealed class MoveTool : FileTool
{
    public const string ToolName = "move";

    public const string FromArgument = "from";
    public const string ToArgument = "to";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "from": { "type": "string", "description": "The file or folder to rename or move, relative to the working directory." },
            "to": { "type": "string", "description": "Its new path, relative to the working directory: a new name in the same place is a rename; another folder is a move." },
            "overwrite": { "type": "boolean", "description": "true to replace whatever is already at the new path. Without it the move is refused." }
          },
          "required": ["from", "to"]
        }
        """);

    public MoveTool(WorkingDirectory files) : base(files)
    {
    }

    public override string Name => ToolName;

    public override string Description => DescriptionText;

    /// <summary>The description; what a <c>.trash</c> kept went with File safe edits (2026-10-01, the user's call). Pinned.</summary>
    public const string DescriptionText =
        "Renames or moves a file or a folder under the working directory (the user's cwd / current directory): to is the new path " +
        "(a new name in the same place is a rename; a folder moves with everything in it). Refuses to replace something already at the new path unless overwrite is true; " +
        "a file is replaced in place and a folder in the way is refused.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string from, string to, bool overwrite) => FileText.Moved(Files.Move(from, to, overwrite));

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool? overwrite = ReadOverwrite(arguments, out var raw);
        if (overwrite is null)
        {
            return new ValueTask<object?>(FileText.BadBoolean(OverwriteArgument, raw));
        }

        if (!RequirePath(arguments, FromArgument, out string from, out string error) || !RequirePath(arguments, ToArgument, out string to, out error))
        {
            return new ValueTask<object?>(error);
        }

        return new ValueTask<object?>(Describe(from, to, overwrite.Value));
    }
}
