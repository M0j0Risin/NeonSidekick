using NeonSidekick.App;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SpeakReadingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeSynthesizer _synth = new();
    private readonly FakeAudioPlayback _playback = new();

    private SpeechOutput Output(CancellationToken token) => new(_synth, _playback, "af_heart", 1.0, token);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met in time");
    }

    // ── Split ───────────────────────────────────────────────────────────────

    [Fact]
    public void Split_IsTheSpeechChunking_WithEachSentencesOffset()
    {
        string text = "# Title\n\nFirst one. Second one!\n---\n- a bullet\n- another\n\nThe tail";

        var sentences = SpeakReading.Split(text);

        // A newline is a boundary, so a heading and a bullet are sentences; the rule line is dropped; the tail comes from the flush.
        Assert.Equal(["# Title", "First one.", "Second one!", "- a bullet", "- another", "The tail"], sentences.Select(s => s.Text));
        Assert.All(sentences, s => Assert.Equal(s.Text, text.Substring(s.Offset, s.Text.Length)));
        Assert.Equal(0, sentences[0].Offset);
        Assert.Equal(text.IndexOf("Second", StringComparison.Ordinal), sentences[2].Offset);
        Assert.Equal(text.Length - "The tail".Length, sentences[^1].Offset);
    }

    [Fact]
    public void Split_LeavesOutTheFencedCodeBlocks_OffsetsStillVerbatim()
    {
        string text = "Run this:\n```bash\necho Hi.\nls\n```\nThen this.\n~~~\nopen\n";

        var sentences = SpeakReading.Split(text);

        Assert.Equal(["Run this:", "Then this."], sentences.Select(s => s.Text));
        Assert.All(sentences, s => Assert.Equal(s.Text, text.Substring(s.Offset, s.Text.Length)));
    }

    [Fact]
    public void Split_ARunOnSentence_BreaksAtTheChunkerLimit_AndTheSameWordTwiceKeepsOrder()
    {
        string words = string.Join(" ", Enumerable.Repeat("word", 70));   // 349 chars, no terminator
        var sentences = SpeakReading.Split(words);

        Assert.Equal(2, sentences.Count);
        Assert.True(sentences[0].Text.Length <= SentenceChunker.DefaultMaxChunkChars);
        Assert.Equal(0, sentences[0].Offset);
        Assert.Equal(sentences[0].Text.Length + 1, sentences[1].Offset);   // the next word, past the space
        Assert.Equal(words.Length, sentences[1].Offset + sentences[1].Text.Length);

        Assert.Empty(SpeakReading.Split(""));
        Assert.Empty(SpeakReading.Split("---\n\n"));
    }

    [Theory]
    [InlineData("5", true, 5)]
    [InlineData(" 12 ", true, 12)]
    [InlineData("0", true, 0)]
    [InlineData("notes.md", false, 0)]
    [InlineData("5a", false, 0)]
    [InlineData("-3", false, 0)]
    [InlineData("", false, 0)]
    public void TryParsePosition_IsDigitsAlone(string argument, bool expected, int position)
    {
        Assert.Equal(expected, SpeakReading.TryParsePosition(argument, out int at));
        Assert.Equal(position, at);
    }

    [Theory]
    [InlineData("notes.txt 5", true, "notes.txt", 5)]
    [InlineData("  docs/a b.md   12 ", true, "docs/a b.md", 12)]
    [InlineData("notes.txt 0", true, "notes.txt", 0)]   // the caller reports it as beyond the end
    [InlineData("5", false, "", 0)]                      // no head: a bare position
    [InlineData("notes.txt", false, "", 0)]
    [InlineData("notes.txt 5a", false, "", 0)]
    [InlineData("", false, "", 0)]
    public void TrySplitPosition_IsAHeadAndADigitsTail(string argument, bool expected, string head, int position)
    {
        Assert.Equal(expected, SpeakReading.TrySplitPosition(argument, out string h, out int at));
        Assert.Equal(head, h);
        Assert.Equal(position, at);
    }

    // ── Position, Resume, StatusLine ────────────────────────────────────────

    [Fact]
    public void Lines_ArePinned()
    {
        // The user's shape (2026-09-17): the book, the count, the verb, the file's name alone.
        Assert.Equal("📖 92/181 reading book.txt", SpeakReading.ReadingLine("docs/book.txt", 92, 181));
        Assert.Equal("📖 3/40 stopped notes.md", SpeakReading.StoppedLine(@"deep\er\notes.md", 3, 40));
        Assert.Equal("📖 40/40 read notes.md", SpeakReading.ReadLine("notes.md", 40));
        Assert.Equal("\U0001F4D6", SpeakReading.Glyph);
        Assert.Equal("notes.md", SpeakReading.Name("notes.md"));
        Assert.Equal("notes.md", SpeakReading.Name("a/b/notes.md"));
        // An echo (2026-09-17): the balloon, the count, the verb, no file.
        Assert.Equal("💬 1/2 speaking", SpeakReading.SpeakingLine(1, 2));
        Assert.Equal("💬 1/2 stopped", SpeakReading.EchoStoppedLine(1, 2));
        Assert.Equal("💬 2/2 spoken", SpeakReading.SpokenLine(2));
        Assert.Equal("\U0001F4AC", SpeakReading.EchoGlyph);
    }

    [Fact]
    public async Task AnEcho_ReadsSpeaking_Stopped_AndSpoken()
    {
        _playback.HoldBytes = true;
        using var cts = new CancellationTokenSource();
        var echo = new SpeakReading("", "One. Two.") { Source = SpeakReading.Kind.Echo };
        Assert.Equal(SpeakReading.Kind.Echo, echo.Source);
        Assert.Equal("💬 2/2 spoken", echo.StatusLine());   // speech off: done
        var speaker = Output(cts.Token);
        speaker.Speak("One.");
        speaker.Speak("Two.");
        speaker.CompleteAdding();
        echo.Started(1, speaker);
        await WaitUntilAsync(() => speaker.ChunksStarted == 2 && _playback.Writes.Count == 2);

        Assert.Equal("💬 1/2 speaking", echo.StatusLine());
        cts.Cancel();
        await speaker.Completion.WaitAsync(Timeout);
        Assert.Equal("💬 1/2 stopped", echo.StatusLine());
    }

    [Fact]
    public void WithSpeechOff_TheReadingIsDone_AndResumesAtTheTop()
    {
        var reading = new SpeakReading("notes.md", "One. Two. Three.");

        Assert.Equal(3, reading.Count);
        Assert.True(reading.Done);
        Assert.False(reading.Playing);
        Assert.Equal(1, reading.Resume);
        reading.Started(2, null);
        Assert.Equal(2, reading.From);
        Assert.Equal(3, reading.Position);
        Assert.Equal("📖 3/3 read notes.md", reading.StatusLine());
        Assert.Throws<ArgumentOutOfRangeException>(() => reading.Started(0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => reading.Started(4, null));
    }

    [Fact]
    public async Task Position_FollowsThePlayHead_FromTheStartSentence_AndAStopKeepsIt()
    {
        _playback.HoldBytes = true;
        using var cts = new CancellationTokenSource();
        var reading = new SpeakReading("notes.md", "One. Two. Three. Four.");
        var speaker = Output(cts.Token);
        // A seek to sentence 2: the speaker gets 2, 3 and 4.
        for (int i = 1; i < reading.Count; i++)
        {
            speaker.Speak(reading.Sentences[i].Text);
        }

        speaker.CompleteAdding();
        reading.Started(2, speaker);
        await WaitUntilAsync(() => speaker.ChunksStarted == 3 && _playback.Writes.Count == 3);

        Assert.True(reading.Playing);
        Assert.Equal(2, reading.Position);               // chunk 1 of the speaker is sentence 2
        Assert.Equal("📖 2/4 reading notes.md", reading.StatusLine());
        _playback.Release(4800 + 1);
        Assert.Equal(3, reading.Position);
        Assert.Equal("📖 3/4 reading notes.md", reading.StatusLine());

        cts.Cancel();                                     // stopped mid-sentence 3
        await speaker.Completion.WaitAsync(Timeout);

        Assert.False(reading.Playing);
        Assert.False(reading.Done);
        Assert.Equal(3, reading.Position);
        Assert.Equal(3, reading.Resume);
        Assert.Equal("📖 3/4 stopped notes.md", reading.StatusLine());
    }

    [Fact]
    public async Task AFullRead_IsDone_AndResumesAtTheTop()
    {
        using var cts = new CancellationTokenSource();
        var reading = new SpeakReading("notes.md", "One. Two.");
        var speaker = Output(cts.Token);
        speaker.Speak("One.");
        speaker.Speak("Two.");
        speaker.CompleteAdding();
        reading.Started(1, speaker);
        await speaker.Completion.WaitAsync(Timeout);

        Assert.True(reading.Done);
        Assert.Equal(2, reading.Position);
        Assert.Equal(1, reading.Resume);
        Assert.Equal("📖 2/2 read notes.md", reading.StatusLine());
    }
}
