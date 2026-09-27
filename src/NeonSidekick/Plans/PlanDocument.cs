using System.Globalization;
using System.Text;

namespace NeonSidekick.Plans;

/// <summary>Where a plan stands, the <c>status:</c> line of its file.</summary>
public enum PlanStatus
{
    Draft,
    Approved,
    Cancelled,
}

/// <summary>A plan file's front matter: its status, revision, when it was written and rewritten, and the requirement it answers.</summary>
public sealed record PlanHeader(PlanStatus Status, int Revision, DateTimeOffset Created, DateTimeOffset Updated, string Requirement);

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
        PlanStatus.Cancelled => "cancelled",
        _ => "draft",
    };

    /// <summary>The whole file for <paramref name="header"/> over the model's <paramref name="markdown"/>.</summary>
    public static string Render(PlanHeader header, string title, string markdown)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(markdown);
        string body = Body(markdown);
        if (!body.StartsWith('#'))
        {
            body = "# " + title.Trim() + "\n\n" + body;
        }

        return HeaderText(header) + "\n" + body.TrimEnd() + "\n";
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
            .Append(Fence).Append('\n')
            .ToString();
    }

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
        return new PlanHeader(status, revision, created ?? DateTimeOffset.MinValue, updated ?? DateTimeOffset.MinValue, Unquote(fields.GetValueOrDefault("requirement") ?? ""));
    }

    /// <summary>
    /// <paramref name="text"/> with its status set and <c>updated</c> stamped, the body untouched; a file
    /// with no header the app wrote (edited by hand) gains one over the whole text.
    /// </summary>
    public static string WithStatus(string text, PlanStatus status, DateTimeOffset now, string requirement)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (TryParse(normalized) is { } header && Split(normalized, out _) is { } body)
        {
            return HeaderText(header with { Status = status, Updated = now }) + body;
        }

        return HeaderText(new PlanHeader(status, 1, now, now, requirement)) + "\n" + normalized.TrimStart('﻿');
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
        "cancelled" => PlanStatus.Cancelled,
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
