using System.Globalization;
using NeonSidekick.Docker;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The Docker tab of /settings: Docker servers (2026-10-02) ──────────────

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// The engine's containers for <c>Docker server containers</c> (2026-10-02): the screen's Docker door
    /// (<see cref="DockerSession.ContainersAsync"/>); null where there is none, and the checklist then says so.
    /// </summary>
    public Func<CancellationToken, Task<(IReadOnlyList<DockerContainer>? Containers, string? Error)>>? DockerContainers { get; set; }

    /// <summary>The Docker tab's strip title (2026-10-02, after Embedded, the user's place). Pinned.</summary>
    public const string DockerTabTitle = "Docker";

    /// <summary>The checklist's refusal where no engine door was handed in (a session without one). Pinned.</summary>
    public const string NoDockerEngineError = "There is no Docker engine to ask here.";

    /// <summary>The checklist's notice when the engine lists no container at all. Pinned.</summary>
    public const string NoDockerContainersNotice = "(the Docker engine lists no containers)";

    public static readonly string DockerServerStopTimeoutRangeError =
        "must be " + AppSettingsData.MinDockerServerStopTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxDockerServerStopTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    public static readonly string DockerServerPostStopDelayRangeError =
        "must be 0 to " + AppSettingsData.MaxDockerServerPostStopDelaySeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    public static readonly string DockerServerReadyTimeoutRangeError =
        "must be " + AppSettingsData.MinDockerServerReadyTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxDockerServerReadyTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";

    /// <summary>The value of <c>Docker server containers</c>: <c>none</c>, or how many and their names, cut short; asked of no engine. Pinned.</summary>
    public static string DockerServerContainersValue(IReadOnlyList<string>? chosen)
    {
        var names = (chosen ?? []).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return names.Count == 0 ? "none" : DockerText.Clip(names.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", names), 70);
    }

    /// <summary>One row of the checklist: the mark, the name padded, then its state, image and ports dim. Pinned.</summary>
    public static string DockerServerRow(DockerContainer container, bool chosen, int width)
    {
        ArgumentNullException.ThrowIfNull(container);
        return Markup.Escape((chosen ? "[x] " : "[ ] ") + container.Name.PadRight(width)) + Theme.DimMarkup(Markup.Escape(DockerServerText.RowDetail(container)));
    }

    /// <summary>
    /// <c>Docker server containers</c> (2026-10-02, <see cref="EditUncOfferedAsync"/>'s twin): every container the engine lists,
    /// running or not, read under a spinner, ticked or not; Enter or Space flips one until ESC, the buttons tick all or none.
    /// The order kept is the engine's list order. A ticked name the engine no longer lists is dropped as the list opens (2026-10-04,
    /// the user's call; it was kept until then, for an image being rebuilt, and nothing could untick it): only once the engine has
    /// answered with at least one container.
    /// Not a reconnect: a container ticked or unticked here counts at the next <c>/server</c> or connect, so the one in use
    /// never stops as a side effect of the list. True when anything changed.
    /// </summary>
    private async Task<bool> EditDockerServerContainersAsync(CancellationToken cancellationToken)
    {
        if (DockerContainers is not { } read)
        {
            Sink.Error(NoDockerEngineError);
            return false;
        }

        var (listed, error) = await _transcript.WithSpinnerAsync(DockerText.Working, () => read(cancellationToken)).ConfigureAwait(false);
        if (listed is null)
        {
            Sink.Error(error ?? NoDockerEngineError);
            return false;
        }

        if (listed.Count == 0)
        {
            Sink.Notice(NoDockerContainersNotice);
            return false;
        }

        var containers = DockerText.Ordered(listed);
        var before = _settings.Current.DockerServerContainers;
        bool changed = PruneStale(SettingsField.DockerServerContainers, before, OfferedNames.Stale(before, containers.Select(c => c.Name), [], StringComparer.Ordinal), StringComparer.Ordinal, (d, kept) => d.DockerServerContainers = kept);
        int cursor = 0;
        int width = containers.Max(c => c.Name.Length) + 2;
        while (true)
        {
            var saved = _settings.Current.DockerServerContainers;
            var on = (saved ?? []).Select(n => n.Trim()).ToHashSet(StringComparer.Ordinal);
            var page = new MenuPage(Crumb(FieldName(SettingsField.DockerServerContainers)), containers.Select(c => DockerServerRow(c, on.Contains(c.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, containers.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = containers[pick.Row].Name;
            var next = pick.Button == SelectAllIndex ? containers.Select(c => c.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : containers.Select(c => c.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.Ordinal)).ToList();
            if (next.Count == containers.Count(c => on.Contains(c.Name)) && next.All(on.Contains))
            {
                continue;
            }

            Apply(SettingsField.DockerServerContainers, d => d.DockerServerContainers = next);
            changed = true;
        }
    }
}
