using System.Text.Json;
using NeonSidekick.Advisors;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── An advisor's Change the server, model and reasoning, and Switch to profile (2026-11-22, the user's asks from UI testing) ──

internal sealed partial class SettingsMenu
{
    /// <summary>The <see cref="AdvisorEntryRows"/> row that changes the advisor profile's own server (2026-11-22, the user's ask).</summary>
    public const int ChangeAdvisorServerChoice = 3;

    /// <summary>The <see cref="AdvisorEntryRows"/> row that changes the advisor profile's model alone (2026-11-22, the user's ask).</summary>
    public const int ChangeAdvisorModelChoice = 4;

    /// <summary>The <see cref="AdvisorEntryRows"/> row that changes the advisor profile's reasoning alone (2026-11-22, the user's ask).</summary>
    public const int ChangeAdvisorReasoningChoice = 5;

    /// <summary>The <see cref="AdvisorEntryRows"/> row that switches to the advisor's profile (2026-11-22, the user's ask and wording).</summary>
    public const int SwitchToAdvisorChoice = 6;

    /// <summary>Switch to profile's yes/no: the conversation cleared is what the yes costs (<see cref="SwitchedNotice"/>). Pinned.</summary>
    public static string SwitchToAdvisorQuestion(string profile) => $"Switch to profile {profile}? The conversation is cleared.";

    /// <summary>A <c>(missing)</c> advisor's Switch to profile. Pinned.</summary>
    public static string MissingAdvisorProfileNotice(string profile) => $"({profile}'s profile no longer exists, so there is nothing to switch to)";

    /// <summary>A <c>(missing)</c> advisor's Change the server, model or reasoning (one notice for the three since 2026-11-22). Pinned.</summary>
    public static string MissingAdvisorChangeNotice(string profile) => $"({profile}'s profile no longer exists, so there is nothing to change)";

    /// <summary>
    /// An advisor entry naming the profile in use (2026-11-22: a <c>/profile new</c> seed keeps the loaded list, so a re-made advisor can
    /// list itself; or a hand edit): its <c>profile.json</c> written behind the loaded settings would be lost at their next save. Pinned.
    /// </summary>
    public static string AdvisorIsLoadedNotice(string profile) => $"({profile} is the profile in use: change it with /server, /model or /reasoning)";

    /// <summary>An advisor's <c>profile.json</c> that could not be read; nothing is written over it. Pinned.</summary>
    public static string AdvisorProfileReadError(string profile, string detail) => $"Could not read {profile}'s profile.json: {detail}";

    /// <summary>Change the server's look-around, said on the status line while it runs. Pinned.</summary>
    public static string AdvisorServerSearchNotice(string profile) => $"(looking for {profile}'s servers…)";

    /// <summary>No server answered under the advisor's own settings: its servers are switched on in its profile. Pinned.</summary>
    public static string AdvisorNoServerNotice(string profile) => $"(no server answered for {profile}; turn one on in its own settings)";

    /// <summary>
    /// Change the server's first row (2026-11-22, the user's pick): no URL saved, so the advisor borrows the session's server as it
    /// is (<c>LlmSession.LinkAsync</c>); <c>/server</c>'s list has no way back to that. Pinned.
    /// </summary>
    public const string AdvisorBorrowServerRow = "Your session's server (none saved)";

    /// <summary>Its band. Pinned.</summary>
    public const string AdvisorBorrowServerBand = "Answers on whatever server your own session is connected to, with this advisor's model when that server has it.";

    /// <summary>A write of the advisor's <c>profile.json</c> that failed (the server's, model's or reasoning's since 2026-11-22). Pinned.</summary>
    public static string AdvisorSaveFailedError(string profile, string detail) => $"Could not save {profile}'s profile: {detail}";

    /// <summary>The notice after Change the server or the model: the advisor's line as the band and a bare <c>/advisor</c> show it (<see cref="AdvisorText.Detail"/>). Pinned.</summary>
    public static string AdvisorServerNotice(AdvisorEntry entry, AdvisorServerFacts facts) =>
        FieldName(SettingsField.AdvisorList) + ": " + AdvisorText.Detail(entry, facts, exists: true);

    /// <summary>Change the model asking the advisor's server, said on the status line while it runs (2026-11-22). Pinned.</summary>
    public static string AdvisorModelSearchNotice(string profile) => $"(asking {profile}'s server for its models…)";

