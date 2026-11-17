using System.Text.Json.Nodes;
using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/theme edit</c>'s pure parts (2026-11-16, plan sharded-hugging-puppy #54): <see cref="HslColor"/>'s round trips and steps, the color
/// picker's bars, and the save's <c>colors</c>-only rewrite. The editor itself is driven in <c>ChatScreenTests.ThemeEdit</c>.
/// </summary>
public sealed class ThemeEditTests
{
    [Fact]
    public void Hsl_EveryBuiltInColor_RoundTripsExactly()
    {
        var failures = ThemeLibrary.All
            .SelectMany(t => t.Colors().Select((c, i) => (t.Name, Role: ThemeKeys.Colors[i], Color: c)))
            .Where(x => !HslColor.From(x.Color).ToColor().Equals(x.Color))
            .Select(x => $"{x.Name} {x.Role} {Theme.ToHex(x.Color)} → {Theme.ToHex(HslColor.From(x.Color).ToColor())}")
            .ToList();
        Assert.Empty(failures);

        for (int v = 0; v < 256; v += 5)
        {
            var c = new Color((byte)v, (byte)(255 - v), (byte)((v * 7) % 256));
            Assert.Equal(c, HslColor.From(c).ToColor());
        }
    }

    [Fact]
    public void Hsl_KnownValues_AndSteps_HueWraps_TheOthersStopAtTheirEnds()
    {
        var red = HslColor.From(new Color(255, 0, 0));
        Assert.Equal((0d, 100d, 50d), (red.Hue, red.Saturation, red.Lightness));
        Assert.Equal(new Color(0, 0, 255), new HslColor(240, 100, 50).ToColor());
        Assert.Equal(new Color(128, 128, 128), HslColor.From(new Color(128, 128, 128)).ToColor());
        Assert.Equal(0, HslColor.From(new Color(128, 128, 128)).Saturation);

        Assert.Equal(350, SettingsMenu.Step(red, 0, -5).Hue);                         // 0° − 5 × 2°, round the circle
        Assert.Equal(4, SettingsMenu.Step(red, 0, 2).Hue);
        Assert.Equal(100, SettingsMenu.Step(red, 1, 5).Saturation);                   // already full
        Assert.Equal(95, SettingsMenu.Step(red, 1, -5).Saturation);
        Assert.Equal(0, SettingsMenu.Step(red with { Lightness = 3 }, 2, -5).Lightness);
        Assert.Equal(100, SettingsMenu.Step(red with { Lightness = 98 }, 2, 5).Lightness);
        Assert.Equal(new HslColor(0, 100, 50), red.WithLightness(0));
    }

    /// <summary>The picker's four rows: three bars of the width asked, each with one marker cell where its value sits, the value after it; then the hex row.</summary>
    [Fact]
    public void PickerRows_ThreeBarsWithAMarker_ThenTheHexRow()
    {
        var hsl = new HslColor(120, 50, 25);
        var rows = SettingsMenu.PickerRows(hsl, hsl.ToColor(), new Color(0, 0, 0), 20);

        Assert.Equal(4, rows.Count);
        string[] plain = rows.Select(r => Markup.Remove(r)).ToArray();
        Assert.StartsWith(ThemeText.PickerHue, plain[0]);
        Assert.EndsWith(" 120°", plain[0]);
        Assert.EndsWith("  50%", plain[1]);
        Assert.EndsWith("  25%", plain[2]);
        Assert.Contains(Theme.ToHex(hsl.ToColor()), plain[3]);
        foreach (string bar in plain.Take(3))
        {
            Assert.Equal(1, bar.Count(c => c == '┃'));
            Assert.Equal(19, bar.Count(c => c == '█'));
        }

        // The marker sits where the value is: the hue at a third of the way along, the lightness at a quarter.
        Assert.Equal((int)Math.Round(19 / 3.0), plain[0].IndexOf('┃', StringComparison.Ordinal) - plain[0].IndexOf('▕', StringComparison.Ordinal) - 1);
        Assert.Equal((int)Math.Round(19 * 0.25), plain[2].IndexOf('┃', StringComparison.Ordinal) - plain[2].IndexOf('▕', StringComparison.Ordinal) - 1);

        // Each cell is the color at its point: the hue bar's first cell is pure red at any saturation and lightness (2026-11-17, #182), the
        // saturation bar's first a mid-gray, the lightness bar's first black.
        Assert.Contains("[#FF0000]█[/]", rows[0], StringComparison.Ordinal);
        Assert.Contains($"[{Theme.ToHex(new HslColor(120, 0, 50).ToColor())}]█[/]", rows[1], StringComparison.Ordinal);
        Assert.Contains("[#000000]█[/]", rows[2], StringComparison.Ordinal);

        // On white the hue and saturation bars still show their colors, not one white run.
        var white = SettingsMenu.PickerRows(new HslColor(0, 0, 100), Color.White, new Color(0, 0, 0), 20);
        Assert.Contains("[#FF0000]█[/]", white[0], StringComparison.Ordinal);
        Assert.Contains($"[{Theme.ToHex(new HslColor(0, 100, 50).ToColor())}]█[/]", white[1], StringComparison.Ordinal);
        Assert.Contains('▕', Markup.Remove(white[3]));
        Assert.All(rows, r => Assert.True(Cells(Markup.Remove(r)) <= 119, r));
    }

    /// <summary>The save rewrites the fifteen roles and nothing else: the description, gradient, styles, an unknown key and the key order stay.</summary>
    [Fact]
    public void WithColorsJson_ReplacesOnlyTheColors_KeepingEveryOtherKeyInOrder()
    {
        string file = ThemeJson.File("\"name\": \"mine\", \"description\": \"my own\", \"gradient\": [\"#111111\", \"#222222\"], \"styles\": { \"user\": { \"bold\": true } }, \"extra\": 7");
        var colors = ShippedThemes.Synthwave.Colors();
        colors[8] = new Color(0x12, 0x34, 0x56);

        var root = JsonNode.Parse(SettingsMenu.WithColorsJson(file, colors))!.AsObject();

        Assert.Equal(["name", "description", "gradient", "styles", "extra", "colors"], root.Select(p => p.Key));
        Assert.Equal("#123456", (string?)root["colors"]!["dim"]);
        Assert.Equal(Theme.ToHex(colors[0]), (string?)root["colors"]!["primary"]);
        Assert.Equal(7, (int)root["extra"]!);
        Assert.True((bool)root["styles"]!["user"]!["bold"]!);
        Assert.Equal("my own", (string?)root["description"]);

        // A file with no colors block gets one; a file that is no object is refused.
        var made = JsonNode.Parse(SettingsMenu.WithColorsJson("{ \"name\": \"x\" }", colors))!.AsObject();
        Assert.Equal(15, made["colors"]!.AsObject().Count);
        Assert.Throws<System.Text.Json.JsonException>(() => SettingsMenu.WithColorsJson("[1]", colors));
    }

    [Fact]
    public void EditWord_IsNoNewName_ButAnOldEditFileStillLoads()
    {
        Assert.True(ThemeFile.IsValidName(ThemeText.EditWord));
        Assert.False(ThemeFile.IsValidNewName(ThemeText.EditWord));
        Assert.False(ThemeFile.IsValidNewName(ThemeText.ExportWord));
        Assert.True(ThemeFile.IsValidNewName("mine-2"));
    }

    [Fact]
    public void TheHints_FitTheWindow()
    {
        Assert.True(Cells(ThemeText.EditHint) <= 119);
        Assert.True(Cells(ThemeText.PickerHint) <= 119);
    }

    private static int Cells(string text) => TextCells.Width(text);
}
