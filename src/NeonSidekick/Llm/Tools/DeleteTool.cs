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

    private readonly Func<bool>? _recycles;

    /// <param name="recycles">Whether a delete goes to the Recycle bin now (<c>File deletes to Recycle bin</c>, later on 2026-10-15): the description follows it.</param>
    public DeleteTool(WorkingDirectory files, Func<bool>? recycles = null) : base(files)
    {
        _recycles = recycles;
    }

    public override string Name => ToolName;

    /// <summary>
    /// What the model reads. It names neither the setting that once kept a copy nor that nothing brings a file back
    /// (2026-09-21, the user's ask: told so, the model answered about a restore it could not reach). It ends on
    /// <see cref="GitNote"/> (2026-09-23, the user's call). Pinned.
    /// </summary>
    public override string Description => _recycles?.Invoke() == true ? RecycleDescriptionText : DescriptionText;

    /// <summary>The description with <c>File deletes to Recycle bin</c> off (<see cref="Description"/>). Pinned.</summary>
    public const string DescriptionText =
        "Deletes a file or folder under the working directory (the user's cwd / current directory) for good; a folder goes with everything in it." + GitNote;

    /// <summary>
    /// The description with <c>File deletes to Recycle bin</c> on (later on 2026-10-15): the bin, where the user can restore it; a drive with none
    /// (a network share, a removable drive) deletes for good, which the result says. Pinned.
    /// </summary>
    public static string RecycleDescriptionText =>
        "Moves a file or folder under the working directory (the user's cwd / current directory) to the " + FileText.RecycleBinName + ", where the user can restore it; a folder goes with everything in it. " +
        "On a drive with no " + FileText.RecycleBinName + " it is deleted for good, and the result says so; an item the " + FileText.RecycleBinName + " can't take is not deleted at all." + GitNote;

    /// <summary>The sentence the description ends on: <c>.git</c> is refused, whatever the path (2026-09-23). Pinned.</summary>
    public const string GitNote = " A .git folder, anything in it, or a folder holding one is never deleted.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string path) => FileText.Deleted(Files.Delete(path));

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string path = ReadPath(arguments);
        // Off the turn's thread, the folder's walk under Esc (2026-10-22, review P9).
        return await Task.Run(() => FileText.Deleted(Files.Delete(path, cancellationToken)), cancellationToken).ConfigureAwait(false);
    }
}
