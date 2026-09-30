using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Claude CLI server (2026-09-30): the stream and stdin shapes against the spike's captures from Claude Code 2.1.285
/// (<c>Fixtures/claude/server-*</c>, paths scrubbed), the command line, the chat client over
/// <see cref="FakeClaudeServerHost"/> — the app's own loop running the model's calls, the process kept across turns, an
/// interrupt, a lost session — and the MCP server reached through the relay.
/// </summary>
public class ClaudeCliServerTests
{
    private const string SessionId = "11111111-2222-3333-4444-555555555555";

    /// <summary>The spike's first call: <c>mcp__neon__echo</c> with <c>{"text":"hi"}</c>.</summary>
    private const string EchoCallId = "toolu_016mMQBLfi4hUy2iFqK2dtMN";

    private static string[] Fixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude", name));

    private static List<ClaudeServerEvent> Parse(IEnumerable<string> lines)
    {
        var stream = new ClaudeServerStream();
        return lines.SelectMany(stream.Read).ToList();
    }

    private static bool IsUserLine(string line) => line.StartsWith("{\"type\":\"user\"", StringComparison.Ordinal);

    private static bool IsInterruptLine(string line) => line.Contains("\"subtype\":\"interrupt\"", StringComparison.Ordinal);

    /// <summary>The spike's two turns: the tool turn up to its first <c>message_stop</c>, the rest of it, and the second turn.</summary>
    private static (string[] ToCall, string[] AfterCall, string[] Second) ToolTurns()
    {
        var lines = Fixture("server-tool-turns.jsonl");
        int stop = Array.FindIndex(lines, l => l.Contains("\"type\":\"message_stop\"", StringComparison.Ordinal));
        int end = Array.FindIndex(lines, l => l.Contains("\"type\":\"result\"", StringComparison.Ordinal));
        return (lines[..(stop + 1)], lines[(stop + 1)..(end + 1)], lines[(end + 1)..]);
    }

    private static ClaudeCliChatClient Client(IClaudeServerHost host, Func<ProcessLaunchCapture, IAsyncEnumerable<ClaudeEvent>>? oneShot = null, ProcessLaunchCapture? capture = null) =>
        new(LlmSession.ClaudeCliEndpointOf("haiku"), host, new ClaudeCliContext(() => @"C:\tools\claude.exe", @"C:\home\claude-cli",
            oneShot is null ? null : (launch, prompt, _) =>
            {
                capture!.Launch = launch;
                capture.Prompt = prompt;
                return oneShot(capture);
            }), TimeSpan.FromMinutes(1));

    public sealed class ProcessLaunchCapture
    {
        public Shell.ProcessLaunch? Launch { get; set; }

        public string Prompt { get; set; } = "";
    }

    private static async IAsyncEnumerable<ClaudeEvent> Events(params ClaudeEvent[] events)
    {
        foreach (var evt in events)
        {
            await Task.Yield();
            yield return evt;
        }
    }

    private static async Task<List<TurnEvent>> RunAsync(Assistant assistant, string text, CancellationToken cancellationToken = default)
    {
        var events = new List<TurnEvent>();
        await foreach (var evt in assistant.RunTurnAsync(text, cancellationToken))
        {
            events.Add(evt);
        }

        return events;
    }

    private static string Reply(IEnumerable<TurnEvent> events) => string.Concat(events.OfType<TurnEvent.TextDelta>().Select(d => d.Text));

    private static JsonNode UserContent(string line) => JsonNode.Parse(line)!["message"]!["content"]!;

    /// <summary>
    /// The host plays the spike's tool turn: the stream to the call, then — as the CLI's MCP relay — the call itself,
    /// the rest of the stream once the app answers it. The answer the relay got is <paramref name="answer"/>'s.
    /// </summary>
    private static void PlayToolTurn(FakeClaudeServerHost host, TaskCompletionSource<ClaudeToolAnswer> answer)
    {
        var (toCall, afterCall, _) = ToolTurns();
        host.OnWrite = line =>
        {
            if (IsUserLine(line))
            {
                host.Feed(toCall);
                var arguments = new Dictionary<string, JsonElement> { ["text"] = JsonDocument.Parse("\"hi\"").RootElement.Clone() };
                _ = Task.Run(async () =>
                {
                    var got = await host.OnToolCall!(new ClaudeToolCall(EchoCallId, "echo", arguments), CancellationToken.None);
                    answer.TrySetResult(got);
                    host.Feed(afterCall);
                });
            }

            return Task.CompletedTask;
        };
    }

