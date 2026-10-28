using System.Globalization;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.UI;

namespace NeonSidekick.Prompts;

/// <summary>One saved prompt (2026-10-13, the user's ask): an id the pane follows it by, its text as <see cref="SavedPrompts.Clean"/> left it, and when it was saved.</summary>
public sealed record SavedPrompt(string Id, string Text, DateTimeOffset Added);

/// <summary>The profile's <c>prompts.json</c>: a version and the prompts in the user's order.</summary>
public sealed class SavedPromptsFile
{
    public int Version { get; set; } = SavedPrompts.Version;

    public List<SavedPrompt> Prompts { get; set; } = [];
}

/// <summary>What a change of the saved prompts did.</summary>
public enum SavedPromptOutcome
{
    /// <summary>Done (for a move, or an edit to the text it had, nothing needed writing).</summary>
    Done,

    /// <summary>An add of a text saved already: it keeps its place.</summary>
    AlreadySaved,

    /// <summary>An edit to the text another saved prompt has.</summary>
    Duplicate,

    /// <summary>No saved prompt has that id.</summary>
    NotSaved,

    /// <summary>Nothing left once cleaned: a blank text is never saved, and an edit to one is refused (never a removal).</summary>
    Empty,

    /// <summary>Longer than <see cref="SavedPrompts.MaxLength"/>.</summary>
    TooLong,

    /// <summary>The file is from a newer NeonSidekick: left alone, nothing written.</summary>
    Newer,

    /// <summary>The file could not be read or written (logged); nothing changed.</summary>
    Failed,
}

/// <summary>What <see cref="SavedPrompts.Import"/> did: its outcome, and how many prompts it added and skipped (a text the target had).</summary>
public sealed record SavedPromptImport(SavedPromptOutcome Outcome, int Added, int Skipped);

/// <summary>
/// The profile's saved prompts (2026-10-13, the user's ask: messages the user sends often, kept like the saved links and videos and put
/// on the draft line from <c>/prompts</c>' pane; the model has no tool for them), in <see cref="FileName"/> beside <c>browser-links.json</c>,
/// in the user's order (a new one last). <c>BrowserLinks</c>' shape: read on each use, a save a temp file moved over the old one, a failure
/// logged and reported, never thrown, a static lock; a broken file moved aside to <c>.bad</c> before the first write, one from a newer
/// version left alone and refusing every write, one that cannot be read for now never written over. A prompt is found by its
/// <see cref="SavedPrompt.Id"/>, never its text, so two edits and a move never mix two prompts up; texts are kept unique all the same.
/// </summary>
public sealed class SavedPrompts
{
    public const string FileName = "prompts.json";

    /// <summary>The file's version; a higher one is a newer NeonSidekick's and is left alone.</summary>
    public const int Version = 1;

    /// <summary>The longest text kept, in characters (<see cref="SavedPromptOutcome.TooLong"/> past it).</summary>
    public const int MaxLength = 20_000;

    private const string Category = "Prompts";

    // One for every instance: the screen makes a new one when the profile moves, and headless one per line.
    private static readonly Lock s_gate = new();

    private readonly string _filePath;
    private readonly TimeProvider _time;

