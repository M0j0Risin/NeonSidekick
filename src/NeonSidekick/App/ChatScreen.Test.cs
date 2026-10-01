using NeonSidekick.Bench;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Anthropic;

namespace NeonSidekick.App;

// ── /test (2026-09-28) ────────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/test</c> (2026-09-28, the user's ask: LLMTester's tests run from here): a bare <c>/test</c> lists the tests with
    /// each one's latest verdict against the connected model; <c>/test history</c> the saved runs; anything else names the
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
    private async Task HandleTestAsync(string args, CancellationToken cancellationToken)
    {
        var history = new BenchHistory(_settings.ProfileDirectory);
        if (args.Length == 0)
        {
            string? model = _session.Endpoint?.ModelId;
            WriteMarkdownReply(BenchText.Listing(model is null ? new Dictionary<string, (BenchResult, DateTimeOffset)>() : history.Latest(model), model));
            return;
        }

        if (args.Equals(BenchCatalog.HistoryWord, StringComparison.OrdinalIgnoreCase))
        {
            var runs = history.Runs();
            if (runs.Count == 0)
            {
                _transcript.Notice(BenchText.NoRuns);
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

        if (_session.Assistant is not { } assistant || _session.Endpoint is not { } endpoint)
        {
            _transcript.Error(NoAssistantError);
            return;
        }

        // The clean slate (2026-09-30): the screen, the conversation, the session and the hint row's figures, as /clear leaves them.
        ClearAndRefresh();
        var context = new BenchContext(_session.ContextLength?.Tokens);
        bool claudeApi = ClaudeApi.IsClaudeApi(endpoint.BaseUrl);
        if (tests.Any(t => t.Category == BenchCategory.LongContext))
        {
            _transcript.Notice(BenchText.ContextLine(context));
        }

        var run = BenchRunner.NewRun(assistant, endpoint, context, _time);
        bool live = _pane.Enabled;
        using var testCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        // The reply's watch (2026-09-30, UnderWatchAsync's shape): a line typed or a glyph's word goes through the mid-turn policy.
        _turnRunning = true;
        _paneClose?.Dispose();
        _paneClose = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var paneToken = _paneClose.Token;
        _queuedClicks.Reset();
        var watcher = _keys.WatchAsync(testCts, stop.Token, null, null, _pane.Enabled ? line => OnMidTurnLineAsync(line, testCts, paneToken) : null, spend: e => { _queuedClicks.Reset(); return ScrollInput(e); }, onClick: _pane.Enabled ? HintClickLine : null, editor: LiveEditor);
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
            await EndTurnAsync(closePane: testCts.IsCancellationRequested, cancellationToken).ConfigureAwait(false);
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
            WriteMarkdownReply(BenchText.Summary(run));
            if (!history.Append(run))
            {
                _transcript.Warning(BenchText.NotSaved);
            }
        }

        DiagnosticLog.Info("Test", $"/test {args}: {run.Passed}/{run.Counted} passed on {run.Model}{(run.Cancelled ? ", cancelled" : "")}.");
        DrainDiagnostics();
    }

    /// <summary><c>/test</c>'s argument list: each test by id (its name the note), the three group words, <c>all</c> and <c>history</c>.</summary>
    internal static IReadOnlyList<UI.CompletionItem> TestChoices() =>
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

    /// <summary>Markdown written as a reply is, styled per <c>Transcript markdown</c> (the <c>/echo</c> path): <c>/test</c>'s tables. Never spoken, never in the history.</summary>
    private void WriteMarkdownReply(string markdown)
    {
        var effective = _effective();
        _transcript.BeginAssistant(StyledReply(effective.TranscriptMarkdown, _pane.Enabled));
        _transcript.AppendDelta(markdown);
        _transcript.EndAssistant();
    }
}
