using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests.Fakes;

/// <summary>The file tools in the order <c>ChatScreen.FileTools</c> offers them. Pinned once, used by every tool-list assertion.</summary>
public static class FileToolNames
{
    /// <summary>
    /// Every file tool, the PDF converter last. Where there are no picture codecs (2026-10-06, the macOS build: <see cref="Files.ImageCodecs"/>)
    /// <c>view_image</c>, <c>image_info</c> and <c>image_edit</c> are not built, so they are not here either; on Windows all seventeen.
    /// </summary>
    public static readonly string[] All = [.. Everything.Where(name => Files.ImageCodecs.Available || name is not (ViewImageTool.ToolName or ImageInfoTool.ToolName or ImageEditTool.ToolName))];

    private static string[] Everything =>
    [
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
    ];

    /// <summary>
    /// The sixteen <c>ChatScreen.FileTools</c> builds without a PDF converter (the tests that build the list alone; 2026-10-03; the image
    /// tools since 2026-10-04), thirteen where there are no picture codecs (<see cref="All"/>).
    /// </summary>
    public static readonly string[] WithoutPdf = All[..^1];
}
