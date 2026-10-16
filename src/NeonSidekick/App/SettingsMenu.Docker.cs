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

    /// <summary>
    /// The Docker tools page's manage button (2026-10-11, the user's ask): the bare <c>/docker</c>'s container list, ESC there back to
    /// the page; null where the screen hands none in, and the page has no button.
    /// </summary>
    public Func<CancellationToken, Task>? ManageDocker { get; set; }

    /// <summary>The Docker tools page's button (2026-10-11): the container list. Pinned.</summary>
    public const string DockerManageButtonTitle = "⊞ manage";

    /// <summary>The key that is <see cref="DockerManageButtonTitle"/>.</summary>
    public const char DockerManageKey = 'm';

    /// <summary>The Docker tools page's buttons: <see cref="DockerManageButtonTitle"/> alone. Pinned.</summary>
    public static readonly IReadOnlyList<MenuButton> DockerToolsButtons = [new(DockerManageButtonTitle, DockerManageKey)];

    /// <summary>The Docker tools page's hint: <see cref="PickKeys"/> with the manage button's key. Pinned.</summary>
    public const string DockerToolsKeys = "Enter = choose · m = manage · Esc = back";

    /// <summary>The Docker tab's strip title (2026-10-02, after Embedded, the user's place). Pinned.</summary>
    public const string DockerTabTitle = "Docker";

    /// <summary>
    /// The <c>DockerEnginePipe</c> row's label on a Mac (2026-10-11): the same field holds the engine's Unix socket there, blank
    /// (or Windows' pipe name) to find it. On the <c>/tools</c> Docker tab. Pinned.
    /// </summary>
    public const string DockerEngineSocketLabel = "Docker engine socket";

    /// <summary>
    /// The refusal of a typed <see cref="DockerEngineSocketLabel"/> that names no socket (code review of 9c145d1, 2026-10-11: it was
    /// saved blank, "find it", without a word). Follows the row's name. Pinned.
    /// </summary>
    public const string DockerEngineSocketError = "must be a full path (/var/run/docker.sock, ~/.orbstack/run/docker.sock) or unix:///path, or empty to find it";

    /// <summary>The checklist's refusal where no engine door was handed in (a session without one). Pinned.</summary>
    public const string NoDockerEngineError = "There is no Docker engine to ask here.";

    /// <summary>The checklist's notice when the engine lists no container at all. Pinned.</summary>
    public const string NoDockerContainersNotice = "(the Docker engine lists no containers)";

    /// <summary>The checklist's notice when the engine lists containers but none is an LLM server by the words and none is ticked (2026-10-10). Pinned.</summary>
    public const string NoLlmContainersNotice = "(no container matches Docker server match words; empty the list to see every container)";

    /// <summary>The match words' first row, always there (2026-10-10): Enter on it opens the slot to type one into. Pinned.</summary>
    public const string AddDockerWordRow = "+ Add a word…";

    /// <summary>What the match words' description band says under a word (2026-10-10). Pinned.</summary>
    public const string DockerWordNote = "a container whose image or command holds this is listed as a server";

    /// <summary>The yes/no before a match word is removed (2026-10-10, every list's remove asks). Pinned.</summary>
    public static string RemoveDockerWordQuestion(string word) => $"Remove \"{word}\" from the match words? Containers only it matched are no longer listed.";

    /// <summary>The notice after a word is added: <c>Docker server match words: tgi added</c>. Pinned.</summary>
    public static string DockerWordAddedNotice(string word) => FieldName(SettingsField.DockerServerMatchWords) + ": " + word + " added";

    /// <summary>The notice when the typed word is in the list already (case ignored, as the match is): nothing saved. Pinned.</summary>
    public static string DockerWordDuplicateNotice(string word) => FieldName(SettingsField.DockerServerMatchWords) + ": " + word + " is already in the list";

    /// <summary>The notice after a word is removed: <c>Docker server match words: ollama removed</c>. Pinned.</summary>
    public static string DockerWordRemovedNotice(string word) => FieldName(SettingsField.DockerServerMatchWords) + ": " + word + " removed";

    /// <summary>The yes/no before the restore button replaces the words (2026-10-10): an empty list is filled, a list with words is replaced and the question says what goes. Pinned.</summary>
    public static string RestoreDockerWordsQuestion(int count, int defaults) => count == 0
        ? $"Restore the {defaults} default match words?"
        : $"Replace the list ({Words(count)}) with the {defaults} default words? Any you added are removed.";

    /// <summary>The notice after the defaults are back: <c>Docker server match words: 12 defaults restored</c>. Pinned.</summary>
    public static string DockerWordsRestoredNotice(int defaults) => FieldName(SettingsField.DockerServerMatchWords) + ": " + defaults.ToString(CultureInfo.InvariantCulture) + " defaults restored";

    /// <summary>The notice when the list already is the defaults (case ignored): nothing asked, nothing saved. Pinned.</summary>
    public static string DockerWordsAlreadyDefaultsNotice => FieldName(SettingsField.DockerServerMatchWords) + ": already the defaults";

    /// <summary>The value of <c>Docker server match words</c> (2026-10-10): how many, or <c>none (every container)</c> for an empty list. Pinned.</summary>
    public static string DockerServerWordsValue(IReadOnlyList<string>? words)
    {
        int count = DockerServerWords.Sorted(words ?? []).Count;
        return count == 0 ? "none (every container)" : Words(count);
    }

    /// <summary><c>1 word</c>, <c>12 words</c>.</summary>
    private static string Words(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " word" : " words");

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
    /// <c>Docker server containers</c> (2026-10-02, <see cref="EditUncOfferedAsync"/>'s twin): the containers the engine lists,
    /// running or not, read under a spinner, ticked or not; Enter or Space flips one until ESC, the buttons tick all or none.
    /// The order kept is the engine's list order. A ticked name the engine no longer lists is dropped as the list opens (2026-10-04,
    /// the user's call; it was kept until then, for an image being rebuilt, and nothing could untick it): only once the engine has
    /// answered with at least one container.
    /// Only the LLM servers are shown since 2026-10-10 (the user's ask: the databases buried them): a container
    /// <see cref="DockerServerWords.Matches"/> by <see cref="AppSettingsData.DockerServerMatchWords"/>, plus any already ticked so it
    /// can still be unticked; an empty word list shows every container. A hidden container is not stale (the prune reads the
    /// whole list), the buttons and a flip touch the shown rows only, and a ticked name not shown stays ticked.
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

        var all = DockerText.Ordered(listed);
        var before = _settings.Current.DockerServerContainers;
        bool changed = PruneStale(SettingsField.DockerServerContainers, before, OfferedNames.Stale(before, all.Select(c => c.Name), [], StringComparer.Ordinal), StringComparer.Ordinal, (d, kept) => d.DockerServerContainers = kept);
        var containers = DockerServerWords.Servers(all, _settings.Current.DockerServerMatchWords, _settings.Current.DockerServerContainers);
        if (containers.Count == 0)
        {
            Sink.Notice(NoLlmContainersNotice);
            return changed;
        }

        var shown = containers.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
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
            // A shown row takes the press; a hidden one keeps its tick.
            var next = all.Select(c => c.Name).Where(n => !shown.Contains(n) ? on.Contains(n)
                : pick.Button == SelectAllIndex || (pick.Button != SelectNoneIndex && on.Contains(n) != string.Equals(n, name, StringComparison.Ordinal))).ToList();
            if (next.Count == all.Count(c => on.Contains(c.Name)) && next.All(on.Contains))
            {
                continue;
            }

            Apply(SettingsField.DockerServerContainers, d => d.DockerServerContainers = next);
            changed = true;
        }
    }

    /// <summary>
    /// The <c>Docker server match words</c> list (2026-10-10, the user's ask), <see cref="EditForbiddenStringsAsync"/>'s twin:
    /// <see cref="AddDockerWordRow"/> on top, then each word (<see cref="DockerServerWords.Sorted"/>). Enter on the top row opens the
    /// input slot under it (without the pane, the prompt line) and a typed word is saved at once (<see cref="DockerWordAddedNotice"/>,
    /// or <see cref="DockerWordDuplicateNotice"/> and nothing saved); Enter on a word removes it after a yes. On the pane the title
    /// row carries the forbidden list's restore button: the known engines back after a yes, or
    /// <see cref="DockerWordsAlreadyDefaultsNotice"/> and nothing asked when the list already is them. True when anything changed.
    /// </summary>
    private async Task<bool> EditDockerServerWordsAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        string title = FieldName(SettingsField.DockerServerMatchWords);
        while (true)
        {
            var words = DockerServerWords.Sorted(_settings.Current.DockerServerMatchWords);
            IReadOnlyList<string> rows = [Markup.Escape(AddDockerWordRow), .. words.Select(Markup.Escape)];
            var page = new MenuPage(Crumb(title), rows, _pane.Enabled ? ForbiddenPaneKeys : ForbiddenKeys) { Footer = (_, row) => row > 0 && row <= words.Count ? new MenuFooter(words[row - 1], DockerWordNote) : null };
            if (await PickChecklistAsync(page, Math.Min(cursor, rows.Count - 1), cancellationToken, ForbiddenButtons).ConfigureAwait(false) is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            if (pick.Button >= 0)
            {
                changed |= await RestoreDockerWordsAsync(words, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (pick.Row > 0)
            {
                string word = words[pick.Row - 1];
                var removing = new MenuPage(RemoveDockerWordQuestion(word), ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
                if (await PickAsync(removing, 0, cancellationToken).ConfigureAwait(false) != 1)
                {
                    Sink.Notice(ChatScreen.KeptNotice);
                    continue;
                }

                _settings.Update(d => d.DockerServerMatchWords = DockerServerWords.Without(d.DockerServerMatchWords, word));
                Sink.Notice(DockerWordRemovedNotice(word));
                changed = true;
                continue;
            }

            InputResult result;
            if (_pane.Enabled)
            {
                result = await _pane.EditAsync(page with { Hint = EditKeys }, 0, _input, "", allowEmpty: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(title, EditKeys));
                result = await _input.ReadAsync("", remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
            }

            string typed = result is InputResult.Submitted submitted ? DockerServerWords.Normalize(submitted.Text) : "";
            if (typed.Length == 0)
            {
                continue;   // ESC or a blank line: back on the list, nothing saved
            }

            if (DockerServerWords.Contains(_settings.Current.DockerServerMatchWords, typed))
            {
                Sink.Notice(DockerWordDuplicateNotice(typed));
                continue;
            }

            _settings.Update(d => d.DockerServerMatchWords = DockerServerWords.Add(d.DockerServerMatchWords, typed));
            Sink.Notice(DockerWordAddedNotice(typed));
            changed = true;
        }
    }

    /// <summary>The restore button's press on the match words (2026-10-10), <see cref="RestoreForbiddenDefaultsAsync"/>'s twin. True when saved.</summary>
    private async Task<bool> RestoreDockerWordsAsync(IReadOnlyList<string> words, CancellationToken cancellationToken)
    {
        var defaults = DockerServerWords.Defaults;
        if (words.SequenceEqual(defaults, StringComparer.OrdinalIgnoreCase))
        {
            Sink.Notice(DockerWordsAlreadyDefaultsNotice);
            return false;
        }

        var restoring = new MenuPage(RestoreDockerWordsQuestion(words.Count, defaults.Count), ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
        if (await PickAsync(restoring, 0, cancellationToken).ConfigureAwait(false) != 1)
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return false;
        }

        _settings.Update(d => d.DockerServerMatchWords = DockerServerWords.Defaults);
        Sink.Notice(DockerWordsRestoredNotice(defaults.Count));
        return true;
    }
}