    /// <summary>Change the model with nothing to list: the reason, excerpted (2026-11-22). Pinned.</summary>
    public static string AdvisorNoModelsNotice(string profile, string why) => $"(no models for {profile}: {why})";

    /// <summary>The advisor's URL changed between the list and the write: the pick was the old server's, so nothing is written (2026-11-22). Pinned.</summary>
    public static string AdvisorChangedMeanwhileNotice(string profile) => $"({profile}'s server changed while you picked; open Change the model again)";

    /// <summary>The notice after Change the reasoning: the advisor's line shows no reasoning (2026-11-22). Pinned.</summary>
    public static string AdvisorReasoningNotice(string profile, string level) => FieldName(SettingsField.AdvisorList) + ": " + profile + "'s reasoning: " + level;

    /// <summary>
    /// After a pick the advisor's line won't name (2026-11-22): an embedded advisor under <c>Advisor embedded mode</c> <c>parent-server</c>
    /// answers on the model running, whatever it saves, so the notice says so rather than read as a failed save. Pinned.
    /// </summary>
    public static string AdvisorSharedModelNote(string running) =>
        $" (it answers on {running} while that runs: {FieldName(SettingsField.AdvisorEmbedded)} {BotChatMultiEmbedded.Default})";

    /// <summary>
    /// <c>/server</c>'s look-around under the settings given (2026-11-22, an advisor's Change the server): the screen's
    /// <c>LlmSession.ProbeServersAsync</c>, which reads only those settings. Null (the tests' menus) finds none.
    /// </summary>
    public Func<AppSettingsData, Uri?, CancellationToken, Task<IReadOnlyList<LlmServer>>>? AdvisorServerProbe { get; set; }

    /// <summary>
    /// The models an advisor's saved settings would answer on (2026-11-22, an advisor's Change the model): the screen's
    /// <c>LlmSession.ListModelsForAsync</c>, the list or the reason there is none. Null (the tests' menus) lists none.
    /// </summary>
    public Func<AppSettingsData, CancellationToken, Task<(ProbeResult? Listed, string? Problem)>>? AdvisorModelLister { get; set; }

    /// <summary>
    /// The model an advisor's saved settings answer on where that is known without asking (2026-11-22, the user's screenshot): the
    /// screen's <c>LlmSession.LinkedModelOf</c>, for the rows, the band and the notices. Null (the tests' menus) names the saved model.
    /// </summary>
    public Func<AppSettingsData, string?>? AdvisorLinkedModel { get; set; }

    /// <summary>
    /// Set by an advisor's Switch to profile (2026-11-22) once the profile switched: every loop the Advisor list sits under takes it
    /// (<see cref="TakeProfileSwitched"/>) and ends its visit with <see cref="SettingsChanges.Profile"/>, so the pane closes and the screen
    /// reconnects, as <c>/profile &lt;name&gt;</c> does. Cleared as each visit opens, so a flag no path took can't outlive its visit.
    /// </summary>
    private bool _profileSwitched;

    /// <summary>Whether an advisor's Switch to profile switched since the last take; clears it.</summary>
    internal bool TakeProfileSwitched()
    {
        bool switched = _profileSwitched;
        _profileSwitched = false;
        return switched;
    }

    /// <summary>
    /// The reply under the menu while another pane drives it (2026-11-22: <see cref="ToolsMenu"/>'s visits, which reach the Advisor list
    /// through the Advisors switch page): set for the visit, the one it replaced returned for the caller's <c>finally</c> to put back.
    /// <see cref="ShowAsync(CancellationToken, ReplyRun, SettingsField?, string?)"/> sets its own.
    /// </summary>
    internal ReplyRun UnderReply(ReplyRun reply)
    {
        var was = _reply;
        _reply = reply;
        return was;
    }

