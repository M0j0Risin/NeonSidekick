namespace NeonSidekick.Viewer;

/// <summary>What a Ctrl+K in the process window did (<see cref="ProcessKillArm.Press"/>).</summary>
public enum KillPress
{
    /// <summary>Nothing: the process has exited already.</summary>
    Ignored,

    /// <summary>The first press: the title asks for a second.</summary>
    Armed,

    /// <summary>The second press within <see cref="ProcessKillArm.Window"/>: stop the process.</summary>
    Fire,
}

/// <summary>
/// The process window's kill key, pressed twice to confirm (2026-10-05, <see cref="ProcessWindow"/>): the first Ctrl+K arms it
/// and the title asks for a second, a second within <see cref="Window"/> stops the process, and a press after that starts
/// over. A process that has exited ignores it. Pure: the clock is passed in, so a test drives it without waiting.
/// </summary>
public sealed class ProcessKillArm
{
    /// <summary>How long the first press waits for the second.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private DateTimeOffset? _armedAt;

    /// <summary>A press at <paramref name="now"/>; <paramref name="exited"/> says whether the process has ended already.</summary>
    public KillPress Press(DateTimeOffset now, bool exited)
    {
        if (exited)
        {
            _armedAt = null;
            return KillPress.Ignored;
        }

        if (IsArmed(now))
        {
            _armedAt = null;
            return KillPress.Fire;
        }

        _armedAt = now;
        return KillPress.Armed;
    }

    /// <summary>Whether a first press at most <see cref="Window"/> ago waits for its second.</summary>
    public bool IsArmed(DateTimeOffset now) => _armedAt is { } at && now - at < Window && now >= at;
}
