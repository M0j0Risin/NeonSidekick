using System.Globalization;
using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Updates;

/// <summary>Where the update stands on this screen, for the marker and <c>/update</c>'s branches.</summary>
public abstract record UpdateScreenState
{
    /// <summary>Nothing known or under way.</summary>
    public static readonly UpdateScreenState Nothing = new None();

    /// <summary>See <see cref="Nothing"/>.</summary>
    public sealed record None : UpdateScreenState;

    /// <summary>A new version is out: <c>📦 0.5.7</c>.</summary>
    public sealed record Offered(string Version) : UpdateScreenState;

    /// <summary><c>📦 0.5.7 42%</c>.</summary>
    public sealed record Downloading(string Version, int Percent) : UpdateScreenState;

    /// <summary>Verifying and test-running: <c>📦 0.5.7 checking</c>.</summary>
    public sealed record Checking(string Version) : UpdateScreenState;

    /// <summary>Verified and ready: <c>📦 0.5.7 at quit</c>.</summary>
    public sealed record Ready(string Version) : UpdateScreenState;

    /// <summary>A pending rollback: <c>📦 back to 0.5.6 at quit</c>.</summary>
    public sealed record GoingBack(string Target) : UpdateScreenState;
}

/// <summary>The update's words (2026-11-04, self-update): the transcript lines, the marker, the refusals and the log's lines. Pure statics, one source for the screen, the exit and the tests.</summary>
public static class UpdateText
{
    /// <summary>The Releases page, the way out of every refusal: updating by hand always works.</summary>
    public const string ReleasesUrl = AboutText.RepositoryUrl + "/releases";

    public const string ManifestUnreadable = "its update manifest can't be read";

    public const string AnswerUnreadable = "GitHub's answer can't be read";

    public const string RateLimited = "GitHub is limiting requests from this address; try again in a while";

    public const string NoReleaseFound = "GitHub lists no release";

    public const string NoAnswerInTime = "GitHub didn't answer in time";

    public const string NotAPackage = "This copy wasn't installed from a release package, so it can't update itself.";

    public const string NotSigned = "this release isn't signed by a key this copy trusts, so it won't be installed";

    public const string StageDisplay = "the update";

    public const string StageChanged = "the downloaded update changed after it was checked, so it was dropped";

    public const string InstallChanged = "the installed files changed after the update was downloaded, so it was dropped";

    public const string AnotherCopyStaging = "another copy is already downloading this update";

    public const string Stalled = "the download stalled with no data for 60 seconds; /update tries again and picks up where it stopped";

    public const string ProbeTimedOut = "the new version didn't finish its test run in 30 seconds, so it was dropped. Antivirus can cause this";

    public const string ProbeFailed = "the new version failed its test run, so it was dropped. Antivirus can cause this";

    public const string NothingToGoBackTo = "There's no earlier version to go back to.";

    public const string WaitingLine = "Waiting for an update to finish...";

    public const string WaitRanOut = "Waited for an update to finish and it hasn't; starting anyway, without the lock.";

    public const string OtherCopyOpen = "Another copy is running from this folder, so the update waits for a later exit.";

    public const string RecheckOutOfTime = "No time left to recheck the update at this close; it waits for the next exit.";

    /// <summary>The command's word, what a double-click on the marker runs.</summary>
    public const string Word = "/update";

    public const string NotesWord = "notes";

    public const string RollbackWord = "rollback";

    public const string CancelWord = "cancel";

    public const string NotesNote = "the release notes of the version running";

    public const string RollbackNote = "go back to the earlier version when you quit";

    public const string CancelNote = "stop a download, or drop a stage or a pending rollback";

    public const string Usage = "Usage: /update, /update notes, /update rollback or /update cancel";

    /// <summary>The marker's glyph on the lower rule.</summary>
    public const string MarkerGlyph = "📦";

    public const string CheckingSpinner = "Checking for updates...";

    public const string PaneLabel = "Update";

