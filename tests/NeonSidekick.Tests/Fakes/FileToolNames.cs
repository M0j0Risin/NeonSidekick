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

    /// <summary>The sixteen <c>ChatScreen.FileTools</c> builds without a PDF converter (the tests that build the list alone; 2026-10-03; the image tools since 2026-10-04).</summary>
    public static readonly string[] WithoutPdf = All[..^1];
}
