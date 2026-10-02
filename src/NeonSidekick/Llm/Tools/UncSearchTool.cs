using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>unc_search(share?, text?, path?, files?, regex?, context?, output?, order?, limit?, depth?)</c> (2026-09-30):
/// <c>search_files</c> on a share — the same arguments, the same shapes (<see cref="SearchFilesTool.Run"/>) — as the share's
/// account, with a share's budgets: four readers, 256 MB read, 100,000 entries looked at (<see cref="Files.WorkingDirectoryOptions.Share"/>),
/// a note when one stopped it. ESC ends it. <c>limit</c> goes up to the <c>File search max results</c> setting, as
/// <c>search_files</c>' does (2026-10-01, <see cref="SearchFilesTool.LimitOf"/>); the share's budgets stay fixed (the user's call).
/// </summary>
public sealed class UncSearchTool : UncTool
{
    public const string ToolName = "unc_search";

    // The schema for the cap last asked for (search_files' shape).
    private int _schemaLimit;
    private JsonElement _schema;

    /// <summary>The schema under the given cap: <c>limit</c>'s description is <c>search_files</c>' (<see cref="SearchFilesTool.LimitDescription"/>). Pinned.</summary>
    public static JsonElement SchemaFor(int max) => ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "share": { "type": "string", "description": "The share's name (unc_shares lists them); leave it out for the default share, or when path is a full \\\\server\\share path or a folder under one." },
            "text": { "type": "string", "description": "The word or phrase to look for inside files. Case does not matter. Leave it out to list files instead of searching inside them." },
            "path": { "type": "string", "description": "A subfolder to search or list, or one file to search in, relative to the share (or a full path under it). Leave it out for the share's root." },
            "files": { "type": "string", "description": "Only files matching this pattern: a name pattern such as *.md or *.docx, or a path pattern such as specs/**/*.md (** spans folders); braces give alternatives, *.{png,jpg}. Without text, lists the matching files at every level." },
            "regex": { "type": "boolean", "description": "true to treat text as a regular expression instead of plain words. Default false." },
            "context": { "type": "integer", "description": "Lines to show before and after each hit, 0 to 5 (default 0)." },
            "output": { "type": "string", "enum": ["content", "files"], "description": "With text: content (the default) shows every matching line; files shows one row per file with how many lines matched." },
            "order": { "type": "string", "enum": ["name", "modified"], "description": "Without text: name (the default) lists by name; modified lists the most recently changed files, newest first, at every level." },
            "limit": { "type": "integer", "description": "{{SearchFilesTool.LimitDescription(max)}}" },
            "depth": { "type": "integer", "description": "How many folder levels to walk, 1 being the folder's own entries. Without text and files it defaults to 1 and 2 to 4 nest the subfolders' entries; otherwise every level." }
          }
        }
        """);

    public UncSearchTool(UncAccess unc, Func<AppSettingsData> effective) : base(unc, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Searches inside the text files on one of the user's network shares (or outside folders), under a subfolder, or in one file named as path, for a word or phrase, case-insensitive; " +
        "each hit is file:line: text, or with output files one row per file with its count. files narrows by name (*.md) or path (specs/**/*.md); set regex true for a regular expression. " +
        "Without text it lists instead: a folder's files and folders with sizes (depth 2 to 4 shows a tree), the files whose names match files at every level, or with order modified the most recently changed files. " +
        "Paths in the result are relative to the share. A big share is searched in part: narrow with path, files or depth.";

    public override JsonElement JsonSchema
    {
        get
        {
            int limit = SearchFilesTool.LimitOf(Effective);
            if (_schema.ValueKind == JsonValueKind.Undefined || limit != _schemaLimit)
            {
                _schema = SchemaFor(limit);
                _schemaLimit = limit;
            }

            return _schema;
        }
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (SearchFilesTool.TryRead(arguments, out var request) is { } refused)
        {
            return refused;
        }

        int limit = SearchFilesTool.LimitOf(Effective);
        return await ReadAsync(ReadShare(arguments), request!.Path, (files, relative) =>
        {
            var (text, budgeted) = SearchFilesTool.Run(files, request with { Path = relative }, limit, cancellationToken);
            return budgeted ? text + "\n" + UncText.BudgetNote : text;
        }, cancellationToken).ConfigureAwait(false);
    }
}
