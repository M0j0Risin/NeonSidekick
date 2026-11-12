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

        Assert.Equal(MidiSaveOutcome.Saved, file.SetCommand(1, "/new"));
        Assert.Equal(MidiSaveOutcome.Failed, file.SetCommand(5, "/new"));
        Assert.Equal(["/clear", "/new"], file.Load().Bindings.Select(b => b.Command));

        // A relearn onto another binding's button takes its place; the other goes.
        Assert.Equal(MidiSaveOutcome.Saved, file.SetTrigger(1, Pedal1));
        Assert.Equal([new MidiBinding(Pedal1, "/new")], file.Load().Bindings);

        Assert.Equal(MidiSaveOutcome.Saved, file.Remove(0));
        Assert.Empty(file.Load().Bindings);
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
