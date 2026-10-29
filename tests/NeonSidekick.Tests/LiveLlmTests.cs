using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

/// <summary>
/// One real round trip. Skips unless a server is reachable (see <see cref="LiveLlmServer"/>);
/// proves the wire path the fakes cannot: streaming, the model name, the bearer header.
/// </summary>
[Collection("LiveLlm")]
public class LiveLlmTests
{
    [LiveLlmFact]
    public async Task RealServer_StreamsANonBlankReply()
    {
        var endpoint = LiveLlmServer.Endpoint!;
        using var client = new OpenAICompatibleChatClient(endpoint, TimeSpan.FromSeconds(60));
        var assistant = new Assistant(client, new ConversationHistory(Assistant.DefaultSystemPrompt), new LlmTimeouts(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90)));

        var deltas = new List<string>();
        var notices = new List<TurnEvent.Notice>();
        await foreach (var evt in assistant.RunTurnAsync("Reply with the single word: pong"))
        {
            if (evt is TurnEvent.TextDelta d) deltas.Add(d.Text);
            if (evt is TurnEvent.Notice n) notices.Add(n);
        }

        Assert.Empty(notices);
        Assert.NotEmpty(deltas);
        Assert.False(string.IsNullOrWhiteSpace(string.Concat(deltas)));
        Assert.Equal(2, assistant.History.Messages.Count);
    }

    /// <summary>
    /// An image part reaches the server as an <c>image_url</c> data URL: a solid pink bitmap
    /// through <see cref="ImageFile.Load"/> and the model asked for its colour. A text-only model
    /// answers with the server's error, which is a notice here — the test then reports it and
    /// stops short of judging the colour (the wire shape is what it proves).
    /// </summary>
    [LiveLlmFact]
    public async Task RealServer_TakesAnImagePart()
    {
        var endpoint = LiveLlmServer.Endpoint!;
        using var client = new OpenAICompatibleChatClient(endpoint, TimeSpan.FromSeconds(90));
        var assistant = new Assistant(client, new ConversationHistory(Assistant.DefaultSystemPrompt), new LlmTimeouts(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120)));
        string path = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "live-" + Guid.NewGuid().ToString("N") + ".bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, SmokeChecks.SolidBmp(64, 64));
        try
        {
            var image = ImageFile.Load(path, out string? error);
            Assert.Null(error);
            Assert.NotNull(image);

            var deltas = new List<string>();
            var notices = new List<TurnEvent.Notice>();
            await foreach (var evt in assistant.RunTurnAsync("What color is [Image #1]? Answer with one word.", [image]))
            {
                if (evt is TurnEvent.TextDelta d) deltas.Add(d.Text);
                if (evt is TurnEvent.Notice n) notices.Add(n);
            }

            if (notices.Count > 0)
            {
                // A text-only model: the request was well-formed, the server just cannot see.
                Assert.Contains(notices, n => n.Text.Contains("image", StringComparison.OrdinalIgnoreCase) || n.Text.Contains("400", StringComparison.Ordinal));
                return;
            }

            string reply = string.Concat(deltas);
            Assert.False(string.IsNullOrWhiteSpace(reply));
            string[] colours = ["pink", "magenta", "purple", "violet", "red", "fuchsia", "rose"];
            Assert.True(colours.Any(reply.ToLowerInvariant().Contains), "not a color: " + reply);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A model asked for a spreadsheet calls <c>convert_to_xlsx</c> with a table (2026-10-29, the Office writing plan): the tool's schema is
    /// one a real model fills, and the workbook it makes passes the validator.
    /// </summary>
    [LiveLlmFact]
    public async Task RealServer_MakesAWorkbook_ThroughConvertToXlsx()
    {
        var endpoint = LiveLlmServer.Endpoint!;
        string root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "live-xlsx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new WorkingDirectory(() => root, TimeProvider.System);
            using var client = new OpenAICompatibleChatClient(endpoint, TimeSpan.FromSeconds(180));
            var assistant = new Assistant(client, new ConversationHistory(Assistant.DefaultSystemPrompt), new LlmTimeouts(TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(240)))
            {
                Tools = [new Llm.Tools.ConvertToXlsxTool(files, TimeProvider.System)],
            };

            var results = new List<TurnEvent.ToolResult>();
            await foreach (var evt in assistant.RunTurnAsync(
                "Make a spreadsheet named fruit.xlsx with the columns Fruit and Price and three rows: apple 1.25, pear 0.8, plum 2. Use your tool."))
            {
                if (evt is TurnEvent.ToolResult r) results.Add(r);
            }

            var made = Assert.Single(results, r => r.Name == Llm.Tools.ConvertToXlsxTool.ToolName && r.Text.StartsWith("Made ", StringComparison.Ordinal));
            string path = Assert.Single(Directory.GetFiles(root, "*.xlsx", SearchOption.AllDirectories));
            Fakes.OfficeSchema.ValidWorkbook(File.ReadAllBytes(path));
            Assert.Contains("3 rows × 2 columns", made.Text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// <c>/test</c>'s reasoning and structured-output tests against the real server (2026-09-28): every one asked and
    /// judged, none an error — the verdicts are the model's, so not asserted — and the schema tests' <c>response_format</c>
    /// taken by the server.
    /// </summary>
    [LiveLlmFact]
    public async Task RealServer_RunsTheShortBenchTests_WithoutAnError()
    {
        var endpoint = LiveLlmServer.Endpoint!;
        using var client = new OpenAICompatibleChatClient(endpoint, TimeSpan.FromSeconds(180));
        var assistant = new Assistant(client, new ConversationHistory(Assistant.DefaultSystemPrompt), new LlmTimeouts(TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(240)));
        var tests = Bench.BenchCatalog.All.Where(t => t.Category != Bench.BenchCategory.LongContext).ToList();

        var results = await Bench.BenchRunner.RunAsync(assistant, tests, new Bench.BenchContext(null), claudeApi: false, TimeProvider.System, null, null, CancellationToken.None);

        Assert.Equal(tests.Count, results.Count);
        Assert.All(results, r => Assert.True(r.Verdict is Bench.BenchVerdict.Pass or Bench.BenchVerdict.Fail, r.Test + ": " + r.Reason));
    }
}
