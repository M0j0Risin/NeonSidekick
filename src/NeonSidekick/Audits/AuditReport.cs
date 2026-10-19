using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.Audits;

/// <summary>Where a code audit stands, the report's <c>status:</c> line (2026-10-18).</summary>
public enum AuditStatus
{
    /// <summary>Passes under way (or, with no live loop after a restart, listed as stopped).</summary>
    Auditing,

    /// <summary>Enough clean passes in a row.</summary>
    Converged,

    /// <summary>Stopped short: the passes line says why.</summary>
    Stopped,

    /// <summary>A fix plan was started from it (<c>Fix via plan</c>, <c>/code-audit fix</c>).</summary>
    Fixing,
}

/// <summary>
/// A report's front matter (2026-10-18): its status, the passes line (the app's words: the count, the clean streak, the share read, a
/// stop's reason), the scope as the line typed it (<see cref="AuditCommand"/> words, a blank-holding path quoted; empty for the whole
/// tree), the focus, the brief, the reviewer, the next id (never reused), when it was made and last saved.
/// </summary>
public sealed record AuditHeader(
    AuditStatus Status,
    string Passes,
    string Scope,
    IReadOnlyList<string> Focus,
    string Brief,
    string Reviewer,
    int NextId,
    DateTimeOffset? Created,
    DateTimeOffset? Updated);

/// <summary>
/// A finding line <see cref="AuditReport.Parse"/> could not read, kept verbatim under its severity with the indented lines after it
/// (2026-10-18, a hand edit never loses anything); counted open unless its box is ticked.
/// </summary>
public sealed record AuditRawBlock(AuditSeverity Severity, string Text)
{
    /// <summary>A bullet whose box is not ticked counts as an open finding; stray text under a heading counts as nothing.</summary>
    public bool CountsOpen => Text.StartsWith("- ", StringComparison.Ordinal) && !Text.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase);

    /// <summary>A bullet whose box is ticked.</summary>
    public bool CountsSettled => Text.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A <c>## </c> section the report does not know (a hand edit's), kept whole on every save.</summary>
public sealed record AuditSection(string Heading, string Body);

