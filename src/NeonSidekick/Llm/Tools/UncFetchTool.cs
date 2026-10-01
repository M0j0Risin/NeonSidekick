using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>unc_fetch(share?, path, to?, overwrite?)</c> (2026-09-30): a file or folder copied from a share into the working directory
/// (<see cref="WorkingDirectory.CopyBetween"/>), as the share's account — how a share's picture reaches <c>view_image</c>, a
/// spreadsheet <c>execute_code</c>, a folder <c>zip</c>. Offered only with the File tools on (it writes the working directory, so
/// plan mode keeps it out); what it replaces there is kept in <c>.trash</c> under <c>File safe edits</c>. Nothing on the share changes.
/// </summary>
public sealed class UncFetchTool : UncTool
{
    public const string ToolName = "unc_fetch";
    public const string ToArgument = "to";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when path is a full \\\\server\\share path or a folder under one." },
            "path": { "type": "string", "description": "The file or folder on the share to copy, relative to the share (or a full path under it)." },
            "to": { "type": "string", "description": "Where it goes in the working directory, relative to it. Leave it out for the same name at the working directory's root." },
            "overwrite": { "type": "boolean", "description": "true to replace what is already at to (a folder is merged). Default false." }
          },
          "required": ["path"]
        }
        """);

    private readonly WorkingDirectory _sandbox;

    public UncFetchTool(UncAccess unc, WorkingDirectory sandbox, Func<AppSettingsData> effective) : base(unc, effective)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Copies a file or folder from one of the user's network shares (or outside folders) into the working directory, so the file tools, view_image and execute_code can use it. " +
        "Nothing on the share changes. One call carries at most 5,000 files and 500 MB.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadBoolean(arguments, FileTool.OverwriteArgument, out var overwrite, out var raw))
        {
            return FileText.BadBoolean(FileTool.OverwriteArgument, raw);
        }

        string path = ToolArguments.ReadString(arguments, PathArgument).Trim();
        if (path.Length == 0)
        {
            return FileText.PathRequired(PathArgument);
        }

        string to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        bool keepCopy = Effective.FileSafeEdits;
        return await ReadAsync(ReadShare(arguments), path, (files, relative, share) =>
        {
            // The share's end judged first, in the share's words; what fails after is the working directory's end, or the copy.
            var side = files.Resolve(relative, forWrite: false, out _);
            return side != FileOutcome.Ok
                ? UncText.Scoped(FileText.Error(side, relative, "fetch"), share)
                : UncText.Fetched(WorkingDirectory.CopyBetween(files, relative, _sandbox, to, overwrite ?? false, keepCopy), share);
        }, cancellationToken).ConfigureAwait(false);
    }
}
