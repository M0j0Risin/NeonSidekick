using System.Globalization;

namespace NeonSidekick.Llm;

/// <summary>
/// The turn loop's own words about a call it stopped (2026-10-30, review F37: they were constants of <see cref="Assistant"/> and
/// <see cref="ToolArgumentWatch"/>, apart from the area's other text, which is all about the model's errors).
/// </summary>
public static class TurnText
{
    /// <summary>The name a call goes by on the busy row while no chunk has named it. Pinned.</summary>
    public const string UnnamedCall = "(unnamed)";

    /// <summary>
    /// What follows <see cref="RunawayNotice"/> when the user is the one to go on (2026-10-30, review F96): not under an audit or Ralph
    /// pass, whose own stop line says how to go on. Pinned.
    /// </summary>
    public const string AskAgain = " Ask again.";

    /// <summary>
    /// The error a turn ends on when a call streamed past its tool's ceiling (2026-10-29, session 832: a <c>present_audit</c> retry wrote
    /// 60,259 tokens of arguments for seven minutes): the request was dropped and nothing ran. The way on is the host's to add
    /// (<see cref="TurnEvent.Notice.AskAgain"/>). Pinned.
    /// </summary>
    public static string RunawayNotice(string name, int ceiling) =>
        string.Create(CultureInfo.InvariantCulture, $"Stopped: the model's call to {name} ran past {ceiling:N0} characters, more than it ever takes, so nothing ran. It was likely repeating itself.");

    /// <summary>The <c>--log</c> line for a call stopped past its ceiling (2026-10-29). Pinned.</summary>
    internal static string RunawayLogLine(string name, long chars, int ceiling) =>
        string.Create(CultureInfo.InvariantCulture, $"Arguments for tool '{name}' passed {chars:N0} characters (its ceiling is {ceiling:N0}); the request was stopped.");

    /// <summary><paramref name="notice"/> as the user reads it: <see cref="AskAgain"/> after a notice that asks for it, unless a loop's pass runs (F96).</summary>
    public static string Shown(TurnEvent.Notice notice, bool loopPass)
    {
        ArgumentNullException.ThrowIfNull(notice);
        return notice.AskAgain && !loopPass ? notice.Text + AskAgain : notice.Text;
    }
}
