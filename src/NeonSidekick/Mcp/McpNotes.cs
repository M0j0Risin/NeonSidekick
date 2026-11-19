using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Mcp;

/// <summary>One connected server's notes (2026-11-19, feature idea #59): its name and its own text, already cut at <see cref="McpText.NotesPerServer"/>.</summary>
public sealed record McpServerNote(string Server, string Text);

/// <summary>
/// The servers' own instructions on the way to the prompt (2026-11-19, feature idea #59). A server's instructions are its own text and a
/// known prompt-injection route, so they're fenced in the prompt, capped per server and in total, and framed as hints that never override
/// the rules. <see cref="For"/> is the one filter both the turn and <c>/usage</c> call, so they can't disagree.
/// </summary>
public static class McpNotes
{
    private static readonly HashSet<string> s_logged = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="text"/> trimmed and cut at <see cref="McpText.NotesPerServer"/> with <c>…</c>, or null for none. A cut writes its Info
    /// line once per server and text, as <c>PromptFile</c> warns once.
    /// </summary>
    public static string? Cut(string server, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        if (trimmed.Length <= McpText.NotesPerServer)
        {
            return trimmed;
        }

        LogOnce("cut\0" + server + "\0" + trimmed, McpText.InstructionsCutLogLine(server, trimmed.Length));
        return Files.TextCut.Safe(trimmed, McpText.NotesPerServer - 1) + "…";
    }

    /// <summary>
    /// The notes a turn carries: those of the servers with a tool in <paramref name="offered"/> (the turn's final MCP list), in order, while
    /// their total stays within <see cref="McpText.NotesTotal"/>. A server whose notes would pass it is left out whole, never cut mid-way, with
    /// its Info line once. Empty when nothing qualifies.
    /// </summary>
    public static IReadOnlyList<McpServerNote> For(IReadOnlyList<McpServerNote>? notes, IEnumerable<AIFunction>? offered)
    {
        if (notes is not { Count: > 0 } || offered is null)
        {
            return [];
        }

        var servers = offered.OfType<McpTool>().Select(t => t.ServerName).ToHashSet(StringComparer.Ordinal);
        var kept = new List<McpServerNote>();
        int total = 0;
        foreach (var note in notes)
        {
            if (!servers.Contains(note.Server))
            {
                continue;
            }

            if (total + note.Text.Length > McpText.NotesTotal)
            {
                LogOnce("total\0" + note.Server + "\0" + note.Text, McpText.InstructionsLeftOutLogLine(note.Server));
                continue;
            }

            total += note.Text.Length;
            kept.Add(note);
        }

        return kept;
    }

    private static void LogOnce(string key, string line)
    {
        lock (s_logged)
        {
            if (!s_logged.Add(key))
            {
                return;
            }
        }

        DiagnosticLog.Info(McpConfigFile.Category, line);
    }
}
