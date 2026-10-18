using System.Globalization;
using System.Text;

namespace NeonSidekick.Plans;

/// <summary>Where a plan stands, the <c>status:</c> line of its file.</summary>
public enum PlanStatus
{
    Draft,
    Approved,
    Cancelled,

    /// <summary>Carried out with every step ticked (2026-09-26, round two).</summary>
    Done,

    /// <summary>Carried out with steps still unticked: <c>/plan open</c> picks it up.</summary>
    Incomplete,
}

/// <summary>
/// A plan file's front matter: its status, revision, when it was written and rewritten, the requirement it answers,
/// and — once it has been carried out (2026-09-26) — how many of its steps are ticked (<c>progress: 5/7</c>).
/// </summary>
/// <para><c>review:</c> (2026-10-17, <see cref="Review"/>): how the last gap review that ended on its own went, with the body's fingerprint
/// (<see cref="PlanReview"/>); written only when set, so a file without it reads as before, and dropped by the next revision.</para>
public sealed record PlanHeader(PlanStatus Status, int Revision, DateTimeOffset Created, DateTimeOffset Updated, string Requirement, int? StepsDone = null, int? StepsTotal = null, string? Review = null);

/// <summary>
/// A plan file (2026-09-26): a small front-matter block the app owns — <c>status</c>, <c>revision</c>,
/// <c>created</c>, <c>updated</c>, <c>requirement</c> — then the model's Markdown as it sent it, less any
/// front matter of its own, and headed with the plan's title when it did not start with a heading.
/// Approval and cancellation rewrite the block alone (<see cref="WithStatus"/>), so the body the user
/// approved is the body on disk. Times are ISO 8601 with their offset, invariant. Pure.
/// </summary>
public static class PlanDocument
{
    private const string Fence = "---";
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:sszzz";

    /// <summary>The status word written in the file.</summary>
    public static string Word(PlanStatus status) => status switch
    {
        PlanStatus.Approved => "approved",
        PlanStatus.Cancelled => "canceled",
        PlanStatus.Done => "done",
        PlanStatus.Incomplete => "incomplete",
        _ => "draft",
    };

