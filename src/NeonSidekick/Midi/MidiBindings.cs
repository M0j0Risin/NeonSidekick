using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Midi;

/// <summary>One learned button and the command line it runs (2026-11-12, the MIDI plan).</summary>
public sealed record MidiBinding(MidiTrigger Trigger, string Command);

/// <summary>
/// The bindings as the file has them (2026-11-12): <paramref name="Bindings"/> in the user's order, <paramref name="Skipped"/> each
/// entry a hand edit left unusable (shown on the pane, never dropped from the file until the user saves), and the file's state.
/// </summary>
public sealed record MidiBindingSet(IReadOnlyList<MidiBinding> Bindings, IReadOnlyList<string> Skipped, MidiFileState State)
{
    public static readonly MidiBindingSet Empty = new([], [], MidiFileState.Ok);

    /// <summary>The binding for <paramref name="trigger"/>'s button, or null.</summary>
    public MidiBinding? Find(MidiTrigger trigger) => Bindings.FirstOrDefault(b => b.Trigger.SameButton(trigger));
}

/// <summary>How <c>midi.json</c> read (2026-11-12).</summary>
public enum MidiFileState
{
    Ok,

    /// <summary>Not JSON, or null: no bindings, and the next save moves it aside to <c>.bad</c> first.</summary>
    Broken,

    /// <summary>From a newer NeonSidekick: left alone, every save refused.</summary>
    Newer,

    /// <summary>There but could not be read (locked, no access): no bindings, every save refused.</summary>
    Unreadable,
}

/// <summary>What a change to the bindings did (2026-11-12).</summary>
public enum MidiSaveOutcome
{
    Saved,

    /// <summary>The file is a newer NeonSidekick's: left alone.</summary>
    Newer,

    /// <summary>The file could not be read or written (logged); nothing changed.</summary>
    Failed,
}

/// <summary>What <see cref="MidiBindings.Import"/> did (2026-11-12, <c>/profile push|pull midi</c>).</summary>
public enum MidiImportOutcome
{
    Done,

    /// <summary>The target's file is there but could not be read, or is a newer NeonSidekick's: left alone, nothing written.</summary>
    Unreadable,

    /// <summary>The file could not be written (logged); nothing changed.</summary>
    Failed,
}

/// <summary>What <see cref="MidiBindings.Import"/> did: its outcome, the bindings added or replaced, and those kept as the target had them.</summary>
public readonly record struct MidiImport(MidiImportOutcome Outcome, int Added, int Skipped);

/// <summary>
/// The profile's MIDI bindings (2026-11-12, the MIDI plan: a learned button and the slash command it runs), in <see cref="FileName"/>
/// beside <c>youtube.json</c>. <c>BrowserLinks</c>' shape: read on each use, a save a temp file moved over the old one, one static lock
/// for every instance (the screen and the board's thread both read), a failure logged and reported, never thrown; a broken file is moved
/// aside to <c>.bad</c> before the first write, and one from a newer version is left alone and refuses every write. An entry a hand edit
/// made unusable (a kind that isn't <c>note</c>, <c>cc</c> or <c>program</c>, a channel outside 1–16, a number outside 0–127, a command
/// that doesn't start with <c>/</c>) is skipped with one warning and listed in <see cref="MidiBindingSet.Skipped"/>; the file keeps it
/// until the user next saves, so a typo is seen before it is lost. The model can't write here: the file is under the home (<c>WriteGuard</c>).
/// </summary>
public sealed class MidiBindings
{
    public const string FileName = "midi.json";

    /// <summary>The file's version; a higher one is a newer NeonSidekick's and is left alone.</summary>
    public const int Version = 1;

    private const string Category = "MIDI";

    private static readonly Lock s_gate = new();

    private readonly string _filePath;

    /// <param name="directory">The profile's directory; the file is <see cref="FileName"/> in its <c>configs</c>.</param>
    public MidiBindings(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _filePath = Settings.Profiles.ConfigPath(Directory, FileName);
    }

    /// <summary>The profile directory the file lives in.</summary>
    public string Directory { get; }

    public string FilePath => _filePath;

    /// <summary>The bindings, the skipped entries and the file's state.</summary>
    public MidiBindingSet Load()
    {
        lock (s_gate)
        {
            return Read().Set;
        }
    }

