using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>copy(from, to, overwrite?)</c>: copies a file or a folder (with everything in it) to a new path.</summary>
public sealed class CopyTool : FileTool
{
    public const string ToolName = "copy";

    public const string FromArgument = "from";
    public const string ToArgument = "to";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "from": { "type": "string", "description": "The file or folder to copy, relative to the working directory." },
            "to": { "type": "string", "description": "The path of the copy, relative to the working directory (the new name, not a parent folder)." },
            "overwrite": { "type": "boolean", "description": "true to replace files already at the new path. Without it the copy is refused." }
          },
          "required": ["from", "to"]
        }
        """);

    public CopyTool(WorkingDirectory files) : base(files)
    {
    }

    public override string Name => ToolName;

    public override string Description => DescriptionText;

    /// <summary>The description; what a <c>.trash</c> kept went with File safe edits (2026-10-01, the user's call). Pinned.</summary>
    public const string DescriptionText =
        "Copies a file or a folder (with everything in it) under the working directory (the user's cwd / current directory) to a new path. " +
        "Refuses to replace something already at the new path unless overwrite is true; " +
        "a file is replaced in place and a folder in the way is refused (a folder copied over a folder merges into it).";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string from, string to, bool overwrite) => FileText.Copied(Files.Copy(from, to, overwrite));

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