    /// <summary>
    /// <c>Switch to profile</c> on an advisor (2026-11-22, the user's ask): to the advisor's profile as <c>/profile &lt;name&gt;</c> switches,
    /// asked first here. Refused under a reply (as the Profile row) and for a profile that is gone; the name resolved again after the
    /// yes, as <c>/profile</c> does right before its switch (a name that no longer resolves would make the switch throw). True when it
    /// switched: the caller ends the visit.
    /// </summary>
    private async Task<bool> SwitchToAdvisorAsync(AdvisorEntry entry, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        if (_reply.Running)
        {
            Sink.Notice(NotWhileReplyRunsNotice);
            return false;
        }

        if (!Profiles.Exists(home, entry.Profile))
        {
            Sink.Notice(MissingAdvisorProfileNotice(entry.Profile));
            return false;
        }

        var asking = new MenuPage(SwitchToAdvisorQuestion(entry.Profile), ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
        if (await PickAsync(asking, 0, cancellationToken).ConfigureAwait(false) != 1)
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return false;
        }

        if (Profiles.Resolve(home, entry.Profile) is not { } resolved)
        {
            Sink.Notice(MissingAdvisorProfileNotice(entry.Profile));
            return false;
        }

        if (!await SwitchProfileAsync(resolved).ConfigureAwait(false))
        {
            return false;
        }

        _profileSwitched = true;
        return true;
    }

    /// <summary>
    /// <c>Change the server</c> on an advisor (2026-11-22, the user's ask): the servers the advisor's own saved settings offer, looked for as
    /// <c>/server</c> typed in that profile would (<see cref="AdvisorServerProbe"/>), with <see cref="AdvisorBorrowServerRow"/> first; then the
    /// model and the reasoning, as <c>/server</c> asks them, on pages that keep the pane open. Nothing is written until the last pick; then
    /// the advisor's <c>profile.json</c> read again (it may be loaded in another instance) and its server, model and reasoning written
    /// over it, as <c>/profile push</c> writes another profile (<see cref="WriteAdvisorProfile"/>). The loaded profile and the session are
    /// never touched. True when written.
    /// </summary>
    private async Task<bool> ChangeAdvisorServerAsync(AdvisorEntry entry, CancellationToken cancellationToken)
    {
        if (OpenAdvisorProfile(entry) is not { } saved)
        {
            return false;
        }

        string savedUrl = saved.LlmUrl?.Trim() ?? "";
        Uri? inUse = savedUrl.Length > 0 && Uri.TryCreate(savedUrl, UriKind.Absolute, out var parsed) ? parsed : null;
        IReadOnlyList<LlmServer> servers = [];
        if (AdvisorServerProbe is { } probe)
        {
            Sink.Notice(AdvisorServerSearchNotice(entry.Profile));
            servers = await probe(saved, inUse, cancellationToken).ConfigureAwait(false);
        }

        if (servers.Count == 0)
        {
            Sink.Notice(AdvisorNoServerNotice(entry.Profile));
        }

        string crumb = AdvisorActionCrumb(entry, ChangeAdvisorServerChoice);
        if (await PickServerAsync(servers, inUse, crumb, cancellationToken, saved.LlmModel, (AdvisorBorrowServerRow, AdvisorBorrowServerBand), close: false).ConfigureAwait(false) is not { } choice)
        {
            return false;
        }

        string url = "";
        string? model = null;   // null keeps the saved model
        string? reasoning = null;   // null keeps the saved level
        if (!ReferenceEquals(choice, LeadServerRow))
        {
            url = choice.BaseUrl.ToString();
            if (NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(choice.BaseUrl))
            {
                // The row is the model, as /server's embedded rows are: the sentinel URL and its one id (SaveEmbeddedModel's pair).
                model = EmbeddedRowId(choice);
            }
            else
            {
                // The advisor's model is offered only on its own server: /server clears the model on a new URL before its picker.
                string current = string.Equals(savedUrl, url, StringComparison.Ordinal) ? saved.LlmModel?.Trim() ?? "" : "";
                var (ids, cursor) = ModelChoices(choice.Result, current);
                if (ids.Count == 0)
                {
                    model = "";   // a Docker container or a server that listed none: its own default
                }
                else if (await ChooseModelFromListAsync(ids, cursor, cancellationToken, crumb + " › model", close: false).ConfigureAwait(false) is { } id)
                {
                    model = id;
                }
                else
                {
                    return false;
                }
            }

            reasoning = await ChooseReasoningAsync(AdvisorReasoningOf(saved).Level, cancellationToken, crumb + " › reasoning", close: false).ConfigureAwait(false);
        }

        if (WriteAdvisorProfile(entry, fresh =>
            {
                fresh.LlmUrl = url;
                if (model is not null)
                {
                    fresh.LlmModel = model;
                }

                if (reasoning is not null)
                {
                    fresh.LlmReasoning = reasoning;
                }
            }) is not { } written)
        {
            return false;
        }

        Sink.Notice(AdvisorServerNotice(entry, AdvisorServer.Of(written, AdvisorLinkedModel)) + SharedModelNoteFor(written));
        return true;
    }

