using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>read_file(path, start_line?, max_lines?)</c>: a text file, or a window of it. The result
/// starts with a header line naming the window, so the transcript's one-line note reads
/// <c>notes.txt (lines 1–40 of 48): …</c>, then the text as it is — never numbered (the
/// <c>numbered</c> argument and <c>Always return line numbers</c> went on 2026-09-19; an edit
/// result's region is the one place a line shows behind its number).
/// </summary>
public sealed class ReadFileTool : FileTool
{
    public const string ToolName = "read_file";

    public const string StartLineArgument = "start_line";
    public const string MaxLinesArgument = "max_lines";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file to read, relative to the working directory." },
            "start_line": { "type": "integer", "description": "The first line to return; 1 is the top. A negative number counts from the end: -20 is the last 20 lines. Leave it out to start at the top." },
            "max_lines": { "type": "integer", "description": "How many lines to return at most. Leave it out for the whole file. To read on after a partial read, call again with the start_line its header names." }
          },
          "required": ["path"]
        }
        """);

    public ReadFileTool(WorkingDirectory files) : base(files)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Reads a text file under the working directory (the user's cwd / current directory), or part of it: start_line 1 is the top, " +
        "a negative start_line counts from the end (-20 = the last 20 lines), max_lines limits how much comes back; " +
        "a partial read's header ends with the start_line that continues it (next: start_line 201) — call again with that to read on, never from the top again. " +
        "The text comes back as it is in the file, so it can be copied into patch_file's old_text; search_files finds where a phrase is.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string path, int? startLine, int? maxLines)
    {
        var read = Files.ReadText(path, startLine, maxLines);
        if (read.Outcome == FileOutcome.Ok)
        {
            Files.MarkSeen(path);   // write_file may replace it without a mode while it stays as read (2026-10-10)
        }

        return FileText.Read(read);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, StartLineArgument, out var start, out var raw))
        {
            return new ValueTask<object?>(ClockText.BadInteger(StartLineArgument, raw));
        }

        if (!ToolArguments.TryReadInt32(arguments, MaxLinesArgument, out var max, out raw))
        {
            return new ValueTask<object?>(ClockText.BadInteger(MaxLinesArgument, raw));
        }

        if (!RequirePath(arguments, PathArgument, out string path, out string error))
        {
            return new ValueTask<object?>(error);
        }

        return new ValueTask<object?>(Describe(path, start, max));
    }
}
