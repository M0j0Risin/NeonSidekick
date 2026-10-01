using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>patch_file(path, old_text, new_text, replace_all?)</c>: the one editing tool since 2026-09-19
/// (<c>edit_file</c> and the line-addressed <c>edit_lines</c> before it). One occurrence of
/// <c>old_text</c> replaced — every one with <c>replace_all</c> — found by <see cref="FuzzyMatch"/>'s
/// chain: as written first, then with spacing, indentation, escapes and typographic characters
/// tolerated, last by similarity; the result names the strategy when it was not exact. The match
/// runs over LF-normalised text, so what a read shows matches a CRLF file; the file keeps its line
/// ending and its BOM. The result names the edited lines and shows them numbered.
/// </summary>
public sealed class PatchFileTool : FileTool
{
    public const string ToolName = "patch_file";
    public const string OldTextArgument = "old_text";
    public const string NewTextArgument = "new_text";
    public const string ReplaceAllArgument = "replace_all";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file to change, relative to the working directory." },
            "old_text": { "type": "string", "description": "The text to replace, copied from the file as read_file shows it (without the line numbers an edit result shows). It must occur exactly once unless replace_all is true; include surrounding lines to make it unique. Small differences in spacing, indentation, quotes and dashes are tolerated." },
            "new_text": { "type": "string", "description": "The text to put in its place. Empty removes old_text." },
            "replace_all": { "type": "boolean", "description": "true replaces every occurrence of old_text (a rename across the file), matched as written or with spacing and indentation tolerated, never by similarity. Default false: exactly one." }
          },
          "required": ["path", "old_text", "new_text"]
        }
        """);

    public PatchFileTool(WorkingDirectory files) : base(files)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Changes part of a text file under the working directory (the user's cwd / current directory): replaces one occurrence of old_text with new_text, " +
        "or every occurrence with replace_all. old_text is matched as written first, then with spacing, indentation, \\n escapes and typographic quotes or dashes tolerated, " +
        "and last by the similarity of its lines — so text copied from a read lands even when its indentation was guessed; the result says how it matched. " +
        "old_text must appear exactly once unless replace_all (which never matches by similarity) — include enough surrounding text to make it unique; an empty new_text removes it. " +
        "The rest of the file is untouched; the result shows the edited lines with their numbers and the file's new line and word counts. An edit already in the file answers as done. To rewrite or append a whole file use write_file.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string path, string oldText, string newText, bool replaceAll = false) =>
        FileText.Edited(Files.EditText(path, oldText, newText, replaceAll));

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (ReadFlag(arguments, ReplaceAllArgument, out string raw) is not { } replaceAll)
        {
            return new ValueTask<object?>(FileText.BadBoolean(ReplaceAllArgument, raw));
        }

        if (!RequirePath(arguments, PathArgument, out string path, out string error))
        {
            return new ValueTask<object?>(error);
        }

        return new ValueTask<object?>(Describe(
            path,
            ToolArguments.ReadString(arguments, OldTextArgument),
            ToolArguments.ReadString(arguments, NewTextArgument),
            replaceAll));
    }
}
