namespace NeonSidekick.Audio;

/// <summary>
/// The microphone permission on macOS (2026-10-07, sound on a Mac). macOS asks once per <em>terminal app</em>, not per
/// NeonSidekick: the first recording from a terminal brings up "“Terminal” would like to access the microphone", and the
/// answer covers everything run in it. A refusal does not fail anything: an AudioQueue input opens, starts and delivers
/// buffers of exact zeros, so a voice feature would sit there hearing nothing. Two checks turn that into a sentence:
/// <see cref="Refusal"/> before recording (the status read through <c>AVCaptureDevice</c>), and <see cref="SilenceWatch"/>
/// while recording, which also catches what no status shows — a MacBook with its lid closed, whose own microphone is
/// switched off and sends the same exact zeros with the permission given (found the same day on the user's M4 in clamshell).
/// The values are <c>AVAuthorizationStatus</c>'s; the decisions are here, pure, and the native read is
/// <see cref="AudioQueueNative.MicrophoneAuthorization"/>.
/// </summary>
public static class MicrophoneAccess
{
    public const long NotDetermined = 0;
    public const long Restricted = 1;
    public const long Denied = 2;
    public const long Authorized = 3;

    /// <summary>The sentence that stops a recording before it starts, or null when it may go ahead (allowed, not asked yet, or unknown).</summary>
    public static string? Refusal(long? status, string terminal) => status switch
    {
        Denied => MicrophoneText.Denied(terminal),
        Restricted => MicrophoneText.Restricted,
        _ => null,
    };

    /// <summary>
    /// What a recording of nothing but exact zeros means, read against the status as it is now: refused, restricted, or
    /// (allowed) a microphone that is off. Null while the question is still on screen (not determined): the zeros are the
    /// wait for the answer, and the watch starts over.
    /// </summary>
    public static string? SilenceVerdict(long? status, string terminal) => status switch
    {
        NotDetermined => null,
        Denied => MicrophoneText.Denied(terminal),
        Restricted => MicrophoneText.Restricted,
        _ => MicrophoneText.Silent,
    };
}
