using System.Globalization;
using System.Numerics;

namespace NeonSidekick.Companion;

/// <summary>
/// The robot's behavior over time: it boots up (its eyes flicker on like an old CRT, then the cap light and chest core), follows
/// the pointer (or, while someone types, the text cursor), sweeps its head one pose at a time, eases its eyes, blinks, and
/// glances around on its own when the pointer goes quiet or the window loses focus. Its body moves too (<see cref="Body"/>): its
/// chest core pulses (faster for a moment after a quick sweep of the pointer), its torso turns after a deep head
/// turn, its feet shuffling after it, and it waves — hello shortly after it's on, again when the pointer comes back after it went idle (at most once per
/// <see cref="WaveCooldown"/>), and whenever <see cref="Wave"/> is called — with the hand on the pointer's side, smiling while
/// it does (<see cref="Face"/>). On <see cref="Dismiss"/> it says goodbye and powers down, and then it's
/// <see cref="IsDone"/>. Everything runs off the injected <see cref="TimeProvider"/>, so it looks the same at any frame rate
/// and tests can step the clock.
///
/// <para>Boot-up, the smile and the goodbye each have their own switch (<see cref="BootsUp"/>,
/// <see cref="SmilesWhileWaving"/>, and the host calling <see cref="Dismiss"/>) and their own <c>Update…</c> step, so each
/// can be turned off or taken out on its own.</para>
///
/// <para>Call <see cref="Update"/> once a frame; it repaints <see cref="Frame"/> and says whether any pixel changed, so a
/// caller can skip drawing while the robot is still. Not thread-safe: the caller feeds it from one thread. A host drives it
/// as an <see cref="ICompanion"/>.</para>
///
/// <para>Moved in from the AnimationTrials proof of concept on 2026-10-20 (the user's call: <c>Welcome splash</c> <c>robot</c>,
/// <see cref="UI.SplashMode"/>); this copy is the source of truth from then on. The splash's host is
/// <see cref="UI.ScreenPane.ShowCompanion"/>, which ticks it at 30 fps and stands it in the pane's padding just above the draft line; the
/// host never calls <see cref="Dismiss"/> (round 2, the user's call: the robot goes at the first line with no goodbye).</para>
/// </summary>
public sealed class RobotAnimator : ICompanion
{
    /// <summary>The least time between two head steps.</summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(45);

    /// <summary>The eyes' easing time constant.</summary>
    public static readonly TimeSpan GazeEase = TimeSpan.FromMilliseconds(80);

    /// <summary>How long the pointer may stay still before the robot looks away on its own.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(4);

    /// <summary>How long a pause in typing lasts before the robot stops watching the text cursor and looks away on its own.</summary>
    public static readonly TimeSpan TypingIdleAfter = TimeSpan.FromSeconds(3);

    /// <summary>The longest frame gap the easing honors, so a stall never makes the eyes jump.</summary>
    public static readonly TimeSpan MaxFrameGap = TimeSpan.FromMilliseconds(100);

    /// <summary>How long the eyes take to close, and again to open.</summary>
    public static readonly TimeSpan BlinkHalf = TimeSpan.FromMilliseconds(75);

    /// <summary>One core pulse at rest, and while excited.</summary>
    public static readonly TimeSpan PulseCalm = TimeSpan.FromSeconds(1.8);

    /// <inheritdoc cref="PulseCalm"/>
    public static readonly TimeSpan PulseExcited = TimeSpan.FromSeconds(0.6);

    /// <summary>How long a quick sweep of the pointer keeps the core excited.</summary>
    public static readonly TimeSpan ExcitedFor = TimeSpan.FromSeconds(1);

    /// <summary>How long the torso waits before following one of the two deepest head turns (and before un-turning).</summary>
    public static readonly TimeSpan LeanDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How long each frame of a shuffle step lasts: the near foot up and over, the near foot down, the far foot up and over
    /// — then both are planted under the lean.
    /// </summary>
    public static readonly TimeSpan StepFrame = TimeSpan.FromMilliseconds(80);

