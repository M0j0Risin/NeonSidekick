using System.Collections.Concurrent;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Timers;

/// <summary>
/// Plays <see cref="TimerTones"/> (2026-10-18, the user's ask: a timer going off made no sound with TTS off). Each play opens a
/// device of its own from the factory — never the TTS speaker's, which <c>SpeechSession</c> owns and stops and clears freely —
/// starts it, writes the clip, waits until it has drained (checking before each <see cref="Poll"/> wait, so a fake that drains at
/// once never waits on a manual clock; capped at the clip's length plus a second), then stops and disposes it. One play at a time:
/// a play while one sounds is skipped and gets the running task, so a burst of alerts rings once; the picker's
/// <c>replace: true</c> cancels the one sounding and plays the new one, latest wins (the voice preview's rule). It never throws: a
/// factory or a <see cref="IAudioPlayback.Start"/> that fails (Linux's <see cref="PlatformNotSupportedException"/> comes from
/// <see cref="AudioSupport.DefaultPlayback"/> itself) is logged once per run. Each clip is rendered once per name and kept, never
/// written after, so a play and a preview never share a buffer being written. A null factory plays nothing (the tests' default).
/// </summary>
public sealed class TimerTonePlayer : IDisposable
{
    /// <summary>The wait between drain checks.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

    /// <summary>How long <see cref="Dispose"/> waits for a play to stop on its own.</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly Func<PcmFormat, IAudioPlayback>? _playback;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, byte[]> _clips = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task _playing = Task.CompletedTask;
    private CancellationTokenSource? _cancel;
    private IAudioPlayback? _device;
    private bool _warned;
    private bool _disposed;

    public TimerTonePlayer(Func<PcmFormat, IAudioPlayback>? playback, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _playback = playback;
        _time = time;
    }

    /// <summary>The format every tone is rendered and played in.</summary>
    public static PcmFormat Format => PcmFormat.Kokoro;

    /// <summary><paramref name="name"/> played once, or the play already sounding; completes when it ends. Never throws.</summary>
    public Task PlayAsync(string name, CancellationToken cancellationToken) => PlayAsync(name, cancellationToken, replace: false);

    /// <summary>
    /// <paramref name="name"/> played once. With <paramref name="replace"/> (the picker's preview) a play already sounding is cut
    /// off and this one follows it; without, that play's task is returned and nothing new starts. Never throws.
    /// </summary>
    public Task PlayAsync(string name, CancellationToken cancellationToken, bool replace)
    {
        string tone = TimerTones.Normalize(name);
        if (_playback is null || tone == TimerTones.Silent)
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (!_playing.IsCompleted)
            {
                if (!replace)
                {
                    return _playing;
                }

                _cancel?.Cancel();
            }

            var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancel = cancel;
            _playing = RunAsync(tone, _playing, cancel);
            return _playing;
        }
    }

    private async Task RunAsync(string tone, Task previous, CancellationTokenSource cancel)
    {
        IAudioPlayback? device = null;
        try
        {
            await previous.ConfigureAwait(false);   // a replaced play stops first (its task never faults)
            if (cancel.IsCancellationRequested)
            {
                return;
            }

            byte[] clip = _clips.GetOrAdd(tone, n => TimerTones.Render(n, Format));
            try
            {
                device = _playback!(Format);
                device.Start();
            }
            catch (Exception ex)
            {
                Unavailable(ex);
                device?.Dispose();
                device = null;
                return;
            }

            lock (_gate)
            {
                _device = device;
            }

            try
            {
                device.Write(clip, clip.Length);
                var cap = TimeSpan.FromSeconds((double)clip.Length / Format.BytesPerSecond + 1);
                long started = _time.GetTimestamp();
                while (device.BufferedBytes > 0 && _time.GetElapsedTime(started) < cap)
                {
                    await Task.Delay(Poll, _time, cancel.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                device.ClearBuffer();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn(TimerBoard.Category, TimerText.ToneFailedLogLine(ex.Message), ex);
        }
        finally
        {
            if (device is not null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_device, device))
                    {
                        _device = null;
                    }
                }

                try
                {
                    device.Stop();
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Debug(TimerBoard.Category, TimerText.ToneFailedLogLine(ex.Message));
                }
            }

            lock (_gate)
            {
                if (ReferenceEquals(_cancel, cancel))
                {
                    _cancel = null;
                }
            }

            // A source being cancelled may still be running its callbacks (a replace cancels under the gate, and this finally can run
            // inline in that call): only an uncancelled one is disposed, which unlinks it from the caller's token.
            if (!cancel.IsCancellationRequested)
            {
                cancel.Dispose();
            }
        }
    }

    /// <summary>The first failure to open the output this run, logged; later ones quietly.</summary>
    private void Unavailable(Exception ex)
    {
        bool first;
        lock (_gate)
        {
            first = !_warned;
            _warned = true;
        }

        if (first)
        {
            DiagnosticLog.Warn(TimerBoard.Category, TimerText.ToneUnavailableLogLine(ex.Message));
        }
        else
        {
            DiagnosticLog.Debug(TimerBoard.Category, TimerText.ToneUnavailableLogLine(ex.Message));
        }
    }

    /// <summary>Any play cut off and its device stopped; later plays do nothing.</summary>
    public void Dispose()
    {
        Task playing;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancel?.Cancel();
            playing = _playing;
        }

        try
        {
            playing.Wait(DisposeWait);
        }
        catch (AggregateException)
        {
            // RunAsync never faults; nothing to report
        }

        IAudioPlayback? stuck;
        lock (_gate)
        {
            stuck = _device;
            _device = null;
        }

        if (stuck is not null)
        {
            try
            {
                stuck.ClearBuffer();
                stuck.Stop();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Debug(TimerBoard.Category, TimerText.ToneFailedLogLine(ex.Message));
            }
        }
    }
}
