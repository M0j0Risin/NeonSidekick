using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SessionManagerToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly SessionStore _store;
    private readonly AppSettingsData _settings = new();
    private long? _current;
    private readonly SessionManagerTool _tool;

    public SessionManagerToolTests()
    {
        _store = new SessionStore(_dir, _time);
        _tool = new SessionManagerTool(_store, () => _settings, () => _current, _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var (name, value) in values)
        {
            dictionary[name] = value;
        }

        return new AIFunctionArguments(dictionary);
    }

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<string> Invoke(params (string Name, object? Value)[] values) =>
        (string)(await _tool.InvokeAsync(Args(values), CancellationToken.None))!;

    private long Seed(string title, params (string User, string Reply)[] turns)
    {
        long id = _store.Begin(title, "llama")!.Value;
        foreach (var (user, reply) in turns)
        {
            _store.AppendTurn(id, user, reply, 0, [], [], 0, 1, 1, false);
        }

        _time.Advance(TimeSpan.FromMinutes(1));
        return id;
    }

    [Fact]
    public void Schema_NamesTheActionAsAnEnum_AndTheRest()
    {
        Assert.Equal("session_manager", _tool.Name);
        Assert.Equal("object", _tool.JsonSchema.GetProperty("type").GetString());
        var properties = _tool.JsonSchema.GetProperty("properties");
        Assert.Equal(new[] { "action", "query", "id", "max_results", "from_turn", "to_turn" }, properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "search", "list", "read" }, properties.GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("integer", properties.GetProperty("id").GetProperty("type").GetString());
        Assert.Equal(new[] { "action" }, _tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("earlier conversations", _tool.Description);
        Assert.Contains("not included", _tool.Description);
    }

    [Fact]
    public async Task Search_AnswersTheHits_WithTheCurrentSessionLeftOut()
    {
        long a = Seed("Vosk wiring", ("how do I wire the vosk model", "pass the folder"));
        long b = Seed("Dinner", ("what is for dinner", "pasta"));
        long c = Seed("Now", ("vosk once more", "the vosk model"));
        _current = c;

        string result = await Invoke(("action", "search"), ("query", "vosk"));

        Assert.StartsWith("Searched \"vosk\" (1 session):\n#" + a + " · ", result);
        Assert.DoesNotContain("#" + c + " ", result);
        Assert.DoesNotContain("#" + b + " ", result);
        Assert.Contains("\n   turn 1: ", result);
        Assert.Equal("Searched \"vosk\" (1 session):", SessionManagerTool.Note(result));
        _current = null;
        Assert.StartsWith("Searched \"vosk\" (2 sessions):", await Invoke(("action", "SEARCH"), ("query", "vosk")));
        Assert.Equal("Searched \"zebra\": no earlier session matches", await Invoke(("action", "search"), ("query", "zebra")));
    }

    [Fact]
    public async Task Search_NeedsAQuery_AndAnInRangeCount()
    {
        Assert.Equal(SessionText.NoQuery, await Invoke(("action", "search")));
        Assert.Equal(SessionText.NoQuery, await Invoke(("action", "search"), ("query", "  ")));
        Assert.Equal("Error: max_results must be 1 to 20", await Invoke(("action", "search"), ("query", "x"), ("max_results", Json("0"))));
        Assert.Equal("Error: max_results must be 1 to 20", await Invoke(("action", "search"), ("query", "x"), ("max_results", Json("21"))));
        Assert.Equal(ClockText.BadInteger("max_results", "lots"), await Invoke(("action", "search"), ("query", "x"), ("max_results", "lots")));
    }

    [Fact]
    public async Task Search_TheSettingIsTheDefaultCount_TheArgumentOverridesIt()
    {
        for (int i = 0; i < 5; i++)
        {
            Seed("s" + i, ("vosk " + i, "reply"));
        }

        _settings.SessionSearchMaxResults = 2;
        Assert.StartsWith("Searched \"vosk\" (2 sessions):", await Invoke(("action", "search"), ("query", "vosk")));
        Assert.StartsWith("Searched \"vosk\" (4 sessions):", await Invoke(("action", "search"), ("query", "vosk"), ("max_results", Json("4"))));
        Assert.Equal(10, SessionManagerTool.DefaultCount(new AppSettingsData()));
        Assert.Equal(20, SessionManagerTool.DefaultCount(new AppSettingsData { SessionSearchMaxResults = 99 }));   // a hand-edited value is clamped
        Assert.Equal(1, SessionManagerTool.DefaultCount(new AppSettingsData { SessionSearchMaxResults = 0 }));
    }

    [Fact]
    public async Task List_IsTheNewestFirst_WithTheCurrentLeftOut_UpToTheCount()
    {
        long a = Seed("a", ("x", "y"));
        long b = Seed("b", ("x", "y"));
        long c = Seed("c", ("x", "y"));
        _current = c;
        _settings.SessionSearchMaxResults = 1;

        string result = await Invoke(("action", "list"));

        Assert.Equal("Sessions, newest first (1 session):\n#" + b + " · 2026-09-11 14:06 · 1 turn · b", result);
        Assert.Equal("Sessions, newest first (2 sessions):\n#" + b + " · 2026-09-11 14:06 · 1 turn · b\n#" + a + " · 2026-09-11 14:05 · 1 turn · a", await Invoke(("action", "list"), ("max_results", Json("5"))));
        _store.PurgeAll();
        Assert.Equal(SessionText.NoSessions, await Invoke(("action", "list")));
    }

    [Fact]
    public async Task Read_AnswersTheTurns_ByIdAndRange()
    {
        long id = Seed("Vosk wiring", ("hello", "Hi."), ("again", "Again."));

        string result = await Invoke(("action", "read"), ("id", Json(id.ToString())));

        Assert.Equal("Session #" + id + " \"Vosk wiring\" (2026-09-11 14:05, 2 turns), turns 1–2:\n\nTurn 1\nYou: hello\nNeon: Hi.\n\nTurn 2\nYou: again\nNeon: Again.", result);
        Assert.Equal("Session #" + id + " \"Vosk wiring\" (2026-09-11 14:05, 2 turns), turns 2–2:\n\nTurn 2\nYou: again\nNeon: Again.", await Invoke(("action", "read"), ("id", id), ("from_turn", Json("2"))));
        Assert.Equal("Session #" + id + " \"Vosk wiring\" (2026-09-11 14:05, 2 turns), turns 1–1:\n\nTurn 1\nYou: hello\nNeon: Hi.", await Invoke(("action", "read"), ("id", id.ToString()), ("to_turn", Json("1"))));
        Assert.Equal("Session #" + id + " \"Vosk wiring\" (2026-09-11 14:05, 2 turns), turns 1–2:", SessionManagerTool.Note(result));
        Assert.Equal(SessionText.Missing(999), await Invoke(("action", "read"), ("id", Json("999"))));
        Assert.Equal(SessionText.NoId, await Invoke(("action", "read")));
        Assert.Equal(ClockText.BadInteger("id", "twelve"), await Invoke(("action", "read"), ("id", "twelve")));
        Assert.Equal(ClockText.BadInteger("from_turn", "x"), await Invoke(("action", "read"), ("id", id), ("from_turn", "x")));
        Assert.Equal(ClockText.BadInteger("to_turn", "y"), await Invoke(("action", "read"), ("id", id), ("to_turn", "y")));
    }

    [Fact]
    public async Task AnUnknownAction_IsAnErrorSentence()
    {
        Assert.Equal("Error: 'purge' is not one of search, list, read for 'action'", await Invoke(("action", "purge")));
        Assert.Equal("Error: '' is not one of search, list, read for 'action'", await Invoke());
    }

    /// <summary>Only search and list read max_results (2026-11-15, the tool-arguments review's #153): read refuses it.</summary>
    [Fact]
    public async Task MaxResults_OnRead_IsRefused()
    {
        long id = Seed("Notes", ("hi", "hello"));
        Assert.Equal(ArgumentText.NotReadBy("max_results", "read", "search and list"), await Invoke(("action", "read"), ("id", id), ("max_results", 3)));
        Assert.DoesNotContain("Error", await Invoke(("action", "list"), ("max_results", 3)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _tool.InvokeAsync(Args(("action", "list")), cts.Token));
    }

    [Fact]
    public void Note_IsTheFirstLine()
    {
        Assert.Equal("one", SessionManagerTool.Note("one\ntwo"));
        Assert.Equal("only", SessionManagerTool.Note("only"));
    }
}
