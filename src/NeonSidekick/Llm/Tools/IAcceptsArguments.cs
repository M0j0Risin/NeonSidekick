namespace NeonSidekick.Llm.Tools;

/// <summary>
/// A tool that reads argument names its schema does not declare, on purpose (2026-11-15, feature idea #42): <c>camera_capture</c>'s prompt
/// aliases, <c>write_file</c>'s <c>old_text</c> kept for its "use patch_file" refusal, a hidden <c>share</c>. <see cref="ToolArgumentCheck"/>
/// lets these through as known names; any other undeclared name is still refused before the tool runs.
/// </summary>
public interface IAcceptsArguments
{
    /// <summary>The undeclared names the tool reads.</summary>
    IReadOnlyCollection<string> ExtraArguments { get; }

    /// <summary>True when the tool answers any key itself (<c>ask_user</c>, whose refusal lists what was sent): the check passes every call.</summary>
    bool AcceptsAnyArgument => false;
}
