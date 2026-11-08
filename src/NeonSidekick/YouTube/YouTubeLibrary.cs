using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.YouTube;

/// <summary>
/// One saved video (2026-10-07, the user's ask: a bookmark list that remembers where each video was left): its id, what the player
/// reported of it (the title and channel, how long it runs), where it was left (seconds; 0 for the start), whether it has been seen
/// to the end, when it was saved and when it last played (null: never yet). Or a public playlist (2026-11-08, the user's ask: saved
/// playlists in the same list, <see cref="Playlist"/>): its id in <see cref="Id"/>, its title and channel the list's own, and the
/// entry it was left on (<see cref="Entry"/>, <see cref="EntryVideo"/>) with <see cref="Position"/>, <see cref="Duration"/> and
/// <see cref="Watched"/> that entry's video's — watched once its last entry ended — and its own thumbnail (<see cref="Thumbnail"/>). When a
/// title lookup last found nothing is <see cref="TitleTried"/> (2026-11-08, the review). The new fields are left out of the file at their
/// defaults, so a video's row reads and writes as it did before.
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

    /// <summary>True when <see cref="Id"/> is a playlist's (<c>VideoRequest.IsPlaylistId</c>, a channel as its uploads <c>UU…</c>), not a video's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Playlist { get; init; }

    /// <summary>A playlist's entry it was left on, 0-based.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Entry { get; init; }

    /// <summary>A playlist's entry's video, or null when only the entry's number is known (the player then starts at <see cref="Entry"/>).</summary>
    public string? EntryVideo { get; init; }

    /// <summary>How many entries the player last counted in a playlist; 0 when not known yet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Count { get; init; }

    /// <summary>
    /// A playlist's own thumbnail video (2026-11-08, the review): the one oEmbed names, the owner's pick and not always an entry the list
    /// starts at, so it is only shown (the Web UI's tile while <see cref="EntryVideo"/> is unknown), never played from.
    /// </summary>
    public string? Thumbnail { get; init; }

    /// <summary>
    /// When a title lookup last found nothing (2026-11-08, the review): the list asks again only after <see cref="YouTubeTitles.RetryAfter"/>,
    /// not at every showing. Null once a title is known, and for a row copied from another profile.
    /// </summary>
    public DateTimeOffset? TitleTried { get; init; }
}

/// <summary><c>youtube.json</c>'s shape: the saved videos and playlists in the order they were saved, the newest last.</summary>
public sealed class YouTubeLibraryFile
{
    public List<YouTubeSaved> Videos { get; set; } = [];
}

/// <summary>What <see cref="YouTubeLibrary.Add"/> or <see cref="YouTubeLibrary.AddPlaylist"/> did.</summary>
public enum YouTubeSaveOutcome
{
    Added,
    AlreadySaved,

    /// <summary>The file could not be written (logged); nothing changed.</summary>
    Failed,
}

/// <summary>What <see cref="YouTubeLibrary.Import"/> did (2026-10-25).</summary>
public enum YouTubeImportOutcome
{
    Done,

    /// <summary>The target's file is there but could not be read or parsed: left alone, nothing written.</summary>
    Unreadable,

    /// <summary>The file could not be written (logged); nothing changed.</summary>
    Failed,
}

/// <summary>What <see cref="YouTubeLibrary.Import"/> did: its outcome, the videos added and those skipped as saved already.</summary>
public readonly record struct YouTubeImport(YouTubeImportOutcome Outcome, int Added, int Skipped);

