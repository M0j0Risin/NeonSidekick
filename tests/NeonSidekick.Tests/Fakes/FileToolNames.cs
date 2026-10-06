using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests.Fakes;

/// <summary>The file tools in the order <c>ChatScreen.FileTools</c> offers them. Pinned once, used by every tool-list assertion.</summary>
public static class FileToolNames
{
    public static readonly string[] All =
    {
        GetWorkingDirectoryTool.ToolName,
        SearchFilesTool.ToolName,
        FileInfoTool.ToolName,
        ReadFileTool.ToolName,
        ViewImageTool.ToolName,
        WriteFileTool.ToolName,
        PatchFileTool.ToolName,
        CreateDirectoryTool.ToolName,
        MoveTool.ToolName,
        CopyTool.ToolName,
        DeleteTool.ToolName,
        ZipTool.ToolName,
        UnzipTool.ToolName,
        OpenTool.ToolName,
        ImageInfoTool.ToolName,
        ImageEditTool.ToolName,
        ConvertToPdfTool.ToolName,
    };

    /// <summary>
    /// The sixteen <c>ChatScreen.FileTools</c> builds without a PDF converter (the tests that build the list alone; 2026-10-03; the image
    /// tools since 2026-10-04). Where there are no picture codecs (2026-10-06, the macOS build: <see cref="Files.ImageCodecs"/>) the list
    /// leaves out <c>view_image</c>, <c>image_info</c> and <c>image_edit</c>, so this does too; on Windows it is all sixteen as before.
    /// </summary>
    public static readonly string[] WithoutPdf = Files.ImageCodecs.Available
        ? All[..^1]
        : [.. All[..^1].Where(name => name is not (ViewImageTool.ToolName or ImageInfoTool.ToolName or ImageEditTool.ToolName))];
}
