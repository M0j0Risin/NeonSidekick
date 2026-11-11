using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.CustomCommands;
using NeonSidekick.Files;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Sql;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.App;

// ── /profile push|pull mcp, links, youtube (videos until 2026-11-08), skills (2026-10-25, the user's ask) ────────────────────────────────────
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
    /// <c>/profile push|pull youtube [overwrite]</c> (<c>videos</c> until 2026-11-08): the saved YouTube videos and playlists into the target's <c>youtube.json</c>, each whole (where it was
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

        // After a yes there is always a summary (2026-10-31, review P4-4): one that copied nothing said nothing at all.
        _flow.Notice(ProfileSkillsCopiedNotice(copied, skipped, other, pull, links, failed.Count));
        if (pull && copied.Count > 0)
        {
            ReconcileSkills();
        }
    }

    /// <summary>
    /// <c>/profile push|pull commands [overwrite]</c> (2026-10-30, the custom commands plan): the profile's <c>commands/</c> files the catalog
    /// walks (<see cref="CustomCommandCatalog.CopyableFiles"/>: <c>*.md</c> up to four folders deep, subfolders kept, dot-files left behind, a
    /// Skipped file copied as it is, but for a case twin's second file, which runs nowhere) into the target's, byte for byte
    /// (<see cref="CustomCommandEditor.CopyInto"/>). A link is never followed
    /// and is named apart. A same-named file is skipped and named, or replaced with <paramref name="overwrite"/>, the yes naming those apart
    /// from the new. Two folders that are one (both linked to one place) have nothing to copy, never a file onto itself. No reconcile: the
    /// next scan reads the new files.
    /// </summary>
    private async Task CopyCustomCommandsSectionAsync(string home, string other, bool overwrite, bool pull, CancellationToken cancellationToken)
    {
        string ours = _settings.ProfileCustomCommandsDirectory;
        string theirs = Path.Combine(Profiles.Directory(home, other), CustomCommandRoots.DirectoryName);
        string sourceRoot = pull ? theirs : ours;
        string targetRoot = pull ? ours : theirs;
        if (CustomCommandRoots.SameFolder(sourceRoot, targetRoot))
        {
            _flow.Notice(ProfileCustomCommandsSameFolderNotice(other));
            return;
        }

        var (walked, links, unreadable) = CustomCommandCatalog.CopyableFiles(sourceRoot);

        // A case twin's second file stays behind (the Mac pass of 2026-11-01: from a case-sensitive folder holding Review.md and review.md both
        // went, the yes naming /review twice, and in a case-blind target review.md landed last on Review.md, so /review there ran the file this
        // one skips). The scan's own rule (CustomCommandName.CaseDuplicates) picks it, so what is copied is what runs here.
        var twins = CustomCommandName.CaseDuplicates(walked.Select(f => f.Relative));
        var files = walked.Where(f => !twins.Contains(f.Relative)).ToList();

        // A folder that couldn't be listed is named (2026-10-31, review P5-4), never read as "nothing to copy".
        foreach (var problem in unreadable)
        {
            _flow.Error(ProfileCustomCommandsUnreadableError(problem.FilePath, problem.Problem));
        }

        if (files.Count == 0)
        {
            if (unreadable.Count == 0)
            {
                _flow.Notice(links.Count == 0 ? ProfileCustomCommandsNothingNotice(other, pull) : ProfileCustomCommandsNothingNewNotice([], other, pull, links));
            }

            return;
        }

        var copies = new List<(string Relative, string Full)>();
        var replacing = new List<string>();
        var skipped = new List<string>();
        foreach (var (relative, full) in files)
        {
            string name = CommandFileName(relative);

            // Any case (2026-10-31, review P5-5): Review.md there is review.md's command on a case-sensitive volume too.
            if (CustomCommandEditor.Existing(targetRoot, relative) is not null)
            {
                if (!overwrite)
                {
                    skipped.Add(name);
                    continue;
                }

                replacing.Add(name);
            }

            copies.Add((relative, full));
        }

        if (copies.Count == 0)
        {
            _flow.Notice(ProfileCustomCommandsNothingNewNotice(skipped, other, pull, links));
            return;
        }

        var fresh = copies.Select(c => CommandFileName(c.Relative)).Where(name => !replacing.Contains(name)).ToList();
        if (!await ConfirmAsync(ProfileCustomCommandsPrompt(fresh, replacing, other, pull), cancellationToken).ConfigureAwait(false))
        {
            _flow.Notice(KeptNotice);
            return;
        }

        // Under a kill scope like the skills copy (2026-10-31, review P4-5): Esc stops it between files, the ones made staying.
        var copied = new List<string>();
        var failed = new List<(string Name, string Detail)>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using (BeginKillScope(cts))
        {
            try
            {
                await Task.Run(() =>
                {
                    foreach (var (relative, full) in copies)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var result = CustomCommandEditor.CopyInto(sourceRoot, full, targetRoot, relative, overwrite);
                        switch (result.Outcome)
                        {
                            case CustomCommandEditOutcome.Copied:
                                copied.Add(CommandFileName(relative));
                                break;
                            case CustomCommandEditOutcome.Exists:
                                skipped.Add(CommandFileName(relative));   // made there meanwhile: never replaced without overwrite
                                break;
                            case CustomCommandEditOutcome.Linked:
                                failed.Add((CommandFileName(relative), result.LinkIsFile ? CustomCommandText.LinkedFileError(result.Path) : CustomCommandText.LinkedError(CommandFileName(relative).TrimStart('/'))));
                                break;
                            default:
                                failed.Add((CommandFileName(relative), result.Detail));
                                break;
                        }
                    }
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _flow.Notice(ProfileCustomCommandsStoppedNotice(copied.Count, other, pull));
                return;
            }
        }

        foreach (var (name, detail) in failed)
        {
            _flow.Error(ProfileCustomCommandFailedError(name, detail));
        }

        // After a yes there is always a summary (2026-10-31, review P4-4), all skipped or failed included.
        _flow.Notice(ProfileCustomCommandsCopiedNotice(copied, skipped, other, pull, links, failed.Count));
    }

    // A copied file as the lines name it: its /word, or its path under the folder when it is no command name.
    private static string CommandFileName(string relative) =>
        CustomCommandName.FromRelativePath(relative) is { } name ? "/" + name : relative;

    // ── /profile push|pull servers, homeassistant, toolbar, perfbar, statusbar, barorder (2026-10-26, the user's ask) ──
    // profile.json sections, mirrored (PlanSectionCopy): the target ends up as the source, a default here resetting the
    // target's. The saved values alone, as a bare push carries them; a pull goes through Update, and every bar reads its settings on
    // each draw and tick (the home bar reconnects by itself when its URL or key change; the ha_ tools read them each turn).

    /// <summary>
    /// What each mirrored profile.json section carries (<c>servers</c> joined the five on 2026-10-26, the review's R16: one list of names, one
    /// copy loop): each <see cref="AppSettingsData"/> property by name (<c>nameof</c>, as
    /// <see cref="Profiles.ResetKeptSettings"/> keys them: most have no settings row) and the copy of its stored value, lists and maps
    /// copied deep and null staying null, as <see cref="AppSettings.Copy"/> does. <c>homeassistant</c> carries the <c>Home Assistant API
    /// key</c> too, as stored (a home bar without it shows nothing; it stays in <c>keys</c>): a <c>dpapi:</c> value reads the same in any
    /// profile of this Windows user, a Mac's <c>keychain:</c> one likewise (one app key, nothing per profile), <see cref="CopyKeys"/>'
    /// reasoning. Not <c>Web UI home controls</c>, not the ha_ tools' Offered switches (the user's call). Each prefix
    /// (<c>HomeAssistant</c>/<c>HomeBar</c>, <c>Toolbar</c>, <c>PerformanceBar</c>, <c>StatusBar</c> and <c>HintsBar</c>, <c>BarOrder</c>)
    /// names exactly these properties, which a test holds, so a new one can't be missed. <c>servers</c> (2026-10-07, the user's list, and the
    /// three groups they added: the Claude Code server, the embedded tuning, the Docker server timings) spans several prefixes in part, so its
    /// test pins its names instead: <c>Embedded models</c> stores nothing per profile (the models are shared under the home, the one in use is
    /// in <c>LLM URL</c> and <c>LLM model</c>), and the keys are <c>keys</c>'. 2026-10-10 (the user's list): how the model answers — LLM
    /// reasoning, its estimate, the sampling overrides (every model's, a deep copy) and the Hugging Face switch — and the Anthropic and OpenAI
    /// APIs' own rows beyond their switches; later that day (the user's pick) <c>/claude</c>'s four rows. Each name is its settings row's too
    /// (<c>SettingsField</c>), so the done line reads the pane's words. The order each joined in, pinned, as nothing reads it by position.
    /// 2026-11-10 (the user's ask): <c>LLM one local model</c> and <c>Docker server one at a time</c>, what a connect stops.
    /// </summary>
    public static readonly IReadOnlyDictionary<ProfileSection, IReadOnlyList<(string Name, Action<AppSettingsData, AppSettingsData> Copy)>> ProfileSettingGroups =
        new Dictionary<ProfileSection, IReadOnlyList<(string Name, Action<AppSettingsData, AppSettingsData> Copy)>>
        {
            [ProfileSection.Servers] =
            [
                (nameof(AppSettingsData.LlmScanMode), (from, to) => to.LlmScanMode = from.LlmScanMode),
                (nameof(AppSettingsData.LlmUrl), (from, to) => to.LlmUrl = from.LlmUrl),
                (nameof(AppSettingsData.LlmModel), (from, to) => to.LlmModel = from.LlmModel),
                (nameof(AppSettingsData.LlmReasoning), (from, to) => to.LlmReasoning = from.LlmReasoning),
                (nameof(AppSettingsData.LlmReasoningEstimate), (from, to) => to.LlmReasoningEstimate = from.LlmReasoningEstimate),
                (nameof(AppSettingsData.LlmSampling), (from, to) => to.LlmSampling = LlmSamplingEntry.CopyAll(from.LlmSampling)),
                (nameof(AppSettingsData.LlmSamplingFromHuggingFace), (from, to) => to.LlmSamplingFromHuggingFace = from.LlmSamplingFromHuggingFace),
                (nameof(AppSettingsData.EmbeddedLlmServer), (from, to) => to.EmbeddedLlmServer = from.EmbeddedLlmServer),
                (nameof(AppSettingsData.EmbeddedBackend), (from, to) => to.EmbeddedBackend = from.EmbeddedBackend),
                (nameof(AppSettingsData.EmbeddedContextSize), (from, to) => to.EmbeddedContextSize = from.EmbeddedContextSize),
                (nameof(AppSettingsData.EmbeddedGpuLayers), (from, to) => to.EmbeddedGpuLayers = from.EmbeddedGpuLayers),
                (nameof(AppSettingsData.EmbeddedVramBudget), (from, to) => to.EmbeddedVramBudget = from.EmbeddedVramBudget),
                (nameof(AppSettingsData.EmbeddedVramOnly), (from, to) => to.EmbeddedVramOnly = from.EmbeddedVramOnly),
                (nameof(AppSettingsData.EmbeddedVision), (from, to) => to.EmbeddedVision = from.EmbeddedVision),
                (nameof(AppSettingsData.EmbeddedDrafter), (from, to) => to.EmbeddedDrafter = from.EmbeddedDrafter),
                (nameof(AppSettingsData.DockerServers), (from, to) => to.DockerServers = from.DockerServers),
                (nameof(AppSettingsData.DockerServerContainers), (from, to) => to.DockerServerContainers = CopyList(from.DockerServerContainers)),
                (nameof(AppSettingsData.DockerServerMatchWords), (from, to) => to.DockerServerMatchWords = [.. from.DockerServerMatchWords]),
                (nameof(AppSettingsData.DockerServerStopTimeoutSeconds), (from, to) => to.DockerServerStopTimeoutSeconds = from.DockerServerStopTimeoutSeconds),
                (nameof(AppSettingsData.DockerServerPostStopDelaySeconds), (from, to) => to.DockerServerPostStopDelaySeconds = from.DockerServerPostStopDelaySeconds),
                (nameof(AppSettingsData.DockerServerReadyTimeoutSeconds), (from, to) => to.DockerServerReadyTimeoutSeconds = from.DockerServerReadyTimeoutSeconds),
                (nameof(AppSettingsData.DockerServerStopOnExit), (from, to) => to.DockerServerStopOnExit = from.DockerServerStopOnExit),
                (nameof(AppSettingsData.ClaudeCodeServer), (from, to) => to.ClaudeCodeServer = from.ClaudeCodeServer),
                (nameof(AppSettingsData.ClaudeCodeExecutable), (from, to) => to.ClaudeCodeExecutable = from.ClaudeCodeExecutable),
                (nameof(AppSettingsData.ClaudeCodePermissions), (from, to) => to.ClaudeCodePermissions = from.ClaudeCodePermissions),
                (nameof(AppSettingsData.ClaudeCodeModel), (from, to) => to.ClaudeCodeModel = from.ClaudeCodeModel),
                (nameof(AppSettingsData.ClaudeCodeEffort), (from, to) => to.ClaudeCodeEffort = from.ClaudeCodeEffort),
                (nameof(AppSettingsData.AnthropicApi), (from, to) => to.AnthropicApi = from.AnthropicApi),
                (nameof(AppSettingsData.AnthropicApiMaxTokens), (from, to) => to.AnthropicApiMaxTokens = from.AnthropicApiMaxTokens),
                (nameof(AppSettingsData.AnthropicApiPromptCaching), (from, to) => to.AnthropicApiPromptCaching = from.AnthropicApiPromptCaching),
                (nameof(AppSettingsData.OpenAIApi), (from, to) => to.OpenAIApi = from.OpenAIApi),
                (nameof(AppSettingsData.OpenAIApiMaxTokens), (from, to) => to.OpenAIApiMaxTokens = from.OpenAIApiMaxTokens),
                (nameof(AppSettingsData.OpenAIApiOrganization), (from, to) => to.OpenAIApiOrganization = from.OpenAIApiOrganization),
                (nameof(AppSettingsData.OpenAIApiProject), (from, to) => to.OpenAIApiProject = from.OpenAIApiProject),
                (nameof(AppSettingsData.LlmOneLocalModel), (from, to) => to.LlmOneLocalModel = from.LlmOneLocalModel),
                (nameof(AppSettingsData.DockerServerOneAtATime), (from, to) => to.DockerServerOneAtATime = from.DockerServerOneAtATime),
            ],
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

    /// <summary>Whether a section is one of <see cref="ProfileSettingGroups"/>' six.</summary>
    public static bool IsSettingGroupSection(ProfileSection section) => ProfileSettingGroups.ContainsKey(section);

    private static List<string>? CopyList(List<string>? list) => list is null ? null : [.. list];

    private static Dictionary<string, string>? CopyMap(Dictionary<string, string>? map) =>
        map is null ? null : new Dictionary<string, string>(map, StringComparer.Ordinal);

    // The home bar's chips, its dividers left out, for the done line.
    private static int HomeBarItemCount(AppSettingsData data) => (data.HomeBarItems ?? []).Count(item => !HomeBarItems.IsDivider(item));

    /// <summary>
    /// A mirrored section's question and its done line (given the target's data afterwards and its folder) (2026-10-26, the review's R16):
    /// <c>servers</c>' lines as they were, the five's from <see cref="ProfileText"/>.
    /// </summary>
    private static (string Prompt, Func<AppSettingsData, string, string> Notice) ProfileGroupWords(ProfileSection section, AppSettingsData from, string profile, bool pull) => section switch
    {
        ProfileSection.Servers => (
            ProfileServersPrompt(profile, pull),
            (data, directory) => ProfileServersCopiedNotice(
                profile, SettingsMenu.FieldValue(SettingsField.LlmUrl, data, directory), SettingsMenu.FieldValue(SettingsField.LlmModel, data, directory), pull)),
        ProfileSection.HomeAssistant => (
            ProfileText.HomeAssistantPrompt(profile, !string.IsNullOrWhiteSpace(from.HomeAssistantUrl), !string.IsNullOrWhiteSpace(from.HomeAssistantToken), pull),
            (data, directory) => ProfileText.HomeAssistantCopiedNotice(profile, SettingsMenu.FieldValue(SettingsField.HomeAssistantUrl, data, directory), HomeBarItemCount(data), pull)),
        _ => (ProfileText.BarPrompt(section, profile, pull), (_, _) => ProfileText.BarCopiedNotice(section, profile, pull)),
    };

    // ── The lines ─────────────────────────────────────────────────────────────────────────────────────
    // The five sections' questions and done lines are ProfileText's (2026-10-26, the review's R27).

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

    /// <summary><c>youtube</c> with nothing to copy: <c>(nothing to copy: this profile has no saved videos)</c>, or <c>"work"</c> on a pull.</summary>
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

    /// <summary>A <c>skills</c> copy done: <c>(🧩 2 skills copied into "work"; 1 skipped, there already: notes)</c>, <c>; 1 failed</c> after.</summary>
    public static string ProfileSkillsCopiedNotice(IReadOnlyList<string> copied, IReadOnlyList<string> skipped, string profile, bool pull, IReadOnlyList<string>? links = null, int failed = 0)
    {
        ArgumentNullException.ThrowIfNull(copied);
        return ProfileCopySummary(NoticeGlyphs.Skill, SkillCount(copied.Count), skipped, failed, profile, pull, links);
    }

    /// <summary>
    /// The one summary a profile copy ends with (2026-10-31, review P6-9: skills and commands built theirs apart): what was copied, then
    /// skipped (there already), failed and the links, each only when there are any.
    /// </summary>
    private static string ProfileCopySummary(string glyph, string copied, IReadOnlyList<string> skipped, int failed, string profile, bool pull, IReadOnlyList<string>? links)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        string done = $"{copied} copied {(pull ? $"from \"{profile}\"" : $"into \"{profile}\"")}";
        string tail = skipped.Count == 0 ? "" : $"; {skipped.Count.ToString(CultureInfo.InvariantCulture)} skipped, there already: {ProfileSectionNames(skipped)}";
        string failures = failed == 0 ? "" : $"; {failed.ToString(CultureInfo.InvariantCulture)} failed";
        return $"({glyph}{done}{tail}{failures}{ProfileSkillsLinksTail(links ?? [])})";
    }

    /// <summary>A profile copy stopped (Esc, the kill switch): the ones made before it stay.</summary>
    private static string ProfileCopyStoppedNotice(string glyph, string copied, string profile, bool pull) =>
        $"({glyph}stopped: {copied} copied {(pull ? $"from \"{profile}\"" : $"into \"{profile}\"")} before it)";

    /// <summary><c>commands</c> with nothing to copy: <c>(🪄 nothing to copy: this profile has no custom commands)</c>, or <c>"work"</c> on a pull.</summary>
    public static string ProfileCustomCommandsNothingNotice(string profile, bool pull) =>
        $"({NoticeGlyphs.Commands}nothing to copy: {SourceName(profile, pull)} has no custom commands)";

    /// <summary>The two profiles' <c>commands</c> folders are one (both linked to one place): nothing to copy (the pane's words too, <see cref="CustomCommandText.SharedFolderNotice"/>).</summary>
    public static string ProfileCustomCommandsSameFolderNotice(string profile) =>
        $"({NoticeGlyphs.Commands}{CustomCommandText.SharedFolderNotice(profile)})";

    /// <summary>A folder of the copy's source that couldn't be listed (2026-10-31, review P5-4).</summary>
    public static string ProfileCustomCommandsUnreadableError(string folder, string problem) => $"Could not copy from {folder}: {problem}";

    /// <summary>
    /// Every file held by the target already, or a link: <c>(🪄 nothing to copy: "work" has these 2 already: /review, /triage)</c>, the links
    /// after (<see cref="ProfileSkillsLinksTail"/>), or only <c>(🪄 nothing to copy: 1 is a link, never followed: notes)</c>.
    /// </summary>
    public static string ProfileCustomCommandsNothingNewNotice(IReadOnlyList<string> skipped, string profile, bool pull, IReadOnlyList<string>? links = null)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        links ??= [];
        if (skipped.Count == 0)
        {
            return $"({NoticeGlyphs.Commands}nothing to copy: {Counted(links.Count, "is a link", "are links")}, never followed: {ProfileSectionNames(links)})";
        }

        string target = pull ? "this profile" : $"\"{profile}\"";
        return $"({NoticeGlyphs.Commands}nothing to copy: {target} has {(skipped.Count == 1 ? "this one" : $"these {skipped.Count.ToString(CultureInfo.InvariantCulture)}")} already: {ProfileSectionNames(skipped)} (overwrite replaces them){ProfileSkillsLinksTail(links)})";
    }

    /// <summary>
    /// The question before a <c>commands</c> copy: <c>🪄 Copy 2 custom commands (/review, /triage) into "work"?</c>, with
    /// <c> It replaces 1 there: /fix.</c> when overwrite replaces some.
    /// </summary>
    public static string ProfileCustomCommandsPrompt(IReadOnlyList<string> fresh, IReadOnlyList<string> replacing, string profile, bool pull)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(replacing);
        string where = pull ? $"from \"{profile}\" into this profile" : $"into \"{profile}\"";
        string head = fresh.Count > 0
            ? $"{NoticeGlyphs.Commands}Copy {CustomCommandCount(fresh.Count)} ({ProfileSectionNames(fresh)}) {where}?"
            : $"{NoticeGlyphs.Commands}Copy {CustomCommandCount(replacing.Count)} {where}?";
        return replacing.Count == 0 ? head : $"{head} It replaces {replacing.Count.ToString(CultureInfo.InvariantCulture)} there: {ProfileSectionNames(replacing)}.";
    }

    /// <summary>A <c>commands</c> copy done: <c>(🪄 2 custom commands copied into "work"; 1 skipped, there already: /fix)</c>, <c>; 1 failed</c> after.</summary>
    public static string ProfileCustomCommandsCopiedNotice(IReadOnlyList<string> copied, IReadOnlyList<string> skipped, string profile, bool pull, IReadOnlyList<string>? links = null, int failed = 0)
    {
        ArgumentNullException.ThrowIfNull(copied);
        return ProfileCopySummary(NoticeGlyphs.Commands, CustomCommandCount(copied.Count), skipped, failed, profile, pull, links);
    }

    /// <summary>A <c>commands</c> copy stopped (Esc, the kill switch): the ones made before it stay.</summary>
    public static string ProfileCustomCommandsStoppedNotice(int copied, string profile, bool pull) =>
        ProfileCopyStoppedNotice(NoticeGlyphs.Commands, CustomCommandCount(copied), profile, pull);

    public static string ProfileCustomCommandFailedError(string name, string detail) => $"Could not copy {NameScrub.Display(name)}: {detail}";

    private static string CustomCommandCount(int count) => Counted(count, "custom command", "custom commands");

    /// <summary>A <c>skills</c> copy stopped (Esc, the kill switch): the ones made before it stay.</summary>
    public static string ProfileSkillsStoppedNotice(int copied, string profile, bool pull) =>
        ProfileCopyStoppedNotice(NoticeGlyphs.Skill, SkillCount(copied), profile, pull);

    /// <summary>One skill's copy that failed (the caps, the volume's room, a file failure, a linked folder).</summary>
    public static string ProfileSkillFailedError(string name, string detail) => $"Could not copy skill '{NameScrub.Display(name)}': {detail}";
}
