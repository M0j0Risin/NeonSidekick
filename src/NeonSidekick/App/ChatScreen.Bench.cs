using NeonSidekick.Bench;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Anthropic;

namespace NeonSidekick.App;

// ── /bench (2026-09-28) ────────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/bench</c> (2026-09-28, the user's ask: LLMTester's tests run from here): a bare <c>/bench</c> lists the tests with
    /// each one's latest verdict against the connected model; <c>/bench history</c> the saved runs; anything else names the
    /// tests to run (<see cref="BenchCatalog.Resolve"/>). A run goes test by test under a spinner whose label counts them,
    /// ESC stopping it the way it stops a compact; with the pane each test's line lands as it finishes, without it (the
    /// spinner owning the screen) they are written after. Then the table, and the run is saved in the profile's
    /// <c>tests.json</c> — a stopped one too, with what finished. Nothing enters the conversation.
    /// <para>A run starts from a clean slate (2026-09-30, the user's ask): <c>/clear</c>'s act first (<see cref="ClearAndRefresh"/>), so
    /// the screen is the banner, the conversation and its session are forgotten, and the hint row's usage figures go. Under the run
    /// the row and the glyphs are what they are under a reply (2026-09-30, the user's ask; a line waited for the run's end until then,
    /// and a glyph's word was dropped): a line goes through the mid-turn policy (<see cref="OnMidTurnLineAsync"/>). A pane
    /// (<c>/settings</c>, <c>/usage</c>, a toolbar glyph) opens over the run, a quick act runs at its end
    /// (<see cref="EndTurnAsync"/>), <c>/clear</c>, <c>/new</c> or <c>/exit</c> stops it as ESC does, and a message is queued.</para>
    /// </summary>
    private async Task HandleBenchAsync(string args, CancellationToken cancellationToken)
    {
        var history = new BenchHistory(_settings.ProfileDirectory);
        if (args.Length == 0)
        {
            // At the level the next run would use (2026-11-27); with no assistant (an idle-unloaded model still shown) every level, as before.
            string? model = _session.ShownModelId;
            string? level = _session.Assistant is { } shown ? BenchText.ReasoningWord(shown.Reasoning) : null;
            var latest = model is null ? new Dictionary<string, (BenchResult, DateTimeOffset)>()
                : level is null ? history.Latest(model) : history.Latest(model, level);
            WriteMarkdownReply(BenchText.Listing(latest, model, level));
            return;
        }

        if (args.Equals(BenchCatalog.HistoryWord, StringComparison.OrdinalIgnoreCase))
        {
            var runs = history.Runs();
            if (runs.Count == 0)
            {
                _transcript.Notice(BenchText.NoRuns);
            }
            else if (_pane.Enabled && !FromWebNow)
            {
                // The Bench tab at its recent runs (2026-11-27, the user's pick); from the page a pane at the PC would answer no one.
                await ShowUsageAsync(UsageText.BenchTabIndex, cancellationToken, BenchRecentLine(runs)).ConfigureAwait(false);
            }
            else
            {
                WriteMarkdownReply(BenchText.History(runs, history.FilePath));
            }

            return;
        }

        var tests = BenchCatalog.Resolve(args);
        if (tests.Count == 0)
        {
            _transcript.Error(BenchText.UnknownTest(args));
            return;
        }

        if (!await EnsureLlmAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (_session.Assistant is not { } assistant || _session.Endpoint is not { } endpoint)
        {
            _transcript.Error(NoAssistantError);
            return;
        }

        // The clean slate (2026-09-30): the screen, the conversation, the session and the hint row's figures, as /clear leaves them; a private
        // conversation ends first, as /clear ends it (2026-11-26, the review's #103).
        await EndPrivateForResetAsync(cancellationToken, ClearAndRefresh).ConfigureAwait(false);
        var context = new BenchContext(_session.ContextLength?.Tokens);
        bool claudeApi = ClaudeApi.IsClaudeApi(endpoint.BaseUrl);
        if (tests.Any(t => t.SizedToWindow))
        {
            _transcript.Notice(BenchText.ContextLine(context, tests));
        }

        var run = BenchRunner.NewRun(assistant, endpoint, context, _time);
        bool live = _pane.Enabled;
        using var testCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        using var killScope = BeginKillScope(testCts);
        // The reply's watch (2026-09-30, UnderWatchAsync's shape; StartReplyWatch's since 2026-10-01): a line typed or a glyph's word goes through the mid-turn policy.
        var watcher = StartReplyWatch(testCts, stop.Token, cancellationToken);
        try
        {
            await _transcript.WithSpinnerAsync(BenchText.Label(1, tests.Count, tests[0]), setLabel => BenchRunner.RunAsync(
                assistant, tests, context, claudeApi, _time,
                (index, count, test) => setLabel(BenchText.Label(index, count, test)),
                result =>
                {
                    run.Results.Add(result);
                    if (live)
                    {
                        WriteTestResult(result);
                    }
                },
                testCts.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (testCts.IsCancellationRequested)
        {
            run.Cancelled = true;
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
            await EndTurnAsync(closePane: testCts.IsCancellationRequested, cancellationToken, run: true).ConfigureAwait(false);
        }

        if (!live)
        {
            foreach (var result in run.Results)
            {
                WriteTestResult(result);
            }
        }

        if (run.Cancelled)
        {
            _transcript.Notice(BenchText.Cancelled(run.Results.Count, tests.Count));
        }

        if (run.Results.Count > 0)
        {
            WriteMarkdownReply(BenchText.Summary(run, history.Previous(run)));   // read before the append, so never this run
            if (!history.Append(run))
            {
                _transcript.Warning(BenchText.NotSaved);
            }
        }

        DiagnosticLog.Info(BenchText.LogCategory, $"/bench {args}: {run.Passed}/{run.Counted} passed on {run.Model}{(run.Cancelled ? ", canceled" : "")}.");
        DrainDiagnostics();
    }

    /// <summary>The profile's saved runs, for the Bench tab (read when the tab is built, as <c>/bench</c> reads them).</summary>
    private IReadOnlyList<BenchRun> BenchRuns() => new BenchHistory(_settings.ProfileDirectory).Runs();

    /// <summary>Your setup for the Bench grid's mark: the connected model at the assistant's level; null with no assistant, so no row is guessed.</summary>
    private (string? Model, string Reasoning)? BenchCurrent() =>
        _session.Assistant is { } assistant ? (_session.ShownModelId, BenchText.ReasoningWord(assistant.Reasoning)) : null;

    /// <summary>The Bench tab's Recent runs rule's line at the pane's body width, where <c>/bench history</c> opens the view.</summary>
    private int? BenchRecentLine(IReadOnlyList<BenchRun> runs) =>
        UsageText.BenchLayout(_sessions.ModelStats(), runs, BenchCurrent(), _effective().SessionLogging, UsageBodyWidth(), _private.IsPrivate).RecentLine;

    /// <summary><c>/bench</c>'s argument list: each test by id (its name the note), the three group words, <c>all</c> and <c>history</c>.</summary>
    internal static IReadOnlyList<UI.CompletionItem> BenchChoices() =>
        BenchCatalog.All.Select(t => new UI.CompletionItem(t.Id, t.Name))
            .Concat(BenchCatalog.CategoryWords.Select(g => new UI.CompletionItem(g.Word, BenchText.GroupNote(g.Category))))
            .Append(new UI.CompletionItem(BenchCatalog.AllWord, BenchText.AllNote))
            .Append(new UI.CompletionItem(BenchCatalog.HistoryWord, BenchText.HistoryNote))
            .ToList();

    /// <summary>One finished test: its line (a notice for a pass or a skip, a warning for a fail, an error for an error) and, under a fail, what the model answered.</summary>
    private void WriteTestResult(BenchResult result)
    {
        string line = BenchText.ResultLine(result);
        switch (result.Verdict)
        {
            case BenchVerdict.Fail:
                _transcript.Warning(line);
                break;
            case BenchVerdict.Error:
                _transcript.Error(line);
                break;
            default:
                _transcript.Notice(line);
                break;
        }

        if (BenchText.AnswerLine(result) is { } answer)
        {
            _transcript.Notice(answer);
        }
    }

    /// <summary>Markdown written as a reply is, styled per <c>Transcript markdown</c> (the <c>/echo</c> path): <c>/bench</c>'s tables. Never spoken, never in the history.</summary>
    private void WriteMarkdownReply(string markdown)
    {
        var effective = _effective();
        _transcript.BeginAssistant(StyledReply(effective.TranscriptMarkdown, _pane.Enabled), UI.TranscriptRenderer.GlyphFor(effective.ModelAvatar));
        _transcript.AppendDelta(markdown);
        _transcript.EndAssistant();
    }
}
