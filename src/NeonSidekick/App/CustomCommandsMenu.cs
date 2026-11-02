using NeonSidekick.CustomCommands;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>What a <c>/commands</c> pane row is.</summary>
internal enum CommandsRowKind
{
    /// <summary><c>new command…</c>, the first row: a name, a scope, the template in the editor.</summary>
    New,

    /// <summary>A command that runs.</summary>
    Command,

    /// <summary>A file a built-in or the profile's command hides.</summary>
    Shadow,

    /// <summary>A file the scan skipped.</summary>
    Problem,

    /// <summary>A heading, a gap, the empty list's line, the no-match line: no act.</summary>
    Other,
}

/// <summary>One row of the pane: its markup and what it is.</summary>
internal sealed record CommandsRow(string Markup, CommandsRowKind Kind, CustomCommand? Command = null, CustomCommandShadow? Shadow = null, CustomCommandProblem? Problem = null, bool Heading = false);

/// <summary>
/// <c>/commands</c> (2026-10-30, the custom commands plan; the <see cref="SkillsMenu"/> shape): one tab, Commands, with sub-tabs all · profile ·
/// global (every visit on all), the typed filter on each. Rows: <c>new command…</c> first (the filter never hides it, the count leaves it out,
/// a filter that keeps nothing puts the no-match row under it), a row per command (<c>/name  hint  description</c>, the scope on all), then a
/// Shadowed and a Skipped block, each heading shown only over a row the filter keeps. The band under the list: the file's path and the body's
/// first lines, cleaned the diff view's way (a tab four cells, a control character <c>·</c>) and escaped by the pane.
/// <para>Enter on a command: use (the word on the draft line, the pane closed), move (the other scope first, then the other profiles), copy (to
/// another profile), rename, edit, delete. A Shadowed row: rename, edit, delete, and move to another profile for a global one the profile's
/// hides; a Skipped row: edit, delete. Acts run under a reply too: no turn reads the files. No pane: <see cref="CustomCommandText.Lines"/>.</para>
/// </summary>
internal sealed class CustomCommandsMenu
{
    /// <summary>The list's hint (≤119 cells). Pinned.</summary>
    public const string Keys = "Enter = use, move, copy, rename, edit, delete · ←/→ tabs · " + MenuFilter.TypeAndCloseKeys;

    /// <summary>The sub-tabs' titles: all, profile, global.</summary>
    public static readonly IReadOnlyList<string> SubTitles = [SkillsText.AllSubTitle, CustomCommandText.ScopeName(CustomCommandScope.Profile), CustomCommandText.ScopeName(CustomCommandScope.Global)];

    /// <summary>How many of the body's lines the band shows under the path.</summary>
    public const int FooterBodyLines = 3;

    private readonly Func<CustomCommandScan> _scan;
    private readonly Func<CustomCommandRoots> _roots;
    private readonly AppSettings _settings;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InputLine _input;
    private readonly Action<string> _openFile;
    private readonly Func<IRecycleBin?> _bin;

    /// <param name="scan">A fresh scan, asked when the pane opens and after every act.</param>
    /// <param name="roots">The two folders as they stand.</param>
    /// <param name="settings">The home and the loaded profile, for the other profiles a move or copy lists.</param>
    /// <param name="transcript">Where a notice goes when the pane isn't open.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="input">The line a name is typed on, under the page.</param>
    /// <param name="openFile">Opens a file in the user's editor (the screen's seam).</param>
    /// <param name="bin">The bin a delete goes to, or null to delete for good (<c>File deletes to Recycle bin</c>).</param>
    public CustomCommandsMenu(Func<CustomCommandScan> scan, Func<CustomCommandRoots> roots, AppSettings settings, INoticeSink transcript, MenuPane pane, InputLine input, Action<string> openFile, Func<IRecycleBin?> bin)
    {
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _openFile = openFile ?? throw new ArgumentNullException(nameof(openFile));
        _bin = bin ?? throw new ArgumentNullException(nameof(bin));
    }

    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    /// <summary>The scope a sub-tab shows: null for all.</summary>
    public static CustomCommandScope? ScopeOf(int sub) => sub switch
    {
        1 => CustomCommandScope.Profile,
        2 => CustomCommandScope.Global,
        _ => null,
    };

