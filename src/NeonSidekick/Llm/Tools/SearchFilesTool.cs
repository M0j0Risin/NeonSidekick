using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

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
/// first. <c>limit</c> caps every shape. The one file tool that honours the turn's token: ESC ends a long search.
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

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
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
            "limit": { "type": "integer", "description": "The most rows to return: hits or files with text (default 50, up to 200); entries, matching files or recent files without it (default 200, 100 or 10; up to 200)." },
            "depth": { "type": "integer", "description": "How many folder levels to walk, 1 being the folder's own entries. Without text and files it defaults to 1 and 2 to 4 nest the subfolders' entries under them; with files, order modified or text it defaults to every level." }
          }
        }
        """);

    public SearchFilesTool(WorkingDirectory files) : base(files)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Searches inside the text files under the working directory (the user's cwd / current directory), under a subfolder, or in one file named as path, for a word or phrase, case-insensitive; " +
        "each hit is file:line: text, with context lines around it when asked (file-11- text), or with output files one row per file with its count. files narrows by name (*.cs) or path (src/**/*.cs); set regex true for a regular expression. " +
        "Without text it lists instead: the folder's files and folders with sizes (depth 2 to 4 shows a tree), the files whose names match files at every level, or with order modified the most recently changed files newest first with their times. " +
        "limit caps every list.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The dispatch: a content search with <paramref name="text"/>, else one of the three listing shapes.</summary>
    public string Describe(string text, string path, string? files, bool regex, CancellationToken cancellationToken, int context = 0, string output = ContentOutput, string order = NameOrder, int? limit = null, int? depth = null)
    {
        string needle = (text ?? "").Trim();
        int walkDepth = depth is { } d ? Math.Max(1, d) : int.MaxValue;
        if (needle.Length > 0)
        {
            bool countFiles = string.Equals((output ?? "").Trim(), FilesOutput, StringComparison.OrdinalIgnoreCase);
            var result = Files.Search(needle, path, files, regex, cancellationToken, context, limit ?? WorkingDirectory.DefaultSearchLimit, walkDepth, countFiles ? SearchOutput.Files : SearchOutput.Content);
            return countFiles ? FileText.SearchFiles(result, needle) : FileText.SearchHits(result, needle);
        }

        if (string.Equals((order ?? "").Trim(), ModifiedOrder, StringComparison.OrdinalIgnoreCase))
        {
            return FileText.Recent(Files.Recent(path, limit ?? WorkingDirectory.DefaultRecent, walkDepth));
        }

        if (!string.IsNullOrWhiteSpace(files))
        {
            return FileText.Found(Files.Find(files, path, limit ?? WorkingDirectory.DefaultFindLimit, walkDepth), files.Trim());
        }

        int levels = Math.Clamp(depth ?? 1, 1, WorkingDirectory.MaxTreeDepth);
        return levels == 1
            ? FileText.Listing(Files.List(path, limit ?? WorkingDirectory.MaxEntries))
            : FileText.Listing(Files.FileTree(path, Math.Clamp(limit ?? WorkingDirectory.MaxEntries, 1, WorkingDirectory.MaxListLimit), levels), levels);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
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

        string text = ToolArguments.ReadString(arguments, TextArgument);
        string path = ReadPath(arguments);
        string files = ToolArguments.ReadString(arguments, FilesArgument);
        // Off the caller's thread: the walk is synchronous and parallel, and the turn loop is the UI's.
        return await Task.Run(() => Describe(text, path, files, regex ?? false, cancellationToken, context ?? 0, output, order, limit, depth), cancellationToken).ConfigureAwait(false);
    }
}
