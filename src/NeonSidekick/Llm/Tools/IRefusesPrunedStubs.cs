namespace NeonSidekick.Llm.Tools;

/// <summary>
/// A tool that refuses a pruned stub (<see cref="ConversationCompactor.IsPrunedStub"/>) in its own words in some of its arguments (2026-10-21): the
/// write tools' <see cref="Files.FileText.PrunedStubSent"/>, <c>present_plan</c>'s <see cref="Plans.PlanText.MarkdownStubSent"/>, <c>present_audit</c>'s
/// <see cref="Audits.AuditText.StubSentResult"/>, each naming where to read the text again. <see cref="Assistant.InvokeToolWithDiffAsync"/> refuses a
/// stub sent anywhere else with <see cref="App.CompactionText.PrunedStubSentResult"/> before the tool runs — every other tool's arguments, and these
/// tools' arguments not in <see cref="StubArguments"/> (2026-10-22, a review: a marker exempted the whole tool, and a stub sent as patch_file's
/// <c>old_text</c> or present_plan's <c>verify</c> reached the tool, which checked one argument).
/// </summary>
public interface IRefusesPrunedStubs
{
    /// <summary>The top-level arguments the tool refuses a stub in itself; the generic refusal covers the rest.</summary>
    IReadOnlyCollection<string> StubArguments { get; }
}
