using System.Globalization;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;

namespace NeonSidekick.Docker;

/// <summary>
/// What one <c>/docker</c> line came to: the lines to print, whether it failed (an error's line), and whether the screen shows them
/// on the info pane (<paramref name="Paned"/>: <c>logs</c>, 2026-10-04); headless prints them either way.
/// </summary>
public sealed record DockerCommandResult(IReadOnlyList<string> Lines, bool Failed, bool Paned = false)
{
    public static DockerCommandResult Error(string line) => new([line], true);

    public static DockerCommandResult Of(string text) => new(text.Split('\n'), text.StartsWith("Error:", StringComparison.Ordinal));
}

/// <summary>
/// <c>/docker</c>'s typed forms (2026-10-02, the <see cref="HomeAssistant.HaCommand"/> shape): Docker driven without the
/// model — the user's own hand, so <c>Docker writes</c> never judges it, though every change is audited. One engine for the
/// screen and headless; the bare word opens the pane on the screen (<see cref="App.DockerMenu"/>) and lists here.
/// <list type="bullet">
/// <item><c>/docker</c>, <c>/docker ps</c> — the containers;</item>
/// <item><c>/docker status</c> — Docker Desktop, the engine, the counts;</item>
/// <item><c>/docker logs &lt;container&gt; [lines]</c> — the last lines (<see cref="DefaultLogLines"/>);</item>
/// <item><c>/docker stats [container]</c>;</item>
/// <item><c>/docker start|stop|restart|pause|unpause &lt;container&gt;</c>.</item>
/// </list>
/// Its argument list (<see cref="Complete"/>) reads the last list read and never the engine.
/// </summary>
public static class DockerCommand
{
    /// <summary>How many log lines <c>/docker logs</c> shows when no count is given.</summary>
    public const int DefaultLogLines = 50;

