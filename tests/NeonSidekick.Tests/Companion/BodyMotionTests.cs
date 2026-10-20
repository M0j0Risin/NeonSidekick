using NeonSidekick.Companion;
using System.Numerics;
using Spectre.Console;

namespace NeonSidekick.Tests.Companion;

public sealed class BodyMotionTests
{
    private static readonly RobotPalette Palette = RobotPalette.Default;

    private static PixelBuffer Paint(BodyMotion body, HeadPose? pose = null)
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, pose ?? HeadPose.Center, Vector2.Zero, 0, Palette, body);
        return buffer;
    }

    private static List<(int X, int Y)> Where(PixelBuffer buffer, Func<Color?, bool> match)
    {
        var found = new List<(int, int)>();
        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
            {
                if (match(buffer[x, y]))
                {
                    found.Add((x, y));
                }
            }
        }

        return found;
    }

    [Fact]
    public void RestIsTheDefault()
    {
        var buffer = PixelBuffer.ForRobot();
        RobotPainter.Paint(buffer, HeadPose.Center, Vector2.Zero, 0, Palette);

        Assert.True(buffer.SameAs(Paint(BodyMotion.Rest)));
    }

    [Fact]
    public void LeaningShiftsTheCoreAndDropsTheNearShoulder()
    {
        var rest = Paint(BodyMotion.Rest);
        var left = Paint(BodyMotion.Rest with { Lean = -1 });
        var right = Paint(BodyMotion.Rest with { Lean = 1 });

        static int CoreX(PixelBuffer b) => Where(b, c => c == Palette.ShellLight).Where(p => p.Y is > 23 and < 30).Min(p => p.X);
        Assert.Equal(CoreX(rest) - 1, CoreX(left));
        Assert.Equal(CoreX(rest) + 1, CoreX(right));

        // A shoulder pad's top row (at an inner column, which moves with the lean): the near one drops a pixel, the far one
        // stays level (raised, it would read as a shrug).
        static int PadTop(PixelBuffer b, int x) => Enumerable.Range(22, 8).First(y => b[x, y] is not null); // below the head
        Assert.Equal(PadTop(rest, 3) + 1, PadTop(left, 3 - 1));
        Assert.Equal(PadTop(rest, 20), PadTop(left, 20 - 1));
        Assert.Equal(PadTop(rest, 3), PadTop(right, 3 + 1));
        Assert.Equal(PadTop(rest, 20) + 1, PadTop(right, 20 + 1));

        // The waist and hips go over with the torso (the feet step after them): the waist's dark top row and the hips' seam.
        static int DarkFrom(PixelBuffer b, int y) => Where(b, c => c == Palette.Visor).Where(p => p.Y == y && p.X is > 5 and < 18).Min(p => p.X);
        foreach (int y in new[] { 30, 32 })
        {
            Assert.Equal(DarkFrom(rest, y) - 1, DarkFrom(left, y));
            Assert.Equal(DarkFrom(rest, y) + 1, DarkFrom(right, y));
        }
    }

    [Fact]
    public void EachLegMovesOnItsOwn()
    {
        var rest = Paint(BodyMotion.Rest);
        var moved = Paint(BodyMotion.Rest with { LeftLeg = 1 });

        for (int y = 34; y < RobotMetrics.Height; y++)
        {
            // The left leg (its columns, clear of the hand hanging at x 3), a pixel to the right...
            for (int x = 5; x < 11; x++)
            {
                Assert.Equal(rest[x, y], moved[x + 1, y]);
            }

            // ...and the right one where it was.
            for (int x = 12; x < RobotMetrics.Width; x++)
            {
                Assert.Equal(rest[x, y], moved[x, y]);
            }
        }
    }

    [Fact]
    public void ALiftedFootIsARowUp()
    {
        var rest = Paint(BodyMotion.Rest);
        var lifted = Paint(BodyMotion.Rest with { Lift = 1 });

        // The right foot: its boot on the shin's row, its sole on the boot's, nothing under it.
        for (int x = 13; x < 17; x++)
        {
            Assert.Equal(rest[x, 38], lifted[x, 37]);
        }

        for (int x = 13; x < 18; x++)
        {
            Assert.Equal(Palette.ShellShade, lifted[x, 38]);
            Assert.Null(lifted[x, 39]);
        }

        // The thigh and knee stay, and the left leg is untouched.
        for (int y = 34; y < 37; y++)
        {
            for (int x = 13; x < 16; x++)
            {
                Assert.Equal(rest[x, y], lifted[x, y]);
            }
        }

        for (int y = 34; y < RobotMetrics.Height; y++)
        {
            for (int x = 0; x < 12; x++)
            {
                Assert.Equal(rest[x, y], lifted[x, y]);
            }
        }
    }

    [Fact]
    public void MidStepItStaysInOnePieceOnTheCanvasAndClearOfTheArms()
    {
        foreach (int lean in new[] { -1, 0, 1 })
        {
            foreach (var (leftLeg, rightLeg, lift) in StepStates())
            {
                var body = BodyMotion.Rest with { Lean = lean, LeftLeg = leftLeg, RightLeg = rightLeg, Lift = lift };
                var buffer = Paint(body);
                AssertOnePiece(buffer, $"{body}");

                // Nothing clipped: each planted sole is whole on the bottom row, a lifted one on the row above (where each
                // planted boot also has its one shaded pixel).
                int planted = lift == 0 ? 2 : 1;
                Assert.Equal(5 * planted, Where(buffer, c => c == Palette.ShellShade).Count(p => p.Y == 39));
                Assert.Equal(planted + (lift == 0 ? 0 : 5), Where(buffer, c => c == Palette.ShellShade).Count(p => p.Y == 38));

                // The hips keep a gap from both arms: three separate runs across the hips' top row.
                int runs = Enumerable.Range(0, RobotMetrics.Width).Count(x => buffer[x, 32] is not null && buffer[x - 1, 32] is null);
                Assert.True(runs == 3, $"{body}: {runs} runs on row 32");
            }
        }
    }

    [Fact]
    public void EachCoreLevelGlowsDifferently()
    {
        var frames = Enumerable.Range(0, BodyMotion.MaxCore + 1).Select(level => Paint(BodyMotion.Rest with { Core = level })).ToList();

        for (int i = 0; i < frames.Count; i++)
        {
            for (int j = i + 1; j < frames.Count; j++)
            {
                Assert.False(frames[i].SameAs(frames[j]), $"core levels {i} and {j} look the same");
            }
        }

        // Brighter levels have more white-hot pixels in the core.
        int Hot(PixelBuffer b) => Where(b, c => c == Palette.ShellLight).Count(p => p.Y is > 23 and < 30 && p.X is > 8 and < 16);
        Assert.True(Hot(frames[0]) < Hot(frames[2]) && Hot(frames[2]) < Hot(frames[3]));
    }

    [Fact]
    public void SwayMovesOnlyTheAntennaTipsAndBendsTheStalksWithThem()
    {
        foreach (var pose in HeadPose.All)
        {
            foreach (int sway in new[] { -1, 1 })
            {
                var head = HeadLayout.For(pose);
                var swayed = Paint(BodyMotion.Rest with { Sway = sway }, pose);
                foreach (var (tip, stalk) in new[] { (head.LeftTip, head.LeftStalk), (head.RightTip, head.RightStalk) })
                {
                    // A tip at the canvas's edge (the near antenna at the deepest turn) stays put rather than sway off it.
                    int dx = RobotPainter.SwayWithin(tip, sway);
                    var moved = tip.Offset(dx, 0);
                    Assert.Equal(Palette.Accent, swayed[moved.X, moved.Y]);

                    // The stalk's top pixel sits right under the moved tip: still joined.
                    int topX = stalk.X + dx;
                    Assert.Equal(Palette.Ear, swayed[topX, stalk.Y]);
                    Assert.True(topX >= moved.X && topX < moved.Right, $"{pose} sway {sway}: stalk top {topX} not under tip {moved}");
                }
            }
        }

        var rest = Paint(BodyMotion.Rest);
        var swayed1 = Paint(BodyMotion.Rest with { Sway = 1 });
        for (int y = RobotMetrics.HeadTop; y < RobotMetrics.Height; y++)
        {
            for (int x = 0; x < RobotMetrics.Width; x++)
            {
                Assert.Equal(rest[x, y], swayed1[x, y]);
            }
        }
    }

    [Fact]
    public void EveryCombinationStaysOnTheCanvas()
    {
        var canvas = new PixelRect(0, 0, RobotMetrics.Width, RobotMetrics.Height);
        foreach (var pose in HeadPose.All)
        {
            foreach (int sway in new[] { -1, 0, 1 })
            {
                var head = HeadLayout.For(pose);
                foreach (var part in new[] { head.Shell, head.Cap, head.LeftEar, head.RightEar, head.LeftStalk, head.RightStalk })
                {
                    Assert.True(canvas.Contains(part), $"{pose}: {part} is off the canvas");
                }

                Assert.True(canvas.Contains(head.LeftTip.Offset(RobotPainter.SwayWithin(head.LeftTip, sway), 0)), $"{pose} sway {sway}: left tip off the canvas");
                Assert.True(canvas.Contains(head.RightTip.Offset(RobotPainter.SwayWithin(head.RightTip, sway), 0)), $"{pose} sway {sway}: right tip off the canvas");
            }
        }

        // And the lowest-hanging hand (on the dropped side) is still drawn: its finger lands a row lower, on the canvas.
        Assert.Equal(Palette.ShellShade, Paint(new BodyMotion(-1, 2, 0))[2, 35]);
        Assert.Equal(Palette.ShellShade, Paint(new BodyMotion(1, 2, 0))[21, 35]);
    }

    [Fact]
    public void NothingEverComesApart()
    {
        // Every pose with every lean and sway: all the robot's pixels form one piece (touching sideways, up, down or
        // diagonally, as pixel art reads), so no arm, cap or antenna tip floats free.
        foreach (var pose in HeadPose.All)
        {
            foreach (int lean in new[] { -1, 0, 1 })
            {
                foreach (var (sway, wave, frame) in Variants())
                {
                    var body = new BodyMotion(lean, 2, sway, wave, frame);
                    AssertOnePiece(Paint(body, pose), $"{pose} {body}");
                }
            }
        }
    }

    // All the pixels touch (sideways, up, down or diagonally, as pixel art reads).
    private static void AssertOnePiece(PixelBuffer buffer, string what)
    {
        var pixels = Where(buffer, c => c is not null).ToHashSet();
        var reached = new HashSet<(int X, int Y)>();
        var queue = new Queue<(int X, int Y)>([pixels.First()]);
        while (queue.TryDequeue(out var p))
        {
            if (!pixels.Contains(p) || !reached.Add(p))
            {
                continue;
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    queue.Enqueue((p.X + dx, p.Y + dy));
                }
            }
        }

        var loose = pixels.Except(reached).ToList();
        Assert.True(loose.Count == 0, $"{what}: {loose.Count} pixels apart from the rest, e.g. {string.Join(" ", loose.Take(5))}");
    }

    // Every leg state a shuffle step can show: planted at each stance, and each frame of each step between stances — the
    // near foot (on the side it goes) up at the new place, then down there, then the far foot up at the new place.
    private static IEnumerable<(int LeftLeg, int RightLeg, int Lift)> StepStates()
    {
        foreach (int from in new[] { -1, 0, 1 })
        {
            yield return (from, from, 0);
            foreach (int d in new[] { -1, 1 })
            {
                int to = from + d;
                if (to is < -1 or > 1)
                {
                    continue;
                }

                (int, int, int) Legs(int near, int far, int lift) => d > 0 ? (far, near, lift) : (near, far, lift);
                yield return Legs(to, from, d);
                yield return Legs(to, from, 0);
                yield return Legs(to, to, -d);
            }
        }
    }

    // Every sway with arms down, and every waving side and frame with the antennas upright.
    private static IEnumerable<(int Sway, int Wave, int Frame)> Variants()
    {
        foreach (int sway in new[] { -1, 0, 1 })
        {
            yield return (sway, 0, 0);
        }

        foreach (int wave in new[] { -1, 1 })
        {
            for (int frame = BodyMotion.WaveLift; frame <= BodyMotion.WaveIn; frame++)
            {
                yield return (0, wave, frame);
            }
        }
    }

    [Fact]
    public void WavingRaisesThatArmBesideTheHead()
    {
        var rest = Paint(BodyMotion.Rest);
        var waving = Paint(BodyMotion.Rest with { Wave = 1, WaveFrame = BodyMotion.WaveOut });

        // The right arm no longer hangs (its forearm and hand rows are empty)...
        for (int y = 31; y < 35; y++)
        {
            Assert.NotNull(rest[20, y]); // forearm, hand, then the finger at column 20
            Assert.Null(waving[19, y]);
            Assert.Null(waving[20, y]);
        }

        // ...it's up at the canvas's edge beside the head instead, below the ear; the left arm still hangs.
        Assert.All(Enumerable.Range(17, 6), y => Assert.NotNull(waving[23, y]));
        Assert.All(Enumerable.Range(17, 6), y => Assert.Null(rest[23, y]));
        Assert.Equal(rest[3, 32], waving[3, 32]);
    }

    [Fact]
    public void TheWaveSwingsTheHand()
    {
        var lift = Paint(BodyMotion.Rest with { Wave = 1, WaveFrame = BodyMotion.WaveLift });
        var out_ = Paint(BodyMotion.Rest with { Wave = 1, WaveFrame = BodyMotion.WaveOut });
        var in_ = Paint(BodyMotion.Rest with { Wave = 1, WaveFrame = BodyMotion.WaveIn });

        Assert.False(out_.SameAs(in_));
        Assert.False(lift.SameAs(out_));

        // Tipped in, the hand's outer column clears.
        Assert.NotNull(out_[23, 17]);
        Assert.Null(in_[23, 17]);
    }

    [Fact]
    public void EitherHandWavesTheSameWay()
    {
        // The robot's outline is symmetric, so the left wave is the right one mirrored.
        foreach (int frame in new[] { BodyMotion.WaveLift, BodyMotion.WaveOut, BodyMotion.WaveIn })
        {
            var right = Paint(BodyMotion.Rest with { Wave = 1, WaveFrame = frame });
            var left = Paint(BodyMotion.Rest with { Wave = -1, WaveFrame = frame });
            for (int y = 0; y < RobotMetrics.Height; y++)
            {
                for (int x = 0; x < RobotMetrics.Width; x++)
                {
                    Assert.Equal(right[x, y] is null, left[RobotMetrics.Width - 1 - x, y] is null);
                }
            }
        }
    }

    [Fact]
    public void AHandInFrontOfTheHeadIsOutlined()
    {
        // Turned fully toward the waving hand, the head sits behind it; a dark line separates them.
        var pose = new HeadPose(2, 0);
        var plain = Paint(BodyMotion.Rest, pose);
        var waving = Paint(BodyMotion.Rest with { Wave = 1, WaveFrame = BodyMotion.WaveOut }, pose);

        Assert.Equal(Palette.Shell, plain[20, 17]);
        Assert.Equal(Palette.Visor, waving[20, 17]);
    }

    [Fact]
    public void JustTheHeadIsOnePieceToo()
    {
        // In every pose and lean (no wave: the animator stops it when only the head shows), the crop's pixels
        // touch one another, and the head's bottom row is inside it.
        foreach (var pose in HeadPose.All)
        {
            foreach (int lean in new[] { -1, 0, 1 })
            {
                var buffer = Paint(new BodyMotion(lean, 2, 0), pose);
                var pixels = Where(buffer, c => c is not null).Where(p => p.Y < RobotMetrics.HeadPixelRows).ToHashSet();
                var reached = new HashSet<(int X, int Y)>();
                var queue = new Queue<(int X, int Y)>([pixels.First()]);
                while (queue.TryDequeue(out var p))
                {
                    if (!pixels.Contains(p) || !reached.Add(p))
                    {
                        continue;
                    }

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            queue.Enqueue((p.X + dx, p.Y + dy));
                        }
                    }
                }

                Assert.True(pixels.SetEquals(reached), $"{pose} lean {lean}: {pixels.Count - reached.Count} pixels apart from the rest");
                Assert.True(HeadLayout.For(pose).Shell.Bottom <= RobotMetrics.HeadPixelRows);
            }
        }
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        Assert.True(Paint(new BodyMotion(-9, 99, 9)).SameAs(Paint(new BodyMotion(-1, BodyMotion.MaxCore, 1))));
    }
}
