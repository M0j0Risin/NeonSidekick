using Microsoft.Extensions.AI;

namespace NeonSidekick.Mcp;

/// <summary>
/// One connected server's notes (2026-11-19, feature idea #59): its name and its own text, already scrubbed and cut at
/// <see cref="McpText.NotesPerServer"/> (<see cref="McpNotes.Cut"/>).
/// </summary>
public sealed record McpServerNote(string Server, string Text);

/// <summary>
/// A server's notes as <see cref="McpNotes.Cut"/> left them: the text to send; <see cref="Shown"/> the characters the footer counts (the
/// text's length, or of a cut one the raw characters kept); <see cref="Raw"/> the raw text's length when it was cut, else null.
/// </summary>
public readonly record struct McpCutNotes(string Text, int Shown, int? Raw);

/// <summary>
/// The servers' own instructions on the way to the prompt (2026-11-19, feature idea #59). A server's instructions are its own text and a
/// known prompt-injection route, so they're fenced in the prompt, capped per server and in total, and framed as hints that never override
/// the rules. <see cref="For"/> is the one filter both the turn and <c>/usage</c> call, so they can't disagree. Pure since 2026-11-20 (the
/// mcp-ask review's #7): the once-per-text log lines belong to each <c>McpSession</c>, not to the process, so a second session (a profile
/// switch, a test) says them again.
/// </summary>
public static class McpNotes
{
    /// <summary>
    /// <paramref name="text"/> trimmed, scrubbed (<see cref="Llm.Tools.UntrustedText.ScrubPrefix"/>) and cut at
    /// <see cref="McpText.NotesPerServer"/> with <c>…</c>, or null for none. The fit is judged after scrubbing (2026-11-20, the mcp-ask
    /// review's #28 and #59): an escape is six or ten characters for one, so a note the raw length passed could run past the cap. A cut
    /// calls <paramref name="logOnce"/> with a key for the server and text and the Info line, which says how many of the text's own
    /// characters were kept.
    /// </summary>
    public static McpCutNotes? Cut(string server, string? text, Action<string, string>? logOnce = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        var whole = Llm.Tools.UntrustedText.ScrubPrefix(trimmed, 0, McpText.NotesPerServer);
        if (whole.Consumed == trimmed.Length)
        {
            return new McpCutNotes(whole.Scrubbed, whole.Scrubbed.Length, null);
        }

        var part = Llm.Tools.UntrustedText.ScrubPrefix(trimmed, 0, McpText.NotesPerServer - 1);
        logOnce?.Invoke("cut\0" + server + "\0" + trimmed, McpText.InstructionsCutLogLine(server, trimmed.Length, part.Consumed));
        return new McpCutNotes(part.Scrubbed + "…", part.Consumed, trimmed.Length);
    }

    /// <summary>
    /// The notes a turn carries: those of the servers with a tool in <paramref name="offered"/> (the turn's final MCP list), in order, while
    /// their lines (<see cref="McpText.NoteLine"/> and its line break, the one measure, 2026-11-20, #28) stay within
    /// <see cref="McpText.NotesTotal"/>. A server whose notes would pass it is left out whole, never cut mid-way. Empty when nothing
    /// qualifies. Pure.
    /// </summary>
    public static IReadOnlyList<McpServerNote> For(IReadOnlyList<McpServerNote>? notes, IEnumerable<AIFunction>? offered) =>
        Split(notes, offered).Kept;

    /// <summary>The servers with a tool in <paramref name="offered"/> whose notes <see cref="For"/> leaves out under the total (#43's mark).</summary>
    public static IReadOnlyList<string> LeftOut(IReadOnlyList<McpServerNote>? notes, IEnumerable<AIFunction>? offered) =>
        Split(notes, offered).LeftOut;

    private static (IReadOnlyList<McpServerNote> Kept, IReadOnlyList<string> LeftOut) Split(IReadOnlyList<McpServerNote>? notes, IEnumerable<AIFunction>? offered)
    {
        if (notes is not { Count: > 0 } || offered is null)
        {
            return ([], []);
        }

        var servers = offered.OfType<McpTool>().Select(t => t.ServerName).ToHashSet(StringComparer.Ordinal);
        var kept = new List<McpServerNote>();
        var leftOut = new List<string>();
        int total = 0;
        foreach (var note in notes)
        {
            if (!servers.Contains(note.Server))
            {
                continue;
            }

            int length = McpText.NoteLine(note).Length + 1;
            if (total + length > McpText.NotesTotal)
            {
                leftOut.Add(note.Server);
                continue;
            }

            total += length;
            kept.Add(note);
        }

        return (kept, leftOut);
    }
}
