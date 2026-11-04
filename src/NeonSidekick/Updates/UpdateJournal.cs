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
/// <para>Untrusted on read: a planted one would otherwise move files at the next start. <see cref="TryRead"/> takes only relative paths inside the install folder, and one
/// that fails, or can't be read, is logged and deleted with nothing moved, so it can't be rejected at every start or stand in the next swap's way.</para>
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

    /// <summary>The journal on disk, or null with none. One that can't be read, or that names a path outside the install folder, is logged and deleted with nothing moved.</summary>
    public static UpdateJournal? TryRead(UpdateFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!File.Exists(folder.Journal))
        {
            return null;
        }

        UpdateJournal? journal = null;
        try
        {
            journal = JsonSerializer.Deserialize(File.ReadAllBytes(folder.Journal), UpdateJsonContext.Default.UpdateJournal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.Warn(Category, UpdateText.JournalRejected(ex.Message));
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

    private string? Problem()
    {
        if (Format != CurrentFormat)
        {
            return UpdateText.ManifestUnknownFormat(Format);
        }

        if (!UpdateVersion.TryParse(Source, out _) || !UpdateVersion.TryParse(Target, out _))
        {
            return UpdateText.ManifestUnreadable;
        }

        foreach (var step in Steps)
        {
            bool shape = step.Kind switch
            {
                UpdateStepKind.Move => step.From is not null && step.To is not null,
                UpdateStepKind.MakeFolder => step.To is not null,
                UpdateStepKind.RemoveFolder => step.From is not null,
                _ => false,
            };
            if (!shape || (step.From is { } from && !UpdateManifest.IsRelativeInside(from)) || (step.To is { } to && !UpdateManifest.IsRelativeInside(to)))
            {
                return UpdateText.ManifestBadEntry(step.From ?? step.To ?? "");
            }
        }

        return null;
    }
}
