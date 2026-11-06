using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Updates;

/// <summary>What a journal step does.</summary>
public enum UpdateStepKind
{
    /// <summary>Moves a file or folder from <see cref="UpdateStep.From"/> to <see cref="UpdateStep.To"/>.</summary>
    Move,

    /// <summary>Makes the folder <see cref="UpdateStep.To"/>.</summary>
    MakeFolder,

    /// <summary>Removes the emptied folder <see cref="UpdateStep.From"/>.</summary>
    RemoveFolder,
}

/// <summary>One step of a swap, with relative paths, so moving the folder with an interrupted swap changes nothing.</summary>
public sealed class UpdateStep
{
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter<UpdateStepKind>))]
    public UpdateStepKind Kind { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }

    public static UpdateStep Move(string from, string to) => new() { Kind = UpdateStepKind.Move, From = from, To = to };

    public static UpdateStep MakeFolder(string folder) => new() { Kind = UpdateStepKind.MakeFolder, To = folder };

    public static UpdateStep RemoveFolder(string folder) => new() { Kind = UpdateStepKind.RemoveFolder, From = folder };
}

/// <summary>
/// <c>.update/swap.json</c> (2026-11-04, self-update), a lasting contract: written before the first move, and whatever it finds at the next start is
/// finished by which exe is running. The exe moves last, so an unfinished journal under the old exe means undo (never finish forward, since the old exe
/// would then run on with new DLLs, the mismatch that swapping at exit exists to avoid), and under the new exe means only the cleanup was left. Every
/// write goes through a temp file and <see cref="File.Move(string, string, bool)"/>, as <c>AppSettings</c> saves, so a process ended mid-write leaves the old
/// journal or none.
///
/// <para>Untrusted on read: a planted one would otherwise move files at the next start. <see cref="TryRead(UpdateFolder, out bool)"/> takes only relative paths inside the install folder, and one
/// that fails is logged and deleted with nothing moved, so it can't be rejected at every start or stand in the next swap's way. A file that can't be read right now is left alone (2026-11-04, review P1-2: a scanner's sharing
/// violation just after a kill cost the only record of the moves), and every reader counts it as unfinished, or the next swap's <see cref="Save"/> would write over that record.</para>
/// </summary>
public sealed class UpdateJournal
{
    /// <summary>The format this build reads and writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The log's category for the swap, its undo and the start's step.</summary>
    public const string Category = "Update";

    [JsonPropertyName("format")]
    public int Format { get; set; } = CurrentFormat;

    /// <summary>The version the exe it replaced reports.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    /// <summary>The version this swap installs.</summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    /// <summary>True when this swap goes back: its result line reads "Back on X", not "Updated to X".</summary>
    [JsonPropertyName("rollback")]
    public bool Rollback { get; set; }

    /// <summary>True once every move and the cleanup are done: all that's left is telling the user.</summary>
    [JsonPropertyName("finished")]
    public bool Finished { get; set; }

    [JsonPropertyName("steps")]
    public List<UpdateStep> Steps { get; set; } = [];

    /// <summary>Writes the journal in one replace.</summary>
    public void Save(UpdateFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string temp = folder.Journal + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this, UpdateJsonContext.Default.UpdateJournal);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, folder.Journal, overwrite: true);
    }

    /// <summary>The journal on disk, or null with none or with one this copy can't read. See <see cref="TryRead(UpdateFolder, out bool)"/> where the two differ.</summary>
    public static UpdateJournal? TryRead(UpdateFolder folder) => TryRead(folder, out _);

    /// <summary>
    /// The journal on disk, or null. One that isn't valid, or that names a path outside the install folder, is logged and deleted with nothing moved. One that can't be read now stays where it is, with <paramref name="unreadable"/> true.
    /// </summary>
    public static UpdateJournal? TryRead(UpdateFolder folder, out bool unreadable)
    {
        ArgumentNullException.ThrowIfNull(folder);
        unreadable = false;
        if (!File.Exists(folder.Journal))
        {
            return null;
        }

        byte[]? bytes;
        try
        {
            bytes = UpdateFolder.ReadCapped(folder.Journal);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, UpdateText.JournalUnreadable(ex.Message));
            unreadable = true;
            return null;
        }

        UpdateJournal? journal = null;
        if (bytes is null)
        {
            DiagnosticLog.Warn(Category, UpdateText.JournalRejected(UpdateText.ManifestUnreadable));
        }
        else
        {
            try
            {
                journal = JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.UpdateJournal);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                DiagnosticLog.Warn(Category, UpdateText.JournalRejected(ex.Message));
            }
        }

        if (journal is not null && journal.Problem() is { } problem)
        {
            DiagnosticLog.Warn(Category, UpdateText.JournalRejected(problem));
            journal = null;
        }

        if (journal is null)
        {
            Delete(folder);
        }

        return journal;
    }

    /// <summary>Whether a swap isn't settled: its journal is unfinished, or can't be read and so may be. Nothing may touch the stage or start a swap until it is, since the journal's undo moves files back into it.</summary>
    public static bool IsUnsettled(UpdateFolder folder) => TryRead(folder, out bool unreadable) is { Finished: false } || unreadable;

    /// <summary>Removes the journal; a failure is logged and the next start meets it again.</summary>
    public static void Delete(UpdateFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            File.Delete(folder.Journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, UpdateText.JournalNotDeleted(ex.Message));
        }
    }

    /// <summary>
    /// The first path a step names that <see cref="TryRead(UpdateFolder, out bool)"/> would refuse, or null. A swap asks this before its first move (2026-11-04, review P3-5): a stray in the way with a name like Finder's <c>Icon\r</c> would go in the steps,
    /// and after a kill mid-swap the journal would be rejected and deleted, leaving a half-moved install with no record.
    /// </summary>
    internal string? BadPath()
    {
        foreach (var step in Steps)
        {
            if (step is null)
            {
                return "";
            }

            bool shape = step.Kind switch
            {
                UpdateStepKind.Move => step.From is not null && step.To is not null,
                UpdateStepKind.MakeFolder => step.To is not null,
                UpdateStepKind.RemoveFolder => step.From is not null,
                _ => false,
            };
            if (!shape || (step.From is { } from && !UpdateManifest.IsRelativeInside(from)) || (step.To is { } to && !UpdateManifest.IsRelativeInside(to)))
            {
                return step.From ?? step.To ?? "";
            }
        }

        return null;
    }

    private string? Problem()
    {
        if (Format != CurrentFormat)
        {
            return UpdateText.ManifestUnknownFormat(Format);
        }

        // A JSON null reaches these past their initializers (2026-11-04, review P1-1), and a null must not take the app's start with it.
        if (!UpdateVersion.TryParse(Source, out _) || !UpdateVersion.TryParse(Target, out _) || Steps is null)
        {
            return UpdateText.ManifestUnreadable;
        }

        return BadPath() is { } bad ? UpdateText.ManifestBadEntry(bad) : null;
    }
}
