using NeonSidekick.Files;

namespace NeonSidekick.Tests;

/// <summary><see cref="RegularFiles"/> (2026-10-31, review P5-1, P5-2: the custom commands' walk and read go through it too).</summary>
public sealed class RegularFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "neon-regular-" + Guid.NewGuid().ToString("N"));

    public RegularFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void IsPlain_IsAFileOnly()
    {
        string file = Path.Combine(_dir, "x.md");
        File.WriteAllText(file, "x");

        Assert.True(RegularFiles.IsPlain(file));
        Assert.False(RegularFiles.IsPlain(_dir));
        Assert.False(RegularFiles.IsPlain(Path.Combine(_dir, "missing.md")));
    }

    [Fact]
    public void ReadAtMost_ReadsUpToTheCap_AndNothingOverIt()
    {
        string file = Path.Combine(_dir, "x.md");
        File.WriteAllBytes(file, new byte[100]);

        Assert.Equal(100, RegularFiles.ReadAtMost(file, 100)!.Length);
        Assert.Null(RegularFiles.ReadAtMost(file, 99));
    }
}
