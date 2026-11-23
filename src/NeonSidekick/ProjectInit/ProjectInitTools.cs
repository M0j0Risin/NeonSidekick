using NeonSidekick.Llm.Tools;

namespace NeonSidekick.ProjectInit;

/// <summary>
/// The tools a <c>/project-init</c> pass is offered (2026-11-23), as <c>OnlyTools</c> narrows the turn: the file reads, the git reads and the
/// question; <c>present_project_notes</c> rides the turn's plan slot. Unlike <see cref="Audits.AuditTools.Always"/>, which keeps
/// <c>recall_memory</c>, the notes are often committed and read by others, so the user's memories stay out (and the prompt's memory section
/// with them); <c>load_skill</c> too, as the prompt's skills section goes. Plan mode's read-only narrowing applies first. Pure.
/// </summary>
public static class ProjectInitTools
{
    /// <summary>The names a pass keeps.</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        GetWorkingDirectoryTool.ToolName, SearchFilesTool.ToolName, FileInfoTool.ToolName, ReadFileTool.ToolName,
        GitStatusTool.ToolName, GitLogTool.ToolName, GitShowTool.ToolName, GitDiffTool.ToolName, GitBlameTool.ToolName,
        AskUserTool.ToolName,
    };
}
