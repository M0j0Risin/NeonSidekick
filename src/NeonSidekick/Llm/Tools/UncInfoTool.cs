using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>unc_info(share?, path?)</c> (2026-09-30): <c>file_info</c> on a share — size, dates, lines and words of a text file; a folder's counts — as the share's account.</summary>
public sealed class UncInfoTool : UncTool
{
    public const string ToolName = "unc_info";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when path is a full \\\\server\\share path or a folder under one." },
            "path": { "type": "string", "description": "The file or folder, relative to the share (or a full path under it). Leave it out for the share's root." }
          }
        }
        """);

    public UncInfoTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Tells about a file or folder on one of the user's network shares (or outside folders): a file's size, created and modified times, and for text its lines, words and line endings; " +
        "a folder's files, folders and total size.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await ReadAsync(ReadShare(arguments), ToolArguments.ReadString(arguments, PathArgument), (files, relative) => FileText.Info(files.Info(relative)), cancellationToken).ConfigureAwait(false);
    }
}
