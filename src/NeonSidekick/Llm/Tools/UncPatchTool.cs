using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>unc_patch(share?, path, old_text, new_text, replace_all?)</c> (2026-09-30): <c>patch_file</c> on a read-write share, under <c>UNC writes</c>; the file replaced in place, nothing kept.</summary>
public sealed class UncPatchTool : UncTool
{
    public const string ToolName = "unc_patch";
    public const string OldTextArgument = PatchFileTool.OldTextArgument;
    public const string NewTextArgument = PatchFileTool.NewTextArgument;
    public const string ReplaceAllArgument = PatchFileTool.ReplaceAllArgument;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when path is a full \\\\server\\share path or a folder under one." },
            "path": { "type": "string", "description": "The text file to change, relative to the share (or a full path under it)." },
            "old_text": { "type": "string", "description": "The exact text to replace, copied from unc_read; enough of it to match one place." },
            "new_text": { "type": "string", "description": "What goes in its place." },
            "replace_all": { "type": "boolean", "description": "true to replace every place old_text matches. Default false: it must match exactly one." }
          },
          "required": ["path", "old_text", "new_text"]
        }
        """);

    public UncPatchTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Changes part of a text file on one of the user's read-write network shares (or outside folders): old_text, copied from unc_read, is replaced by new_text; the result shows the changed lines. " +
        "The old text is gone for good: nothing is kept on a share. Only when the user asks for the change.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadBoolean(arguments, ReplaceAllArgument, out var replaceAll, out var raw))
        {
            return FileText.BadBoolean(ReplaceAllArgument, raw);
        }

        string path = ToolArguments.ReadString(arguments, PathArgument).Trim();
        if (path.Length == 0)
        {
            return FileText.PathRequired(PathArgument);
        }

        string oldText = ToolArguments.ReadString(arguments, OldTextArgument);
        string newText = ToolArguments.ReadString(arguments, NewTextArgument);
        // The diff beside the sentence for the transcript (2026-10-03, ToolDiffResult).
        FileDiff? diff = null;
        string text = await WriteAsync(ReadShare(arguments), path, "patched", (files, relative) =>
        {
            var edited = files.EditText(relative, oldText, newText, replaceAll ?? false);
            diff = edited.Diff;
            return (FileText.Edited(edited), edited.Outcome == FileOutcome.Ok);
        }, cancellationToken).ConfigureAwait(false);
        return ToolDiffResult.Of(text, diff);
    }
}
