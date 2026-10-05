using System.Drawing;
using System.Numerics;
using NeonSidekick.Images;

namespace NeonSidekick.Tests;

/// <summary><see cref="ImageColor"/>'s matrices (pure) and its colour parser (2026-10-04).</summary>
public sealed class ImageColorTests
{
    // A pixel through a matrix as ColorMatrixTransform reads it: a row vector, the fourth row the offset.
    private static Vector3 Apply(Matrix4x4 m, float r, float g, float b) => new(
        r * m.M11 + g * m.M21 + b * m.M31 + m.M41,
        r * m.M12 + g * m.M22 + b * m.M32 + m.M42,
        r * m.M13 + g * m.M23 + b * m.M33 + m.M43);

    private static float Luma(Vector3 v) => v.X * ImageColor.LumaR + v.Y * ImageColor.LumaG + v.Z * ImageColor.LumaB;

    [Fact]
    public void Matrix_NoSteps_IsTheIdentity()
    {
        Assert.True(ImageColor.Matrix(ImageFilter.None, 0, 0, 0, 0, null, 50).IsIdentity);
        Assert.True(ImageColor.Matrix(ImageFilter.None, 0, 0, 0, 360, Color.Red, 0).IsIdentity);   // a full turn and a zero tint are nothing
        Assert.False(ImageColor.Matrix(ImageFilter.Grey, 0, 0, 0, 0, null, 50).IsIdentity);
    }

    [Fact]
    public void Matrix_ComposesInOrder_BrightnessThenContrast()
    {
        // Brightness first lifts black to 0.25; contrast 100 doubles its distance from mid-grey: 0.5 - 2·0.25 = 0.
        var m = ImageColor.Matrix(ImageFilter.None, 50, 100, 0, 0, null, 0);
        Assert.Equal(0f, Apply(m, 0, 0, 0).X, 3);
        Assert.Equal(m, ImageColor.Brightness(50) * ImageColor.Contrast(100));
    }

    [Fact]
    public void Saturation_And_Hue_KeepLuma()
    {
        foreach (var colour in new[] { new Vector3(1, 0, 0), new Vector3(0.2f, 0.7f, 0.4f) })
        {
            Assert.Equal(Luma(colour), Luma(Apply(ImageColor.Saturation(-60), colour.X, colour.Y, colour.Z)), 3);
        }

        var grey = Apply(ImageColor.Saturation(-100), 1, 0, 0);
        Assert.Equal(grey.X, grey.Y, 3);
        Assert.Equal(grey.Y, grey.Z, 3);
        // The SVG hue matrix keeps its own (Rec. 709-like) luma: a grey stays grey whatever the angle.
        var turned = Apply(ImageColor.Hue(77), 0.5f, 0.5f, 0.5f);
        Assert.Equal(0.5f, turned.X, 3);
        Assert.Equal(0.5f, turned.Z, 3);
    }

    [Fact]
    public void Hue_Minus180_Is180_And120_TurnsRedGreen()
    {
        Assert.True(Matrix4x4.Equals(ImageColor.Hue(180), ImageColor.Hue(-180)) || Close(ImageColor.Hue(180), ImageColor.Hue(-180)));
        var red = Apply(ImageColor.Hue(120), 1, 0, 0);
        Assert.True(red.Y > red.X && red.Y > red.Z, red.ToString());

        static bool Close(Matrix4x4 a, Matrix4x4 b) => Math.Abs(a.M11 - b.M11) < 1e-4 && Math.Abs(a.M12 - b.M12) < 1e-4 && Math.Abs(a.M21 - b.M21) < 1e-4 && Math.Abs(a.M33 - b.M33) < 1e-4;
    }

    [Fact]
    public void Tint_AtFull_IsTheColourAtThePixelsLuma()
    {
        var white = Apply(ImageColor.Tint(Color.FromArgb(255, 0, 0, 255), 100), 1, 1, 1);
        Assert.Equal(new Vector3(0, 0, 1), new Vector3(MathF.Round(white.X, 3), MathF.Round(white.Y, 3), MathF.Round(white.Z, 3)));
        Assert.True(ImageColor.Tint(Color.Red, 0).IsIdentity);
    }

    [Theory]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#FF8000", 255, 255, 128, 0)]
    [InlineData("80ff0000", 128, 255, 0, 0)]
    [InlineData(" White ", 255, 255, 255, 255)]
    [InlineData("transparent", 0, 0, 0, 0)]
    public void TryParseColor_HexAndNames(string text, int a, int r, int g, int b)
    {
        Assert.True(ImageColor.TryParseColor(text, out var colour));
        Assert.Equal(Color.FromArgb(a, r, g, b), colour);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12")]
    [InlineData("#gggggg")]
    [InlineData("chartreuse")]
    public void TryParseColor_RefusesTheRest(string text)
    {
        Assert.False(ImageColor.TryParseColor(text, out _));
    }
}