    /// <summary>Runs one <c>/docker</c> line (<paramref name="args"/>, the text after the word).</summary>
    public static async Task<DockerCommandResult> RunAsync(DockerSession docker, string args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(args);
        if (!OperatingSystem.IsWindows())
        {
            return DockerCommandResult.Error(DockerText.NotWindows);
        }

        string text = args.Trim();
        string verb = text.Split(' ', 2)[0].ToLowerInvariant();
        string rest = text.Length > verb.Length ? text[verb.Length..].Trim() : "";
        switch (verb)
        {
            case "":
            case "ps":
            {
                var (containers, error) = await docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
                return containers is null ? DockerCommandResult.Error(error!) : DockerCommandResult.Of(DockerContainersTool.List(containers, all: true, rest.Length > 0 ? rest : null, null));
            }

            case "status":
                return await StatusAsync(docker, cancellationToken).ConfigureAwait(false);

            case "logs":
            {
                var (target, count) = SplitCount(rest);
                if (target.Length == 0 || count is < 1 or > DockerLogsTool.MaxTail)
                {
                    return DockerCommandResult.Error(DockerText.Usage);
                }

                var (container, error) = await ResolveAsync(docker, target, cancellationToken).ConfigureAwait(false);
                if (container is null)
                {
                    return DockerCommandResult.Error(error!);
                }

                int tail = count ?? DefaultLogLines;
                var logs = await docker.Client().LogsAsync(container.Id, tail, null, timestamps: false, DockerLogStreams.Both, DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
                return logs.Error is not null ? DockerCommandResult.Error(logs.Error) : DockerCommandResult.Of(DockerLogsTool.Render(container.Name, logs.Lines, tail, null, logs.Capped)) with { Paned = true };
            }

            case "stats":
            {
                IReadOnlyList<DockerContainer> targets;
                if (rest.Length > 0)
                {
                    var (container, error) = await ResolveAsync(docker, rest, cancellationToken).ConfigureAwait(false);
                    if (container is null)
                    {
                        return DockerCommandResult.Error(error!);
                    }

                    if (!container.Running)
                    {
                        return DockerCommandResult.Of(DockerText.NotRunning(container));
                    }

                    targets = [container];
                }
                else
                {
                    var (all, error) = await docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
                    if (all is null)
                    {
                        return DockerCommandResult.Error(error!);
                    }

                    targets = DockerText.Ordered(all.Where(c => c.Running));
                }

                var lines = await DockerStatsTool.SampleAsync(docker, targets, cancellationToken).ConfigureAwait(false);
                return new DockerCommandResult([DockerText.StatsHeader(targets.Count), .. lines], false);
            }

            default:
                if (DockerText.ActionVerbs.Contains(verb))
                {
                    if (rest.Length == 0)
                    {
                        return DockerCommandResult.Error(DockerText.Usage);
                    }

                    var (container, error) = await ResolveAsync(docker, rest, cancellationToken).ConfigureAwait(false);
                    if (container is null)
                    {
                        return DockerCommandResult.Error(error!);
                    }

                    return DockerCommandResult.Of(await docker.ActAsync(verb, [container], null, DockerText.ByUser, cancellationToken).ConfigureAwait(false));
                }

                return DockerCommandResult.Error(DockerText.Usage);
        }
    }

    /// <summary><c>/docker status</c>: the version (and the agreed API), the counts and the engine's machine.</summary>
    public static async Task<DockerCommandResult> StatusAsync(DockerSession docker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(docker);
        var client = docker.Client();
        var (version, error) = await client.VersionAsync(DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (version is null || error is not null)
        {
            return DockerCommandResult.Error(error ?? DockerText.BadAnswer("/version"));
        }

        var info = await client.GetAsync("/info", DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
        var lines = DockerText.Status(client.Display, version, info.Ok ? DockerJson.Info(info.Body) : null, client.Prefix ?? "");
        return new DockerCommandResult(lines, false);
    }

    /// <summary>The one container a name means, over a fresh list.</summary>
    private static async Task<(DockerContainer? Container, string? Error)> ResolveAsync(DockerSession docker, string target, CancellationToken cancellationToken)
    {
        var (all, error) = await docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        if (all is null)
        {
            return (null, error);
        }

        var match = DockerTargets.Resolve(all, target);
        return match.Error is not null ? (null, match.Error) : (match.Containers[0], null);
    }

    /// <summary>A name and its trailing count — <c>mysql_dev 200</c> — the count null when there is none. Pure.</summary>
    public static (string Target, int? Count) SplitCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        int space = trimmed.LastIndexOf(' ');
        if (space > 0 && int.TryParse(trimmed[(space + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            return (trimmed[..space].Trim(), count);
        }

        return (trimmed, null);
    }

    /// <summary>
    /// <c>/docker</c>'s argument list over <paramref name="last"/> (the last list read; null = the verbs alone): the verbs, then
    /// after a verb that takes one the containers' names — running ones after <c>stop</c>, <c>restart</c>, <c>pause</c> and
    /// <c>stats</c>, stopped ones after <c>start</c>, paused ones after <c>unpause</c>. Each item is the whole argument. Pure.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(IReadOnlyList<DockerContainer>? last, string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int space = argText.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            return MentionCompleter.Matches(DockerText.Verbs.Select(v => new CompletionItem(v, DockerText.VerbNote(v))).ToList(), argText);
        }

        string verb = argText[..space].ToLowerInvariant();
        if (last is null || argText[(space + 1)..].Contains(' ', StringComparison.Ordinal))
        {
            return [];
        }

        string head = argText[..(space + 1)];
        IEnumerable<DockerContainer> pool = verb switch
        {
            "stop" or "restart" or "pause" or "stats" => last.Where(c => c.Running),
            "start" => last.Where(c => !c.Running && !c.Paused),
            "unpause" => last.Where(c => c.Paused),
            "logs" => last,
            _ => [],
        };
        return MentionCompleter.Matches(DockerText.Ordered(pool).Select(c => new CompletionItem(head + c.Name, c.State + " · " + c.Image)).ToList(), argText);
    }
}