    /// <summary>The frames in a shuffle step, before both feet are planted again.</summary>
    public const int StepFrames = 3;

    /// <summary>How long after starting the robot waves hello when it doesn't boot up (<see cref="BootsUp"/> false).</summary>
    public static readonly TimeSpan HelloAfter = TimeSpan.FromSeconds(0.8);

    /// <summary>How long the arm takes to go up, and again to come down.</summary>
    public static readonly TimeSpan WaveLift = TimeSpan.FromMilliseconds(150);

    /// <summary>How long the hand holds each side of the wave.</summary>
    public static readonly TimeSpan WaveSwing = TimeSpan.FromMilliseconds(200);

    /// <summary>How many swings a wave has (out, in, out, …).</summary>
    public const int WaveSwings = 6;

    /// <summary>The least time between two waves the robot starts on its own (hello and welcome back); <see cref="Wave"/> ignores it.</summary>
    public static readonly TimeSpan WaveCooldown = TimeSpan.FromSeconds(20);

    /// <summary>How long a whole wave takes: up, the swings, down.</summary>
    public static TimeSpan WaveDuration => (WaveLift * 2) + (WaveSwing * WaveSwings);

    /// <summary>Pointer speed, in cells a second (rows counted double, as cells are tall), that excites the core.</summary>
    public const double ExcitedSpeed = 60;

    /// <summary>Booting up: how long the robot stands dark before its eyes first come on.</summary>
    public static readonly TimeSpan BootDark = TimeSpan.FromMilliseconds(250);

    /// <summary>Booting up: how long each eye shows as a dot, before and after the flicker.</summary>
    public static readonly TimeSpan BootDot = TimeSpan.FromMilliseconds(80);

    /// <summary>Booting up: how long the eyes flicker off between the two dots.</summary>
    public static readonly TimeSpan BootFlicker = TimeSpan.FromMilliseconds(60);

    /// <summary>Booting up: how long the eyes show as lines, the cap light and core coming on, before they open.</summary>
    public static readonly TimeSpan BootLine = TimeSpan.FromMilliseconds(100);

    /// <summary>How long booting up takes, from the robot appearing to its eyes open.</summary>
    public static TimeSpan BootDuration => BootDark + BootDot + BootFlicker + BootDot + BootLine;

    /// <summary>How long after booting up the robot waves hello.</summary>
    public static readonly TimeSpan HelloAfterBoot = TimeSpan.FromSeconds(0.3);

    /// <summary>The goodbye before powering down: a nod with happy eyes and the core bright.</summary>
    public static readonly TimeSpan GoodbyeFor = TimeSpan.FromMilliseconds(500);

    /// <summary>How long into the goodbye the head is down for the nod before it comes back up.</summary>
    public static readonly TimeSpan NodDown = TimeSpan.FromMilliseconds(250);

    /// <summary>How long each power-down stage shows: the line, the dot, then dark.</summary>
    public static readonly TimeSpan PowerDownStep = TimeSpan.FromMilliseconds(100);

    /// <summary>How long <see cref="Dismiss"/> takes from the call to <see cref="IsDone"/>: the goodbye and the three power-down stages.</summary>
    public static TimeSpan DismissDuration => GoodbyeFor + (PowerDownStep * RobotFace.On);

    // The core's levels through one pulse: up to bright and back down to dim.
    private static readonly int[] PulseLevels = [0, 1, 2, 3, 2, 1];

    // The eyes' stages while booting up, each held for its time; after the last they're on.
    private static readonly (TimeSpan For, int Power)[] BootStages =
        [(BootDark, RobotFace.Off), (BootDot, RobotFace.Dot), (BootFlicker, RobotFace.Off), (BootDot, RobotFace.Dot), (BootLine, RobotFace.Line)];

    private const float GazeSnap = 0.01f;

    // The nod: down, then back up to straight ahead.
    private static readonly HeadPose NodPose = new(0, 1);

