using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>delete(path)</c>: removes a file or folder for good, a folder with everything in it (in place with
/// <c>File safe edits</c> off since 2026-09-20, the user's call, and always since 2026-10-01, when that setting,
/// its <c>.trash</c> and <c>restore</c> went, the user's call again).
/// </summary>
public sealed class DeleteTool : FileTool
{
    public const string ToolName = "delete";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file or folder to delete, relative to the working directory." }
          },
          "required": ["path"]
        }
        """);

    public DeleteTool(WorkingDirectory files) : base(files)
    {
    }

    public override string Name => ToolName;

    /// <summary>
    /// What the model reads. It names neither the setting that once kept a copy nor that nothing brings a file back
    /// (2026-09-21, the user's ask: told so, the model answered about a restore it could not reach). It ends on
    /// <see cref="GitNote"/> (2026-09-23, the user's call). Pinned.
    /// </summary>
    public override string Description => DescriptionText;

    /// <summary>The description (<see cref="Description"/>). Pinned.</summary>
    public const string DescriptionText =
        "Deletes a file or folder under the working directory (the user's cwd / current directory) for good; a folder goes with everything in it." + GitNote;

    /// <summary>The sentence the description ends on: <c>.git</c> is refused, whatever the path (2026-09-23). Pinned.</summary>
    public const string GitNote = " A .git folder, anything in it, or a folder holding one is never deleted.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string path) => FileText.Deleted(Files.Delete(path));

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Describe(ReadPath(arguments)));
    }
}
