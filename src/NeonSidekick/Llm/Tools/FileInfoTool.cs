using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>file_info(path | paths)</c>: size, modified time and line/word count of a file; counts and total size of a folder. Since 2026-10-09
/// (the user's call) a full path in the profile's images folder is read there too; since 2026-11-15 (feature idea #45) several paths in one
/// call, one line each, as <c>image_info</c> answers.
/// </summary>
public sealed class FileInfoTool : FileTool
{
    public const string ToolName = "file_info";

    /// <summary>The most paths one call inspects; the rest are named for the next call.</summary>
    public const int MaxPaths = 20;

    private static readonly JsonElement Schema = SchemaFor("A file or folder, relative to the working directory.");

    // The images folder's (2026-10-09): path may be a full path there too.
    private static readonly JsonElement ImagesSchema = SchemaFor("A file or folder, relative to the working directory, or a full path in the user's images folder.");

    // No "required" (2026-11-15): a blank path is the root, and paths may come instead.
    private static JsonElement SchemaFor(string path) => ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { \"path\": { \"type\": \"string\", \"description\": \"" + path + " Leave it out for the working directory itself.\" }, " +
        "\"paths\": { \"type\": \"array\", \"items\": { \"type\": \"string\" }, \"description\": \"Several files or folders to inspect in one call (at most 20), each as path takes it.\" } } }");

    /// <param name="images">The profile's images folder, read by its full paths (2026-10-09); null = the working directory alone.</param>
    public FileInfoTool(WorkingDirectory files, WorkingDirectory? images = null) : base(files, images)
    {
    }

    public override string Name => ToolName;

    public override string Description => DescriptionFor(Images?.Root);

    /// <summary>The description, with <see cref="FileText.ImagesFolderInfoSentence"/> after it while <paramref name="imagesRoot"/> is given. Pinned.</summary>
    public static string DescriptionFor(string? imagesRoot) => imagesRoot is null ? DescriptionText : DescriptionText + " " + FileText.ImagesFolderInfoSentence(imagesRoot);

    /// <summary>The description over the working directory alone. Pinned.</summary>
    public const string DescriptionText =
        "Size, modified time, line and word count, line ending (CRLF or LF) and BOM of a file under the working directory (the user's cwd / current directory); " +
        "file and folder counts and total size of a folder. Also the way to check whether something exists. paths inspects several at once.";

    public override JsonElement JsonSchema => Images is null ? Schema : ImagesSchema;

    public string Describe(string path) => FileText.Info(FilesFor(path).Info(path));

    /// <summary>
    /// One line per path, in order, each routed as the single form routes it (<see cref="FileTool.FilesFor"/>), an <c>Error:</c> line inline;
    /// past <see cref="MaxPaths"/> the rest are named by <see cref="FileText.MorePaths"/>. Cancellation is checked before each path.
    /// </summary>
    public string DescribeMany(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var lines = new List<string>(Math.Min(paths.Count, MaxPaths) + 1);
        foreach (string path in paths.Take(MaxPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(Describe(path));
        }

        if (paths.Count > MaxPaths)
        {
            lines.Add(FileText.MorePaths(paths.Skip(MaxPaths).ToList()));
        }

        return string.Join("\n", lines);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryReadPaths(arguments, out var paths, out string error))
        {
            return error;
        }

        // Off the turn's thread (2026-10-22, review R11: a 2 MB line count or a 10,000-entry walk held it), Esc returning the turn. Neither
        // path nor paths is the root, as a blank path always was.
        return await Task.Run(() => paths.Count switch
        {
            0 => Describe(""),
            1 => Describe(paths[0]),
            _ => DescribeMany(paths, cancellationToken),
        }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
