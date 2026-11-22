using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/usage</c> › Models (2026-11-21, feature idea #7): the saved turns grouped by the model that answered, with the session's model for
/// a turn from before the column; side turns as rows of their own; tok/s over the timed turns alone; the error rate capped at the calls.
/// </summary>
public sealed class ModelStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly SessionStore _store;

    public ModelStatsTests() => _store = new SessionStore(_dir, _time);

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static TokenUsage Timed(long output, double seconds) =>
        new(Input: 1, Output: output, Total: output + 1, Requests: 1, ToFirstToken: TimeSpan.Zero, Generating: TimeSpan.FromSeconds(seconds));

    private void Turn(long id, string? model, int calls = 0, int errors = 0, long output = 10, TokenUsage? usage = null, bool cancelled = false)
    {
        _store.AppendTurn(id, "q", "r", calls, [], [], errors, 1, output, cancelled, model: model, usage: usage);
        _time.Advance(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void TheTurns_GroupByTheModelThatAnswered_NewestUsedFirst()
    {
        long a = _store.Begin("a", "qwen3")!.Value;
        Turn(a, null, calls: 4, errors: 1, output: 100, usage: Timed(100, 4));     // from before the column: the session's model
        Turn(a, "qwen3", calls: 2, errors: 0, output: 50, usage: Timed(50, 1));
        Turn(a, "/claude", output: 7);                                              // a side turn: its own row
        long b = _store.Begin("b", "gemma")!.Value;
        Turn(b, "gemma", calls: 0, errors: 1, output: 30, cancelled: true);         // a failed bot turn with no calls: no error rate
        Turn(b, "qwen3", output: 40, usage: new TokenUsage(1, 40, 41, 1, TimeSpan.Zero, TimeSpan.Zero));   // untimed: out of the speed

        var stats = _store.ModelStats();

        Assert.Equal(["qwen3", "gemma", "/claude"], stats.Select(s => s.Model));
        var qwen = stats[0];
        Assert.Equal((3, 2, 6L, 1L, 190L), (qwen.Turns, qwen.Sessions, qwen.ToolCalls, qwen.Errors, qwen.Output));
        Assert.Equal(1, qwen.Untagged);
        Assert.Equal(150.0 / 5, qwen.TokensPerSecond);   // the two timed turns: 150 tokens over 5 s
        Assert.Equal(1.0 / 6, qwen.ErrorRate);
        var gemma = stats[1];
        Assert.Null(gemma.ErrorRate);
        Assert.Equal(1.0, gemma.CancelledRate);
        Assert.Null(gemma.TokensPerSecond);
        Assert.Equal((1, 0), (stats[2].Turns, stats[2].Untagged));
    }

    [Fact]
    public void TheTab_ShowsARowAModel_FewTurnsDim_LongIdsCutInTheMiddleAndWrittenWhole_AndNoRowWraps()
    {
        string longId = "unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF:UD-Q4_K_XL";
        var stats = new List<ModelStat>
        {
            new(longId, 25, 3, 50, 5, 1, 2500, 2000, 40_000, DateTimeOffset.UnixEpoch, 0),
            new("gemma", 2, 1, 0, 0, 0, 20, 0, 0, DateTimeOffset.UnixEpoch, 2),
        };

        foreach (int width in new[] { 120, UsageText.ModelsModelMin + UsageText.ModelsFiguresWidth })   // the pane's narrowest the table fits
        {
            var rows = UsageText.ModelsRows(stats, logging: true, width);
            Assert.All(rows.Skip(2).Take(stats.Count + 1), r => Assert.True(TextCells.Width(r.Text) <= width, r.Text));   // the heading and the rows never wrap
            Assert.Equal((UsageText.ModelsCaption, ""), (rows[0].Text, rows[1].Text));
            Assert.StartsWith(UsageText.ModelsModelHeading, rows[2].Text);
            var (longRow, longDim) = rows[3];   // the caption, a gap, the heading, then a row a model
            Assert.False(longDim);
            Assert.Contains("50.0", longRow);   // 2000 tokens over 40 s
            Assert.Contains("10%", longRow);    // 5 of 50 calls
            Assert.EndsWith("50.0", longRow);
            var (gemma, gemmaDim) = rows[4];
            Assert.StartsWith("gemma ", gemma);
            Assert.True(gemmaDim);
            Assert.EndsWith("—", gemma);        // no speed
            Assert.Contains(rows, r => r.Text == UsageText.ModelsUntaggedNote);
            Assert.Contains(rows, r => r.Text == UsageText.ModelsFewNote);
            int modelWidth = Math.Max(UsageText.ModelsModelMin, width - UsageText.ModelsFiguresWidth);
            Assert.Equal(width < 120 ? UsageText.CutMiddle(longId, modelWidth) : longId, longRow[..modelWidth].TrimEnd());
            Assert.Equal(width < 120, rows.Any(r => r.Text == longId && r.Dim));   // a cut id written whole under the table
            Assert.Equal(width < 120, longRow.Contains('…'));
        }
    }

    [Fact]
    public void TheTab_WithNothingSaved_SaysWhereTheFiguresComeFrom_AndThatLoggingIsOff()
    {
        Assert.Equal([UsageText.ModelsEmpty], UsageText.ModelsRows([], logging: true, 120).Select(r => r.Text));
        Assert.Equal([UsageText.ModelsEmpty, UsageText.ModelsLoggingOff], UsageText.ModelsRows([], logging: false, 120).Select(r => r.Text));
        Assert.Equal(UsageText.ModelsTabIndex, ChatScreen.UsageTab("Models"));
        Assert.Equal("Qwen…K_XL", UsageText.CutMiddle("Qwen3-Coder-Q4_K_XL", 9));
        Assert.Equal("short", UsageText.CutMiddle("short", 9));
    }
}
