using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SessionTextTests
{
    private static readonly TimeZoneInfo Zone = ManualTimeProvider.DefaultZone;
    private static readonly DateTimeOffset At = ManualTimeProvider.DefaultUtcNow;   // 2026-09-11 14:05 in the zone

    private static SessionSummary Summary(long id = 12, string title = "Vosk wiring", int turns = 12) => new(id, At, At, title, TitleSource.FirstLine, "llama", turns);

    [Theory]
    [InlineData("How do I wire the Vosk model?", "How do I wire the Vosk model?")]
    [InlineData("\n\n  first   line \nsecond", "first line")]
    [InlineData("   ", "(untitled)")]
    [InlineData("", "(untitled)")]
    public void FirstLineTitle_IsTheFirstNonBlankLine_Collapsed(string text, string expected) => Assert.Equal(expected, SessionText.FirstLineTitle(text));

    [Fact]
    public void FirstLineTitle_CutsAtSixtyWithAnEllipsis()
    {
        string title = SessionText.FirstLineTitle(new string('x', 70));
        Assert.Equal(60, title.Length);
        Assert.EndsWith("…", title);
        Assert.Equal(60, SessionText.MaxTitleChars);
    }

    [Theory]
    [InlineData("Vosk wake word wiring", "vosk-wake-word-wiring")]
    [InlineData("\"Vosk wake word wiring\"", "vosk-wake-word-wiring")]
    [InlineData("Pip the Frog's Secret Rhythm", "pip-the-frogs-secret-rhythm")]
    [InlineData("Title: Vosk wiring.", "vosk-wiring")]
    [InlineData("**Vosk wiring**\n\nBecause it fits.", "vosk-wiring")]
    [InlineData("\n\n'Dinner plans'  ", "dinner-plans")]
    [InlineData("Ünïcode café — 2 tricks!", "ünïcode-café-2-tricks")]
    [InlineData("already-a-slug", "already-a-slug")]
    [InlineData("--- ---", null)]
    [InlineData("\"\"", null)]
    [InlineData("   ", null)]
    public void CleanTitle_IsALowerCaseKebabSlug(string text, string? expected) => Assert.Equal(expected, SessionText.CleanTitle(text));

    [Fact]
    public void Slug_CutsAtSixty_WithNoHyphenAtTheCut()
    {
        string words = string.Join(' ', Enumerable.Repeat("abcdefghi", 8));   // "abcdefghi abcdefghi …", the 60th character a space
        string slug = SessionText.Slug(words)!;
        Assert.True(slug.Length <= SessionText.MaxTitleChars);
        Assert.False(slug.EndsWith('-'));
        Assert.Equal(59, slug.Length);   // the 60-cut lands on a hyphen, trimmed
        Assert.Null(SessionText.Slug("’'"));
    }

    [Fact]
    public void TitleRequest_AndInstruction_ArePinned()
    {
        Assert.Equal("User: hello\n\nAssistant: hi there", SessionText.TitleRequest("  hello ", "hi there\n"));
        Assert.Equal(600, SessionText.TitleSampleChars);
        string request = SessionText.TitleRequest(new string('u', 700), new string('a', 700));
        Assert.Equal("User: " + new string('u', 599) + "…\n\nAssistant: " + new string('a', 599) + "…", request);
        Assert.Equal("Write a title of at most six words for the conversation below, in the language it is in. Answer with the title alone: no quotes, no full stop, no explanation.", SessionText.TitleInstruction);
    }

    [Fact]
    public void Labels_ArePinned()
    {
        Assert.Equal("2026-09-11 14:05", SessionText.Moment(At, Zone));
        Assert.Equal("#12", SessionText.Id(12));
        Assert.Equal("1 turn", SessionText.Turns(1));
        Assert.Equal("12 turns", SessionText.Turns(12));
        Assert.Equal("1 session", SessionText.Sessions(1));
        Assert.Equal("3 sessions", SessionText.Sessions(3));
        Assert.Equal("#12 · 2026-09-11 14:05 · 12 turns · Vosk wiring", SessionText.Label(Summary(), Zone));
        Assert.Equal("1 tool call", SessionText.ToolCallsNote(1));
        Assert.Equal("3 tool calls", SessionText.ToolCallsNote(3));
    }

    // ---- ages (2026-09-21) ----

    [Theory]
    [InlineData("30", 30 * 86400)]
    [InlineData("0", 0)]
    [InlineData("  7  ", 7 * 86400)]
    [InlineData("2d", 2 * 86400)]
    [InlineData("2 days", 2 * 86400)]
    [InlineData("1 day", 86400)]
    [InlineData("12h", 12 * 3600)]
    [InlineData("12 hours", 12 * 3600)]
    [InlineData("1hr", 3600)]
    [InlineData("90m", 5400)]
    [InlineData("45 min", 2700)]
    [InlineData("5 minutes", 300)]
    [InlineData("1d 6h", 30 * 3600)]
    [InlineData("1h30m", 5400)]
    [InlineData("2d12h30m", 2 * 86400 + 12 * 3600 + 1800)]
    [InlineData("30s", 30)]
    [InlineData("1D", 86400)]
    public void TryParseAge_Accepts(string text, int seconds)
    {
        Assert.True(SessionText.TryParseAge(text, out var age));
        Assert.Equal(TimeSpan.FromSeconds(seconds), age);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-1")]
    [InlineData("ten")]
    [InlineData("0h")]
    [InlineData("0d")]
    [InlineData("1h 1h")]
    [InlineData("1d 1 day")]
    [InlineData("1x")]
    [InlineData("1.5d")]
    [InlineData("1234567")]
    [InlineData("1234567d")]
    [InlineData("2 days now")]
    public void TryParseAge_Refuses(string text)
    {
        Assert.False(SessionText.TryParseAge(text, out var age));
        Assert.Equal(TimeSpan.Zero, age);
    }

    [Theory]
    [InlineData(30 * 86400, "30 days")]
    [InlineData(86400, "1 day")]
    [InlineData(0, "0 days")]
    [InlineData(12 * 3600, "12 hours")]
    [InlineData(3600, "1 hour")]
    [InlineData(30 * 3600, "1 day 6 hours")]
    [InlineData(2700, "45 minutes")]
    [InlineData(60, "1 minute")]
    [InlineData(5410, "1 hour 30 minutes 10 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(86400 + 1, "1 day 1 second")]
    public void Age_IsPinned(int seconds, string expected)
    {
        Assert.Equal(expected, SessionText.Age(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void SearchResults_AreTheHeaderThenALabelAndASnippetPerHit()
    {
        var hits = new List<SessionHit>
        {
            new(Summary(12, "Vosk wiring", 12), 3, "pass the\nmodel's   folder…"),
            new(Summary(7, "Dinner", 1), 1, "…pasta"),
        };

        Assert.Equal(
            "Searched \"vosk\" (2 sessions):\n" +
            "#12 · 2026-09-11 14:05 · 12 turns · Vosk wiring\n   turn 3: pass the model's folder…\n" +
            "#7 · 2026-09-11 14:05 · 1 turn · Dinner\n   turn 1: …pasta",
            SessionText.SearchResults("vosk", hits, Zone));
        Assert.Equal("Searched \"vosk\": no earlier session matches", SessionText.SearchResults("vosk", [], Zone));
        Assert.Equal("Searched \"q\" (1 session):", SessionText.SearchHeader("q", 1));
    }

    [Fact]
    public void ListResults_AreTheHeaderThenALabelPerSession()
    {
        Assert.Equal("Sessions, newest first (2 sessions):\n#12 · 2026-09-11 14:05 · 12 turns · Vosk wiring\n#7 · 2026-09-11 14:05 · 1 turn · Dinner",
            SessionText.ListResults([Summary(), Summary(7, "Dinner", 1)], Zone));
        Assert.Equal("No earlier sessions are stored.", SessionText.ListResults([], Zone));
        Assert.Equal(SessionText.NoSessions, SessionText.ListResults([], Zone));
    }

    [Fact]
    public void Read_IsTheHeaderThenTheTurns_ClampedToWhatIsHeld()
    {
        var record = new SessionRecord(Summary(12, "Vosk wiring", 3), [
            new SessionTurn(1, At, " hello ", "Hi.\n", 0, [], [], 0, 1, 1, false),
            new SessionTurn(2, At, "again", "Again.", 2, [], [], 0, 1, 1, true),
            new SessionTurn(3, At, "bye", "Bye.", 0, [], [], 0, 1, 1, false),
        ], "");

        Assert.Equal(
            "Session #12 \"Vosk wiring\" (2026-09-11 14:05, 3 turns), turns 1–3:\n\n" +
            "Turn 1\nYou: hello\nNeon: Hi.\n\n" +
            "Turn 2 (cut short)\nYou: again\nNeon: Again.\n\n" +
            "Turn 3\nYou: bye\nNeon: Bye.",
            SessionText.Read(record, 1, 0, Zone));
        Assert.StartsWith("Session #12 \"Vosk wiring\" (2026-09-11 14:05, 3 turns), turns 2–2:\n\nTurn 2", SessionText.Read(record, 2, 2, Zone));
        Assert.StartsWith("Session #12 \"Vosk wiring\" (2026-09-11 14:05, 3 turns), turns 1–3:", SessionText.Read(record, -5, 99, Zone));
        Assert.Equal("Error: session #12 has 3 turns; from_turn is past the end", SessionText.Read(record, 4, 0, Zone));
    }

    [Fact]
    public void Read_CutsAtTheCap_AndNamesTheTurnsLeftOut()
    {
        var turns = Enumerable.Range(1, 12).Select(i => new SessionTurn(i, At, "q" + i, new string('r', 1500), 0, [], [], 0, 0, 0, false)).ToList();
        var record = new SessionRecord(Summary(12, "Long", 12), turns, "");

        string text = SessionText.Read(record, 1, 0, Zone);

        Assert.True(text.Length <= SessionText.MaxReadChars + 80);
        Assert.Contains("\n\nTurn 7\n", text);
        Assert.DoesNotContain("\n\nTurn 8\n", text);
        Assert.EndsWith("\n\n… (turns 8–12 cut; call read with from_turn 8)", text);
        Assert.Equal("… (turns 7–12 cut; call read with from_turn 7)", SessionText.ReadCut(7, 12));
        Assert.Equal(12_000, SessionText.MaxReadChars);
    }

    [Fact]
    public void Errors_ArePinned()
    {
        Assert.Equal("Error: no session #12; call list or search first", SessionText.Missing(12));
        Assert.Equal("Error: 'purge' is not one of search, list, read for 'action'", SessionText.BadAction(" purge "));
        Assert.Equal("Error: max_results must be 1 to 20", SessionText.BadResultCount(1, 20));
        Assert.Equal("abc", SessionText.Cut("abc", 3));
        Assert.Equal("ab…", SessionText.Cut("abcd", 3));
    }

    [Fact]
    public void SearchWords_AreTheLongerWords_LowerCased_Distinct_InOrder_Capped()
    {
        // The reflection's evidence search (2026-09-19): runs of letters and digits of four or more, at most twelve.
        Assert.Equal(["deploy", "docker", "stack", "again", "please"], SessionText.SearchWords("Deploy the Docker stack again, please! deploy"));
        Assert.Equal(["vosk", "0123"], SessionText.SearchWords("hi vosk & 0123 - ok"));
        Assert.Empty(SessionText.SearchWords("hi, ok?"));
        Assert.Empty(SessionText.SearchWords(""));
        Assert.Equal(12, SessionText.SearchWords(string.Join(' ', Enumerable.Range(0, 20).Select(i => "word" + i))).Count);
        Assert.Equal(["éclair"], SessionText.SearchWords("ÉCLAIR"));   // letters, not ASCII alone
        Assert.Equal(12, SessionText.MaxSearchWords);
        Assert.Equal(4, SessionText.MinSearchWordLength);
    }

    [Fact]
    public void Read_WithAMax_CutsThere()
    {
        var turns = Enumerable.Range(1, 4).Select(i => new SessionTurn(i, At, "q" + i, new string('r', 400), 0, [], [], 0, 0, 0, false)).ToList();
        var record = new SessionRecord(Summary(3, "Long", 4), turns, "");

        string text = SessionText.Read(record, 1, 0, Zone, 1_000);

        Assert.Contains("Turn 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Turn 3\n", text, StringComparison.Ordinal);
        Assert.EndsWith(SessionText.ReadCut(3, 4), text, StringComparison.Ordinal);
        Assert.Equal(SessionText.Read(record, 1, 0, Zone), SessionText.Read(record, 1, 0, Zone, SessionText.MaxReadChars));
    }

    /// <summary>A model-written title shows as words (2026-10-04, the UI review); a first-line or typed one as it is; the store keeps the slug.</summary>
    [Fact]
    public void DisplayTitle_ShowsAModelSlugAsWords()
    {
        Assert.Equal("Summary of notes file", SessionText.Words("summary-of-notes-file"));
        Assert.Equal("", SessionText.Words("-"));
        Assert.Equal("Vosk model wiring", SessionText.DisplayTitle(Summary(title: "vosk-model-wiring") with { TitleSource = TitleSource.Model }));
        Assert.Equal("half-done notes", SessionText.DisplayTitle(Summary(title: "half-done notes")));
        Assert.Equal("my-notes", SessionText.DisplayTitle(Summary(title: "my-notes") with { TitleSource = TitleSource.User }));
    }
}
