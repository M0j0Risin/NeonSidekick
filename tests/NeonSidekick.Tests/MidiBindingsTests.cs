using NeonSidekick.Midi;

namespace NeonSidekick.Tests;

/// <summary>
/// The profile's <c>midi.json</c> (2026-11-12, the MIDI plan, <see cref="MidiBindings"/>): a round trip, a button bound again replaced in
/// place, a relearn that takes another binding's button, a remove, the hand-edited entries skipped (and kept until a save), a duplicate,
/// a broken file moved aside at the next save, a newer version's left alone, and <c>/profile push|pull midi</c>'s import.
/// </summary>
public sealed class MidiBindingsTests : IDisposable
{
    private static readonly MidiTrigger Pedal1 = new("FS-4", 0, MidiKind.ControlChange, 1, 80);
    private static readonly MidiTrigger Pedal2 = new("FS-4", 0, MidiKind.ControlChange, 1, 81);
    private static readonly MidiTrigger Pad = new("Pad", 1, MidiKind.Note, 10, 36);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FilePath => Settings.Profiles.ConfigPath(_dir, MidiBindings.FileName);

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, json);
    }

    [Fact]
    public void NoFile_IsNoBindings_AndABindRoundTrips()
    {
        var file = new MidiBindings(_dir);
        Assert.Same(MidiBindingSet.Empty, file.Load());

        Assert.Equal(MidiSaveOutcome.Saved, file.Bind(Pedal1, " /tts toggle "));
        Assert.Equal(MidiSaveOutcome.Saved, file.Bind(Pad, "/camera snap"));
        var set = new MidiBindings(_dir).Load();
        Assert.Equal(MidiFileState.Ok, set.State);
        Assert.Equal([new MidiBinding(Pedal1, "/tts toggle"), new MidiBinding(Pad, "/camera snap")], set.Bindings);
        Assert.Equal("/camera snap", set.Find(Pad with { Device = "2- PAD" })?.Command);

        string json = File.ReadAllText(FilePath);
        Assert.Contains("\"kind\": \"cc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"occurrence\": 1", json, StringComparison.Ordinal);
        Assert.Equal(1, json.Split("occurrence").Length - 1);   // the first of its name writes none
    }

    [Fact]
    public void BindingAButtonAgain_ReplacesItInPlace_AndEditsKeepTheOrder()
    {
        var file = new MidiBindings(_dir);
        file.Bind(Pedal1, "/tts");
        file.Bind(Pedal2, "/voice");
        file.Bind(Pedal1, "/clear");
        Assert.Equal(["/clear", "/voice"], file.Load().Bindings.Select(b => b.Command));

        Assert.Equal(MidiSaveOutcome.Saved, file.SetCommand(new MidiBinding(Pedal2, "/voice"), "/new"));
        Assert.Equal(MidiSaveOutcome.Gone, file.SetCommand(new MidiBinding(Pad, "/x"), "/new"));   // a button the file doesn't bind
        Assert.Equal(["/clear", "/new"], file.Load().Bindings.Select(b => b.Command));

        // A relearn onto another binding's button takes its place; the other goes.
        Assert.Equal(MidiSaveOutcome.Saved, file.SetTrigger(new MidiBinding(Pedal2, "/new"), Pedal1));
        Assert.Equal([new MidiBinding(Pedal1, "/new")], file.Load().Bindings);

        Assert.Equal(MidiSaveOutcome.Saved, file.Remove(new MidiBinding(Pedal1, "/new")));
        Assert.Empty(file.Load().Bindings);
        Assert.Equal(MidiSaveOutcome.Gone, file.Remove(new MidiBinding(Pedal1, "/new")));
    }

    /// <summary>
    /// The pane's edits find the binding by its button in the file as it is now (2026-11-14, the review's #9): a hand edit between the pane's
    /// read and its Del removes the binding shown, never the one that moved to its place.
    /// </summary>
    [Fact]
    public void AnEdit_AfterAHandEdit_ChangesTheBindingShown_NotTheOneAtItsOldPlace()
    {
        var file = new MidiBindings(_dir);
        file.Bind(Pedal1, "/tts");
        file.Bind(Pedal2, "/voice");
        var shown = file.Load().Bindings[1];   // Pedal2, second on the pane

        // By hand: Pad put first, so Pedal2 is third now.
        WriteFile("""
            { "version": 1, "bindings": [
              { "device": "PAD", "kind": "note", "channel": 10, "number": 36, "command": "/camera snap" },
              { "device": "FS-4", "kind": "cc", "channel": 1, "number": 80, "command": "/tts" },
              { "device": "FS-4", "kind": "cc", "channel": 1, "number": 81, "command": "/voice" } ] }
            """);

        Assert.Equal(MidiSaveOutcome.Saved, file.Remove(shown));
        Assert.Equal(["/camera snap", "/tts"], file.Load().Bindings.Select(b => b.Command));
    }

    /// <summary>
    /// <c>/midi forget</c> and an import without overwrite keep the entries the pane would have shown as skipped (2026-11-14, the review's #40
    /// leftover); an import with overwrite replaces the whole list, those too.
    /// </summary>
    [Fact]
    public void ForgetAndImport_KeepTheSkippedEntries_ButOverwriteDropsThem()
    {
        const string withTypo = """
            { "version": 1, "bindings": [
              { "device": "FS-4", "kind": "cc", "channel": 1, "number": 80, "command": "/tts" },
              { "device": "FS-4", "kind": "pitch", "channel": 1, "number": 81, "command": "/typo" } ] }
            """;
        var file = new MidiBindings(_dir);

        WriteFile(withTypo);
        Assert.Equal(MidiSaveOutcome.Saved, file.Forget(0));
        Assert.Empty(file.Load().Bindings);
        Assert.Equal([MidiText.SkippedKind("pitch")], file.Load().Skipped);

        WriteFile(withTypo);
        Assert.Equal(MidiImportOutcome.Done, file.Import([new MidiBinding(Pad, "/camera snap")], overwrite: false).Outcome);
        Assert.Equal(2, file.Load().Bindings.Count);
        Assert.Equal([MidiText.SkippedKind("pitch")], file.Load().Skipped);

        WriteFile(withTypo);
        Assert.Equal(MidiImportOutcome.Done, file.Import([new MidiBinding(Pad, "/camera snap")], overwrite: true).Outcome);
        Assert.Equal([new MidiBinding(Pad, "/camera snap")], file.Load().Bindings);
        Assert.Empty(file.Load().Skipped);

        // The pane's own save drops them, as it showed them.
        WriteFile(withTypo);
        Assert.Equal(MidiSaveOutcome.Saved, file.Bind(Pad, "/camera snap"));
        Assert.Empty(file.Load().Skipped);
    }

    [Fact]
    public void HandEditedEntries_AreSkippedWithAReason_AndKeptUntilASave()
    {
        WriteFile("""
            {
              // a hand edit
              "version": 1,
              "bindings": [
                { "device": "FS-4", "kind": "cc", "channel": 1, "number": 80, "command": "/tts" },
                { "device": "FS-4", "kind": "pitch", "channel": 1, "number": 81, "command": "/tts" },
                { "device": "FS-4", "kind": "cc", "channel": 17, "number": 81, "command": "/tts" },
                { "device": "FS-4", "kind": "cc", "channel": 1, "number": 128, "command": "/tts" },
                { "device": "", "kind": "note", "channel": 1, "number": 1, "command": "/tts" },
                { "device": "FS-4", "kind": "cc", "channel": 1, "number": 82, "command": "tts" },
                { "device": "FS-4", "occurrence": -1, "kind": "cc", "channel": 1, "number": 83, "command": "/tts" },
                { "device": "2- fs-4", "kind": "CC", "channel": 1, "number": 80, "command": "/voice" },
                null,
              ],
            }
            """);
        var set = new MidiBindings(_dir).Load();
        Assert.Equal(MidiFileState.Ok, set.State);
        Assert.Equal([new MidiBinding(Pedal1, "/tts")], set.Bindings);
        Assert.Equal(
        [
            MidiText.SkippedKind("pitch"),
            MidiText.SkippedChannel(17),
            MidiText.SkippedNumber(128),
            MidiText.SkippedDevice,
            MidiText.SkippedCommand("tts"),
            MidiText.SkippedOccurrence(-1),
            MidiText.SkippedDuplicate("CC 80 · ch 1 · fs-4"),
            MidiText.SkippedEmpty,
        ], set.Skipped);
        Assert.Contains("pitch", File.ReadAllText(FilePath), StringComparison.Ordinal);   // read, never rewritten
    }

    [Fact]
    public void ABrokenFile_ReadsAsNone_AndIsMovedAsideAtTheNextSave()
    {
        WriteFile("{ not json");
        var file = new MidiBindings(_dir);
        Assert.Equal(MidiFileState.Broken, file.Load().State);
        Assert.Equal(MidiSaveOutcome.Saved, file.Bind(Pedal1, "/tts"));
        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".bad"));
        Assert.Single(file.Load().Bindings);
    }

    [Fact]
    public void ANewerVersionsFile_IsLeftAlone_AndRefusesEveryChange()
    {
        const string newer = """{ "version": 2, "bindings": [] }""";
        WriteFile(newer);
        var file = new MidiBindings(_dir);
        Assert.Equal(MidiFileState.Newer, file.Load().State);
        Assert.Equal(MidiSaveOutcome.Newer, file.Bind(Pedal1, "/tts"));
        Assert.Equal(MidiImportOutcome.Unreadable, file.Import([new MidiBinding(Pedal1, "/tts")], overwrite: false).Outcome);
        Assert.Equal(newer, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Import_AddsTheNewButtons_KeepsTheTargetsOwn_OrReplacesTheListWithOverwrite()
    {
        var target = new MidiBindings(_dir);
        target.Bind(Pedal1, "/tts");
        MidiBinding[] incoming = [new(Pedal1, "/voice"), new(Pad, "/camera snap")];

        Assert.Equal(new MidiImport(MidiImportOutcome.Done, 1, 1), target.Import(incoming, overwrite: false));
        Assert.Equal(["/tts", "/camera snap"], target.Load().Bindings.Select(b => b.Command));

        Assert.Equal(new MidiImport(MidiImportOutcome.Done, 2, 0), target.Import(incoming, overwrite: true));
        Assert.Equal(["/voice", "/camera snap"], target.Load().Bindings.Select(b => b.Command));

        WriteFile("{ broken");
        Assert.Equal(MidiImportOutcome.Unreadable, target.Import(incoming, overwrite: true).Outcome);
        Assert.Equal("{ broken", File.ReadAllText(FilePath));
    }
}
