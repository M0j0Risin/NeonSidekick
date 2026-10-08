using System.Globalization;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.YouTube;

/// <summary>
/// One saved video (2026-10-07, the user's ask: a bookmark list that remembers where each video was left): its id, what the player
/// reported of it (the title and channel, how long it runs), where it was left (seconds; 0 for the start), whether it has been seen
/// to the end, when it was saved and when it last played (null: never yet).
/// </summary>
public sealed record YouTubeSaved
{
    public string Id { get; init; } = "";

    public string? Title { get; init; }

    public string? Author { get; init; }

    public double Duration { get; init; }

    public double Position { get; init; }

    public bool Watched { get; init; }

    public DateTimeOffset Added { get; init; }

    public DateTimeOffset? LastPlayed { get; init; }
}

/// <summary><c>youtube.json</c>'s shape: the saved videos in the order they were saved, the newest last.</summary>
public sealed class YouTubeLibraryFile
{
    public List<YouTubeSaved> Videos { get; set; } = [];
}

/// <summary>What <see cref="YouTubeLibrary.Add"/> did.</summary>
public enum YouTubeSaveOutcome
{
    Added,
    AlreadySaved,

    /// <summary>The file could not be written (logged); nothing changed.</summary>
    Failed,
}

/// <summary>
/// The profile's saved YouTube videos (2026-10-07, the user's ask: bookmarks, added and removed by the user's <c>/youtube save</c>,
/// <c>unsave</c> and <c>saved</c> pane or the model's <c>youtube_save</c>, each resuming where it was left — <see cref="YouTubeResume"/>
/// keeps the place, <see cref="ResumeAt"/> hands it to a play), in <see cref="FileName"/> beside <c>memory.json</c>. The
/// <c>BenchHistory</c> shape: missing is empty, unreadable is empty with a warning (the next save writes over it), a save is a temp
/// file moved over the old one, and a failed save is logged and reported, never thrown. Read on each use, never kept in memory:
/// the file is small, the resume reads it only when it writes (at most every 15 s while a video plays), and a copy kept by its time
/// went stale under two instances writing within the same tick of the file clock (found by the tests, 2026-10-07).
/// </summary>
public sealed class YouTubeLibrary
{
    public const string FileName = "youtube.json";

    /// <summary>How far before the saved place a resume starts (2026-10-07): a few seconds of what was last heard, to pick the thread up.</summary>
    public const double ResumeRewind = 3;

    /// <summary>A place this near the start is no place: the video starts from the beginning.</summary>
    public const double ResumeMinimum = 5;

    /// <summary>A video stopped this near its end has been seen (<see cref="Finished"/>): its place goes back to the start.</summary>
    public const double FinishedWithin = 10;

    private const string Category = "YouTube";

    private readonly Lock _gate = new();
    private readonly string _filePath;
    private readonly TimeProvider _time;

    /// <param name="directory">The profile's directory; the file is <see cref="FileName"/> under it.</param>
    public YouTubeLibrary(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _filePath = Path.Combine(Directory, FileName);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The profile directory the file lives in: the screen builds a new library when the profile moves.</summary>
    public string Directory { get; }

    public string FilePath => _filePath;

    /// <summary>Whether a video stopped at <paramref name="position"/> of <paramref name="duration"/> was seen to the end (within <see cref="FinishedWithin"/>).</summary>
    public static bool Finished(double position, double duration) => duration > 0 && position >= duration - FinishedWithin;

    /// <summary>The saved videos, in the order they were saved.</summary>
    public IReadOnlyList<YouTubeSaved> List()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    /// <summary>The saved video <paramref name="id"/>, or null when it is not saved.</summary>
    public YouTubeSaved? Find(string id)
    {
        lock (_gate)
        {
            return Load().Find(v => v.Id == id);
        }
    }

    /// <summary>
    /// The saved video <paramref name="text"/> names: its number in <see cref="List"/> (from 1), its id, or a link to it; null
    /// when it names none of them.
    /// </summary>
    public YouTubeSaved? Resolve(string? text)
    {
        string trimmed = (text ?? "").Trim();
        var videos = List();
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return number >= 1 && number <= videos.Count ? videos[number - 1] : null;
        }

        return YouTubeIds.TryParse(trimmed, out string id, out _) ? videos.FirstOrDefault(v => v.Id == id) : null;
    }

