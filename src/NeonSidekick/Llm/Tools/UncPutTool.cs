using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>unc_put(from, share?, to?, overwrite?)</c> (2026-09-30): a file or folder of the working directory copied onto a read-write
/// share, under <c>UNC writes</c> (<see cref="WorkingDirectory.CopyBetween"/>) — <c>unc_fetch</c>'s way back. Offered only with the
/// File tools on too. A replaced file is replaced in place, its permissions kept, nothing kept of what it held.
/// </summary>
public sealed class UncPutTool : UncTool
{
    public const string ToolName = "unc_put";
    public const string FromArgument = "from";
    public const string ToArgument = "to";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "from": { "type": "string", "description": "The file or folder in the working directory to copy, relative to it." },
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when to is a full \\\\server\\share path or a folder under one." },
            "to": { "type": "string", "description": "Where it goes on the share, relative to the share (or a full path under it). Leave it out for the same name at the share's root." },
            "overwrite": { "type": "boolean", "description": "true to replace what is already at to, for good (a folder is merged). Default false." }
          },
          "required": ["from"]
        }
        """);

    private readonly WorkingDirectory _sandbox;

    public UncPutTool(UncAccess unc, WorkingDirectory sandbox, Func<AppSettingsData> effective) : base(unc, effective)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Copies a file or folder from the working directory onto one of the user's read-write network shares (or outside folders). " +
        "With overwrite, a file that was there is gone for good. One call carries at most 5,000 files and 500 MB. Only when the user asks for the change.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadBoolean(arguments, FileTool.OverwriteArgument, out var overwrite, out var raw))
        {
            return FileText.BadBoolean(FileTool.OverwriteArgument, raw);
        }

        string from = ToolArguments.ReadString(arguments, FromArgument).Trim();
        if (from.Length == 0)
        {
            return FileText.PathRequired(FromArgument);
        }

        // The working directory's end judged first, in its own words; the share's end is then the copy's.
        var side = _sandbox.Resolve(from, forWrite: false, out string source);
        if (side != FileOutcome.Ok)
        {
            return FileText.Error(side, from, "put");
        }

        string to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        string target = to.Length > 0 ? to : Path.GetFileName(source);
        return await WriteAsync(ReadShare(arguments), target, "put", (files, relative, share) =>
        {
            var result = WorkingDirectory.CopyBetween(_sandbox, from, files, relative, overwrite ?? false);
            return (UncText.Put(result, share), result.Outcome == FileOutcome.Ok);
        }, cancellationToken).ConfigureAwait(false);
    }
}
