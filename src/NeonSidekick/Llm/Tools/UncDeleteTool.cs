using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>unc_delete(share?, path)</c> (2026-09-30): a file or folder deleted on a read-write share, under <c>UNC writes</c> — for good,
/// a folder with everything in it (a share has no trash, the user's call). The share's root and anything holding a <c>.git</c> are
/// refused, as in the working directory. Off in a fresh profile (<c>ToolsDisabled</c>), as <c>gitlib_delete</c> is.
/// </summary>
public sealed class UncDeleteTool : UncTool
{
    public const string ToolName = "unc_delete";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when path is a full \\\\server\\share path or a folder under one." },
            "path": { "type": "string", "description": "The file or folder to delete for good, relative to the share (or a full path under it)." }
          },
          "required": ["path"]
        }
        """);

    public UncDeleteTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Deletes a file or folder, with everything in it, on one of the user's read-write network shares (or outside folders). It is gone for good: there is no trash on a share and no way back. " +
        "Only when the user asks for exactly this deletion.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string path = ToolArguments.ReadString(arguments, PathArgument).Trim();
        if (path.Length == 0)
        {
            return FileText.PathRequired(PathArgument);
        }

        return await WriteAsync(ReadShare(arguments), path, "deleted", (files, relative) =>
        {
            var deleted = files.Delete(relative, toTrash: false);
            return (FileText.Trashed(deleted), deleted.Outcome == FileOutcome.Ok);
        }, cancellationToken).ConfigureAwait(false);
    }
}
