namespace NeonSidekick.Computers;

/// <summary>
/// The shared computers layer's strings (2026-10-15, the admin-tools plan: the event logs' config and catalog sentences made a family's): a
/// file's problems, a computer the call cannot use, the write refusals each family's order gives. Each takes the family's words
/// (<see cref="ComputerFamilyInfo"/>), so services and tasks say the same thing their own way. Pinned by the tests.
/// </summary>
public static class ComputerText
{
    /// <summary>
    /// The longest program or command line a model's change shows in its question (2026-10-20, a review: the pane cut a long one at its
    /// width, so the user approved what they never saw). Past it, the model's change is refused; the user's own still asks.
    /// </summary>
    public const int MaxShownInQuestion = 600;

    /// <summary>
    /// Names as a refusal lists them, each in single quotes (2026-10-15, the user's report: a model read "the ticked ones are: lab." as
    /// the name <c>lab.</c>, the sentence's full stop and all). Pinned.
    /// </summary>
    public static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => "'" + n + "'"));

    /// <summary>What <c>local</c> is called where a host would stand.</summary>
    public const string ThisPc = "this PC";

    // ── Config ───────────────────────────────────────────────────────────────

    public const string BlankName = "an entry has a blank name";

    public const string NoHost = "\"host\" is required";

    public const string LocalTakesNoHost = "\"local\" is this PC: it takes no \"host\", \"auth\", \"user\" or password";

    public static string BadAuth(string word) => $"\"auth\" is '{word}'; it must be windows or runas";

    public static string BadDetail(string word) => $"\"detail\" is '{word}'; it must be full or redacted";

    public static string BadAction(string word, ComputerFamilyInfo family) => $"\"controlActions\" has '{word}'; the words are {string.Join(", ", family.ActionWords)}";

    public const string RunAsNoUser = "runas needs the account in \"user\"";

    /// <summary>A runas entry whose host is this PC (2026-10-23, J6): the sign-in would not be used. Pinned.</summary>
    public const string RunAsOnThisPc = "runas does not apply to this PC (Windows uses the app's own account for it); use the built-in \"local\" entry, or auth windows";

    public const string CredmanWithWindows = "\"passwordStore\": \"credman\" is only for runas";

    public static string UnreadableFile(string detail) => $"the file could not be read: {detail}";

    /// <summary>An entry key the family does not know (2026-10-23, the admin review's H5, DA1), with the nearest known one. Pinned.</summary>
    public static string UnknownKey(string key, string? nearest) =>
        $"\"{Short(key)}\" is not a key this entry takes" + (nearest is null ? "" : $"; did you mean \"{nearest}\"?") + "; the entry is not used until it is fixed";

    /// <summary>A file-level key beside the section (DA1): listed, the entries still load.</summary>
    public static string UnknownFileKey(string key, string section) => $"\"{Short(key)}\" is not a key this file takes (the entries go under \"{section}\"); it is ignored";

    /// <summary>
    /// A <c>credential</c> outside the family's own prefix (2026-10-23, the admin review's N2, decision DA2: any generic entry could be named,
    /// so another program's secret went to the entry's host, and set-password overwrote it). Pinned.
    /// </summary>
    public static string CredentialOutsidePrefix(string prefix) =>
        $"\"credential\" must start with {prefix} (the app's own entries in {Sql.SqlText.CredentialStore}); another program's is never read";

    /// <summary>
    /// A stored credential whose own user is not the entry's (DA2, V1: profiles share the default target, so one profile's password signed
    /// another's account in and locked it out). No sign-in is tried. Pinned.
    /// </summary>
    public static string CredentialUserMismatch(string target, string stored, string user) =>
        $"the password stored as {target} is for {Short(stored)}, not {Short(user)}, so it was not used; set this entry's password again";

    /// <summary>
    /// The entry was written but its password could not be stored (2026-10-23, the admin review's H9): it is left unticked, so nothing signs in
    /// with no password or an old one. Pinned.
    /// </summary>
    public static string SavedUnticked(string name, string setPasswordRow, string menuPath) =>
        $"'{name}' was saved but is not ticked, since its password was not stored; set it with {setPasswordRow} on {menuPath}, then tick it.";

    /// <summary>A name given twice in one file (2026-10-23, the admin review's Q1: <c>UncText.NamedTwice</c>'s rule for every family). Pinned.</summary>
    public const string NamedTwice = "this name is given to more than one entry in the file (names ignore case and spaces), so neither is used; rename or remove one";

    /// <summary>An entry holding one key twice (C7: the reader keeps the last, a password write found the first). Pinned.</summary>
    public static string KeyTwice(string key) => $"\"{Short(key)}\" is given twice in this entry, so it is not used; remove one";

    private static string Short(string text) => Files.NameScrub.Display(text.Length <= 40 ? text : Files.TextCut.Safe(text, 40) + "…");

    public static string NoPassword(string name, ComputerFamilyInfo family) => $"no password for '{name}'; set it with {family.SetPasswordRow} on {family.MenuPath}";

    // DP1 (2026-10-23, the admin review's L4, L5): a refusal never hands the model the key that widens its own access, nor a ready
    // cmdkey line (run_command hung on its prompt, or the model asked for the password in the transcript); it says what the user can do.
    public static string NoCredential(string target, ComputerFamilyInfo family) =>
        $"no password in {Sql.SqlText.CredentialStore} for {target}; the user can set it with {family.SetPasswordRow} on {family.MenuPath}";

    public static string CannotSignIn(string name, string reason) => $"Error: cannot sign in to '{name}': {reason}.";

    public static string ProfilesUnlistedLogLine(string file, string folder, string detail) => $"The profiles under {folder} could not be listed, so only the home's {file} was read: {detail}";

    // ── Picking a computer ───────────────────────────────────────────────────

    public static string UnknownComputer(string name, string offered, ComputerFamilyInfo family) =>
        $"Error: no computer named '{Llm.Tools.ArgumentText.Echo(name)}' is ticked in {family.OfferedSetting}" + (offered.Length > 0 ? $"; the ticked ones are: {offered}." : "; none is ticked.");

    public static string ComputerNeeded(string offered) => $"Error: several computers are ticked ({offered}); name one with computer.";

    public static string NoComputerTicked(ComputerFamilyInfo family) => $"Error: no computer is ticked in {family.OfferedSetting} ({family.MenuPath}).";

    public static string NotVisible(string name, string computer, ComputerFamilyInfo family) =>
        $"Error: '{Llm.Tools.ArgumentText.Echo(name)}' on {computer} is not among the {family.Things} offered there.";

    // ── Writes ───────────────────────────────────────────────────────────────

    /// <summary>The computer's entry does not say <c>access: readwrite</c>. Pinned.</summary>
    public static string ReadOnlyComputer(string computer, ComputerFamilyInfo family) =>
        $"Error: {computer} is read-only for {family.Things}; only the user can allow changes there ({family.MenuPath}).";

    /// <summary>The name matches no <c>control</c> pattern. Pinned.</summary>
    public static string NotInControl(string name, string computer, ComputerFamilyInfo family) =>
        $"Error: '{Llm.Tools.ArgumentText.Echo(name)}' on {computer} is not among what may be changed there, so it cannot be changed; only the user can allow it ({family.MenuPath}).";

    /// <summary>The action is not in the computer's <c>controlActions</c>. Pinned.</summary>
    public static string ActionNotAllowed(IReadOnlyList<string> allowed, ComputerFamilyInfo family) =>
        allowed.Count == 0
            ? $"Error: this computer allows no changes to {family.Things} (\"controlActions\" is empty)."
            : $"Error: this computer allows only {string.Join(", ", allowed)} for {family.Things}.";

    /// <summary>The user said no on the pane. Pinned.</summary>
    public const string Declined = "Error: the user declined the change; do not retry it.";

    /// <summary>The user's own no, as a pane says it back to them (2026-10-23, the admin review's T15: they were shown the model's "do not retry it"). Pinned.</summary>
    public const string DeclinedOnPane = "Not changed.";

    /// <summary>
    /// A change's answer as a pane shows it (2026-10-23, the admin review's T15): the user's no as <see cref="DeclinedOnPane"/>, "Error: " left
    /// to the red, and the model's "do not make it again" left out. <c>Failed</c> for a red line; <c>Changed</c> unless nothing was tried, so
    /// the list is read again after a change made in part, a restart left stopped or a wait stopped while it settled.
    /// </summary>
    public static (string Text, bool Failed, bool Changed) ForPane(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer == Declined)
        {
            return (DeclinedOnPane, false, false);
        }

        string text = answer.Replace(" Do not make it again.", "", StringComparison.Ordinal);
        bool failed = text.StartsWith("Error: ", StringComparison.Ordinal);
        return (failed ? text["Error: ".Length..] : text, failed, true);
    }

    /// <summary>No pane to ask on (headless, a bot). Pinned.</summary>
    public const string NoPaneToAsk = "Error: changes need the user's yes on the pane, and there is none here.";

    /// <summary>An audit line: who changed what, where, and how it went (Docker's shape). Pinned.</summary>
    public static string AuditLine(string who, string act, string computer, string outcome) => $"{who} {act} on {computer}: {outcome}";

    /// <summary>Who an audit line names for the model and for the user's own hand.</summary>
    public const string ByModel = "The model";

    public const string ByYou = "You";

    /// <summary>A wait the user ended (ESC, the page's Stop) during a change: it was not abandoned mid-call. Pinned.</summary>
    public const string StoppedWaiting = "Error: stopped waiting; the change may still complete.";

    /// <summary>
    /// A change that was made, whose settling the user stopped waiting for (2026-10-23, the admin review's T13: it was answered "may still
    /// complete", so a model ran a side-effecting change again). Pinned.
    /// </summary>
    public static string StoppedWaitingMade(string state) => $"The change was made; the wait for it to settle was stopped while it was {state}. Do not make it again.";

    /// <summary>
    /// A computer whose password for this account was refused earlier this run (2026-10-23, the admin review's F2: AD's hold, now every
    /// family's): nothing was tried, so the account is not locked out by retries. Pinned.
    /// </summary>
    public static string Held(string computer, string account) =>
        $"Error: {computer} refused the password for {account} earlier this run, so it was not tried again. Once the password is set again, the next call tries it.";

    /// <summary>Calls to the computer that never answered still wait (F2, B2); nothing more is started there for now. Pinned.</summary>
    public static string StillWaiting(string computer) => $"Error: earlier calls to {computer} still wait for an answer, so nothing more is started there for now; check that it is up, then try again.";
}
