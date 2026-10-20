namespace NeonSidekick.Companion;

/// <summary>
/// The body's small movements for one frame, a pixel at a time. <see cref="Rest"/> is the robot standing still.
/// </summary>
/// <param name="Lean">−1, 0 or +1: the torso turns toward one of the two deepest head turns (−1 left, +1 right).</param>
/// <param name="Core">0 (dim) to 3 (bright): the chest core's glow; 2 is its resting look.</param>
/// <param name="Sway">−1, 0 or +1: the antenna tips' sideways offset. The animator keeps them upright (a follow-through was
/// tried and distracted); the painter still draws it, bending the stalks with the tips.</param>
/// <param name="Wave">0, or the side waving: −1 the left arm (as the viewer sees it), +1 the right.</param>
/// <param name="WaveFrame">The waving arm's frame: <see cref="WaveLift"/> on the way up and down, then <see cref="WaveOut"/>
/// and <see cref="WaveIn"/> back and forth.</param>
/// <param name="LeftLeg">−1, 0 or +1: the left leg's sideways offset (as the viewer sees it), as the feet shuffle after a
/// lean.</param>
/// <param name="RightLeg">The same for the right leg.</param>
/// <param name="Lift">0, or the foot that's up mid-step: −1 the left, +1 the right.</param>
public readonly record struct BodyMotion(int Lean, int Core, int Sway, int Wave = 0, int WaveFrame = 0, int LeftLeg = 0, int RightLeg = 0, int Lift = 0)
{
    /// <summary>The brightest core level.</summary>
    public const int MaxCore = 3;

    /// <summary>The arm halfway up (raising or lowering).</summary>
    public const int WaveLift = 0;

    /// <summary>The arm up, hand straight.</summary>
    public const int WaveOut = 1;

    /// <summary>The arm up, hand tipped in toward the head.</summary>
    public const int WaveIn = 2;

    /// <summary>Standing still: no lean, the core at its resting glow, the antennas upright, both arms down, both feet planted under the hips.</summary>
    public static BodyMotion Rest { get; } = new(Lean: 0, Core: 2, Sway: 0);
}
