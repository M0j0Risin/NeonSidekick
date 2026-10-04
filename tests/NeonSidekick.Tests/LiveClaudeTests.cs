using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/claude</c> against the real CLI (2026-09-27, <see cref="LiveClaudeFactAttribute"/>): one thread of two messages in a
/// temp folder with no tools, the second resuming the first — the round trip the fixtures only replay.
/// </summary>
public class LiveClaudeTests
{
    private static async Task<List<ClaudeEvent>> RunAsync(ClaudeProcess cli, ClaudeRequest request)
    {
        var events = new List<ClaudeEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await foreach (var evt in cli.RunAsync(request, timeout.Token))
        {
            events.Add(evt);
        }

        return events;
    }

    [LiveClaudeFact]
    public async Task RealCli_AnswersStreamed_AndTheSecondMessageResumesTheFirst()
    {
        string folder = Directory.CreateTempSubdirectory("neon-claude-").FullName;
        try
        {
            var cli = new ClaudeProcess(Environment.GetEnvironmentVariable);
            string id = Guid.NewGuid().ToString("D");
            var first = await RunAsync(cli, new ClaudeRequest("Remember the word PELICAN. Reply with exactly: ok", id, false, folder, ClaudePermissionLevel.ReadOnly, Model: "haiku"));
            var second = await RunAsync(cli, new ClaudeRequest("Which word did I ask you to remember? Reply with that one word.", id, true, folder, ClaudePermissionLevel.ReadOnly, Model: "haiku"));

            var one = Assert.IsType<ClaudeEvent.Result>(first[^1]);
            Assert.False(one.IsError, one.Error);
            Assert.Equal(id, one.SessionId);
            Assert.NotEmpty(first.OfType<ClaudeEvent.TextDelta>());
            var two = Assert.IsType<ClaudeEvent.Result>(second[^1]);
            Assert.False(two.IsError, two.Error);
            Assert.Contains("PELICAN", string.Concat(second.OfType<ClaudeEvent.TextDelta>().Select(d => d.Text)), StringComparison.OrdinalIgnoreCase);
            Assert.True(two.CostUsd >= 0m);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary><c>claude_advisor_cli</c> for real (2026-09-27): read-only, Claude reads a file in the working directory to answer.</summary>
    [LiveClaudeFact]
    public async Task RealCli_TheAdvisor_ReadsTheWorkingDirectory_AndAnswers()
    {
        string folder = Directory.CreateTempSubdirectory("neon-advisor-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "answer.txt"), "The code word is HERON.");
            var settings = new AppSettingsData { ClaudeCliAdvisor = true, ClaudeCliAdvisorModel = "haiku" };
            decimal spent = 0m;
            var tool = new Llm.Tools.ClaudeAdvisorTool(new ClaudeProcess(Environment.GetEnvironmentVariable), () => settings, () => folder, new ClaudeAdvisorThread(), (_, usd) => spent += usd, () => []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            var answer = await tool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["question"] = "What is the code word in answer.txt? Reply with the word alone." }, timeout.Token);

            string text = Assert.IsType<string>(answer);
            Assert.True(text.Contains("HERON", StringComparison.OrdinalIgnoreCase), text);
            Assert.True(spent > 0m);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
    }
}
