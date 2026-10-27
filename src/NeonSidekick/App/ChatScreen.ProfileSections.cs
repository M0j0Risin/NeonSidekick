using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.Files;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Sql;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.App;

// ── /profile push|pull mcp, links, videos, skills (2026-10-25, the user's ask) ─────────────────────────────────────
// The profile's other files and lists, each a section with the memory section's shape: the source read, nothing to copy said and
// nothing asked, the question, then the target written. A push reads the loaded profile's and writes the other's; a pull the other way.
// The monitors section is a profile.json one (PlanSectionCopy), as are the home and the bars (2026-10-26, below). Names read from the
// other profile's files are shown through NameScrub.Display, and a question lists only a few of them. Every line below is pinned.

internal sealed partial class ChatScreen
{
    /// <summary>How many names a section's question or notice lists before <c>and 3 more</c>.</summary>
    public const int ProfileSectionNamesShown = 3;

    /// <summary>
    /// <c>/profile push|pull mcp [overwrite]</c>: the source profile's own <c>configs/mcp.json</c> servers (never the home's, which every profile
    /// reads already) into the target's file, through <see cref="ConnectionsFileEdit"/> (its comments kept, its lock held, a missing file made).
    /// Names are compared ignoring case, as the editor matches them: a server the target lacks is added; one it holds unchanged is already
    /// there; one that differs is kept, or replaced with <paramref name="overwrite"/> (the target's spelling of the name kept). Either file
    /// that won't parse is an error, nothing asked; a source server with a problem is skipped and counted. One write per server: a failure
    /// stops there and says how many were written. A pull reloads the MCP servers when they are on and no connect wave runs.
    /// </summary>
    private async Task CopyMcpSectionAsync(string home, string other, bool overwrite, bool pull, CancellationToken cancellationToken)
    {
        string ours = McpConfigFile.ProfilePath(_settings.ProfileDirectory);
        string theirs = McpConfigFile.ProfilePath(Profiles.Directory(home, other));
        var source = McpConfigFile.Load(pull ? theirs : ours);
        var target = McpConfigFile.Load(pull ? ours : theirs);
        foreach (var (load, isSource) in new[] { (source, true), (target, false) })
        {
            if (FileProblem(load) is { } problem)
            {
                _flow.Error(ProfileMcpReadError(isSource == pull ? other : null, problem));
                return;
            }
        }

        int bad = source.Problems.Count;
        if (source.Servers.Count == 0)
        {
            _flow.Notice(ProfileMcpNothingNotice(other, pull, bad));
            return;
        }

        var writes = new List<(string Name, McpServerConfig Config, string? Existing)>();
        int same = 0, kept = 0;
        foreach (var (name, config) in source.Servers)
        {
            var held = target.Servers.FirstOrDefault(s => string.Equals(s.Key.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (held.Value is null)
            {
                writes.Add((name, config, null));
            }
            else if (held.Value.Fingerprint() == config.Fingerprint())
            {
                same++;
            }
            else if (overwrite)
            {
                writes.Add((held.Key, config, held.Key));
            }
            else
            {
                kept++;
            }
        }

        if (writes.Count == 0)
        {
            _flow.Notice(ProfileMcpNothingNewNotice(same, kept, other, pull));
            return;
        }

        if (!await ConfirmAsync(ProfileMcpPrompt(writes.Select(w => w.Name).ToList(), writes.Count(w => w.Existing is not null), other, pull), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        string path = pull ? ours : theirs;
        int added = 0, replaced = 0;
        foreach (var (name, config, existing) in writes)
        {
            string json = McpEntryText(config);
            string? error = existing is null
                ? ConnectionsFileEdit.AddConnection(path, name, json, McpConfigFile.EmptyText, McpSection, McpNoun)
                : ConnectionsFileEdit.ReplaceConnection(path, existing, existing, json, McpSection, McpNoun);
            if (error is not null)
            {
                _flow.Error(ProfileMcpFailedError(added + replaced, error));
                break;
            }

            if (existing is null)
            {
                added++;
            }
            else
            {
                replaced++;
            }
        }

        if (added + replaced == 0)
        {
            return;
        }

        var disabled = (pull ? _settings.Current.McpServersDisabled : OtherDisabledServers(home, other)).ToHashSet(StringComparer.Ordinal);
        int off = writes.Take(added + replaced).Count(w => disabled.Contains(w.Name));
        _flow.Notice(ProfileMcpCopiedNotice(added, replaced, same, kept, bad, off, other, pull));
        if (!pull)
        {
            return;
        }

        var effective = _effective();
        if (!effective.McpServers)
        {
            _flow.Notice(ProfileMcpOffNotice);
        }
        else if (_jobs.Running(BackgroundJobKind.Mcp))
        {
            _flow.Notice(ProfileMcpReloadLaterNotice);
        }
        else
        {
            var (reloadAdded, removed, reloadKept) = await _mcp.ReloadAsync(effective, cancellationToken).ConfigureAwait(false);
            _flow.Notice(McpText.ReloadedNotice(reloadAdded, removed, reloadKept));
        }
    }

    // mcp.json's entries sit under mcpServers, and a refusal calls one a server.
    private const string McpSection = "mcpServers";
    private const string McpNoun = "server";

    // A file-level problem of a load (not JSON, unreadable), or null: the per-server ones name the server too, and a blank name is one of those.
    private static string? FileProblem(McpConfigLoad load) =>
        load.Problems.FirstOrDefault(p => p.Source == load.Path && p.Reason != McpText.BlankName)?.Reason;

    // One server as the file holds it: the context's camel-case keys, indented, the relaxed escaping (a name in env or an argument as written).
    private static string McpEntryText(McpServerConfig config)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            JsonSerializer.Serialize(writer, config, McpJsonContext.Default.McpServerConfig);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    // The other profile's McpServersDisabled, for the done line; none when its profile.json can't be read (the copy is made either way).
    private static IReadOnlyList<string> OtherDisabledServers(string home, string other)
    {
        try
        {
            return Profiles.ReadProfileFile(Profiles.ProfileFile(home, other)).McpServersDisabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// <c>/profile push|pull links [overwrite]</c>: the Browser window's saved links into the target's <c>browser-links.json</c>, appended in
    /// their order (an address there skipped, <see cref="BrowserLinks.Key"/>) or in place of its list (<see cref="BrowserLinks.Import"/>). A
    /// pull tells an open Browser window, so its ☆ menu has them.
    /// </summary>
    private async Task CopyLinksSectionAsync(string home, string other, bool overwrite, bool pull, CancellationToken cancellationToken)
    {
        var theirs = new BrowserLinks(Profiles.Directory(home, other), _time);
        var ours = BrowserLinksNow();
        var links = (pull ? theirs : ours).List();
        if (links.Count == 0)
        {
            _flow.Notice(ProfileLinksNothingNotice(other, pull));
            return;
        }

        if (!await ConfirmAsync(ListSectionPrompt(SavedLinkCount(links.Count), "saved links", other, overwrite, pull), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        var result = (pull ? ours : theirs).Import(links, overwrite);
        if (result.Outcome != BrowserLinkOutcome.Done)
        {
            _flow.Error(ProfileLinksFailedError(result.Outcome, other, pull));
            return;
        }

        _flow.Notice(ListSectionNotice(SavedLinkCount(result.Added), result.Skipped, "saved links", other, overwrite, pull));
        if (pull)
        {
            _browserWindow?.LinksChanged();
        }
    }

    /// <summary>
    /// <c>/profile push|pull videos [overwrite]</c>: the saved YouTube videos into the target's <c>youtube.json</c>, each whole (where it was
    /// left, watched, added, last played), appended (an id there skipped, its blanks filled) or in place of its list
    /// (<see cref="YouTubeLibrary.Import"/>). A target file that can't be read is refused, never written over.
    /// </summary>
    private async Task CopyVideosSectionAsync(string home, string other, bool overwrite, bool pull, CancellationToken cancellationToken)
    {
        var theirs = new YouTubeLibrary(Profiles.Directory(home, other), _time);
        var ours = YouTubeLibraryNow();
        var videos = (pull ? theirs : ours).List();
        if (videos.Count == 0)
        {
            _flow.Notice(ProfileVideosNothingNotice(other, pull));
            return;
        }

        if (!await ConfirmAsync(ListSectionPrompt(SavedVideoCount(videos.Count), "saved videos", other, overwrite, pull), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        var result = (pull ? ours : theirs).Import(videos, overwrite);
        if (result.Outcome != YouTubeImportOutcome.Done)
        {
            _flow.Error(ProfileVideosFailedError(result.Outcome, other, pull));
            return;
        }

        _flow.Notice(ListSectionNotice(SavedVideoCount(result.Added), result.Skipped, "saved videos", other, overwrite, pull));
    }

    /// <summary>
    /// <c>/profile push|pull skills</c>: every profile skill's folder (<c>&lt;profile&gt;/skills</c>; the global and external ones are shared
    /// already) copied into the target's skills (<see cref="SkillEditor.CopyFolder"/>, the marker files with each). A folder whose name the
    /// target's profile skills hold, or whose skill's name they hold (a same-scope twin would be shadowed), is skipped and named, and so is a
    /// folder that is itself a link. No overwrite: nothing is ever replaced. The copies run on the pool under a kill scope; one stopped
    /// leaves the ones made. A pull reconciles the loaded profile's skill records, so they show at once; a push needs nothing (the target
    /// reconciles at its load). A copy starts with no history.
    /// </summary>
    private async Task CopySkillsSectionAsync(string home, string other, bool pull, CancellationToken cancellationToken)
    {
        string ours = Path.Combine(_settings.ProfileDirectory, SkillRoots.DirectoryName);
        string theirs = SkillsMenu.ProfileSkillsRoot(home, other);
        string sourceRoot = pull ? theirs : ours;
        string targetRoot = pull ? ours : theirs;
        var folders = SkillCatalog.Folders(sourceRoot);
        if (folders.Count == 0)
        {
            _flow.Notice(ProfileSkillsNothingNotice(other, pull));
            return;
        }

        var (heldFolders, heldNames) = SkillEditor.ProfileSkillNames(targetRoot);
        var copies = new List<string>();
        var skipped = new List<string>();
        var links = new List<string>();
        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder);
            if ((new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                links.Add(name);   // never followed, and not "there already" (the Mac pass of 2026-10-25: a link was named as the target's own)
            }
            else if (heldFolders.Contains(name) || (SkillEditor.FrontmatterName(folder) is { } skill && heldNames.Contains(skill)))
            {
                skipped.Add(name);
            }
            else
            {
                copies.Add(folder);
            }
        }

        if (copies.Count == 0)
        {
            _flow.Notice(ProfileSkillsNothingNewNotice(skipped, other, pull, links));
            return;
        }

        if (!await ConfirmAsync(ProfileSkillsPrompt(copies.Select(folder => Path.GetFileName(folder)).ToList(), other, pull), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        var copied = new List<string>();
        var failed = new List<(string Name, string Detail)>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using (BeginKillScope(cts))
        {
            try
            {
                await Task.Run(() =>
                {
                    foreach (string folder in copies)
                    {
                        var result = SkillEditor.CopyFolder(folder, targetRoot, cts.Token);
                        switch (result.Outcome)
                        {
                            case SkillEditOutcome.Copied:
                                copied.Add(result.Name);
                                break;
                            case SkillEditOutcome.Exists:
                                skipped.Add(result.Name);   // made there meanwhile (a reflection's write): never replaced
                                break;
                            default:
                                failed.Add((result.Name, result.Detail));
                                break;
                        }
                    }
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _flow.Notice(ProfileSkillsStoppedNotice(copied.Count, other, pull));
                if (pull && copied.Count > 0)
                {
                    ReconcileSkills();
                }

                return;
            }
        }

        foreach (var (name, detail) in failed)
        {
            _flow.Error(ProfileSkillFailedError(name, detail));
        }

        if (copied.Count > 0)
        {
            _flow.Notice(ProfileSkillsCopiedNotice(copied, skipped, other, pull, links));
            if (pull)
            {
                ReconcileSkills();
            }
        }
    }

    // ── /profile push|pull homeassistant, toolbar, perfbar, statusbar, barorder (2026-10-26, the user's ask) ──
    // profile.json sections, mirrored as servers is (PlanSectionCopy): the target ends up as the source, a default here resetting the
    // target's. The saved values alone, as a bare push carries them; a pull goes through Update, and every bar reads its settings on
    // each draw and tick (the home bar reconnects by itself when its URL or key change; the ha_ tools read them each turn).

    /// <summary>
    /// What each of the five sections carries: each <see cref="AppSettingsData"/> property by name (<c>nameof</c>, as
    /// <see cref="Profiles.ResetKeptSettings"/> keys them: most have no settings row) and the copy of its stored value, lists and maps
    /// copied deep and null staying null, as <see cref="AppSettings.Copy"/> does. <c>homeassistant</c> carries the <c>Home Assistant API
    /// key</c> too, as stored (a home bar without it shows nothing; it stays in <c>keys</c>): a <c>dpapi:</c> value reads the same in any
    /// profile of this Windows user, a Mac's <c>keychain:</c> one likewise (one app key, nothing per profile), <see cref="CopyKeys"/>'
    /// reasoning. Not <c>Web UI home controls</c>, not the ha_ tools' Offered switches (the user's call). Each prefix
    /// (<c>HomeAssistant</c>/<c>HomeBar</c>, <c>Toolbar</c>, <c>PerformanceBar</c>, <c>StatusBar</c> and <c>HintsBar</c>, <c>BarOrder</c>)
    /// names exactly these properties, which a test holds, so a new one can't be missed.
    /// </summary>
    public static readonly IReadOnlyDictionary<ProfileSection, IReadOnlyList<(string Name, Action<AppSettingsData, AppSettingsData> Copy)>> ProfileSettingGroups =
        new Dictionary<ProfileSection, IReadOnlyList<(string Name, Action<AppSettingsData, AppSettingsData> Copy)>>
        {
            [ProfileSection.HomeAssistant] =
            [
                (nameof(AppSettingsData.HomeAssistantTools), (from, to) => to.HomeAssistantTools = from.HomeAssistantTools),
                (nameof(AppSettingsData.HomeAssistantUrl), (from, to) => to.HomeAssistantUrl = from.HomeAssistantUrl),
                (nameof(AppSettingsData.HomeAssistantToken), (from, to) => to.HomeAssistantToken = from.HomeAssistantToken),
                (nameof(AppSettingsData.HomeAssistantActionPolicy), (from, to) => to.HomeAssistantActionPolicy = from.HomeAssistantActionPolicy),
                (nameof(AppSettingsData.HomeAssistantSafeServices), (from, to) => to.HomeAssistantSafeServices = CopyList(from.HomeAssistantSafeServices)),
                (nameof(AppSettingsData.HomeAssistantAssistAgent), (from, to) => to.HomeAssistantAssistAgent = from.HomeAssistantAssistAgent),
                (nameof(AppSettingsData.HomeAssistantTimeoutSeconds), (from, to) => to.HomeAssistantTimeoutSeconds = from.HomeAssistantTimeoutSeconds),
                (nameof(AppSettingsData.HomeBarItems), (from, to) => to.HomeBarItems = CopyList(from.HomeBarItems)),
                (nameof(AppSettingsData.HomeBarLastItems), (from, to) => to.HomeBarLastItems = CopyList(from.HomeBarLastItems)),
                (nameof(AppSettingsData.HomeBarLeft), (from, to) => to.HomeBarLeft = CopyList(from.HomeBarLeft)),
                (nameof(AppSettingsData.HomeBarRight), (from, to) => to.HomeBarRight = CopyList(from.HomeBarRight)),
                (nameof(AppSettingsData.HomeBarAliases), (from, to) => to.HomeBarAliases = CopyMap(from.HomeBarAliases)),
                (nameof(AppSettingsData.HomeBarGlyphs), (from, to) => to.HomeBarGlyphs = CopyMap(from.HomeBarGlyphs)),
            ],
            [ProfileSection.Toolbar] =
            [
                (nameof(AppSettingsData.ToolbarItems), (from, to) => to.ToolbarItems = CopyList(from.ToolbarItems)),
                (nameof(AppSettingsData.ToolbarLastItems), (from, to) => to.ToolbarLastItems = CopyList(from.ToolbarLastItems)),
                (nameof(AppSettingsData.ToolbarOrder), (from, to) => to.ToolbarOrder = CopyList(from.ToolbarOrder)),
                (nameof(AppSettingsData.ToolbarRight), (from, to) => to.ToolbarRight = CopyList(from.ToolbarRight)),
            ],
            [ProfileSection.PerfBar] =
            [
                (nameof(AppSettingsData.PerformanceBarItems), (from, to) => to.PerformanceBarItems = CopyList(from.PerformanceBarItems)),
                (nameof(AppSettingsData.PerformanceBarLastItems), (from, to) => to.PerformanceBarLastItems = CopyList(from.PerformanceBarLastItems)),
                (nameof(AppSettingsData.PerformanceBarOrder), (from, to) => to.PerformanceBarOrder = CopyList(from.PerformanceBarOrder)),
                (nameof(AppSettingsData.PerformanceBarLeft), (from, to) => to.PerformanceBarLeft = CopyList(from.PerformanceBarLeft)),
                (nameof(AppSettingsData.PerformanceBarRight), (from, to) => to.PerformanceBarRight = CopyList(from.PerformanceBarRight)),
                (nameof(AppSettingsData.PerformanceBarLook), (from, to) => to.PerformanceBarLook = from.PerformanceBarLook),
            ],
            [ProfileSection.StatusBar] =
            [
                (nameof(AppSettingsData.StatusBarItems), (from, to) => to.StatusBarItems = CopyList(from.StatusBarItems)),
                (nameof(AppSettingsData.StatusBarOrder), (from, to) => to.StatusBarOrder = CopyList(from.StatusBarOrder)),
                (nameof(AppSettingsData.StatusBarRight), (from, to) => to.StatusBarRight = CopyList(from.StatusBarRight)),
                (nameof(AppSettingsData.HintsBar), (from, to) => to.HintsBar = from.HintsBar),
            ],
            [ProfileSection.BarOrder] =
            [
                (nameof(AppSettingsData.BarOrder), (from, to) => to.BarOrder = CopyList(from.BarOrder)),
            ],
        };

    /// <summary>Whether a section is one of <see cref="ProfileSettingGroups"/>' five.</summary>
    public static bool IsSettingGroupSection(ProfileSection section) => ProfileSettingGroups.ContainsKey(section);

    private static List<string>? CopyList(List<string>? list) => list is null ? null : [.. list];

    private static Dictionary<string, string>? CopyMap(Dictionary<string, string>? map) =>
        map is null ? null : new Dictionary<string, string>(map, StringComparer.Ordinal);

    // The home bar's chips, its dividers left out, for the done line.
    private static int HomeBarItemCount(AppSettingsData data) => (data.HomeBarItems ?? []).Count(item => !HomeBarItems.IsDivider(item));

    // ── The lines ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a bar section's copy replaces, its verb included (2026-10-26): <c>toolbar items, order and sides are replaced</c>, or the
    /// performance bar's, the Status bar's or the bar order's. Pinned through <see cref="ProfileBarPrompt"/>.
    /// </summary>
    private static string ProfileBarReplaced(ProfileSection section) => section switch
    {
        ProfileSection.Toolbar => "toolbar items, order and sides are replaced",
        ProfileSection.PerfBar => "performance bar meters, order, sides and look are replaced",
        ProfileSection.StatusBar => "Status bar sections, order, sides and Hints bar are replaced",
        ProfileSection.BarOrder => "bar order is replaced",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Not a bar section."),
    };

    /// <summary>
    /// The question before a bar section's copy: <c>Copy this profile's toolbar setup into "work"? Its toolbar items, order and sides are
    /// replaced.</c>, pulled <c>Copy "work"'s toolbar setup into this profile? This profile's toolbar items, order and sides are replaced.</c>
    /// </summary>
    public static string ProfileBarPrompt(ProfileSection section, string profile, bool pull)
    {
        string subject = ProfileSectionSubject(section);
        return pull
            ? $"Copy \"{profile}\"'s {subject} into this profile? This profile's {ProfileBarReplaced(section)}."
            : $"Copy this profile's {subject} into \"{profile}\"? Its {ProfileBarReplaced(section)}.";
    }

    /// <summary>A bar section's copy done: <c>(copied the toolbar setup into "work")</c>, a pull <c>from "work"</c>.</summary>
    public static string ProfileBarCopiedNotice(ProfileSection section, string profile, bool pull) =>
        $"(copied the {ProfileSectionSubject(section)} {(pull ? "from" : "into")} \"{profile}\")";

    /// <summary>
    /// The question before a <c>homeassistant</c> copy, in the rows' own words: <c>Copy this profile's Home Assistant settings and home bar
    /// into "work"? Its Home Assistant tools switch, URL, API key, … are replaced.</c>; pulled, the mirror. When the source has no URL or no
    /// key, what the mirror does, as <see cref="ProfileKeysPrompt"/> says it: <c> "work"'s Home Assistant URL and API key are cleared: none
    /// here.</c>, pulled <c> This profile's … cleared: none in "work".</c>, so a mirror that wipes is never a surprise.
    /// </summary>
    public static string ProfileHomeAssistantPrompt(string profile, bool urlSet, bool keySet, bool pull)
    {
        const string Replaced = "Home Assistant tools switch, URL, API key, action policy, safe services, Assist agent and timeout, and its home bar's items, sides, names and emoji are replaced.";
        string question = pull
            ? $"Copy \"{profile}\"'s Home Assistant settings and home bar into this profile? This profile's {Replaced}"
            : $"Copy this profile's Home Assistant settings and home bar into \"{profile}\"? Its {Replaced}";
        string? cleared = (urlSet, keySet) switch
        {
            (false, false) => "Home Assistant URL and API key are",
            (false, true) => "Home Assistant URL is",
            (true, false) => HomeAssistantKeyName + " is",
            _ => null,
        };
        if (cleared is null)
        {
            return question;
        }

        return pull
            ? $"{question} This profile's {cleared} cleared: none in \"{profile}\"."
            : $"{question} \"{profile}\"'s {cleared} cleared: none here.";
    }

    /// <summary>
    /// A <c>homeassistant</c> copy done: <c>(copied the Home Assistant settings into "work": Home Assistant URL http://…, 6 home bar items)</c>,
    /// the URL as the settings pane shows it (<c>(not set)</c> when empty), the chips counted without their dividers; never the key. A pull
    /// says from.
    /// </summary>
    public static string ProfileHomeAssistantCopiedNotice(string profile, string url, int items, bool pull) =>
        $"(copied the Home Assistant settings {(pull ? "from" : "into")} \"{profile}\": Home Assistant URL {url}, {Counted(items, "home bar item", "home bar items")})";

    /// <summary>
    /// Names as a question or notice lists them: each through <see cref="NameScrub.Display"/>, the first <see cref="ProfileSectionNamesShown"/>
    /// joined, then <c>and 3 more</c>: <c>docker, github, files and 2 more</c>.
    /// </summary>
    public static string ProfileSectionNames(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var shown = names.Take(ProfileSectionNamesShown).Select(name => NameScrub.Display(name));
        string list = string.Join(", ", shown);
        int more = names.Count - ProfileSectionNamesShown;
        return more > 0 ? $"{list} and {more.ToString(CultureInfo.InvariantCulture)} more" : list;
    }

    private static string Counted(int count, string one, string many) =>
        count == 1 ? "1 " + one : $"{count.ToString(CultureInfo.InvariantCulture)} {many}";

    private static string McpServerCount(int count) => Counted(count, "MCP server", "MCP servers");

    private static string SavedLinkCount(int count) => Counted(count, "saved link", "saved links");

    private static string SavedVideoCount(int count) => Counted(count, "saved video", "saved videos");

    private static string SkillCount(int count) => Counted(count, "skill", "skills");

    /// <summary>Either profile's <c>mcp.json</c> couldn't be read: the other one named on a pull's source or a push's target, else this profile's.</summary>
    public static string ProfileMcpReadError(string? profile, string reason) =>
        $"Could not read {(profile is null ? "this profile" : $"\"{profile}\"")}'s {McpConfigFile.FileName}: {reason}";

    /// <summary><c>mcp</c> with nothing to copy: <c>(🔌 nothing to copy: this profile has no MCP servers of its own)</c>, the servers skipped for a problem counted.</summary>
    public static string ProfileMcpNothingNotice(string profile, bool pull, int bad = 0) =>
        $"({McpText.Glyph} nothing to copy: {SourceName(profile, pull)} has no MCP servers of its own"
        + (bad > 0 ? $"; {Counted(bad, "entry", "entries")} with a problem skipped" : "") + ")";

    /// <summary>Every server there already: <c>(🔌 nothing to copy: "work" has these 3 MCP servers already, 1 of them different (overwrite replaces it))</c>.</summary>
    public static string ProfileMcpNothingNewNotice(int same, int kept, string profile, bool pull)
    {
        string target = pull ? "this profile" : $"\"{profile}\"";
        string notice = $"({McpText.Glyph} nothing to copy: {target} has {(same + kept == 1 ? "this MCP server" : $"these {McpServerCount(same + kept)}")} already";
        return notice + (kept == 0 ? ")" : $", {kept.ToString(CultureInfo.InvariantCulture)} of them different ({OverwriteWord} replaces {(kept == 1 ? "it" : "them")}))");
    }

    /// <summary>
    /// The question before an <c>mcp</c> copy: the servers named, the replaced ones counted, and what travels with them:
    /// <c>🔌 Copy 2 MCP servers (docker, github) into "work"? Their env, headers and args go as written, plain text.</c>
    /// </summary>
    public static string ProfileMcpPrompt(IReadOnlyList<string> names, int replacing, string profile, bool pull)
    {
        ArgumentNullException.ThrowIfNull(names);
        string servers = $"{McpServerCount(names.Count)} ({ProfileSectionNames(names)})";
        string where = pull ? $"from \"{profile}\" into this profile" : $"into \"{profile}\"";
        string replaced = replacing == 0 ? "" : $", replacing {replacing.ToString(CultureInfo.InvariantCulture)} of the same name";
        return $"{McpText.Glyph} Copy {servers} {where}{replaced}? Their env, headers and args go as written, plain text.";
    }

    /// <summary>
    /// An <c>mcp</c> copy done: <c>(🔌 2 MCP servers copied into "work", 1 replaced, 1 already there, 1 different kept, 1 stays off there)</c>;
    /// the problem entries skipped counted last.
    /// </summary>
    public static string ProfileMcpCopiedNotice(int added, int replaced, int same, int kept, int bad, int off, string profile, bool pull)
    {
        var parts = new List<string> { $"{McpServerCount(added)} copied {(pull ? $"from \"{profile}\"" : $"into \"{profile}\"")}" };
        if (replaced > 0)
        {
            parts.Add($"{replaced.ToString(CultureInfo.InvariantCulture)} replaced");
        }

        if (same > 0)
        {
            parts.Add($"{same.ToString(CultureInfo.InvariantCulture)} already there");
        }

        if (kept > 0)
        {
            parts.Add($"{kept.ToString(CultureInfo.InvariantCulture)} different kept");
        }

        if (off > 0)
        {
            parts.Add($"{off.ToString(CultureInfo.InvariantCulture)} {(off == 1 ? "stays" : "stay")} off {(pull ? "here" : "there")} (/mcp)");
        }

        if (bad > 0)
        {
            parts.Add($"{Counted(bad, "entry", "entries")} with a problem skipped");
        }

        return $"({McpText.Glyph} {string.Join(", ", parts)})";
    }

    /// <summary>A write that failed partway: how many were written before it, and the editor's own refusal.</summary>
    public static string ProfileMcpFailedError(int written, string detail) =>
        $"Could not copy the MCP servers ({written.ToString(CultureInfo.InvariantCulture)} written first): {detail}";

    /// <summary>After a pull with <c>MCP servers</c> off: the copied servers connect when it is on.</summary>
    public const string ProfileMcpOffNotice = "(" + McpText.Glyph + " MCP servers are off here: the copied ones connect when they are on)";

    /// <summary>After a pull while a connect wave runs: <c>/mcp</c>'s Reload row connects them.</summary>
    public const string ProfileMcpReloadLaterNotice = "(" + McpText.Glyph + " the servers are still connecting: /mcp's Reload row picks up the copied ones)";

    /// <summary><c>links</c> with nothing to copy: <c>(nothing to copy: this profile has no saved links)</c>, or <c>"work"</c> on a pull.</summary>
    public static string ProfileLinksNothingNotice(string profile, bool pull) => $"(nothing to copy: {SourceName(profile, pull)} has no saved links)";

    /// <summary>A links copy the target refused (a newer version's file, one that can't be read or written): nothing changed.</summary>
    public static string ProfileLinksFailedError(BrowserLinkOutcome outcome, string profile, bool pull) =>
        $"Could not copy into {(pull ? "this profile" : $"\"{profile}\"")}'s saved links: "
        + (outcome == BrowserLinkOutcome.Newer ? BrowserLinkText.NewerFile : BrowserLinkText.SaveFailed);

    /// <summary><c>videos</c> with nothing to copy: <c>(nothing to copy: this profile has no saved videos)</c>, or <c>"work"</c> on a pull.</summary>
    public static string ProfileVideosNothingNotice(string profile, bool pull) => $"(nothing to copy: {SourceName(profile, pull)} has no saved videos)";

    /// <summary>A videos copy the target refused: its file couldn't be read (left alone) or written. Nothing changed.</summary>
    public static string ProfileVideosFailedError(YouTubeImportOutcome outcome, string profile, bool pull) =>
        $"Could not copy into {(pull ? "this profile" : $"\"{profile}\"")}'s saved videos: "
        + (outcome == YouTubeImportOutcome.Unreadable ? $"its {YouTubeLibrary.FileName} could not be read, so it was left alone; the log says why." : YouTubeText.SaveFailed);

    /// <summary><c>skills</c> with nothing to copy: <c>(🧩 nothing to copy: this profile has no skills of its own)</c>, or <c>"work"</c> on a pull.</summary>
    public static string ProfileSkillsNothingNotice(string profile, bool pull) =>
        $"({NoticeGlyphs.Skill}nothing to copy: {SourceName(profile, pull)} has no skills of its own)";

    /// <summary>
    /// Every skill held by the target already, or a link: <c>(🧩 nothing to copy: "work" has these 2 skills already: notes, todo)</c>, the links
    /// after (<see cref="ProfileSkillsLinksTail"/>), or only <c>(🧩 nothing to copy: 1 skill folder is a link, never followed: notes)</c>.
    /// </summary>
    public static string ProfileSkillsNothingNewNotice(IReadOnlyList<string> skipped, string profile, bool pull, IReadOnlyList<string>? links = null)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        links ??= [];
        if (skipped.Count == 0)
        {
            return $"({NoticeGlyphs.Skill}nothing to copy: {Counted(links.Count, "skill folder is a link", "skill folders are links")}, never followed: {ProfileSectionNames(links)})";
        }

        string target = pull ? "this profile" : $"\"{profile}\"";
        return $"({NoticeGlyphs.Skill}nothing to copy: {target} has {(skipped.Count == 1 ? "this skill" : $"these {SkillCount(skipped.Count)}")} already: {ProfileSectionNames(skipped)}{ProfileSkillsLinksTail(links)})";
    }

    /// <summary>
    /// The skill folders skipped as links (the Mac pass of 2026-10-25: they were counted "there already"): <c>; 1 skipped as a link: notes</c>,
    /// or "" with none.
    /// </summary>
    public static string ProfileSkillsLinksTail(IReadOnlyList<string> links) =>
        links.Count == 0 ? "" : $"; {links.Count.ToString(CultureInfo.InvariantCulture)} skipped as {(links.Count == 1 ? "a link" : "links")}: {ProfileSectionNames(links)}";

    /// <summary>
    /// The question before a <c>skills</c> copy: <c>🧩 Copy 2 skills (notes, todo) into "work"? Each starts there with no history.</c>
    /// </summary>
    public static string ProfileSkillsPrompt(IReadOnlyList<string> names, string profile, bool pull)
    {
        ArgumentNullException.ThrowIfNull(names);
        string where = pull ? $"from \"{profile}\" into this profile" : $"into \"{profile}\"";
        return $"{NoticeGlyphs.Skill}Copy {SkillCount(names.Count)} ({ProfileSectionNames(names)}) {where}? {(names.Count == 1 ? "It starts" : "Each starts")} there with no history.";
    }

    /// <summary>A <c>skills</c> copy done: <c>(🧩 2 skills copied into "work"; 1 skipped, there already: notes)</c>.</summary>
    public static string ProfileSkillsCopiedNotice(IReadOnlyList<string> copied, IReadOnlyList<string> skipped, string profile, bool pull, IReadOnlyList<string>? links = null)
    {
        ArgumentNullException.ThrowIfNull(copied);
        ArgumentNullException.ThrowIfNull(skipped);
        string done = $"{SkillCount(copied.Count)} copied {(pull ? $"from \"{profile}\"" : $"into \"{profile}\"")}";
        string tail = skipped.Count == 0 ? "" : $"; {skipped.Count.ToString(CultureInfo.InvariantCulture)} skipped, there already: {ProfileSectionNames(skipped)}";
        return $"({NoticeGlyphs.Skill}{done}{tail}{ProfileSkillsLinksTail(links ?? [])})";
    }

    /// <summary>A <c>skills</c> copy stopped (Esc, the kill switch): the ones made before it stay.</summary>
    public static string ProfileSkillsStoppedNotice(int copied, string profile, bool pull) =>
        $"({NoticeGlyphs.Skill}stopped: {SkillCount(copied)} copied {(pull ? $"from \"{profile}\"" : $"into \"{profile}\"")} before it)";

    /// <summary>One skill's copy that failed (the caps, the volume's room, a file failure, a linked folder).</summary>
    public static string ProfileSkillFailedError(string name, string detail) => $"Could not copy skill '{NameScrub.Display(name)}': {detail}";
}