    /// <summary>
    /// <c>Change the model</c> on an advisor (2026-11-22, the user's ask): the models its own saved settings would answer on
    /// (<see cref="AdvisorModelLister"/>, the link's own branches), its saved model under the cursor, on a page that keeps the pane open;
    /// the pick written to its <c>profile.json</c> alone. A list it can't have (a Docker container or a borrowed server running one model,
    /// a server that doesn't answer) is said instead. Esc, or the model already saved, writes and says nothing. True when written.
    /// </summary>
    private async Task<bool> ChangeAdvisorModelAsync(AdvisorEntry entry, CancellationToken cancellationToken)
    {
        if (OpenAdvisorProfile(entry) is not { } saved)
        {
            return false;
        }

        string url = saved.LlmUrl?.Trim() ?? "";
        if (!Claude.ClaudeCliEndpoint.IsClaudeCli(url) && !NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(url) && !NeonSidekick.Docker.DockerEndpoint.IsDocker(url))
        {
            Sink.Notice(AdvisorModelSearchNotice(entry.Profile));   // only where a server is asked: the rest answer at once
        }

        (ProbeResult? Listed, string? Problem) asked = AdvisorModelLister is { } lister ? await lister(saved, cancellationToken).ConfigureAwait(false) : (null, null);
        var (listed, problem) = asked;
        if (listed is not { ModelIds.Count: > 0 } list)
        {
            string why = problem ?? (listed is { } empty ? empty.Detail : LlmSession.BotLinkNoServer);
            Sink.Notice(AdvisorNoModelsNotice(entry.Profile, Diagnostics.LogText.Excerpt(why)));
            return false;
        }

        string current = saved.LlmModel?.Trim() ?? "";
        var (ids, cursor) = ModelChoices(list, current);
        if (await ChooseModelFromListAsync(ids, cursor, cancellationToken, AdvisorActionCrumb(entry, ChangeAdvisorModelChoice), close: false).ConfigureAwait(false) is not { } id
            || string.Equals(id, current, StringComparison.Ordinal))
        {
            return false;
        }

        // The pick is the listed server's: should another instance or a hand edit have moved the advisor to another server meanwhile, say so.
        bool moved = false;
        if (WriteAdvisorProfile(entry, fresh => fresh.LlmModel = id, fresh => !(moved = !string.Equals(fresh.LlmUrl?.Trim() ?? "", url, StringComparison.Ordinal))) is not { } written)
        {
            if (moved)
            {
                Sink.Notice(AdvisorChangedMeanwhileNotice(entry.Profile));
            }

            return false;
        }

        Sink.Notice(AdvisorServerNotice(entry, AdvisorServer.Of(written, AdvisorLinkedModel)) + SharedModelNoteFor(written));
        return true;
    }

    /// <summary>
    /// <c>Change the reasoning</c> on an advisor (2026-11-22, the user's ask): <c>/reasoning</c>'s levels, the one in force under the
    /// cursor, written to its <c>profile.json</c> alone. Esc, or the level in force, writes nothing; a saved word that is none of the
    /// levels is never in force, so any pick cleans it up. True when written.
    /// </summary>
    private async Task<bool> ChangeAdvisorReasoningAsync(AdvisorEntry entry, CancellationToken cancellationToken)
    {
        if (OpenAdvisorProfile(entry) is not { } saved)
        {
            return false;
        }

        var (level, known) = AdvisorReasoningOf(saved);
        if (await ChooseReasoningAsync(level, cancellationToken, AdvisorActionCrumb(entry, ChangeAdvisorReasoningChoice), close: false).ConfigureAwait(false) is not { } picked
            || (known && picked == level))
        {
            return false;
        }

        if (WriteAdvisorProfile(entry, fresh => fresh.LlmReasoning = picked) is null)
        {
            return false;
        }

        Sink.Notice(AdvisorReasoningNotice(entry.Profile, picked));
        return true;
    }