    // Idle glances favor looking ahead and slight turns over the extremes.
    private static readonly int[] GlanceYaws = [-3, -2, -1, -1, 0, 0, 0, 1, 1, 2, 3];
    private static readonly int[] GlancePitches = [-1, 0, 0, 0, 1];

    private readonly TimeProvider _time;
    private readonly Random _random;
    private RobotPalette _palette;
    private ThemeRoles? _theme;
    private readonly GazeTracker _gaze = new();
    private readonly PixelBuffer _scratch = PixelBuffer.ForRobot();
    private readonly long _bornAt;

    private int _originColumn;
    private int _originRow;
    private HeadPose _target;
    private Vector2 _gazeTarget;
    private long _lastUpdate;
    private long _lastStep;
    private long _lastPointer;
    private bool _tracking;
    private bool _followingCaret;
    private bool _headOnly;
    private bool _focused = true;
    private long _nextGlance;
    private long _nextBlink;
    private long? _blinkStart;
    private bool _painted;

    private double _pulse = 2.0 / 6; // starts at the resting glow
    private long _excitedUntil;
    private (int Column, int Row, long At)? _lastPointerAt;
    private int _leanTarget;
    private long _leanSince;
    private int _stance;
    private long? _stepStart;
    private int _stepDirection;

    private long _helloAt;
    private bool _helloPending = true;
    private long? _waveStart;
    private int _waveSide;
    private long _autoWaveAllowedAt;

    private bool _bootsUp = true;
    private long _onAt;
    private int _power;

    private long? _dismissedAt;
    private int _powerDownFrom;
    private bool _skipGoodbye;

    public RobotAnimator(TimeProvider time, Random random, RobotPalette palette)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(palette);
        _time = time;
        _random = random;
        _palette = palette;

