using System.Net;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.WebUi;

/// <summary>Which of the Web UI's limits a post counts against (<see cref="WebRateLimit"/>).</summary>
public enum WebRateBucket
{
    /// <summary>A tap: a shopping line, the store, a light, a scene, the video remote. 5 a second, bursts of 15.</summary>
    Act,

    /// <summary><i>Ask the house</i>: one every 2 seconds.</summary>
    Say,

    /// <summary>A sign-in, pasted or by the link's GET (2026-10-19, the review: the GET went unlimited): 5 a minute.</summary>
    SignIn,

    /// <summary>The YouTube tab's player reports (2026-10-16): a tick a second and a command's answers, 10 a second, bursts of 30.</summary>
    Report,

    /// <summary>A bot check card's taps, holds, scrolls and typing (2026-10-17): 10 a second, bursts of 30, more than any finger.</summary>
    BotCheck,

    /// <summary>A camera's still on the Home tab (2026-10-27): one a second, bursts of 3, each a request to Home Assistant and the camera.</summary>
    Camera,
}

/// <summary>
/// The Web UI's rate limits (2026-10-15): a token bucket per address and <see cref="WebRateBucket"/>, so a stuck or misbehaving page can't flood
/// Home Assistant or guess at the token. Thread-safe (one lock, held for arithmetic only). The table is bounded: an address idle for
/// <see cref="IdleAfter"/> is dropped at the next check, and past <see cref="MaxEntries"/> the least recently seen goes first. A refusal is logged
/// once per address a minute, never once per request (and a refused remote's line the same, <see cref="LogDue"/>). <c>/api/send</c> and
/// <c>/api/answer</c> are not limited here: the chat's own flow gates them.
/// </summary>
public sealed class WebRateLimit
{
    /// <summary>How long an address may go unseen before its buckets are dropped.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(10);

    /// <summary>The most buckets kept, all addresses together.</summary>
    public const int MaxEntries = 1024;

    private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(1);
    private const string Category = "WebUi";

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<(string Address, WebRateBucket Bucket), Entry> _entries = [];
    private readonly Dictionary<(string Address, string What), long> _logged = [];
    private long _lastSweep;

