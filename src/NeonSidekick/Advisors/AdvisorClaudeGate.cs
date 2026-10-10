using NeonSidekick.Settings;

namespace NeonSidekick.Advisors;

/// <summary>
/// Which advisor on the Claude Code server gets the app's tools (2026-10-10, the user's rules). The server is one <c>claude</c>
/// process holding one conversation, so it can carry one advisor's tool turns, and only while the chat itself is elsewhere:
/// <list type="bullet">
/// <item>the chat (the master) is not on the Claude Code server;</item>
/// <item>the advisor is the first in <see cref="AppSettingsData.AdvisorList"/> whose profile is on it (the user's pick: the first keeps the tools).</item>
/// </list>
/// Any other Claude Code advisor answers text-only through a one-shot <c>claude -p</c> (the user's pick: text only, not refused),
/// told it has no tools, its footer saying why. Pure.
/// </summary>
public static class AdvisorClaudeGate
{
    /// <summary>The advisor that holds the Claude Code server's tools: the first of <paramref name="entries"/> on it; null when the chat is on it, or none is.</summary>
    public static string? Holder(IReadOnlyList<AdvisorEntry> entries, Func<string, bool> onClaudeCode, bool masterOnClaudeCode)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(onClaudeCode);
        return masterOnClaudeCode ? null : entries.FirstOrDefault(e => onClaudeCode(e.Profile))?.Profile;
    }

    /// <summary>Why <paramref name="profile"/>, on the Claude Code server, answers text-only; null when it holds the tools. Pinned.</summary>
    public static string? TextOnlyReason(string profile, string? holder, bool masterOnClaudeCode) =>
        masterOnClaudeCode ? AdvisorText.MasterOnClaudeCodeNote
        : AdvisorRoster.SameProfile(profile, holder) ? null
        : AdvisorText.OtherClaudeCodeNote(holder ?? "");
}
