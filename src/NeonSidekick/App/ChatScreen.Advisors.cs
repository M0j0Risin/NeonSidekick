using Microsoft.Extensions.AI;
using NeonSidekick.Advisors;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── The profile advisors: neon_advisor (2026-10-10) ──────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The advisors' name colour over their answers: a steady teal, whatever the theme.</summary>
    internal static readonly Spectre.Console.Color AdvisorColor = new(0x4F, 0xB8, 0xA8);

    /// <summary>Each advisor's thread in this session (2026-10-10): stored with the session, read back by a restore, dropped by <see cref="ForgetSession"/>.</summary>
    private readonly AdvisorThreads _advisorThreads = new();

    /// <summary>The advisors' profiles and links (2026-10-10), the links dropped by <see cref="ForgetSession"/> and with the screen.</summary>
    private readonly AdvisorHost _advisorHost;

    /// <summary><c>neon_advisor</c> (2026-10-10), built once; <c>/advisor</c> asks through it too.</summary>
    private readonly NeonAdvisorTool _neonAdvisor;

    /// <summary>The advisors' group (one tool), offered while <c>Advisors enabled</c> is on and an advisor is listed.</summary>
    private readonly IReadOnlyList<AIFunction> _neonAdvisorTools;

    /// <summary>
    /// The advisors' group (2026-10-10): <see cref="NeonAdvisorTool"/> over the host, the settings, the loaded profile's name, the threads,
    /// the tools a turn would offer now with their rules, the conversation, the confirm seam, the view and the tally's advisors bucket
    /// (<see cref="TokenTally.AddAdvisors"/>, later on 2026-10-10). Shared with headless.
    /// </summary>
    public static NeonAdvisorTool AdvisorTool(IAdvisorHost host, Func<AppSettingsData> effective, Func<string> master, AdvisorThreads threads, Func<(IReadOnlyList<AIFunction> Tools, string Rules)> offered, Func<IReadOnlyList<ChatMessage>> history, Func<AdvisorEntry, string, CancellationToken, Task<bool?>>? confirm, IAdvisorView? view, Action<TokenUsage, int>? spent) =>
        new(host, effective, master, threads, offered, history, confirm, view, spent);

    /// <summary>Whether a turn offers <c>neon_advisor</c>: <c>Advisors enabled</c> on and an advisor listed (2026-10-10). Pure.</summary>
    public static bool AdvisorsOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.Advisors && AdvisorsListed(effective);
    }

    /// <summary>
    /// Whether <c>Advisor list</c> holds an advisor (2026-10-10): the Advisors group shows on <c>/tools</c>, <c>/sys</c> and the Botchat
    /// checklist only then — with none there is nothing it could ask, and its switch lives on <c>/settings › Advisors</c> beside the list. Pure.
    /// </summary>
    public static bool AdvisorsListed(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return AdvisorRoster.Entries(effective.AdvisorList).Count > 0;
    }

    /// <summary>
    /// The tools an advisor may call (2026-10-10): this chat's turn tools as plan mode narrows them, less the question pane, the
    /// camera, the screen and the advisors themselves, and their rules' text. The tool narrows again (<see cref="Llm.Tools.NeonAdvisorTool.ReadOnlyOf"/>).
    /// </summary>
    public static (IReadOnlyList<AIFunction> Tools, string Rules) AdvisorTurnTools(TurnToolInputs inputs, bool markdown)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var set = ComposeTurnTools(inputs with { PlanReadOnly = true, Plan = null, Ask = null, Camera = null, Screen = null, NeonAdvisor = null });
        return set.Tools.Count == 0 ? ([], "") : (set.Tools, set.Rules.DefaultRules(markdown));
    }

    private (IReadOnlyList<AIFunction> Tools, string Rules) AdvisorOffered()
    {
        var effective = _effective();
        return AdvisorTurnTools(TurnInputs(effective), effective.TranscriptMarkdown);
    }

    /// <summary>
    /// <c>Advisor confirm</c>'s question, on the turn task: the yes/no pane through the watcher (<see cref="ApproveCommandAsync"/>'s shape; the Claude Code advisor tool's confirm, retired later that day, had it too).
    /// Null (never asked) without the pane or a watcher to run it.
    /// </summary>
    private async Task<bool?> ConfirmNeonAdvisorAsync(AdvisorEntry advisor, string question, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        bool? yes = null;
        var pending = _keys.RequestPaneAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
            try
            {
                yes = await _menu.ConfirmAsync(AdvisorText.ConfirmQuestion(advisor, question), linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !linked.IsCancellationRequested)
            {
                DiagnosticLog.Error(UI.ScreenPane.Category, "The advisor's confirm pane failed: " + Assistant.Explain(ex), ex);
            }
        });
        try
        {
            await pending.WaitAsync(turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
        {
            return null;
        }

        return yes ?? false;
    }

    /// <summary><c>/advisor</c>'s names (2026-10-10): the advisor list as <c>/advisor</c> reads it, each profile with its role as the note.</summary>
    private IReadOnlyList<CompletionItem> AdvisorChoices() =>
        AdvisorRoster.Entries(_effective().AdvisorList).Select(entry => new CompletionItem(entry.Profile, entry.Role)).ToList();

    /// <summary>
    /// <c>/advisor</c> (2026-10-10, the user's own hand on the profile advisors): bare, the advisors listed; <c>/advisor &lt;name&gt;
    /// &lt;question&gt;</c>, the question put to that one through <c>neon_advisor</c>'s own path — its thread, its link, this chat's
    /// read-only tools, the conversation's last messages always (<c>Advisor context</c> is the model's calls'; the user's call, later on 2026-10-10) — on or off whatever <c>Advisors enabled</c> says, never capped or confirmed (it is the
    /// user's own ask). ESC cancels. The pair joins the model's history tagged (<see cref="AdvisorText.HistoryUser"/>), as
    /// <c>/claude</c>'s does, and the store as a turn.
    /// </summary>
    private async Task HandleAdvisorAsync(string args, CancellationToken cancellationToken)
    {
        var effective = _effective();
        var advisors = AdvisorRoster.Entries(effective.AdvisorList);
        if (args.Trim().Length == 0)
        {
            if (advisors.Count == 0)
            {
                _transcript.Notice(AdvisorText.NoAdvisorsNotice);
                return;
            }

            _transcript.Notice(AdvisorText.ListHeading);
            foreach (var entry in advisors)
            {
                _transcript.Notice(AdvisorText.ListLine(entry, AdvisorServer.Read(_settings.StorageDirectory, entry.Profile), Profiles.Exists(_settings.StorageDirectory, entry.Profile)));
            }

            return;
        }

        var (words, question) = AdvisorText.Split(args);
        if (question.Length == 0)
        {
            _transcript.Error(AdvisorText.UsageError);
            return;
        }

        if (advisors.Count == 0)
        {
            _transcript.Error(AdvisorText.ForUser(AdvisorText.NoAdvisorsError));
            return;
        }

        var (advisor, candidates) = AdvisorRoster.Find(advisors, words);
        if (advisor is null)
        {
            _transcript.Error(AdvisorText.ForUser(AdvisorText.UnknownAdvisorError(words, candidates)));
            return;
        }

        // Always the recent messages (2026-10-10, the user's call): no model writes a brief for /advisor, so under Advisor context
        // brief the advisor knew nothing of the conversation; that setting is the model's calls' alone.
        var recent = AdvisorRecent.Lines(_session.History.Messages);
        using var askCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        using var killScope = BeginKillScope(askCts);
        // The reply's watch (2026-10-10, the user's report: the chords and the mouse were dead while an advisor answered; the /test and
        // /cmd shape, as /imagine's and the embedded load's fixes): a line typed, a chord or a glyph's word goes through the mid-turn
        // policy — a pane opens at once, a quick act runs while the advisor thinks, a message is queued — never pended for the idle line.
        var watcher = StartReplyWatch(askCts, stop.Token, cancellationToken);
        string? answer = null;
        try
        {
            using (_transcript.BeginBusy(AdvisorText.AskingLabel))
            {
                var asking = _neonAdvisor.AskAsync(advisor, question, "", recent, effective, askCts.Token);
                await RunActsUntilAsync(asking).ConfigureAwait(false);
                answer = await asking.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (askCts.IsCancellationRequested)
        {
            _transcript.Notice(CancelledNotice);
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
            await EndTurnAsync(closePane: askCts.IsCancellationRequested, cancellationToken).ConfigureAwait(false);
        }

        DrainDiagnostics();
        if (answer is null)
        {
            return;
        }

        if (AdvisorText.IsError(answer))
        {
            _transcript.Error(AdvisorText.ForUser(answer));
            return;
        }

        _session.History.AddUser(AdvisorText.HistoryUser(advisor.Profile, question));
        _session.History.AddAssistant(AdvisorText.HistoryReply(advisor.Profile, answer));
        string line = AdvisorText.Word + " " + args.Trim();
        _log.Add(line, answer);
        _lastReply = answer;
        LogSideTurn(line, answer, default, cancelled: false, effective);
    }

    /// <summary>
    /// An advisor on the transcript as it answers (2026-10-10, the shape of the Claude Code advisor tool's view, retired later that day): the question as a tool note, each tool
    /// it calls as a dim line, then its answer in <see cref="AdvisorColor"/> and the footer naming its model and tokens. The result's own
    /// line is left to <see cref="Render"/>, which prints only a refusal or a failure.
    /// </summary>
    private sealed class NeonAdvisorView(ChatScreen screen) : IAdvisorView
    {
        public void Began(AdvisorEntry advisor, string question) => screen._transcript.ToolNote(AdvisorText.QuestionNote(advisor, question));

        public void Tool(AdvisorEntry advisor, string name, string detail) => screen._transcript.ToolNote(AdvisorText.ToolNote(advisor.Profile, name, detail));

        public void Answered(AdvisorEntry advisor, string answer, string model, TokenUsage usage, string? warning)
        {
            screen._transcript.ToolAnswer(answer, AdvisorColor);
            screen._transcript.ToolNote(AdvisorText.Footer(advisor.Profile, model, usage, warning));
        }
    }
}
