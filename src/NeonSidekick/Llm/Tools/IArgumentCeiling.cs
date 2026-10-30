namespace NeonSidekick.Llm.Tools;

/// <summary>
/// A tool that knows how long its arguments can honestly get (2026-10-29, session 832: a <c>present_audit</c> retry streamed 60,259 tokens of
/// arguments). Past <see cref="MaxArgumentChars"/> of streamed arguments the turn stops the request (<see cref="ToolArgumentWatch"/>) and ends
/// on <see cref="Assistant.RunawayNotice"/>; a tool without one is never stopped.
/// </summary>
public interface IArgumentCeiling
{
    /// <summary>
    /// The most characters of arguments (as JSON, escapes and all) a good-faith call sends, with room to spare: a call the tool would take is
    /// never stopped. Read as a call is named, so it may follow the tool's state (<c>present_audit</c>'s grows with the audit's scope).
    /// </summary>
    int MaxArgumentChars { get; }
}
