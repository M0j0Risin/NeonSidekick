using System.Text;
using NeonSidekick.Claude;
using NeonSidekick.Files;
using NeonSidekick.Llm;

namespace NeonSidekick.App;

// ── /usage sysprompt (2026-11-23, the user's ask) ─────────────────────────

internal sealed partial class ChatScreen
{
    // The made names a folder export tries (2026-11-24, review #49): they carry milliseconds, so a taken one is a race, not a path users meet.
    private const int ExportTries = 5;

    /// <summary>The index of <paramref name="text"/>'s first space outside double quotes, or -1 (2026-11-24, review #51): the toggle on <c>"</c> that <c>Words</c> reads by.</summary>
    internal static int FirstSpaceOutsideQuotes(string text)
    {
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
            }
            else if (text[i] == ' ' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// <c>/usage sysprompt [conversation] [to=&lt;path&gt;]</c> (2026-11-23, the user's ask): the next request — the system message and every
    /// tool offered, and with <c>conversation</c> the history after the picture budget, with the thinking the wire sends back — written word for word to <see cref="UsageText.ExportFileName"/>
    /// in the working folder, or at <c>to=</c> (a file or a folder, as <c>/pdf</c>'s), never over a file. Every path goes through the sandbox's
    /// <see cref="WorkingDirectory.Resolve(string, bool, out string)"/> for a write, so its guards hold. At the idle line only
    /// (<see cref="MidTurnClass.Deferred"/>): under a reply the history is the turn's own list.
    /// </summary>
    private void ExportSysprompt(string args)
    {
        var (_, conversation, to, error) = UsageText.ParseSysprompt(args);
        if (error is not null)
        {
            _transcript.Error(error);
            return;
        }

        var effective = _effective();
        var url = _session.Endpoint?.BaseUrl;
        if (ClaudeCliEndpoint.IsClaudeCli(url))
        {
            _transcript.Error(UsageText.ExportClaudeCode);
            return;
        }

        // A blank to=" " names nothing (2026-11-24, review #55).
        if (to is not null && to.Trim().Length == 0)
        {
            _transcript.Error(UsageText.SyspromptUsage);
            return;
        }

        bool IsFolder(string target) => _files.Resolve(target, forWrite: false, out string full) == FileOutcome.Ok && Directory.Exists(full);
        if (OutputNames.Refusal(to, UsageText.ExportExtension, IsFolder) is { } refusal)
        {
            _transcript.Error(refusal);
            return;
        }

        var disabled = TurnDisabled(effective);
        string prompt = SystemPromptSummary.SystemPrompt(SystemPromptFacts(effective, disabled));
        var tools = ToolGroups(effective, disabled).SelectMany(g => g.Tools).ToList();
        // The conversation only when asked for (2026-11-23, the user's ask: a bare /usage sysprompt is the system message and the tools).
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>? messages = null;
        int thinkingFrom = 0;
        long historyTokens = 0;
        // Which reason leaves earlier thinking out (2026-11-24, the last-7 review's #22): the setting off, or a server taking back only the turn in flight's.
        bool inFlightOnly = effective.LlmPreserveThinking && Assistant.InFlightOnlyWire(url);
        if (conversation)
        {
            var budget = _session.Assistant?.PictureBudget ?? PictureBudget.Default;
            messages = budget.Apply(_session.History.Messages).Messages;
            thinkingFrom = effective.LlmPreserveThinking && !Assistant.InFlightOnlyWire(url) ? 0 : messages.Count;
            historyTokens = ConversationCompactor.Estimate(messages, thinkingFrom).Total;
        }

        var now = _time.GetLocalNow();
        var facts = new UsageExportFacts(
            now,
            _session.ShownModelId,
            _settings.ProfileName,
            _session.ContextLength?.Tokens,
            prompt,
            prompt.Length / ConversationCompactor.CharsPerToken,
            tools,
            tools.Sum(ToolTokens),
            messages,
            thinkingFrom,
            historyTokens,
            _files.Root,
            inFlightOnly,
            // The polls the turn seeds for notified exits, only while process is offered (2026-11-24, review #58), peeked, never taken.
            tools.Any(t => t.Name == Llm.Tools.ProcessTool.ToolName) ? _processes.PeekNotes().Count : 0);
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(UsageExport.Markdown(facts));

        bool intoFolder = OutputNames.IntoFolder(to, IsFolder);
        string folder = intoFolder ? (to ?? "").Trim().TrimEnd('/', '\\') : "";
        if (intoFolder && to is not null && folder.Length == 0)
        {
            // Only separators (2026-11-24, review #55): to=/ named the root, and trimmed to "here". The root itself is judged for a write, "/" and
            // never the text as typed (on a Mac a backslash is a name's character).
            var rooted = _files.Resolve("/", forWrite: true, out string root);
            if (rooted != FileOutcome.Ok)
            {
                _transcript.Error(FileText.Error(rooted, to.Trim(), "write"));
                return;
            }

            folder = root;
        }

        for (int n = 1; ; n++)
        {
            string name = UsageText.ExportFileName(now, n);
            string target = !intoFolder ? OutputNames.WithExtension(to!.Trim(), UsageText.ExportExtension)
                : folder.Length == 0 || folder == "." ? name
                : Path.EndsInDirectorySeparator(folder) ? folder + name
                : folder + "/" + name;
            var outcome = _files.Resolve(target, forWrite: true, out string full);
            if (outcome != FileOutcome.Ok)
            {
                _transcript.Error(FileText.Error(outcome, target, "write"));
                return;
            }

            string shown = _files.Relative(full);
            try
            {
                // A temp sibling moved in, never over anything (2026-11-24, review #49): no partial file is left, and false is only a name taken,
                // by a file or a folder.
                if (WorkingDirectory.WriteNew(full, bytes))
                {
                    _transcript.Notice(UsageText.Exported(shown, tools.Count, messages?.Count));
                    return;
                }

                if (!intoFolder || n >= ExportTries)
                {
                    _transcript.Error(UsageText.ExportExists(shown));
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _transcript.Error(UsageText.ExportFailed(shown, ex.Message));
                return;
            }
        }
    }
}
