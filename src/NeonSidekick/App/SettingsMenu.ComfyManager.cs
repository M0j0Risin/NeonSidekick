using NeonSidekick.Comfy;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>ComfyUI workflows</c> (2026-11-25, the user's ask: "one row, one list"): the workflow manager in place of the <c>workflows offered</c>
/// checklist and the <c>add workflow</c> row. One list of every installed workflow: Space offers or hides one, <c>a</c>/<c>n</c> all or none,
/// Del deletes, Enter opens its actions (Edit, Test, offer or hide, Duplicate, Rename, Move, Open the JSON or the Markdown, Delete); the
/// first row walks a new one in (<c>SettingsMenu.ComfyWizard.cs</c>), and a file the catalog skipped is listed under <c>Skipped</c> with its reason.
/// A rename or a delete keeps the loaded profile's lists in step (offered, Botchat's), as a database connection's does; other profiles' lists are never touched.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The list's first row: a new workflow, built or imported. Pinned.</summary>
    public const string ComfyNewWorkflowRow = "+ New workflow";

    /// <summary>The heading over the files the catalog skipped.</summary>
    public const string ComfySkippedHeading = "Skipped";

    /// <summary>The list's hint (≤119 cells).</summary>
    public const string ComfyManagerKeys = "Enter = open · Space = offer · a = all · n = none · c = check · Del = delete · Esc = back";

    /// <summary>The actions page's hint.</summary>
    public const string ComfyActionKeys = "Enter = choose · Del = delete · Esc = back";

    public const string ComfyEditAction = "Edit";
    public const string ComfyTestAction = "Test";
    public const string ComfyCheckAction = "Check against the server";
    public const string ComfyOfferAction = "Offer to the model";
    public const string ComfyHideAction = "Hide from the model";
    public const string ComfyDuplicateAction = "Duplicate";
    public const string ComfyRenameAction = "Rename";
    public const string ComfyMoveToGlobalAction = "Move to global (every profile)";
    public const string ComfyMoveToProfileAction = "Move to this profile";
    public const string ComfyOpenJsonAction = "Open the JSON";
    public const string ComfyOpenMarkdownAction = "Open the Markdown";
    public const string ComfyDeleteAction = "Delete";

    /// <summary>The scope column's words.</summary>
    public const string ComfyScopeProfile = "profile";
    public const string ComfyScopeGlobal = "global";

    /// <summary>The list's caption when the tools are off.</summary>
    public const string ComfyCaptionToolsOff = "ComfyUI tools is off: turn it on so the model can use the workflows ticked here.";

    /// <summary>The list's caption when no server is set.</summary>
    public const string ComfyCaptionNoUrl = "Set ComfyUI URL so these workflows can run.";

    /// <summary>The list's caption when nothing is offered.</summary>
    public const string ComfyCaptionNoneOffered = "Space ticks a workflow to offer it to the model; none is offered yet.";

    public const string ComfyRenameQuestion = "The new name (" + ComfyWorkflowFile.NameRule + ").";
    public const string ComfyDuplicateQuestion = "The copy's name (" + ComfyWorkflowFile.NameRule + ").";

    /// <summary>The value of the <c>ComfyUI workflows</c> row: <see cref="ComfyOfferedValue"/> and <c>offered</c>. Pinned.</summary>
    public static string ComfyWorkflowsValue(IReadOnlyList<string>? offered, IReadOnlyList<ComfyWorkflow> installed) => ComfyOfferedValue(offered, installed) + " offered";

    public static string ComfyRenamedNotice(string from, string to, bool global) =>
        $"Renamed workflow '{from}' to '{to}'." + (global ? " Other profiles that offered it no longer do." : "");

    public static string ComfyRenameUncoveredNotice(string name) => $"The global workflow '{name}' shows again, not offered here.";

    public static string ComfyDuplicatedNotice(string from, string to) => $"Copied workflow '{from}' as '{to}', not offered yet.";

    public static string ComfyMovedNotice(string name, bool global) =>
        global ? $"Moved workflow '{name}' to the global folder: every profile sees it now." : $"Moved workflow '{name}' to this profile: other profiles no longer see it.";

    public static string ComfyDeletedNotice(string name, DeleteWay way) => way switch
    {
        DeleteWay.Recycled => $"Moved workflow '{name}' to the {FileText.RecycleBinName}.",
        DeleteWay.DeletedNoBin => $"Deleted workflow '{name}' for good (no {FileText.RecycleBinName} on that drive).",
        _ => $"Deleted workflow '{name}'.",
    };

    public static string ComfyDeleteUncoveredNotice(string name) => $"The global workflow '{name}' takes its place, offered as before.";

    /// <summary>Delete's question: the files, where they go, and a global workflow of the name it uncovers.</summary>
    public static string ComfyDeleteQuestion(string name, IReadOnlyList<string> files, bool toBin, bool uncovers) =>
        $"Delete workflow '{name}' ({string.Join(" and ", files)})" + (toBin ? $" to the {FileText.RecycleBinName}" : " for good") + "?"
        + (uncovers ? $" The global workflow '{name}' shows in its place." : "");

    public static string ComfyOpenedNotice(string path) => $"Opened {path} in the editor; the next call reads it.";

    public static string ComfyOpenFailed(string path, string why) => $"Could not open {path}: {why}.";

    /// <summary>The band of a skipped file: where it is and why it was skipped.</summary>
    public static string ComfySkippedBand(string path, string problem) => $"{path}: {problem}";

    /// <summary>A workflow in the profile's folder that hides a global one of its name.</summary>
    public const string ComfyHidesGlobalNote = "It hides the global workflow of the same name.";

    /// <summary>The widest a name is drawn in the list; a longer one is cut with ….</summary>
    public const int ComfyNameCap = 32;

    /// <summary>One item of the list: the new row (both null), a workflow, or a skipped file.</summary>
    private sealed record ComfyItem(ComfyWorkflow? Workflow, ComfyWorkflowProblem? Problem);

    /// <summary>The catalog the list reads, kept for the visit so a skipped file is logged once.</summary>
    private ComfyWorkflowCatalog? _comfyCatalog;

    private ComfyWorkflowCatalog ComfyCatalog => _comfyCatalog ??= new ComfyWorkflowCatalog(() => ComfyRoots);

    private bool InProfileFolder(string path) =>
        string.Equals(Path.GetFullPath(Path.GetDirectoryName(path) ?? ""), Path.GetFullPath(_settings.ProfileComfyDirectory), StringComparison.OrdinalIgnoreCase);

    private bool HidesGlobal(ComfyWorkflow workflow) => InProfileFolder(workflow.FilePath) && File.Exists(Path.Combine(_settings.GlobalComfyDirectory, workflow.Name + ".json"));

    /// <summary>The list's caption: the one next step, or none.</summary>
    private string? ComfyManagerCaption(int offered)
    {
        var current = _settings.Current;
        return !current.ComfyTools ? ComfyCaptionToolsOff
            : string.IsNullOrWhiteSpace(current.ComfyUrl) ? ComfyCaptionNoUrl
            : offered == 0 ? ComfyCaptionNoneOffered
            : null;
    }

    /// <summary>
    /// The list (<see cref="SettingsField.ComfyWorkflowsOffered"/>'s Enter, and the ComfyUI tools page's offered button). True when anything
    /// changed: a tick, a file written, moved or deleted.
    /// </summary>
    private async Task<bool> EditComfyOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        _comfyCatalog = null;
        _comfyTestSetup = new ComfyTestSetup();
        string? follow = null;
        int cursor = 1;
        try
        {
            while (true)
            {
                var (installed, problems) = ComfyCatalog.Scan();
                var broken = problems.Select(p => Path.GetFileNameWithoutExtension(p.FilePath)).ToList();
                changed |= PruneStale(SettingsField.ComfyWorkflowsOffered, _settings.Current.ComfyWorkflowsOffered,
                    OfferedNames.Stale(_settings.Current.ComfyWorkflowsOffered, installed.Select(w => w.Name), broken, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase, (d, next) => d.ComfyWorkflowsOffered = next);
                var chosen = _settings.Current.ComfyWorkflowsOffered ?? [];
                var on = ComfyWorkflowCatalog.Offered(installed, chosen).Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var items = new List<ComfyItem> { new(null, null) };
                items.AddRange(installed.Select(w => new ComfyItem(w, null)));
                items.AddRange(problems.Select(p => new ComfyItem(null, p)));
                if (follow is not null)
                {
                    int at = items.FindIndex(i => i.Workflow is { } w && string.Equals(w.Name, follow, StringComparison.OrdinalIgnoreCase));
                    cursor = at >= 0 ? at : cursor;
                    follow = null;
                }

                cursor = Math.Clamp(cursor, 0, items.Count - 1);
                var rows = ComfyManagerRows(installed, problems, on);
                var ruled = RuledRows.Of([[Markup.Escape(ComfyNewWorkflowRow)], rows.Workflows, rows.Skipped], [null, null, ComfySkippedHeading]);
                var page = new MenuPage(Crumb(FieldName(SettingsField.ComfyWorkflowsOffered)), ruled.Rows, ComfyManagerKeys)
                {
                    Headings = ruled.Headings,
                    SpaceToggles = true,
                    Caption = ComfyManagerCaption(on.Count),
                    Footer = (_, row) => ComfyManagerBand(items, ruled.ItemOf(row)),
                };

                MenuPick? pick;
                if (_pane.Enabled)
                {
                    pick = await _pane.PickAsync(page with { Buttons = ComfyManagerButtons, Removes = true }, ruled.RowOf(cursor), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    pick = await PickAsync(page, ruled.RowOf(cursor), cancellationToken).ConfigureAwait(false) is { } row ? new MenuPick(0, row) : null;
                }

                if (pick is not { } picked)
                {
                    if (!changed)
                    {
                        Sink.Notice(UnchangedNotice);
                    }

                    return changed;
                }

                if (picked.Button == SelectAllIndex || picked.Button == SelectNoneIndex)
                {
                    changed |= ComfySetOffered(installed, chosen, picked.Button == SelectAllIndex ? installed.Select(w => w.Name).ToList() : []);
                    continue;
                }

                if (picked.Button == ComfyCheckButtonIndex)
                {
                    await ComfyCheckAllAsync(installed, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                int item = ruled.ItemOf(picked.Row);
                if (item < 0 || item >= items.Count)
                {
                    continue;
                }

                cursor = item;
                var target = items[item];
                if (target.Workflow is null && target.Problem is null)
                {
                    if (picked.Toggle || picked.Remove)
                    {
                        continue;   // Space or Del on the add row: nothing
                    }

                    var (added, name) = await AddComfyWorkflowAsync(cancellationToken).ConfigureAwait(false);
                    changed |= added;
                    follow = name;
                    continue;
                }

                if (picked.Toggle)
                {
                    if (target.Workflow is { } flip)
                    {
                        var next = installed.Select(w => w.Name).Where(n => on.Contains(n) != string.Equals(n, flip.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                        changed |= ComfySetOffered(installed, chosen, next);
                    }

                    continue;
                }

                if (picked.Remove)
                {
                    var (deleted, _) = await ComfyDeleteAsync(target, cancellationToken).ConfigureAwait(false);
                    changed |= deleted;
                    continue;
                }

                var acted = await ComfyActionsAsync(target, cancellationToken).ConfigureAwait(false);
                changed |= acted.Changed;
                follow = acted.Follow;
            }
        }
        finally
        {
            _comfyCatalog = null;
        }
    }

    /// <summary>The list's buttons: select all, select none (<see cref="ChecklistButtons"/>), then check (index 2).</summary>
    public static IReadOnlyList<MenuButton> ComfyManagerButtons => [.. ChecklistButtons, new(ComfyCheckButton, ComfyCheckKey)];

    /// <summary>The check button's title. Pinned.</summary>
    public const string ComfyCheckButton = "✓ check";

    /// <summary>The key that is <see cref="ComfyCheckButton"/>.</summary>
    public const char ComfyCheckKey = 'c';

    private const int ComfyCheckButtonIndex = 2;

    /// <summary>
    /// Writes the offered list as <paramref name="next"/>, keeping a ticked name whose file failed to load (2026-10-04). False when nothing
    /// changed (a button that changes nothing saves nothing).
    /// </summary>
    private bool ComfySetOffered(IReadOnlyList<ComfyWorkflow> installed, IReadOnlyList<string> chosen, List<string> next)
    {
        var on = ComfyWorkflowCatalog.Offered(installed, chosen).Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (next.Count == on.Count && next.All(on.Contains))
        {
            return false;
        }

        next.AddRange(chosen.Where(n => !installed.Any(w => string.Equals(w.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
        Apply(SettingsField.ComfyWorkflowsOffered, d => d.ComfyWorkflowsOffered = next);
        return true;
    }

    /// <summary>The list's rows as markup: the workflows (tick, check mark, name, family, shape, size, scope), then the skipped files.</summary>
    private (List<string> Workflows, List<string> Skipped) ComfyManagerRows(IReadOnlyList<ComfyWorkflow> installed, IReadOnlyList<ComfyWorkflowProblem> problems, IReadOnlySet<string> on)
    {
        var columns = ComfyOfferedColumns.Of(installed);
        bool flags = _comfyFindings.Count > 0;
        List<string> Build(bool size) => installed.Select(w => ComfyOfferedRow(w, on.Contains(w.Name), columns,
            scope: InProfileFolder(w.FilePath) ? ComfyScopeProfile : ComfyScopeGlobal, size: size,
            flag: flags ? (_comfyFindings.TryGetValue(w.Name, out var found) && found.Count > 0 ? '!' : ' ') : null)).ToList();
        var workflows = Build(size: true);
        if (_pane.Enabled && workflows.Count > 0 && workflows.Max(r => TextCells.Width(Markup.Remove(r))) > _pane.ListWidth(side: false) - 2)
        {
            workflows = Build(size: false);   // the scope always shows; the band gives the size
        }

        int nameWidth = Math.Max(columns.Name, 2);
        var skipped = problems.Select(p => Markup.Escape("    " + TextCells.Cell(Path.GetFileName(p.FilePath), nameWidth - 2).PadRight(nameWidth)) + Theme.DimMarkup(p.Problem)).ToList();
        return (workflows, skipped);
    }

    /// <summary>The band under the list for item <paramref name="item"/>: what the add row does, a workflow's description and parts, a skipped file's reason.</summary>
    private MenuFooter? ComfyManagerBand(IReadOnlyList<ComfyItem> items, int item)
    {
        if (item < 0 || item >= items.Count)
        {
            return null;
        }

        var target = items[item];
        if (target.Problem is { } problem)
        {
            return new MenuFooter(ComfySkippedBand(problem.FilePath, problem.Problem));
        }

        if (target.Workflow is not { } workflow)
        {
            return new MenuFooter(Help.HelpSettings.Brief(SettingsField.ComfyWorkflowsOffered));
        }

        var lines = new List<string>();
        if (_comfyFindings.TryGetValue(workflow.Name, out var findings) && findings.Count > 0)
        {
            lines.Add("! " + findings[0]);
        }

        if (workflow.Description.Length > 0)
        {
            lines.Add(UntrustedText.OneLine(workflow.Description));
        }

        var parts = ComfyGraphEdit.Load(workflow.GraphJson).Graph is { } graph ? ComfyGraphEdit.Read(graph) : null;
        string model = parts?.Model.Value is { Length: > 0 } m ? m : "";
        string loras = parts is null ? "" : string.Join(", ", parts.Loras.Loras.Where(l => !l.SwitchedOff).Select(l => l.Name));
        string size = workflow.Placeholders.Contains(ComfyWorkflow.WidthKey) ? Invariant(workflow.Defaults.Width) + "×" + Invariant(workflow.Defaults.Height) : "";
        string facts = string.Join(" · ", new[] { model, loras.Length > 0 ? "LoRAs: " + loras : "", size }.Where(s => s.Length > 0));
        if (facts.Length > 0)
        {
            lines.Add(UntrustedText.OneLine(facts));
        }

        if (HidesGlobal(workflow))
        {
            lines.Add(ComfyHidesGlobalNote);
        }

        lines.Add(workflow.FilePath);
        return new MenuFooter(string.Join("\n", lines)) { KeepLines = true };
    }

    /// <summary>The workflow named <paramref name="name"/> at <paramref name="path"/> as the folders hold it now, or null (said) when it is gone.</summary>
    private ComfyWorkflow? ComfyFresh(string name, string path)
    {
        var found = ComfyCatalog.Scan().Workflows.FirstOrDefault(w => string.Equals(w.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            Sink.Error(ComfyText.WorkflowGone(name));
        }

        return found;
    }

    /// <summary>
    /// A workflow's actions page (or a skipped file's: Open the JSON, Delete). Edit, Test, offer or hide, Check and Open come back here on the same
    /// row; Duplicate, Rename, Move and Delete go back to the list, which follows <c>Follow</c> by name.
    /// </summary>
    private async Task<(bool Changed, string? Follow)> ComfyActionsAsync(ComfyItem item, CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            ComfyWorkflow? workflow = null;
            if (item.Workflow is { } listed)
            {
                if ((workflow = ComfyFresh(listed.Name, listed.FilePath)) is null)
                {
                    return (changed, null);
                }
            }
            else if (item.Problem is { } skipped && !File.Exists(skipped.FilePath))
            {
                Sink.Error(ComfyText.WorkflowGone(Path.GetFileNameWithoutExtension(skipped.FilePath)));
                return (changed, null);
            }

            string name = workflow?.Name ?? Path.GetFileNameWithoutExtension(item.Problem!.FilePath);
            bool offered = workflow is not null && ComfyWorkflowCatalog.Offered([workflow], _settings.Current.ComfyWorkflowsOffered).Count > 0;
            bool global = workflow is not null && !InProfileFolder(workflow.FilePath);
            List<List<string>> groups = workflow is null
                ? [[ComfyOpenJsonAction], [ComfyDeleteAction]]
                : [[ComfyEditAction, ComfyTestAction, ComfyCheckAction, offered ? ComfyHideAction : ComfyOfferAction],
                   [ComfyDuplicateAction, ComfyRenameAction, global ? ComfyMoveToProfileAction : ComfyMoveToGlobalAction],
                   [ComfyOpenJsonAction, ComfyOpenMarkdownAction],
                   [ComfyDeleteAction]];
            var actions = groups.SelectMany(g => g).ToList();
            var ruled = RuledRows.Of(groups.Select(g => (IReadOnlyList<string>)g.Select(Markup.Escape).ToList()).ToList());
            var page = new MenuPage(Crumb(FieldName(SettingsField.ComfyWorkflowsOffered) + " › " + name), ruled.Rows, ComfyActionKeys)
            {
                Headings = ruled.Headings,
                Caption = workflow is null ? ComfySkippedBand(item.Problem!.FilePath, item.Problem.Problem) : null,
            };
            if (await PickListAsync(page, ruled.RowOf(cursor), cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return (changed, name);
            }

            int index = ruled.ItemOf(pick.Row);
            if (index < 0)
            {
                continue;
            }

            cursor = index;
            string action = pick.Remove ? ComfyDeleteAction : actions[index];
            switch (action)
            {
                case ComfyEditAction:
                    changed |= await EditInstalledComfyAsync(workflow!, cancellationToken).ConfigureAwait(false);
                    ForgetComfyFindings(name);
                    continue;

                case ComfyTestAction:
                    await TestComfyAsync([workflow!], workflow!.Name, cancellationToken).ConfigureAwait(false);
                    continue;

                case ComfyCheckAction:
                    await ComfyCheckOneAsync(workflow!, page, cancellationToken).ConfigureAwait(false);
                    continue;

                case ComfyOfferAction or ComfyHideAction:
                {
                    var (installed, _) = ComfyCatalog.Scan();
                    var chosen = _settings.Current.ComfyWorkflowsOffered ?? [];
                    var on = ComfyWorkflowCatalog.Offered(installed, chosen).Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var next = installed.Select(w => w.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
                    changed |= ComfySetOffered(installed, chosen, next);
                    continue;
                }

                case ComfyOpenJsonAction:
                    ComfyOpen(workflow?.FilePath ?? item.Problem!.FilePath);
                    continue;

                case ComfyOpenMarkdownAction:
                {
                    var (md, _, error) = ComfyWorkflowFile.EnsureSidecar(workflow!.FilePath, workflow.Family);
                    if (error is not null)
                    {
                        Sink.Error(ComfyOpenFailed(Path.ChangeExtension(workflow.FilePath, ".md"), error));
                    }
                    else
                    {
                        ComfyOpen(md!);
                    }

                    continue;
                }

                case ComfyDuplicateAction:
                {
                    string folder = Path.GetDirectoryName(workflow!.FilePath)!;
                    string? copy = await ComfyAskNameAsync(page, index, ComfyDuplicateQuestion, ComfyWorkflowFile.FreeName(ComfyWorkflowFile.Suggest(name), ComfyRoots, folder),
                        typed => ComfyWorkflowFile.Duplicate(folder, ComfyRoots, name, typed), cancellationToken).ConfigureAwait(false);
                    if (copy is null)
                    {
                        continue;
                    }

                    Sink.Notice(ComfyDuplicatedNotice(name, copy));
                    return (true, copy);
                }

                case ComfyRenameAction:
                {
                    string folder = Path.GetDirectoryName(workflow!.FilePath)!;
                    bool uncovers = !global && File.Exists(Path.Combine(_settings.GlobalComfyDirectory, name + ".json"));
                    string? renamed = await ComfyAskNameAsync(page, index, ComfyRenameQuestion, name,
                        typed => string.Equals(typed, name, StringComparison.Ordinal) ? "" : ComfyWorkflowFile.Rename(folder, ComfyRoots, name, typed), cancellationToken).ConfigureAwait(false);
                    if (renamed is null || string.Equals(renamed, name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ComfyRenameInLists(name, renamed);
                    ForgetComfyFindings(name);
                    Sink.Notice(ComfyRenamedNotice(name, renamed, global));
                    if (uncovers)
                    {
                        Sink.Notice(ComfyRenameUncoveredNotice(name));
                    }

                    return (true, renamed);
                }

                case ComfyMoveToGlobalAction or ComfyMoveToProfileAction:
                {
                    string from = Path.GetDirectoryName(workflow!.FilePath)!;
                    string to = ComfyFolder(!global);
                    if (ComfyWorkflowFile.Move(from, to, ComfyRoots, name) is { } error)
                    {
                        Sink.Error(error);
                        continue;
                    }

                    ForgetComfyFindings(name);
                    Sink.Notice(ComfyMovedNotice(name, !global));
                    return (true, name);
                }

                default:   // Delete
                {
                    var (deleted, done) = await ComfyDeleteAsync(new ComfyItem(workflow, item.Problem), cancellationToken).ConfigureAwait(false);
                    changed |= deleted;
                    if (done)
                    {
                        return (changed, null);
                    }

                    continue;
                }
            }
        }
    }

    /// <summary>
    /// A name typed on the actions page at <paramref name="row"/> and handed to <paramref name="act"/>, asked again with the reason while it
    /// refuses (<paramref name="act"/> returns null when done, "" for nothing to do). The name, or null on Esc.
    /// </summary>
    private async Task<string?> ComfyAskNameAsync(MenuPage page, int row, string question, string initial, Func<string, string?> act, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await ComfyAskTextAsync(page, row, question, initial, allowEmpty: false, cancellationToken).ConfigureAwait(false) is not { } typed)
            {
                return null;
            }

            string name = typed.Trim();
            string? error = act(name);
            if (error is null || error.Length == 0)
            {
                return name;
            }

            Sink.Error(error);
            initial = typed;
        }
    }

    /// <summary>One line typed on <paramref name="page"/>'s row <paramref name="row"/> with <paramref name="question"/> as the caption (the plain prompt without the pane); null on Esc.</summary>
    private async Task<string?> ComfyAskTextAsync(MenuPage page, int row, string question, string initial, bool allowEmpty, CancellationToken cancellationToken)
    {
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Hint = EditKeys, Caption = question }, row, _input, initial, allowEmpty, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(question, EditKeys));
            result = await _input.ReadAsync(initial, remember: false, allowEmpty: allowEmpty, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
        }

        return result is InputResult.Submitted submitted ? submitted.Text : null;
    }

    /// <summary>Opens <paramref name="path"/> in the editor; no editor or a failed launch is said.</summary>
    private void ComfyOpen(string path)
    {
        if (_openFile is null)
        {
            Sink.Error(ComfyOpenFailed(path, "no editor to open it in"));
            return;
        }

        try
        {
            _openFile(path);
            Sink.Notice(ComfyOpenedNotice(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(ComfyOpenFailed(path, Diagnostics.LogText.Excerpt(ex.Message)));
        }
    }

    /// <summary>
    /// Deletes a workflow (or a skipped file) after a yes, No first: the files named, the bin probed first (a drive with none says for good), a
    /// global workflow it uncovers said. The loaded profile's lists drop the name unless a global one takes its place. (Changed, Done): Done
    /// when the files are gone, so the actions page closes.
    /// </summary>
    private async Task<(bool Changed, bool Done)> ComfyDeleteAsync(ComfyItem item, CancellationToken cancellationToken)
    {
        if (item.Workflow is null && item.Problem is null)
        {
            return (false, false);
        }

        string json = item.Workflow?.FilePath ?? item.Problem!.FilePath;
        string name = item.Workflow?.Name ?? Path.GetFileNameWithoutExtension(json);
        if (!File.Exists(json))
        {
            Sink.Error(ComfyText.WorkflowGone(name));
            return (false, true);
        }

        var bin = ThemeBin();
        bool toBin = bin is not null;
        if (bin?.Probe(Path.GetDirectoryName(json)!) is { } probe)
        {
            if (probe.Outcome == RecycleOutcome.Failed)
            {
                Sink.Error(ComfyOpenFailed(json, probe.Message));
                return (false, false);
            }

            toBin = false;
        }

        string md = Path.ChangeExtension(json, ".md");
        var files = File.Exists(md) ? new[] { json, md } : [json];
        bool uncovers = item.Workflow is not null && InProfileFolder(json) && File.Exists(Path.Combine(_settings.GlobalComfyDirectory, name + ".json"));
        if (!await ConfirmAsync(ComfyDeleteQuestion(name, files, toBin, uncovers), cancellationToken, close: false).ConfigureAwait(false))
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return (false, false);
        }

        var (way, error) = ComfyWorkflowFile.DeleteFile(json, name, toBin ? bin : null);
        if (way is null)
        {
            Sink.Error(error!);
            return (false, false);
        }

        Sink.Notice(ComfyDeletedNotice(name, way.Value));
        if (error is not null)
        {
            Sink.Warning(error);
        }

        ForgetComfyFindings(name);
        if (item.Workflow is not null)
        {
            if (uncovers)
            {
                Sink.Notice(ComfyDeleteUncoveredNotice(name));
            }
            else
            {
                ComfyDropFromLists(name);
            }
        }

        return (true, true);
    }

    /// <summary>A rename in the loaded profile's lists, each only where the old name stood, at its place.</summary>
    private void ComfyRenameInLists(string from, string to)
    {
        var current = _settings.Current;
        if (current.ComfyWorkflowsOffered is { } offered && offered.Contains(from, StringComparer.OrdinalIgnoreCase))
        {
            Apply(SettingsField.ComfyWorkflowsOffered, d => d.ComfyWorkflowsOffered = ConnectionOfferedAfterSave(d.ComfyWorkflowsOffered, from, to, offer: true));
        }

        if (current.BotChatLimitedComfyWorkflows is { } botchat && botchat.Contains(from, StringComparer.OrdinalIgnoreCase))
        {
            Apply(SettingsField.BotChatLimitedComfyWorkflows, d => d.BotChatLimitedComfyWorkflows = ConnectionOfferedAfterSave(d.BotChatLimitedComfyWorkflows, from, to, offer: true));
        }
    }

    /// <summary>A deleted name out of the loaded profile's lists; Botchat's back to null (every offered one) when it empties.</summary>
    private void ComfyDropFromLists(string name)
    {
        var current = _settings.Current;
        if (current.ComfyWorkflowsOffered is { } offered && offered.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Apply(SettingsField.ComfyWorkflowsOffered, d => d.ComfyWorkflowsOffered = (d.ComfyWorkflowsOffered ?? []).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        if (current.BotChatLimitedComfyWorkflows is { } botchat && botchat.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Apply(SettingsField.BotChatLimitedComfyWorkflows, d =>
            {
                var left = (d.BotChatLimitedComfyWorkflows ?? []).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
                d.BotChatLimitedComfyWorkflows = left.Count == 0 ? null : left;
            });
        }
    }
}
