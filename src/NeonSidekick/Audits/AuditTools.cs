using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

namespace NeonSidekick.Audits;

/// <summary>
/// The tools a code audit's pass is offered (2026-10-18), as <c>OnlyTools</c> narrows the turn: the file reads, the picture and PDF reads,
/// the git reads, the advisors, the manual, the clock, the skills' load (a skill may hold coding standards), memory's recall and the
/// question; the web's reads only under <see cref="AppSettingsData.CodeAuditWebResearch"/> (off by default: audited code may carry
/// injected instructions, and the web is the one way a read-only pass could send code out). Plan mode's read-only narrowing applies first,
/// so nothing here could ever change anything; an advisor asked in a pass is offered the same set. Pure.
/// </summary>
public static class AuditTools
{
    /// <summary>The names kept whatever the settings say.</summary>
    public static readonly IReadOnlyList<string> Always =
    [
        GetWorkingDirectoryTool.ToolName, SearchFilesTool.ToolName, FileInfoTool.ToolName, ReadFileTool.ToolName, ViewImageTool.ToolName, ReadPdfTool.ToolName,
        GitStatusTool.ToolName, GitLogTool.ToolName, GitShowTool.ToolName, GitDiffTool.ToolName, GitBlameTool.ToolName,
        NeonAdvisorTool.ToolName, NeonHelpTool.ToolName, GetCurrentTimeTool.ToolName, LoadSkillTool.ToolName, RecallMemoryTool.ToolName, AskUserTool.ToolName,
    ];

    /// <summary>The web's reads, kept only under <c>LLM code audit web research</c>.</summary>
    public static readonly IReadOnlyList<string> Web = [WebSearchTool.ToolName, WebFetchTool.ToolName, WebLinksTool.ToolName];

    /// <summary>The set a pass keeps for <paramref name="effective"/>, read per turn.</summary>
    public static IReadOnlySet<string> Allowed(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var allowed = new HashSet<string>(Always, StringComparer.Ordinal);
        if (effective.CodeAuditWebResearch)
        {
            allowed.UnionWith(Web);
        }

        return allowed;
    }
}