    /// <summary>An advisor action's page title: <c>Advisor list › architect › Change the model</c>.</summary>
    private string AdvisorActionCrumb(AdvisorEntry entry, int action) =>
        Crumb(FieldName(SettingsField.AdvisorList) + " › " + entry.Profile + " › " + AdvisorEntryRows[action]);

    /// <summary>
    /// The level <paramref name="saved"/> has in force, by its canonical name (the cursor's lookup is exact, and a hand-edited
    /// <c> Medium </c> parses), and whether it was one of the levels at all; an unknown word is <see cref="ReasoningLevel.Default"/>, not known.
    /// </summary>
    private static (string Level, bool Known) AdvisorReasoningOf(AppSettingsData saved) =>
        ReasoningLevel.TryParse(saved.LlmReasoning, out var effort) ? (ReasoningLevel.Name(effort), true) : (ReasoningLevel.Default, false);

    /// <summary>
    /// The advisor's saved settings for a change, or null with why said: a profile that is gone, the profile in use (its file written
    /// behind the loaded settings would be lost at their next save), or one that can't be read. The saved values, never effective ones:
    /// an advisor answers on what its profile saves (<c>AdvisorHost.Read</c>).
    /// </summary>
    private AppSettingsData? OpenAdvisorProfile(AdvisorEntry entry)
    {
        string home = _settings.StorageDirectory;
        if (Profiles.Resolve(home, entry.Profile) is not { } onDisk)
        {
            Sink.Notice(MissingAdvisorChangeNotice(entry.Profile));
            return null;
        }

        if (Profiles.NameEquals(onDisk, _settings.ProfileName))
        {
            Sink.Notice(AdvisorIsLoadedNotice(entry.Profile));
            return null;
        }

        return ReadAdvisorProfile(home, onDisk, entry.Profile);
    }

    /// <summary>
    /// The one write of an advisor's change (2026-11-22, shared by the server, the model and the reasoning): its <c>profile.json</c>
    /// resolved and read again just before the write, with the first read's checks (the picks can take a while, and the profile may be
    /// loaded and saving in another instance, /profile push's pattern, deleted or broken meanwhile), then <paramref name="change"/> made
    /// and the whole file written. <paramref name="stillValid"/> false on the fresh read writes nothing and says nothing (the caller
    /// does). The written settings, or null with why said.
    /// </summary>
    private AppSettingsData? WriteAdvisorProfile(AdvisorEntry entry, Action<AppSettingsData> change, Func<AppSettingsData, bool>? stillValid = null)
    {
        string home = _settings.StorageDirectory;
        if (Profiles.Resolve(home, entry.Profile) is not { } again)
        {
            Sink.Notice(MissingAdvisorChangeNotice(entry.Profile));
            return null;
        }

        if (ReadAdvisorProfile(home, again, entry.Profile) is not { } fresh || stillValid?.Invoke(fresh) == false)
        {
            return null;
        }

        change(fresh);
        try
        {
            Profiles.WriteProfileFile(Profiles.ProfileFile(home, again), fresh);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Sink.Error(AdvisorSaveFailedError(entry.Profile, Diagnostics.LogText.Excerpt(ex.Message)));
            return null;
        }

        return fresh;
    }

    /// <summary>
    /// <see cref="AdvisorSharedModelNote"/> after a write of an embedded model the advisor won't answer on (another runs under
    /// <c>parent-server</c>, <see cref="AdvisorLinkedModel"/>'s answer); "" otherwise.
    /// </summary>
    private string SharedModelNoteFor(AppSettingsData written) =>
        NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(written.LlmUrl) && !string.IsNullOrWhiteSpace(written.LlmModel)
        && AdvisorLinkedModel?.Invoke(written) is { Length: > 0 } linked
        && !string.Equals(linked.Trim(), written.LlmModel.Trim(), StringComparison.OrdinalIgnoreCase)
            ? AdvisorSharedModelNote(ModelNames.Short(linked))
            : "";

    /// <summary>Advisor <paramref name="profile"/>'s saved settings (on disk as <paramref name="onDisk"/>), or null with the read error said.</summary>
    private AppSettingsData? ReadAdvisorProfile(string home, string onDisk, string profile)
    {
        try
        {
            return Profiles.ReadProfileFile(Profiles.ProfileFile(home, onDisk));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Sink.Error(AdvisorProfileReadError(profile, Diagnostics.LogText.Excerpt(ex.Message)));
            return null;
        }
    }
}
