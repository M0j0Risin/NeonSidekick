namespace NeonSidekick.Llm.Tools;

/// <summary>
/// A tool that refuses a pruned stub (<see cref="ConversationCompactor.IsPrunedStub"/>) in its own words (2026-10-21): the write tools'
/// <see cref="Files.FileText.PrunedStubSent"/>, <c>present_plan</c>'s <see cref="Plans.PlanText.MarkdownStubSent"/>, <c>present_audit</c>'s
/// <see cref="Audits.AuditText.StubSentResult"/>, each naming where to read the text again. <see cref="Assistant.InvokeToolWithDiffAsync"/>
/// refuses a stub sent to any other tool with <see cref="ConversationCompactor.PrunedStubSentResult"/> before it runs, and leaves these to
/// their own check. A marker: nothing to implement.
/// </summary>
public interface IRefusesPrunedStubs;
