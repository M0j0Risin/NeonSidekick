using System.Text.Json;
using NeonSidekick.Advisors;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── An advisor's Change the server and Switch to profile (2026-11-22, the user's asks from UI testing) ──

internal sealed partial class SettingsMenu
{
    /// <summary>The <see cref="AdvisorEntryRows"/> row that changes the advisor profile's own server (2026-11-22, the user's ask).</summary>
    public const int ChangeAdvisorServerChoice = 3;

    /// <summary>The <see cref="AdvisorEntryRows"/> row that switches to the advisor's profile (2026-11-22, the user's ask and wording).</summary>
    public const int SwitchToAdvisorChoice = 4;

    /// <summary>Switch to profile's yes/no: the conversation cleared is what the yes costs (<see cref="SwitchedNotice"/>). Pinned.</summary>
    public static string SwitchToAdvisorQuestion(string profile) => $"Switch to profile {profile}? The conversation is cleared.";

    /// <summary>A <c>(missing)</c> advisor's Switch to profile and Change the server. Pinned.</summary>
    public static string MissingAdvisorProfileNotice(string profile) => $"({profile}'s profile no longer exists, so there is nothing to switch to)";

    public static string MissingAdvisorServerNotice(string profile) => $"({profile}'s profile no longer exists, so it has no server to change)";

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

    /// <summary>A write of the advisor's <c>profile.json</c> that failed. Pinned.</summary>
    public static string AdvisorServerFailedError(string profile, string detail) => $"Could not save {profile}'s server: {detail}";

    /// <summary>The notice after Change the server: the advisor's line as the band and a bare <c>/advisor</c> show it (<see cref="AdvisorText.Detail"/>). Pinned.</summary>
    public static string AdvisorServerNotice(AdvisorEntry entry, AdvisorServerFacts facts) =>
        FieldName(SettingsField.AdvisorList) + ": " + AdvisorText.Detail(entry, facts, exists: true);

    /// <summary>
    /// <c>/server</c>'s look-around under the settings given (2026-11-22, an advisor's Change the server): the screen's
    /// <c>LlmSession.ProbeServersAsync</c>, which reads only those settings. Null (the tests' menus) finds none.
    /// </summary>
    public Func<AppSettingsData, Uri?, CancellationToken, Task<IReadOnlyList<LlmServer>>>? AdvisorServerProbe { get; set; }

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
    /// over it, as <c>/profile push</c> writes another profile. The loaded profile and the session are never touched. True when written.
    /// </summary>
    private async Task<bool> ChangeAdvisorServerAsync(AdvisorEntry entry, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        if (Profiles.Resolve(home, entry.Profile) is not { } onDisk)
        {
            Sink.Notice(MissingAdvisorServerNotice(entry.Profile));
            return false;
        }

        // The saved values, never effective ones: an advisor answers on what its profile saves (AdvisorHost.Read).
        if (ReadAdvisorProfile(home, onDisk, entry.Profile) is not { } saved)
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

        string crumb = Crumb(FieldName(SettingsField.AdvisorList) + " › " + entry.Profile + " › " + AdvisorEntryRows[ChangeAdvisorServerChoice]);
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
                string current = string.Equals(savedUrl, url, StringComparison.Ordinal) ? saved.LlmModel ?? "" : "";
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

            reasoning = await ChooseReasoningAsync(saved.LlmReasoning ?? "", cancellationToken, crumb + " › reasoning", close: false).ConfigureAwait(false);
        }

        // Read again just before the write, with the first read's checks: the picks can take a while, and the profile may be loaded and
        // saving in another instance (/profile push's pattern), deleted or broken meanwhile.
        if (Profiles.Resolve(home, entry.Profile) is not { } again)
        {
            Sink.Notice(MissingAdvisorServerNotice(entry.Profile));
            return false;
        }

        if (ReadAdvisorProfile(home, again, entry.Profile) is not { } fresh)
        {
            return false;
        }

        fresh.LlmUrl = url;
        if (model is not null)
        {
            fresh.LlmModel = model;
        }

        if (reasoning is not null)
        {
            fresh.LlmReasoning = reasoning;
        }

        try
        {
            Profiles.WriteProfileFile(Profiles.ProfileFile(home, again), fresh);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Sink.Error(AdvisorServerFailedError(entry.Profile, Diagnostics.LogText.Excerpt(ex.Message)));
            return false;
        }

        Sink.Notice(AdvisorServerNotice(entry, AdvisorServer.Of(fresh)));
        return true;
    }

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
