namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What a tool returns when the call changed nothing though it ran (2026-11-23, part of feature idea #64): <c>present_project_notes</c>' Revise,
/// Cancel and refusals. <see cref="Assistant"/> hands <paramref name="Text"/> to the model as any result and sets
/// <see cref="ToolInvocation.NothingRan"/>, so the undo journal leaves the call out of <c>/undo</c>'s "Not undone:". The
/// <see cref="ToolShownResult"/> shape; the other families' refusals (MCP, Docker, print, Home Assistant, database writes, shell) are #64's rest.
/// </summary>
public sealed record ToolNothingRanResult(string Text);
