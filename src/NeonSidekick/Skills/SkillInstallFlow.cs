using NeonSidekick.Web;

namespace NeonSidekick.Skills;

/// <summary>
/// What <see cref="SkillInstallFlow"/> runs on: the screen's transcript, spinner and panes, or
/// headless's lines. <see cref="Headless"/> changes two steps — several hits are listed, not
/// picked, and nothing is written without <c>--yes</c>.
/// </summary>
public interface ISkillInstallHost
{
    bool Headless { get; }

    SkillRoots Roots { get; }

    /// <summary>Whether the catalog reads the external root: <c>Agent skills</c> and <c>Use external skills</c> both on.</summary>
    bool External { get; }

    /// <summary>Whether <c>Agent skills</c> is on.</summary>
    bool SkillsEnabled { get; }

    /// <summary>The fetch options of the moment (the network mode).</summary>
    FetchOptions FetchOptions { get; }

    TimeProvider Time { get; }

    void Notice(string text);

    void Warning(string text);

    void Error(string text);

    /// <summary>The preview, as Markdown (sanitised already).</summary>
    void Preview(string markdown);

    Task<T> SpinAsync<T>(string label, Func<Task<T>> work);

    /// <summary>One row of <paramref name="rows"/> (plain text), or null for none. Never called headless.</summary>
    Task<int?> PickAsync(string title, IReadOnlyList<string> rows, CancellationToken cancellationToken);

    /// <summary>The scope to install to, or null to keep it out. Never called headless.</summary>
    Task<SkillScope?> ConfirmAsync(SkillCandidate candidate, SkillSource source, SkillInstallCheck check, SkillScope? preselect, CancellationToken cancellationToken);

    /// <summary>A rescan of the catalog, so the next turn offers the skill.</summary>
    void Rescan();
}

/// <summary>
/// <c>/skills add &lt;query | owner/repo[/skill] | github url | zip url&gt; [--global | --profile] [--yes]</c>
/// (2026-09-26, the user's ask: search and download Agent Skills into the app, preview and confirm
/// before anything is written). The steps: the argument read (<see cref="SkillSource"/>); words
/// searched on skills.sh — none is an error, one goes straight on, several are a pick (headless:
/// listed as ids to type back); the archive downloaded under a spinner (<see cref="SkillHub"/>); its
/// skills found (<see cref="SkillArchive"/>) and the one named or picked; the preview printed; the
/// collision rule (<see cref="SkillInstaller.Check"/>); the scope asked on a pane with the cursor on
/// Cancel (headless: <c>--yes</c> is the consent, <c>--global</c> the scope, the profile's otherwise);
/// the folder installed and the catalog rescanned. Shared by the screen and headless so both say
/// the same; tested over a fake host.
/// </summary>
public sealed class SkillInstallFlow
{
    private readonly SkillHub _hub;