    // ── the stream ──────────────────────────────────────────────────────────

    [Fact]
    public void Stream_TheToolTurns_TheInitTheCallTheTextAndTheResults()
    {
        var events = Parse(Fixture("server-tool-turns.jsonl"));

        var init = events.OfType<ClaudeServerEvent.Init>().First();
        Assert.Equal(["mcp__neon__echo", "mcp__neon__slow"], init.Tools);   // Claude Code's own tools all off
        Assert.True(init.Interrupts);
        Assert.Equal(2, events.OfType<ClaudeServerEvent.Init>().Count());   // one per turn, after its user line

        var call = Assert.Single(events.OfType<ClaudeServerEvent.ToolUse>());
        Assert.Equal((EchoCallId, "mcp__neon__echo"), (call.Id, call.Name));
        Assert.Equal("hi", JsonNode.Parse(call.InputJson)!["text"]!.GetValue<string>());
        Assert.True(events.IndexOf(call) < events.FindIndex(e => e is ClaudeServerEvent.MessageStop));   // the call is known at its message's stop

        var results = events.OfType<ClaudeServerEvent.Result>().ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.IsError));
        Assert.Equal((0.003454m, 0.00615m), (results[0].TotalCostUsd, results[1].TotalCostUsd));   // the process's running total
        Assert.Equal(200_000, results[0].ContextWindow);
        Assert.Equal("completed", results[0].TerminalReason);
        int first = events.IndexOf(results[0]);
        Assert.Equal("echo: hi", string.Concat(events.Take(first).OfType<ClaudeServerEvent.TextDelta>().Select(d => d.Text)));
        Assert.Empty(events.OfType<ClaudeServerEvent.ThinkingDelta>());   // the spike's thinking came with no text: nothing to show
    }

    [Fact]
    public void Stream_ThinkingWithText_IsAThinkingDelta()
    {
        var events = Parse(["""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me see."}}}"""]);

        Assert.Equal("Let me see.", Assert.IsType<ClaudeServerEvent.ThinkingDelta>(Assert.Single(events)).Text);
    }

    [Fact]
    public void Stream_AnInterrupt_IsAnsweredAndTheTurnEndsAborted()
    {
        var events = Parse(Fixture("server-interrupt.jsonl"));

        var answers = events.OfType<ClaudeServerEvent.ControlResponse>().ToList();
        Assert.Contains(answers, a => a is { RequestId: "int-1", Success: true });
        var results = events.OfType<ClaudeServerEvent.Result>().ToList();
        Assert.Equal(["aborted_tools", "aborted_streaming", "completed"], results.Select(r => r.TerminalReason));
        Assert.True(results[0].IsError && results[1].IsError && !results[2].IsError);
    }

    [Fact]
    public void Stream_AToolsHeartbeats_AreSkipped()
    {
        var lines = Fixture("server-tool-progress.jsonl").Where(l => l.Contains("\"type\":\"tool_progress\"", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(lines);   // the spike's 130 s call beat every 30 s
        Assert.Empty(Parse(lines));
    }

    // ── stdin ───────────────────────────────────────────────────────────────

    [Fact]
    public void UserLine_IsTheShapeTheCliRead()
    {
        string spike = Fixture("server-tool-turns.stdin.jsonl")[0];
        var parsed = JsonNode.Parse(spike)!;
        string text = UserContent(spike)[0]!["text"]!.GetValue<string>();

        string line = ClaudeServerInput.UserLine(parsed["session_id"]!.GetValue<string>(), [new TextContent(text)]);

        Assert.True(JsonNode.DeepEquals(parsed, JsonNode.Parse(line)));
        Assert.DoesNotContain('\n', line);   // one line
    }

    [Fact]
    public void UserLine_APicture_IsABase64ImageBlock()
    {
        byte[] png = [137, 80, 78, 71];
        var content = UserContent(ClaudeServerInput.UserLine(SessionId, [new TextContent("what is this"), new DataContent(png, "image/png")]));

        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        var image = content[1]!;
        Assert.Equal("image", image["type"]!.GetValue<string>());
        Assert.Equal(("base64", "image/png"), (image["source"]!["type"]!.GetValue<string>(), image["source"]!["media_type"]!.GetValue<string>()));
        Assert.Equal(Convert.ToBase64String(png), image["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public void InterruptLine_IsTheSpikes()
    {
        string spike = Fixture("server-interrupt.stdin.jsonl").First(l => JsonNode.Parse(l)!["type"]!.GetValue<string>() == "control_request");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(spike), JsonNode.Parse(ClaudeServerInput.InterruptLine("int-1"))));
    }

    [Fact]
    public void Split_ATurnsSeededCalls_GoAheadOfTheMessageAsContext()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "be brief"),
            new(ChatRole.User, "hello"),
            new(ChatRole.Assistant, [new FunctionCallContent(Assistant.OpeningClockCallId, "get_current_time", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent(Assistant.OpeningClockCallId, "Wednesday, noon")]),
        };

        var input = ClaudeServerInput.Split(messages);

        Assert.Equal("be brief", input.SystemPrompt);
        Assert.False(input.HasEarlierTurns);
        Assert.Empty(input.Results);
        Assert.Equal(
            [ClaudeCliText.SeededContext([ClaudeCliText.SeededLine("get_current_time", "Wednesday, noon")]), "hello"],
            input.NewContent.OfType<TextContent>().Select(t => t.Text));
    }

    [Fact]
    public void Split_AfterTheModelsCalls_TheResultsAreTheNewPart()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "echo hi"),
            new(ChatRole.Assistant, [new FunctionCallContent("toolu_1", "echo", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_1", "echo: hi")]),
        };

        var input = ClaudeServerInput.Split(messages);

        Assert.True(input.HasEarlierTurns);
        Assert.Equal("echo: hi", ClaudeServerInput.ResultText(Assert.Single(input.Results)));
    }

    [Fact]
    public void Preamble_TheWordsOfEachSide_ToolsLeftOut()
    {
        var earlier = new List<ChatMessage>
        {
            new(ChatRole.User, "what is 2+2"),
            new(ChatRole.Assistant, [new FunctionCallContent("c", "calc", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("c", "4")]),
            new(ChatRole.Assistant, "four"),
        };

        Assert.Equal(ClaudeCliText.PreambleHeader + "\n\nUser: what is 2+2\n\nAssistant: four", ClaudeServerInput.Preamble(earlier));
        Assert.Equal("", ClaudeServerInput.Preamble([]));
    }

    // ── the command line ────────────────────────────────────────────────────

    [Fact]
    public void BuildServer_TheCliRunsTheAppsToolsAlone()
    {
        var launch = new ClaudeServerLaunch(@"C:\c.exe", "id-1", "haiku", "low", @"C:\w", "sys", "echo");

        Assert.Equal(
            ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--include-partial-messages", "--verbose",
             "--session-id", "id-1", "--tools", "", "--strict-mcp-config", "--mcp-config", "{cfg}", "--allowedTools", "mcp__neon",
             "--permission-prompts", "none", "--disable-slash-commands", "--restricted", "--system-prompt-snapshot", "off",
             "--system-prompt-file", "P.md", "--model", "haiku", "--effort", "low"],
            ClaudeArguments.BuildServer(launch, resume: false, "P.md", "{cfg}"));
        Assert.Equal("--resume", ClaudeArguments.BuildServer(launch with { Effort = null }, resume: true, "P.md", "{cfg}")[7]);
        Assert.DoesNotContain("--effort", ClaudeArguments.BuildServer(launch with { Effort = null }, resume: true, "P.md", "{cfg}"));
    }

    [Fact]
    public void BuildOneShot_NoToolsNoSession()
    {
        var arguments = ClaudeArguments.BuildOneShot("sonnet", null, "P.md");

        Assert.Equal(["--tools", ""], arguments.SkipWhile(a => a != "--tools").Take(2));
        Assert.Contains("--no-session-persistence", arguments);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.DoesNotContain("--mcp-config", arguments);
        Assert.DoesNotContain("--input-format", arguments);
    }

    [Fact]
    public void McpConfig_OneStdioServer_TheAppsOwnRelay()
    {
        var config = JsonNode.Parse(ClaudeArguments.McpConfig(@"C:\app\NeonSidekick.exe", McpRelay.Arguments("127.0.0.1:5000", "key")))!;

        var neon = config["mcpServers"]!["neon"]!;
        Assert.Equal("stdio", neon["type"]!.GetValue<string>());
        Assert.Equal(@"C:\app\NeonSidekick.exe", neon["command"]!.GetValue<string>());
        Assert.Equal(["--mcp-relay", "127.0.0.1:5000", "key"], neon["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.True(McpRelay.Asked(["--mcp-relay", "127.0.0.1:5000", "key"]));
        Assert.False(McpRelay.Asked(["--mcp-relay"]));
    }

    // ── the client ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Client_TheModelsToolCall_RunsInTheAppsLoop_AndItsResultGoesBackToTheCli()
    {
        await using var host = new FakeClaudeServerHost();
        var answer = new TaskCompletionSource<ClaudeToolAnswer>();
        PlayToolTurn(host, answer);
        var echo = new EchoTool();
        using var client = Client(host);
        var assistant = new Assistant(client, new ConversationHistory("be brief"), LlmTimeouts.Default, tools: [echo]) { ConversationId = SessionId };

        var events = await RunAsync(assistant, "Call the echo tool with hi");

        Assert.Equal(["hi"], echo.Received);   // the app's own tool, run by the app's own loop
        var call = Assert.Single(events.OfType<TurnEvent.ToolCall>());
        Assert.Equal((EchoCallId, "echo"), (call.CallId, call.Name));   // the CLI's id, the app's name
        var got = await answer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("echo: hi", false), (got.Text, got.IsError));
        Assert.Empty(got.Images);
        Assert.Equal("echo: hi", Reply(events));
        Assert.DoesNotContain(events, e => e is TurnEvent.Notice { IsError: true });

        var (launch, resume, tools) = Assert.Single(host.Starts);
        Assert.False(resume);   // a first turn opens the session
        Assert.Equal((SessionId, "haiku", "echo"), (launch.SessionId, launch.Model, launch.ToolNames));
        Assert.Equal("be brief\n\n" + ClaudeCliText.ToolNamesNote, launch.SystemPrompt);
        Assert.Same(echo, Assert.Single(tools));
        var written = Assert.Single(host.Written);
        Assert.Equal(SessionId, JsonNode.Parse(written)!["session_id"]!.GetValue<string>());
        Assert.Equal("Call the echo tool with hi", UserContent(written)[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Client_TheNextTurn_KeepsTheProcess_AndSendsOnlyTheNewMessage()
    {
        await using var host = new FakeClaudeServerHost();
        PlayToolTurn(host, new TaskCompletionSource<ClaudeToolAnswer>());
        using var client = Client(host);
        var assistant = new Assistant(client, new ConversationHistory("be brief"), LlmTimeouts.Default, tools: [new EchoTool()]) { ConversationId = SessionId };
        await RunAsync(assistant, "Call the echo tool with hi");
        var (_, _, second) = ToolTurns();
        host.OnWrite = line =>
        {
            host.Feed(second);
            return Task.CompletedTask;
        };

        var events = await RunAsync(assistant, "What did it return?");

        Assert.Single(host.Starts);   // the same process
        Assert.Equal(2, host.Written.Count);
        var content = UserContent(host.Written[1]).AsArray();
        Assert.Equal("What did it return?", Assert.Single(content)!["text"]!.GetValue<string>());   // the new message alone
        Assert.Contains("echo: hi", Reply(events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_ChangedTools_RestartTheCli_ResumingTheSession()
    {
        await using var host = new FakeClaudeServerHost();
        PlayToolTurn(host, new TaskCompletionSource<ClaudeToolAnswer>());
        using var client = Client(host);
        var assistant = new Assistant(client, new ConversationHistory("be brief"), LlmTimeouts.Default, tools: [new EchoTool()]) { ConversationId = SessionId };
        await RunAsync(assistant, "Call the echo tool with hi");
        var (_, _, second) = ToolTurns();
        host.OnWrite = _ =>
        {
            host.Feed(second);
            return Task.CompletedTask;
        };
        assistant.Tools = [new EchoTool(), new FixedTool("clock", "noon")];

        await RunAsync(assistant, "again");

        Assert.Equal(2, host.Starts.Count);
        Assert.True(host.Starts[1].Resume);
        Assert.Equal("echo,clock", host.Starts[1].Launch.ToolNames);
    }

    [Fact]
    public async Task Client_ASessionTheCliLost_StartsOverWithTheConversationSoFar()
    {
        await using var host = new FakeClaudeServerHost();
        host.OnWrite = _ =>
        {
            // The first start resumed a session the CLI does not have; the second starts it fresh and replies.
            host.Feed(host.Starts.Count == 1 ? Fixture("resume-missing.jsonl") : Fixture("text-reply.jsonl"));
            return Task.CompletedTask;
        };
        using var client = Client(host);
        var history = new ConversationHistory("be brief");
        history.AddUser("what is 2+2");
        history.AddAssistant("four");
        var assistant = new Assistant(client, history, LlmTimeouts.Default) { ConversationId = SessionId };

        var events = await RunAsync(assistant, "and 3+3?");

        Assert.Equal([true, false], host.Starts.Select(s => s.Resume));
        Assert.Equal(1, host.Stops);
        var texts = UserContent(host.Written[1]).AsArray().Select(b => b!["text"]!.GetValue<string>()).ToList();
        Assert.Equal([ClaudeCliText.PreambleHeader + "\n\nUser: what is 2+2\n\nAssistant: four", "and 3+3?"], texts);
        Assert.Equal("hello there", Reply(events));
    }

    [Fact]
    public async Task Client_ACancel_InterruptsTheCli_AndTheNextTurnRunsOnTheSameProcess()
    {
        await using var host = new FakeClaudeServerHost();
        var spike = Fixture("server-interrupt.jsonl");
        string init = spike.First(l => l.Contains("\"subtype\":\"init\"", StringComparison.Ordinal));
        string answered = spike.First(l => l.Contains("\"control_response\"", StringComparison.Ordinal));
        string aborted = spike.First(l => l.Contains("aborted_streaming", StringComparison.Ordinal));
        host.OnWrite = line =>
        {
            if (IsInterruptLine(line))
            {
                host.Feed(answered, aborted);
            }
            else if (host.Written.Count(IsUserLine) == 1)
            {
                host.Feed(init, """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Once upon"}}}""");
            }
            else
            {
                host.Feed(Fixture("text-reply.jsonl"));
            }

            return Task.CompletedTask;
        };
        using var client = Client(host);
        using var cts = new CancellationTokenSource();
        var options = new ChatOptions { ConversationId = SessionId };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "tell a story")], options, cts.Token))
            {
                if (update.Text.Length > 0)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.Contains(host.Written, IsInterruptLine);
        Assert.Equal(0, host.Stops);   // the CLI answered: kept
        var next = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "tell a story"), new ChatMessage(ChatRole.Assistant, "Once upon"), new ChatMessage(ChatRole.User, "hi")], options);
        Assert.Equal("hello there", next.Text);
        Assert.Single(host.Starts);
    }

    [Fact]
    public async Task Client_ACancelTheCliDoesNotAnswer_StopsIt()
    {
        await using var host = new FakeClaudeServerHost();
        string init = Fixture("server-interrupt.jsonl").First(l => l.Contains("\"subtype\":\"init\"", StringComparison.Ordinal));
        host.OnWrite = line =>
        {
            if (IsUserLine(line))
            {
                host.Feed(init, """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Once"}}}""");
            }

            return Task.CompletedTask;   // the interrupt goes unanswered
        };
        using var client = Client(host);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")], new ChatOptions { ConversationId = SessionId }, cts.Token))
            {
                cts.Cancel();
            }
        });

        Assert.Equal(1, host.Stops);   // after the grace: the next turn resumes on a new process
        Assert.False(host.Running);
    }

    [Fact]
    public async Task Client_TheCliEndsMidTurn_IsAModelErrorWithItsStderr()
    {
        await using var host = new FakeClaudeServerHost();
        host.OnWrite = _ =>
        {
            host.StderrTail = "Invalid API key · Please run /login";
            host.ExitCode = 1;
            host.End();
            return Task.CompletedTask;
        };
        using var client = Client(host);
        var assistant = new Assistant(client, new ConversationHistory("sys"), LlmTimeouts.Default) { ConversationId = SessionId };

        var events = await RunAsync(assistant, "hi");

        var notice = Assert.Single(events.OfType<TurnEvent.Notice>());
        Assert.True(notice.IsError);
        Assert.Contains(ClaudeCliText.EndedError(1, "Invalid API key · Please run /login"), notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_ARequestWithNoConversation_IsAskedBesideTheChat()
    {
        await using var host = new FakeClaudeServerHost();
        var capture = new ProcessLaunchCapture();
        using var client = Client(host, _ => Events(new ClaudeEvent.TextDelta("Adding numbers"), FakeClaudeCli.Ok("x")), capture);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.System, "Title this chat."), new ChatMessage(ChatRole.User, "what is 2+2")]);

        Assert.Equal("Adding numbers", response.Text);
        Assert.Equal("what is 2+2", capture.Prompt);   // a lone user message as it is
        Assert.Contains("--no-session-persistence", capture.Launch!.ArgumentList!);
        Assert.Equal(@"C:\home\claude-cli", capture.Launch.WorkingDirectory);
        Assert.Empty(host.Starts);   // the chat's CLI is never asked
        Assert.Empty(host.Written);
    }

    [Fact]
    public void Client_TheEffortWords_AreTheCliS()
    {
        Assert.Null(ClaudeCliChatClient.EffortOf(null));
        Assert.Null(ClaudeCliChatClient.EffortOf(new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } }));
        Assert.Equal("xhigh", ClaudeCliChatClient.EffortOf(new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh } }));
    }

    [Fact]
    public void Flatten_AConversation_IsLabelledLines()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "a"),
            new(ChatRole.Assistant, "b"),
            new(ChatRole.Tool, [new FunctionResultContent("c", "r")]),
        };

        Assert.Equal("User: a\n\nAssistant: b\n\nTool result: r", ClaudeCliText.Flatten(messages));
    }

    // ── the MCP server and its relay ────────────────────────────────────────

    [Fact]
    public async Task McpServer_ThroughTheRelay_ListsTheTools_AndHandsACallOverWithItsId()
    {
        ClaudeToolCall? seen = null;
        await using var server = ClaudeMcpServer.Start(() => [new EchoTool()], (call, _) =>
        {
            seen = call;
            return Task.FromResult(new ClaudeToolAnswer("answered", [new DataContent(new byte[] { 1, 2, 3 }, "image/png")], false));
        });
        var stdin = new Pipe();
        var stdout = new Pipe();
        var relay = McpRelay.RunAsync(server.Address, server.Token, stdin.Reader.AsStream(), stdout.Writer.AsStream(), Stream.Null);

        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(stdin.Writer.AsStream(), stdout.Reader.AsStream())))
        {
            var tools = await client.ListToolsAsync();
            Assert.Equal(["echo"], tools.Select(t => t.Name));

            var result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "echo",
                Arguments = new Dictionary<string, JsonElement> { ["text"] = JsonDocument.Parse("\"hi\"").RootElement.Clone() },
                Meta = new JsonObject { [ClaudeMcpServer.ToolUseIdMetaKey] = "toolu_x" },
            });

            Assert.Equal("answered", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.Equal("image/png", Assert.IsType<ImageContentBlock>(result.Content[1]).MimeType);
            Assert.NotEqual(true, result.IsError);
        }

        Assert.Equal(("toolu_x", "echo", "hi"), (seen!.ToolUseId, seen.Name, seen.Arguments["text"].GetString()));
        stdin.Writer.Complete();
        Assert.Equal(0, await relay.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task McpServer_AConnectionWithTheWrongKey_IsClosed()
    {
        await using var server = ClaudeMcpServer.Start(() => [], (_, _) => throw new InvalidOperationException("never"));
        string[] parts = server.Address.Split(':');
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(new string('0', 64) + "\n"));

        var buffer = new byte[16];
        int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Relay_NoListener_SaysSoAndFails()
    {
        using var error = new MemoryStream();

        int code = await McpRelay.RunAsync("nowhere", "key", Stream.Null, Stream.Null, error);

        Assert.Equal(1, code);
        Assert.Equal(ClaudeCliText.RelayBadAddress("nowhere"), Encoding.UTF8.GetString(error.ToArray()).Trim());
    }

    [Fact]
    public void McpList_ANameTooLongWithThePrefix_IsLeftOff()
    {
        var listed = ClaudeMcpServer.ListOf([new EchoTool(), new FixedTool(new string('x', ClaudeMcpServer.MaxToolNameLength), "t")]);

        Assert.Equal(["echo"], listed.Select(t => t.Name));
    }

    // ── the endpoint, the settings, the stored id ───────────────────────────

    [Fact]
    public void Endpoint_TheAliasAndTheSentinel()
    {
        Assert.Equal(ClaudeCliEndpoint.BaseUrl, LlmEndpoint.NormalizeBaseUrl("claude-cli"));
        Assert.Equal("http://claude-cli.localhost/v1", ClaudeCliEndpoint.BaseUrl.AbsoluteUri);
        Assert.True(ClaudeCliEndpoint.IsClaudeCli("CLAUDE-CLI"));
        Assert.True(ClaudeCliEndpoint.IsClaudeCli("http://claude-cli.localhost/v1"));
        Assert.False(ClaudeCliEndpoint.IsClaudeCli("http://127.0.0.1:1234/v1"));
        Assert.Equal("Claude CLI", LlmServer.NameFor(ClaudeCliEndpoint.BaseUrl, null));
        Assert.Equal(("sonnet", "opus"), (ClaudeCliEndpoint.ModelOf(" "), ClaudeCliEndpoint.ModelOf(" opus ")));
    }

    [Fact]
    public void Offered_TheSwitchAndTheCli()
    {
        string cli = Path.Combine(Path.GetTempPath(), "claude.exe");
        bool Exists(string path) => path == cli;

        Assert.False(ClaudeCliEndpoint.Offered(new AppSettingsData { ClaudeExecutable = cli }, _ => null, Exists));   // off by default
        Assert.True(ClaudeCliEndpoint.Offered(new AppSettingsData { ClaudeCliServer = true, ClaudeExecutable = cli }, _ => null, Exists));
        Assert.False(ClaudeCliEndpoint.Offered(new AppSettingsData { ClaudeCliServer = true, ClaudeExecutable = @"C:\gone\claude.exe" }, _ => null, Exists));
    }

    [Fact]
    public void Setting_TheSwitchRow_AndItsVariable()
    {
        Assert.Equal("Claude CLI server", SettingsMenu.FieldName(SettingsField.ClaudeCliServer));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ClaudeCliServer));
        Assert.True(SettingsMenu.IsLlmField(SettingsField.ClaudeCliServer));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ClaudeCliServer, new AppSettingsData(), @"C:\p"));
        Assert.Equal(SettingsField.ClaudeCliServer, SettingsMenu.ToolsTabFields[ToolsText.TabTitles.ToList().IndexOf(ToolsText.ClaudeTabTitle) - 1][^1]);
        Assert.True(AppSettings.Copy(new AppSettingsData { ClaudeCliServer = true }).ClaudeCliServer);

        var environment = new EnvironmentOverrides(name => name == EnvironmentOverrides.ClaudeCliServerVariable ? "on" : null);
        Assert.True(environment.ClaudeCliServer);
        Assert.Equal("NEONSIDEKICK_CLAUDE_CLI_SERVER", EnvironmentOverrides.ClaudeCliServerVariable);
        Assert.Contains(EnvironmentOverrides.ClaudeCliServerVariable, environment.ActiveVariables());
    }

    [Fact]
    public void StoredHistory_KeepsTheServersSession()
    {
        string json = SessionHistory.ToJson([new ChatMessage(ChatRole.User, "hi")], claudeServerSessionId: SessionId);

        SessionHistory.FromJson(json, out _, out _, out _, out _, out string? server);

        Assert.Equal(SessionId, server);
        Assert.DoesNotContain("ClaudeServerSessionId", SessionHistory.ToJson([new ChatMessage(ChatRole.User, "hi")]), StringComparison.Ordinal);   // absent until used
    }

    // ── the session ─────────────────────────────────────────────────────────

    private sealed class SessionRig : IAsyncDisposable
    {
        public StubHttpMessageHandler Http { get; } = new();

        public FakeClaudeServerHost Host { get; } = new();

        public List<LlmEndpoint> Endpoints { get; } = new();

        public bool Offered { get; set; } = true;

        public LlmSession Session { get; }

        public SessionRig()
        {
            Session = new LlmSession(
                new LlmEndpointProbe(new HttpClient(Http), TimeSpan.FromMilliseconds(200)),
                new ContextLengthProbe(new HttpClient(Http), TimeSpan.FromMilliseconds(200)),
                (endpoint, _) =>
                {
                    Endpoints.Add(endpoint);
                    return new FakeChatClient();
                },
                claudeServer: Host,
                claudeCliOffered: _ => Offered);
        }

        public async ValueTask DisposeAsync()
        {
            Session.Dispose();
            await Host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Session_ASavedClaudeCliUrl_ConnectsAskingNoOne()
    {
        await using var rig = new SessionRig();

        Assert.True(await rig.Session.ConnectAsync(new AppSettingsData { LlmUrl = "claude-cli", LlmModel = "opus", LlmScanMode = "disabled" }, CancellationToken.None));

        var endpoint = Assert.Single(rig.Endpoints);
        Assert.Equal((ClaudeCliEndpoint.BaseUrl, "opus"), (endpoint.BaseUrl, endpoint.ModelId));
        Assert.Equal(new ContextLength(200_000, LlmSession.ClaudeCliSource), rig.Session.ContextLength);
        Assert.Empty(rig.Http.Requests);   // no probe, no context tiers
        Assert.Equal(["sonnet", "fable", "opus", "haiku"], (await rig.Session.ListModelsAsync(CancellationToken.None))!.Value.ModelIds);   // the default first
        Assert.Null(await rig.Session.ServerSamplingAsync(new AppSettingsData(), CancellationToken.None));
    }

    [Fact]
    public async Task Session_TheRow_OnlyWhileOffered()
    {
        await using var rig = new SessionRig();

        var row = Assert.Single(rig.Session.ClaudeCliRows(new AppSettingsData()));
        Assert.Equal((ClaudeCliEndpoint.BaseUrl, "Claude CLI", true), (row.BaseUrl, row.Name, row.Result.Exists));
        Assert.Equal(ClaudeCliEndpoint.Models, row.Result.ModelIds);
        rig.Offered = false;
        Assert.Empty(rig.Session.ClaudeCliRows(new AppSettingsData()));
    }

    [Fact]
    public async Task Session_ASavedUrlNotOffered_IsABlankOne()
    {
        await using var rig = new SessionRig { Offered = false };

        await rig.Session.ConnectAsync(new AppSettingsData { LlmUrl = "claude-cli", LlmScanMode = "disabled" }, CancellationToken.None);

        Assert.Null(rig.Session.Endpoint);   // a blank URL with the scan disabled finds nothing
        Assert.Empty(rig.Endpoints);
    }

    [Fact]
    public async Task Session_AnotherServerPicked_StopsTheCli()
    {
        await using var rig = new SessionRig();
        await rig.Host.EnsureRunningAsync(new ClaudeServerLaunch("c", SessionId, "haiku", null, "w", "s", ""), false, [], CancellationToken.None);

        rig.Session.Connect(new AppSettingsData(), new LlmEndpoint(new Uri("http://127.0.0.1:1234/v1"), "m", "k", "configured"));

        Assert.Equal(1, rig.Host.Stops);
        Assert.False(rig.Host.Running);
    }
}
