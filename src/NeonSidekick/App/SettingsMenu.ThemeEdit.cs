using System.Text;
using System.Text.Json.Nodes;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/theme edit [&lt;name&gt;]</c> (2026-11-16, plan sharded-hugging-puppy #54, the user's ask: a theme of their own edited in the app, not by
/// hand in its JSON): the theme's fifteen colors as a list, each with its swatch and hex and what it colors, the theme drawn live beside it
/// (<see cref="ThemePreview"/>, never put in force), and the contrast bars it misses under it (<see cref="ThemeChecks"/>, warnings that never
/// block a save). ←/→ step the cursor's color lighter or darker; Enter opens the color picker (hue, saturation and lightness bars, and a typed
/// hex value: the user's pick after the mockup); <c>s</c> saves, <c>r</c> puts the row back, Esc with changes asks. A built-in can't be
/// changed: its <c>&lt;name&gt;-custom</c> copy is offered, made by <c>/theme export</c>'s path, or the one already made. A save rewrites
/// only the file's <c>colors</c> (a <see cref="JsonNode"/> edit: its gradient, styles, description, unknown keys and key order stay; a JSONC
/// comment does not), atomically, asking first when the file changed on disk since the editor opened.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The lightness step of the editor's ←/→ (a Shift press is five).</summary>
    internal const double EditLightnessStep = 4;

    /// <summary>The picker's hue step in degrees, and its saturation and lightness step in percent (a Shift press is five).</summary>
    internal const double PickerHueStep = 2;
    internal const double PickerPercentStep = 1;

    /// <summary>The fewest and the most cells a picker bar takes.</summary>
    internal const int PickerBarMin = 12;
    internal const int PickerBarMax = 34;

    private const int RoleColumn = 10;

    private static readonly IReadOnlyList<MenuButton> EditButtons =
        [new(ThemeText.SaveButton, 's'), new(ThemeText.ResetButton, 'r'), new(ThemeText.DiscardButton, 'd')];

    /// <summary>
    /// The editor: true when it saved the theme in force (so <c>/theme</c> repaints the screen in it, as a pick does), false for another
    /// theme, a discard, a refusal or Esc.
    /// </summary>
    private async Task<bool> EditThemeAsync(string args, CancellationToken cancellationToken)
    {
        string request = args.Trim();
        var scan = ScanThemes();
        if (!ThemeName.TryParse(request.Length > 0 ? request : Theme.Current.Name, scan.Themes, out var palette))
        {
            Flow.Error(ThemeNameError(request, scan.Names));
            return false;
        }

        if (!_pane.Enabled)
        {
            Flow.Error(ThemeText.EditNeedsPane);
            return false;
        }

        (bool InForce, string? Notice) outcome = (false, null);
        var held = new List<(bool Error, string Text)>();
        try
        {
            if (palette.IsBuiltIn)
            {
                if (await CopyToEditAsync(palette, held, cancellationToken).ConfigureAwait(false) is not { } copy)
                {
                    return false;
                }

                palette = copy;
            }

            outcome = await EditLoopAsync(palette, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pane.Close();
        }

        // Said once the pane is gone, so the lines land in the transcript, not under the pane: the copy's, then the save's.
        foreach (var (error, text) in held)
        {
            if (error)
            {
                Flow.Error(text);
            }
            else
            {
                Flow.Notice(text);
            }
        }

        if (outcome.Notice is { } notice)
        {
            Flow.Notice(notice);
        }

        return outcome.InForce;
    }

    /// <summary>A built-in's copy to edit: the one made before, or one made now on a yes; null on a no or a failed export.</summary>
    private async Task<ThemePalette?> CopyToEditAsync(ThemePalette builtIn, List<(bool Error, string Text)> held, CancellationToken cancellationToken)
    {
        string copyName = builtIn.Name + "-custom";
        var scan = ScanThemes();
        if (ThemeName.TryParse(copyName, scan.Themes, out var existing) && !existing.IsBuiltIn)
        {
            return await ConfirmAsync(ThemeText.EditCopyQuestion(builtIn.Name, copyName), cancellationToken, close: false).ConfigureAwait(false) ? existing : null;
        }

        if (!await ConfirmAsync(ThemeText.EditBuiltInQuestion(builtIn.Name, copyName), cancellationToken, close: false).ConfigureAwait(false))
        {
            return null;
        }

        ExportTheme(builtIn.Name, held);
        return ThemeName.TryParse(copyName, ScanThemes().Themes, out var made) && !made.IsBuiltIn ? made : null;
    }

    /// <summary>The editor's list until a save, a discard or Esc: whether the save was of the theme in force, and its notice (null for none saved).</summary>
    private async Task<(bool InForce, string? Notice)> EditLoopAsync(ThemePalette palette, CancellationToken cancellationToken)
    {
        string path = palette.SourcePath!;
        DateTime opened = File.GetLastWriteTimeUtc(path);
        var original = palette.Colors();
        var working = (Color[])original.Clone();
        int cursor = 0;
        while (true)
        {
            var current = WithColors(palette, working);
            var page = new MenuPage(ThemeText.EditTitle(palette.Name), EditRows(current, original), ThemeText.EditHint)
            {
                Buttons = EditButtons,
                Nudges = true,
                Side = (_, width, rows) => ThemePreview.Lines(current, width, rows, SidekickApp.BannerTitle.Trim(), SidekickApp.Version, EffectiveNow().ThemedBackground, EffectiveNow().DiffCollapseCount),
                Footer = (_, _) => new MenuFooter(ChecksBand(current)),
            };
            if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                if (!working.SequenceEqual(original)
                    && !await ConfirmAsync(ThemeText.EditDiscardQuestion, cancellationToken, close: false).ConfigureAwait(false))
                {
                    continue;
                }

                return (false, null);
            }

            cursor = pick.Row;
            if (pick.Nudge != 0)
            {
                working[cursor] = HslColor.From(working[cursor]).WithLightness(pick.Nudge * EditLightnessStep).ToColor();
                continue;
            }

            switch (pick.Button)
            {
                case 0:
                    if (await SaveEditAsync(palette, path, opened, working, original, cancellationToken).ConfigureAwait(false) is { } saved)
                    {
                        return saved;
                    }

                    continue;
                case 1:
                    working[cursor] = original[cursor];
                    continue;
                case 2:
                    return (false, null);
            }

            if (await PickColorAsync(current, (ThemeColorSlot)cursor, original[cursor], cancellationToken).ConfigureAwait(false) is { } picked)
            {
                working[cursor] = picked;
            }
        }
    }

    /// <summary>
    /// The save: only <c>colors</c> rewritten, atomically. Null when nothing was written and the editor goes on (no changes, a changed file
    /// kept, a failure: said on the status line); else whether the theme saved is the one in force, and the line saying so.
    /// </summary>
    private async Task<(bool InForce, string Notice)?> SaveEditAsync(ThemePalette palette, string path, DateTime opened, Color[] working, Color[] original, CancellationToken cancellationToken)
    {
        if (working.SequenceEqual(original))
        {
            _pane.Notice(ThemeText.EditUnchanged);
            return null;
        }

        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != opened
            && !await ConfirmAsync(ThemeText.EditChangedQuestion(path), cancellationToken, close: false).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            WorkingDirectory.WriteAtomically(path, Encoding.UTF8.GetBytes(WithColorsJson(File.ReadAllText(path), working)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _pane.Error(ThemeText.EditFailed(ex.Message));
            return null;
        }

        bool inForce = string.Equals(Theme.Current.Name, palette.Name, StringComparison.Ordinal)
            && string.Equals(Theme.Current.SourcePath, palette.SourcePath, StringComparison.Ordinal);
        return (inForce, inForce ? ThemeText.EditSaved(palette.Name, path) : ThemeText.EditSavedNotInForce(palette.Name, path));
    }

    /// <summary>
    /// <paramref name="fileText"/> with its <c>colors</c> object's fifteen roles set to <paramref name="colors"/>: the object's other keys and
    /// order kept, a role it lacks added, and every other key of the file as it was. Pure.
    /// </summary>
    internal static string WithColorsJson(string fileText, IReadOnlyList<Color> colors)
    {
        if (JsonNode.Parse(fileText, documentOptions: SettingsImportFixups.ReadOptions) is not JsonObject root)
        {
            throw new System.Text.Json.JsonException(ThemeText.NotJson("not a JSON object"));
        }

        if (root["colors"] is not JsonObject block)
        {
            block = new JsonObject();
            root["colors"] = block;
        }

        for (int i = 0; i < ThemeKeys.Colors.Count; i++)
        {
            block[ThemeKeys.Colors[i]] = Theme.ToHex(colors[i]);
        }

        return root.ToJsonString(SettingsImportFixups.Indented) + "\n";
    }

    /// <summary>One row per color role: the role, its swatch on the page's color, its hex, a mark when changed, and what it colors.</summary>
    private static List<string> EditRows(ThemePalette current, IReadOnlyList<Color> original)
    {
        var rows = new List<string>(ThemeKeys.Colors.Count);
        string bg = Theme.ToHex(current.Bg);
        for (int i = 0; i < ThemeKeys.Colors.Count; i++)
        {
            var color = current.ColorOf((ThemeColorSlot)i);
            string changed = color.Equals(original[i]) ? " " : "•";
            rows.Add(Markup.Escape(ThemeKeys.Colors[i].PadRight(RoleColumn)) + $" [{Theme.ToHex(color)} on {bg}]██[/] " + Theme.ToHex(color) + " " + changed + "  "
                + $"[{Theme.DimText.ToMarkup()}]" + Markup.Escape(ThemeText.RoleNotes[i]) + "[/]");
        }

        return rows;
    }

    /// <summary>The band under the list: the pairs under their bars, or that all are met.</summary>
    private static string ChecksBand(ThemePalette palette) =>
        ThemeChecks.Problems(palette) is { Count: > 0 } problems ? ThemeText.ChecksWarning(problems) : ThemeText.ChecksOk;

    /// <summary><paramref name="palette"/> with <paramref name="colors"/> as its fifteen roles, in <see cref="ThemeColorSlot"/> order.</summary>
    internal static ThemePalette WithColors(ThemePalette palette, IReadOnlyList<Color> colors) => palette with
    {
        Primary = colors[0], Secondary = colors[1], Tertiary = colors[2], Deep = colors[3], Highlight = colors[4], Warm = colors[5], Tint = colors[6],
        Ink = colors[7], Dim = colors[8], Dimmer = colors[9], Bg = colors[10], PanelBg = colors[11], Good = colors[12], Bad = colors[13], Warn = colors[14],
    };

    // ── The color picker ────────────────────────────────────────────────────

    /// <summary>
    /// The color picker for <paramref name="slot"/> (2026-11-16, the user's pick: hue, saturation and lightness bars over a swatch grid or the
    /// system's dialog, so it works alike in Windows Terminal, on a Mac and over SSH): ↑↓ pick a bar, ←/→ move it, the theme's preview beside
    /// it drawn with the candidate; Enter on a bar uses the color, Enter on the hex row types one; Esc goes back with the color as it was
    /// (null). The starting color is kept as it is until a bar moves, so a value that only went out and back is never rewritten.
    /// </summary>
    private async Task<Color?> PickColorAsync(ThemePalette current, ThemeColorSlot slot, Color was, CancellationToken cancellationToken)
    {
        var start = current.ColorOf(slot);
        var hsl = HslColor.From(start);
        Color? exact = start;
        int cursor = 0;
        while (true)
        {
            var candidate = exact ?? hsl.ToColor();
            var shown = WithColors(current, current.Colors().Select((c, i) => i == (int)slot ? candidate : c).ToArray());
            var page = new MenuPage(ThemeText.PickerTitle(current.Name, ThemeKeys.Colors[(int)slot], Theme.ToHex(was)), PickerRows(hsl, candidate, current.Bg, PickerBarWidth()), ThemeText.PickerHint)
            {
                Nudges = true,
                Side = (_, width, rows) => ThemePreview.Lines(shown, width, rows, SidekickApp.BannerTitle.Trim(), SidekickApp.Version, EffectiveNow().ThemedBackground, EffectiveNow().DiffCollapseCount),
            };
            if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return null;
            }

            cursor = pick.Row;
            if (pick.Nudge != 0)
            {
                if (cursor < 3)
                {
                    hsl = Step(hsl, cursor, pick.Nudge);
                    exact = null;
                }

                continue;
            }

            if (cursor < 3)
            {
                return candidate;
            }

            var typed = await _pane.EditAsync(page with { Hint = EditKeys }, cursor, _input, Theme.ToHex(candidate), allowEmpty: false, cancellationToken).ConfigureAwait(false);
            if (typed is not InputResult.Submitted { Text: var text })
            {
                continue;
            }

            if (ThemeFile.TryParseColor(text, out var parsed))
            {
                return parsed;
            }

            _pane.Error(ThemeText.PickerBadHex(text.Trim()));
        }
    }

    /// <summary>Bar <paramref name="bar"/> (0 hue, 1 saturation, 2 lightness) moved <paramref name="steps"/> steps: the hue wraps, the others stop at their ends. Pure.</summary>
    internal static HslColor Step(HslColor hsl, int bar, int steps) => bar switch
    {
        0 => hsl.WithHue(steps * PickerHueStep),
        1 => hsl.WithSaturation(steps * PickerPercentStep),
        _ => hsl.WithLightness(steps * PickerPercentStep),
    };

    /// <summary>The bars' width in the pane as it is now: the list's cells less the label, the value and the ends, held to its range.</summary>
    private int PickerBarWidth() => Math.Clamp(_pane.ListWidth - 2 - 12 - 2 - 6, PickerBarMin, PickerBarMax);

    /// <summary>
    /// The picker's four rows: each bar <paramref name="width"/> cells, a cell the color at that point with the others held, the current
    /// point a <c>┃</c> in whichever of black and white reads on it, and the value after it; then the hex row with the candidate's swatch. Pure.
    /// </summary>
    internal static List<string> PickerRows(HslColor hsl, Color candidate, Color bg, int width)
    {
        string Bar(Func<double, HslColor> at, double position)
        {
            var bar = new StringBuilder("▕");
            int marker = (int)Math.Round(Math.Clamp(position, 0, 1) * (width - 1));
            for (int i = 0; i < width; i++)
            {
                var cell = at(width == 1 ? 0 : i / (double)(width - 1)).ToColor();
                bar.Append(i == marker
                    ? $"[{Theme.ToHex(ColorContrast.IsLight(cell) ? Color.Black : Color.White)} on {Theme.ToHex(cell)}]┃[/]"
                    : $"[{Theme.ToHex(cell)}]█[/]");
            }

            return bar.Append('▏').ToString();
        }

        string Value(string text) => " " + text.PadLeft(4);
        return
        [
            ThemeText.PickerHue.PadRight(12) + Bar(t => hsl with { Hue = t * 360 }, hsl.Hue / 360) + Value(Whole(hsl.Hue) + "°"),
            ThemeText.PickerSaturation.PadRight(12) + Bar(t => hsl with { Saturation = t * 100 }, hsl.Saturation / 100) + Value(Whole(hsl.Saturation) + "%"),
            ThemeText.PickerLightness.PadRight(12) + Bar(t => hsl with { Lightness = t * 100 }, hsl.Lightness / 100) + Value(Whole(hsl.Lightness) + "%"),
            ThemeText.PickerHex.PadRight(12) + $"[{Theme.ToHex(candidate)} on {Theme.ToHex(bg)}]██████[/] " + Theme.ToHex(candidate),
        ];
    }

    private static string Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
