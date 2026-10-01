using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>unc_read(share?, path, start_line?, max_lines?)</c> (2026-09-30): <c>read_file</c> on a share — a text file whole or in part, as the share's account.</summary>
public sealed class UncReadTool : UncTool
{
    public const string ToolName = "unc_read";
    public const string StartLineArgument = ReadFileTool.StartLineArgument;
    public const string MaxLinesArgument = ReadFileTool.MaxLinesArgument;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when path is a full \\\\server\\share path or a folder under one." },
            "path": { "type": "string", "description": "The text file, relative to the share (or a full path under it)." },
            "start_line": { "type": "integer", "description": "The first line to return; 1 is the top. A negative number counts from the end: -20 is the last 20 lines. Leave it out to start at the top." },
            "max_lines": { "type": "integer", "description": "How many lines to return at most. Leave it out for the whole file. To read on after a partial read, call again with the start_line its header names." }
          },
          "required": ["path"]
        }
        """);

    public UncReadTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Reads a text file on one of the user's network shares (or outside folders), whole or a range of lines; a long file comes back in part with the start_line to read on from. " +
        "A picture or other binary file is not read here: unc_fetch copies it into the working directory, where view_image and the other file tools reach it.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, StartLineArgument, out var start, out var raw))
        {
            return ClockText.BadInteger(StartLineArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, MaxLinesArgument, out var max, out raw))
        {
            return ClockText.BadInteger(MaxLinesArgument, raw);
        }

        string path = ToolArguments.ReadString(arguments, PathArgument).Trim();
        if (path.Length == 0)
        {
            return FileText.PathRequired(PathArgument);
        }

        return await ReadAsync(ReadShare(arguments), path, (files, relative) => FileText.Read(files.ReadText(relative, start, max)), cancellationToken).ConfigureAwait(false);
    }
}
