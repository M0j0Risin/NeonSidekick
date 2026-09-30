using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Claude CLI server against the real CLI (2026-09-30, <see cref="LiveClaudeFactAttribute"/>, Haiku, a few cents): the
/// app's own tool reached through the real relay (this build's <c>NeonSidekick.exe</c>, beside the tests) and run by the
/// app's loop, the process kept across turns, and ESC interrupting a reply with the next turn on the same process — what
/// the fixtures only replay.
/// </summary>
public class LiveClaudeCliServerTests
{
    private static ClaudeServerHost Host() => new(() => [Path.Combine(AppContext.BaseDirectory, "NeonSidekick.exe")]);

    private static ClaudeCliChatClient Client(IClaudeServerHost host, string folder) =>
        new(LlmSession.ClaudeCliEndpointOf("haiku"), host, new ClaudeCliContext(() => LiveClaudeCli.Executable, folder), TimeSpan.FromMinutes(3));

    private static async Task<string> RunAsync(Assistant assistant, string text, Func<TurnEvent, bool>? cancelWhen = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var reply = new System.Text.StringBuilder();
        await foreach (var evt in assistant.RunTurnAsync(text, timeout.Token))
        {
            if (evt is TurnEvent.Notice { IsError: true } notice)
            {
                Assert.Fail(notice.Text);
            }

            if (evt is TurnEvent.TextDelta delta)
            {
                reply.Append(delta.Text);
            }

            if (cancelWhen?.Invoke(evt) == true)
            {
                timeout.Cancel();
            }
        }

        return reply.ToString();
    }

    [LiveClaudeFact]
    public async Task RealCli_RunsTheAppsTool_AndKeepsTheSessionAcrossTurns()
    {
        string folder = Directory.CreateTempSubdirectory("neon-claude-cli-").FullName;
        try
        {
            await using var host = Host();
            using var client = Client(host, folder);
            var echo = new EchoTool();
            var assistant = new Assistant(client, new ConversationHistory("You are a terse test assistant."), LlmTimeouts.Default, tools: [echo]) { ConversationId = Guid.NewGuid().ToString("D") };

            string first = await RunAsync(assistant, "Call the echo tool with the text pelican, then reply with exactly what it returned.");
            string second = await RunAsync(assistant, "Without calling any tool: which word did you echo? Reply with that one word.");

            Assert.Equal(["pelican"], echo.Received);   // the app's tool, run by the app's loop
            Assert.Contains("echo: pelican", first, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pelican", second, StringComparison.OrdinalIgnoreCase);
            Assert.True(host.Running);   // one process for both turns
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [LiveClaudeFact]
    public async Task RealCli_EscInterruptsAReply_AndTheNextTurnRunsOnTheSameProcess()
    {
        string folder = Directory.CreateTempSubdirectory("neon-claude-cli-").FullName;
        try
        {
            await using var host = Host();
            using var client = Client(host, folder);
            var assistant = new Assistant(client, new ConversationHistory("You are a test assistant."), LlmTimeouts.Default) { ConversationId = Guid.NewGuid().ToString("D") };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(assistant, "Write a 600-word story about a lighthouse keeper.", evt => evt is TurnEvent.TextDelta));
            string next = await RunAsync(assistant, "Reply with exactly: ok");

            Assert.Contains("ok", next, StringComparison.OrdinalIgnoreCase);
            Assert.True(host.Running);   // the CLI answered the interrupt: not stopped
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
