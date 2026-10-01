using NeonSidekick.Diagnostics;

namespace NeonSidekick.Comfy;

/// <summary>A workflow file the catalog skipped, and why (<see cref="ComfyWorkflow.TryLoad"/>'s sentence).</summary>
public sealed record ComfyWorkflowProblem(string FilePath, string Problem);

/// <summary>
/// The ComfyUI workflows the image tools can run (2026-09-24): every <c>*.json</c> in the <c>comfy</c> folders — the
/// loaded profile's first, then the home's — a profile file shadowing a global one of the same name, the
/// <see cref="Skills.SkillCatalog"/> shape. Rescanned at every call (<see cref="Scan"/>) so a file dropped in, edited
/// or removed shows at the next turn without a restart; a file is parsed again only when it or its sidecar changed.
/// A missing folder is no workflow; an unreadable one is a warning and no workflow. Sorted by name.
/// </summary>
public sealed class ComfyWorkflowCatalog
{
    private const string Category = "Comfy";

    /// <summary>The folder's name under a profile and under the home.</summary>
    public const string DirectoryName = "comfy";

    private readonly Func<IReadOnlyList<string>> _roots;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Json, DateTime Sidecar, ComfyWorkflow? Workflow, string? Problem)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="roots">The folders to read, first wins: the profile's <c>comfy</c>, then the home's — asked afresh at each scan, so a profile switch needs no rebuild.</param>
    public ComfyWorkflowCatalog(Func<IReadOnlyList<string>> roots)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
    }

    /// <summary>The folders as they stand now, first wins.</summary>
    public IReadOnlyList<string> Roots => _roots();

    /// <summary>The workflows found and the files skipped, as of now.</summary>
    public (IReadOnlyList<ComfyWorkflow> Workflows, IReadOnlyList<ComfyWorkflowProblem> Problems) Scan()
    {
        var workflows = new Dictionary<string, ComfyWorkflow>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<ComfyWorkflowProblem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (string root in _roots())
            {
                foreach (string file in Files(root))
                {
                    seen.Add(file);
                    var (workflow, problem) = Load(file);
                    if (workflow is not null)
                    {
                        workflows.TryAdd(workflow.Name, workflow);
                    }
                    else if (problem is not null)
                    {
                        problems.Add(new ComfyWorkflowProblem(file, problem));
                    }
                }
            }

            foreach (string gone in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _cache.Remove(gone);
            }
        }

        return (workflows.Values.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList(), problems);
    }

    /// <summary>
    /// <paramref name="workflows"/> narrowed to <paramref name="names"/> (later on 2026-09-24, the SQL connections'
    /// shape, <c>SqlCatalog.Offered</c>): exactly those names, any case — a workflow added later stays out until ticked, a
    /// name no longer installed is ignored. Null offers none, as an empty list does (2026-10-01, the user's call; it was
    /// "not narrowed", every one, until then). Pure.
    /// </summary>
    public static IReadOnlyList<ComfyWorkflow> Offered(IReadOnlyList<ComfyWorkflow> workflows, IReadOnlyList<string>? names)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        var wanted = new HashSet<string>((names ?? []).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        return workflows.Where(w => wanted.Contains(w.Name)).ToList();
    }

    /// <summary>The workflows alone.</summary>
    public IReadOnlyList<ComfyWorkflow> Workflows => Scan().Workflows;

    private (ComfyWorkflow? Workflow, string? Problem) Load(string file)
    {
        DateTime json, sidecar;
        try
        {
            json = File.GetLastWriteTimeUtc(file);
            string md = Path.ChangeExtension(file, ".md");
            sidecar = File.Exists(md) ? File.GetLastWriteTimeUtc(md) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }

        if (_cache.TryGetValue(file, out var cached) && cached.Json == json && cached.Sidecar == sidecar)
        {
            return (cached.Workflow, cached.Problem);
        }

        ComfyWorkflow.TryLoad(file, out var workflow, out string? problem);
        if (problem is not null)
        {
            DiagnosticLog.Warn(Category, ComfyText.SkippedWorkflow(file, problem));
        }

        _cache[file] = (json, sidecar, workflow, problem);
        return (workflow, problem);
    }

    private static IReadOnlyList<string> Files(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return [];
            }

            return Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, ComfyText.UnreadableFolder(root, ex.Message));
            return [];
        }
    }
}
