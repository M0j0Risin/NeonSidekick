namespace NeonSidekick.Audio;

/// <summary>
/// A playback that speaks on a Web UI page rather than at the PC (2026-11-13, the webui-voice plan): the speech session keeps the page with
/// the speaker, so the screen can tell a page's tail from the PC's own (<c>SpeechSession.PlayingPage</c>).
/// </summary>
internal interface IPageAudio
{
    /// <summary>The page id the audio goes to.</summary>
    string Page { get; }
}
