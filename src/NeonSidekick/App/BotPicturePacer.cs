using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// The pace of one <c>/botchat</c>'s app pictures (2026-09-25, the user's ask): with <c>Botchat images enabled</c> and
/// <c>Botchat image async</c> on and no voice to wait for, nothing held the chat back, so each reply's picture reached
/// ComfyUI straight on the heels of the last. Here each one is sent only once the one before it is done, and then
/// <see cref="Gap"/> after it finished; the pictures therefore also finish in the order they were asked for, as
/// <c>ShowReadyBotPictures</c> draws them. A cancelled wait sends nothing and holds nobody after it.
/// </summary>
public sealed class BotPicturePacer
{
    /// <summary>The rest after a picture is made before the next is sent: one second, the user's number.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly TimeSpan _gap;
    private readonly object _lock = new();
    // The last job's end, success or not; the next waits on it. Under _lock.
    private Task _previous = Task.CompletedTask;
    // When the last picture actually sent finished; null before the first. Under _lock.
    private DateTimeOffset? _lastFinished;

    public BotPicturePacer(TimeProvider time, TimeSpan? gap = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _gap = gap ?? Gap;
    }

    /// <summary>
    /// Whether the chat's pictures are paced, as the settings stand at the send: pictures on, async on, and no voice —
    /// <c>TTS output</c> off, or on with the speech not ready (the chat's own <c>speaking</c> test). Pure.
    /// </summary>
    public static bool Applies(AppSettingsData effective, bool speechReady)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.BotChatImages && effective.BotChatImageAsync && !(effective.TtsOutput && speechReady);
    }

    /// <summary>
    /// <paramref name="send"/> once the picture before it is done and <see cref="Gap"/> has passed since; its result, or
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> ends the wait.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_lock)
        {
            previous = _previous;
            _previous = done.Task;
        }

        try
        {
            await previous.WaitAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan rest;
            lock (_lock)
            {
                rest = _lastFinished is { } finished ? _gap - (_time.GetUtcNow() - finished) : TimeSpan.Zero;
            }

            if (rest > TimeSpan.Zero)
            {
                await Task.Delay(rest, _time, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await send(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    _lastFinished = _time.GetUtcNow();
                }
            }
        }
        finally
        {
            done.TrySetResult();
        }
    }
}
