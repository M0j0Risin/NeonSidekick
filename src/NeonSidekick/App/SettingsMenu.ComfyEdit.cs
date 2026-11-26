using System.Globalization;
using System.Text.Json.Nodes;
using NeonSidekick.Comfy;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The summary a new workflow ends on and an installed one's Edit opens (2026-11-25, the workflow manager): its rows in sections — <b>Model</b>
/// (model, VAE, LoRAs, CLIP skip, sampler, scheduler), <b>Defaults</b> (size, steps, CFG, denoise, negative), <b>For the model</b> (family,
/// description, picture roles, tips, reinforce) and <b>Saving</b> (name and folder, or the output name) — Enter on one changing it. The graph's
/// parts are read and changed through <see cref="ComfyGraphEdit"/> (a build's are its own fields, the graph made from them); a part it can't
/// change shows why, dim. An Edit writes back only the keys and the graph it changed (<see cref="ComfySidecarEdit"/>, <see cref="ComfyWorkflowFile.Save"/>).
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>A summary row.</summary>
    internal enum ComfyField
    {
        Found,
        Model,
        Vae,
        Loras,
        ClipSkip,
        Sampler,
        Scheduler,
        Width,
        Height,
        Steps,
        Cfg,
        Denoise,
        Negative,
        Family,
        Description,
        Image1,
        Image2,
        Image3,
        Tips,
        Reinforce,
        Name,
        Scope,
        Output,
    }

    /// <summary>The summary rows' labels, by <see cref="ComfyField"/>. Pinned.</summary>
    public static readonly IReadOnlyList<string> ComfyFieldLabels =
        ["Placeholders", "Model", "VAE", "LoRAs", "CLIP skip", "Sampler", "Scheduler", "Width", "Height", "Steps", "CFG", "Denoise", "Negative",
         "Family", "Description", "Picture 1", "Picture 2", "Picture 3", "Tips", "Reinforce", "Name", "Folder", "Output name"];

    /// <summary>The summary's section headings.</summary>
    public static readonly IReadOnlyList<string> ComfySectionHeadings = ["Model", "Defaults", "For the model", "Saving"];

    public const string ComfyEditSaveRow = "Save";
    public const string ComfyEditCaption = "Enter on a row changes it; Save writes the files, Test runs it once without saving.";
    public const string ComfyEditSavedNotice = "Saved.";
    public const string ComfyEditDiscardedNotice = "Nothing saved.";
    public const string ComfyFamilysNote = "the family's";
    public const string ComfyNone = "(none)";
    public const string ComfyLorasTitle = "LoRAs";
    public const string ComfyAddLoraRow = "+ Add LoRA";
    public const string ComfyLoraKeys = "Enter = strength · Del = remove · Esc = back";
    public const string ComfyNoLoras = "The server lists no LoRAs.";
    public const string ComfySwitchedOffMark = "(switched off)";
    public const string ComfyStrengthQuestion = "The strength, -100 to 100 (1 is full); empty removes the LoRA.";
    public const string ComfyImageRoleQuestion = "What this picture is for, in your words (the model reads it); empty for nothing said.";
    public const string ComfyTipsQuestion = "Prompt tips the model reads, one line (longer tips: Open the Markdown).";
    public const string ComfyOutputQuestion = "The name ComfyUI saves the pictures under (its filename_prefix).";
    public const string ComfyNoServerLists = "No ComfyUI URL is set, so the server's lists can't open; the other rows still can.";

    public static string ComfyTipsLines(int lines) => $"{Invariant(lines)} lines: Open the Markdown";

    public static string ComfyFoundCount(int found) => found == 1 ? "1 found" : Invariant(found) + " found";

    public static string ComfyDefaultNote(string value) => $"({value}, {ComfyFamilysNote})";

    public static string ComfyEmptyForDefault(string value) => $" Empty for the family's ({value}).";

    public static string ComfyValueRefused(string label) => $"{label} holds characters the file can't keep: nothing was saved.";

    // ── The draft's graphs and sidecars ──────────────────────────────────────

    /// <summary>A build's graph as it will be saved under <paramref name="name"/>: <see cref="ComfyGraphs.Build"/>, then the VAE and the LoRAs.</summary>
    private static JsonObject ComfyBuildGraph(ComfyDraft draft, string name, bool fromImage)
    {
        var graph = (JsonObject)JsonNode.Parse(ComfyGraphs.Build(name, draft.Checkpoint, draft.EffSampler, draft.EffScheduler, draft.EffClipSkip, fromImage))!;
        if (draft.Vae is { } vae)
        {
            ComfyGraphEdit.SetVae(graph, vae);
        }

        foreach (var lora in draft.Loras)
        {
            ComfyGraphEdit.AddLora(graph, lora.Name, lora.Strength);
        }

        return graph;
    }

    /// <summary>The graph the summary reads: an import's or an Edit's working graph, a build's made afresh (the text one for <c>both</c>).</summary>
    private static JsonObject? ComfyGraphView(ComfyDraft draft) =>
        draft.Import || draft.Editing ? draft.Graph : ComfyBuildGraph(draft, draft.Name.Length > 0 ? draft.Name : "draft", draft.FromImage);

    /// <summary>The graph JSON a new workflow saves under <paramref name="name"/>; an import's <c>neon/…</c> output name follows the new name.</summary>
    private static string ComfyDraftGraphJson(ComfyDraft draft, string name, bool fromImage)
    {
        if (!draft.Import)
        {
            return ComfyBuildGraph(draft, name, fromImage).ToJsonString(ComfyGraphs.Indented);
        }

        if (draft.Graph is not { } working)
        {
            return draft.Imported!.GraphJson;
        }

        var graph = (JsonObject)working.DeepClone();
        var output = ComfyGraphEdit.Read(graph).OutputName;
        // Only a plain name after neon/ follows the workflow's (2026-11-26, the review's #84): neon/portraits/x keeps its subfolder.
        if (output is { Editable: true, Value: { } prefix } && prefix.StartsWith(ComfyGraphs.PrefixFolder, StringComparison.Ordinal)
            && ComfyWorkflowFile.IsValidName(prefix[ComfyGraphs.PrefixFolder.Length..]))
        {
            ComfyGraphEdit.SetText(graph, output, ComfyGraphs.PrefixFolder + name);
        }

        return graph.ToJsonString(ComfyGraphs.Indented);
    }

    /// <summary>A new workflow's sidecar: every default its graph takes, written out (the import's or the family's when not changed).</summary>
    private static string ComfyDraftSidecar(ComfyDraft draft, string graphJson)
    {
        var holes = ComfyWorkflow.FindPlaceholders(graphJson);
        int images = ComfyWorkflow.CountImages(holes);
        return ComfyWorkflowFile.Sidecar(new ComfySidecar(
            draft.Description,
            draft.Family,
            holes.Contains(ComfyWorkflow.WidthKey) ? draft.EffWidth : null,
            holes.Contains(ComfyWorkflow.HeightKey) ? draft.EffHeight : null,
            holes.Contains(ComfyWorkflow.StepsKey) ? draft.EffSteps : null,
            holes.Contains(ComfyWorkflow.CfgKey) ? draft.EffCfg : null,
            holes.Contains(ComfyWorkflow.DenoiseKey) ? draft.EffDenoise : null,
            draft.EffNegative,
            images > 0 ? draft.ImageRoles.Take(images).ToList() : null,
            draft.Reinforce,
            draft.Tips));
    }

    /// <summary>The placeholders the draft's graphs hold (both of <c>both</c>'s).</summary>
    private static IReadOnlySet<string> ComfyPlaceholders(ComfyDraft draft)
    {
        if (draft.Editing)
        {
            return draft.Graph is { } edited ? ComfyWorkflow.FindPlaceholders(edited.ToJsonString()) : draft.Original!.Placeholders;
        }

        if (draft.Import)
        {
            return ComfyWorkflow.FindPlaceholders(draft.Imported?.GraphJson ?? "");
        }

        var holes = new HashSet<string>(ComfyWorkflow.FindPlaceholders(ComfyBuildGraph(draft, "draft", draft.FromImage).ToJsonString()), StringComparer.Ordinal);
        if (draft.Kind == ComfyKind.Both)
        {
            holes.UnionWith(ComfyWorkflow.FindPlaceholders(ComfyBuildGraph(draft, "draft", true).ToJsonString()));
        }

        return holes;
    }

    // ── The summary ─────────────────────────────────────────────────────────

    /// <summary>The fields a section shows for this draft, in order.</summary>
    private static List<List<ComfyField>> ComfySections(ComfyDraft draft, IReadOnlySet<string> holes)
    {
        int images = ComfyWorkflow.CountImages(holes);
        var model = new List<ComfyField>();
        if (draft.Import && !draft.Editing)
        {
            model.Add(ComfyField.Found);
        }

        model.AddRange([ComfyField.Model, ComfyField.Vae, ComfyField.Loras, ComfyField.ClipSkip, ComfyField.Sampler, ComfyField.Scheduler]);
        var defaults = new List<ComfyField> { ComfyField.Width, ComfyField.Height, ComfyField.Steps, ComfyField.Cfg, ComfyField.Denoise, ComfyField.Negative };
        var forModel = new List<ComfyField> { ComfyField.Family, ComfyField.Description };
        forModel.AddRange(new[] { ComfyField.Image1, ComfyField.Image2, ComfyField.Image3 }.Take(images));
        forModel.Add(ComfyField.Tips);
        if (holes.Contains(ComfyWorkflow.NegativeKey) && ComfyFamilies.UsesNegative(draft.Family))
        {
            forModel.Add(ComfyField.Reinforce);   // only where TakesReinforcement could hold
        }

        var saving = draft.Editing ? new List<ComfyField> { ComfyField.Output } : [ComfyField.Name, ComfyField.Scope];
        return [model, defaults, forModel, saving];
    }

    /// <summary>A field's value as markup, and whether Enter can change it.</summary>
    private (string Markup, bool Editable) ComfyFieldValue(ComfyField field, ComfyDraft draft, ComfyGraphParts? parts, IReadOnlySet<string> holes)
    {
        static (string, bool) Plain(string text) => (Markup.Escape(text), true);
        static (string, bool) Dim(string text) => (Theme.DimMarkup(text), false);
        (string, bool) Part(ComfyGraphPart? part, string? empty = null) =>
            part is null ? Dim(draft.GraphWhy ?? ComfyWizardNotNeeded)
            : part.Editable ? Plain(part.Value is { Length: > 0 } v ? UntrustedText.Name(v) : empty ?? ComfyWizardUnset)
            : (Markup.Escape(part.Value is { Length: > 0 } shown ? UntrustedText.Name(shown) + "  " : "") + Theme.DimMarkup(part.Why!), false);
        (string, bool) Number(string key, double? own, double eff) =>
            !holes.Contains(key) ? Dim(ComfyWizardNotNeeded)
            : own is { } value || !draft.Editing ? Plain(Fmt(own ?? eff))
            : (Theme.DimMarkup(ComfyDefaultNote(Fmt(eff))), true);

        switch (field)
        {
            case ComfyField.Found:
                return Plain(ComfyFoundCount(draft.Imported?.Found.Count ?? 0));
            case ComfyField.Model:
                return Part(parts?.Model);
            case ComfyField.Vae:
                return Part(parts?.Vae, ComfyCheckpointVaeRow);
            case ComfyField.Loras:
            {
                if (parts is null)
                {
                    return Dim(draft.GraphWhy ?? ComfyWizardNotNeeded);
                }

                var on = parts.Loras.Loras.Where(l => !l.SwitchedOff).Select(l => UntrustedText.Name(l.Name) + (l.StrengthModel is { } s ? " " + Fmt(s) : "")).ToList();
                string text = on.Count == 0 ? ComfyNone : string.Join(", ", on);
                return parts.Loras.Editable || parts.Loras.Loras.Any(l => l.Editable)
                    ? Plain(text)
                    : (Markup.Escape(text + "  ") + Theme.DimMarkup(parts.Loras.Why ?? parts.Loras.Loras.First(l => !l.Editable).Why!), on.Count > 0);
            }

            case ComfyField.ClipSkip:
            {
                var part = parts?.ClipSkip;
                return part is { Editable: true } ? Plain(part.Value is { Length: > 0 } v && v != "0" ? v : ComfyWizardClipSkipRows[0]) : Part(part);
            }

            case ComfyField.Sampler:
                return Part(parts?.Sampler);
            case ComfyField.Scheduler:
                return Part(parts?.Scheduler);
            case ComfyField.Width:
                return Number(ComfyWorkflow.WidthKey, draft.Width, draft.EffWidth);
            case ComfyField.Height:
                return Number(ComfyWorkflow.HeightKey, draft.Height, draft.EffHeight);
            case ComfyField.Steps:
                return Number(ComfyWorkflow.StepsKey, draft.Steps, draft.EffSteps);
            case ComfyField.Cfg:
                return Number(ComfyWorkflow.CfgKey, draft.Cfg, draft.EffCfg);
            case ComfyField.Denoise:
                return Number(ComfyWorkflow.DenoiseKey, draft.Denoise, draft.EffDenoise);
            case ComfyField.Negative:
                return !holes.Contains(ComfyWorkflow.NegativeKey) ? Dim(ComfyWizardNotNeeded)
                    : draft.Editing && draft.Negative is null ? (Theme.DimMarkup(ComfyDefaultNote(draft.EffNegative.Length == 0 ? ComfyNone : UntrustedText.OneLine(draft.EffNegative))), true)
                    : Plain(draft.EffNegative.Length == 0 ? ComfyNone : UntrustedText.OneLine(draft.EffNegative));
            case ComfyField.Family:
                return Plain(ComfyFamilies.Name(draft.Family));
            case ComfyField.Description:
                return Plain(draft.Description.Length == 0 ? ComfyWizardUnset : UntrustedText.OneLine(draft.Description));
            case ComfyField.Image1 or ComfyField.Image2 or ComfyField.Image3:
            {
                string role = draft.ImageRoles[field - ComfyField.Image1];
                return Plain(role.Length == 0 ? ComfyWizardUnset : UntrustedText.OneLine(role));
            }

            case ComfyField.Tips:
            {
                int lines = draft.Tips.Length == 0 ? 0 : draft.Tips.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Length;
                return lines > 1 ? Dim(ComfyTipsLines(lines)) : Plain(lines == 0 ? ComfyWizardUnset : UntrustedText.OneLine(draft.Tips));
            }

            case ComfyField.Reinforce:
                return Plain(draft.Reinforce ? "yes" : "no");
            case ComfyField.Name:
                return Plain(ComfyWizardNames(draft) is { Count: 2 } both ? both[0] + ", " + both[1] : draft.Name.Length == 0 ? ComfyWizardUnset : draft.Name);
            case ComfyField.Scope:
                return Plain((draft.Global ? ComfyScopeGlobal : ComfyScopeProfile) + "  " + ComfyFolder(draft.Global));
            default:
                return Part(parts?.OutputName);
        }
    }

    /// <summary>
    /// The summary over <paramref name="draft"/>: its actions (a new one's two saves, or an Edit's one; Test; Cancel), then the sections. Enter on a
    /// row changes it here. (Done, Changed, Added): not Done on Esc (the wizard goes back to the name; an Edit back to the actions, nothing saved).
    /// </summary>
    private async Task<(bool Done, bool Changed, string? Added)> ComfySummaryAsync(ComfyDraft draft, CancellationToken cancellationToken)
    {
        object at = 0;
        bool touched = false;
        string title = draft.Editing ? Crumb(FieldName(SettingsField.ComfyWorkflowsOffered) + " › " + draft.Name + " › " + ComfyEditAction) : ComfyWizardTitle;
        while (true)
        {
            var holes = ComfyPlaceholders(draft);
            var view = ComfyGraphView(draft);
            var parts = view is null ? null : ComfyGraphEdit.Read(view);
            var sections = ComfySections(draft, holes);
            var actions = draft.Editing ? new List<string> { ComfyEditSaveRow } : [ComfyWizardSaveOfferedRow, ComfyWizardSaveHiddenRow];
            var tail = new List<string> { ComfyWizardTestRow, ComfyWizardCancelRow };
            var fields = sections.SelectMany(s => s).ToList();
            int width = fields.Max(f => ComfyFieldLabels[(int)f].Length) + 2;
            var values = fields.Select(f => ComfyFieldValue(f, draft, parts, holes)).ToList();
            string Row(ComfyField f, int i) => Markup.Escape(ComfyFieldLabels[(int)f].PadRight(width)) + values[i].Markup;
            int offset = 0;
            var groups = new List<IReadOnlyList<string>> { actions.Select(Markup.Escape).ToList(), tail.Select(Markup.Escape).ToList() };
            foreach (var section in sections)
            {
                groups.Add(section.Select((f, i) => Row(f, offset + i)).ToList());
                offset += section.Count;
            }

            var ruled = RuledRows.Of(groups, [null, null, .. ComfySectionHeadings]);
            int lead = actions.Count + tail.Count;
            int cursor = at is ComfyField wanted && fields.IndexOf(wanted) is >= 0 and var found ? lead + found : at is int item ? Math.Min(item, lead - 1) : 0;
            string caption = draft.Editing ? ComfyEditCaption
                : !draft.Import && ComfyGraphs.UsuallySplit(draft.Family) ? ComfyWizardSplitWarning(ComfyFamilies.Name(draft.Family))
                : ComfyWizardSummaryCaption;
            var page = new MenuPage(title, ruled.Rows, PickKeys) { Headings = ruled.Headings, Caption = caption, CaptionGrows = true };
            if (!_pane.Enabled)
            {
                Flow.Notice(caption);
            }

            if (await PickAsync(page, ruled.RowOf(cursor), cancellationToken).ConfigureAwait(false) is not { } pickedRow)
            {
                if (draft.Editing)
                {
                    Sink.Notice(touched ? ComfyEditDiscardedNotice : ConnectionWizardUnchangedNotice);
                }

                return (false, false, null);
            }

            int picked = ruled.ItemOf(pickedRow);
            if (picked < 0)
            {
                continue;
            }

            if (picked >= lead)
            {
                var field = fields[picked - lead];
                at = field;
                if (!values[picked - lead].Editable)
                {
                    continue;
                }

                touched |= await ComfyEditFieldAsync(field, draft, page, pickedRow, parts, cancellationToken).ConfigureAwait(false);
                continue;
            }

            at = picked;
            if (picked == actions.Count)
            {
                if (ComfyDraftWorkflows(draft) is { } workflows)
                {
                    await TestComfyAsync(workflows, draft.Name, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (picked == actions.Count + 1)
            {
                Sink.Notice(draft.Editing ? (touched ? ComfyEditDiscardedNotice : ConnectionWizardUnchangedNotice) : ComfyWizardCancelledNotice);
                return (true, false, null);
            }

            if (draft.Editing)
            {
                if (ComfyEditSave(draft) is { } saved)
                {
                    return (true, saved, null);
                }

                continue;
            }

            if (ComfyWizardSave(draft, offer: picked == 0) is { } done)
            {
                return (true, done.Changed, done.Added);
            }
        }
    }

    /// <summary>One summary row changed through its page; true when the draft changed.</summary>
    private async Task<bool> ComfyEditFieldAsync(ComfyField field, ComfyDraft draft, MenuPage page, int row, ComfyGraphParts? parts, CancellationToken cancellationToken)
    {
        bool build = !draft.Import && !draft.Editing;
        string title = page.Title;
        switch (field)
        {
            case ComfyField.Found:
                await PickAsync(new MenuPage(title + " › " + ComfyFieldLabels[(int)field], (draft.Imported?.Found ?? []).Select(f => Markup.Escape(UntrustedText.OneLine(f))).ToList(), PickKeys), 0, cancellationToken).ConfigureAwait(false);
                return false;

            case ComfyField.Model:
            {
                var part = parts!.Model;
                string nodeClass = build ? "CheckpointLoaderSimple" : part.ClassType!;
                string input = build ? "ckpt_name" : part.Input!;
                if (await ComfyServerPickAsync(draft, title, ComfyWizardCheckpointQuestion, nodeClass, input, part.Value, null, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                if (build)
                {
                    ComfySetCheckpoint(draft, picked.Value!);
                    return true;
                }

                return ComfyGraphChange(ComfyGraphEdit.SetText(draft.Graph!, part, picked.Value!));
            }

            case ComfyField.Vae:
            {
                bool own = build || parts!.CheckpointVae;
                if (await ComfyServerPickAsync(draft, title, ComfyWizardVaeQuestion, "VAELoader", "vae_name", parts!.Vae.Value, own ? ComfyCheckpointVaeRow : null, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                if (build)
                {
                    draft.Vae = picked.Value;
                    return true;
                }

                return ComfyGraphChange(ComfyGraphEdit.SetVae(draft.Graph!, picked.Value));
            }

            case ComfyField.Loras:
                return await ComfyLorasAsync(draft, title, cancellationToken).ConfigureAwait(false);

            case ComfyField.ClipSkip:
            {
                var rows = ComfyWizardClipSkipRows.ToList();
                int now = int.TryParse(parts!.ClipSkip.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
                if (now is not (0 or 2))
                {
                    rows.Add(Invariant(now));
                }

                int cursor = now == 0 ? 0 : now == 2 ? 1 : 2;
                if (await PickAsync(new MenuPage(title, rows.Select(Markup.Escape).ToList(), PickKeys) { Caption = ComfyWizardClipSkipQuestion }, cursor, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                int skip = picked == 0 ? 0 : picked == 1 ? 2 : now;
                if (build)
                {
                    draft.ClipSkip = skip;
                    return true;
                }

                return ComfyGraphChange(ComfyGraphEdit.SetClipSkip(draft.Graph!, skip));
            }

            case ComfyField.Sampler or ComfyField.Scheduler:
            {
                var part = field == ComfyField.Sampler ? parts!.Sampler : parts!.Scheduler;
                string input = field == ComfyField.Sampler ? "sampler_name" : "scheduler";
                string question = field == ComfyField.Sampler ? ComfyWizardSamplerQuestion : ComfyWizardSchedulerQuestion;
                // The sampler node's own class (2026-11-26, the review's #54): a KSamplerAdvanced's lists, as the Model row reads its loader's.
                if (await ComfyServerPickAsync(draft, title, question, part.ClassType ?? "KSampler", input, part.Value, null, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                if (build)
                {
                    if (field == ComfyField.Sampler)
                    {
                        draft.Sampler = picked.Value;
                    }
                    else
                    {
                        draft.Scheduler = picked.Value;
                    }

                    return true;
                }

                return ComfyGraphChange(ComfyGraphEdit.SetText(draft.Graph!, part, picked.Value!));
            }

            case ComfyField.Width:
                return await ComfyNumberAsync(page, row, ComfyWizardWidthQuestion, draft.Width, draft.EffWidth, draft, ComfyStudio.MinSide, ComfyStudio.MaxSide, true, v => draft.Width = (int?)v, cancellationToken).ConfigureAwait(false);
            case ComfyField.Height:
                return await ComfyNumberAsync(page, row, ComfyWizardHeightQuestion, draft.Height, draft.EffHeight, draft, ComfyStudio.MinSide, ComfyStudio.MaxSide, true, v => draft.Height = (int?)v, cancellationToken).ConfigureAwait(false);
            case ComfyField.Steps:
                return await ComfyNumberAsync(page, row, ComfyWizardStepsQuestion, draft.Steps, draft.EffSteps, draft, 1, ComfyStudio.MaxSteps, true, v => draft.Steps = (int?)v, cancellationToken).ConfigureAwait(false);
            case ComfyField.Cfg:
                return await ComfyNumberAsync(page, row, ComfyWizardCfgQuestion, draft.Cfg, draft.EffCfg, draft, 0, ComfyStudio.MaxCfg, false, v => draft.Cfg = v, cancellationToken).ConfigureAwait(false);
            case ComfyField.Denoise:
                return await ComfyNumberAsync(page, row, ComfyWizardDenoiseQuestion, draft.Denoise, draft.EffDenoise, draft, 0, 1, false, v => draft.Denoise = v, cancellationToken).ConfigureAwait(false);

            case ComfyField.Negative:
            {
                if (await ComfyAskTextAsync(page, row, ComfyWizardNegativeQuestion, draft.Editing ? draft.Negative ?? "" : draft.EffNegative, allowEmpty: true, cancellationToken).ConfigureAwait(false) is not { } typed)
                {
                    return false;
                }

                draft.Negative = ComfySidecarEdit.Fold(typed).Trim();   // empty: no negative (a missing key is the family's)
                return true;
            }

            case ComfyField.Family:
            {
                if (await PickAsync(new MenuPage(title, ComfyWizardFamilyRows.Select(Markup.Escape).ToList(), PickKeys) { Caption = ComfyWizardFamilyQuestion }, (int)draft.Family, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Family = (ComfyFamily)picked;
                draft.FamilyTouched = true;
                return true;
            }

            case ComfyField.Description:
                return await ComfyTypedAsync(page, row, ComfyWizardDescriptionQuestion, draft.Description, v => draft.Description = v, cancellationToken).ConfigureAwait(false);

            case ComfyField.Image1 or ComfyField.Image2 or ComfyField.Image3:
            {
                int slot = field - ComfyField.Image1;
                return await ComfyTypedAsync(page, row, ComfyImageRoleQuestion, draft.ImageRoles[slot], v => draft.ImageRoles[slot] = v, cancellationToken).ConfigureAwait(false);
            }

            case ComfyField.Tips:
                return await ComfyTypedAsync(page, row, ComfyTipsQuestion, draft.Tips, v => draft.Tips = v, cancellationToken).ConfigureAwait(false);

            case ComfyField.Reinforce:
                draft.Reinforce = !draft.Reinforce;
                return true;

            case ComfyField.Name:
                return await ComfyAskNameAsync(page, row, ComfyWizardNameQuestion, draft.Name, typed => ComfySetName(draft, typed), cancellationToken).ConfigureAwait(false) is not null;

            case ComfyField.Scope:
            {
                string[] rows = [ComfyScopeProfile + "  " + ComfyFolder(false), ComfyScopeGlobal + "   " + ComfyFolder(true)];
                if (await PickAsync(new MenuPage(title, rows.Select(Markup.Escape).ToList(), PickKeys) { Caption = ComfyWizardScopeQuestion }, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            default:   // Output
            {
                var part = parts!.OutputName;
                if (await ComfyAskTextAsync(page, row, ComfyOutputQuestion, part.Value ?? "", allowEmpty: false, cancellationToken).ConfigureAwait(false) is not { } typed)
                {
                    return false;
                }

                return ComfyGraphChange(ComfyGraphEdit.SetText(draft.Graph!, part, ComfySidecarEdit.Fold(typed).Trim()));
            }
        }
    }

    /// <summary>A graph edit's outcome: true when made, else the reason said.</summary>
    private bool ComfyGraphChange(string? why)
    {
        if (why is null)
        {
            return true;
        }

        Sink.Error(why);
        return false;
    }

    /// <summary>A value picked from the server's <paramref name="nodeClass"/>.<paramref name="input"/> list (<see cref="ComfyPickValueAsync"/>); null on Esc or no list.</summary>
    private async Task<(string? Value, bool Ok)?> ComfyServerPickAsync(ComfyDraft draft, string title, string question, string nodeClass, string input, string? current, string? noneRow, CancellationToken cancellationToken, string? empty = null)
    {
        if (await ComfyChoicesAsync(draft, nodeClass, input, cancellationToken, empty).ConfigureAwait(false) is not { } list)
        {
            return null;
        }

        return await ComfyPickValueAsync(title, question, list, current is { Length: > 0 } ? current : null, noneRow, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A free-text row: folded to one line and trimmed; empty allowed.</summary>
    private async Task<bool> ComfyTypedAsync(MenuPage page, int row, string question, string initial, Action<string> set, CancellationToken cancellationToken)
    {
        if (await ComfyAskTextAsync(page, row, question, initial, allowEmpty: true, cancellationToken).ConfigureAwait(false) is not { } typed)
        {
            return false;
        }

        set(ComfySidecarEdit.Fold(typed).Trim());
        return true;
    }

    /// <summary>
    /// A number row: the slot opens on the draft's own value (a new workflow's on the default it will write; an Edit's empty when the sidecar
    /// leaves it to the family, so Enter never writes the family's value as its own); empty clears it back to the default. Asked again while wrong.
    /// </summary>
    private async Task<bool> ComfyNumberAsync(MenuPage page, int row, string question, double? own, double eff, ComfyDraft draft, double min, double max, bool whole, Action<double?> set, CancellationToken cancellationToken)
    {
        string initial = own is { } value ? Fmt(value) : draft.Editing ? "" : Fmt(eff);
        string caption = question + (draft.Editing ? ComfyEmptyForDefault(Fmt(eff)) : "");
        while (true)
        {
            if (await ComfyAskTextAsync(page, row, caption, initial, allowEmpty: true, cancellationToken).ConfigureAwait(false) is not { } typed)
            {
                return false;
            }

            string text = typed.Trim();
            if (text.Length == 0)
            {
                set(null);
                return true;
            }

            string range = Fmt(min) + " to " + Fmt(max);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < min || number > max
                || (whole && number != Math.Floor(number)))
            {
                Sink.Error(ComfyWizardNumberError(text, range));
                initial = typed;
                continue;
            }

            set(number);
            return true;
        }
    }

    // ── LoRAs ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The LoRAs page: <c>+ Add LoRA</c>, then one row each with its strength. Enter edits the strength (−100 to 100), Del or a cleared strength
    /// removes; a LoRA behind a switch keeps its strength editable, a switched-off one or a read-only chain shows why. True when anything changed.
    /// </summary>
    private async Task<bool> ComfyLorasAsync(ComfyDraft draft, string title, CancellationToken cancellationToken)
    {
        bool build = !draft.Import && !draft.Editing;
        bool changed = false;
        int cursor = 0;
        string crumb = title + " › " + ComfyLorasTitle;
        while (true)
        {
            var view = ComfyGraphView(draft);
            var chain = view is null ? new ComfyLoraChain([], draft.GraphWhy) : ComfyGraphEdit.Read(view).Loras;
            var loras = chain.Loras;
            int width = loras.Count == 0 ? 0 : Math.Min(loras.Max(l => TextCells.Width(UntrustedText.Name(l.Name))), 48) + 2;
            var rows = loras.Select(l =>
            {
                string head = TextCells.Cell(UntrustedText.Name(l.Name), width - 2).PadRight(width) + (l.StrengthModel is { } s ? Fmt(s) : "");
                string note = l.SwitchedOff ? ComfySwitchedOffMark : l.Why ?? "";
                return Markup.Escape(head) + (note.Length > 0 ? Theme.DimMarkup("  " + note) : "");
            }).ToList();
            bool canAdd = build || chain.Editable;
            var ruled = RuledRows.Of([canAdd ? new List<string> { Markup.Escape(ComfyAddLoraRow) } : new List<string>(), rows]);
            int lead = canAdd ? 1 : 0;
            var page = new MenuPage(crumb, ruled.Rows.Count == 0 ? [Theme.DimMarkup(ComfyNone)] : ruled.Rows, ComfyLoraKeys)
            {
                Headings = ruled.Headings,
                Caption = canAdd ? null : chain.Why,
                Placeholder = ruled.Rows.Count == 0,
            };
            if (await PickListAsync(page, ruled.RowOf(cursor), cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return changed;
            }

            int item = ruled.ItemOf(pick.Row);
            if (item < 0 || ruled.Rows.Count == 0)
            {
                continue;
            }

            cursor = item;
            if (item < lead)
            {
                if (pick.Remove)
                {
                    continue;
                }

                if (await ComfyServerPickAsync(draft, crumb, ComfyWizardLoraQuestion, "LoraLoader", "lora_name", null, null, cancellationToken, ComfyNoLoras).ConfigureAwait(false) is not { Value: { } name })
                {
                    continue;
                }

                if (build)
                {
                    draft.Loras.Add(new ComfyDraftLora(name, 1));
                    changed = true;
                }
                else
                {
                    changed |= ComfyGraphChange(ComfyGraphEdit.AddLora(draft.Graph!, name, 1).Why);
                }

                cursor = lead + loras.Count;
                continue;
            }

            var lora = loras[item - lead];
            int index = item - lead;
            // Del still removes a LoRA whose strength another node sets (2026-11-26, #20); only its strength can't change.
            bool removable = !lora.SwitchedOff && lora.Why == ComfyText.EditStrengthNotLiteral;
            if (!lora.Editable && !(removable && pick.Remove))
            {
                if (lora.Why is { } why && !lora.SwitchedOff)
                {
                    Sink.Error(why);
                }

                continue;
            }

            double? strength;
            if (pick.Remove)
            {
                strength = null;
            }
            else
            {
                string initial = lora.StrengthModel is { } s ? Fmt(s) : "";
                if (await ComfyStrengthAsync(page, pick.Row, initial, cancellationToken).ConfigureAwait(false) is not { } typed)
                {
                    continue;
                }

                strength = typed.Value;
            }

            if (build)
            {
                if (strength is { } set)
                {
                    draft.Loras[index].Strength = set;
                }
                else
                {
                    draft.Loras.RemoveAt(index);
                }

                changed = true;
                continue;
            }

            changed |= ComfyGraphChange(strength is { } value ? ComfyGraphEdit.SetLoraStrength(draft.Graph!, lora.Node, value) : ComfyGraphEdit.RemoveLora(draft.Graph!, lora.Node));
        }
    }

    /// <summary>A LoRA's strength typed (−100 to 100), asked again while wrong; (null) when cleared, null on Esc.</summary>
    private async Task<(double? Value, bool Ok)?> ComfyStrengthAsync(MenuPage page, int row, string initial, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await ComfyAskTextAsync(page, row, ComfyStrengthQuestion, initial, allowEmpty: true, cancellationToken).ConfigureAwait(false) is not { } typed)
            {
                return null;
            }

            string text = typed.Trim();
            if (text.Length == 0)
            {
                return (null, true);
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value >= -100 && value <= 100)
            {
                return (value, true);
            }

            Sink.Error(ComfyWizardNumberError(text, "-100 to 100"));
            initial = typed;
        }
    }

    // ── An installed workflow's Edit ─────────────────────────────────────────

    /// <summary>
    /// The summary over an installed workflow (the actions page's Edit): its files read as they are, its graph changed in a working copy, and
    /// Save writing back only what changed. True when it saved.
    /// </summary>
    private async Task<bool> EditInstalledComfyAsync(ComfyWorkflow workflow, CancellationToken cancellationToken)
    {
        var json = ComfyFileText.Read(workflow.FilePath);
        var md = ComfyFileText.Read(Path.ChangeExtension(workflow.FilePath, ".md"));
        if (!json.Exists)
        {
            Sink.Error(ComfyText.WorkflowGone(workflow.Name));
            return false;
        }

        var (graph, why) = ComfyGraphEdit.Load(json.Text);
        var draft = new ComfyDraft
        {
            Editing = true,
            JsonFile = json,
            MdFile = md,
            Original = workflow,
            OriginalGraph = (JsonObject?)graph?.DeepClone(),
            Graph = graph,
            GraphWhy = why,
            Name = workflow.Name,
            Family = workflow.Family,
            Width = workflow.Width,
            Height = workflow.Height,
            Steps = workflow.Steps,
            Cfg = workflow.Cfg,
            Denoise = workflow.Denoise,
            Negative = workflow.Negative,
            Description = workflow.Description,
            Tips = workflow.Tips,
            Reinforce = workflow.Reinforce,
            Import = true,
        };
        for (int i = 0; i < draft.ImageRoles.Length; i++)
        {
            draft.ImageRoles[i] = workflow.ImageRole(i);
        }

        var (_, saved, _) = await ComfySummaryAsync(draft, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    /// <summary>An Edit's new texts: the graph when it changed, the sidecar with each changed key set in place; or why it can't be written.</summary>
    private static (string? Json, string? Md, string? Error) ComfyEditTexts(ComfyDraft draft)
    {
        var original = draft.Original!;
        string? json = draft.Graph is { } graph && draft.OriginalGraph is { } before && !JsonNode.DeepEquals(graph, before) ? graph.ToJsonString(ComfyGraphs.Indented) : null;
        string md = draft.MdFile!.Text;
        string start = md;
        string? error = null;
        void Put(string label, string? next)
        {
            if (next is null)
            {
                error ??= ComfyValueRefused(label);
            }
            else
            {
                md = next;
            }
        }

        void Number(ComfyField field, string key, double? now, double? was)
        {
            if (now != was)
            {
                Put(ComfyFieldLabels[(int)field], ComfySidecarEdit.SetNumber(md, key, now));
            }
        }

        Number(ComfyField.Width, ComfyWorkflow.WidthKey, draft.Width, original.Width);
        Number(ComfyField.Height, ComfyWorkflow.HeightKey, draft.Height, original.Height);
        Number(ComfyField.Steps, ComfyWorkflow.StepsKey, draft.Steps, original.Steps);
        Number(ComfyField.Cfg, ComfyWorkflow.CfgKey, draft.Cfg, original.Cfg);
        Number(ComfyField.Denoise, ComfyWorkflow.DenoiseKey, draft.Denoise, original.Denoise);
        if (draft.Family != original.Family)
        {
            Put(ComfyFieldLabels[(int)ComfyField.Family], ComfySidecarEdit.SetWord(md, "family", ComfyFamilies.Name(draft.Family)));
        }

        if (!string.Equals(draft.Description, original.Description, StringComparison.Ordinal))
        {
            Put(ComfyFieldLabels[(int)ComfyField.Description], ComfySidecarEdit.Set(md, "description", draft.Description.Length == 0 ? null : draft.Description));
        }

        if (!string.Equals(draft.Negative, original.Negative, StringComparison.Ordinal))
        {
            Put(ComfyFieldLabels[(int)ComfyField.Negative], ComfySidecarEdit.Set(md, ComfyWorkflow.NegativeKey, draft.Negative));
        }

        if (draft.Reinforce != original.Reinforce)
        {
            Put(ComfyFieldLabels[(int)ComfyField.Reinforce], draft.Reinforce ? ComfySidecarEdit.Set(md, "reinforce", null) : ComfySidecarEdit.SetWord(md, "reinforce", "false"));
        }

        for (int i = 0; i < draft.ImageRoles.Length; i++)
        {
            if (!string.Equals(draft.ImageRoles[i], original.ImageRole(i), StringComparison.Ordinal))
            {
                Put(ComfyFieldLabels[(int)ComfyField.Image1 + i], ComfySidecarEdit.Set(md, ComfyWorkflow.ImageKeys[i], draft.ImageRoles[i].Length == 0 ? null : draft.ImageRoles[i]));
            }
        }

        if (!string.Equals(draft.Tips, original.Tips, StringComparison.Ordinal))
        {
            md = ComfySidecarEdit.SetBody(md, draft.Tips);
        }

        return (json, string.Equals(md, start, StringComparison.Ordinal) ? null : md, error);
    }

    /// <summary>An Edit saved (<see cref="ComfyWorkflowFile.Save"/>): true when written, false when nothing changed (said), null on a refusal (said).</summary>
    private bool? ComfyEditSave(ComfyDraft draft)
    {
        var (json, md, error) = ComfyEditTexts(draft);
        if (error is not null)
        {
            Sink.Error(error);
            return null;
        }

        if (json is null && md is null)
        {
            Sink.Notice(ConnectionWizardUnchangedNotice);
            return false;
        }

        if (ComfyWorkflowFile.Save(draft.Name, draft.JsonFile!, json, draft.MdFile!, md) is { } refused)
        {
            Sink.Error(refused);
            return null;
        }

        Sink.Notice(ComfyEditSavedNotice);
        return true;
    }

    /// <summary>The workflows a summary's Test runs: a new one's (both of <c>both</c>) or an Edit's as it would be saved; null with the reason said.</summary>
    private List<ComfyWorkflow>? ComfyDraftWorkflows(ComfyDraft draft)
    {
        if (ComfyWizardMissing(draft) is { } missing)
        {
            Sink.Error(missing);
            return null;
        }

        var made = new List<ComfyWorkflow>();
        if (draft.Editing)
        {
            var (json, md, error) = ComfyEditTexts(draft);
            if (error is not null)
            {
                Sink.Error(error);
                return null;
            }

            if (!ComfyWorkflow.TryParse(draft.Name, draft.Original!.FilePath, json ?? draft.JsonFile!.Text, md ?? draft.MdFile!.Text, out var edited, out string? problem))
            {
                Sink.Error(ComfyText.SaveRefused(problem!));
                return null;
            }

            made.Add(edited!);
            return made;
        }

        var names = ComfyWizardNames(draft);
        for (int i = 0; i < names.Count; i++)
        {
            string json = ComfyDraftGraphJson(draft, names[i], draft.Kind == ComfyKind.Both ? i == 1 : draft.FromImage);
            if (!ComfyWorkflow.TryParse(names[i], "", json, ComfyDraftSidecar(draft, json), out var workflow, out string? problem))
            {
                Sink.Error(ComfyWorkflowAddFailed(names[i], problem!));
                return null;
            }

            made.Add(workflow!);
        }

        return made;
    }
}