    public WebRateLimit(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>A bucket's size and how fast it fills, in tokens a second.</summary>
    public static (double Burst, double PerSecond) Rate(WebRateBucket bucket) => bucket switch
    {
        WebRateBucket.Act => (15, 5),
        WebRateBucket.Say => (1, 0.5),
        WebRateBucket.SignIn => (5, 5.0 / 60),
        WebRateBucket.Report => (30, 10),
        WebRateBucket.BotCheck => (30, 10),
        WebRateBucket.Camera => (3, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null),
    };

    /// <summary>
    /// The bucket a request counts against: a post to one of the limited paths, a <paramref name="get"/> (not a HEAD, which spends nothing) of the
    /// sign-in link (2026-10-19, the review: anyone on the LAN could loop it unlimited, a log line each), or null for everything else. A camera's
    /// still spends <see cref="WebRateBucket.Camera"/> past its own checks instead (2026-10-27, the review).
    /// </summary>
    public static WebRateBucket? BucketOf(string path, bool post, bool get = false)
    {
        if (!post)
        {
            return !get ? null : path switch
            {
                WebUiAuth.SignInPath => WebRateBucket.SignIn,
                _ => null,
            };
        }

        return path switch
        {
            "/api/shopping/check" or "/api/shopping/store" or "/api/strip/select" or WebUiHost.PictureDeletePath or "/api/home/tap" or "/api/home/act" or "/api/home/scene" or "/api/home/video"
                or WebUiHost.PromptSavePath or WebUiHost.PromptDeletePath or WebUiHost.MemorySavePath or WebUiHost.MemoryDeletePath or WebUiHost.HistoryClearPath   // the sheet's (2026-10-16)
                or WebUiHost.MemoryEditPath or WebUiHost.MemoryTogglePath   // the 💾 tab's edit and off/on (2026-10-18)
                or WebUiHost.LearnCancelPath or WebUiHost.QueueResumePath   // the status row's 🧠 and the held queue's Resume (2026-10-17)
                or WebUiHost.YouTubeSavedPlayPath or WebUiHost.YouTubeSavedRemovePath or WebUiHost.YouTubeSavedClearPath or WebUiHost.YouTubeSavedMovePath
                or WebUiHost.YouTubeSavedSavePath   // the YouTube tab's saved list (later on 2026-10-16)
                or WebUiHost.ShopAddPath or WebUiHost.ShopReAddPath or WebUiHost.ShopForgetPath or WebUiHost.ShopQuantityPath or WebUiHost.ShopRenamePath
                or WebUiHost.ShopRemovePath or WebUiHost.ShopCheckAllPath or WebUiHost.ShopClearPath => WebRateBucket.Act,   // the Shopping tab's list controls (2026-10-16)
            "/api/home/say" => WebRateBucket.Say,
            WebUiAuth.SignInPath => WebRateBucket.SignIn,
            WebUiHost.YouTubeReportPath or WebUiHost.YouTubeSeenPath => WebRateBucket.Report,   // the YouTube tab's (2026-10-16; seen later that day); its stream's GET is unlimited, as Chat's
            WebUiHost.BotCheckInputPath => WebRateBucket.BotCheck,   // a bot check card's input (2026-10-17); its pictures' GET is bounded by the screen's one capture at a time
            _ => null,
        };
    }

    /// <summary>Takes one token for <paramref name="remote"/>: true when there was one; false with how long until the next.</summary>
    public bool TryTake(IPAddress? remote, WebRateBucket bucket, out TimeSpan retryAfter)
    {
        var (burst, perSecond) = Rate(bucket);
        long now = _time.GetTimestamp();
        string address = remote is null ? "" : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        bool log = false;
        lock (_gate)
        {
            Sweep(now);
            var key = (address, bucket);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry { Tokens = burst, Filled = now, LastLogged = long.MinValue };
                _entries[key] = entry;
            }

            entry.Tokens = Math.Min(burst, entry.Tokens + _time.GetElapsedTime(entry.Filled, now).TotalSeconds * perSecond);
            entry.Filled = now;
            entry.Seen = now;
            if (entry.Tokens >= 1)
            {
                entry.Tokens -= 1;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = TimeSpan.FromSeconds((1 - entry.Tokens) / perSecond);
            if (entry.LastLogged == long.MinValue || _time.GetElapsedTime(entry.LastLogged, now) >= LogEvery)
            {
                entry.LastLogged = now;
                log = true;
            }
        }

        if (log)
        {
            DiagnosticLog.Info(Category, WebUiText.RateLimitedLogLine(remote, bucket));
        }

        return false;
    }

    /// <summary>
    /// Whether a log line about <paramref name="what"/> from <paramref name="remote"/> is due (2026-10-19, the review: a refused remote's line was
    /// written once per request, so any address could flood the log): once per address a minute, as a refusal's. The table holds at
    /// <see cref="MaxEntries"/>: the lines older than a minute go first, then all of them.
    /// </summary>
    public bool LogDue(IPAddress? remote, string what)
    {
        ArgumentNullException.ThrowIfNull(what);
        long now = _time.GetTimestamp();
        string address = remote is null ? "" : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        lock (_gate)
        {
            if (_logged.TryGetValue((address, what), out long last) && _time.GetElapsedTime(last, now) < LogEvery)
            {
                return false;
            }

            if (_logged.Count >= MaxEntries)
            {
                foreach (var key in _logged.Where(e => _time.GetElapsedTime(e.Value, now) >= LogEvery).Select(e => e.Key).ToList())
                {
                    _logged.Remove(key);
                }

                if (_logged.Count >= MaxEntries)
                {
                    _logged.Clear();
                }
            }

            _logged[(address, what)] = now;
            return true;
        }
    }

    /// <summary>The buckets kept now (tests).</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    // Under the lock: drop what has been idle (once a minute at most), then the least recently seen past the cap.
    private void Sweep(long now)
    {
        if (_lastSweep == 0 || _time.GetElapsedTime(_lastSweep, now) >= TimeSpan.FromMinutes(1) || _entries.Count >= MaxEntries)
        {
            _lastSweep = now;
            foreach (var key in _entries.Where(e => _time.GetElapsedTime(e.Value.Seen, now) >= IdleAfter).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }
        }

        if (_entries.Count >= MaxEntries)
        {
            foreach (var key in _entries.OrderBy(e => e.Value.Seen).Take(_entries.Count - MaxEntries + 1).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class Entry
    {
        public double Tokens;
        public long Filled;
        public long Seen;
        public long LastLogged;
    }
}