/// <summary>
/// The profile's saved YouTube videos (2026-10-07, the user's ask: bookmarks, added and removed by the user's <c>/youtube save</c>,
/// <c>unsave</c> and <c>list</c> pane or the model's <c>youtube_save</c>, each resuming where it was left — <see cref="YouTubeResume"/>
/// keeps the place, <see cref="ResumeAt"/> hands it to a play), in <see cref="FileName"/> beside <c>memory.json</c>. The
/// <c>BenchHistory</c> shape: missing is empty, unreadable is empty with a warning (the next save writes over it), a save is a temp
/// file moved over the old one, and a failed save is logged and reported, never thrown. Read on each use, never kept in memory:
/// the file is small, the resume reads it only when it writes (at most every 15 s while a video plays), and a copy kept by its time
/// went stale under two instances writing within the same tick of the file clock (found by the tests, 2026-10-07). One lock for every
/// instance (later on 2026-10-16, the <c>SavedPrompts</c> shape: the Web UI's saved list changes the file from Kestrel threads while the
/// screen and the window's resume do), so two instances on one file never interleave a read-modify-write. Playlists share the list
/// (2026-11-08, the user's ask: <see cref="AddPlaylist"/>, <see cref="ResumePlaylist"/>): a playlist's id is never a video's (13
/// characters or more against 11), so <see cref="YouTubeSaved.Id"/> stays the one key for <see cref="Update"/>, <see cref="Remove"/>
/// and <see cref="Move"/>. An older build drops playlist rows at its next write (accepted).
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

    private static readonly Lock s_gate = new();
    private readonly string _filePath;
    private readonly TimeProvider _time;

    /// <param name="directory">The profile's directory; the file is <see cref="FileName"/> under it.</param>
    public YouTubeLibrary(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _filePath = NeonSidekick.Settings.Profiles.ConfigPath(Directory, FileName);   // <profile>\configs since 2026-10-16
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The profile directory the file lives in: the screen builds a new library when the profile moves.</summary>
    public string Directory { get; }

    public string FilePath => _filePath;

    /// <summary>Whether a video stopped at <paramref name="position"/> of <paramref name="duration"/> was seen to the end (within <see cref="FinishedWithin"/>).</summary>
    public static bool Finished(double position, double duration) => duration > 0 && position >= duration - FinishedWithin;

    /// <summary>
    /// Where a play of a video left at <paramref name="position"/> starts (2026-11-08, one rule for videos and playlists): a moment before
    /// it (<see cref="ResumeRewind"/>), or 0 at or under <see cref="ResumeMinimum"/>.
    /// </summary>
    public static double ResumeStart(double position) => position > ResumeMinimum ? Math.Max(0, position - ResumeRewind) : 0;

    /// <summary>
    /// The entry after a playlist's <paramref name="entry"/> was seen to the end (2026-11-08, one rule for the resume and a save): the next
    /// one, or the first again after the last of <paramref name="count"/> (0 unknown: never the last), the list then watched.
    /// </summary>
    internal static (int Entry, bool Watched) EntryAfter(int entry, int count)
    {
        bool last = count > 0 && entry >= count - 1;
        return (last ? 0 : Math.Max(0, entry) + 1, last);
    }

    /// <summary>
    /// Whether the playlist <paramref name="id"/> may be saved (2026-11-08, the review: one rule for every save and the Web UI's ☆): a
    /// playlist's id with a known prefix (<see cref="YouTubeIds.IsPlaylistPrefix"/>, so it plays again by its bare id), and no mix.
    /// </summary>
    public static bool CanSavePlaylist(string? id) => Viewer.VideoRequest.IsPlaylistId(id) && !YouTubeIds.IsMix(id) && YouTubeIds.IsPlaylistPrefix(id);

    /// <summary>
    /// Whether <paramref name="row"/>'s missing title is worth a lookup now (2026-11-08, the review): never tried, tried at least
    /// <see cref="YouTubeTitles.RetryAfter"/> ago, or stamped in the future (a hand edit, a clock set back).
    /// </summary>
    public bool LookupDue(YouTubeSaved row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var now = _time.GetUtcNow();
        return row.TitleTried is not { } tried || now < tried || now - tried >= YouTubeTitles.RetryAfter;
    }

    /// <summary>The saved row <paramref name="id"/> stamped as looked up in vain now (2026-11-08); false when it is not saved or the write failed.</summary>
    public bool MarkTried(string id)
    {
        var now = _time.GetUtcNow();
        return Update(id, v => v with { TitleTried = now });
    }

    /// <summary>The saved videos, in the order they were saved.</summary>
    public IReadOnlyList<YouTubeSaved> List()
    {
        lock (s_gate)
        {
            return Load();
        }
    }

    /// <summary>The saved video <paramref name="id"/>, or null when it is not saved.</summary>
    public YouTubeSaved? Find(string id)
    {
        lock (s_gate)
        {
            return Load().Find(v => v.Id == id);
        }
    }

    /// <summary>
    /// The saved video or playlist <paramref name="text"/> names: its number in <see cref="List"/> (from 1), its id, or a link to it;
    /// null when it names none of them. A link that names a video matches only that video; a list's link or id matches the saved list
    /// (2026-11-08, the review: a video's removal by a <c>watch?v=…&amp;list=…</c> link never takes the list instead).
    /// </summary>
    public YouTubeSaved? Resolve(string? text)
    {
        string trimmed = (text ?? "").Trim();
        var videos = List();
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return number >= 1 && number <= videos.Count ? videos[number - 1] : null;
        }

        if (videos.FirstOrDefault(v => v.Id == trimmed) is { } exact)
        {
            return exact;   // a row's own id, whatever its shape
        }

        if (YouTubeIds.TryParse(trimmed, out string id, out _))
        {
            return videos.FirstOrDefault(v => !v.Playlist && v.Id == id);
        }

        return YouTubeIds.NamesPlaylistOnly(trimmed) && YouTubeIds.TryParsePlaylist(trimmed, out string list, out _, out _, out _)
            ? videos.FirstOrDefault(v => v.Playlist && v.Id == list)
            : null;
    }

    /// <summary>
    /// Where a play of <paramref name="id"/> that names no time starts: a saved video's place as <see cref="ResumeStart"/> gives it, or
    /// 0 for one not saved, never played past <see cref="ResumeMinimum"/>, or seen to the end (its place went back to 0).
    /// </summary>
    public double ResumeAt(string id) => Find(id) is { Playlist: false } saved ? ResumeStart(saved.Position) : 0;

    /// <summary>
    /// Where a play of the saved playlist <paramref name="id"/> that names no entry or time starts (2026-11-08, the user's ask: a saved
    /// list resumes at its entry and the place in that video), as <see cref="ResumePlaylist(YouTubeSaved)"/> gives it; null for a list not
    /// saved.
    /// </summary>
    public (int Entry, string? Video, double Start)? ResumePlaylist(string id) =>
        Find(id) is { Playlist: true } saved ? ResumePlaylist(saved) : null;

    /// <summary>
    /// Where a play of the saved playlist <paramref name="saved"/> starts (2026-11-08): the entry it was left on (held below its count when
    /// that is known), that entry's video when known (never its <see cref="YouTubeSaved.Thumbnail"/>), and the place as
    /// <see cref="ResumeStart"/> gives it. Pure.
    /// </summary>
    public static (int Entry, string? Video, double Start) ResumePlaylist(YouTubeSaved saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        int entry = saved.Count > 0 ? Math.Min(saved.Entry, saved.Count - 1) : saved.Entry;
        return (Math.Max(0, entry), saved.EntryVideo, ResumeStart(saved.Position));
    }

    /// <summary>
    /// <paramref name="id"/> saved as the newest, with what is known of it; a video saved already keeps its place in the list and
    /// where it was left, and only takes a title or channel it lacked.
    /// </summary>
    public YouTubeSaveOutcome Add(string id, string? title = null, string? author = null, double duration = 0, double position = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (s_gate)
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

    /// <summary>
    /// The playlist <paramref name="id"/> saved as the newest (2026-11-08, the user's ask), with what is known of it: its title and channel,
    /// how many entries it holds (0 unknown), where it is now — the entry (0-based), that entry's video and the place in it, as the player
    /// or a link gives them — and the list's thumbnail video (<paramref name="thumbnail"/>, the owner's pick, shown only). A place within
    /// <see cref="FinishedWithin"/> of the entry's end is saved at the next entry's start, as the resume moves on (2026-11-08, the review).
    /// A list saved already keeps its place in the list and where it was left, and only takes what it lacked. An id
    /// <see cref="CanSavePlaylist"/> refuses throws: every caller checks it first.
    /// </summary>
    public YouTubeSaveOutcome AddPlaylist(string id, string? title = null, string? author = null, int count = 0, int entry = 0, string? entryVideo = null, double position = 0, double duration = 0,
        string? thumbnail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!CanSavePlaylist(id))
        {
            throw new ArgumentException("Not a playlist that can be saved: " + id, nameof(id));
        }

        string? video = Viewer.VideoRequest.IsVideoId(entryVideo) ? entryVideo : null;
        string? thumb = Viewer.VideoRequest.IsVideoId(thumbnail) ? thumbnail : null;
        entry = Math.Max(0, entry);
        count = Math.Max(0, count);
        bool watched = false;
        if (Finished(position, duration))
        {
            (entry, watched) = EntryAfter(entry, count);
            (video, position, duration) = (null, 0, 0);
        }

        lock (s_gate)
        {
            var videos = Load();
            int at = videos.FindIndex(v => v.Id == id);
            if (at >= 0)
            {
                var known = videos[at];
                var filled = known with
                {
                    Title = known.Title ?? Blank(title),
                    Author = known.Author ?? Blank(author),
                    Count = known.Count > 0 ? known.Count : count,
                    EntryVideo = known.EntryVideo ?? (known.Entry == entry ? video : null),
                    Thumbnail = known.Thumbnail ?? thumb,
                };
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
                Playlist = true,
                Title = Blank(title),
                Author = Blank(author),
                Count = count,
                Entry = entry,
                EntryVideo = video,
                Thumbnail = thumb,
                Duration = Math.Max(0, duration),
                Position = Math.Max(0, position),
                Watched = watched,
                Added = _time.GetUtcNow(),
            });
            return Save(videos) ? YouTubeSaveOutcome.Added : YouTubeSaveOutcome.Failed;
        }
    }

    /// <summary>The saved video <paramref name="id"/> taken off the list; false when it was not saved or the file could not be written.</summary>
    public bool Remove(string id)
    {
        lock (s_gate)
        {
            var videos = Load();
            return videos.RemoveAll(v => v.Id == id) > 0 && Save(videos);
        }
    }

    /// <summary>
    /// Every saved video taken off the list (2026-10-08, the user's ask: <c>/youtube list --clear</c> and the pane's clear all): how
    /// many went, 0 with none saved (nothing written), or null when the file could not be written (logged; nothing changed).
    /// </summary>
    public int? Clear()
    {
        lock (s_gate)
        {
            int count = Load().Count;
            return count == 0 ? 0 : Save([]) ? count : null;
        }
    }

    /// <summary>The saved video <paramref name="id"/> changed by <paramref name="change"/>; false when it is not saved (nothing written) or the write failed.</summary>
    public bool Update(string id, Func<YouTubeSaved, YouTubeSaved> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (s_gate)
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

    /// <summary>
    /// The saved video <paramref name="id"/> moved in the list (2026-10-13, the user's ask: the saved videos reordered as the Browser
    /// window's saved links are): <paramref name="delta"/> places down (negative up), <see cref="int.MinValue"/> to the top,
    /// <see cref="int.MaxValue"/> to the bottom, held at the ends. True when it is where it was asked to be (nothing written when it was
    /// there already); false when it is not saved or the write failed. <see cref="Update"/> keeps a video's place, so the resume's
    /// writes never undo a move.
    /// </summary>
    public bool Move(string id, int delta)
    {
        lock (s_gate)
        {
            var videos = Load();
            int at = videos.FindIndex(v => v.Id == id);
            if (at < 0)
            {
                return false;
            }

            int to = MovedTo(at, delta, videos.Count);
            if (to == at)
            {
                return true;
            }

            var video = videos[at];
            videos.RemoveAt(at);
            videos.Insert(to, video);
            return Save(videos);
        }
    }

    /// <summary>
    /// <c>/profile push|pull youtube [overwrite]</c> (2026-10-25 as <c>videos</c>, renamed 2026-11-08, the user's ask): <paramref name="videos"/>
    /// (another profile's list, its playlists with it) appended in their order, each record whole (where it was left, watched, added, last
    /// played; never <see cref="YouTubeSaved.TitleTried"/>, the other profile's attempt); a video or playlist this list holds is skipped,
    /// taking only a title, channel, a video's length, or a list's count or thumbnail it lacked, as <see cref="Add"/> does. With <paramref name="overwrite"/>, in place of this list. An entry <see cref="Load"/> would drop is dropped. Unlike <see cref="Load"/>, a file that is there but cannot be read is refused, never written over: a whole
    /// list the user built is at stake.
    /// </summary>
    public YouTubeImport Import(IReadOnlyList<YouTubeSaved> videos, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(videos);
        lock (s_gate)
        {
            if (LoadStrict() is not { } current)
            {
                return new(YouTubeImportOutcome.Unreadable, 0, 0);
            }

            var kept = overwrite ? [] : current;
            int added = 0, skipped = 0;
            bool changed = overwrite;
            foreach (var video in videos)
            {
                if (!Kept(video))
                {
                    continue;
                }

                int at = kept.FindIndex(v => v.Id == video.Id);
                if (at < 0)
                {
                    // TitleTried was the other profile's attempt: asked about again here (2026-11-08).
                    kept.Add(Cleaned(video) with { TitleTried = null });
                    added++;
                    changed = true;
                    continue;
                }

                skipped++;
                var known = kept[at];
                var copied = Cleaned(video);

                // A list's length is its entry's video's, not the list's (2026-11-08, the review): never taken from another profile's row.
                var filled = known with
                {
                    Title = known.Title ?? Blank(video.Title),
                    Author = known.Author ?? Blank(video.Author),
                    Duration = known.Duration > 0 || known.Playlist ? known.Duration : video.Duration,
                    Count = known.Count > 0 ? known.Count : copied.Count,
                    Thumbnail = known.Thumbnail ?? (known.Playlist ? copied.Thumbnail : null),
                };
                if (filled != known)
                {
                    kept[at] = filled;
                    changed = true;
                }
            }

            return !changed || Save(kept) ? new(YouTubeImportOutcome.Done, added, skipped) : new(YouTubeImportOutcome.Failed, 0, 0);
        }
    }

    /// <summary>Where a row at <paramref name="at"/> of <paramref name="count"/> lands after a move of <paramref name="delta"/> (<see cref="int.MinValue"/> the top, <see cref="int.MaxValue"/> the bottom). Pure.</summary>
    public static int MovedTo(int at, int delta, int count) => delta switch
    {
        int.MinValue => 0,
        int.MaxValue => count - 1,
        _ => (int)Math.Clamp((long)at + delta, 0L, count - 1L),
    };

    /// <summary><paramref name="text"/> trimmed, or null when blank: how every title and channel is stored.</summary>
    internal static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Whether a stored row is kept (2026-11-08): a video with a video id, or a playlist with a playlist id that is no mix
    /// (<see cref="YouTubeIds.IsMix"/>: the player cannot load one).
    /// </summary>
    private static bool Kept(YouTubeSaved? row) =>
        row is not null && (row.Playlist ? Viewer.VideoRequest.IsPlaylistId(row.Id) && !YouTubeIds.IsMix(row.Id) : Viewer.VideoRequest.IsVideoId(row.Id));

    /// <summary>
    /// A kept row with its playlist fields made safe (2026-11-08): an entry or thumbnail video of the wrong shape dropped (a play would throw
    /// on the entry's: <c>VideoRequest.Playlist</c>; the page builds an address from the thumbnail's), a negative entry or count made 0. A
    /// hand-edited or pulled file can carry either. A row with nothing to clean is the row itself, never a copy (checked first: every load
    /// passes every row through here).
    /// </summary>
    private static YouTubeSaved Cleaned(YouTubeSaved row)
    {
        bool entryVideoOk = row.EntryVideo is null || Viewer.VideoRequest.IsVideoId(row.EntryVideo);
        bool thumbnailOk = row.Thumbnail is null || Viewer.VideoRequest.IsVideoId(row.Thumbnail);
        if (entryVideoOk && thumbnailOk && row.Entry >= 0 && row.Count >= 0)
        {
            return row;
        }

        return row with
        {
            EntryVideo = entryVideoOk ? row.EntryVideo : null,
            Thumbnail = thumbnailOk ? row.Thumbnail : null,
            Entry = Math.Max(0, row.Entry),
            Count = Math.Max(0, row.Count),
        };
    }

    /// <summary>The list as the file has it; missing or unreadable is empty, and an entry <see cref="Kept"/> refuses is dropped. Caller holds the lock.</summary>
    private List<YouTubeSaved> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), YouTubeJsonContext.Default.YouTubeLibraryFile);
            return (file?.Videos ?? []).Where(Kept).Select(Cleaned).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}; starting with no saved videos: {ex.Message}", ex);
            return [];
        }
    }

    /// <summary>The list as the file has it for <see cref="Import"/>: missing is empty, and one that cannot be read or parsed is null (logged). Caller holds the lock.</summary>
    private List<YouTubeSaved>? LoadStrict()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), YouTubeJsonContext.Default.YouTubeLibraryFile);
            return (file?.Videos ?? []).Where(Kept).Select(Cleaned).ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}; nothing copied into it: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>The list written (a temp file moved over the old one); false, logged, when it could not be. Caller holds the lock.</summary>
    private bool Save(List<YouTubeSaved> videos)
    {
        string tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new YouTubeLibraryFile { Videos = videos }, YouTubeJsonContext.Default.YouTubeLibraryFile));
            File.Move(tempPath, _filePath, overwrite: true);
            // The argument list's cached read goes with it: a rename can keep the stamp's size and time (2026-10-27, the review).
            Computers.ConfigCache.Invalidate(_filePath);
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