/// <summary>
/// A code audit's report (2026-10-18, <c>.neon/audits/&lt;name&gt;.md</c>): a front-matter block the app owns, then
/// <c>## Summary</c>, <c>## Findings</c> under <c>### Critical/High/Medium/Low</c> (one <c>- [ ] **A3** [security] src/x.cs:42 — title</c>
/// each, its detail, fix, evidence, pass and notes indented under it), <c>## Nits</c>, <c>## Open questions</c>, <c>## Coverage</c>,
/// <c>## Checked</c> and <c>## Retracted</c>. The app writes it (<c>present_audit</c> carries a pass's changes, never the document), so
/// every finding has one shape. <see cref="Parse"/> is tolerant of what an editor does to it — CRLF, <c>-</c> for <c>—</c>, <c>[X]</c>,
/// an unknown section (kept), a finding line it cannot read (kept verbatim, counted open) — and round-trips what <see cref="Render"/>
/// writes. Text the model sent can never forge structure: a title is one line, every body line is indented under its part with a line
/// opening on <c>#</c>, <c>- [</c> or <c>\</c> escaped, and evidence sits in a fence one backtick longer than any run in it. Pure.
/// </summary>
public sealed partial class AuditReport
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm";
    private const string Fence = "---";
    private const string TitlePrefix = "# Code audit: ";

    public const string SummaryHeading = "Summary";
    public const string FindingsHeading = "Findings";
    public const string NitsHeading = "Nits";
    public const string QuestionsHeading = "Open questions";
    public const string CoverageHeading = "Coverage";
    public const string CheckedHeading = "Checked";
    public const string RetractedHeading = "Retracted";

    private const string DetailPart = "Detail";
    private const string FixPart = "Fix";
    private const string EvidencePart = "Evidence";
    private const string PassPart = "Pass";
    private const string NotePart = "Note";
    private const string ReasonPart = "Reason";

    private static readonly string[] Parts = [DetailPart, FixPart, EvidencePart, PassPart, NotePart, ReasonPart];

    public AuditHeader Header { get; set; } = new(AuditStatus.Auditing, "", "", AuditFocus.Dimensions, "", "model", 1, null, null);

    /// <summary>Whether the text had a header the app wrote (a hand-made report has none).</summary>
    public bool HasHeader { get; set; }

    /// <summary>The title's words after <c>Code audit:</c>: the scope's label.</summary>
    public string Label { get; set; } = "";

    public string Summary { get; set; } = "";

    /// <summary>The findings not retracted, open and settled, in their order.</summary>
    public List<AuditFinding> Findings { get; } = [];

    /// <summary>The finding lines <see cref="Parse"/> could not read, kept as they were.</summary>
    public List<AuditRawBlock> Unreadable { get; } = [];

    public List<string> Nits { get; } = [];

    public List<string> Questions { get; } = [];

    /// <summary>The coverage line's figures: files read, out of the scope's listed files (0 when not known yet).</summary>
    public int CoverageRead { get; set; }

    public int CoverageScope { get; set; }

    /// <summary>The areas the passes said they covered.</summary>
    public List<string> Areas { get; } = [];

    /// <summary>What each clean pass said it checked (<c>pass 3: …</c>).</summary>
    public List<string> Checked { get; } = [];

    public List<AuditFinding> Retracted { get; } = [];

    /// <summary>
    /// The <c>## Retracted</c> blocks <see cref="Parse"/> could not read, kept verbatim after the readable ones (2026-10-19, the review: they
    /// were dropped on the next save, unlike the findings' <see cref="Unreadable"/>); counted as nothing.
    /// </summary>
    public List<string> UnreadableRetracted { get; } = [];

    /// <summary>
    /// Text between the title and the first section, a hand edit's (2026-10-19, the review: dropped on the next save), kept verbatim under the
    /// title.
    /// </summary>
    public string Preamble { get; set; } = "";

    /// <summary>
    /// A list section's lines that are no bullet and continue none (2026-10-19, the review: a note typed under <c>## Nits</c>, <c>## Open
    /// questions</c>, <c>## Coverage</c> or <c>## Checked</c> was dropped on the next save), by the section's heading, kept verbatim under it
    /// ahead of its bullets. A line straight after a bullet joins that bullet.
    /// </summary>
    public Dictionary<string, List<string>> Prose { get; } = new(StringComparer.Ordinal);

    /// <summary>Sections the report does not know, kept.</summary>
    public List<AuditSection> Other { get; } = [];

    /// <summary>The open findings: unticked and not retracted, in their order.</summary>
    public IEnumerable<AuditFinding> Open => Findings.Where(f => f.State == AuditFindingState.Open);

    /// <summary>Open findings, an unreadable bullet that is not ticked counted too.</summary>
    public int OpenCount => Findings.Count(f => f.State == AuditFindingState.Open) + Unreadable.Count(r => r.CountsOpen);

    /// <summary>Settled (ticked) findings, a ticked unreadable bullet counted too.</summary>
    public int SettledCount => Findings.Count(f => f.State == AuditFindingState.Settled) + Unreadable.Count(r => r.CountsSettled);

    /// <summary>Every finding ticked (2026-10-18): derived from the body, never written, so no code watches the fix plan.</summary>
    public bool Fixed => OpenCount == 0 && SettledCount > 0;

    /// <summary>Open findings by severity, Critical first.</summary>
    public IReadOnlyList<(AuditSeverity Severity, int Count)> OpenBySeverity() =>
        Enum.GetValues<AuditSeverity>().Select(s => (s, Findings.Count(f => f.State == AuditFindingState.Open && f.Severity == s) + Unreadable.Count(r => r.Severity == s && r.CountsOpen)))
            .Where(p => p.Item2 > 0).ToList();

    /// <summary>The highest id number in the body (findings and retracted alike), 0 for none.</summary>
    public int HighestId() => Findings.Concat(Retracted).Select(f => AuditFinding.IdNumber(f.Id) ?? 0).DefaultIfEmpty(0).Max();

    /// <summary>The next id's number: the header's <c>next-id:</c>, raised past any id in the body (a hand edit), at least 1.</summary>
    public int NextIdNumber => Math.Max(Math.Max(1, Header.NextId), HighestId() + 1);

    // ── Render ──────────────────────────────────────────────────────────────

    /// <summary>The whole file.</summary>
    public string Render() => HeaderText(Header) + RenderBody();

    /// <summary>The file less its front matter: the title and the sections (the Web UI's card shows it).</summary>
    public string RenderBody()
    {
        var sb = new StringBuilder();
        sb.Append(TitlePrefix).Append(Flatten(Label)).Append("\n\n");
        if (Preamble.Trim().Length > 0)
        {
            sb.Append(Preamble.Trim('\n')).Append("\n\n");
        }

        sb.Append("## ").Append(SummaryHeading).Append("\n\n");
        if (Summary.Trim().Length > 0)
        {
            foreach (string line in BodyLines(Summary))
            {
                sb.Append(line).Append('\n');
            }

            sb.Append('\n');
        }

        sb.Append(FindingsSection());
        sb.Append(BulletSection(NitsHeading, Nits));
        sb.Append(BulletSection(QuestionsHeading, Questions));
        sb.Append("## ").Append(CoverageHeading).Append("\n\n");
        sb.Append(ProseText(CoverageHeading));
        if (CoverageScope > 0)
        {
            sb.Append(CoverageLine(CoverageRead, CoverageScope)).Append("\n\n");
        }

        if (Areas.Count > 0)
        {
            foreach (string area in Areas)
            {
                sb.Append("- ").Append(Flatten(area)).Append('\n');
            }

            sb.Append('\n');
        }

        sb.Append(BulletSection(CheckedHeading, Checked));
        sb.Append(RetractedSection());
        foreach (var other in Other)
        {
            sb.Append("## ").Append(other.Heading).Append("\n\n");
            if (other.Body.Trim().Length > 0)
            {
                sb.Append(other.Body.Trim('\n')).Append("\n\n");
            }
        }

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// The body the loop compares for a stall (2026-10-18): the <c>## Findings</c> and <c>## Retracted</c> sections alone, so the coverage,
    /// checked and summary lines a clean pass changes never mask one.
    /// </summary>
    public string StallBody() => FindingsSection() + RetractedSection();

    /// <summary><c>412 of 1,204 files read (34%).</c></summary>
    public static string CoverageLine(int read, int scope) =>
        $"{N(read)} of {N(scope)} files read ({Percent(read, scope).ToString(CultureInfo.InvariantCulture)}%).";

    /// <summary>The whole percent <paramref name="read"/> is of <paramref name="scope"/>, rounded down, 0 for an empty scope.</summary>
    public static int Percent(int read, int scope) => scope <= 0 ? 0 : (int)Math.Min(100, (long)Math.Max(0, read) * 100 / scope);

    private string FindingsSection()
    {
        var sb = new StringBuilder("## " + FindingsHeading + "\n\n");
        foreach (var severity in Enum.GetValues<AuditSeverity>())
        {
            var findings = Findings.Where(f => f.Severity == severity).ToList();
            var raw = Unreadable.Where(r => r.Severity == severity).ToList();
            if (findings.Count == 0 && raw.Count == 0)
            {
                continue;
            }

            sb.Append("### ").Append(AuditFinding.Heading(severity)).Append("\n\n");
            foreach (var finding in findings)
            {
                sb.Append(FindingText(finding, retracted: false)).Append('\n');
            }

            foreach (var block in raw)
            {
                sb.Append(block.Text.TrimEnd('\n')).Append("\n\n");
            }
        }

        return sb.ToString();
    }

    private string RetractedSection()
    {
        var sb = new StringBuilder("## " + RetractedHeading + "\n\n");
        foreach (var finding in Retracted)
        {
            sb.Append(FindingText(finding, retracted: true)).Append('\n');
        }

        foreach (string block in UnreadableRetracted)
        {
            sb.Append(block.TrimEnd('\n')).Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>A section's kept prose (<see cref="Prose"/>), a blank line after it; empty for none.</summary>
    private string ProseText(string heading) =>
        Prose.TryGetValue(heading, out var lines) && string.Join('\n', lines).Trim('\n') is { Length: > 0 } text ? text + "\n\n" : "";

    private string BulletSection(string heading, IReadOnlyList<string> items)
    {
        var sb = new StringBuilder("## " + heading + "\n\n");
        sb.Append(ProseText(heading));
        if (items.Count > 0)
        {
            foreach (string item in items)
            {
                sb.Append("- ").Append(EscapeLine(Flatten(item))).Append('\n');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>One finding's block: its line, then its parts indented under it, ending in a newline.</summary>
    public static string FindingText(AuditFinding finding, bool retracted)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var sb = new StringBuilder("- ");
        if (!retracted)
        {
            sb.Append(finding.Settled ? "[x] " : "[ ] ");
        }

        sb.Append("**").Append(finding.Id).Append("** ");
        if (retracted)
        {
            sb.Append('(').Append(AuditFinding.Word(finding.Severity)).Append(") ");
        }

        sb.Append('[').Append(Flatten(finding.Dimension)).Append("] ").Append(Flatten(finding.Location)).Append(" — ").Append(Flatten(finding.Title)).Append('\n');
        if (retracted && finding.RetractReason is { Length: > 0 } reason)
        {
            AppendPart(sb, ReasonPart, reason);
        }

        AppendPart(sb, DetailPart, finding.Detail);
        if (finding.Fix is { Length: > 0 } fix)
        {
            AppendPart(sb, FixPart, fix);
        }

        if (finding.Evidence is { Length: > 0 } evidence)
        {
            string fence = FenceFor(evidence);
            sb.Append("  ").Append(EvidencePart).Append(":\n");
            sb.Append("    ").Append(fence).Append('\n');
            foreach (string line in Normalize(evidence).Split('\n'))
            {
                sb.Append(line.Length == 0 ? "" : "    " + line).Append('\n');
            }

            sb.Append("    ").Append(fence).Append('\n');
        }

        sb.Append("  ").Append(PassPart).Append(": ").Append(N(finding.Pass)).Append('\n');
        foreach (string note in finding.Notes)
        {
            AppendPart(sb, NotePart, note);
        }

        return sb.ToString();
    }

    /// <summary>A part's first line after its label, every later line indented four; blank lines dropped, each line escaped.</summary>
    private static void AppendPart(StringBuilder sb, string label, string text)
    {
        var lines = BodyLines(text);
        if (lines.Count == 0)
        {
            return;
        }

        sb.Append("  ").Append(label).Append(": ").Append(lines[0]).Append('\n');
        foreach (string line in lines.Skip(1))
        {
            sb.Append("    ").Append(line).Append('\n');
        }
    }

    /// <summary>A text's lines with the blank ones dropped, each trimmed at its end and escaped.</summary>
    private static List<string> BodyLines(string text) =>
        Normalize(text).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).Select(EscapeLine).ToList();

    /// <summary>
    /// A line of model text made safe for the report (2026-10-18, text hygiene): one opening (after its spaces) on <c>#</c>, <c>- [</c> or
    /// <c>\</c> gains a <c>\</c> in front, so it can never read back as a heading, a finding or a box; <see cref="UnescapeLine"/> undoes it.
    /// </summary>
    public static string EscapeLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string trimmed = line.TrimStart();
        return trimmed.StartsWith('#') || trimmed.StartsWith("- [", StringComparison.Ordinal) || trimmed.StartsWith('\\') || trimmed.StartsWith("---", StringComparison.Ordinal)
            ? "\\" + line
            : line;
    }

    public static string UnescapeLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.StartsWith('\\') ? line[1..] : line;
    }

    /// <summary>The evidence's fence: backticks, one more than the longest run in <paramref name="text"/>, at least three.</summary>
    public static string FenceFor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    private static string HeaderText(AuditHeader header) =>
        new StringBuilder()
            .Append(Fence).Append('\n')
            .Append("status: ").Append(StatusWord(header.Status)).Append('\n')
            .Append("passes: ").Append(Flatten(header.Passes)).Append('\n')
            .Append("scope: ").Append(Flatten(header.Scope)).Append('\n')
            .Append("focus: ").Append(AuditFocus.Label(header.Focus)).Append('\n')
            .Append("brief: ").Append(Quote(header.Brief)).Append('\n')
            .Append("reviewer: ").Append(Flatten(header.Reviewer)).Append('\n')
            .Append("next-id: ").Append(N(Math.Max(1, header.NextId))).Append('\n')
            .Append(header.Created is { } created ? "created: " + created.ToString(TimeFormat, CultureInfo.InvariantCulture) + "\n" : "")
            .Append(header.Updated is { } updated ? "updated: " + updated.ToString(TimeFormat, CultureInfo.InvariantCulture) + "\n" : "")
            .Append(Fence).Append("\n\n")
            .ToString();

    /// <summary>The status word written in the file.</summary>
    public static string StatusWord(AuditStatus status) => status switch
    {
        AuditStatus.Converged => "converged",
        AuditStatus.Stopped => "stopped",
        AuditStatus.Fixing => "fixing",
        _ => "auditing",
    };

    private static AuditStatus? ParseStatus(string word) => word.Trim().ToLowerInvariant() switch
    {
        "auditing" => AuditStatus.Auditing,
        "converged" => AuditStatus.Converged,
        "stopped" => AuditStatus.Stopped,
        "fixing" => AuditStatus.Fixing,
        _ => null,
    };

    // ── Parse ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The report in <paramref name="text"/>, read as tolerantly as an editor needs: CRLF or LF, a missing header (<see cref="HasHeader"/>
    /// false), <c>-</c> for <c>—</c>, <c>[X]</c>, unknown sections kept, an unreadable finding line kept verbatim under its severity.
    /// </summary>
    public static AuditReport Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var report = new AuditReport();
        string normal = Normalize(text).TrimStart('﻿');
        var lines = normal.Split('\n').ToList();
        int at = 0;
        if (lines.Count > 0 && lines[0].TrimEnd() == Fence)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            int end = lines.FindIndex(1, l => l.TrimEnd() == Fence);
            if (end > 0)
            {
                foreach (string line in lines.Skip(1).Take(end - 1))
                {
                    int colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0)
                    {
                        fields[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
                    }
                }

                report.Header = HeaderFrom(fields, out bool known);
                report.HasHeader = known;
                at = end + 1;
            }
        }

        string section = "";
        AuditSeverity? severity = null;
        var summary = new List<string>();
        var otherBody = new List<string>();
        var preamble = new List<string>();
        string? otherHeading = null;
        bool titled = false;
        bool continues = false;
        while (at < lines.Count)
        {
            string line = lines[at];
            if (line.StartsWith("# ", StringComparison.Ordinal) && section.Length == 0 && otherHeading is null && !titled)
            {
                report.Label = line.StartsWith(TitlePrefix, StringComparison.OrdinalIgnoreCase) ? line[TitlePrefix.Length..].Trim() : line[2..].Trim();
                titled = true;
                at++;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushOther(report, ref otherHeading, otherBody);
                string heading = line[3..].Trim();
                section = KnownSection(heading) ?? "";
                severity = null;
                continues = false;
                if (section.Length == 0)
                {
                    otherHeading = heading;
                }

                at++;
                continue;
            }

            if (otherHeading is not null)
            {
                otherBody.Add(line);
                at++;
                continue;
            }

            switch (section)
            {
                case SummaryHeading:
                    summary.Add(UnescapeLine(line));
                    at++;
                    break;
                case FindingsHeading:
                    if (line.StartsWith("### ", StringComparison.Ordinal))
                    {
                        severity = Enum.TryParse<AuditSeverity>(line[4..].Trim(), ignoreCase: true, out var s) ? s : severity ?? AuditSeverity.Medium;
                        at++;
                    }
                    else if (line.Trim().Length == 0)
                    {
                        at++;
                    }
                    else
                    {
                        at = ReadFindingBlock(lines, at, out var block);
                        var sev = severity ?? AuditSeverity.Medium;
                        if (TryFinding(block, sev, retracted: false, out var finding))
                        {
                            report.Findings.Add(finding);
                        }
                        else
                        {
                            report.Unreadable.Add(new AuditRawBlock(sev, string.Join('\n', block)));
                        }
                    }

                    break;
                case RetractedHeading:
                    if (line.Trim().Length == 0)
                    {
                        at++;
                    }
                    else
                    {
                        at = ReadFindingBlock(lines, at, out var block);
                        if (TryFinding(block, AuditSeverity.Medium, retracted: true, out var finding))
                        {
                            report.Retracted.Add(finding);
                        }
                        else
                        {
                            report.UnreadableRetracted.Add(string.Join('\n', block));
                        }
                    }

                    break;
                case NitsHeading:
                    AddListLine(report, NitsHeading, report.Nits, line, ref continues);
                    at++;
                    break;
                case QuestionsHeading:
                    AddListLine(report, QuestionsHeading, report.Questions, line, ref continues);
                    at++;
                    break;
                case CheckedHeading:
                    AddListLine(report, CheckedHeading, report.Checked, line, ref continues);
                    at++;
                    break;
                case CoverageHeading:
                    if (CoverageFigures().Match(line) is { Success: true } m)
                    {
                        report.CoverageRead = Number(m.Groups["read"].Value);
                        report.CoverageScope = Number(m.Groups["scope"].Value);
                        continues = false;
                    }
                    else
                    {
                        AddListLine(report, CoverageHeading, report.Areas, line, ref continues);
                    }

                    at++;
                    break;
                default:
                    // Before the first section: a hand edit's text under the title, kept.
                    preamble.Add(line);
                    at++;
                    break;
            }
        }

        FlushOther(report, ref otherHeading, otherBody);
        report.Summary = string.Join('\n', summary).Trim('\n').Trim();
        report.Preamble = string.Join('\n', preamble).Trim('\n');
        report.Renumber();
        return report;
    }

    /// <summary>
    /// Every id made unique (2026-10-19, the review: a finding block copied in an editor, or a new one typed with an id in use, gave two findings
    /// one id, and <c>present_audit</c>'s map of the open ones threw on every later call): the first keeps it, findings before retracted ones, and
    /// each later one takes the next free number with a note naming the id it had. Nothing is dropped.
    /// </summary>
    private void Renumber()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int next = NextIdNumber;
        foreach (var list in (List<AuditFinding>[])[Findings, Retracted])
        {
            for (int i = 0; i < list.Count; i++)
            {
                var finding = list[i];
                if (seen.Add(finding.Id))
                {
                    continue;
                }

                string id = AuditFinding.IdFor(next++);
                seen.Add(id);
                list[i] = finding with { Id = id, Notes = [.. finding.Notes, AuditText.RenumberedNote(finding.Id)] };
            }
        }

        if (next > Header.NextId)
        {
            Header = Header with { NextId = next };
        }
    }

    private static void FlushOther(AuditReport report, ref string? heading, List<string> body)
    {
        if (heading is not null)
        {
            report.Other.Add(new AuditSection(heading, string.Join('\n', body).Trim('\n')));
        }

        heading = null;
        body.Clear();
    }

    private static string? KnownSection(string heading)
    {
        string[] known = [SummaryHeading, FindingsHeading, NitsHeading, QuestionsHeading, CoverageHeading, CheckedHeading, RetractedHeading];
        return known.FirstOrDefault(k => string.Equals(k, heading, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One line of a list section: a bullet is an item; a line straight after one (no blank between) joins it; any other text is kept as the
    /// section's <see cref="Prose"/>; a blank line ends a bullet.
    /// </summary>
    private static void AddListLine(AuditReport report, string heading, List<string> into, string line, ref bool continues)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            continues = false;
            return;
        }

        if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed is "-" or "*")
        {
            string item = trimmed.Length > 2 ? UnescapeLine(trimmed[2..].Trim()) : "";
            if (item.Length > 0)
            {
                into.Add(item);
                continues = true;
            }

            return;
        }

        if (continues && into.Count > 0)
        {
            into[^1] = into[^1] + " " + UnescapeLine(trimmed);
            return;
        }

        if (!report.Prose.TryGetValue(heading, out var prose))
        {
            report.Prose[heading] = prose = [];
        }

        prose.Add(line);
    }

    /// <summary>
    /// The finding block from <paramref name="start"/>: its line, then every line indented under it; a blank line ends it, except inside an
    /// evidence fence. Returns the index after it. A fence's lines are indented four (<see cref="FindingText"/>), so a line that is not ends
    /// a fence a hand edit left open (2026-10-19, the review: the open fence swallowed every later finding and section into one evidence).
    /// </summary>
    private static int ReadFindingBlock(List<string> lines, int start, out List<string> block)
    {
        block = [lines[start]];
        int at = start + 1;
        string? fence = null;
        while (at < lines.Count)
        {
            string line = lines[at];
            if (fence is not null && (line.Trim().Length == 0 || line.StartsWith("    ", StringComparison.Ordinal)))
            {
                block.Add(line);
                if (line.Trim() == fence)
                {
                    fence = null;
                }

                at++;
                continue;
            }

            fence = null;

            if (line.Trim().Length == 0 || !line.StartsWith("  ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("    ", StringComparison.Ordinal) && line.Trim().StartsWith("```", StringComparison.Ordinal) && block.Count > 0
                && block[^1].Trim().StartsWith(EvidencePart + ":", StringComparison.OrdinalIgnoreCase))
            {
                fence = line.Trim();
            }

            block.Add(line);
            at++;
        }

        return at;
    }

    /// <summary>A finding read from its block, or false when its line is not one <see cref="FindingText"/> would write.</summary>
    private static bool TryFinding(List<string> block, AuditSeverity severity, bool retracted, out AuditFinding finding)
    {
        finding = null!;
        var match = FindingLine().Match(block[0].TrimEnd());
        if (!match.Success)
        {
            return false;
        }

        if (!retracted && !match.Groups["box"].Success)
        {
            return false;
        }

        if (match.Groups["sev"].Success && AuditFinding.ParseSeverity(match.Groups["sev"].Value) is { } named)
        {
            severity = named;
        }

        string rest = match.Groups["rest"].Value;
        int sep = rest.IndexOf(" — ", StringComparison.Ordinal);
        const int width = 3;
        if (sep < 0)
        {
            sep = rest.IndexOf(" - ", StringComparison.Ordinal);
        }

        if (sep < 0)
        {
            return false;
        }

        string location = rest[..sep].Trim();
        string title = rest[(sep + width)..].Trim();
        int? line = null;
        int colon = location.LastIndexOf(':');
        if (colon > 0 && colon < location.Length - 1 && location[(colon + 1)..].All(char.IsAsciiDigit)
            && int.TryParse(location.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
        {
            line = n;
            location = location[..colon];
        }

        // The parts in their order, each its label and lines: a line at two spaces opening on a known label starts one, a line at four
        // continues the one before; Evidence reads its fence.
        var read = new List<(string Label, List<string> Lines)>();
        string? evidence = null;
        for (int i = 1; i < block.Count; i++)
        {
            string raw = block[i];
            if (!raw.StartsWith("    ", StringComparison.Ordinal) && PartStart(raw.Trim(), out string label, out string first))
            {
                if (label == EvidencePart)
                {
                    i = ReadEvidence(block, i + 1, out evidence);
                    continue;
                }

                read.Add((label, [UnescapeLine(first)]));
                continue;
            }

            string text = UnescapeLine(raw.StartsWith("    ", StringComparison.Ordinal) ? raw[4..] : raw.Trim());
            if (read.Count > 0 && read[^1].Label != PassPart)
            {
                read[^1].Lines.Add(text);
            }
            else if (text.Trim().Length > 0)
            {
                // A hand-written line with no text part to join (2026-10-19, the second review: one before the first part was dropped, one
                // under Pass broke its number and was lost at the next save): kept as a note of its own.
                read.Add((NotePart, [text]));
            }
        }

        var notes = read.Where(p => p.Label == NotePart).Select(p => string.Join('\n', p.Lines).Trim()).Where(n => n.Length > 0).ToList();
        string Part(string name) => string.Join('\n', read.Where(p => p.Label == name).SelectMany(p => p.Lines)).Trim();
        int pass = Number(Part(PassPart));
        bool settled = match.Groups["box"].Success && match.Groups["box"].Value is "x" or "X";
        string fix = Part(FixPart);
        string reason = Part(ReasonPart);
        finding = new AuditFinding(match.Groups["id"].Value.ToUpperInvariant(), severity, match.Groups["dim"].Value.Trim(), location, line, title, Part(DetailPart),
            fix.Length == 0 ? null : fix, evidence, pass, settled, retracted, retracted && reason.Length > 0 ? reason : null, notes);
        return true;
    }

    /// <summary>The evidence's fenced lines from <paramref name="start"/> (the fence's line), read back by the fence's length; returns the fence's closing index.</summary>
    private static int ReadEvidence(List<string> block, int start, out string? evidence)
    {
        evidence = null;
        if (start >= block.Count)
        {
            return start - 1;
        }

        string fence = block[start].Trim();
        if (!fence.StartsWith("```", StringComparison.Ordinal) || fence.Trim('`').Length != 0)
        {
            return start - 1;
        }

        var lines = new List<string>();
        int i = start + 1;
        for (; i < block.Count; i++)
        {
            if (block[i].Trim() == fence)
            {
                break;
            }

            string raw = block[i];
            if (raw.Trim().Length > 0 && !raw.StartsWith("    ", StringComparison.Ordinal))
            {
                // The fence left open (a hand edit): the evidence ends where its indent does, and this line is read as the part it is.
                evidence = string.Join('\n', lines).TrimEnd('\n');
                return i - 1;
            }

            lines.Add(raw.StartsWith("    ", StringComparison.Ordinal) ? raw[4..] : raw.TrimStart());
        }

        evidence = i < block.Count ? string.Join('\n', lines) : string.Join('\n', lines).TrimEnd('\n');
        return i;
    }

    private static bool PartStart(string trimmed, out string label, out string first)
    {
        foreach (string part in Parts)
        {
            if (trimmed.StartsWith(part + ":", StringComparison.OrdinalIgnoreCase))
            {
                label = part;
                first = trimmed[(part.Length + 1)..].Trim();
                return true;
            }
        }

        label = "";
        first = "";
        return false;
    }

    private static AuditHeader HeaderFrom(Dictionary<string, string> fields, out bool known)
    {
        var status = fields.TryGetValue("status", out var word) ? ParseStatus(word) : null;
        known = status is not null;
        // Read as written, thousands separators and all (2026-10-19, the second review: next-id 1,004 read back as 0, so a hand-deleted id came back).
        int nextId = fields.TryGetValue("next-id", out var next) ? Number(next) : 0;
        var focus = fields.TryGetValue("focus", out var f) && f.Length > 0 && !f.Equals(AuditFocus.AllWord, StringComparison.OrdinalIgnoreCase)
            ? AuditFocus.Normalize(f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : AuditFocus.Dimensions;
        return new AuditHeader(status ?? AuditStatus.Stopped, fields.GetValueOrDefault("passes") ?? "", fields.GetValueOrDefault("scope") ?? "", focus,
            Unquote(fields.GetValueOrDefault("brief") ?? ""), fields.GetValueOrDefault("reviewer") is { Length: > 0 } r ? r : "model", nextId,
            Time(fields.GetValueOrDefault("created")), Time(fields.GetValueOrDefault("updated")));
    }

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time) ? time : null;

    private static int Number(string text) =>
        int.TryParse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

    /// <summary>A text with its newlines made <c>\n</c>.</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>A text on one line: every run of whitespace one space, trimmed.</summary>
    public static string Flatten(string text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Quote(string text) =>
        "\"" + Flatten(text).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 1; i < text.Length - 1; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length - 1)
            {
                i++;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^- (?:\[(?<box>[ xX])\] )?\*\*(?<id>[Aa]\d+)\*\*(?: \((?<sev>[A-Za-z]+)\))? \[(?<dim>[^\]]+)\] (?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FindingLine();

    [GeneratedRegex(@"^\s*(?<read>\d[\d,]*) of (?<scope>\d[\d,]*) files read", RegexOptions.CultureInvariant)]
    private static partial Regex CoverageFigures();
}