        // Set up to boot (BootsUp's default); turning it off moves the times back.
        long now = _time.GetTimestamp();
        _bornAt = now;
        _onAt = now + Ticks(BootDuration);
        _power = RobotFace.Off;
        _lastUpdate = now;
        _lastStep = now - Ticks(StepInterval);
        _nextGlance = _onAt + Ticks(GlanceDelay());
        _nextBlink = _onAt + Ticks(BlinkDelay());
        _helloAt = _onAt + Ticks(HelloAfterBoot);
        _autoWaveAllowedAt = now;
    }

    /// <summary>
    /// Whether the robot boots up — standing dark, then its eyes flickering on as a dot, off, a dot again and a line, the cap
    /// light and core coming on with the line — before it opens its eyes (<see cref="BootDuration"/>), and waves hello
    /// <see cref="HelloAfterBoot"/> after. Meanwhile its head stays still and it doesn't blink, glance or wave, but it notes
    /// the pointer and caret, so it looks straight at whichever came last once it's on. True by default; false starts it
    /// on, waving hello after <see cref="HelloAfter"/>.
    /// </summary>
    public bool BootsUp
    {
        get => _bootsUp;
        init
        {
            if (value == _bootsUp)
            {
                return;
            }

            // The constructor set the times up for booting: move them by the boot's length, so no new random draws are made.
            long shift = Ticks(BootDuration) * (value ? 1 : -1);
            _bootsUp = value;
            _onAt += shift;
            _nextGlance += shift;
            _nextBlink += shift;
            _helloAt = value ? _onAt + Ticks(HelloAfterBoot) : _bornAt + Ticks(HelloAfter);
            _power = value ? RobotFace.Off : RobotFace.On;
        }
    }

    /// <summary>Whether the robot smiles while it waves (<see cref="Expression.Smile"/>). True by default.</summary>
    public bool SmilesWhileWaving { get; set; } = true;

    /// <summary>The robot as last painted by <see cref="Update"/>: always the same buffer, so a view can hold on to it.</summary>
    public PixelBuffer Frame { get; } = PixelBuffer.ForRobot();

    /// <summary>
    /// The colors the robot is painted in; changing them repaints it on the next <see cref="Update"/>. This is the robot's
    /// own, lower-level knob: setting it directly doesn't change <see cref="Theme"/>.
    /// </summary>
    public RobotPalette Palette
    {
        get => _palette;
        set => _palette = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The head's current pose (it may still be sweeping toward <see cref="TargetPose"/>).</summary>
    public HeadPose Pose { get; private set; }

    /// <summary>Where the head is heading.</summary>
    public HeadPose TargetPose => _target;

    /// <summary>Where the eyes look, −1..1 per axis.</summary>
    public Vector2 Gaze { get; private set; }

    /// <summary>How far the eyes are closed, 0 open to 1 shut.</summary>
    public float Blink { get; private set; }

    /// <summary>The body's movements in the last frame.</summary>
    public BodyMotion Body { get; private set; } = BodyMotion.Rest;

    /// <summary>The face in the last frame: its expression and how far its lights are on.</summary>
    public RobotFace Face { get; private set; } = RobotFace.Default;

    /// <summary>Whether the robot is looking around on its own rather than following the pointer or the text cursor.</summary>
    public bool IsIdle => !_tracking;

    /// <summary>Whether the robot is watching the text cursor (someone is typing) rather than the mouse.</summary>
    public bool IsFollowingCaret => _tracking && _followingCaret;

    /// <summary>Whether a wave is under way.</summary>
    public bool IsWaving => _waveStart is not null;

    /// <summary>Whether the robot is still booting up (<see cref="BootsUp"/>).</summary>
    public bool IsBooting => Booting(_time.GetTimestamp());

    /// <summary>Whether <see cref="Dismiss"/> was called and the robot is still saying goodbye or powering down.</summary>
    public bool IsDismissing => _dismissedAt is not null && !IsDone;

    /// <summary>Whether the robot has powered down after <see cref="Dismiss"/>: the host can take it away.</summary>
    public bool IsDone { get; private set; }

    /// <summary>The robot's canvas (<see cref="RobotMetrics"/>) and its head-only crop.</summary>
    public CompanionCanvas Canvas => new(RobotMetrics.Width, RobotMetrics.Height, RobotMetrics.HeadPixelRows);

    /// <summary>
    /// The theme's colors by role, or null for the robot's own (<see cref="RobotPalette.Default"/>); setting it sets
    /// <see cref="Palette"/> to <see cref="RobotPalette.FromTheme"/>'s mapping.
    /// </summary>
    public ThemeRoles? Theme
    {
        get => _theme;
        set
        {
            _theme = value;
            Palette = value is null ? RobotPalette.Default : RobotPalette.FromTheme(value);
        }
    }

    /// <summary>
    /// The robot's part of a debug line: its pose and where it's heading, the gaze, the body (lean, feet, core, wave,
    /// expression, power) and what it's doing (dismissing, booting, typing, idle or following the mouse).
    /// </summary>
    public string DebugLine
    {
        get
        {
            var pose = Pose;
            var target = TargetPose;
            var body = Body;
            char expression = Face.Expression switch
            {
                Expression.Smile => 's',
                Expression.Happy => 'h',
                _ => 'n',
            };
            string state = IsDismissing ? "dismissing"
                : IsBooting ? "booting"
                : IsFollowingCaret ? "typing"
                : IsIdle ? "idle"
                : "mouse";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"pose {pose.Yaw:+0;-0;0},{pose.Pitch:+0;-0;0} -> {target.Yaw:+0;-0;0},{target.Pitch:+0;-0;0} | gaze {Gaze.X:+0.00;-0.00;0.00},{Gaze.Y:+0.00;-0.00;0.00} | body l{body.Lean:+0;-0;0} f{body.LeftLeg:+0;-0;0},{body.RightLeg:+0;-0;0}{(body.Lift < 0 ? "^L" : body.Lift > 0 ? "^R" : "")} c{body.Core} w{body.Wave:+0;-0;0} e{expression} p{Face.Power} | {state}");
        }
    }

    /// <summary>
    /// Whether only the head is being shown (<see cref="CompanionFit.IsHeadOnly"/>). Then waving stops (it would be a hand popping
    /// up with no arm): <see cref="Wave"/> does nothing, the hello and welcome-back waves are skipped (without starting the
    /// cooldown), and a wave under way ends at once. Clearing it brings it back.
    /// </summary>
    public bool HeadOnly
    {
        get => _headOnly;
        set
        {
            _headOnly = value;
            if (value)
            {
                _waveStart = null;
            }
        }
    }

    /// <summary>Where the canvas's top-left cell is on the screen; pointer and caret positions are taken relative to it.</summary>
    public void SetOrigin(int column, int row)
    {
        _originColumn = column;
        _originRow = row;
    }

    /// <summary>
    /// The pointer moved to screen cell (<paramref name="column"/>, <paramref name="row"/>): follow it. Ignored once
    /// <see cref="Dismiss"/> is called.
    /// </summary>
    public void OnPointer(int column, int row)
    {
        if (_dismissedAt is not null)
        {
            return;
        }

        _lastPointer = _time.GetTimestamp();
        bool booting = Booting(_lastPointer);
        if (!booting && _lastPointerAt is { } last && _lastPointer > last.At)
        {
            double dx = column - last.Column;
            double dy = (row - last.Row) * 2.0;
            double seconds = _time.GetElapsedTime(last.At, _lastPointer).TotalSeconds;
            if (Math.Sqrt((dx * dx) + (dy * dy)) / seconds >= ExcitedSpeed)
            {
                _excitedUntil = _lastPointer + Ticks(ExcitedFor);
            }
        }

        _lastPointerAt = (column, row, _lastPointer);
        bool wasIdle = !_tracking;
        _gaze.Track(CanvasCell(column, _originColumn), CanvasCell(row, _originRow));
        Follow(caret: false);

        // Welcome back — but not while booting, when the eyes are still dark (and the hello is still to come).
        if (wasIdle && !booting)
        {
            AutoWave(_lastPointer);
        }
    }

    /// <summary>
    /// The text cursor is at screen cell (<paramref name="column"/>, <paramref name="row"/>) and someone is typing: watch it,
    /// until the mouse moves or typing pauses for <see cref="TypingIdleAfter"/>. Unlike the mouse, typing never excites the
    /// core or brings on a welcome-back wave (it would interrupt every few words). The line
    /// it is on runs from screen column <paramref name="lineStart"/> to <paramref name="lineEnd"/> (exclusive): the head
    /// turns by where the caret is along it (<see cref="GazeTracker.TrackAlong"/>), so it starts moving with the first few
    /// characters typed. Ignored once <see cref="Dismiss"/> is called.
    /// </summary>
    public void OnCaret(int column, int row, int lineStart, int lineEnd)
    {
        if (_dismissedAt is not null)
        {
            return;
        }

        _lastPointer = _time.GetTimestamp();
        _gaze.TrackAlong(
            CanvasCell(column, _originColumn),
            CanvasCell(row, _originRow),
            lineStart - _originColumn,
            lineEnd - _originColumn);
        Follow(caret: true);
    }

    // A screen cell as a canvas cell.
    private static double CanvasCell(int screen, int origin) => screen - origin;

    private void Follow(bool caret)
    {
        _focused = true;
        _tracking = true;
        _followingCaret = caret;
        _target = _gaze.Pose;
        _gazeTarget = _gaze.Gaze;
    }

    /// <summary>
    /// Waves now, with the hand on <paramref name="side"/> (−1 left, +1 right as the viewer sees it) or, given 0, the hand on
    /// the pointer's side — a random one when the pointer is straight ahead or the robot isn't following it. False (and
    /// nothing changes) when a wave is already under way, when only the head is showing
    /// (<see cref="HeadOnly"/>), while booting up, or once <see cref="Dismiss"/> is called.
    /// </summary>
    public bool Wave(int side = 0)
    {
        long now = _time.GetTimestamp();
        if (IsWaving || _headOnly || Booting(now) || _dismissedAt is not null)
        {
            return false;
        }

        _waveSide = side != 0 ? Math.Sign(side) : SideTowardPointer();
        _waveStart = now;
        _autoWaveAllowedAt = now + Ticks(WaveCooldown);
        return true;
    }

    private void AutoWave(long now)
    {
        if (now >= _autoWaveAllowedAt)
        {
            Wave();
        }
    }

    private int SideTowardPointer() =>
        _tracking && _target.Yaw != 0 ? Math.Sign(_target.Yaw) : (_random.Next(2) == 0 ? -1 : 1);

    /// <summary>The window gained or lost focus; losing it sends the robot idle at once. Ignored once <see cref="Dismiss"/> is called.</summary>
    public void OnFocus(bool focused)
    {
        if (_dismissedAt is null)
        {
            _focused = focused;
        }
    }

    /// <summary>
    /// Says goodbye and powers down, for the host to take the robot away once it's <see cref="IsDone"/>
    /// (<see cref="DismissDuration"/>): a nod with happy eyes, a smile and the core bright for <see cref="GoodbyeFor"/>, then
    /// the eyes shrink to lines and dots and go dark, an old CRT switching off, <see cref="PowerDownStep"/> a stage. From
    /// here on the pointer, the caret, focus and <see cref="Wave"/> are ignored, and a wave under way ends. While booting up it skips the goodbye and powers down from wherever it got to. A second call does
    /// nothing.
    /// </summary>
    public void Dismiss()
    {
        if (_dismissedAt is not null)
        {
            return;
        }

        // Bring the lights up to date first: power down from where they are now, even if no frame has run since.
        long now = _time.GetTimestamp();
        UpdateBoot(now);
        _dismissedAt = now;
        _skipGoodbye = Booting(now);
        _powerDownFrom = _power;
        _waveStart = null;
        _blinkStart = null;
        Blink = 0;
        _gazeTarget = Vector2.Zero;
    }

    /// <summary>Advances the animation to now and repaints <see cref="Frame"/>; true when any pixel changed.</summary>
    public bool Update()
    {
        if (IsDone)
        {
            return false;
        }

        long now = _time.GetTimestamp();
        var gap = _time.GetElapsedTime(_lastUpdate, now);
        float dt = (float)(gap > MaxFrameGap ? MaxFrameGap : gap).TotalMilliseconds;
        _lastUpdate = now;

        // Earlier steps win: the goodbye over glancing around.
        UpdateBoot(now);
        UpdateDismiss(now);
        UpdateIdle(now);
        UpdateHead(now);
        UpdateGaze(dt);
        UpdateBlink(now);
        UpdateBody(now, dt);

        RobotPainter.Paint(_scratch, Pose, Gaze, Blink, _palette, Body, Face);
        if (_painted && _scratch.SameAs(Frame))
        {
            return false;
        }

        _scratch.CopyTo(Frame);
        _painted = true;
        return true;
    }

    private bool Booting(long now) => now < _onAt;

    private void UpdateBoot(long now)
    {
        if (_dismissedAt is not null)
        {
            return;
        }

        if (!Booting(now))
        {
            _power = RobotFace.On;
            return;
        }

        var t = _time.GetElapsedTime(_bornAt, now);
        foreach (var (span, power) in BootStages)
        {
            if (t < span)
            {
                _power = power;
                return;
            }

            t -= span;
        }

        _power = RobotFace.On;
    }

    private void UpdateDismiss(long now)
    {
        if (_dismissedAt is not { } dismissedAt)
        {
            return;
        }

        var t = _time.GetElapsedTime(dismissedAt, now);
        var goodbye = _skipGoodbye ? TimeSpan.Zero : GoodbyeFor;
        if (t < goodbye)
        {
            _target = t < NodDown ? NodPose : HeadPose.Center;
            return;
        }

        // Down a stage each step, the last (dark) shown for a step too; from dark already, it's done at once.
        int step = (int)((t - goodbye) / PowerDownStep);
        _power = Math.Max(RobotFace.Off, _powerDownFrom - 1 - step);
        if (step >= _powerDownFrom)
        {
            IsDone = true;
        }
    }

    private void UpdateIdle(long now)
    {
        if (_dismissedAt is not null)
        {
            return;
        }

        var idleAfter = _followingCaret ? TypingIdleAfter : IdleAfter;
        if (_tracking && (!_focused || _time.GetElapsedTime(_lastPointer, now) >= idleAfter))
        {
            _tracking = false;
            _gaze.Reset();
            _target = HeadPose.Center;
            _gazeTarget = Vector2.Zero;
            _nextGlance = now + Ticks(GlanceDelay());
        }

        if (!_tracking && !Booting(now) && now >= _nextGlance)
        {
            _target = new HeadPose(Pick(GlanceYaws), Pick(GlancePitches));
            _gazeTarget = new Vector2(Between(-0.8f, 0.8f), Between(-0.5f, 0.5f));
            _nextGlance = now + Ticks(GlanceDelay());
        }
    }

    private void UpdateHead(long now)
    {
        // Booting, the head holds still; it turns to what it noted once the eyes are on.
        if (Booting(now) || (_dismissedAt is not null && _skipGoodbye))
        {
            _lastStep = now - Ticks(StepInterval);
            return;
        }

        if (Pose != _target && _time.GetElapsedTime(_lastStep, now) >= StepInterval)
        {
            Pose = Pose.StepToward(_target);

            // Carry the leftover time to the next step, so steps average StepInterval even when it isn't a whole number of
            // frames — but bank at most one interval, so a head that's been still doesn't then rush through several.
            _lastStep = Math.Max(_lastStep + Ticks(StepInterval), now - Ticks(StepInterval));
        }
    }

    private void UpdateBody(long now, float dt)
    {
        bool goodbye = _dismissedAt is { } dismissedAt && !_skipGoodbye && _time.GetElapsedTime(dismissedAt, now) < GoodbyeFor;

        int core;
        if (_power < RobotFace.Line)
        {
            // Dark until the eyes are lines; then the pulse starts from its dimmest, so the core rises as it comes on.
            _pulse = 0;
            core = 0;
        }
        else
        {
            // A phase accumulator, so a change of pace never makes the pulse jump.
            var pulse = now < _excitedUntil ? PulseExcited : PulseCalm;
            _pulse = (_pulse + (dt / pulse.TotalMilliseconds)) % 1;
            core = goodbye ? BodyMotion.MaxCore
                : _dismissedAt is not null ? 0
                : PulseLevels[Math.Min(PulseLevels.Length - 1, (int)(_pulse * PulseLevels.Length))];
        }

        // The torso follows the two deepest turns a moment later, a unit at a time.
        int leanTarget = Math.Abs(Pose.Yaw) >= RobotMetrics.MaxYaw - 1 ? Math.Sign(Pose.Yaw) : 0;
        if (leanTarget != _leanTarget)
        {
            _leanTarget = leanTarget;
            _leanSince = now;
        }

        int lean = Body.Lean;
        if (lean != _leanTarget && _time.GetElapsedTime(_leanSince, now) >= LeanDelay)
        {
            lean += Math.Sign(_leanTarget - lean);
            _leanSince = now;
        }

        var (leftLeg, rightLeg, lift) = UpdateStep(now, lean);

        // Hello, once, shortly after it's on (unless a wave already happened).
        if (_helloPending && now >= _helloAt)
        {
            _helloPending = false;
            AutoWave(now);
        }

        // The wave: up, swing out and in, down.
        int wave = 0;
        int waveFrame = BodyMotion.WaveLift;
        if (_waveStart is { } waveStart)
        {
            var t = _time.GetElapsedTime(waveStart, now);
            if (t < WaveDuration)
            {
                wave = _waveSide;
                var swinging = t - WaveLift;
                if (swinging >= TimeSpan.Zero && swinging < WaveSwing * WaveSwings)
                {
                    waveFrame = (int)(swinging / WaveSwing) % 2 == 0 ? BodyMotion.WaveOut : BodyMotion.WaveIn;
                }
            }
            else
            {
                _waveStart = null;
            }
        }

        Body = new BodyMotion(lean, core, Sway: 0, Wave: wave, WaveFrame: waveFrame, LeftLeg: leftLeg, RightLeg: rightLeg, Lift: lift);

        // The goodbye's happy face stays on through the power-down (cut short while booting, there's no goodbye to smile
        // through); otherwise a smile while waving.
        var expression = _dismissedAt is not null ? (_skipGoodbye ? Expression.Neutral : Expression.Happy)
            : wave != 0 && SmilesWhileWaving ? Expression.Smile
            : Expression.Neutral;
        Face = new RobotFace(expression, _power);
    }

    /// <summary>
    /// The feet shuffle after the lean, a step at a time: when they aren't under it, the foot on the side they're moving
    /// toward lifts and goes over, plants, then the other follows (<see cref="StepFrame"/> a frame). A step always finishes;
    /// if the lean has moved on by then, the next starts from where the feet are. Returns each leg's offset and the foot
    /// that's up (0 for neither).
    /// </summary>
    private (int LeftLeg, int RightLeg, int Lift) UpdateStep(long now, int lean)
    {
        if (_stepStart is { } start)
        {
            int frame = (int)(_time.GetElapsedTime(start, now) / StepFrame);
            if (frame < StepFrames)
            {
                return StepFrameAt(frame);
            }

            _stance += _stepDirection;
            _stepStart = null;
        }

        if (_stance == lean)
        {
            return (_stance, _stance, 0);
        }

        _stepDirection = Math.Sign(lean - _stance);
        _stepStart = now;
        return StepFrameAt(0);
    }

    // Frame 0: the near foot up at the new place; 1: it's down there, the far foot still at the old; 2: the far foot up at the new.
    private (int LeftLeg, int RightLeg, int Lift) StepFrameAt(int frame)
    {
        int from = _stance;
        int to = _stance + _stepDirection;
        var (near, far, lift) = frame switch
        {
            0 => (to, from, _stepDirection),
            1 => (to, from, 0),
            _ => (to, to, -_stepDirection),
        };

        // The near foot is the one on the side the step goes.
        return _stepDirection > 0 ? (far, near, lift) : (near, far, lift);
    }

    private void UpdateGaze(float dt)
    {
        var delta = _gazeTarget - Gaze;
        if (delta == Vector2.Zero)
        {
            return;
        }

        float alpha = 1f - MathF.Exp(-dt / (float)GazeEase.TotalMilliseconds);
        var eased = Gaze + (delta * alpha);
        Gaze = Vector2.Distance(eased, _gazeTarget) < GazeSnap ? _gazeTarget : eased;
    }

    private void UpdateBlink(long now)
    {
        // Booting and saying goodbye each hold the lids open.
        if (Booting(now) || _dismissedAt is not null)
        {
            return;
        }

        if (_blinkStart is null)
        {
            if (now < _nextBlink)
            {
                return;
            }

            _blinkStart = now;
        }

        double t = _time.GetElapsedTime(_blinkStart.Value, now) / BlinkHalf;
        if (t >= 2)
        {
            Blink = 0;
            _blinkStart = null;
            _nextBlink = now + Ticks(BlinkDelay());
            return;
        }

        Blink = (float)(t < 1 ? t : 2 - t);
    }

    private TimeSpan GlanceDelay() => TimeSpan.FromSeconds(Between(2f, 5f));

    private TimeSpan BlinkDelay() => TimeSpan.FromSeconds(Between(3f, 6f));

    private float Between(float low, float high) => low + ((high - low) * _random.NextSingle());

    private int Pick(int[] choices) => choices[_random.Next(choices.Length)];

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);
}
