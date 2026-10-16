using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Advisors;

/// <summary>
/// The <c>Advisor context</c> setting's words (2026-09-27 for the Claude Code advisor tool, the user's call: a setting, not a fixed
/// choice; the profile advisors' since 2026-10-10, and theirs alone once that tool was retired the same day): what an advisor call
/// sends besides the model's question. <c>brief</c> — the question and the model's own <c>context</c> argument, nothing else;
/// <c>recent</c> — those and the last <see cref="AdvisorText.RecentMessages"/> messages of the conversation. Pure; an unknown saved
/// word reads as <see cref="Default"/> with a warning. The model's calls only since later on 2026-10-10 (the user's call): <c>/advisor</c>
/// has no model to write a brief, so it always sends <see cref="AdvisorRecent.Lines"/>.
/// </summary>
public static class AdvisorContextMode
{
    public const string Brief = "brief";
    public const string Recent = "recent";

    /// <summary>A fresh profile's: the model writes the brief.</summary>
    public const string Default = Brief;

    /// <summary>The picker's rows, in order.</summary>
    public static readonly string[] Names = { Brief, Recent };

    /// <summary>What each word does, for the picker's hint. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        Brief => "the model's question and its own brief",
        Recent => $"those and the last {AdvisorText.RecentMessages} messages",
        _ => "",
    };

    /// <summary>Whether <paramref name="saved"/> asks for the recent messages; anything but the two words warns and reads as <see cref="Brief"/>.</summary>
    public static bool IsRecent(string? saved)
    {
        string word = saved?.Trim().ToLowerInvariant() ?? "";
        if (word == Recent)
        {
            return true;
        }

        if (word != Brief)
        {
            DiagnosticLog.Warn(AdvisorText.Category, $"AdvisorContext='{saved}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return false;
    }
}

/// <summary>The conversation's last messages as an advisor reads them (<c>Advisor context: recent</c>; the Claude Code advisor tool's until 2026-10-10).</summary>
public static class AdvisorRecent
{
    /// <summary>
    /// The conversation's last <see cref="AdvisorText.RecentMessages"/> messages as the advisor reads them: one line per
    /// text, call and result — the role and the words, a call as its tool's name, a result cut to
    /// <see cref="AdvisorText.ToolResultChars"/>. Pictures and empty parts are left out. Pure.
    /// </summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        // The event-log tools' calls by id (2026-10-15, the event-log plan): their results are withheld from an advisor, a stand-in naming the
        // call in their place, so a full computer's events never pass to another profile through the recent lines. Every withheld family's
        // since later that day (the admin-tools plan, Withheld).
        var withheldCalls = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Where(c => StandIn(c) is not null)
            .GroupBy(c => c.CallId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var message in messages.Skip(Math.Max(0, messages.Count - AdvisorText.RecentMessages)))
        {
            if (message.Role == ChatRole.System)
            {
                continue;
            }

            string role = message.Role == ChatRole.User ? "user" : message.Role == ChatRole.Assistant ? "assistant" : "tool";
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent { Text: { Length: > 0 } text } when !string.IsNullOrWhiteSpace(text):
                        lines.Add(AdvisorText.RecentLine(role, text));
                        break;
                    case FunctionCallContent call:
                        lines.Add(AdvisorText.RecentLine("assistant", "(called " + call.Name + ")"));
                        break;
                    case FunctionResultContent withheld when withheldCalls.TryGetValue(withheld.CallId, out var call):
                        lines.Add(AdvisorText.RecentLine("tool", StandIn(call)!));
                        break;
                    case FunctionResultContent result:
                        string body = result.Result?.ToString() ?? "";
                        body = body.Length > AdvisorText.ToolResultChars ? body[..AdvisorText.ToolResultChars] + "…" : body;
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            lines.Add(AdvisorText.RecentLine("tool", body));
                        }

                        break;
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// The families whose results an advisor never reads (2026-10-15, the admin-tools plan: the event logs' one check made a list): each
    /// family's tool names with the stand-in line for one of its calls — the call, never its text. Read in order; the first family holding
    /// the name answers.
    /// </summary>
    private static IEnumerable<(IReadOnlySet<string> Names, Func<FunctionCallContent, string> Line)> Withheld()
    {
        yield return (App.ChatScreen.EventLogToolNames, call => EventLog.EventLogText.WithheldFromAdvisor(call.Name, Argument(call, Llm.Tools.EventLogTool.LogArgument), Argument(call, Llm.Tools.EventLogEventTool.RecordArgument)));
        // The admin families (2026-10-15, the admin-tools plan's item 10): services, tasks and the directory, and net_certs alone of the network
        // tools — the user gave the certificate stores their own switch for privacy; the other network results are not withheld.
        yield return (App.ChatScreen.ServiceToolNames, call => Services.ServiceText.WithheldFromAdvisor(call.Name, Argument(call, Llm.Tools.ServiceTool.NameArgument)));
        yield return (App.ChatScreen.TaskToolNames, call => Tasks.TaskText.WithheldFromAdvisor(call.Name, Argument(call, Llm.Tools.TaskTool.PathArgument)));
        yield return (App.ChatScreen.DirectoryToolNames, call => ActiveDirectory.DirectoryText.WithheldFromAdvisor(call.Name, Argument(call, Llm.Tools.DirectoryTool.NameArgument)));
        yield return (CertificateToolNames, call => "(" + call.Name + ": read it with your own network tools)");
    }

    private static readonly IReadOnlySet<string> CertificateToolNames = new HashSet<string>(StringComparer.Ordinal) { Llm.Tools.NetCertsTool.ToolName };

    /// <summary>The stand-in line for <paramref name="call"/> when its family is withheld; null when the advisor may read its result.</summary>
    public static string? StandIn(FunctionCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);
        foreach (var (names, line) in Withheld())
        {
            if (names.Contains(call.Name))
            {
                return line(call);
            }
        }

        return null;
    }

    /// <summary>A call's argument as text, or null.</summary>
    internal static string? Argument(FunctionCallContent call, string name) =>
        call.Arguments is { } arguments && arguments.TryGetValue(name, out object? value) && value is not null ? value.ToString() : null;
}
