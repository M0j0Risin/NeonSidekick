using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>file_info(path)</c>: size, modified time and line/word count of a file; counts and total size of a folder. Since 2026-10-09
/// (the user's call) a full path in the profile's images folder is read there too.
/// </summary>
public sealed class FileInfoTool : FileTool
{
    public const string ToolName = "file_info";

    private static readonly JsonElement Schema = SchemaFor("A file or folder, relative to the working directory.");

    // The images folder's (2026-10-09): path may be a full path there too.
    private static readonly JsonElement ImagesSchema = SchemaFor("A file or folder, relative to the working directory, or a full path in the user's images folder.");

    private static JsonElement SchemaFor(string path) => ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { \"path\": { \"type\": \"string\", \"description\": \"" + path + "\" } }, \"required\": [\"path\"] }");

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
        "file and folder counts and total size of a folder. Also the way to check whether something exists.";

    public override JsonElement JsonSchema => Images is null ? Schema : ImagesSchema;

    public string Describe(string path) => FileText.Info(FilesFor(path).Info(path));

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ValueTask<object?>(Describe(ReadPath(arguments)));
    }
}
