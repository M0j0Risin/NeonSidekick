using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>read_file(path | paths, start_line?, max_lines?)</c>: a text file, or a window of it — or several files at once (2026-11-15,
/// feature idea #31), each under its own header, sharing one size limit. The result
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

    /// <summary>The most files one call reads; the rest are named for the next call.</summary>
    public const int MaxPaths = 20;

    /// <summary>
    /// The least of the shared limit a file in a batch is read with: below it the rest go on the not-read list rather than each getting a
    /// sliver (a read always gives a file's first line, cut to the cap).
    /// </summary>
    public const int MinBatchChars = 2_000;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file to read, relative to the working directory." },
            "paths": { "type": "array", "items": { "type": "string" }, "description": "Several files to read in one call (at most 20), each relative to the working directory; they share one size limit. Leave path out when you give this." },
            "start_line": { "type": "integer", "description": "The first line to return; 1 is the top. A negative number counts from the end: -20 is the last 20 lines. Leave it out to start at the top." },
            "max_lines": { "type": "integer", "description": "How many lines to return at most. Leave it out for the whole file. To read on after a partial read, call again with the start_line its header names." }
          }
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
        "The text comes back as it is in the file, so it can be copied into patch_file's old_text; search_files finds where a phrase is. " +
        "paths reads several files at once (up to 20), sharing one size limit; start_line and max_lines go with one file.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string path, int? startLine, int? maxLines) => FileText.Read(ReadOne(path, startLine, maxLines, WorkingDirectory.MaxReadChars));

    /// <summary>
    /// The result for several files (2026-11-15): <see cref="FileText.ReadMany"/> (or <see cref="FileText.NoneRead"/> when none could be read),
    /// then each file's block, read in order with what the shared <see cref="WorkingDirectory.MaxReadChars"/> has left; once less than
    /// <see cref="MinBatchChars"/> is left, the rest are named by <see cref="FileText.NotRead"/>, and past <see cref="MaxPaths"/> by
    /// <see cref="FileText.MorePaths"/>.
    /// </summary>
    public string DescribeMany(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var blocks = new List<string>();
        var notRead = new List<string>();
        int budget = WorkingDirectory.MaxReadChars;
        int refused = 0;
        foreach (string path in paths.Take(MaxPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget < MinBatchChars)
            {
                notRead.Add(path);
                continue;
            }

            var read = ReadOne(path, null, null, budget);
            if (read.Outcome != FileOutcome.Ok)
            {
                refused++;
            }

            budget -= read.Text.Length;
            blocks.Add(FileText.Read(read));
        }

        string first = refused == blocks.Count ? FileText.NoneRead(blocks.Count) : FileText.ReadMany(blocks.Count, refused);
        var tail = new List<string>();
        if (notRead.Count > 0)
        {
            tail.Add(FileText.NotRead(notRead, WorkingDirectory.MaxReadChars));
        }

        if (paths.Count > MaxPaths)
        {
            tail.Add(FileText.MorePaths(paths.Skip(MaxPaths).ToList()));
        }

        return first + "\n" + string.Join("\n\n", blocks) + (tail.Count == 0 ? "" : "\n\n" + string.Join("\n", tail));
    }

    /// <summary>One file read with <paramref name="maxChars"/> as its cap, marked viewed when text came back and seen only when whole.</summary>
    private ReadResult ReadOne(string path, int? startLine, int? maxLines, int maxChars)
    {
        var read = Files.ReadText(path, startLine, maxLines, maxChars);
        if (read.Outcome == FileOutcome.Ok)
        {
            // Any read that returned text, a window too (2026-10-18): what present_audit's read check asks of a finding's file.
            Files.MarkViewed(path, read.Seen);
        }

        if (read.Whole)
        {
            // write_file may replace it without a mode while it stays as read (2026-10-10); a window or a cut read never (2026-10-11). The
            // stamp is the one the read took before its bytes (2026-10-22, review J12).
            Files.MarkSeen(path, read.Seen);
        }

        return read;
    }

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

        if (!TryReadPaths(arguments, out var paths, out string error))
        {
            return error;
        }

        switch (paths.Count)
        {
            case 0:
                return FileText.PathsRequired;
            case 1:
                // One file: synchronous, exactly as before.
                return Describe(paths[0], start, max);
        }

        if (start is not null || max is not null)
        {
            return FileText.WindowNeedsOneFile;
        }

        // Off the turn's thread (2026-11-15: up to 20 files of up to 8 MB each are decoded to count their lines), Esc between files.
        return await Task.Run(() => DescribeMany(paths, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