    public SkillInstallFlow(SkillHub hub)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    }

    /// <summary>Runs <c>/skills add</c> with <paramref name="args"/>, the words after <c>add</c>. True when a skill was installed.</summary>
    public async Task<bool> RunAsync(string args, ISkillInstallHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        var (rest, scopeFlag, yes) = SkillSource.SplitFlags(args);
        if (!SkillSource.TryParse(rest, out var source, out string? error))
        {
            host.Error(error!);
            return false;
        }

        string? skillsShId = null;
        if (source!.Kind == SkillSourceKind.Search)
        {
            string query = source.Query!;
            var found = await host.SpinAsync(SkillInstallText.SearchingLabel(query), () => _hub.SearchAsync(query, host.FetchOptions, cancellationToken)).ConfigureAwait(false);
            if (!found.Ok)
            {
                host.Error(found.Error!);
                return false;
            }

            if (found.Hits.Count == 0)
            {
                host.Error(SkillInstallText.NoResultsError(query));
                return false;
            }

            var hit = found.Hits[0];
            if (found.Hits.Count > 1)
            {
                if (host.Headless)
                {
                    host.Notice(SkillInstallText.HeadlessPickHint);
                    foreach (string line in SkillInstallText.HeadlessHitLines(found.Hits))
                    {
                        host.Notice(line);
                    }

                    return false;
                }

                if (await host.PickAsync(SkillInstallText.PickHitTitle, SkillInstallText.HitRows(found.Hits), cancellationToken).ConfigureAwait(false) is not { } row)
                {
                    host.Notice(SkillInstallText.CancelledNotice);
                    return false;
                }

                hit = found.Hits[row];
            }

            skillsShId = hit.Id;
            SkillSource.TryParse(hit.Id, out source, out _);
        }

        var (archive, failed) = await host.SpinAsync(SkillInstallText.DownloadingLabel(source!.Label), () => _hub.DownloadAsync(source, host.FetchOptions, cancellationToken)).ConfigureAwait(false);
        if (archive is null)
        {
            host.Error(failed!);
            return false;
        }

        using (archive)
        {
            var candidates = archive.Candidates(source.SubPath);
            if (candidates.Count == 0)
            {
                host.Error(SkillInstallText.NoSkillsError(source.Label + (source.SubPath is null ? "" : "/" + source.SubPath)));
                return false;
            }

            SkillCandidate candidate;
            if (source.SkillId is { } id)
            {
                if (SkillArchive.Match(candidates, id) is not { } matched)
                {
                    host.Error(SkillInstallText.NoSuchSkillError(source.RepoName + "/" + id, candidates.Select(c => c.Name)));
                    return false;
                }

                candidate = matched;
            }
            else if (candidates.Count == 1)
            {
                candidate = candidates[0];
            }
            else if (host.Headless)
            {
                host.Notice(SkillInstallText.HeadlessPickHint);
                foreach (string line in SkillInstallText.HeadlessCandidateLines(source, candidates))
                {
                    host.Notice(line);
                }

                return false;
            }
            else if (await host.PickAsync(SkillInstallText.PickSkillTitle, SkillInstallText.CandidateRows(candidates), cancellationToken).ConfigureAwait(false) is { } row)
            {
                candidate = candidates[row];
            }
            else
            {
                host.Notice(SkillInstallText.CancelledNotice);
                return false;
            }

            host.Preview(SkillInstallText.PreviewMarkdown(candidate, source, archive.Commit));
            if (candidate.Refusal is { } refusal)
            {
                host.Error(SkillInstallText.CannotInstallError(candidate.Name, refusal));
                return false;
            }

            var provenance = new SkillProvenance
            {
                Source = source.Kind == SkillSourceKind.Zip ? SkillProvenance.ZipKind : SkillProvenance.GitHubKind,
                Repo = source.RepoName,
                Ref = source.Kind == SkillSourceKind.Zip ? "" : source.Ref,
                Commit = archive.Commit,
                Path = candidate.Folder,
                Url = source.ArchiveUrl!.AbsoluteUri,
                SkillsShId = skillsShId,
                InstalledAt = host.Time.GetUtcNow(),
                Files = candidate.Files.Count,
                Bytes = candidate.Bytes,
            };

            var roots = host.Roots;
            var check = SkillInstaller.Check(roots, candidate.Name, provenance, host.External);
            switch (check.Option)
            {
                case SkillInstallOption.Taken:
                    host.Error(SkillInstallText.TakenError(candidate.Name, check.Scope!.Value));
                    return false;
                case SkillInstallOption.ExternalReadOnly:
                    host.Error(SkillInstallText.ExternalReadOnlyError(candidate.Name));
                    return false;
            }

            SkillScope scope;
            if (host.Headless)
            {
                if (!yes)
                {
                    host.Notice(SkillInstallText.HeadlessNeedsYes(candidate.Name));
                    return false;
                }

                scope = check.Scope ?? scopeFlag ?? SkillScope.Profile;
            }
            else if (await host.ConfirmAsync(candidate, source, check, scopeFlag, cancellationToken).ConfigureAwait(false) is { } picked)
            {
                scope = picked;
            }
            else
            {
                host.Notice(SkillInstallText.KeptNotice(candidate.Name));
                return false;
            }

            var result = SkillInstaller.Install(roots, scope, archive, candidate, provenance, replace: check.Option == SkillInstallOption.Update);
            if (!result.Ok)
            {
                host.Error(result.Error!);
                return false;
            }

            host.Rescan();
            host.Notice(result.Updated
                ? SkillInstallText.UpdatedNotice(candidate.Name, result.Scope, result.Directory)
                : SkillInstallText.InstalledNotice(candidate.Name, result.Scope, result.Directory));
            if (!host.SkillsEnabled)
            {
                host.Warning(SkillInstallText.SkillsOffWarning);
            }

            return true;
        }
    }
}
