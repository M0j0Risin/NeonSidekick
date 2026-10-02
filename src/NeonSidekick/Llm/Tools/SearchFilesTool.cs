using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>search_files(text?, path?, files?, regex?, context?, output?, order?, limit?, depth?)</c>: with
/// <c>text</c>, a case-insensitive search inside the text files under the working directory, a
/// subfolder or one file, <c>file:line: text</c> per hit with <c>context</c> lines around each in
/// grep's shape (2026-09-17), or one row per file with its count under <c>output</c> files
/// (2026-09-19); <c>files</c> is a name pattern or a path glob (<c>src/**/*.cs</c>), braces for alternatives (<c>*.{png,jpg}</c>, 2026-09-24). Without <c>text</c>
/// it lists (2026-09-19, when <c>list_directory</c> and <c>recent_files</c> folded in): the folder's
/// own entries, folders first with sizes, nested to <c>depth</c> levels; the files whose names match
/// <c>files</c>, every level; or under <c>order</c> modified the most recently changed files, newest
/// first. <c>limit</c> caps every shape, up to the <c>File search max results</c> setting (2026-10-01, the user's ask; a
/// constant 200 until then), which the schema quotes (<see cref="LimitOf"/>). The one file tool that honours the turn's token:
/// ESC ends a long search.
/// </summary>
public sealed class SearchFilesTool : FileTool
{
    public const string ToolName = "search_files";

    public const string TextArgument = "text";
    public const string FilesArgument = "files";
    public const string RegexArgument = "regex";
    public const string ContextArgument = "context";
    public const string OutputArgument = "output";
    public const string OrderArgument = "order";
    public const string LimitArgument = "limit";
    public const string DepthArgument = "depth";

    public const string ContentOutput = "content";
    public const string FilesOutput = "files";
    public const string NameOrder = "name";
    public const string ModifiedOrder = "modified";

    /// <summary>The choices as <see cref="FileText.BadChoice"/> names them. Pinned.</summary>
    public const string OutputChoices = "content or files";
    public const string OrderChoices = "name or modified";

    private readonly Func<AppSettingsData> _effective;

    // The schema for the cap last asked for: a request reads JsonSchema once per tool, and the cap
    // changes only when the user edits the row, so one parse per edit (view_image's shape).
    private int _schemaLimit;
    private JsonElement _schema;

