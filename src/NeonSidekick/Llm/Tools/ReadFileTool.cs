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

    /// <summary>
    /// The least of the shared limit a file in a batch is cut to: below it a file that doesn't fit goes on the not-read list rather than
    /// coming back as a sliver. A file that fits whole is still read (2026-11-15, the tool-arguments review's #14).
    /// </summary>
    public const int MinBatchChars = 2_000;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file to read, relative to the working directory." },
            "paths": { "type": "array", "items": { "type": "string" }, "description": "Several files to read in one call (at most {{MaxPathsText}}), each relative to the working directory; they share one size limit. Leave path out when you give this." },
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
        "paths reads several files at once (up to " + MaxPathsText + "), sharing one size limit; start_line and max_lines go with one file.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string path, int? startLine, int? maxLines) => FileText.Read(ReadOne(path, startLine, maxLines, WorkingDirectory.MaxReadChars));

    /// <summary>What a file in a batch leaves marked, applied once the batch is answered (2026-11-15, the tool-arguments review's #51).</summary>
    internal sealed record BatchMark(string Path, SeenStamp? Seen, bool Whole);

    /// <summary>A batch's answer and the marks it leaves.</summary>
    internal sealed record BatchRead(string Text, IReadOnlyList<BatchMark> Marks);

    /// <summary><see cref="ReadMany"/> with its marks applied at once: for a caller that awaits nothing.</summary>
    public string DescribeMany(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var batch = ReadMany(paths, cancellationToken);
        Apply(batch.Marks);
        return batch.Text;
    }

    /// <summary>
    /// The result for several files (2026-11-15; reworked by the tool-arguments review's #14, #44, #45, #83, #95, #107, #122, #147, #151):
    /// <see cref="FileText.ReadMany"/> (or <see cref="FileText.NoneRead"/> when none could be read and none are left), then each file's block
    /// under <see cref="FileText.BatchSeparator"/>, in order. The shared <see cref="WorkingDirectory.MaxReadChars"/> counts each whole block.
    /// A file's size on disk is checked against what is left before it is decoded (a character takes at least a byte, so this errs safe):
    /// a file that fits is read whole; the first that doesn't is read cut to what is left, if that is at least <see cref="MinBatchChars"/>,
    /// and every later one that doesn't fit goes on the not-read list undecoded. A refusal shows in place. Past <see cref="FileTool.MaxPaths"/>
    /// the rest are not tried; one <see cref="FileText.NotRead"/> line names everything left. Nothing is marked here: the caller applies
    /// <see cref="BatchRead.Marks"/> once the answer is in hand (#51: a batch Esc cut short runs on and must mark nothing).
    /// </summary>
    internal BatchRead ReadMany(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var blocks = new List<string>();
        var marks = new List<BatchMark>();
        var names = new List<string>();
        var pastLimit = new List<string>();
        int budget = WorkingDirectory.MaxReadChars;
        int refused = 0;
        bool cut = false;
        foreach (string path in paths.Take(MaxPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool fits = SizeOf(path) is not { } bytes || bytes <= budget;
            if (!fits && (cut || budget < MinBatchChars))
            {
                pastLimit.Add(path);
                continue;
            }

            cut |= !fits;
            var read = Files.ReadText(path, null, null, Math.Max(budget, 1));
            if (read.Outcome == FileOutcome.Ok)
            {
                names.Add(read.Relative);
                marks.Add(new BatchMark(path, read.Seen, read.Whole));
            }
            else
            {
                refused++;
            }

            string block = FileText.BatchSeparator(read.Relative) + "\n" + FileText.Read(read, inBatch: true);
            budget -= block.Length;
            blocks.Add(block);
        }

        var pastCap = paths.Skip(MaxPaths).ToList();
        bool left = pastLimit.Count + pastCap.Count > 0;
        string first = names.Count == 0 && !left ? FileText.NoneRead(paths.Count) : FileText.ReadMany(names.Count, paths.Count, refused, names);
        string tail = left ? "\n\n" + FileText.NotRead(pastLimit, pastCap, WorkingDirectory.MaxReadChars, MaxPaths) : "";
        return new BatchRead(first + "\n" + string.Join("\n\n", blocks) + tail, marks);
    }

    /// <summary>A file's size on disk before it is read (#14), or null for anything the read itself will refuse without decoding.</summary>
    private long? SizeOf(string path)
    {
        try
        {
            return Files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full) ? new FileInfo(full).Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A batch's marks, as <see cref="ReadOne"/> leaves a single read's.</summary>
    private void Apply(IReadOnlyList<BatchMark> marks)
    {
        foreach (var mark in marks)
        {
            Files.MarkViewed(mark.Path, mark.Seen);
            if (mark.Whole)
            {
                Files.MarkSeen(mark.Path, mark.Seen);
            }
        }
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

        if (!TryReadPaths(arguments, out var named, out string error))
        {
            return error;
        }

        if (named.Count == 0)
        {
            // A paths sent that names nothing is its own answer (#143), not "path is required".
            return ToolArguments.Has(arguments, PathsArgument) ? FileText.PathsNamedNothing : FileText.PathsRequired;
        }

        // Each file once, by where it resolves (#15, #16): paths ["a", "./a"] reads as path "a" does.
        var paths = Unique(named);
        if (paths.Count == 1)
        {
            // One file: synchronous, exactly as before.
            return Describe(paths[0], start, max);
        }

        // Only a value the one-file read takes as a window (#151): start_line 0 or 1 and max_lines 0 or less are its defaults.
        if (start is not (null or 0 or 1) || max is > 0)
        {
            return FileText.WindowNeedsOneFile;
        }

        // Off the turn's thread (2026-11-15: up to 20 files of up to 8 MB each are decoded to count their lines), Esc between files. The
        // marks after the await (#51): a batch Esc left behind runs on, and marks nothing.
        var batch = await Task.Run(() => ReadMany(paths, cancellationToken), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        Apply(batch.Marks);
        return batch.Text;
    }
}
