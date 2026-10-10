using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests.Fakes;

/// <summary>The file tools in the order <c>ChatScreen.FileTools</c> offers them. Pinned once, used by every tool-list assertion.</summary>
public static class FileToolNames
{
    /// <summary>
    /// Every file tool, the PDF converter last: fourteen since 2026-10-10, when <c>view_image</c>, <c>image_info</c> and <c>image_edit</c>
    /// became a group of their own (<see cref="ImageToolNames"/>, the user's ask).
    /// </summary>
    public static readonly string[] All =
    [
        GetWorkingDirectoryTool.ToolName,
        SearchFilesTool.ToolName,
        FileInfoTool.ToolName,
        ReadFileTool.ToolName,
        WriteFileTool.ToolName,
        PatchFileTool.ToolName,
        CreateDirectoryTool.ToolName,
        MoveTool.ToolName,
        CopyTool.ToolName,
        DeleteTool.ToolName,
        ZipTool.ToolName,
        UnzipTool.ToolName,
        OpenTool.ToolName,
        ConvertToPdfTool.ToolName,
    ];

    /// <summary>The thirteen <c>ChatScreen.FileTools</c> builds without a PDF converter (the tests that build the list alone; 2026-10-03).</summary>
    public static readonly string[] WithoutPdf = All[..^1];

    /// <summary>The file tools and then the picture tools, as a turn offers both groups (2026-10-10): what the file tools' list was until then, in the new order.</summary>
    public static readonly string[] WithImages = [.. All, .. ImageToolNames.All];
}

/// <summary>
/// The picture tools in the order <c>ChatScreen.ImageTools</c> builds them (2026-10-10, their own group under <c>Image tools</c>). Where there
/// are no picture codecs (2026-10-06, the macOS build: <see cref="Files.ImageCodecs"/>) none is built, so none is here either.
/// </summary>
public static class ImageToolNames
{
    public static readonly string[] All = Files.ImageCodecs.Available ? [ViewImageTool.ToolName, ImageInfoTool.ToolName, ImageEditTool.ToolName] : [];
}
