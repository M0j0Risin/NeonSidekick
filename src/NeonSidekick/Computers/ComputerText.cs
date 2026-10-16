namespace NeonSidekick.Computers;

/// <summary>
/// The shared computers layer's strings (2026-10-15, the admin-tools plan: the event logs' config and catalog sentences made a family's): a
/// file's problems, a computer the call cannot use, the write refusals each family's order gives. Each takes the family's words
/// (<see cref="ComputerFamilyInfo"/>), so services and tasks say the same thing their own way. Pinned by the tests.
/// </summary>
public static class ComputerText
{
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

    public const string CredmanWithWindows = "\"passwordStore\": \"credman\" is only for runas";

    public static string UnreadableFile(string detail) => $"the file could not be read: {detail}";

    public static string NoPassword(string name, ComputerFamilyInfo family) => $"no password for '{name}'; set it with {family.SetPasswordRow} on {family.MenuPath}";

    public static string NoCredential(string target, ComputerFamilyInfo family) =>
        $"no password in {Sql.SqlText.CredentialStore} for {target}; set it with {family.SetPasswordRow} on {family.MenuPath}, or: {Sql.SqlText.CredentialCommand(target, "<user>")}";

    public static string CannotSignIn(string name, string reason) => $"Error: cannot sign in to '{name}': {reason}.";

    public static string ProfilesUnlistedLogLine(string file, string folder, string detail) => $"The profiles under {folder} could not be listed, so only the home's {file} was read: {detail}";

    // ── Picking a computer ───────────────────────────────────────────────────

    public static string UnknownComputer(string name, string offered, ComputerFamilyInfo family) =>
        $"Error: no computer named '{name}' is ticked in {family.OfferedSetting}" + (offered.Length > 0 ? $"; the ticked ones are: {offered}." : "; none is ticked.");

    public static string ComputerNeeded(string offered) => $"Error: several computers are ticked ({offered}); name one with computer.";

    public static string NoComputerTicked(ComputerFamilyInfo family) => $"Error: no computer is ticked in {family.OfferedSetting} ({family.MenuPath}).";

    public static string NotVisible(string name, string computer, ComputerFamilyInfo family) =>
        $"Error: '{name}' on {computer} is not among its \"{family.VisibleKey}\" in {family.FileName}.";

    // ── Writes ───────────────────────────────────────────────────────────────

    /// <summary>The computer's entry does not say <c>access: readwrite</c>. Pinned.</summary>
    public static string ReadOnlyComputer(string computer, ComputerFamilyInfo family) =>
        $"Error: {computer} is read-only for {family.Things}; its entry in {family.FileName} needs \"access\": \"readwrite\" to change anything.";

    /// <summary>The name matches no <c>control</c> pattern. Pinned.</summary>
    public static string NotInControl(string name, string computer, ComputerFamilyInfo family) =>
        $"Error: '{name}' on {computer} is not among its \"control\" patterns in {family.FileName}, so it cannot be changed.";

    /// <summary>The action is not in the computer's <c>controlActions</c>. Pinned.</summary>
    public static string ActionNotAllowed(IReadOnlyList<string> allowed, ComputerFamilyInfo family) =>
        allowed.Count == 0
            ? $"Error: this computer allows no changes to {family.Things} (\"controlActions\" is empty)."
            : $"Error: this computer allows only {string.Join(", ", allowed)} for {family.Things}.";

    /// <summary>The user said no on the pane. Pinned.</summary>
    public const string Declined = "Error: the user declined the change; do not retry it.";

    /// <summary>No pane to ask on (headless, a bot). Pinned.</summary>
    public const string NoPaneToAsk = "Error: changes need the user's yes on the pane, and there is none here.";

    /// <summary>An audit line: who changed what, where, and how it went (Docker's shape). Pinned.</summary>
    public static string AuditLine(string who, string act, string computer, string outcome) => $"{who} {act} on {computer}: {outcome}";

    /// <summary>Who an audit line names for the model and for the user's own hand.</summary>
    public const string ByModel = "The model";

    public const string ByYou = "You";

    /// <summary>A wait the user ended (ESC, the page's Stop) during a change: it was not abandoned mid-call. Pinned.</summary>
    public const string StoppedWaiting = "Error: stopped waiting; the change may still complete.";
}