    /// <summary>The whole file for <paramref name="header"/> over the model's <paramref name="markdown"/>.</summary>
    public static string Render(PlanHeader header, string title, string markdown)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(markdown);
        return HeaderText(header) + "\n" + SavedBody(title, markdown) + "\n";
    }

    /// <summary>
    /// The body <see cref="Render"/> writes for <paramref name="markdown"/>: a heading from <paramref name="title"/> when it has none.
    /// What <see cref="Body"/> reads back from the file, so the gap-closing loop (2026-10-13) can tell an unchanged plan from a revision.
    /// </summary>
    public static string SavedBody(string title, string markdown)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(markdown);
        string body = Body(markdown);
        if (!body.StartsWith('#'))
        {
            body = "# " + title.Trim() + "\n\n" + body;
        }

        return body.TrimEnd();
    }

    /// <summary>The front-matter block alone, fences included, ending in a newline.</summary>
    public static string HeaderText(PlanHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return new StringBuilder()
            .Append(Fence).Append('\n')
            .Append("status: ").Append(Word(header.Status)).Append('\n')
            .Append("revision: ").Append(header.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("created: ").Append(header.Created.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('\n')
            .Append("updated: ").Append(header.Updated.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('\n')
            .Append("requirement: ").Append(Quote(header.Requirement)).Append('\n')
            .Append(header is { StepsDone: { } done, StepsTotal: { } total } ? "progress: " + N(done) + "/" + N(total) + "\n" : "")
            .Append(header.Review is { Length: > 0 } review ? "review: " + review + "\n" : "")
            .Append(Fence).Append('\n')
            .ToString();
    }

    /// <summary>
    /// How many of <paramref name="markdown"/>'s task lines are ticked (2026-09-26): a list item opening with <c>[ ]</c>
    /// or <c>[x]</c> / <c>[X]</c> — after <c>-</c>, <c>*</c>, <c>+</c> or a number with <c>.</c> or <c>)</c> — outside
    /// fenced code blocks. Pure.
    /// </summary>
    public static (int Done, int Total) Progress(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        int done = 0;
        int total = 0;
        foreach (var box in TaskBoxes(markdown))
        {
            total++;
            done += box ? 1 : 0;
        }

        return (done, total);
    }

    /// <summary>
    /// Whether a reply reads as a plan (2026-09-26: the hint that <c>/plan save</c> can keep one the model never
    /// presented): two task lines or more, or a Markdown heading and three list items or more. Pure.
    /// </summary>
    public static bool LooksLikePlan(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (Progress(reply).Total >= 2)
        {
            return true;
        }

        bool heading = false;
        int items = 0;
        foreach (var line in Lines(reply))
        {
            string trimmed = line.TrimStart();
            heading |= trimmed.StartsWith('#');
            items += ListMarker(trimmed) > 0 ? 1 : 0;
        }

        return heading && items >= 3;
    }

    /// <summary>The first Markdown heading's text in <paramref name="markdown"/>, or null.</summary>
    public static string? FirstHeading(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        foreach (var line in Lines(markdown))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') && trimmed.TrimStart('#').Trim() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>Each task line's box outside code fences, true when ticked.</summary>
    private static IEnumerable<bool> TaskBoxes(string markdown)
    {
        foreach (var line in Lines(markdown))
        {
            string trimmed = line.TrimStart();
            int marker = ListMarker(trimmed);
            if (marker == 0)
            {
                continue;
            }

            string rest = trimmed[marker..];
            if (rest.Length >= 3 && rest[0] == '[' && rest[2] == ']' && (rest.Length == 3 || char.IsWhiteSpace(rest[3])))
            {
                if (rest[1] == ' ')
                {
                    yield return false;
                }
                else if (rest[1] is 'x' or 'X')
                {
                    yield return true;
                }
            }
        }
    }

    /// <summary>The lines of <paramref name="markdown"/> outside fenced code blocks (<c>```</c> or <c>~~~</c>).</summary>
    private static IEnumerable<string> Lines(string markdown)
    {
        bool fenced = false;
        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (!fenced)
            {
                yield return line;
            }
        }
    }

    /// <summary>The length of a list marker with its space (<c>- </c>, <c>12. </c>) at the start of <paramref name="trimmed"/>, or 0.</summary>
    private static int ListMarker(string trimmed)
    {
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            return 2;
        }

        int digits = 0;
        while (digits < trimmed.Length && digits < 9 && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        return digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ' ? digits + 2 : 0;
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary><paramref name="markdown"/> with its newlines made <c>\n</c> and any leading front matter cut.</summary>
    public static string Body(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimStart('﻿').Trim();
        return Split(text, out _) is { } rest ? rest.Trim() : text;
    }

    /// <summary>The header of <paramref name="text"/>, or null when it has none the app wrote.</summary>
    public static PlanHeader? TryParse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Split(text.Replace("\r\n", "\n", StringComparison.Ordinal), out var block) is null)
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in block)
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        if (!fields.TryGetValue("status", out var word) || ParseStatus(word) is not { } status)
        {
            return null;
        }

        int revision = fields.TryGetValue("revision", out var r) && int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1;
        var created = Time(fields.GetValueOrDefault("created"));
        var updated = Time(fields.GetValueOrDefault("updated")) ?? created;
        int? done = null;
        int? total = null;
        if (fields.TryGetValue("progress", out var progress) && progress.Split('/') is [var d, var t]
            && int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dn) && int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tn))
        {
            (done, total) = (dn, tn);
        }

        string? review = fields.TryGetValue("review", out var reviewed) && reviewed.Length > 0 ? reviewed : null;
        return new PlanHeader(status, revision, created ?? DateTimeOffset.MinValue, updated ?? DateTimeOffset.MinValue, Unquote(fields.GetValueOrDefault("requirement") ?? ""), done, total, review);
    }

    /// <summary>
    /// <paramref name="text"/> with its status set and <c>updated</c> stamped, the body untouched; a file
    /// with no header the app wrote (edited by hand) gains one over the whole text. <paramref name="progress"/>,
    /// when given, is written as the <c>progress:</c> line (2026-09-26); otherwise the file's own is kept.
    /// </summary>
    public static string WithStatus(string text, PlanStatus status, DateTimeOffset now, string requirement, (int Done, int Total)? progress = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (TryParse(normalized) is { } header && Split(normalized, out _) is { } body)
        {
            var next = header with { Status = status, Updated = now };
            if (progress is { } p)
            {
                next = next with { StepsDone = p.Done, StepsTotal = p.Total };
            }

            return HeaderText(next) + body;
        }

        return HeaderText(new PlanHeader(status, 1, now, now, requirement, progress?.Done, progress?.Total)) + "\n" + normalized.TrimStart('﻿');
    }

    /// <summary>
    /// <paramref name="text"/> with its <c>review:</c> line set to <paramref name="review"/> (2026-10-17, <see cref="WithStatus"/>'s twin) and
    /// <c>updated</c> stamped, the body and the status untouched; a file with no header the app wrote gains a draft one over the whole text.
    /// </summary>
    public static string WithReview(string text, string review, DateTimeOffset now, string requirement)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(review);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (TryParse(normalized) is { } header && Split(normalized, out _) is { } body)
        {
            return HeaderText(header with { Review = review, Updated = now }) + body;
        }

        return HeaderText(new PlanHeader(PlanStatus.Draft, 1, now, now, requirement, Review: review)) + "\n" + normalized.TrimStart('﻿');
    }

    /// <summary>
    /// The body's fingerprint for the <c>review:</c> line (2026-10-17): the first 8 hex of SHA-256 over <paramref name="body"/>,
    /// LF-normalized and trimmed, with every task line's <c>[x]</c> / <c>[X]</c> read as <c>[ ]</c> (task lines as <see cref="Progress"/>
    /// knows them, outside code fences), so ticking steps while the plan is carried out never reads as an edit. Pure, invariant.
    /// </summary>
    public static string Fingerprint(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string[] lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim().Split('\n');
        bool fenced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            int marker = fenced ? 0 : ListMarker(trimmed);
            if (marker == 0)
            {
                continue;
            }

            string rest = trimmed[marker..];
            if (rest.Length >= 3 && rest[0] == '[' && rest[1] is 'x' or 'X' && rest[2] == ']' && (rest.Length == 3 || char.IsWhiteSpace(rest[3])))
            {
                int box = lines[i].Length - trimmed.Length + marker + 1;
                lines[i] = string.Concat(lines[i].AsSpan(0, box), " ", lines[i].AsSpan(box + 1));
            }
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    /// <summary>The text after the leading front matter (its lines in <paramref name="block"/>), or null when it does not open with one.</summary>
    private static string? Split(string text, out List<string> block)
    {
        block = [];
        if (!text.StartsWith(Fence + "\n", StringComparison.Ordinal))
        {
            return null;
        }

        int start = Fence.Length + 1;
        int at = start;
        while (at <= text.Length)
        {
            int end = text.IndexOf('\n', at);
            string line = end < 0 ? text[at..] : text[at..end];
            if (line.TrimEnd() == Fence)
            {
                return end < 0 ? "" : text[(end + 1)..];
            }

            block.Add(line);
            if (end < 0)
            {
                break;
            }

            at = end + 1;
        }

        block = [];
        return null;
    }

    private static PlanStatus? ParseStatus(string word) => word.Trim().ToLowerInvariant() switch
    {
        "draft" => PlanStatus.Draft,
        "approved" => PlanStatus.Approved,
        "canceled" or "cancelled" => PlanStatus.Cancelled, // written cancelled until 2026-10-14 (American since, the user's ask); old files still read
        "done" => PlanStatus.Done,
        "incomplete" => PlanStatus.Incomplete,
        _ => null,
    };

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    /// <summary>A YAML double-quoted scalar on one line.</summary>
    private static string Quote(string text)
    {
        string flat = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return "\"" + flat.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

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
}
