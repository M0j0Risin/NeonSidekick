using System.Globalization;
using NeonSidekick.Comfy;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/botchat</c> pictures still on their way to ComfyUI with <c>Botchat image async</c> on (2026-09-27, the user's
/// ask: nothing on the hint row said a generation was running while the next bot answered). One entry per generation from
/// its send — a paced one waiting its turn in <see cref="BotPicturePacer"/> too — until its task ends, however it ends.
/// <see cref="Glyph"/> is what the hint row's strip shows (<see cref="ChatScreen.StripGlyphs"/>), read per draw and on the
/// tick from any thread, so every member is under one lock.
/// </summary>
public sealed class PendingPictures
{
    private readonly object _lock = new();
    // The pending generations' input picture counts, oldest first. Under _lock.
    private readonly List<Entry> _pending = [];

    /// <summary>
    /// The strip's part: empty with nothing pending, else the oldest one's kind (<see cref="ComfyText.GeneratingLabelFor"/>:
    /// 🖼️ text-to-image, 🎨 image-to-image) with the count straight after it when more than one — <c>🖼️2</c>, glued, so
    /// the strip's separator never splits it. Pinned.
    /// </summary>
    public string Glyph
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count switch
                {
                    0 => "",
                    1 => ComfyText.GeneratingLabelFor(_pending[0].InputImages),
                    var count => ComfyText.GeneratingLabelFor(_pending[0].InputImages) + count.ToString(CultureInfo.InvariantCulture),
                };
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
        var entry = new Entry(this, inputImages);
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

    private sealed class Entry(PendingPictures owner, int inputImages) : IDisposable
    {
        public int InputImages { get; } = inputImages;

        public void Dispose() => owner.End(this);
    }
}
