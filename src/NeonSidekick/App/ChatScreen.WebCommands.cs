using NeonSidekick.Audits;
using NeonSidekick.Diagnostics;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── Commands from the Web UI's page (later on 2026-10-14, the user's ask) ───
// A slash command the page sends goes through the same dispatch a typed one does, at the idle line. What differs is where it came from:
// a question it asks is put on the page and the PC at once (RaceAsync, the first answer wins), a pick is a card on the page, a list a card,
// and a draft it fills goes to the page's draft. The origin rides the dispatch as an AsyncLocal, set where the line is taken and put back
// to the PC's wherever the PC's own hand takes over (the watcher, a chord, a later pass off a pane, a turn's hand-overs).

/// <summary>One row of a pick raced on the PC and the page: the value it stands for, its words and a note.</summary>
internal sealed record PickOption(string Value, string Label, string? Note = null);

/// <summary>Who sent the command running now: the PC (typed, a chord, a click) or the Web UI's page.</summary>
internal enum CommandOrigin
{
    Pc,
    Web,
}

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The origin of the command being dispatched (later on 2026-10-14). Only the dispatch path reads it — the idle line's arms and what they
    /// await — never a timer's callback or a job behind the line, which run under whatever origin started them and must not ask by it.
    /// </summary>
    private static readonly AsyncLocal<CommandOrigin> s_origin = new();

    /// <summary>
    /// A command from the page is being dispatched (later on 2026-10-14): <see cref="WaitingAtPc"/> then names a pane the PC opened with no ask on
    /// the page, so the page says what it waits for rather than nothing. The safety net under <see cref="WebCommands"/>' refusals.
    /// </summary>
    private volatile bool _webDispatching;

    /// <summary>The lines held for the idle line and their hand-back to the page, one at a time (<see cref="Pend"/>, <see cref="DropQueue"/>).</summary>
    private readonly object _pendingGate = new();

    /// <summary>Whether the command running now came from the page.</summary>
    private static bool FromWebNow => s_origin.Value == CommandOrigin.Web;

    /// <summary>Runs <paramref name="run"/> under <paramref name="origin"/>: an async run takes it into its awaits; the caller's own is put back as it returns.</summary>
    private static T WithOrigin<T>(CommandOrigin origin, Func<T> run)
    {
        var was = s_origin.Value;
        s_origin.Value = origin;
        try
        {
            return run();
        }
        finally
        {
            s_origin.Value = was;
        }
    }

    /// <summary>The settings a command from the page is checked against, as they stand now.</summary>
    private WebCommandSettings WebCommandSettingsNow()
    {
        var effective = _effective();
        return new WebCommandSettings(effective.WebUiCommands, effective.FileTools, effective.ObsidianTools, effective.WebUiShoppingControls, WebTabsNow().YouTube);
    }

    /// <summary>
    /// The dispatch's guard for a line from the page (later on 2026-10-14): everything <see cref="WebUiSession.Send"/> checked, read again (the
    /// settings may have changed while it waited), and what only the screen knows — a model set by id or a reasoning level whose connect would
    /// look for a server (no URL, the scan on: its pickers are the PC's), or an embedded model that would be installed first. The refusal's
    /// sentence, or null to run it.
    /// </summary>
    private string? WebDispatchRefusal(SlashCommand command, string args, string text)
    {
        var verdict = WebCommands.Check(command, args, text, WebCommandSettingsNow());
        if (!verdict.Allowed)
        {
            return verdict.Reason;
        }

        if (command is SlashCommand.Model or SlashCommand.Reasoning)
        {
            // The connect's own test (2026-10-17, the user's question: a saved hosted API switched off, an embedded server switched off or an
            // unchosen Docker URL scanned too, opening the PC's pickers; only a blank URL with the scan on was caught until then).
            var effective = _effective();
            if (ConnectWouldPick(effective))
            {
                return WebUiText.WouldScan;
            }

            // The model the connect would load: the one named by /model, the saved one for /reasoning (2026-10-17: its reconnect installs a
            // missing model as /model's does).
            string id = command == SlashCommand.Model ? args.Trim() : effective.LlmModel ?? "";
            if (id.Length > 0 && EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(effective.LlmUrl)
                && _session.Embedded is { } embedded && EmbeddedLlm.EmbeddedModelCatalog.Find(id, embedded.Catalog) is { } named
                && !embedded.State(named).IsInstalled)
            {
                return WebUiText.WouldInstall;
            }
        }

        return null;
    }

    /// <summary>
    /// A question a command asks, on the PC's pane and — when the command came from the page — the page at once (later on 2026-10-14): the first
    /// answer wins. From the PC it is the pane alone, as ever. From the page, with a watcher running (a turn the command started) and this not
    /// its own phase, the watcher runs the pane (<see cref="AskBothAsync"/>); otherwise — the idle line, where no watcher reads the keys, or
    /// inside a phase — the pane runs here under a token the page's answer cancels. Either side claims before it counts, since a pane answers
    /// on its cancel (<see cref="WebAskHandle.TryClaim"/>). Without menus the page alone asks. <paramref name="noticeAnswer"/> false keeps the
    /// "answered from the Web UI" line out (a list card's close).
    /// </summary>
    /// <remarks>
    /// <paramref name="bothSides"/> (2026-10-18, the Ralph loop's checkpoint and its baseline question: a loop runs unattended, so the page may be
    /// where the user is) asks the page too whoever started the command, while the Web UI is on; with no pane here and no Web UI, never asked.
    /// </remarks>
    private async Task<AskOutcome<T>> RaceAsync<T>(WebAskSpec spec, Func<CancellationToken, Task<T>> pane, Func<WebAnswerRequest, T> fromWeb, string failure, CancellationToken cancellationToken, bool noticeAnswer = true, bool bothSides = false)
    {
        if (!FromWebNow && !(bothSides && _effective().WebUi))
        {
            return new AskOutcome<T>(await pane(cancellationToken).ConfigureAwait(false), AskSource.Pc, NeverAsked: false);
        }

        if (_keys.Watching && !KeySource.InPhase)
        {
            return await AskBothAsync(spec, pane, fromWeb, failure, cancellationToken, noticeAnswer).ConfigureAwait(false);
        }

        var handle = _webUi.Asks.Open(spec);
        try
        {
            if (!_menu.CanShowMenus())
            {
                // No pane to put it on: the page alone.
                var only = await handle.Answered.WaitAsync(cancellationToken).ConfigureAwait(false);
                return Answered(only);
            }

            using var paneCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            T? paneResult = default;
            bool paneAnswered = false;

            async Task RunPaneAsync()
            {
                try
                {
                    var result = await pane(paneCts.Token).ConfigureAwait(false);
                    if (handle.TryClaim(AskSource.Pc))
                    {
                        paneResult = result;
                        paneAnswered = true;
                    }
                }
                catch (OperationCanceledException) when (paneCts.IsCancellationRequested)
                {
                    // Closed under it: by the page's answer or the app's end.
                }
            }

            var running = RunPaneAsync();
            var first = await Task.WhenAny(running, handle.Answered).ConfigureAwait(false);
            if (first == running)
            {
                await running.ConfigureAwait(false);
                if (paneAnswered || handle.Winner != AskSource.Web)
                {
                    handle.TryClaim(AskSource.Pc);
                    return new AskOutcome<T>(paneResult, paneAnswered ? AskSource.Pc : null, NeverAsked: false);
                }
            }

            // The page answered: the pane closes (its own answer dropped) before anything else reads the keys.
            var answer = await handle.Answered.ConfigureAwait(false);
            VoiceSession.SafeCancel(paneCts);
            await running.ConfigureAwait(false);
            return Answered(answer);
        }
        finally
        {
            _webUi.Asks.Close(handle);
        }

        AskOutcome<T> Answered(WebAnswerRequest answer)
        {
            string said = handle.Describe(answer);
            DiagnosticLog.Info(WebUiCategory, WebUiText.AnsweredLogLine(spec.Title, said, handle.Remote));
            if (noticeAnswer)
            {
                _transcript.Notice(WebUiText.AnsweredNotice(said));
            }

            return new AskOutcome<T>(fromWeb(answer), AskSource.Web, NeverAsked: false);
        }
    }

    /// <summary>
    /// A pick on the PC's pane and the page's card at once (later on 2026-10-14: bare <c>/model</c>, <c>/reasoning</c>, <c>/rewind</c> from the page):
    /// <paramref name="options"/> as the card's rows (ids made up, <c>i0</c>, <c>i1</c>…), the one whose value is <paramref name="current"/> marked;
    /// <paramref name="pane"/> the PC's own picker, which returns a value. The value chosen on either, or null (cancel, ESC).
    /// </summary>
    private async Task<string?> PickBothAsync(string title, IReadOnlyList<PickOption> options, string? current, Func<CancellationToken, Task<string?>> pane, CancellationToken cancellationToken)
    {
        var items = options.Select((option, i) => new WebPickItem("i" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), option.Label, option.Note)).ToList();
        int at = current is null ? -1 : options.ToList().FindIndex(o => o.Value == current);
        var spec = new WebAskSpec("pick", title, [new WebChoice("cancel", WebUiText.CancelChoice)], Items: items, Current: at >= 0 ? items[at].Id : null);
        var outcome = await RaceAsync(spec, pane, answer => items.FindIndex(i => i.Id == answer.Choice) is var n and >= 0 ? options[n].Value : null,
            "The pick pane failed: ", cancellationToken).ConfigureAwait(false);
        return outcome.Value;
    }

    /// <summary>
    /// <c>/model</c> alone from the page (later on 2026-10-14): the server's models listed under the spinner (the page sees its working line), then the
    /// card there and the picker here, the one chosen saved and connected. Without menus here, the page alone.
    /// </summary>
    private async Task PickModelFromWebAsync(CancellationToken cancellationToken)
    {
        var listed = await _transcript.WithSpinnerAsync(SettingsMenu.ListingModelsLabel, () => _session.ListModelsAsync(cancellationToken)).ConfigureAwait(false);
        if (listed is not { } models)
        {
            _transcript.Error(SettingsMenu.NoUrlError);
            return;
        }

        string current = _session.Endpoint?.ModelId ?? _settings.Current.LlmModel;
        var (ids, cursor) = SettingsMenu.ModelChoices(models, current);
        if (ids.Count == 0)
        {
            _transcript.Error(SettingsMenu.NoModelsError(models));
            return;
        }

        var options = ids.Select(id => new PickOption(id, id, id == current ? SettingsMenu.ModelInUseNote : null)).ToList();
        string? chosen = await PickBothAsync(SettingsMenu.ModelTitle, options, current, token => _menu.ChooseModelFromListAsync(ids, cursor, token), cancellationToken).ConfigureAwait(false);
        if (_menu.SaveModelChoice(chosen))
        {
            await ConnectLlmAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The page's form of a list (later on 2026-10-14): the lines in monospace, one close; a list past <see cref="WebUiText.MaxInfoChars"/> keeps its
    /// last part, under <see cref="WebUiText.InfoCutNote"/>.
    /// </summary>
    internal static WebAskSpec InfoSpec(string label, IReadOnlyList<string> lines)
    {
        var kept = new List<string>();
        int chars = 0;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            chars += lines[i].Length + 1;
            if (chars > WebUiText.MaxInfoChars)
            {
                kept.Add(WebUiText.InfoCutNote);
                break;
            }

            kept.Add(lines[i]);
        }

        kept.Reverse();
        return new WebAskSpec("info", label, [new WebChoice("close", WebUiText.CloseChoice)], Lines: kept);
    }

    /// <summary>The page's form of a yes/no: the PC's own No and Yes.</summary>
    internal static WebAskSpec ConfirmSpec(string question) =>
        new("confirm", question, [new WebChoice("no", SettingsMenu.ConfirmRows[0]), new WebChoice("yes", SettingsMenu.ConfirmRows[1])]);

    /// <summary>
    /// A command's yes/no from the page (later on 2026-10-14): raced on the PC's pane and the page (<see cref="RaceAsync"/>); no answer is a no.
    /// The hook <c>/ha</c> and <c>/docker</c> take for a change from the page, which they make unasked from the PC.
    /// </summary>
    private async Task<bool?> ConfirmFromWebAsync(string question, CancellationToken cancellationToken)
    {
        var outcome = await RaceAsync<bool>(ConfirmSpec(question), token => _menu.ConfirmAsync(question, token), a => a.Choice == "yes",
            "The yes/no pane failed: ", cancellationToken).ConfigureAwait(false);
        return outcome.Value;
    }

    /// <summary>The confirm hook a command run from the page takes (null from the PC: it acts unasked there).</summary>
    private Func<string, CancellationToken, Task<bool?>>? WebConfirmHook => FromWebNow ? ConfirmFromWebAsync : null;

    /// <summary>
    /// A text for the draft line (later on 2026-10-14): from the PC, <paramref name="pc"/> as ever (the row's draft); from the page, the page's
    /// draft — a <c>draft</c> event it puts ahead of what it has, with an undo, held until a page takes it. <c>/rewind</c>'s alone since
    /// 2026-10-16, when <c>/prompts &lt;n&gt;</c> came to send its prompt.
    /// </summary>
    private void ToDraft(string text, Action pc)
    {
        if (FromWebNow)
        {
            _webUi.Draft(text);
            return;
        }

        pc();
    }

    /// <summary>
    /// Leaves <paramref name="line"/> for the idle line; always taken. Under a running <c>/botchat</c> a <c>/code-audit</c> that would start passes
    /// is refused here instead (2026-10-18, the user's pick: a held audit would start an unattended chain of passes long after it was typed):
    /// every held line passes through this one place, the botchat drain's own interjections included. A line sent as text, and a multi-line
    /// or pasted draft (no <c>CommandText</c>), stays a message as everywhere else.
    /// </summary>
    private bool Pend(SubmittedLine line)
    {
        if (_botChatRunning && !line.AsText && line.CommandText is { } text && ParseLine(text) is (SlashCommand.CodeAudit, var args) && AuditCommand.StartsPasses(args))
        {
            Post(() => _transcript.Error(AuditText.BotChatError));
            return true;
        }

        // A Ralph loop the same way (later on 2026-10-18): held under a botchat, it would start an unattended chain of passes long after it was typed.
        if (_botChatRunning && !line.AsText && line.CommandText is { } ralphText && ParseLine(ralphText) is (SlashCommand.Plan, var planArgs)
            && ParsePlanArgs(planArgs, planning: true) is PlanCommand.Ralph or PlanCommand.ApproveRalph)
        {
            Post(() => _transcript.Error(Plans.RalphText.UnderBotChatError));
            return true;
        }

        lock (_pendingGate)
        {
            _pendingLines.Enqueue(line);
        }

        return true;
    }

    /// <summary>The page's lines held for the idle line, handed back to the page's draft (a conversation cleared, a profile switch); their count.</summary>
    private int DropPendingWebLines()
    {
        List<SubmittedLine> dropped = [];
        lock (_pendingGate)
        {
            int count = _pendingLines.Count;
            for (int i = 0; i < count && _pendingLines.TryDequeue(out var line); i++)
            {
                if (line.FromWeb)
                {
                    dropped.Add(line);
                }
                else
                {
                    _pendingLines.Enqueue(line);
                }
            }
        }

        foreach (var line in dropped)
        {
            _webUi.Dropped(line.Draft);
        }

        return dropped.Count;
    }

    /// <summary>
    /// The Web UI dropdown's lists, built here on the screen's task (later on 2026-10-14; the idle line's top, while a page is connected): every list
    /// read now and kept as it is, the argument lists' sources over that copy — the last Home Assistant and Docker reads, the printers last listed,
    /// the shopping list's lines and stores — and the working directory's paths only while <i>File tools</i> is on. Leaves out what the page may
    /// not run anyway (sessions, themes, servers, the screen's targets, processes, any drive's folders) and the vault's folders and the embedded
    /// models. Nothing here refreshes, probes or writes.
    /// </summary>
    private WebCompletionSnapshot WebCompletionsNow()
    {
        var effective = _effective();
        var settings = WebCommandSettingsNow();
        bool files = effective.FileTools;
        var wd = _files;
        var empty = new Files.MentionResult(Files.FileOutcome.Ok, [], false);
        IReadOnlyList<CompletionItem> commands = settings.Commands ? CommandChoices() : [];
        var skills = HashChoices();
        var tools = DollarChoices();
        var connections = PercentChoices();
        var workflows = CaretChoices();
        var shares = StarChoices();
        var profiles = Settings.Profiles.List(_settings.StorageDirectory);
        var timers = _timers.Snapshot().Select(timer => timer.Name).ToList();
        var skillList = SkillChoices();
        var workflowList = WorkflowChoices();
        var plans = PlanChoices();
        var audits = AuditChoices();
        var ha = _ha.Last;
        var docker = _docker.Last;
        var printers = _printerNames.Last;
        var advisors = AdvisorChoices();
        var store = _shopping?.Store;
        IReadOnlyList<string> stores = store is { Available: true } ? [.. store.Stores().Select(s => s.Name)] : [];
        IReadOnlyList<string> items = store is { Available: true } ? [.. store.Items().Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase)] : [];
        var arguments = new ArgumentSources(
            () => profiles,
            _settings.ProfileName,
            timers,
            prefix => files ? [.. wd.Complete(prefix).Paths.Where(path => path.EndsWith('/'))] : [],
            prefix => files ? wd.Complete(prefix, full => Files.WorkingDirectory.IsTextFile(full) || Files.PdfFile.IsPdfPath(full)) : empty,
            prefix => files ? wd.Complete(prefix, full => Files.ImageFile.IsImagePath(full) || Files.PdfFile.IsPdfPath(full)) : empty,
            Skills: () => skillList,
            Workflows: () => workflowList,
            Planning: _plan.Active,
            Plans: () => plans,
            Home: argText => HomeAssistant.HaCommand.Complete(ha, argText),
            AnyFiles: prefix => files ? wd.Complete(prefix) : empty,
            Print: argText => Printing.PrintCommand.Complete(argText, printers),
            Docker: argText => Docker.DockerCommand.Complete(docker, argText),
            Advisors: () => advisors,
            Shopping: argText => Shopping.ShoppingCommand.Complete(argText, stores, items),
            EventLog: EventLogChoices,
            Net: NetChoices,
            Services: ServiceChoices,
            Tasks: TaskChoices,
            Ad: AdChoices,
            Audits: () => audits);
        var sources = new CompletionSources(
            () => commands,
            (command, argText) => ArgumentPaths(command, argText, arguments) is { } paths
                ? new ArgumentList([], paths.Paths, paths.Truncated)
                : new ArgumentList(ArgumentItems(command, argText, arguments)),
            () => skills,
            () => tools,
            () => connections,
            () => workflows,
            () => shares,
            query => files ? wd.Complete(query) : empty);
        var lists = new CompletionLists(true, true, files, true, true, true, true, true);
        bool remains = Files.MentionFolderMode.Resolve(effective) == Files.MentionFolderAction.Remain;
        return new WebCompletionSnapshot(sources, lists, _input.PathArgument, remains, settings, effective.WebUiConnectionDetails);
    }

    /// <summary>The dropdown's lists built again for the pages connected now (the idle line's top); nothing with none.</summary>
    private void RefreshWebCompletions()
    {
        if (_webUi.Events.Count == 0)
        {
            return;
        }

        try
        {
            _webUi.Completions = WebCompletionsNow();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(WebUiCategory, "Web UI: the completion lists could not be built: " + e.Message);
        }
    }

    /// <summary>The page's lines held for the idle line, as the page lists them waiting.</summary>
    private IReadOnlyList<string> HeldWebLines() => [.. _pendingLines.Where(l => l.FromWeb).Select(l => l.Draft)];
}
