using System.Text;
using NeonSidekick.Claude;
using NeonSidekick.Files;
using NeonSidekick.Llm;

namespace NeonSidekick.App;

// ── /usage sysprompt (2026-11-23, the user's ask) ─────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/usage sysprompt [to=&lt;path&gt;]</c> (2026-11-23, the user's ask): the next request — the system message, every tool offered and the
    /// history after the picture budget, with the thinking the wire sends back — written word for word to <see cref="UsageText.ExportFileName"/>
    /// in the working folder, or at <c>to=</c> (a file or a folder, as <c>/pdf</c>'s), never over a file. Every path goes through the sandbox's
    /// <see cref="WorkingDirectory.Resolve(string, bool, out string)"/> for a write, so its guards hold. At the idle line only
    /// (<see cref="MidTurnClass.Deferred"/>): under a reply the history is the turn's own list.
    /// </summary>
    private void ExportSysprompt(string args)
    {
        var (_, to, error) = UsageText.ParseSysprompt(args);
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

        bool IsFolder(string target) => _files.Resolve(target, forWrite: false, out string full) == FileOutcome.Ok && Directory.Exists(full);
        if (OutputNames.Refusal(to, UsageText.ExportExtension, IsFolder) is { } refusal)
        {
            _transcript.Error(refusal);
            return;
        }

        var disabled = TurnDisabled(effective);
        string prompt = SystemPromptSummary.SystemPrompt(SystemPromptFacts(effective, disabled));
        var tools = ToolGroups(effective, disabled).SelectMany(g => g.Tools).ToList();
        var budget = _session.Assistant?.PictureBudget ?? PictureBudget.Default;
        var messages = budget.Apply(_session.History.Messages).Messages;
        int thinkingFrom = effective.LlmPreserveThinking && !Assistant.InFlightOnlyWire(url) ? 0 : messages.Count;
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
            ConversationCompactor.Estimate(messages, thinkingFrom).Total);
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(UsageExport.Markdown(facts));

        bool intoFolder = OutputNames.IntoFolder(to, IsFolder);
        string folder = intoFolder ? (to ?? "").Trim().TrimEnd('/', '\\') : "";
        for (int n = 1; ; n++)
        {
            string name = UsageText.ExportFileName(now, n);
            string target = !intoFolder ? OutputNames.WithExtension(to!.Trim(), UsageText.ExportExtension)
                : folder.Length == 0 || folder == "." ? name
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
                if (Path.GetDirectoryName(full) is { Length: > 0 } parent)
                {
                    Directory.CreateDirectory(parent);
                }

                using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                }

                _transcript.Notice(UsageText.Exported(shown, tools.Count, messages.Count));
                return;
            }
            catch (IOException) when (File.Exists(full))
            {
                if (!intoFolder)
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
