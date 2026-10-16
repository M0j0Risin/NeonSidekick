using System.Globalization;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// The directory's visible strings (2026-10-15, the admin-tools plan): the log category, the glyph, the menu path, the refusals, the cards'
/// words, the error sentences (<see cref="DirectoryErrors"/>) and <c>/ad</c>'s words. Pinned by the tests.
/// </summary>
public static class DirectoryText
{
    public const string Category = "Directory";

    /// <summary>The group's glyph: an office building (👥 is botchat's resume, 🪪 the profile's).</summary>
    public const string Glyph = "🏢";

    public const string MenuPath = "/tools › Admin › Directory";

    public const string Working = "Reading the directory…";

    /// <summary>This PC's own domain when joined: built in, reserved in <c>ad.json</c>.</summary>
    public const string CurrentName = "current";

    /// <summary>The refusal off Windows. Pinned.</summary>
    public const string NoneHere = "Error: there is no directory to read here.";

    public const string NoneHereReason = "there is no directory to read here";

    /// <summary>Why the group is not offered on an unjoined PC with no domain ticked. Pinned.</summary>
    public const string NoDomainReason = "no domain is configured";

    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n', StringComparison.Ordinal);
        string first = newline < 0 ? result : result[..newline];
        return "(" + Glyph + " " + (first.Length > 160 ? first[..159] + "…" : first) + ")";
    }

    public static string WithheldFromAdvisor(string tool, string? name) =>
        "(" + tool + (string.IsNullOrWhiteSpace(name) ? "" : " " + name.Trim()) + ": read it with your own directory tools)";

    // ── Refusals of a call ──────────────────────────────────────────────────

    public static string Missing(string argument) => $"Error: {argument} is required.";

    public static string NotBoolean(string argument, string raw) => $"Error: {argument} '{raw}' is not true or false.";

    public static string OutOfRange(string argument, string given, int min, int max) =>
        $"Error: {argument} '{given}' is out of range; it must be {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.";

    public static string BadKind(string word) => $"Error: kind '{word}' is not locked, password_expiring, disabled, stale_users, stale_computers or name.";

    public const string WordsNeeded = "Error: kind name needs words to look for.";

    public static string NotFound(string what, string name, string domain) => $"Error: no {what} named '{name}' in {domain}.";

    /// <summary>A name that matches several objects: the candidates listed. Pinned.</summary>
    public static string Ambiguous(string name, IReadOnlyList<string> candidates) =>
        $"Error: '{name}' matches {candidates.Count.ToString(CultureInfo.InvariantCulture)} accounts; name one by its account name:\n  " + string.Join("\n  ", candidates);

    public static string TimedOut(string domain) => $"Error: the search in {domain} took too long and was stopped.";

    // ── Domains (ad.json) ────────────────────────────────────────────────────

    public const string NoDomain = "\"domain\" is required";

    public const string CurrentReserved = "\"current\" is this PC's own domain, built in; name the entry otherwise";

    public static string BadBaseDn(string text) => $"\"baseDn\" is '{text}'; it must be a distinguished name such as DC=corp,DC=example";

    public static string UnknownDomain(string name, string offered) =>
        $"Error: no domain named '{name}' is ticked in Directory domains offered" + (offered.Length > 0 ? $"; the ticked ones are: {offered}." : "; none is ticked.");

    public static string DomainNeeded(string offered) => $"Error: several domains are ticked ({offered}); name one with domain.";

    public const string NoDomainTicked = "Error: no domain is ticked in Directory domains offered (" + MenuPath + ").";

    public static string NoPassword(string name) => $"no password for '{name}'; set it with Directory set password on {MenuPath}";

    public static string NoCredential(string target) =>
        $"no password in {Sql.SqlText.CredentialStore} for {target}; set it with Directory set password on {MenuPath}, or: {Sql.SqlText.CredentialCommand(target, "<user>")}";

    public static string TestOk(string name, string server, string domain, int users) =>
        $"Reached '{name}' at {server}: {domain}, {users.ToString(CultureInfo.InvariantCulture)} user read.";

    // ── Cards ────────────────────────────────────────────────────────────────

    /// <summary>The note beside a last sign-in. Pinned.</summary>
    public const string StaleNote = "lastLogonTimestamp; up to about 14 days stale";

    /// <summary>A locked user's pointer to where the lockout's source machine is recorded. Pinned.</summary>
    public static string LockoutSource(string pdc) => $"the source machine is in Security event 4740 on the PDC emulator, {pdc}";

    /// <summary>The policy when no fine-grained one is named (Samba may not compute it). Pinned.</summary>
    public const string DomainPolicyMaybe = "the domain policy (a fine-grained policy may apply)";

    public const string MustChange = "must change at next sign-in";

    public const string NeverWord = "never";

    public const string ForeverWord = "until an administrator unlocks it";

    /// <summary>A scan the 1,000-entry cap cut short. Pinned.</summary>
    public const string CapNote = "the 1,000-entry cap cut the search short; narrow it";

    /// <summary>How a value was worked out, beside it.</summary>
    public const string Computed = "computed by the domain";

    public const string WorkedOut = "worked out from the policy";
}
