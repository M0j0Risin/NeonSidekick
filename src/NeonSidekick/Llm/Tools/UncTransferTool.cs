using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>unc_move</c> and <c>unc_copy</c> (2026-09-30): <c>move</c> and <c>copy</c> within one read-write share, under <c>UNC writes</c>.
/// <c>from</c> picks the share (by <c>share</c>, or a full path); <c>to</c> stays in it — a full path on another share is refused
/// (<see cref="UncText.CrossShare"/>). What <c>overwrite</c> replaces is gone for good; a folder copied over a folder merges.
/// </summary>
public abstract class UncTransferTool : UncTool
{
    public const string FromArgument = MoveTool.FromArgument;
    public const string ToArgument = MoveTool.ToArgument;

    private readonly bool _move;

    protected UncTransferTool(UncAccess unc, Func<AppSettingsData> effective, bool move) : base(unc, effective)
    {
        _move = move;
    }

    /// <summary>The schema both share: the verb in its descriptions.</summary>
    protected static JsonElement SchemaFor(string verb) => ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when from is a full \\\\server\\share path or a folder under one." },
            "from": { "type": "string", "description": "The file or folder to VERB, relative to the share (or a full path under it)." },
            "to": { "type": "string", "description": "Where it goes on the same share, relative to it (or a full path under it)." },
            "overwrite": { "type": "boolean", "description": "true to replace what is already at to, for good (a folder copied over a folder merges). Default false." }
          },
          "required": ["from", "to"]
        }
        """.Replace("VERB", verb, StringComparison.Ordinal));

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

        string to = ToolArguments.ReadString(arguments, ToArgument).Trim();
        if (to.Length == 0)
        {
            return FileText.PathRequired(ToArgument);
        }

        return await WriteAsync(ReadShare(arguments), from, _move ? "moved" : "copied", (files, relative, share) =>
        {
            string spelled = to.Replace('/', '\\');
            if (UncCatalog.IsAbsolute(spelled) && !WorkingDirectory.IsInside(share.Config.Root, UncShareConfig.NormalizeRoot(spelled)))
            {
                return (UncText.CrossShare(to), false);
            }

            var result = _move ? files.Move(relative, spelled, overwrite ?? false) : files.Copy(relative, spelled, overwrite ?? false);
            return (UncText.Scoped(_move ? FileText.Moved(result) : FileText.Copied(result), share), result.Outcome == FileOutcome.Ok);
        }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>unc_move(share?, from, to, overwrite?)</c> (2026-09-30): a file or folder moved or renamed within one read-write share.</summary>
public sealed class UncMoveTool : UncTransferTool
{
    public const string ToolName = "unc_move";

    private static readonly JsonElement Schema = SchemaFor("move or rename");

    public UncMoveTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective, move: true)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Moves or renames a file or folder within one of the user's read-write network shares (or outside folders). " +
        "With overwrite, what was at to is gone for good: nothing is kept on a share. Only when the user asks for the change.";

    public override JsonElement JsonSchema => Schema;
}

/// <summary><c>unc_copy(share?, from, to, overwrite?)</c> (2026-09-30): a file or folder copied within one read-write share.</summary>
public sealed class UncCopyTool : UncTransferTool
{
    public const string ToolName = "unc_copy";

    private static readonly JsonElement Schema = SchemaFor("copy");

    public UncCopyTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective, move: false)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Copies a file or folder within one of the user's read-write network shares (or outside folders); a folder copied over a folder merges into it. " +
        "With overwrite, a file that was at to is gone for good. To bring a file into the working directory use unc_fetch.";

    public override JsonElement JsonSchema => Schema;
}
