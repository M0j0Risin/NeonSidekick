using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
/// hex value: the user's pick after the mockup); <c>s</c> saves, <c>r</c> puts the row back, Esc with changes asks Keep editing, Save or Discard (2026-11-26, the user's ask; the <c>d</c> key and its discard button went the same day, since the question covers them). A built-in
/// can't be changed: its <c>&lt;name&gt;-custom</c> copy is offered, made by <c>/theme export</c>'s path, or the one already made. A save
/// changes only the roles changed, in the file's own bytes (<see cref="ThemeFileSplice"/>, 2026-11-17, the admin-themes-render review's #25,
/// #26, #156, #163: its comments, layout, key spelling and line endings stay), atomically, asking first when the file changed on disk since the
/// editor opened, or offering to write it again when it is gone (#214). Every line it says is held (<see cref="HeldNotices"/>) and printed by
/// <c>/theme</c> after any repaint, so none is wiped (#18, #34).
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

    /// <summary>
    /// The editor's band rows (2026-11-24, the tidier pane): the cursor's role note in full and its contrast lines. One number for the page's
    /// <see cref="MenuPage.FooterRows"/> and <see cref="BandLines"/>, so the band never hands the pane more lines than fit (whose own
    /// <c>+n more</c> would sit under <see cref="ThemeText.ChecksMore"/>). The left side padded blank rows beside the preview anyway.
    /// </summary>
    internal const int ThemeEditBandRows = 8;

    /// <summary>The most rows the band's role note takes.</summary>
    private const int BandNoteRows = 3;

    private static readonly IReadOnlyList<MenuButton> EditButtons =
        [new(ThemeText.SaveButton, 's'), new(ThemeText.ResetButton, 'r')];

    /// <summary>The fallback save's writer: indented, and a <c>&lt;</c> or an accent in a description written as itself.</summary>
    private static readonly JsonSerializerOptions RelaxedIndented = new(SettingsImportFixups.Indented) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The theme file as the editor opened it (2026-11-17, #66, #165): its bytes, their text and encoding, and its write time, read together
    /// once the theme is chosen, so the changed-file check and a gone file's rewrite start from what the editor showed.
    /// </summary>
    private sealed record OpenedTheme(ThemePalette Palette, byte[] Bytes, string Text, DateTime Time);

    /// <summary>
    /// Lines said while the pane is open (2026-11-17, #18, #34, #168): the editor's and its copy's, held until <c>/theme</c> has repainted
    /// (<see cref="PrintHeld"/>), so the repaint never wipes them and none lands under the pane.
    /// </summary>
    private sealed class HeldNotices : INoticeSink
    {
        private readonly List<(int Kind, string Text)> _lines = [];

        public void Notice(string text) => _lines.Add((0, text));

        public void Warning(string text) => _lines.Add((1, text));

        public void Error(string text) => _lines.Add((2, text));

        /// <summary>The lines held so far (2026-11-20: the <c>/theme</c> list's Esc says unchanged only when it held none).</summary>
        public int Count => _lines.Count;

        /// <summary>The errors held after the first <paramref name="count"/> lines (2026-11-20: an editor visit's failures, for the list's status line).</summary>
        public IEnumerable<string> ErrorsSince(int count) => _lines.Skip(count).Where(line => line.Kind == 2).Select(line => line.Text);

        /// <summary>The held lines to <paramref name="sink"/>, in the order said, then forgotten.</summary>
        public void PrintTo(INoticeSink sink)
        {
            foreach (var (kind, text) in _lines)
            {
                switch (kind)
                {
                    case 0: sink.Notice(text); break;
                    case 1: sink.Warning(text); break;
                    default: sink.Error(text); break;
                }
            }

            _lines.Clear();
        }
    }

    private readonly HeldNotices _held = new();

    /// <summary><c>/theme</c>'s held lines (<see cref="HeldNotices"/>) to the transcript; called after any repaint, and on every path.</summary>
    public void PrintHeld() => _held.PrintTo(Flow);

    /// <summary>
    /// The editor: true when it saved the theme in force (so <c>/theme</c> repaints the screen in it, as a pick does), false for another
    /// theme, Discard on the question, a refusal or Esc. Its lines are held (<see cref="PrintHeld"/>).
    /// </summary>
    private async Task<bool> EditThemeAsync(string args, CancellationToken cancellationToken)
    {
        string request = args.Trim();
        // The name tried (2026-11-17, #93): the theme in force when none is given, so a miss never reads 'No theme named ""'.
        string asked = request.Length > 0 ? request : Theme.Current.Name;
        var scan = ScanThemes();
        if (!ThemeName.TryParse(asked, scan.Themes, out var palette))
        {
            _held.Error(ThemeNameError(asked, scan.Names));
            return false;
        }

        if (!_pane.Enabled)
        {
            _held.Error(ThemeText.EditNeedsPane);
            return false;
        }

        return await EditPaletteAsync(palette, scan, closeWhenDone: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The editor on <paramref name="palette"/> (a built-in through its copy), <paramref name="scan"/> the caller's so warnings print once (#2,
    /// #142); true when it saved the theme in force. <paramref name="closeWhenDone"/> (2026-11-20, the memory-remember review's #3, #72): the
    /// pane closed after, as <c>/theme edit</c> leaves it; false for the <c>/theme</c> list, which comes back, and then an unchanged exit (Esc,
    /// a Discard, the file gone, a No to the copy) holds nothing, so "unchanged" never leads a later pick's lines. Its other lines are held.
    /// </summary>
    private async Task<bool> EditPaletteAsync(ThemePalette palette, ThemeScan scan, bool closeWhenDone, CancellationToken cancellationToken)
    {
        (bool InForce, string Notice) outcome;
        try
        {
            var opened = palette.IsBuiltIn
                ? await CopyToEditAsync(palette, scan, holdUnchanged: closeWhenDone, cancellationToken).ConfigureAwait(false)
                : Open(palette);
            if (opened is null)
            {
                return false;
            }

            outcome = await EditLoopAsync(opened, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (closeWhenDone)
            {
                _pane.Close();
            }
        }

        if (closeWhenDone || !string.Equals(outcome.Notice, UnchangedNotice, StringComparison.Ordinal))
        {
            _held.Notice(outcome.Notice);
        }

        return outcome.InForce;
    }

    /// <summary>
    /// A built-in's copy to edit: the one made before, or one made now on a yes, read as it stands; null on a no (<see cref="UnchangedNotice"/>
    /// held while <paramref name="holdUnchanged"/>: the <c>/theme</c> list's No holds nothing, 2026-11-20), a failed export or an unreadable file
    /// (the error held). <paramref name="scan"/> is the command's own, so warnings print once (#59, #142).
    /// </summary>
    private async Task<OpenedTheme?> CopyToEditAsync(ThemePalette builtIn, ThemeScan scan, bool holdUnchanged, CancellationToken cancellationToken)
    {
        void No()
        {
            if (holdUnchanged)
            {
                _held.Notice(UnchangedNotice);
            }
        }

        string copyName = ThemeText.CopyName(builtIn.Name);
        if (ThemeName.TryParse(copyName, scan.Themes, out var existing) && !existing.IsBuiltIn)
        {
            if (!await ConfirmAsync(ThemeText.EditCopyQuestion(builtIn.Name, copyName), cancellationToken, close: false).ConfigureAwait(false))
            {
                No();
                return null;
            }

            return Open(existing);
        }

        if (!await ConfirmAsync(ThemeText.EditBuiltInQuestion(copyName), cancellationToken, close: false).ConfigureAwait(false))
        {
            No();
            return null;
        }

        if (ExportTheme(builtIn.Name, _held, quiet: true, scan) is not { } made
            || !ThemeName.TryParse(made, ScanThemes(report: false).Themes, out var copy) || copy.IsBuiltIn)
        {
            return null;
        }

        var opened = Open(copy);
        if (opened is not null)
        {
            _held.Notice(ThemeText.EditCopied(builtIn.Name, made, copy.SourcePath!));
        }

        return opened;
    }

    /// <summary>A user theme's file read as the editor opens it: the write time first, so a write in between reads as a change; null (the error held) when it can't be read.</summary>
    private OpenedTheme? Open(ThemePalette palette)
    {
        string path = palette.SourcePath!;
        try
        {
            var time = File.GetLastWriteTimeUtc(path);
            byte[] bytes = File.ReadAllBytes(path);
            return new OpenedTheme(palette, bytes, TextFile.Decode(bytes, out _), time);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _held.Error(ThemeText.EditUnreadable(ex.Message));
            return null;
        }
    }

    /// <summary>
    /// The editor's list until a save or Esc: whether the save was of the theme in force, and the line to say (the save's, or
    /// <see cref="UnchangedNotice"/>, #195). A pane dismissed by a double-click off it, or the token, leaves at once with the edits dropped (#75).
    /// Esc with changes asks <see cref="AskLeaveAsync"/> (2026-11-26, the user's ask, in place of the yes/no): Save is <c>s</c>'s own.
    /// </summary>
    private async Task<(bool InForce, string Notice)> EditLoopAsync(OpenedTheme opened, CancellationToken cancellationToken)
    {
        var palette = opened.Palette;
        var original = palette.Colors();
        var working = (Color[])original.Clone();
        // Each row's hue, saturation and lightness kept through the nudges (2026-11-17, #6): at 0 or 100 lightness the color alone has no hue.
        var hsls = original.Select(HslColor.From).ToArray();
        var data = ThemeFile.Parse(opened.Text, out _);
        bool Gone() => _pane.Dismissed || cancellationToken.IsCancellationRequested;
        bool Changed() => !working.SequenceEqual(original);
        // Leaving by Esc (2026-11-26): at once without changes or with the pane gone; else the question. Null keeps the editor.
        async Task<(bool InForce, string Notice)?> LeaveAsync()
        {
            if (Gone() || !Changed())
            {
                return (false, UnchangedNotice);
            }

            var choice = await AskLeaveAsync(palette.Name, cancellationToken).ConfigureAwait(false);
            if (Gone())
            {
                return (false, UnchangedNotice);   // the pane dismissed or cancelled: quiet
            }

            if (choice == LeaveChoice.Discard)
            {
                // Said, as the wizards' and the workflow Edit's Discard is (2026-11-27, the last-3 review's #13): the quiet notice read as no change.
                return (false, ComfyEditDiscardedNotice);
            }

            if (choice == LeaveChoice.Save)
            {
                if (await SaveEditAsync(opened, working, original, cancellationToken).ConfigureAwait(false) is { } saved)
                {
                    return saved;
                }

                if (Gone())
                {
                    return (false, UnchangedNotice);
                }
            }

            return null;   // Keep editing, or a save refused (said): the editor stays
        }

        int cursor = 0;
        while (true)
        {
            // Once a pass (#151): the preview through the file's own styles and gradient (#38, #99), its band and the settings it reads.
            var current = Preview(palette, data, working);
            var problems = ThemeChecks.Problems(current);
            var effective = EffectiveNow();
            // The band (2026-11-24, the user's pick "Notes in the band"): the cursor's role note whole, then its contrast; a resize rebuilds the page.
            int bandWidth = _pane.ListWidth(side: true) - TextCells.Width(MenuPane.NoPointer);
            var page = new MenuPage(ThemeText.EditTitle(palette.Name), EditRows(current, original, problems), ThemeText.EditHint)
            {
                Buttons = EditButtons,
                Nudges = true,
                Side = (_, width, rows) => ThemePreview.Lines(current, width, rows, SidekickApp.BannerTitle.Trim(), SidekickApp.Version, effective.ThemedBackground, effective.DiffCollapseCount),
                Footer = (_, row) => row >= 0 && row < ThemeKeys.Colors.Count ? BandFooter((ThemeColorSlot)row, problems, bandWidth) : null,
                FooterRows = ThemeEditBandRows,
                // The whole mock screen beside the list (2026-11-24): on a tall window its blank rows show and the notices stop crowding.
                SideRows = ThemePreview.ScreenRows(effective.DiffCollapseCount) + MenuPane.FooterRuleRows + ThemeEditBandRows,
            };
            if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                if (await LeaveAsync().ConfigureAwait(false) is { } left)
                {
                    return left;
                }

                continue;
            }

            cursor = pick.Row;
            if (pick.Refresh)
            {
                continue;   // a resize (#114): the rows built again at the new width
            }

            if (pick.Nudge != 0)
            {
                hsls[cursor] = hsls[cursor].WithLightness(pick.Nudge * EditLightnessStep);
                working[cursor] = hsls[cursor].ToColor();
                continue;
            }

            switch (pick.Button)
            {
                case 0:
                    if (await SaveEditAsync(opened, working, original, cancellationToken).ConfigureAwait(false) is { } saved)
                    {
                        return saved;
                    }

                    if (Gone())
                    {
                        return (false, UnchangedNotice);
                    }

                    continue;
                case 1:
                    working[cursor] = original[cursor];
                    hsls[cursor] = HslColor.From(original[cursor]);
                    continue;
            }

            // The picker opens on the row's color as it stands, its "was" that color (#164), and hands back the hue it ended on (#6).
            if (await PickColorAsync(current, (ThemeColorSlot)cursor, working[cursor], hsls[cursor], cancellationToken).ConfigureAwait(false) is { } picked)
            {
                working[cursor] = picked.Color;
                hsls[cursor] = picked.Hsl;
            }
        }
    }

    /// <summary>
    /// The theme as the file would load with <paramref name="colors"/> (#38, #99): <see cref="ThemeFile.Build"/> over the file's own data, so
    /// a derived gradient and a style naming a role follow the new colors; <see cref="WithColors"/> when the file doesn't build.
    /// </summary>
    private static ThemePalette Preview(ThemePalette palette, ThemeFileData? data, IReadOnlyList<Color> colors)
    {
        if (data is null)
        {
            return WithColors(palette, colors);
        }

        data.Colors = Enumerable.Range(0, ThemeKeys.Colors.Count).ToDictionary(i => ThemeKeys.Colors[i], i => Theme.ToHex(colors[i]));
        return ThemeFile.Build(palette.Name, data, palette.SourcePath, new List<string>(), out _) ?? WithColors(palette, colors);
    }

    /// <summary>
    /// The save: only the roles changed written, in the file's own bytes, atomically. Null when nothing was written and the editor goes on (no
    /// changes, a changed or gone file kept, a failure: said on the status line); else whether the theme saved is the one in force, and the
    /// line saying so.
    /// </summary>
    private async Task<(bool InForce, string Notice)?> SaveEditAsync(OpenedTheme opened, Color[] working, Color[] original, CancellationToken cancellationToken)
    {
        var palette = opened.Palette;
        string path = palette.SourcePath!;
        if (working.SequenceEqual(original))
        {
            _pane.Notice(ThemeText.EditUnchanged);
            return null;
        }

        bool gone = !File.Exists(path);
        if (gone
            ? !await ConfirmAsync(ThemeText.EditGoneQuestion(path), cancellationToken, close: false).ConfigureAwait(false)
            : File.GetLastWriteTimeUtc(path) != opened.Time
                && !await ConfirmAsync(ThemeText.EditChangedQuestion(path), cancellationToken, close: false).ConfigureAwait(false))
        {
            return null;
        }

        var changes = Enumerable.Range(0, working.Length).Where(i => !working[i].Equals(original[i]))
            .Select(i => (ThemeKeys.Colors[i], Theme.ToHex(working[i]))).ToList();
        try
        {
            byte[] source = gone ? opened.Bytes : File.ReadAllBytes(path);
            string text = TextFile.Decode(source, out var codec);
            byte[]? bytes = codec.Form is TextForm.Utf8 or TextForm.Utf8Bom ? ThemeFileSplice.SetColors(source, changes) : null;
            bytes ??= codec.Encode(WithColorsJson(text, changes, text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n"));
            if (bytes is null)
            {
                _pane.Error(ThemeText.EditFailed(FileText.CannotEncode(path, codec.Label ?? "UTF-8")));
                return null;
            }

            WorkingDirectory.WriteAtomically(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            _pane.Error(ThemeText.EditFailed(ex.Message));
            return null;
        }

        bool inForce = string.Equals(Theme.Current.Name, palette.Name, StringComparison.Ordinal)
            && string.Equals(Theme.Current.SourcePath, palette.SourcePath, StringComparison.Ordinal);
        return (inForce, inForce ? ThemeText.EditSaved(palette.Name, path) : ThemeText.EditSavedNotInForce(palette.Name, path));
    }

    /// <summary><see cref="WithColorsJson(string, IReadOnlyList{ValueTuple{string, string}}, string)"/> with all fifteen roles set to <paramref name="colors"/>. Pure.</summary>
    internal static string WithColorsJson(string fileText, IReadOnlyList<Color> colors) =>
        WithColorsJson(fileText, Enumerable.Range(0, ThemeKeys.Colors.Count).Select(i => (ThemeKeys.Colors[i], Theme.ToHex(colors[i]))).ToList());

    /// <summary>
    /// The fallback save (a file <see cref="ThemeFileSplice"/> can't splice: no <c>colors</c> object, a reader's refusal, another encoding):
    /// <paramref name="fileText"/> with each of <paramref name="changes"/> set onto the key its <c>colors</c> object has for that role (any
    /// case, in its spelling), a role it lacks added, and every other key as it was; written indented, ending in <paramref name="newline"/>.
    /// Comments don't survive this one. Pure.
    /// </summary>
    internal static string WithColorsJson(string fileText, IReadOnlyList<(string Key, string Hex)> changes, string newline = "\n")
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (JsonNode.Parse(fileText, documentOptions: SettingsImportFixups.ReadOptions) is not JsonObject root)
        {
            throw new JsonException(ThemeText.NotJson(ThemeText.NotAnObject));
        }

        if (root["colors"] is not JsonObject block)
        {
            block = new JsonObject();
            root["colors"] = block;
        }

        foreach (var (key, hex) in changes)
        {
            string spelled = block.Select(p => p.Key).LastOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
            block[spelled] = hex;
        }

        return root.ToJsonString(RelaxedIndented).Replace("\n", newline, StringComparison.Ordinal) + newline;
    }

    /// <summary>
    /// One row per color role: the role, its swatch framed on the page's color (#149: a <c>bg</c> swatch shows), its hex, then two one-cell mark
    /// columns, <c>•</c> when changed and (2026-11-24) <c>⚠</c> in <see cref="Theme.WarnText"/> when a pair it is in reads under its bar
    /// (<paramref name="problems"/>, the pass's), a space for an absent mark so both columns line up. What the role colors lives in the band now
    /// (<see cref="BandLines"/>), whole: on the row it was cut to a few cells.
    /// </summary>
    private static List<string> EditRows(ThemePalette current, IReadOnlyList<Color> original, IReadOnlyList<ContrastProblem> problems)
    {
        var rows = new List<string>(ThemeKeys.Colors.Count);
        string bg = Theme.ToHex(current.Bg);
        string dim = Theme.DimText.ToMarkup();
        string warn = Theme.WarnText.ToMarkup();
        string blank = new(' ', TextCells.Width("⚠"));
        for (int i = 0; i < ThemeKeys.Colors.Count; i++)
        {
            var slot = (ThemeColorSlot)i;
            var color = current.ColorOf(slot);
            string changed = color.Equals(original[i]) ? " " : "•";
            bool poor = problems.Any(p => p.Roles?.Contains(slot) == true);
            rows.Add(Markup.Escape(ThemeKeys.Colors[i].PadRight(RoleColumn)) + $" [{dim}]▕[/][{Theme.ToHex(color)} on {bg}]██[/][{dim}]▏[/] " + Theme.ToHex(color) + " " + changed + " "
                + (poor ? $"[{warn}]⚠[/]" : blank));
        }

        return rows;
    }

    /// <summary>The band as the page's footer (2026-11-24, review #65): its full rows as the text, laid out again for the rows a short pane gives.</summary>
    internal static MenuFooter BandFooter(ThemeColorSlot role, IReadOnlyList<ContrastProblem> problems, int width) =>
        new(string.Join('\n', BandLines(role, problems, width, ThemeEditBandRows)))
        {
            KeepLines = true,
            // Laid out for the rows the slab gives (2026-11-24, review #65): a short pane keeps the worst pairs, never the note alone.
            Layout = rows => string.Join('\n', BandLines(role, problems, width, rows)),
        };

    /// <summary>
    /// The band under the list for <paramref name="role"/> (2026-11-24, the user's pick "Notes in the band"): <c>role: what it colors</c> wrapped to
    /// <paramref name="width"/> (at most three rows), then its contrast into what's left of <paramref name="rows"/>:
    /// <list type="bullet">
    /// <item>every pair met: <c>✓  Contrast OK: every pair meets its bar.</c>;</item>
    /// <item>the role in pairs under their bars: those, one a line, the furthest short of its bar first, a diff slab's code colors folded into one
    /// line at the slab's worst (<see cref="ThemeText.ChecksCodeSlab"/>), then the pairs no role marks (a style on its own ground);</item>
    /// <item>the role meeting its bars while others don't: <see cref="ThemeText.ChecksRoleOk"/>, then the roleless pairs.</item>
    /// </list>
    /// Lines that don't fit end on <see cref="ThemeText.ChecksMore"/> with how many were left out. A wrapped line hangs under its text, past the
    /// glyph's two spaces. Plain text: the band is one style. Pure.
    /// </summary>
    internal static IReadOnlyList<string> BandLines(ThemeColorSlot role, IReadOnlyList<ContrastProblem> problems, int width, int rows)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var band = new List<string>();
        if (width <= 0 || rows <= 0)
        {
            return band;
        }

        string name = ThemeKeys.Colors[(int)role];
        band.AddRange(MenuPane.CaptionRows(name + ": " + ThemeText.RoleNotes[(int)role], width, Math.Min(BandNoteRows, rows)));
        int room = rows - band.Count;
        if (room <= 0)
        {
            return band;
        }

        List<string> lines;
        if (problems.Count == 0)
        {
            lines = [ThemeText.Mark("✓") + ThemeText.ChecksOk];
        }
        else
        {
            var own = problems.Where(p => p.Roles?.Contains(role) == true).ToList();
            var roleless = problems.Where(p => p.Roles is null).ToList();
            lines = own.Count > 0 ? PairLines(own) : [ThemeText.ChecksRoleOk(name, problems.Count)];
            lines.AddRange(PairLines(roleless));
        }

        var wrapped = lines.Select(line => Hang(line, width)).ToList();
        if (wrapped.Sum(w => w.Count) <= room)
        {
            band.AddRange(wrapped.SelectMany(w => w));
            return band;
        }

        int used = 0;
        int shown = 0;
        while (shown < wrapped.Count && used + wrapped[shown].Count <= room - 1)
        {
            band.AddRange(wrapped[shown]);
            used += wrapped[shown].Count;
            shown++;
        }

        band.AddRange(Hang(ThemeText.ChecksMore(wrapped.Count - shown), width).Take(1));
        return band;
    }

    /// <summary><paramref name="problems"/>' band lines, worst first (<c>Ratio / Target</c>, lowest first), a slab's code colors one line at its worst.</summary>
    private static List<string> PairLines(IReadOnlyList<ContrastProblem> problems)
    {
        static double Short(ContrastProblem p) => p.Ratio / p.Target;
        var entries = problems.Where(p => p.Slab is null).Select(p => (Short(p), ThemeText.ChecksPair(p))).ToList();
        foreach (var slab in problems.Where(p => p.Slab is not null).GroupBy(p => p.Slab!))
        {
            var worst = slab.MinBy(Short)!;
            entries.Add((Short(worst), ThemeText.ChecksCodeSlab(slab.Count(), slab.Key, worst)));
        }

        return entries.OrderBy(e => e.Item1).Select(e => e.Item2).ToList();
    }

    /// <summary><paramref name="line"/> wrapped to <paramref name="width"/>, its glyph and two spaces kept and the rest hanging under the text.</summary>
    private static List<string> Hang(string line, int width)
    {
        int lead = line.IndexOf("  ", StringComparison.Ordinal) is >= 0 and var at ? at + 2 : 0;
        int indent = TextCells.Width(line[..lead]);
        if (indent >= width)
        {
            return [.. MenuPane.CaptionRows(line, width, int.MaxValue)];
        }

        var rows = KeepSpacesWrap(line[lead..], width - indent);
        return rows.Select((row, i) => (i == 0 ? line[..lead] : new string(' ', indent)) + row).ToList();
    }

    /// <summary>
    /// <paramref name="text"/> wrapped at spaces into rows of <paramref name="width"/> cells, keeping a double space inside a row (2026-11-24:
    /// <c>· ⚠  n other pairs</c>, where the caption wrap would close it up); spaces at a break go, a word wider than a row is cut.
    /// </summary>
    private static List<string> KeepSpacesWrap(string text, int width)
    {
        var rows = new List<string>();
        string row = "";
        foreach (string word in text.Split(' '))
        {
            if (row.Length == 0)
            {
                if (word.Length == 0)
                {
                    continue;
                }
            }
            else if (TextCells.Width(row) + 1 + TextCells.Width(word) <= width)
            {
                row += " " + word;
                continue;
            }
            else
            {
                rows.Add(row.TrimEnd());
                row = "";
                if (word.Length == 0)
                {
                    continue;
                }
            }

            var cut = MenuPane.CaptionRows(word, width, int.MaxValue);
            rows.AddRange(cut.Take(cut.Count - 1));
            row = cut[^1];
        }

        if (row.TrimEnd().Length > 0)
        {
            rows.Add(row.TrimEnd());
        }

        return rows;
    }

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
    /// (null). The bars start at <paramref name="startHsl"/>, the row's own (#6), while the color itself is kept as it is until a bar moves,
    /// so a value that only went out and back is never rewritten. The pick comes back with the hue, saturation and lightness it ended on.
    /// </summary>
    private async Task<(Color Color, HslColor Hsl)?> PickColorAsync(ThemePalette current, ThemeColorSlot slot, Color was, HslColor startHsl, CancellationToken cancellationToken)
    {
        var hsl = startHsl;
        Color? exact = current.ColorOf(slot);
        int cursor = 0;
        while (true)
        {
            var candidate = exact ?? hsl.ToColor();
            var shown = WithColors(current, current.Colors().Select((c, i) => i == (int)slot ? candidate : c).ToArray());
            var effective = EffectiveNow();
            var page = new MenuPage(ThemeText.PickerTitle(current.Name, ThemeKeys.Colors[(int)slot], Theme.ToHex(was)), PickerRows(hsl, candidate, current.Bg, PickerBarWidth()), ThemeText.PickerHint)
            {
                Nudges = true,
                SideRows = ThemePreview.ScreenRows(effective.DiffCollapseCount),   // the whole mock screen (2026-11-24); no footer here
                Side = (_, width, rows) => ThemePreview.Lines(shown, width, rows, SidekickApp.BannerTitle.Trim(), SidekickApp.Version, effective.ThemedBackground, effective.DiffCollapseCount),
            };
            if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return null;
            }

            cursor = pick.Row;
            if (pick.Refresh)
            {
                continue;   // a resize (#114): the bars built again at the new width, never read as Enter
            }

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
                return (candidate, hsl);
            }

            var typed = await _pane.EditAsync(page with { Hint = EditKeys }, cursor, _input, Theme.ToHex(candidate), allowEmpty: false, cancellationToken).ConfigureAwait(false);
            if (typed is not InputResult.Submitted { Text: var text })
            {
                continue;
            }

            if (ThemeFile.TryParseColor(text, out var parsed))
            {
                return (parsed, HslColor.From(parsed));
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

    /// <summary>The bars' width in the pane as it is now: the list's cells beside the picker's preview, less the label, the value and the ends, held to its range.</summary>
    private int PickerBarWidth() => Math.Clamp(_pane.ListWidth(side: true) - 2 - 12 - 2 - 6, PickerBarMin, PickerBarMax);

    /// <summary>
    /// The picker's four rows: each bar <paramref name="width"/> cells, a cell the color at that point, the current point a <c>┃</c> in whichever
    /// of black and white reads on it, and the value after it; then the hex row with the candidate's swatch, framed (#149). The hue bar is drawn
    /// at full saturation and half lightness, the saturation bar at half lightness (2026-11-17, #182: at white or black they were one color); the
    /// lightness bar holds the others. Pure.
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
        string dim = Theme.DimText.ToMarkup();
        return
        [
            ThemeText.PickerHue.PadRight(12) + Bar(t => new HslColor(t * 360, 100, 50), hsl.Hue / 360) + Value(Whole(hsl.Hue) + "°"),
            ThemeText.PickerSaturation.PadRight(12) + Bar(t => hsl with { Saturation = t * 100, Lightness = 50 }, hsl.Saturation / 100) + Value(Whole(hsl.Saturation) + "%"),
            ThemeText.PickerLightness.PadRight(12) + Bar(t => hsl with { Lightness = t * 100 }, hsl.Lightness / 100) + Value(Whole(hsl.Lightness) + "%"),
            ThemeText.PickerHex.PadRight(12) + $"[{dim}]▕[/][{Theme.ToHex(candidate)} on {Theme.ToHex(bg)}]██████[/][{dim}]▏[/] " + Theme.ToHex(candidate),
        ];
    }

    private static string Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
