using System.Globalization;
using NeonSidekick.Comfy;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/botchat</c> pictures still on their way to ComfyUI with <c>Botchat image async</c> on (2026-09-27, the user's
/// ask: nothing on the hint row said a generation was running while the next bot answered). One entry per generation from
/// its send — a paced one waiting its turn in <see cref="BotPicturePacer"/> too — until its task ends, however it ends. Since
/// 2026-10-04 an <c>/imagine</c> too (the user's report: it held the input line while it ran), counted from its send, so one
/// left behind the line shows on the strip and its double-click cancels it. Since 2026-11-27 the foreground generations too (a
/// <c>/loop</c> pass's <c>/imagine</c>, a held Botchat picture, the model's <c>generate_image</c>), so one list covers every picture
/// under way. <see cref="RuleText"/> is what the lower rule's left shows, read per draw and on the tick from any thread, so every member
/// is under one lock.
/// </summary>
public sealed class PendingPictures(TimeProvider? time = null)
{
    private readonly object _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // The pending generations, oldest first. Under _lock.
    private readonly List<Entry> _pending = [];

    /// <summary>The cells between the glyph and the rest (2026-11-27, the user's note: a clear gap after the emoji). Pinned.</summary>
    public const string Gap = "  ";

    /// <summary>
    /// The lower rule's left item (2026-11-27, the user's call; the strip's <c>🖼️ 2</c> until then): empty with nothing pending, else the
    /// oldest one's kind (<see cref="ComfyText.GeneratingLabelFor"/>: 🖼️ text-to-image, 🎨 image-to-image), <see cref="Gap"/>, the count and
    /// <c> · </c> when more than one, and the oldest one's time as the busy row's clock (<see cref="ElapsedText.Countdown"/>):
    /// <c>🖼️  00:38</c>, <c>🖼️  2 · 00:38</c>. Pinned.
    /// </summary>
    public string RuleText
    {
        get
        {
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    return "";
                }

                var oldest = _pending[0];
                string clock = ElapsedText.Countdown(_time.GetUtcNow() - oldest.Started);
                string count = _pending.Count > 1 ? _pending.Count.ToString(CultureInfo.InvariantCulture) + " · " : "";
                return ComfyText.GeneratingLabelFor(oldest.InputImages) + Gap + count + clock;
            }
        }
    }

    /// <summary>How many are pending.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>One generation from its send: disposing the handle drops it (twice is harmless).</summary>
    public IDisposable Begin(int inputImages)
    {
        var entry = new Entry(this, inputImages, _time.GetUtcNow());
        lock (_lock)
        {
            _pending.Add(entry);
        }

        return entry;
    }

    /// <summary><paramref name="job"/> counted from now until it ends, success, failure or cancel; its own task, awaited as it would be.</summary>
    public async Task<T> TrackAsync<T>(int inputImages, Func<Task<T>> job)
    {
        ArgumentNullException.ThrowIfNull(job);
        using (Begin(inputImages))
        {
            return await job().ConfigureAwait(false);
        }
    }

    private void End(Entry entry)
    {
        lock (_lock)
        {
            _pending.Remove(entry);
        }
    }

    private sealed class Entry(PendingPictures owner, int inputImages, DateTimeOffset started) : IDisposable
    {
        public int InputImages { get; } = inputImages;

        public DateTimeOffset Started { get; } = started;

        public void Dispose() => owner.End(this);
    }
}
