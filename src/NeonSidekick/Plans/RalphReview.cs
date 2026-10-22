using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeonSidekick.Plans;

/// <summary>A review's verdict on a step (2026-10-18).</summary>
public enum RalphVerdictKind
{
    Pass,
    Fail,

    /// <summary>No verdict read (no tool call, no verdict line, a request error): logged, the step stands unchecked — never unticked.</summary>
    Unclear,

    /// <summary>
    /// The work the reviewer can see is right, but only a person can tell whether the step's <c>Done when:</c> holds (2026-10-21, the plan at
    /// <c>~/.claude/plans/i-put-you-in-snoopy-perlis.md</c>): the judge asks the user.
    /// </summary>
    NeedsUser,
}

/// <summary>A verdict and its reason (the reviewer's one sentence, empty with none).</summary>
public sealed record RalphVerdict(RalphVerdictKind Kind, string Reason)
{
    public static RalphVerdict Unclear(string reason = "") => new(RalphVerdictKind.Unclear, reason);
}

/// <summary>
/// Reading a Ralph review (2026-10-18): a <c>step_verdict</c> tool call's arguments first, else the answer's text — a <c>verdict: pass|fail|needs_user</c>
/// line, or a lone PASS, FAIL or NEEDS USER (any of Kind's words in capitals) near its end — else <see cref="RalphVerdictKind.Unclear"/>. Pure.
/// </summary>
public static partial class RalphReview
{
    /// <summary>The reviewer's tool.</summary>
    public const string ToolName = "step_verdict";

    public const string VerdictArgument = "verdict";
    public const string ReasonArgument = "reason";

    /// <summary>The verdict a <c>step_verdict</c> call's arguments give; null when they give none.</summary>
    public static RalphVerdict? FromCall(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(VerdictArgument, out object? value))
        {
            return null;
        }

        string? verdict = value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            _ => value?.ToString(),
        };
        string reason = arguments.TryGetValue(ReasonArgument, out object? r) ? r switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
            _ => r?.ToString() ?? "",
        } : "";
        return Kind(verdict) is { } kind ? new RalphVerdict(kind, reason.Trim()) : null;
    }

    /// <summary>The verdict an answer's text gives: its <c>verdict:</c> line (the last), else a lone PASS or FAIL in its last lines; else unclear.</summary>
    public static RalphVerdict FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return RalphVerdict.Unclear();
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var m = VerdictLine().Match(lines[i]);
            if (m.Success && Kind(m.Groups[1].Value) is { } kind)
            {
                string reason = m.Groups[2].Value.Trim().TrimStart('-', ':', '—', ' ').Trim();
                return new RalphVerdict(kind, reason.Length > 0 ? reason : Reason(lines, i));
            }
        }

        foreach (int i in Enumerable.Range(Math.Max(0, lines.Count - 3), Math.Min(3, lines.Count)).Reverse())
        {
            // The words Kind reads, in capitals (2026-10-22, a review: only NEEDS_USER was read here, while the verdict line took NEEDS USER and
            // NEEDS-USER too, so a bare **NEEDS USER** read as unclear and its step stood unchecked unasked).
            string bare = lines[i].Trim('*', '_', '.', '!', ' ', '`');
            if (string.Equals(bare, bare.ToUpperInvariant(), StringComparison.Ordinal) && Kind(bare) is { } kind)
            {
                return new RalphVerdict(kind, Reason(lines, i));
            }
        }

        return RalphVerdict.Unclear();
    }

    private static string Reason(List<string> lines, int verdictAt) => verdictAt > 0 ? lines[verdictAt - 1] : "";

    private static RalphVerdictKind? Kind(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "pass" or "passed" => RalphVerdictKind.Pass,
        "fail" or "failed" => RalphVerdictKind.Fail,
        "needs_user" or "needs user" or "needs-user" => RalphVerdictKind.NeedsUser,
        _ => null,
    };

    [GeneratedRegex(@"^\**\s*verdict\s*\**\s*:\s*\**\s*(pass(?:ed)?|fail(?:ed)?|needs[ _-]user)\b\**(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerdictLine();
}