    /// <summary>
    /// The rows of one sub-tab under <paramref name="filter"/>, from the lists' shared rows (<see cref="CustomCommandText.Rows"/>, 2026-10-31,
    /// review P6-7), the folders from <c>scan.Roots</c>. Pure.
    /// </summary>
    public static IReadOnlyList<CommandsRow> Rows(CustomCommandScan scan, string filter, CustomCommandScope? scope)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(filter);
        var rows = new List<CommandsRow> { new(Markup.Escape(CustomCommandText.NewRow), CommandsRowKind.New) };
        var shown = CustomCommandText.Rows(scan).Where(r => scope is null || r.Scope == scope).Where(r => MenuFilter.Matches(filter, r.Word, r.Detail)).ToList();
        var commands = shown.Where(r => r.Kind == CustomCommandRowKind.Command).ToList();
        var shadows = shown.Where(r => r.Kind == CustomCommandRowKind.Shadow).ToList();
        var problems = shown.Where(r => r.Kind == CustomCommandRowKind.Problem).ToList();
        if (shown.Count == 0)
        {
            rows.Add(new(filter.Length > 0 ? MenuFilter.NoMatchRow(filter) : Theme.DimMarkup(CustomCommandText.EmptyRow), CommandsRowKind.Other));
            return rows;
        }

        rows.AddRange(commands.Select(r => new CommandsRow(CommandRow(r, scope is null), CommandsRowKind.Command, Command: r.Command)));
        if (shadows.Count > 0)
        {
            rows.Add(new("", CommandsRowKind.Other));
            rows.Add(new(SectionRule.Markup(CustomCommandText.ShadowedHeading), CommandsRowKind.Other, Heading: true));
            rows.AddRange(shadows.Select(r => new CommandsRow(Theme.DimMarkup(r.Word + "  " + r.Detail + (scope is null ? " · " + CustomCommandText.ScopeName(r.Scope) : "")), CommandsRowKind.Shadow, Shadow: r.Shadow)));
        }

        if (problems.Count > 0)
        {
            rows.Add(new("", CommandsRowKind.Other));
            rows.Add(new(SectionRule.Markup(CustomCommandText.SkippedHeading), CommandsRowKind.Other, Heading: true));
            rows.AddRange(problems.Select(r => new CommandsRow(Theme.DimMarkup(r.Word + "  " + r.Detail), CommandsRowKind.Problem, Problem: r.Problem)));
        }

