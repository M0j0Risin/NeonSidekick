using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>write_file(path, content, mode?)</c>: creates a text file (<c>create</c>, the default — an
/// existing file refused, since a small model rewriting the user's notes on a misread is the risk
/// and the refusal names the mode so a deliberate retry is one more call), replaces one
/// (<c>overwrite</c>) or adds to its end (<c>append</c>, on a new line, creating a missing file —
/// <c>append_file</c>'s job until 2026-09-19).
/// </summary>
public sealed class WriteFileTool : FileTool, IRefusesPrunedStubs
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> StubArguments { get; } = [ContentArgument];

    public const string ToolName = "write_file";

    public const string ContentArgument = "content";
    public const string ModeArgument = "mode";

    /// <summary>The three words <c>mode</c> takes, as the schema's <c>enum</c> lists them.</summary>
    public const string CreateMode = "create";
    public const string OverwriteMode = "overwrite";
    public const string AppendMode = "append";

    /// <summary>The choices as <see cref="FileText.BadChoice"/> names them. Pinned.</summary>
    public const string ModeChoices = "create, overwrite or append";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file to write, relative to the working directory. Missing folders on the way are created." },
            "content": { "type": "string", "description": "The text to write: the whole file under create and overwrite, the text to add under append." },
            "mode": { "type": "string", "enum": ["create", "overwrite", "append"], "description": "create (the default) writes a new file and leaves an existing one alone; overwrite replaces an existing file; append adds content at the end of the file on a new line, creating it if missing." }
          },
          "required": ["path", "content"]
        }
        """);

    public WriteFileTool(WorkingDirectory files) : base(files)
    {
    }

    public override string Name => ToolName;

    public override string Description => DescriptionText;

    /// <summary>The description; its <c>.trash</c> sentence went with File safe edits (2026-10-01, the user's call). Pinned.</summary>
    public const string DescriptionText =
        "Writes a text file under the working directory (the user's cwd / current directory), creating any missing folders. " +
        "mode create (the default) leaves a file that already exists alone — unless you read it whole, wrote it or patched it in this conversation and it is unchanged since, which it replaces; mode overwrite replaces any; mode append adds the content at its end on a new line, creating the file if missing — for journals, logs and lists. " +
        "The result reports the file's size, line count and word count, so nothing else is needed to check them. To change part of a file use patch_file." + FileText.GuardedPathsSentence;

    public override JsonElement JsonSchema => Schema;

    /// <summary>The word's mode: blank = <see cref="WriteMode.Create"/>; false for any other word.</summary>
    public static bool TryParseMode(string? raw, out WriteMode mode)
    {
        string word = (raw ?? "").Trim();
        if (word.Length == 0 || word.Equals(CreateMode, StringComparison.OrdinalIgnoreCase))
        {
            mode = WriteMode.Create;
            return true;
        }

        if (word.Equals(OverwriteMode, StringComparison.OrdinalIgnoreCase))
        {
            mode = WriteMode.Overwrite;
            return true;
        }

        if (word.Equals(AppendMode, StringComparison.OrdinalIgnoreCase))
        {
            mode = WriteMode.Append;
            return true;
        }

        mode = WriteMode.Create;
        return false;
    }

    public string Describe(string path, string content, WriteMode mode = WriteMode.Create) => Write(Files, path, content, mode).Text;

    /// <summary>
    /// The write and its sentence, with what it changed for the transcript (2026-10-03, <see cref="FileDiff"/>); shared with
    /// <c>unc_write</c>, whose sandbox is a share's (<paramref name="onShare"/>: an Office file is made locally, then put there).
    /// </summary>
    public static (string Text, bool Done, FileDiff? Diff) Write(WorkingDirectory files, string path, string content, WriteMode mode, bool onShare = false)
    {
        ArgumentNullException.ThrowIfNull(files);
        // An Office file is a zip of XML parts: text written over it is a broken document (2026-10-29, the Office writing plan). Word and
        // Excel files point to their converter; a PowerPoint, legacy, binary-workbook or OpenDocument file to what no tool writes.
        if (OfficeRefusal(path, onShare) is { } office)
        {
            return (office, false, null);
        }

        if (mode == WriteMode.Append)
        {
            var appended = files.AppendText(path, content);
            return (FileText.Appended(appended), appended.Outcome == FileOutcome.Ok, appended.Diff);
        }

        var wrote = files.WriteText(path, content, overwrite: mode == WriteMode.Overwrite);
        return (FileText.Wrote(wrote), wrote.Outcome == FileOutcome.Ok, wrote.Diff);
    }

    /// <summary>
    /// The refusal for writing text to an Office path, or null for any other path. Only <c>.docx</c> and <c>.xlsx</c> name their converter,
    /// the only files they make (2026-10-29, review P1-9: a <c>.docm</c> or <c>.xltx</c> was sent to a tool that refuses it); on a share
    /// (<paramref name="onShare"/>), where no converter writes, the converter is named with <c>unc_put</c>.
    /// </summary>
    public static string? OfficeRefusal(string path, bool onShare = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        string ending = OfficeFile.Extension(path);   // trailing dots and spaces dropped, as Windows drops them (2026-10-30, review F14)
        string? converter = ending.Equals(Office.DocxConverter.Extension, StringComparison.OrdinalIgnoreCase) ? ConvertToDocxTool.ToolName
            : ending.Equals(Office.XlsxTarget.Extension, StringComparison.OrdinalIgnoreCase) ? ConvertToXlsxTool.ToolName
            : null;
        if (converter is not null)
        {
            return onShare ? FileText.OfficeOnShare(path, converter) : FileText.OfficeNeedsConverter(path, converter);
        }

        return OfficeFile.IsAnyOfficePath(path) ? FileText.OfficeNotWritten(path) : null;
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string raw = ToolArguments.ReadString(arguments, ModeArgument);
        if (!TryParseMode(raw, out var mode))
        {
            return new ValueTask<object?>(FileText.BadChoice(ModeArgument, raw, ModeChoices));
        }

        if (!RequirePath(arguments, PathArgument, out string path, out string error))
        {
            return new ValueTask<object?>(error);
        }

        if (ShoppingListRefusal(path) is { } refused)
        {
            return new ValueTask<object?>(refused);
        }

        // A missing content is not "" (2026-10-17, the toki-pona session): read as empty, a write with old_text/new_text instead replaced a
        // 27 KB file with nothing — and with the seen-file overwrite below, without any mode.
        if (!ToolArguments.Has(arguments, ContentArgument))
        {
            return new ValueTask<object?>(FileText.ContentRequired(PatchFileTool.ToolName, SentEdit(arguments)));
        }

        if (StubSent(arguments, ContentArgument) is { } stub)
        {
            return new ValueTask<object?>(stub);
        }

        // No mode on a file the model read whole, wrote or patched this session and that is unchanged since (2026-10-10, the user's call: a run
        // retried twice, each a whole request, for files it had just read or made): it is replaced, as mode overwrite would. A file it
        // never saw, or one changed since, is still refused; an explicit create still never replaces.
        if (raw.Trim().Length == 0 && Files.WasSeen(path))
        {
            mode = WriteMode.Overwrite;
        }

        var (text, done, diff) = Write(Files, path, ToolArguments.ReadString(arguments, ContentArgument), mode);
        // An append never marks it (2026-10-11, the review): adding a line says nothing of what the rest of the file holds.
        if (done && mode != WriteMode.Append)
        {
            Files.MarkSeen(path);
        }

        return new ValueTask<object?>(ToolDiffResult.Of(text, diff));
    }

    /// <summary>
    /// The refusal of a write whose <paramref name="argument"/> is a pruned stub copied back from the history (2026-10-17,
    /// <see cref="Llm.ConversationCompactor.IsPrunedStub"/>); null for real text. The write tools share it.
    /// </summary>
    public static string? StubSent(AIFunctionArguments arguments, string argument) =>
        Llm.ConversationCompactor.IsPrunedStub(ToolArguments.ReadString(arguments, argument)) ? FileText.PrunedStubSent(argument) : null;

    /// <summary>Whether a call sent an edit's arguments (<c>old_text</c> or <c>new_text</c>) in place of a write's <c>content</c> (2026-10-17).</summary>
    public static bool SentEdit(AIFunctionArguments arguments) =>
        ToolArguments.Has(arguments, PatchFileTool.OldTextArgument) || ToolArguments.Has(arguments, PatchFileTool.NewTextArgument);
}

/// <summary>What <c>write_file</c> does with a file that is there: refuses (<see cref="Create"/>), replaces it, or adds to its end.</summary>
public enum WriteMode
{
    Create,
    Overwrite,
    Append,
}
