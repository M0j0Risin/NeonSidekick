namespace NeonSidekick.App;

/// <summary>
/// The interrupt's non-acoustic backstop.
/// An interruption that is followed by no request is the shape of the microphone hearing the
/// assistant rather than the user; two in a row switch interrupting off for the session, and the
/// screen says so. A request (or ESC, which is a human at the keyboard) clears the run. Pure:
/// the screen decides what to do with <see cref="Note"/>'s answer.
/// </summary>
internal sealed class InterruptTracker
{
    public const int Threshold = 2;

    /// <summary>Printed as a warning, once, when the threshold is reached. Pinned.</summary>
    public const string DisabledWarning =
        "Interrupting switched off for this session: the microphone keeps hearing the assistant. Headphones fix this; /stt interrupt on turns it back on.";

    /// <summary>The notice after an interruption that heard nothing. Pinned.</summary>
    public static string SilentHint(int silentInARow) => $"({NoticeGlyphs.Stt}heard nothing — {silentInARow} of {Threshold})";

    /// <summary>Interruptions in a row that produced no request.</summary>
    public int SilentInARow { get; private set; }

    /// <summary>Whether the threshold was reached since the last <see cref="Reset"/>.</summary>
    public bool Disabled { get; private set; }

    /// <summary>Records one interruption's outcome. True exactly once, when the threshold is reached.</summary>
    public bool Note(bool hadRequest)
    {
        if (hadRequest)
        {
            SilentInARow = 0;
            return false;
        }

        SilentInARow++;
        if (SilentInARow >= Threshold && !Disabled)
        {
            Disabled = true;
            return true;
        }

        return false;
    }

    /// <summary>Starts over; the session re-enabled interrupting.</summary>
    public void Reset()
    {
        SilentInARow = 0;
        Disabled = false;
    }
}