    public const string PaneTab = "NeonSidekick";

    public const string NoNotesInRelease = "This release has no notes.";

    public const string CantAskHere = "This terminal can't show the Install button, so /update can't install from here. " + ReleasesUrl;

    public const string CancelByCommand = "/update cancel stops it.";

    public const string CanceledLine = "Update canceled.";

    public const string NothingToCancel = "No update is under way, so there's nothing to cancel.";

    public const string CancelDownloadFirst = "An update is downloading. /update cancel stops it first.";

    public const string RollbackWarning = "Going back can turn off sessions, the shopping list or skill records a newer version changed, and settings the old version doesn't know go back to their defaults. Updating again brings them back.";

    /// <summary>Install, on the offer's pane: <c>i</c>, as the plan's stray-key note expects.</summary>
    public static readonly InfoAction InstallAction = new("Install", 'i', "install");

    public static readonly InfoAction LaterAction = new("Later", 'l', "later");

    public static readonly InfoAction CancelAction = new("Cancel update", 'c', "cancel update");

    public static readonly InfoAction KeepAction = new("Keep", 'k', "keep");

    public static string IsOutLine(string version) => $"NeonSidekick {version} is out. /update to see what's new and install it.";

    public static string DownloadingLine(string version, string size) => $"Downloading NeonSidekick {version} ({size}). It installs when you quit; /update cancel stops it.";

    public static string ReadyLine(string version) => $"NeonSidekick {version} is ready and installs when you quit.";

    public static string GoingBackLine(string version) => $"Going back to {version} when you quit.";

    public static string AlreadyGoingBack(string version) => $"Going back to {version} is already pending. /update cancel takes it back.";

    public static string LatestLine(string version) => $"You're on the latest version, {version}.";

    public static string UnsignedLine(string version) => $"NeonSidekick {version} is out, but it isn't signed, so it won't be installed. {ReleasesUrl}";

    public static string NoPackageLine(string version) => $"NeonSidekick {version} is out, with no package for this system. {ReleasesUrl}";

    public static string CheckFailedLine(string reason) => $"Couldn't check for updates: {reason}. {ReleasesUrl}";

    public static string StageFailedLine(string reason) => $"The update stopped: {reason}. {ReleasesUrl}";

    public static string RollbackRefusedLine(string reason) => $"Can't go back: {reason}. {ReleasesUrl}";

    public static string NoNotesSaved(string version) => $"No release notes are saved for {version}. {ReleasesUrl}";

    public static string NotesHeading(string version) => $"What's new in {version}:";

    public static string PaneHeading(string version, string size) => $"NeonSidekick {version} ({size})";

    /// <summary>What the offer to cancel says is under way.</summary>
    public static string UnderWay(UpdateScreenState state) => state switch
    {
        UpdateScreenState.Downloading d => $"NeonSidekick {d.Version} is downloading.",
        UpdateScreenState.Checking c => $"NeonSidekick {c.Version} is being checked.",
        UpdateScreenState.Ready r => $"NeonSidekick {r.Version} is ready and installs when you quit.",
        UpdateScreenState.GoingBack b => $"Going back to {b.Target} when you quit.",
        _ => "",
    };

    public static string VersionOf(UpdateScreenState state) => state switch
    {
        UpdateScreenState.Offered o => o.Version,
        UpdateScreenState.Downloading d => d.Version,
        UpdateScreenState.Checking c => c.Version,
        UpdateScreenState.Ready r => r.Version,
        UpdateScreenState.GoingBack b => b.Target,
        _ => "",
    };

