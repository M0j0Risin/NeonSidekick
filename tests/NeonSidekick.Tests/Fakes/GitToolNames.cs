using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests.Fakes;

/// <summary>The git tools in the order <c>ChatScreen.GitTools</c> offers them (2026-09-20). Pinned once, used by every tool-list assertion.</summary>
public static class GitToolNames
{
    public static readonly string[] All =
    {
        GitStatusTool.ToolName,
        GitLogTool.ToolName,
        GitShowTool.ToolName,
        GitDiffTool.ToolName,
        GitBlameTool.ToolName,
        GitBranchTool.ToolName,
        GitStageTool.ToolName,
        GitCommitTool.ToolName,
        GitStashTool.ToolName,
        GitRevertTool.ToolName,
        GitTagTool.ToolName,
        GitDiscardTool.ToolName,
        GitDeleteTool.ToolName,
    };
}
