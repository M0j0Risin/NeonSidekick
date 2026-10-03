using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>unc_write(share?, path, content, mode?)</c> (2026-09-30): <c>write_file</c> on a read-write share, under <c>UNC writes</c> —
/// create (the default), overwrite or append. A replaced file is replaced in place (its permissions kept) and nothing is kept of
/// what it held: a share has no trash (the user's call).
/// </summary>
public sealed class UncWriteTool : UncTool
{
    public const string ToolName = "unc_write";
    public const string ContentArgument = WriteFileTool.ContentArgument;
    public const string ModeArgument = WriteFileTool.ModeArgument;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when path is a full \\\\server\\share path or a folder under one." },
            "path": { "type": "string", "description": "The file to write, relative to the share (or a full path under it); missing folders are created." },
            "content": { "type": "string", "description": "The whole text to write." },
            "mode": { "type": "string", "enum": ["create", "overwrite", "append"], "description": "create (the default) refuses a file that is already there; overwrite replaces it; append adds the content at its end on a new line." }
          },
          "required": ["path", "content"]
        }
        """);

    public UncWriteTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Writes a text file on one of the user's read-write network shares (or outside folders). mode create (the default) leaves a file that is already there alone; overwrite replaces it; append adds to its end. " +
        "A replaced file's old text is gone for good: nothing is kept on a share. Only when the user asks for the change.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string raw = ToolArguments.ReadString(arguments, ModeArgument);
        if (!WriteFileTool.TryParseMode(raw, out var mode))
        {
            return FileText.BadChoice(ModeArgument, raw, WriteFileTool.ModeChoices);
        }

        string path = ToolArguments.ReadString(arguments, PathArgument).Trim();
        if (path.Length == 0)
        {
            return FileText.PathRequired(PathArgument);
        }

        string content = ToolArguments.ReadString(arguments, ContentArgument);
        string action = mode == WriteMode.Append ? "appended to" : mode == WriteMode.Overwrite ? "wrote over" : "wrote";
        // The diff beside the sentence for the transcript (2026-10-03, ToolDiffResult), kept only when the write was done.
        FileDiff? diff = null;
        string text = await WriteAsync(ReadShare(arguments), path, action, (files, relative) =>
        {
            var (sentence, done, changed) = WriteFileTool.Write(files, relative, content, mode);
            diff = done ? changed : null;
            return (sentence, done);
        }, cancellationToken).ConfigureAwait(false);
        return ToolDiffResult.Of(text, diff);
    }
}