    /// <summary>The marker on the lower rule, or empty. An "is out" marker follows the current profile's setting; what's under way shows whatever the setting says.</summary>
    public static string Marker(UpdateScreenState state, bool checkOn) => state switch
    {
        UpdateScreenState.Offered o => checkOn ? $"{MarkerGlyph} {o.Version}" : "",
        UpdateScreenState.Downloading d => string.Create(CultureInfo.InvariantCulture, $"{MarkerGlyph} {d.Version} {d.Percent}%"),
        UpdateScreenState.Checking c => $"{MarkerGlyph} {c.Version} checking",
        UpdateScreenState.Ready r => $"{MarkerGlyph} {r.Version} at quit",
        UpdateScreenState.GoingBack b => $"{MarkerGlyph} back to {b.Target} at quit",
        _ => "",
    };

    /// <summary>The check's User-Agent: names the app and its version, and nothing about the user.</summary>
    public static string UserAgent(string version) => "NeonSidekick/" + version;

    public static string ManifestUnknownFormat(int format) => string.Create(CultureInfo.InvariantCulture, $"it uses a format this version doesn't know ({format})");

    public static string ManifestBadEntry(string path) => $"its update manifest has a bad entry ({path})";

    public static string ManifestNotThisRelease(string version, string rid) => $"its manifest says {version} for {rid}";

    public static string ServerAnswered(int status) => string.Create(CultureInfo.InvariantCulture, $"GitHub answered {status}");

    public static string JournalRejected(string problem) => $"Ignored and deleted an update journal: {problem}.";

    public static string JournalNotDeleted(string problem) => $"Couldn't replace or delete the update journal: {problem}.";

    public static string JournalNotThisVersion(string source, string target, string running) => $"it goes from {source} to {target}, and this is {running}";

    public static string SwapStopped(string path, string problem) => $"The swap stopped at {path}: {problem}. Undoing it.";

    public static string UndoStopped(string path, string problem) => $"The undo stopped at {path}: {problem}. The next start tries again.";

    public static string LeftoverInUse(string path) => $"{path} is still in use";

    public static string CleanupLeft(string path, string problem) => $"Left {path} for next time: {problem}.";

    public static string DamagedInstall(string problem) => $"This install can't update itself: {problem}.";

    public static string RenamedCopy(string exe) => $"This copy has been renamed, so it can't update itself. Run {exe} to update.";

    public static string ReadOnlyFolder(string problem) => $"This folder can't take the update: {problem}";

    public static string NotThisRelease(string archive) => $"the signed file doesn't name {archive}, so it won't be installed";

    public static string PackageBad(string path) => $"the package's {path} is missing or doesn't match, so it was dropped";

    public static string PackageRefused(string problem) => $"the package was refused: {problem}";

    public static string ProbeFailedOn(string library) => $"{library} failed to load from the new version, so it was dropped. Antivirus can cause this";

    public static string LibraryFailsInBoth(string library) => $"{library} doesn't load here from the installed version either; ignored.";

    public static string NoLongerNewer(string version) => $"{version} is no longer newer than this version, so it was dropped";

    public static string OldVersionNotSigned(string version) => $"{version} isn't signed by a key this copy trusts, so it won't go back to it";

    public static string OldVersionAltered(string path) => $"the kept copy of {path} has been altered, so it won't go back";

    public static string OldVersionIncomplete(string path) => $"the earlier version's {path} isn't kept, so going back would leave a mix of versions";

    public static string StartStepFailed(string problem) => $"The update's start step failed: {problem}.";

    public static string StageDroppedOld(string version) => $"Dropped a stage of {version}: it's no newer than this version.";

    public static string StartAgainLine(string version) => $"NeonSidekick was just updated to {version}. Start it again.";

    public static string Installing(string version) => $"Installing {version}...";

    public static string InstalledLine(string version) => $"Done. {version} runs at next launch.";

    public static string UpdatedLine(string version) => $"Updated to {version}. /update notes shows what changed.";

    public static string BackOnLine(string version) => $"Back on {version}.";

    public static string FailedLine(string reason) => $"The update didn't install: {reason}. {ReleasesUrl}";

    public static string WindowCloseFailed(string reason) => $"The update didn't install when the window closed: {reason}. {ReleasesUrl}";
}
