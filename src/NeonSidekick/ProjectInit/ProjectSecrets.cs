using System.Text.RegularExpressions;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.ProjectInit;

/// <summary>
/// The secret rules <c>/project-init</c> masks the digest with and lists a draft's lines by (2026-11-23): <see cref="ToolRedaction.Prose"/>,
/// which leaves the build lines alone (<c>docker run -p 8080:80</c>, <c>mvn -P release</c>), and the bare key shapes no key=value rule
/// sees (<c>ghp_…</c>, <c>github_pat_…</c>, <c>sk-…</c>, <c>AKIA…</c>, <c>xoxb-…</c>). Never blocks: the digest is masked, a draft only
/// warned about. Pure.
/// </summary>
public static partial class ProjectSecrets
{
    /// <summary><paramref name="text"/> with every secret the rules know hidden as <see cref="ToolRedaction.Placeholder"/>.</summary>
    public static string Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? text : BareKey().Replace(ToolRedaction.Prose(text), ToolRedaction.Placeholder);
    }

    /// <summary>
    /// The lines of <paramref name="text"/> the rules would change, or that hold <see cref="ToolRedaction.Placeholder"/> already (a value hidden
    /// from the model, which Write would save as the mask): each 1-based line number and the line masked.
    /// </summary>
    public static IReadOnlyList<ProjectSecretLine> Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<ProjectSecretLine>();
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string masked = Mask(lines[i]);
            bool holds = lines[i].Contains(ToolRedaction.Placeholder, StringComparison.Ordinal);
            if (holds || !string.Equals(masked, lines[i], StringComparison.Ordinal))
            {
                found.Add(new ProjectSecretLine(i + 1, masked.Trim(), holds));
            }
        }

        return found;
    }

    [GeneratedRegex(@"\b(?:ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|xox[bpas]-[A-Za-z0-9-]{10,})", RegexOptions.CultureInvariant)]
    private static partial Regex BareKey();
}