    /// <summary>The schema under the given cap: <c>limit</c>'s description quotes it (<see cref="LimitDescription"/>). Pinned.</summary>
    public static JsonElement SchemaFor(int max) => ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "text": { "type": "string", "description": "The word or phrase to look for inside files. Case does not matter. Leave it out to list files instead of searching inside them." },
            "path": { "type": "string", "description": "A subfolder to search or list, or one file to search in, relative to the working directory. Leave it out for the working directory itself." },
            "files": { "type": "string", "description": "Only files matching this pattern: a name pattern such as *.md or *.cs, or a path pattern such as src/**/*.cs (** spans folders); braces give alternatives, *.{png,jpg}. Without text, lists the matching files at every level." },
            "regex": { "type": "boolean", "description": "true to treat text as a regular expression instead of plain words. Default false." },
            "context": { "type": "integer", "description": "Lines to show before and after each hit, 0 to 5 (default 0). Context lines read file-11- text around the hit's file:12: text." },
            "output": { "type": "string", "enum": ["content", "files"], "description": "With text: content (the default) shows every matching line; files shows one row per file with how many lines matched." },
            "order": { "type": "string", "enum": ["name", "modified"], "description": "Without text: name (the default) lists by name; modified lists the most recently changed files, newest first with their times, at every level. Ignored with text." },
            "limit": { "type": "integer", "description": "{{LimitDescription(max)}}" },
            "depth": { "type": "integer", "description": "How many folder levels to walk, 1 being the folder's own entries. Without text and files it defaults to 1 and 2 to 4 nest the subfolders' entries under them; with files, order modified or text it defaults to every level." }
          }
        }
        """);

    /// <summary>
    /// <c>limit</c>'s description under the cap <paramref name="max"/>, shared with <c>unc_search</c> so the two never drift
    /// (2026-10-01): each default as a call without <c>limit</c> gets it, never past the cap. At 200 it is the text the
    /// schemas carried when 200 was a constant. Pinned.
    /// </summary>
    public static string LimitDescription(int max) =>
        "The most rows to return: hits or files with text (default " + N(Math.Min(WorkingDirectory.DefaultSearchLimit, max)) + ", up to " + N(max) + "); " +
        "entries, matching files or recent files without it (default " + N(Math.Min(WorkingDirectory.MaxEntries, max)) + ", " + N(Math.Min(WorkingDirectory.DefaultFindLimit, max)) + " or " + N(Math.Min(WorkingDirectory.DefaultRecent, max)) + "; up to " + N(max) + ").";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The cap <paramref name="effective"/> holds (<c>File search max results</c>), clamped into <see cref="AppSettingsData.MinFileSearchMaxResults"/>–<see cref="AppSettingsData.MaxFileSearchMaxResults"/>. Pure.</summary>
    public static int LimitOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.FileSearchMaxResults, AppSettingsData.MinFileSearchMaxResults, AppSettingsData.MaxFileSearchMaxResults);
    }

    /// <param name="files">The sandbox searched and listed.</param>
    /// <param name="effective">The settings the cap (<c>File search max results</c>) is read from at every use.</param>
    public SearchFilesTool(WorkingDirectory files, Func<AppSettingsData> effective) : base(files)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    /// <summary>The most rows one call returns, as the settings stand now (<see cref="LimitOf"/>).</summary>
    public int Limit => LimitOf(_effective());

    public override string Name => ToolName;

    public override string Description =>
        "Searches inside the text files under the working directory (the user's cwd / current directory), under a subfolder, or in one file named as path, for a word or phrase, case-insensitive; " +
        "each hit is file:line: text, with context lines around it when asked (file-11- text), or with output files one row per file with its count. files narrows by name (*.cs) or path (src/**/*.cs); set regex true for a regular expression. " +
        "Without text it lists instead: the folder's files and folders with sizes (depth 2 to 4 shows a tree), the files whose names match files at every level, or with order modified the most recently changed files newest first with their times. " +
        "limit caps every list.";

    public override JsonElement JsonSchema
    {
        get
        {
            int limit = Limit;
            if (_schema.ValueKind == JsonValueKind.Undefined || limit != _schemaLimit)
            {
                _schema = SchemaFor(limit);
                _schemaLimit = limit;
            }

            return _schema;
        }
    }

    /// <summary>The dispatch: a content search with <paramref name="text"/>, else one of the three listing shapes.</summary>
    public string Describe(string text, string path, string? files, bool regex, CancellationToken cancellationToken, int context = 0, string output = ContentOutput, string order = NameOrder, int? limit = null, int? depth = null) =>
        Run(Files, new SearchRequest(text, path, files ?? "", regex, context, output, order, limit, depth), Limit, cancellationToken).Text;

    /// <summary>One <c>search_files</c> call's arguments, read and checked (<see cref="TryRead"/>).</summary>
    public sealed record SearchRequest(string Text, string Path, string Files, bool Regex, int Context, string Output, string Order, int? Limit, int? Depth);

    /// <summary>
    /// <see cref="Describe"/>'s dispatch over any sandbox (2026-09-30: <c>unc_search</c> runs it over a share): a content search
    /// with text, else one of the three listing shapes; <c>Budgeted</c> when a share's walk stopped at its budget
    /// (<see cref="SearchResult.Budgeted"/>, <see cref="RecentResult.Budgeted"/>). <paramref name="maxLimit"/> is the cap of the
    /// call (<see cref="LimitOf"/>, 2026-10-01): a bigger <c>limit</c> is clamped to it, as it was to 200, and every default with it.
    /// </summary>
    public static (string Text, bool Budgeted) Run(WorkingDirectory sandbox, SearchRequest request, int maxLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(request);
        maxLimit = Math.Clamp(maxLimit, 1, WorkingDirectory.MaxResultLimit);
        string needle = (request.Text ?? "").Trim();
        string path = request.Path;
        string files = request.Files;
        int? limit = request.Limit is { } asked ? Math.Clamp(asked, 1, maxLimit) : null;
        int? depth = request.Depth;
        int walkDepth = depth is { } d ? Math.Max(1, d) : int.MaxValue;
        if (needle.Length > 0)
        {
            bool countFiles = string.Equals((request.Output ?? "").Trim(), FilesOutput, StringComparison.OrdinalIgnoreCase);
            var result = sandbox.Search(needle, path, files, request.Regex, cancellationToken, request.Context, limit ?? Math.Min(WorkingDirectory.DefaultSearchLimit, maxLimit), walkDepth, countFiles ? SearchOutput.Files : SearchOutput.Content);
            return (countFiles ? FileText.SearchFiles(result, needle) : FileText.SearchHits(result, needle), result.Budgeted);
        }

        if (string.Equals((request.Order ?? "").Trim(), ModifiedOrder, StringComparison.OrdinalIgnoreCase))
        {
            var recent = sandbox.Recent(path, limit ?? Math.Min(WorkingDirectory.DefaultRecent, maxLimit), walkDepth);
            return (FileText.Recent(recent), recent.Budgeted);
        }

        if (!string.IsNullOrWhiteSpace(files))
        {
            return (FileText.Found(sandbox.Find(files, path, limit ?? Math.Min(WorkingDirectory.DefaultFindLimit, maxLimit), walkDepth), files.Trim()), false);
        }

        int levels = Math.Clamp(depth ?? 1, 1, WorkingDirectory.MaxTreeDepth);
        return levels == 1
            ? (FileText.Listing(sandbox.List(path, limit ?? Math.Min(WorkingDirectory.MaxEntries, maxLimit))), false)
            : (FileText.Listing(sandbox.FileTree(path, limit ?? Math.Min(WorkingDirectory.MaxEntries, maxLimit), levels), levels), false);
    }

    /// <summary>
    /// Reads and checks a <c>search_files</c>-shaped call (shared with <c>unc_search</c>, 2026-09-30): null with the request, else
    /// the sentence for the first bad argument (a non-boolean regex, a non-integer count, an output or order out of range).
    /// </summary>
    public static string? TryRead(AIFunctionArguments arguments, out SearchRequest? request)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        if (!ToolArguments.TryReadBoolean(arguments, RegexArgument, out var regex, out var raw))
        {
            return FileText.BadBoolean(RegexArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, ContextArgument, out var context, out raw))
        {
            return ClockText.BadInteger(ContextArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, LimitArgument, out var limit, out raw))
        {
            return ClockText.BadInteger(LimitArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, DepthArgument, out var depth, out raw))
        {
            return ClockText.BadInteger(DepthArgument, raw);
        }

        string output = ToolArguments.ReadString(arguments, OutputArgument).Trim();
        if (output.Length > 0 && !output.Equals(ContentOutput, StringComparison.OrdinalIgnoreCase) && !output.Equals(FilesOutput, StringComparison.OrdinalIgnoreCase))
        {
            return FileText.BadChoice(OutputArgument, output, OutputChoices);
        }

        string order = ToolArguments.ReadString(arguments, OrderArgument).Trim();
        if (order.Length > 0 && !order.Equals(NameOrder, StringComparison.OrdinalIgnoreCase) && !order.Equals(ModifiedOrder, StringComparison.OrdinalIgnoreCase))
        {
            return FileText.BadChoice(OrderArgument, order, OrderChoices);
        }

        request = new SearchRequest(
            ToolArguments.ReadString(arguments, TextArgument),
            ToolArguments.ReadString(arguments, PathArgument),
            ToolArguments.ReadString(arguments, FilesArgument),
            regex ?? false,
            context ?? 0,
            output,
            order,
            limit,
            depth);
        return null;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (TryRead(arguments, out var request) is { } refused)
        {
            return refused;
        }

        // Off the caller's thread: the walk is synchronous and parallel, and the turn loop is the UI's.
        int limit = Limit;
        return await Task.Run(() => Run(Files, request!, limit, cancellationToken).Text, cancellationToken).ConfigureAwait(false);
    }
}
