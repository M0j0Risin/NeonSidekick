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
public sealed class WriteFileTool : FileTool
{
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
        "mode create (the default) leaves a file that already exists alone; mode overwrite replaces it; mode append adds the content at its end on a new line, creating the file if missing — for journals, logs and lists. " +
        "The result reports the file's size, line count and word count, so nothing else is needed to check them. To change part of a file use patch_file.";

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

    public string Describe(string path, string content, WriteMode mode = WriteMode.Create)
    {
        return mode == WriteMode.Append
            ? FileText.Appended(Files.AppendText(path, content))
            : FileText.Wrote(Files.WriteText(path, content, overwrite: mode == WriteMode.Overwrite));
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

        return new ValueTask<object?>(Describe(path, ToolArguments.ReadString(arguments, ContentArgument), mode));
    }
}

/// <summary>What <c>write_file</c> does with a file that is there: refuses (<see cref="Create"/>), replaces it, or adds to its end.</summary>
public enum WriteMode
{
    Create,
    Overwrite,
    Append,
}
