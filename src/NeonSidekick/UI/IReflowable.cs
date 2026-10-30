namespace NeonSidekick.UI;

/// <summary>
/// A renderable the transcript may render again at another width (2026-10-29, the user's report: after a resize the banner and
/// the transcript stayed at the old width, or were cut by the character, while the pane came back clean). A flow write of one —
/// standalone, not a tool run's line, no pictures, nothing that folds — is kept with its renderable in the store, and a resize lays
/// it out afresh (<see cref="ScreenPane"/>, <see cref="Scrollback"/>). A type is marked only when it is pure: the same input renders
/// the same output, nothing read live at render time (the clock, a counter, <c>Theme.Current</c>). A wrapper marked here vouches for
/// what it wraps, so it is marked only where every caller builds it from parts that are pure too.
/// </summary>
public interface IReflowable
{
}
