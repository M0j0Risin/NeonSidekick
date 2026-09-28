using NeonSidekick.Llm;

namespace NeonSidekick.Bench;

/// <summary>
/// Runs <c>/test</c>'s tests one after the other over the connected <see cref="Assistant"/> (2026-09-28). Each is one
/// <see cref="Assistant.RequestAsync"/>: the test's own messages (not the conversation, not the system prompt), no tools,
/// the profile's reasoning and sampling as the chat's (the user's call), the test's <c>response_format</c>. The history is
/// never touched, and the reply is never shown or spoken — only the verdict. A transport failure (a context-length 400, a
/// timeout) is the test's <see cref="BenchVerdict.Error"/> with the assistant's explanation, as LLMTester's run makes it;
/// cancellation propagates, the results so far having gone through <c>onDone</c>.
/// </summary>
public static class BenchRunner
{
    /// <summary>
    /// A run about to start over <paramref name="assistant"/> at <paramref name="endpoint"/>: its time, the model and server,
    /// the window the long-context tests are sized to, and the reasoning and sampling every request of it carries (2026-09-28,
    /// the user's ask: so runs at different settings compare in <c>/test history</c>). Results are added as tests finish.
    /// </summary>
    public static BenchRun NewRun(Assistant assistant, LlmEndpoint endpoint, BenchContext context, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);
        return new BenchRun
        {
            At = time.GetUtcNow(),
            Model = endpoint.ModelId,
            Server = endpoint.BaseUrl.ToString(),
            ContextWindow = context.ContextWindowTokens,
            Reasoning = BenchText.ReasoningWord(assistant.Reasoning),
            Sampling = BenchText.SamplingWords(assistant.Sampling),
        };
    }

    /// <summary>
    /// Runs <paramref name="tests"/> in order: <paramref name="onStart"/> before each (1-based index, count, test),
    /// <paramref name="onDone"/> after. <paramref name="claudeApi"/> skips the tests that need <c>response_format</c>,
    /// which the Claude API does not take.
    /// </summary>
    public static async Task<IReadOnlyList<BenchResult>> RunAsync(
        Assistant assistant,
        IReadOnlyList<BenchTest> tests,
        BenchContext context,
        bool claudeApi,
        TimeProvider time,
        Action<int, int, BenchTest>? onStart,
        Action<BenchResult>? onDone,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);
        var results = new List<BenchResult>(tests.Count);
        for (int i = 0; i < tests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onStart?.Invoke(i + 1, tests.Count, tests[i]);
            var result = await RunOneAsync(assistant, tests[i], context, claudeApi, time, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            onDone?.Invoke(result);
        }

        return results;
    }

    /// <summary>One test: skipped, asked and judged, or the request's failure as an error.</summary>
    public static async Task<BenchResult> RunOneAsync(Assistant assistant, BenchTest test, BenchContext context, bool claudeApi, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(time);
        if (claudeApi && test.NeedsResponseFormat)
        {
            return new BenchResult { Test = test.Id, Verdict = BenchVerdict.Skipped, Reason = BenchText.SkippedOnClaudeApi };
        }

        var request = test.BuildRequest(context);
        long started = time.GetTimestamp();
        Assistant.SideResponse response;
        try
        {
            response = await assistant.RequestAsync(request.Messages, [], assistant.Reasoning, cancellationToken, request.Format).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BenchResult
            {
                Test = test.Id,
                Verdict = BenchVerdict.Error,
                Reason = assistant.ExplainFailure(ex),
                Seconds = time.GetElapsedTime(started).TotalSeconds,
            };
        }

        double seconds = time.GetElapsedTime(started).TotalSeconds;
        var judgement = test.Judge(response.Text, context);
        var usage = response.Usage;
        return new BenchResult
        {
            Test = test.Id,
            Verdict = judgement.Verdict,
            Reason = judgement.Reason,
            Answer = response.Text,
            Seconds = seconds,
            PromptTokens = usage?.Input,
            CompletionTokens = usage?.Output,
            ReasoningTokens = usage?.Reasoning,
            TokensPerSecond = usage?.TokensPerSecond,
        };
    }
}
