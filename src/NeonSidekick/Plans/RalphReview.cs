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
}

/// <summary>A verdict and its reason (the reviewer's one sentence, empty with none).</summary>
public sealed record RalphVerdict(RalphVerdictKind Kind, string Reason)
{
    public static RalphVerdict Unclear(string reason = "") => new(RalphVerdictKind.Unclear, reason);
}

/// <summary>
/// Reading a Ralph review (2026-10-18): a <c>step_verdict</c> tool call's arguments first, else the answer's text — a <c>verdict: pass|fail</c> line,
/// or a lone PASS or FAIL near its end — else <see cref="RalphVerdictKind.Unclear"/>. Pure.
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
            string bare = lines[i].Trim('*', '_', '.', '!', ' ', '`');
            if (bare is "PASS" or "FAIL")
            {
                return new RalphVerdict(bare == "PASS" ? RalphVerdictKind.Pass : RalphVerdictKind.Fail, Reason(lines, i));
            }
        }

        return RalphVerdict.Unclear();
    }

    private static string Reason(List<string> lines, int verdictAt) => verdictAt > 0 ? lines[verdictAt - 1] : "";

    private static RalphVerdictKind? Kind(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "pass" or "passed" => RalphVerdictKind.Pass,
        "fail" or "failed" => RalphVerdictKind.Fail,
        _ => null,
    };

    [GeneratedRegex(@"^\**\s*verdict\s*\**\s*:\s*\**\s*(pass(?:ed)?|fail(?:ed)?)\b\**(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VerdictLine();
}
