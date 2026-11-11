using NeonSidekick.App;

namespace NeonSidekick.Help;

public static partial class HelpMac
{
    /// <summary>
    /// The band's short texts that differ on a Mac (2026-11-08, with <see cref="HelpSettings.Brief"/>): only the rows a Mac shows
    /// whose brief would otherwise be untrue there. Most briefs are written to hold on both systems, so this stays short.
    /// </summary>
    public static readonly IReadOnlyDictionary<SettingsField, string> Briefs = new Dictionary<SettingsField, string>
    {
        [SettingsField.EmbeddedBackend] = "Which llama.cpp build runs the model. A Mac has one, Metal, on the Apple Silicon GPU: auto and metal both pick it.",
        [SettingsField.LlmOneLocalModel] = "Picking another server unloads the embedded model first. Off, it stays loaded and the memory is yours to manage.",
        [SettingsField.DockerServers] = "Not available on a Mac: Docker Desktop there passes no GPU to containers, so they cannot serve a model. The embedded model runs on Metal instead.",
        [SettingsField.ShellDefault] = "The shell run_command uses when the call names none: zsh, bash or powershell (pwsh, when installed).",
        [SettingsField.FileDeletesToRecycleBin] = "Sends what the app deletes to the Trash, where it can be restored, except on network or FAT volumes. Off, they delete directly. A shell command's own deletes never follow it.",
        [SettingsField.DockerEnginePipe] = "The Docker engine's socket, such as /var/run/docker.sock. Empty finds it the way the docker command does.",
        [SettingsField.NetworkCertificateStores] = "Also offers net_certs, which lists your login keychain or the system's trusted roots (subjects, issuers, end dates). Nothing is exported.",
        [SettingsField.EventLogTools] = "Gives the model read-only tools over this Mac's own logs (the unified log, crash reports, install.log) and .logarchive folders in the working directory.",
        [SettingsField.EventLogComputersOffered] = "What the event log tools may read: local, this Mac's own logs. Untick it and they read only the working directory's .logarchive folders.",
        [SettingsField.EventLogSetPassword] = "A runas computer's password, for reading a Windows computer's logs. A Mac reads none, so there is nothing to sign in with.",
        [SettingsField.EventLogAddComputer] = "A wizard for an eventlog.json entry. A Mac reads only local's entry, so pick local to choose its logs and detail.",
        [SettingsField.DirectoryDomainsOffered] = "A checklist of the domains the directory tools may read: each domain in both ad.json files. Until you tick one, none is read.",
        [SettingsField.PdfEngine] = "The PDF maker: auto and browser use Edge, Chrome, Brave or Chromium from Applications; printer isn't available on a Mac. Editing PDFs needs the browser.",
        [SettingsField.WebUiHttpsAddress] = "The https address of a proxy, such as Tailscale Serve's, that /webui offers first. auto and off show none on a Mac. Its link holds the sign-in token, so type only one that reaches this Mac.",
    };

    /// <summary><paramref name="field"/>'s brief on a Mac, or null where <see cref="HelpSettings.BriefAsWritten"/> serves both.</summary>
    public static string? Brief(SettingsField field) => Briefs.TryGetValue(field, out string? text) ? text : null;
}