    /// <param name="directory">The profile's directory; the file is <see cref="FileName"/> under it.</param>
    public SavedPrompts(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _filePath = NeonSidekick.Settings.Profiles.ConfigPath(Directory, FileName);   // <profile>\configs since 2026-10-16
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The profile directory the file lives in: the screen builds a new one when the profile moves.</summary>
    public string Directory { get; }

    public string FilePath => _filePath;

    /// <summary>The saved prompts in the user's order.</summary>
    public IReadOnlyList<SavedPrompt> List()
    {
        lock (s_gate)
        {
            return Load().Prompts;
        }
    }

    /// <summary>Whether the list can be changed: false while the file is a newer NeonSidekick's or cannot be read.</summary>
    public bool Writable()
    {
        lock (s_gate)
        {
            return Blocked(Load()) is null;
        }
    }

    /// <summary>The saved prompt <paramref name="text"/> numbers in <see cref="List"/> (from 1), or null.</summary>
    public SavedPrompt? Resolve(string? text)
    {
        if (!int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return null;
        }

        var prompts = List();
        return number >= 1 && number <= prompts.Count ? prompts[number - 1] : null;
    }

    /// <summary>
    /// <paramref name="text"/> (<see cref="Clean"/>) saved last; a text saved already keeps its place (<see cref="SavedPromptOutcome.AlreadySaved"/>).
    /// <paramref name="saved"/> is the prompt as the list has it after, null when nothing was saved.
    /// </summary>
    public SavedPromptOutcome Add(string? text, out SavedPrompt? saved)
    {
        saved = null;
        string clean = Clean(text);
        if (Refused(clean) is { } refused)
        {
            return refused;
        }

        lock (s_gate)
        {
            var file = Load();
            if (Blocked(file) is { } blocked)
            {
                return blocked;
            }

            if (file.Prompts.Find(p => p.Text == clean) is { } known)
            {
                saved = known;
                return SavedPromptOutcome.AlreadySaved;
            }

            var prompt = new SavedPrompt(Guid.NewGuid().ToString("N"), clean, _time.GetUtcNow());
            file.Prompts.Add(prompt);
            if (!Save(file))
            {
                return SavedPromptOutcome.Failed;
            }

            saved = prompt;
            return SavedPromptOutcome.Done;
        }
    }

    /// <summary>
    /// The saved prompt <paramref name="id"/> given <paramref name="text"/> (<see cref="Clean"/>): blank is <see cref="SavedPromptOutcome.Empty"/>,
    /// another prompt's text <see cref="SavedPromptOutcome.Duplicate"/>; the same text writes nothing.
    /// </summary>
    public SavedPromptOutcome Edit(string? id, string? text)
    {
        string clean = Clean(text);
        if (Refused(clean) is { } refused)
        {
            return refused;
        }

        return Change(id, (prompts, at) =>
        {
            if (prompts.Where((_, i) => i != at).Any(p => p.Text == clean))
            {
                return SavedPromptOutcome.Duplicate;
            }

            prompts[at] = prompts[at] with { Text = clean };
            return SavedPromptOutcome.Done;
        });
    }

    /// <summary>The saved prompt <paramref name="id"/> taken off the list.</summary>
    public SavedPromptOutcome Remove(string? id) => Change(id, (prompts, at) =>
    {
        prompts.RemoveAt(at);
        return SavedPromptOutcome.Done;
    });

    /// <summary>
    /// The saved prompt moved in the list: <paramref name="delta"/> places down (negative up), <see cref="int.MinValue"/> to the top,
    /// <see cref="int.MaxValue"/> to the bottom, held at the ends (<see cref="YouTube.YouTubeLibrary.MovedTo"/>).
    /// </summary>
    public SavedPromptOutcome Move(string? id, int delta) => Change(id, (prompts, at) =>
    {
        int to = YouTube.YouTubeLibrary.MovedTo(at, delta, prompts.Count);
        var prompt = prompts[at];
        prompts.RemoveAt(at);
        prompts.Insert(to, prompt);
        return SavedPromptOutcome.Done;
    });

    /// <summary>Every saved prompt taken off: how many went (0 with none, nothing written), or the refusal.</summary>
    public SavedPromptOutcome Clear(out int cleared)
    {
        cleared = 0;
        lock (s_gate)
        {
            var file = Load();
            if (Blocked(file) is { } blocked)
            {
                return blocked;
            }

            if (file.Prompts.Count == 0)
            {
                return SavedPromptOutcome.Done;
            }

            int count = file.Prompts.Count;
            file.Prompts.Clear();
            if (!Save(file))
            {
                return SavedPromptOutcome.Failed;
            }

            cleared = count;
            return SavedPromptOutcome.Done;
        }
    }

    /// <summary>
    /// <c>/profile push|pull prompts</c> (2026-10-13, <c>MemoryStore.Import</c>'s role): <paramref name="prompts"/> appended in their order,
    /// a text the list has already skipped, or in place of the list with <paramref name="overwrite"/>. Each gets a new id and keeps its
    /// date. A blank, too long or repeated text in <paramref name="prompts"/> is skipped. Nothing changes on a refusal (a newer file, one
    /// that cannot be read or written).
    /// </summary>
    public SavedPromptImport Import(IReadOnlyList<SavedPrompt> prompts, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        lock (s_gate)
        {
            var file = Load();
            if (Blocked(file) is { } blocked)
            {
                return new SavedPromptImport(blocked, 0, 0);
            }

            var kept = overwrite ? [] : file.Prompts.ToList();
            int added = 0;
            int skipped = 0;
            foreach (var prompt in prompts)
            {
                string clean = Clean(prompt.Text);
                if (Refused(clean) is not null || kept.Exists(p => p.Text == clean))
                {
                    skipped++;
                    continue;
                }

                kept.Add(new SavedPrompt(Guid.NewGuid().ToString("N"), clean, prompt.Added));
                added++;
            }

            if (kept.SequenceEqual(file.Prompts))
            {
                return new SavedPromptImport(SavedPromptOutcome.Done, added, skipped);
            }

            return Save(file with { Prompts = kept })
                ? new SavedPromptImport(SavedPromptOutcome.Done, added, skipped)
                : new SavedPromptImport(SavedPromptOutcome.Failed, 0, 0);
        }
    }

    // Load, find the prompt with that id, change the list, save when it changed.
    private SavedPromptOutcome Change(string? id, Func<List<SavedPrompt>, int, SavedPromptOutcome> change)
    {
        if (string.IsNullOrEmpty(id))
        {
            return SavedPromptOutcome.NotSaved;
        }

        lock (s_gate)
        {
            var file = Load();
            if (Blocked(file) is { } blocked)
            {
                return blocked;
            }

            int at = file.Prompts.FindIndex(p => p.Id == id);
            if (at < 0)
            {
                return SavedPromptOutcome.NotSaved;
            }

            var before = file.Prompts.ToList();
            var outcome = change(file.Prompts, at);
            if (outcome != SavedPromptOutcome.Done || file.Prompts.SequenceEqual(before))
            {
                return outcome;
            }

            return Save(file) ? SavedPromptOutcome.Done : SavedPromptOutcome.Failed;
        }
    }

    /// <summary>
    /// A text as kept: what a paste into the draft becomes (<see cref="PasteText.Normalize"/>: every line break one <c>\n</c>, a tab four
    /// blanks, control characters dropped), then trimmed at both ends, so it holds nothing the draft or the pane cannot draw. Pure.
    /// </summary>
    public static string Clean(string? text) => PasteText.Normalize(text ?? "").Trim();

    /// <summary>Why a cleaned text cannot be saved, or null when it can: blank, or longer than <see cref="MaxLength"/>. Pure.</summary>
    public static SavedPromptOutcome? Refused(string clean) =>
        clean.Length == 0 ? SavedPromptOutcome.Empty
        : clean.Length > MaxLength ? SavedPromptOutcome.TooLong
        : null;

    private enum FileState
    {
        Ok,
        Broken,
        Newer,

        /// <summary>Could not be read for now (locked, denied): not broken, so never moved aside, and not written over.</summary>
        Unreadable,
    }

    // Why a loaded file takes no change: a newer version's, or one that could not be read; null when it can be written.
    private static SavedPromptOutcome? Blocked(Loaded file) => file.State switch
    {
        FileState.Newer => SavedPromptOutcome.Newer,
        FileState.Unreadable => SavedPromptOutcome.Failed,
        _ => null,
    };

    private sealed record Loaded(List<SavedPrompt> Prompts, FileState State);

    /// <summary>The list as the file has it: none for a missing file; none, logged, for a broken one or a newer version's. Caller holds the lock.</summary>
    private Loaded Load()
    {
        if (!File.Exists(_filePath))
        {
            return new Loaded([], FileState.Ok);
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), PromptsJsonContext.Default.SavedPromptsFile);
            if (file is null)
            {
                DiagnosticLog.Warn(Category, $"{FileName} is empty or null; starting with no saved prompts (it is moved aside at the next save).");
                return new Loaded([], FileState.Broken);
            }

            if (file.Version > Version)
            {
                DiagnosticLog.Warn(Category, $"{FileName} is version {file.Version.ToString(CultureInfo.InvariantCulture)}, from a newer NeonSidekick; it is left alone and the saved prompts cannot be changed here.");
                return new Loaded([], FileState.Newer);
            }

            // An entry with no id or no usable text is dropped; a text seen already (an edit by hand) is kept once.
            var prompts = new List<SavedPrompt>();
            foreach (var prompt in file.Prompts ?? [])
            {
                if (prompt is null || string.IsNullOrEmpty(prompt.Id))
                {
                    continue;
                }

                string clean = Clean(prompt.Text);
                if (Refused(clean) is null && !prompts.Exists(p => p.Text == clean || p.Id == prompt.Id))
                {
                    prompts.Add(prompt with { Text = clean });
                }
            }

            return new Loaded(prompts, FileState.Ok);
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}; starting with no saved prompts (it is moved aside at the next save): {ex.Message}", ex);
            return new Loaded([], FileState.Broken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}: {ex.Message}", ex);
            return new Loaded([], FileState.Unreadable);
        }
    }

    /// <summary>
    /// The list written (a temp file moved over the old one), a broken file moved aside to <c>.bad</c> first; false, logged, when it could
    /// not be. Caller holds the lock and has refused a newer version's file.
    /// </summary>
    private bool Save(Loaded file)
    {
        string tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            if (file.State == FileState.Broken && File.Exists(_filePath))
            {
                File.Move(_filePath, _filePath + ".bad", overwrite: true);
                DiagnosticLog.Warn(Category, $"The unreadable {FileName} was moved aside to {FileName}.bad.");
            }

            File.WriteAllText(tempPath, JsonSerializer.Serialize(new SavedPromptsFile { Prompts = file.Prompts }, PromptsJsonContext.Default.SavedPromptsFile));
            File.Move(tempPath, _filePath, overwrite: true);
            // The argument list's cached read goes with it: a rename can keep the stamp's size and time (2026-10-27, the review).
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