    /// <summary>
    /// Where a play of <paramref name="id"/> that names no time starts: a saved video's place less <see cref="ResumeRewind"/>, or
    /// 0 for one not saved, never played past <see cref="ResumeMinimum"/>, or seen to the end (its place went back to 0).
    /// </summary>
    public double ResumeAt(string id) =>
        Find(id) is { Position: > ResumeMinimum } saved ? Math.Max(0, saved.Position - ResumeRewind) : 0;

    /// <summary>
    /// <paramref name="id"/> saved as the newest, with what is known of it; a video saved already keeps its place in the list and
    /// where it was left, and only takes a title or channel it lacked.
    /// </summary>
    public YouTubeSaveOutcome Add(string id, string? title = null, string? author = null, double duration = 0, double position = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            var videos = Load();
            int at = videos.FindIndex(v => v.Id == id);
            if (at >= 0)
            {
                var known = videos[at];
                var filled = known with { Title = known.Title ?? Blank(title), Author = known.Author ?? Blank(author), Duration = known.Duration > 0 ? known.Duration : duration };
                if (filled != known)
                {
                    videos[at] = filled;
                    Save(videos);
                }

                return YouTubeSaveOutcome.AlreadySaved;
            }

            videos.Add(new YouTubeSaved
            {
                Id = id,
                Title = Blank(title),
                Author = Blank(author),
                Duration = Math.Max(0, duration),
                Position = Finished(position, duration) ? 0 : Math.Max(0, position),
                Added = _time.GetUtcNow(),
            });
            return Save(videos) ? YouTubeSaveOutcome.Added : YouTubeSaveOutcome.Failed;
        }
    }

    /// <summary>The saved video <paramref name="id"/> taken off the list; false when it was not saved or the file could not be written.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var videos = Load();
            return videos.RemoveAll(v => v.Id == id) > 0 && Save(videos);
        }
    }

    /// <summary>
    /// Every saved video taken off the list (2026-10-08, the user's ask: <c>/youtube saved --clear</c> and the pane's clear all): how
    /// many went, 0 with none saved (nothing written), or null when the file could not be written (logged; nothing changed).
    /// </summary>
    public int? Clear()
    {
        lock (_gate)
        {
            int count = Load().Count;
            return count == 0 ? 0 : Save([]) ? count : null;
        }
    }

    /// <summary>The saved video <paramref name="id"/> changed by <paramref name="change"/>; false when it is not saved (nothing written) or the write failed.</summary>
    public bool Update(string id, Func<YouTubeSaved, YouTubeSaved> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var videos = Load();
            int at = videos.FindIndex(v => v.Id == id);
            if (at < 0)
            {
                return false;
            }

            var changed = change(videos[at]) with { Id = id };
            if (changed == videos[at])
            {
                return true;
            }

            videos[at] = changed;
            return Save(videos);
        }
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>The list as the file has it; missing or unreadable is empty, and an entry with no video id is dropped. Caller holds the lock.</summary>
    private List<YouTubeSaved> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), YouTubeJsonContext.Default.YouTubeLibraryFile);
            return (file?.Videos ?? []).Where(v => Viewer.VideoRequest.IsVideoId(v.Id)).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}; starting with no saved videos: {ex.Message}", ex);
            return [];
        }
    }

    /// <summary>The list written (a temp file moved over the old one); false, logged, when it could not be. Caller holds the lock.</summary>
    private bool Save(List<YouTubeSaved> videos)
    {
        string tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new YouTubeLibraryFile { Videos = videos }, YouTubeJsonContext.Default.YouTubeLibraryFile));
            File.Move(tempPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            DiagnosticLog.Error(Category, $"Could not write {FileName}: {ex.Message}", ex);
            return false;
        }
    }
}
