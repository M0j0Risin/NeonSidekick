namespace NeonSidekick.Companion;

/// <summary>
/// The face for one frame, beside the <see cref="BodyMotion"/>: its expression and how far its lights are on.
/// <see cref="Default"/> is the robot lit and neutral.
/// </summary>
/// <param name="Expression">The mouth and, for <see cref="Expression.Happy"/>, the eyes.</param>
/// <param name="Power">The lights, <see cref="On"/> down to <see cref="Off"/>, an old CRT switching off: at <see cref="Line"/>
/// each eye is a bright line, at <see cref="Dot"/> a single pixel, and <see cref="Off"/> leaves just the visor. Below
/// <see cref="Line"/> the cap light and the chest core are dark too. Boot-up runs the same stages the other way.</param>
public readonly record struct RobotFace(Expression Expression, int Power)
{
    /// <summary>Just the visor: no eyes, the cap and core dark.</summary>
    public const int Off = 0;

    /// <summary>Each eye a single pixel.</summary>
    public const int Dot = 1;

    /// <summary>Each eye a bright line across its middle.</summary>
    public const int Line = 2;

    /// <summary>Fully lit.</summary>
    public const int On = 3;

    /// <summary>A neutral face, fully lit.</summary>
    public static RobotFace Default { get; } = new(Expression.Neutral, On);
}

/// <summary>What the face shows.</summary>
public enum Expression
{
    /// <summary>A straight mouth.</summary>
    Neutral,

    /// <summary>A smiling mouth (<c>∪</c>), at any turn.</summary>
    Smile,

    /// <summary>The smile with the eyes closed happily (<c>^ ^</c>); the eyes only change when the head is within a step of
    /// straight ahead, where the visor is wide enough for both arcs.</summary>
    Happy,
}
