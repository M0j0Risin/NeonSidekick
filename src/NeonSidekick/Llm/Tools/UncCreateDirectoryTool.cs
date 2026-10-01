using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>unc_create_directory(share?, path)</c> (2026-09-30): <c>create_directory</c> on a read-write share, under <c>UNC writes</c>.</summary>
public sealed class UncCreateDirectoryTool : UncTool
{
    public const string ToolName = "unc_create_directory";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when path is a full \\\\server\\share path or a folder under one." },
            "path": { "type": "string", "description": "The folder to create, relative to the share (or a full path under it); missing parents are created too." }
          },
          "required": ["path"]
        }
        """);

    public UncCreateDirectoryTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description => "Creates a folder, and any missing parents, on one of the user's read-write network shares (or outside folders).";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string path = ToolArguments.ReadString(arguments, PathArgument).Trim();
        if (path.Length == 0)
        {
            return FileText.PathRequired(PathArgument);
        }

        return await WriteAsync(ReadShare(arguments), path, "created", (files, relative) =>
        {
            var created = files.CreateDirectory(relative);
            return (FileText.Created(created), created.Outcome == FileOutcome.Ok);
        }, cancellationToken).ConfigureAwait(false);
    }
}