    /// <summary>
    /// <paramref name="command"/> bound to <paramref name="trigger"/>'s button: the binding it had replaced in place, else a new one last.
    /// The skipped entries go with this save (the pane showed them).
    /// </summary>
    public MidiSaveOutcome Bind(MidiTrigger trigger, string command)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return Change(list =>
        {
            int at = list.FindIndex(b => b.Trigger.SameButton(trigger));
            var binding = new MidiBinding(trigger, command.Trim());
            if (at >= 0)
            {
                list[at] = binding;
            }
            else
            {
                list.Add(binding);
            }

            return true;
        });
    }

    /// <summary>The binding at <paramref name="index"/> (0-based) given <paramref name="command"/> in its place.</summary>
    public MidiSaveOutcome SetCommand(int index, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return Change(list =>
        {
            if (index < 0 || index >= list.Count)
            {
                return false;
            }

            list[index] = list[index] with { Command = command.Trim() };
            return true;
        });
    }

    /// <summary>The binding at <paramref name="index"/> (0-based) given <paramref name="trigger"/>'s button, any other binding of that button removed.</summary>
    public MidiSaveOutcome SetTrigger(int index, MidiTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return Change(list =>
        {
            if (index < 0 || index >= list.Count)
            {
                return false;
            }

            var moved = list[index] with { Trigger = trigger };
            list.RemoveAt(index);
            int other = list.FindIndex(b => b.Trigger.SameButton(trigger));
            if (other >= 0)
            {
                list.RemoveAt(other);
                if (other < index)
                {
                    index--;
                }
            }

            list.Insert(Math.Min(index, list.Count), moved);
            return true;
        });
    }

    /// <summary>The binding at <paramref name="index"/> (0-based) removed.</summary>
    public MidiSaveOutcome Remove(int index) => Change(list =>
    {
        if (index < 0 || index >= list.Count)
        {
            return false;
        }

        list.RemoveAt(index);
        return true;
    });

    /// <summary>
    /// <c>/profile push|pull midi [overwrite]</c> (2026-11-12): <paramref name="bindings"/> (another profile's) added in their order. A
    /// button this file binds already keeps its own command unless <paramref name="overwrite"/>, which replaces the whole list. Unlike
    /// <see cref="Load"/>, a file that is there but can't be read, or a newer version's, is refused and never written over.
    /// </summary>
    public MidiImport Import(IReadOnlyList<MidiBinding> bindings, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        lock (s_gate)
        {
            var file = Read();
            if (file.Set.State is MidiFileState.Broken or MidiFileState.Newer or MidiFileState.Unreadable)
            {
                return new(MidiImportOutcome.Unreadable, 0, 0);
            }

            var kept = overwrite ? [] : file.Set.Bindings.ToList();
            int added = 0, skipped = 0;
            foreach (var binding in bindings)
            {
                if (kept.Any(b => b.Trigger.SameButton(binding.Trigger)))
                {
                    skipped++;
                    continue;
                }

                kept.Add(binding);
                added++;
            }

            bool changed = overwrite || added > 0;
            return !changed || Write(kept, file.Set.State) ? new(MidiImportOutcome.Done, added, skipped) : new(MidiImportOutcome.Failed, 0, 0);
        }
    }

    /// <summary>
    /// A file entry as a binding, or the reason it can't be one (shown on the pane as "(skipped: …)"). Pure.
    /// </summary>
    public static (MidiBinding? Binding, string? Problem) Parse(MidiBindingEntry? entry)
    {
        if (entry is null)
        {
            return (null, MidiText.SkippedEmpty);
        }

        var kind = KindOf(entry.Kind);
        string device = MidiNames.Normalize(entry.Device);
        string command = (entry.Command ?? "").Trim();
        string? problem =
            kind is null ? MidiText.SkippedKind(entry.Kind)
            : device.Length == 0 ? MidiText.SkippedDevice
            : entry.Channel is < 1 or > 16 ? MidiText.SkippedChannel(entry.Channel)
            : entry.Number is < 0 or > 127 ? MidiText.SkippedNumber(entry.Number)
            : entry.Occurrence is < 0 ? MidiText.SkippedOccurrence(entry.Occurrence.Value)
            : !command.StartsWith('/') || command.Length < 2 ? MidiText.SkippedCommand(entry.Command)
            : null;
        return problem is not null
            ? (null, problem)
            : (new MidiBinding(new MidiTrigger(device, entry.Occurrence ?? 0, kind!.Value, entry.Channel, entry.Number), command), null);
    }

    /// <summary>A binding as the file writes it. Pure.</summary>
    public static MidiBindingEntry Entry(MidiBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var t = binding.Trigger;
        return new MidiBindingEntry
        {
            Device = t.Device,
            Occurrence = t.Occurrence == 0 ? null : t.Occurrence,
            Kind = KindWord(t.Kind),
            Channel = t.Channel,
            Number = t.Number,
            Command = binding.Command,
        };
    }

    /// <summary>The file's word for <paramref name="kind"/>: <c>note</c>, <c>cc</c>, <c>program</c>.</summary>
    public static string KindWord(MidiKind kind) => kind switch
    {
        MidiKind.Note => "note",
        MidiKind.ControlChange => "cc",
        _ => "program",
    };

    /// <summary>The kind <paramref name="word"/> names (case ignored), or null.</summary>
    public static MidiKind? KindOf(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "note" => MidiKind.Note,
        "cc" => MidiKind.ControlChange,
        "program" => MidiKind.ProgramChange,
        _ => null,
    };

    private MidiSaveOutcome Change(Func<List<MidiBinding>, bool> edit)
    {
        lock (s_gate)
        {
            var file = Read();
            switch (file.Set.State)
            {
                case MidiFileState.Newer:
                    return MidiSaveOutcome.Newer;
                case MidiFileState.Unreadable:
                    return MidiSaveOutcome.Failed;
            }

            var list = file.Set.Bindings.ToList();
            if (!edit(list))
            {
                return MidiSaveOutcome.Failed;
            }

            return Write(list, file.Set.State) ? MidiSaveOutcome.Saved : MidiSaveOutcome.Failed;
        }
    }

    private sealed record Loaded(MidiBindingSet Set);

    /// <summary>The file as it reads: none for a missing one; none, logged, for a broken one or a newer version's. Caller holds the lock.</summary>
    private Loaded Read()
    {
        if (!File.Exists(_filePath))
        {
            return new Loaded(MidiBindingSet.Empty);
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), MidiJsonContext.Default.MidiBindingsFile);
            if (file is null)
            {
                DiagnosticLog.Warn(Category, $"{FileName} is empty or null; starting with no MIDI bindings (it is moved aside at the next save).");
                return new Loaded(new([], [], MidiFileState.Broken));
            }

            if (file.Version > Version)
            {
                DiagnosticLog.Warn(Category, $"{FileName} is version {file.Version.ToString(CultureInfo.InvariantCulture)}, from a newer NeonSidekick; it is left alone and the MIDI bindings can't be changed here.");
                return new Loaded(new([], [], MidiFileState.Newer));
            }

            var bindings = new List<MidiBinding>();
            var skipped = new List<string>();
            foreach (var entry in file.Bindings ?? [])
            {
                var (binding, problem) = Parse(entry);
                if (binding is null)
                {
                    skipped.Add(problem!);
                }
                else if (bindings.Any(b => b.Trigger.SameButton(binding.Trigger)))
                {
                    skipped.Add(MidiText.SkippedDuplicate(MidiText.Describe(binding.Trigger)));
                }
                else
                {
                    bindings.Add(binding);
                }
            }

            if (skipped.Count > 0)
            {
                DiagnosticLog.Warn(Category, $"{FileName}: {skipped.Count.ToString(CultureInfo.InvariantCulture)} entr{(skipped.Count == 1 ? "y" : "ies")} skipped: {string.Join("; ", skipped)}");
            }

            return new Loaded(new(bindings, skipped, MidiFileState.Ok));
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}; starting with no MIDI bindings (it is moved aside at the next save): {ex.Message}", ex);
            return new Loaded(new([], [], MidiFileState.Broken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}: {ex.Message}", ex);
            return new Loaded(new([], [], MidiFileState.Unreadable));
        }
    }

    /// <summary>
    /// The list written (a temp file moved over the old one), a broken file moved aside to <c>.bad</c> first; false, logged, when it could
    /// not be. Caller holds the lock and has refused a newer version's file.
    /// </summary>
    private bool Write(List<MidiBinding> bindings, MidiFileState state)
    {
        string tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            if (state == MidiFileState.Broken && File.Exists(_filePath))
            {
                File.Move(_filePath, _filePath + ".bad", overwrite: true);
                DiagnosticLog.Warn(Category, $"The unreadable {FileName} was moved aside to {FileName}.bad.");
            }

            var file = new MidiBindingsFile { Version = Version, Bindings = [.. bindings.Select(b => (MidiBindingEntry?)Entry(b))] };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(file, MidiJsonContext.Default.MidiBindingsFile));
            File.Move(tempPath, _filePath, overwrite: true);
            Computers.ConfigCache.Invalidate(_filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* best effort */ }
            DiagnosticLog.Error(Category, $"Could not write {FileName}: {ex.Message}", ex);
            return false;
        }
    }
}

/// <summary><c>midi.json</c>'s shape (2026-11-12).</summary>
public sealed class MidiBindingsFile
{
    public int Version { get; set; } = MidiBindings.Version;

    public List<MidiBindingEntry?>? Bindings { get; set; }
}

/// <summary>One binding as <c>midi.json</c> writes it: the input's name, its occurrence (left out for the first), the kind word, channel 1–16, number 0–127, the command.</summary>
public sealed class MidiBindingEntry
{
    public string? Device { get; set; }

    public int? Occurrence { get; set; }

    public string? Kind { get; set; }

    public int Channel { get; set; }

    public int Number { get; set; }

    public string? Command { get; set; }
}

/// <summary>The source-generated context for <c>&lt;profile&gt;/configs/midi.json</c> (2026-11-12). Reflection serialization is off.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MidiBindingsFile))]
public sealed partial class MidiJsonContext : JsonSerializerContext;
