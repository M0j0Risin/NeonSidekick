using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The tool span's ask clock (2026-11-02, the review: the reply footer's tool times): asks inside a span count once however they overlap,
/// one still open counts to now, a nested span's ask counts up the chain, nothing outside a span counts, and the Assistant stamps each
/// result with its call's time and its asks, the opening calls too.
/// </summary>
public class AskClockTests
{
    [Fact]
    public async Task OverlappingAsks_CountOnce_AndOneStillOpen_CountsToNow()
    {
        var clock = new ManualTimeProvider();
        TimeSpan open = default;
        var (_, asked) = await ToolSpan.RunAsync(clock, () =>
        {
            var span = ToolSpan.Current!;
            using (span.Asking())
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                using (span.Asking())
                {
                    clock.Advance(TimeSpan.FromSeconds(3));   // inside both: counted once
                }

                clock.Advance(TimeSpan.FromSeconds(1));
            }

            clock.Advance(TimeSpan.FromSeconds(10));          // the tool's own work
            var still = span.Asking();
            clock.Advance(TimeSpan.FromSeconds(4));
            open = span.Asked;                                // an ask still open counts to now
            still.Dispose();
            still.Dispose();                                  // a second dispose ends nothing more
            return Task.FromResult(0);
        });

        Assert.Equal(TimeSpan.FromSeconds(10), open);
        Assert.Equal(TimeSpan.FromSeconds(10), asked);
    }

    [Fact]
    public async Task ANestedSpansAsk_CountsOnEverySpanUpTheChain_AndNothingOutsideASpanCounts()
    {
        var clock = new ManualTimeProvider();
        Assert.Null(ToolSpan.Current);
        TimeSpan inner = default;
        var (_, outer) = await ToolSpan.RunAsync(clock, async () =>
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            (_, inner) = await ToolSpan.RunAsync(clock, () =>
            {
                using (ToolSpan.Current!.Asking())
                {
                    clock.Advance(TimeSpan.FromSeconds(30));   // an approval inside the advisor's own tool
                }

                return Task.FromResult(0);
            });

            return 0;
        });

        Assert.Equal(TimeSpan.FromSeconds(30), inner);
        Assert.Equal(TimeSpan.FromSeconds(30), outer);
        Assert.Null(ToolSpan.Current);   // gone once the call returned
    }

    [Fact]
    public async Task TheAssistant_StampsEachResult_WithItsTimeAndItsAsks_TheOpeningCallsToo()
    {
        var clock = new ManualTimeProvider();
        var client = new FakeChatClient();
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)), [new AskingTool(clock, "slow")], clock)
        {
            OpeningCalls = [new(new AskingTool(clock, "opening"), "open1")],
        };
        client.Enqueue(FakeChatClient.Call("c1", "slow", new Dictionary<string, object?>()));
        client.EnqueueText("Done.");

        var results = new List<TurnEvent.ToolResult>();
        await foreach (var evt in assistant.RunTurnAsync("go"))
        {
            if (evt is TurnEvent.ToolResult result)
            {
                results.Add(result);
            }
        }

        Assert.Equal(["opening", "slow"], results.Select(r => r.Name));
        Assert.All(results, r =>
        {
            Assert.Equal(TimeSpan.FromSeconds(9), r.Took);
            Assert.Equal(TimeSpan.FromSeconds(6), r.Asked);
        });
    }

    /// <summary>A tool that works 3 s on the clock and waits 6 s on an ask inside its span.</summary>
    private sealed class AskingTool(ManualTimeProvider clock, string name) : AIFunction
    {
        private static readonly JsonElement Schema = ToolSchema.Parse("""{"type":"object","properties":{}}""");

        public override string Name => name;

        public override string Description => "Asks.";

        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            using (ToolSpan.Current?.Asking())
            {
                clock.Advance(TimeSpan.FromSeconds(6));
            }

            return new("done");
        }
    }
}
