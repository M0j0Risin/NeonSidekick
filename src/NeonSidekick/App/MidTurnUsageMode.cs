using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>What the busy row's token tally shows while a request streams (<see cref="MidTurnUsageMode"/>).</summary>
public enum MidTurnUsage
{
    /// <summary>The live estimate: the streamed chunks counted as tokens, marked <c>~</c>, until the server's report replaces them.</summary>
    Estimate,

    /// <summary>The server's figures as of the last completed request, nothing estimated.</summary>
    LastKnown,
}

/// <summary>
/// The setting <c>LLM mid-turn usage</c> (2026-09-25, the user's words): the busy row carries the token tally
/// the idle row does, and this says how — <c>estimate</c> (the default: the context and the speed ticking with
/// the stream, marked <c>~</c>) or <c>last-known</c> (the server's report of the last completed request, still
/// between reports). The server reports usage only in a request's last chunk, so anything live is an estimate.
/// The <see cref="BotChatLlmMode"/> shape: <see cref="Resolve"/> is the one place the saved string becomes the enum.
/// </summary>
public static class MidTurnUsageMode
{
    /// <summary>Estimate: the compiled default. Pinned.</summary>
    public const string Default = "estimate";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "estimate", "last-known" };

    private const string Category = "Screen";

    /// <summary>Trims and ignores case; false (and <see cref="MidTurnUsage.Estimate"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out MidTurnUsage mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "estimate": mode = MidTurnUsage.Estimate; return true;
            case "last-known": mode = MidTurnUsage.LastKnown; return true;
            default: mode = MidTurnUsage.Estimate; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "estimate" => "the context and tok/s tick live while a reply streams (~, streamed chunks counted as tokens)",
        "last-known" => "the server's figures as of the last completed request",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static MidTurnUsage Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.LlmMidTurnUsage, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.LlmMidTurnUsage)}='{effective.LlmMidTurnUsage}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return MidTurnUsage.Estimate;
    }
}
