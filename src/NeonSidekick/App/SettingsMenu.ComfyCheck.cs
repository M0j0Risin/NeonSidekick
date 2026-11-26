using System.Text.Json.Nodes;
using NeonSidekick.Comfy;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The server check in the workflow list (2026-11-25): <c>c</c> holds every workflow against the server's <c>/object_info</c>
/// (<see cref="ComfyCheck"/>), a <c>!</c> marking each with a finding and the band giving the first; the actions page's Check lists one
/// workflow's findings. Findings read "not on the server's list" (a custom node may take more); nothing is ever unticked by a check. A save,
/// rename, move or delete forgets that workflow's findings; another URL forgets all.
/// </summary>
internal sealed partial class SettingsMenu
{
    public const string ComfyCheckingLabel = "Checking the workflows against the server…";
    public const string ComfyCheckCleanNotice = "Every workflow checked holds only what the server lists.";
    public const string ComfyCheckOneCleanNotice = "It holds only what the server lists.";
    public const string ComfyCheckCancelledNotice = "Check stopped; what it found so far is marked.";

    public static string ComfyCheckFoundNotice(int workflows) => workflows == 1 ? "1 workflow holds what the server doesn't list: marked !." : $"{Invariant(workflows)} workflows hold what the server doesn't list: marked !.";

    /// <summary>The findings by workflow name, kept for the menu's life (forgotten by name on a change, all on another URL).</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _comfyFindings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The server's answers by class, for one check.</summary>
    private readonly Dictionary<string, ComfyNodeInfo?> _comfyInfo = new(StringComparer.Ordinal);

    /// <summary>The URL the findings were made against.</summary>
    private string? _comfyFindingsUrl;

    private void ForgetComfyFindings(string name) => _comfyFindings.Remove(name);

    /// <summary>The findings made against another URL are forgotten.</summary>
    private void ComfyFindingsForUrl()
    {
        string url = _settings.Current.ComfyUrl ?? "";
        if (!string.Equals(url, _comfyFindingsUrl, StringComparison.Ordinal))
        {
            _comfyFindings.Clear();
            _comfyFindingsUrl = url;
        }
    }

    /// <summary>
    /// The server's answer for each class (4 at a time, each read once a check), filled into <see cref="_comfyInfo"/>; false when cancelled or the
    /// server can't be reached (said), with what was read kept.
    /// </summary>
    private async Task<bool> ComfyReadClassesAsync(ComfyClient client, IReadOnlyList<string> classes, CancellationToken cancellationToken)
    {
        var wanted = classes.Where(c => !_comfyInfo.ContainsKey(c)).ToList();
        if (wanted.Count == 0)
        {
            return true;
        }

        string? failure = null;
        var (done, _) = await ComfyBusyAsync(ComfyCheckingLabel, async token =>
        {
            using var gate = new SemaphoreSlim(ComfyCheck.Parallel);
            var reads = wanted.Select(async nodeClass =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var (info, error) = await client.ObjectInfoAsync(nodeClass, token).ConfigureAwait(false);
                    lock (_comfyInfo)
                    {
                        if (info is not null)
                        {
                            _comfyInfo[nodeClass] = info;
                        }
                        else
                        {
                            failure ??= error;
                        }
                    }
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();
            await Task.WhenAll(reads).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        if (failure is not null && !_comfyInfo.Values.Any(v => v is not null))
        {
            Sink.Error(failure);   // unreachable: nothing marked
            return false;
        }

        return done;
    }

    /// <summary>A workflow's graph as an object (comments and trailing commas allowed, as the catalog reads it), or null.</summary>
    private static JsonObject? ComfyGraphOf(ComfyWorkflow workflow)
    {
        try
        {
            return JsonNode.Parse(workflow.GraphJson, documentOptions: ComfyWorkflow.Tolerant) as JsonObject;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The list's <c>c</c>: every workflow checked; the marks and the notice say what was found. Esc keeps what it found so far.</summary>
    private async Task ComfyCheckAllAsync(IReadOnlyList<ComfyWorkflow> workflows, CancellationToken cancellationToken)
    {
        if (_comfyClient() is not { } client)
        {
            Sink.Error(ComfyText.NoServer);
            return;
        }

        ComfyFindingsForUrl();
        _comfyInfo.Clear();
        var graphs = workflows.Select(w => (Workflow: w, Graph: ComfyGraphOf(w))).Where(p => p.Graph is not null).ToList();
        var classes = graphs.SelectMany(p => ComfyCheck.Classes(p.Graph!)).Distinct(StringComparer.Ordinal).ToList();
        bool done = await ComfyReadClassesAsync(client, classes, cancellationToken).ConfigureAwait(false);
        if (!done && !_comfyInfo.Values.Any(v => v is not null))
        {
            if (cancellationToken.IsCancellationRequested || _comfyInfo.Count == 0)
            {
                Sink.Notice(ComfyCheckCancelledNotice);
            }

            return;
        }

        int marked = 0;
        int findings = 0;
        foreach (var (workflow, graph) in graphs)
        {
            // A workflow only part of whose classes were read (a cancel) is held to those.
            var found = ComfyCheck.Findings(graph!, _comfyInfo);
            _comfyFindings[workflow.Name] = found;
            marked += found.Count > 0 ? 1 : 0;
            findings += found.Count;
        }

        DiagnosticLog.Info(ComfyWorkflowFile.Category, ComfyText.LogChecked(graphs.Count, findings));
        Sink.Notice(!done ? ComfyCheckCancelledNotice : marked == 0 ? ComfyCheckCleanNotice : ComfyCheckFoundNotice(marked));
    }

    /// <summary>The actions page's Check: one workflow checked, every finding a row of a plain page.</summary>
    private async Task ComfyCheckOneAsync(ComfyWorkflow workflow, MenuPage from, CancellationToken cancellationToken)
    {
        if (_comfyClient() is not { } client)
        {
            Sink.Error(ComfyText.NoServer);
            return;
        }

        if (ComfyGraphOf(workflow) is not { } graph)
        {
            Sink.Error(ComfyWorkflow.NotJsonProblem);
            return;
        }

        ComfyFindingsForUrl();
        _comfyInfo.Clear();
        if (!await ComfyReadClassesAsync(client, ComfyCheck.Classes(graph), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(ComfyCheckCancelledNotice);
            return;
        }

        var found = ComfyCheck.Findings(graph, _comfyInfo);
        _comfyFindings[workflow.Name] = found;
        DiagnosticLog.Info(ComfyWorkflowFile.Category, ComfyText.LogChecked(1, found.Count));
        if (found.Count == 0)
        {
            Sink.Notice(ComfyCheckOneCleanNotice);
            return;
        }

        var page = new MenuPage(from.Title + " › " + ComfyCheckAction, found.Select(f => Markup.Escape(UntrustedText.OneLine(f))).ToList(), PickKeys);
        await PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
    }
}