        return rows;
    }

    /// <summary>A command's row: the word, its hint dim, its description, the scope dim on all, a warning's mark.</summary>
    public static string CommandRow(CustomCommandRow row, bool withScope)
    {
        ArgumentNullException.ThrowIfNull(row);
        string markup = Markup.Escape(row.Word);
        if (row.Hint is { } hint)
        {
            markup += "  " + Theme.DimMarkup(hint);
        }

        if (row.Detail.Length > 0)
        {
            markup += "  " + Markup.Escape(row.Detail);
        }

        if (withScope)
        {
            markup += Theme.DimMarkup(" · " + CustomCommandText.ScopeName(row.Scope));
        }

        if (row.Command?.Warning is not null)
        {
            markup += Theme.DimMarkup(" (!)");
        }

        return markup;
    }

    /// <summary>
    /// The band for a row: a command's path and its body's first lines (<see cref="FooterBodyLines"/>, each through <see cref="DiffView.Clean"/>),
    /// its warning on the last line; a Shadowed or Skipped row's path and why. Plain text: the pane escapes it. Any other row shows the scan's
    /// <paramref name="note"/> (2026-10-31, review P5-6: a note, no longer a Skipped row), else nothing.
    /// </summary>
    public static MenuFooter? Footer(CommandsRow row, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        switch (row.Kind)
        {
            case CommandsRowKind.Command:
            {
                var command = row.Command!;
                var lines = new List<string> { DiffView.Clean(command.FilePath) };
                lines.AddRange(command.Body.Split('\n').Where(l => l.Trim().Length > 0).Take(FooterBodyLines).Select(DiffView.Clean));
                return new MenuFooter(string.Join('\n', lines), command.Warning is { } warning ? CustomCommandText.NotePrefix + DiffView.Clean(warning) : null) { KeepLines = true };
            }

            case CommandsRowKind.Shadow:
                return new MenuFooter(DiffView.Clean(row.Shadow!.Command.FilePath), CustomCommandText.ShadowReason(row.Shadow));
            case CommandsRowKind.Problem:
                return new MenuFooter(DiffView.Clean(row.Problem!.FilePath), DiffView.Clean(row.Problem.Problem));
            default:
                return note is null ? null : new MenuFooter(CustomCommandText.NotePrefix + DiffView.Clean(note));
        }
    }

    /// <summary>
    /// The page over the three sub-tabs, <paramref name="sub"/> on show, and each sub-tab's rows (2026-10-31, review P5-8: the pick built them
    /// again), indexed by sub-tab. Pure.
    /// </summary>
    public static (MenuPage Page, IReadOnlyList<IReadOnlyList<CommandsRow>> Rows) Page(CustomCommandScan scan, string filter, int sub)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var built = Enumerable.Range(0, SubTitles.Count).Select(i => Rows(scan, filter, ScopeOf(i))).ToList();
        var subTabs = Enumerable.Range(0, SubTitles.Count).Select(i =>
        {
            var scope = ScopeOf(i);
            var rows = built[i];
            int total = scan.Commands.Count(c => scope is null || c.Scope == scope);
            return new MenuTab(SubTitles[i], rows.Select(r => r.Markup).ToList())
            {
                Headings = Enumerable.Range(0, rows.Count).Where(r => rows[r].Heading).ToHashSet(),
                Hint = MenuFilter.Hint(Keys, filter),
                Filter = filter,
                Caption = MenuFilter.CaptionOrNull(filter, rows.Count(r => r.Kind == CommandsRowKind.Command), total),
                Footer = r => r >= 0 && r < rows.Count ? Footer(rows[r], scan.Note) : null,
            };
        }).ToList();
        var tab = new MenuTab(CustomCommandText.Label, []) { SubTabs = subTabs, Sub = Math.Clamp(sub, 0, subTabs.Count - 1) };
        var page = MenuPage.Tabbed(CustomCommandText.Title, [tab], 0, Keys) with
        {
            // The sub-tabs' own bands; the page's turns the band on.
            Footer = (_, _) => null,
        };
        return (page, built);
    }

    /// <summary>
    /// Shows the list until Esc (null) or a <c>use</c> (the word and a space for the draft line, the pane closed). The facts are read on open
    /// and after every act.
    /// </summary>
    public async Task<string?> ShowAsync(CancellationToken cancellationToken)
    {
        var scan = _scan();
        if (!_pane.Enabled)
        {
            foreach (string line in CustomCommandText.Lines(scan))
            {
                _transcript.Notice(line);
            }

            return null;
        }

        int cursor = 0;
        int sub = 0;
        string filter = "";
        try
        {
            while (true)
            {
                var (page, built) = Page(scan, filter, sub);
                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return null;
                }

                cursor = pick.Row;
                sub = _pane.ShownSubs is { Count: > 0 } shown ? shown[0] : 0;
                if (pick.Filter is { } typed)
                {
                    filter = typed;
                    cursor = 0;
                    continue;
                }

                var rows = built[Math.Clamp(sub, 0, built.Count - 1)];
                if (cursor < 0 || cursor >= rows.Count)
                {
                    continue;
                }

                var row = rows[cursor];
                bool changed;
                switch (row.Kind)
                {
                    case CommandsRowKind.New:
                        changed = await NewAsync(page, cursor, ScopeOf(sub), cancellationToken).ConfigureAwait(false);
                        break;
                    case CommandsRowKind.Command:
                    {
                        var (use, did) = await CommandPageAsync(row.Command!, cancellationToken).ConfigureAwait(false);
                        if (use is not null)
                        {
                            return use;
                        }

                        changed = did;
                        break;
                    }

                    case CommandsRowKind.Shadow:
                        changed = await ShadowPageAsync(row.Shadow!, cancellationToken).ConfigureAwait(false);
                        break;
                    case CommandsRowKind.Problem:
                        changed = await ProblemPageAsync(row.Problem!, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        continue;
                }

                if (changed)
                {
                    scan = _scan();
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    // A command's page: use, move, copy, rename, edit, delete. The word to use, or whether the files changed.
    private async Task<(string? Use, bool Changed)> CommandPageAsync(CustomCommand command, CancellationToken cancellationToken)
    {
        string[] words = [CustomCommandText.UseWord, CustomCommandText.MoveWord, CustomCommandText.CopyWord, CustomCommandText.RenameWord, CustomCommandText.EditWord, CustomCommandText.DeleteWord];
        var page = new MenuPage(CustomCommandText.PageTitle(command.Word), words.Select(Markup.Escape).ToList(), SettingsMenu.PickKeys) { Caption = command.Description };
        if (await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { Row: var row } || row < 0 || row >= words.Length)
        {
            return (null, false);
        }

        return words[row] switch
        {
            CustomCommandText.UseWord => (command.Word + " ", false),
            CustomCommandText.MoveWord => (null, await MoveAsync(command, command.Scope, otherScope: true, cancellationToken).ConfigureAwait(false)),
            CustomCommandText.CopyWord => (null, await CopyAsync(command, cancellationToken).ConfigureAwait(false)),
            CustomCommandText.RenameWord => (null, await RenameAsync(page, row, command.FilePath, command.Scope, command.Name, cancellationToken).ConfigureAwait(false)),
            CustomCommandText.EditWord => (null, Edit(command.FilePath)),
            _ => (null, await DeleteAsync(command.FilePath, command.Scope, command.Word, cancellationToken).ConfigureAwait(false)),
        };
    }

    // A Shadowed row's page: rename, edit, delete; move (to another profile) for a global file the profile's hides.
    private async Task<bool> ShadowPageAsync(CustomCommandShadow shadow, CancellationToken cancellationToken)
    {
        var command = shadow.Command;
        var words = new List<string> { CustomCommandText.RenameWord, CustomCommandText.EditWord, CustomCommandText.DeleteWord };
        if (shadow.Winner is not null)
        {
            words.Insert(0, CustomCommandText.MoveWord);
        }

        var page = new MenuPage(CustomCommandText.PageTitle(command.Word), words.Select(Markup.Escape).ToList(), SettingsMenu.PickKeys) { Caption = CustomCommandText.ShadowReason(shadow) };
        if (await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { Row: var row } || row < 0 || row >= words.Count)
        {
            return false;
        }

        return words[row] switch
        {
            CustomCommandText.MoveWord => await MoveAsync(command, command.Scope, otherScope: false, cancellationToken).ConfigureAwait(false),
            CustomCommandText.RenameWord => await RenameAsync(page, row, command.FilePath, command.Scope, command.Name, cancellationToken).ConfigureAwait(false),
            CustomCommandText.EditWord => Edit(command.FilePath),
            _ => await DeleteAsync(command.FilePath, command.Scope, command.Word, cancellationToken).ConfigureAwait(false),
        };
    }

    // A Skipped row's page: edit, delete. A folder's row shows its problem, and a file gone since the scan says so and rescans (2026-10-31,
    // review P2-5: Enter on either did nothing at all).
    private async Task<bool> ProblemPageAsync(CustomCommandProblem problem, CancellationToken cancellationToken)
    {
        if (Directory.Exists(problem.FilePath))
        {
            Sink.Notice(CustomCommandText.FolderProblemNotice(Shown(problem), problem.Problem));
            return false;
        }

        if (!File.Exists(problem.FilePath))
        {
            Sink.Notice(CustomCommandText.GoneNotice(Shown(problem)));
            return true;
        }

        string[] words = [CustomCommandText.EditWord, CustomCommandText.DeleteWord];
        string name = Shown(problem);
        var page = new MenuPage(CustomCommandText.PageTitle(name), words.Select(Markup.Escape).ToList(), SettingsMenu.PickKeys) { Caption = problem.Problem };
        if (await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { Row: var row } || row < 0 || row >= words.Length)
        {
            return false;
        }

        return row == 0 ? Edit(problem.FilePath) : await DeleteAsync(problem.FilePath, problem.Scope, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The move's targets: the other scope first (<c>to global</c> or <c>to this profile</c>, left out when both folders are one or when
    /// <paramref name="otherScope"/> is false), then every other profile. Pure.
    /// </summary>
    public static IReadOnlyList<(string Label, CustomCommandScope? Scope, string? Profile)> MoveTargets(CustomCommandScope scope, bool sameFolder, bool otherScope, IReadOnlyList<string> otherProfiles)
    {
        ArgumentNullException.ThrowIfNull(otherProfiles);
        var targets = new List<(string, CustomCommandScope?, string?)>();
        if (otherScope && !sameFolder)
        {
            targets.Add(scope == CustomCommandScope.Profile ? (CustomCommandText.ToGlobal, CustomCommandScope.Global, null) : (CustomCommandText.ToThisProfile, CustomCommandScope.Profile, null));
        }

        targets.AddRange(otherProfiles.Select(p => (CustomCommandText.ProfileWhere(p), (CustomCommandScope?)null, (string?)p)));
        return targets;
    }

    private IReadOnlyList<string> OtherProfiles() =>
        Profiles.List(_settings.StorageDirectory).Where(name => !Profiles.NameEquals(name, _settings.ProfileName)).ToList();

    private string ProfileRoot(string profile) => Path.Combine(Profiles.Directory(_settings.StorageDirectory, profile), CustomCommandRoots.DirectoryName);

    private async Task<bool> MoveAsync(CustomCommand command, CustomCommandScope scope, bool otherScope, CancellationToken cancellationToken)
    {
        var roots = _roots();
        var targets = MoveTargets(scope, roots.Same, otherScope, OtherProfiles());
        if (targets.Count == 0)
        {
            Sink.Notice(CustomCommandText.NoOtherProfile);
            return false;
        }

        var page = new MenuPage(CustomCommandText.MoveTitle(command.Word), targets.Select(t => Markup.Escape(t.Label)).ToList(), SettingsMenu.PickKeys);
        if (await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { Row: var row } || row < 0 || row >= targets.Count)
        {
            return false;
        }

        var target = targets[row];
        if (!await ConfirmAsync(CustomCommandText.MovePrompt(command.Word, target.Label), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return false;
        }

        var result = target.Scope is { } to
            ? CustomCommandEditor.Move(roots, scope, command.FilePath, to)
            : CustomCommandEditor.MoveToProfile(roots, scope, command.FilePath, ProfileRoot(target.Profile!));
        if (result.Outcome == CustomCommandEditOutcome.Moved)
        {
            Sink.Notice(CustomCommandText.MovedNotice(command.Word, target.Label));
            return true;
        }

        if (result.Outcome == CustomCommandEditOutcome.SharedFolder)
        {
            Sink.Notice(CustomCommandText.SharedFolderNotice(target.Profile));
            return false;
        }

        Sink.Error(result.Outcome == CustomCommandEditOutcome.Exists && target.Profile is { } profile ? CustomCommandText.ExistsInProfileError(result.Name, profile) : Refusal(result));
        return result.Outcome == CustomCommandEditOutcome.Missing;
    }

    private async Task<bool> CopyAsync(CustomCommand command, CancellationToken cancellationToken)
    {
        var others = OtherProfiles();
        if (others.Count == 0)
        {
            Sink.Notice(CustomCommandText.NoOtherProfile);
            return false;
        }

        var page = new MenuPage(CustomCommandText.CopyTitle(command.Word), others.Select(Markup.Escape).ToList(), SettingsMenu.PickKeys);
        if (await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { Row: var row } || row < 0 || row >= others.Count)
        {
            return false;
        }

        var result = CustomCommandEditor.CopyToProfile(_roots(), command.Scope, command.FilePath, ProfileRoot(others[row]));
        if (result.Outcome == CustomCommandEditOutcome.Copied)
        {
            Sink.Notice(CustomCommandText.CopiedNotice(command.Word, others[row]));
        }
        else if (result.Outcome == CustomCommandEditOutcome.SharedFolder)
        {
            Sink.Notice(CustomCommandText.SharedFolderNotice(others[row]));
        }
        else
        {
            // Another profile's copy named (2026-10-31, review P2-13): "rename or delete the old one on /commands" can't be done from here.
            Sink.Error(result.Outcome == CustomCommandEditOutcome.Exists ? CustomCommandText.ExistsInProfileError(result.Name, others[row]) : Refusal(result));
        }

        return false;
    }

    private async Task<bool> RenameAsync(MenuPage page, int row, string file, CustomCommandScope scope, string current, CancellationToken cancellationToken)
    {
        if (await TypeNameAsync(page, row, current, cancellationToken).ConfigureAwait(false) is not { } name)
        {
            return false;
        }

        if (string.Equals(name, current, StringComparison.Ordinal))
        {
            Sink.Notice(CustomCommandText.UnchangedNotice);
            return false;
        }

        var roots = _roots();
        var result = CustomCommandEditor.Rename(roots, scope, file, name);
        if (result.Outcome != CustomCommandEditOutcome.Renamed)
        {
            Sink.Error(Refusal(result));
            return result.Outcome == CustomCommandEditOutcome.Missing;
        }

        Sink.Notice(CustomCommandText.RenamedNotice("/" + current, name) + OverrideNote(roots, scope, name));
        return true;
    }

    private async Task<bool> NewAsync(MenuPage page, int row, CustomCommandScope? tabScope, CancellationToken cancellationToken)
    {
        if (await TypeNameAsync(page with { Title = CustomCommandText.NewNameTitle }, row, "", cancellationToken).ConfigureAwait(false) is not { } name)
        {
            return false;
        }

        var roots = _roots();
        var scope = tabScope ?? await AskScopeAsync(_pane, roots, CustomCommandText.NewScopeTitle(name), cancellationToken).ConfigureAwait(false);
        if (scope is null)
        {
            return false;
        }

        var result = CustomCommandEditor.Create(roots, scope.Value, name);
        if (result.Outcome != CustomCommandEditOutcome.Created)
        {
            Sink.Error(Refusal(result));
            return false;
        }

        Sink.Notice(CustomCommandText.CreatedNotice(name, result.Path) + OverrideNote(roots, scope.Value, name));
        OpenInEditor(Sink, _openFile, result.Path, opened: null);
        return true;
    }

    /// <summary>
    /// A typed name made a name and judged (2026-10-31, review P2-9: the pane's slot and <c>/prompts</c>' m slot were two copies): the name,
    /// or the refusal in words (<see cref="CustomCommandText.BadNameError"/>, <see cref="CustomCommandText.BuiltInError"/>), which each menu
    /// reports its own way. Pure.
    /// </summary>
    public static (string? Name, string? Refusal) JudgeName(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        string name = CustomCommandName.Normalize(typed);
        if (!CustomCommandName.IsValid(name))
        {
            return (null, CustomCommandText.BadNameError(typed.Trim(), ""));
        }

        return CustomCommandCatalog.BuiltInWord(name) is not null ? (null, CustomCommandText.BuiltInError(name)) : (name, null);
    }

    /// <summary>
    /// The scope page (2026-10-31, review P2-9, shared with <c>/prompts</c>' m): profile or global, each with its folder dim; the pick, or
    /// null on Esc.
    /// </summary>
    public static async Task<CustomCommandScope?> AskScopeAsync(MenuPane pane, CustomCommandRoots roots, string title, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(roots);
        CustomCommandScope[] scopes = [CustomCommandScope.Profile, CustomCommandScope.Global];
        var where = new MenuPage(title, scopes.Select(s => Markup.Escape(CustomCommandText.ScopeName(s).PadRight(9)) + Theme.DimMarkup(roots.Of(s))).ToList(), SettingsMenu.PickKeys);
        return await pane.PickAsync(where, 0, cancellationToken).ConfigureAwait(false) is { Row: var picked } && picked >= 0 && picked < scopes.Length ? scopes[picked] : null;
    }

    /// <summary>
    /// A command file opened in the user's editor (2026-10-31, review P7-7: <c>/commands new</c> and the pane's edit were two copies):
    /// <paramref name="opened"/> said once it opens, a failure said as an error.
    /// </summary>
    public static void OpenInEditor(INoticeSink sink, Action<string> open, string path, string? opened)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(open);
        try
        {
            open(path);
            if (opened is not null)
            {
                sink.Notice(opened);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            sink.Error(CustomCommandText.OpenFailedError(ex.Message));
        }
    }

    // A name typed on the slot, made a name and checked; null on Esc or a refusal (said).
    private async Task<string?> TypeNameAsync(MenuPage page, int row, string initial, CancellationToken cancellationToken)
    {
        var typed = await _pane.EditAsync(page with { Hint = SettingsMenu.EditKeys }, row, _input, initial, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (typed is not InputResult.Submitted { Text: var text } || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var (name, refusal) = JudgeName(text);
        if (refusal is not null)
        {
            Sink.Error(refusal);
        }

        return name;
    }

    private bool Edit(string file)
    {
        OpenInEditor(Sink, _openFile, file, CustomCommandText.EditOpenedNotice(file));
        return false;
    }

    private async Task<bool> DeleteAsync(string file, CustomCommandScope scope, string word, CancellationToken cancellationToken)
    {
        if (!await ConfirmAsync(CustomCommandText.DeletePrompt(word), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return false;
        }

        var result = CustomCommandEditor.Delete(_roots(), scope, file, _bin());
        if (result.Outcome == CustomCommandEditOutcome.Deleted)
        {
            Sink.Notice(CustomCommandText.DeletedNotice(word, result.Way));
            return true;
        }

        Sink.Error(Refusal(result));
        return result.Outcome == CustomCommandEditOutcome.Missing;
    }

    /// <summary>A profile name over a global one says it runs instead; a global name under a profile one says it's shadowed here; else nothing.</summary>
    public static string OverrideNote(CustomCommandRoots roots, CustomCommandScope scope, string name)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Same)
        {
            return "";
        }

        var other = scope == CustomCommandScope.Profile ? CustomCommandScope.Global : CustomCommandScope.Profile;
        if (!CustomCommandEditor.Taken(roots.Of(other), name))
        {
            return "";
        }

        return " " + (scope == CustomCommandScope.Profile ? CustomCommandText.OverridesGlobalNote(name) : CustomCommandText.ShadowedHereNote(name));
    }

    /// <summary>An editor refusal in words.</summary>
    public static string Refusal(CustomCommandEditResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            CustomCommandEditOutcome.BadName => CustomCommandText.BadNameError(result.Name, ""),
            CustomCommandEditOutcome.BuiltIn => CustomCommandText.BuiltInError(result.Name),
            CustomCommandEditOutcome.Exists => CustomCommandText.ExistsError(result.Name, result.Scope),
            CustomCommandEditOutcome.Linked => result.LinkIsFile ? CustomCommandText.LinkedFileError(result.Path) : CustomCommandText.LinkedError(result.Name),
            CustomCommandEditOutcome.Missing => CustomCommandText.MissingError("/" + result.Name),
            CustomCommandEditOutcome.Blank => CustomCommandText.BlankError,
            CustomCommandEditOutcome.TooLarge => CustomCommandText.TooLargeError,
            CustomCommandEditOutcome.NothingToChange => CustomCommandText.UnchangedNotice,
            CustomCommandEditOutcome.SharedFolder => CustomCommandText.SharedFolderNotice(null),
            _ => CustomCommandText.FailedError(result.Detail),
        };
    }

    // A Skipped row's file as shown: its path under its folder, cleaned.
    private string Shown(CustomCommandProblem problem) => DiffView.Clean(CustomCommandText.ShownPath(_roots(), problem.Scope, problem.FilePath));

    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var page = new MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys };
        var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        return picked is { Row: 1 };
    }
}
