using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// What the six connection wizards share for an edit (2026-10-05, the user's ask: "SQL add/edit connection", and its kin for
/// Oracle, MySQL, PostgreSQL, SQLite and UNC — an existing entry picked and changed in the wizard rather than in the file). A
/// first page lists <see cref="ConnectionWizardNewRow"/> and every entry of the profile's file and the home's, when there is
/// any (none: the wizard opens on its File page as before); a pick opens the wizard's summary on that entry, prefilled (Enter
/// on a row changes that one), its file fixed (the File row does nothing) and its stored password read into the draft, so a
/// save writes the entry back in its place (<see cref="ConnectionsFileEdit.ReplaceConnection"/>) and the password through the
/// family's store as an add does — a renamed entry or a changed store included. A password that cannot be read is asked
/// first. ESC on an edit's summary, or on a new one's File page, comes back to this list; ESC here ends the visit. An edit with unsaved
/// changes asks first at ESC and Cancel (2026-11-26, the user's ask: <see cref="ConnectionWizardLeaveAsync"/>), and a new one's Cancel asks a
/// yes/no. Enter on a summary row comes back to that row (2026-11-26, the user's report: it jumped to a save row).
/// An entry the loader refuses (a problem in it) is not listed: it is fixed in its file. The offered list follows a rename
/// in its place (<see cref="ConnectionOfferedAfterSave"/>), and a Credential Manager entry under the old name's default
/// target goes once the new one is written (<see cref="ForgetOldCredential"/>). Keys of an entry the wizard never asks
/// (<c>credential</c>, MySQL's <c>allowPublicKeyRetrieval</c>) are carried through; the entry is written as its serialiser
/// writes it, so a key it does not know is not.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The first page's caption. Pinned.</summary>
    public const string ConnectionWizardPickQuestion = "Add a new one, or pick one to edit (an entry with a problem is fixed in its file).";

    /// <summary>The notice when an edit is cancelled from its summary. Pinned.</summary>
    public const string ConnectionWizardUnchangedNotice = "Nothing changed.";

    /// <summary>The notice when an edit's changes are discarded at the unsaved-changes question (2026-11-26).</summary>
    public const string ConnectionWizardDiscardedNotice = "Nothing saved.";

    /// <summary>The masked password of an edit that kept the stored one. Pinned.</summary>
    public const string SqlWizardMaskedKept = SqlWizardMasked + " (stored)";

    /// <summary>The first page's first row: a new entry. Pinned.</summary>
    public static string ConnectionWizardNewRow(string noun) => "+ New " + noun;

    /// <summary>An entry's row on the first page: its name padded to <paramref name="width"/>, then its file as the File page shows it. Pinned.</summary>
    public static string ConnectionWizardEntryRow(string name, int width, bool global, string path) =>
        name.PadRight(width) + "  " + SqlWizardFileRow(global, path);

    /// <summary>
    /// The offered list after a save (pure): <paramref name="original"/> (an edit's old name) and <paramref name="name"/> taken out,
    /// then — when <paramref name="offer"/> — <paramref name="name"/> put back where the old name stood, or at the end.
    /// </summary>
    public static List<string> ConnectionOfferedAfterSave(IReadOnlyList<string>? current, string? original, string name, bool offer)
    {
        ArgumentNullException.ThrowIfNull(name);
        var list = (current ?? []).ToList();
        int at = original is null ? -1 : list.FindIndex(n => string.Equals(n, original, StringComparison.OrdinalIgnoreCase));
        list.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || (original is not null && string.Equals(n, original, StringComparison.OrdinalIgnoreCase)));
        if (offer)
        {
            list.Insert(at >= 0 ? Math.Min(at, list.Count) : list.Count, name);
        }

        return list;
    }

    /// <summary>
    /// A wizard summary's rows (2026-11-24, the user's ask): the two saves, a rule, the test and the cancel, a rule, then the draft's rows
    /// (already markup). The summaries keep their cursor and picks as items through it.
    /// </summary>
    internal static RuledRows ConnectionWizardSummaryRows(IReadOnlyList<string> actions, IEnumerable<string> draftRows) =>
        RuledRows.Of([actions.Take(2).Select(Markup.Escape).ToList(), actions.Skip(2).Select(Markup.Escape).ToList(), draftRows.ToList()]);

    /// <summary>The summary's first cursor: "Save, hidden" for an edit of an entry not offered now, else "Save, and offer it".</summary>
    public static int ConnectionWizardStartCursor(string? original, IReadOnlyList<string>? offered) =>
        original is not null && !(offered ?? []).Contains(original, StringComparer.OrdinalIgnoreCase) ? 1 : 0;

    /// <summary>The offered list written after a save (<see cref="ConnectionOfferedAfterSave"/>); true when it changed.</summary>
    private bool ApplyOfferedAfterSave(SettingsField field, IReadOnlyList<string>? current, string? original, string name, bool offer, Action<AppSettingsData, List<string>> set)
    {
        var next = ConnectionOfferedAfterSave(current, original, name, offer);
        if (next.SequenceEqual(current ?? [], StringComparer.Ordinal))
        {
            return false;
        }

        Apply(field, d => set(d, next));
        return true;
    }

    /// <summary>
    /// The old Credential Manager entry of an edit removed (<paramref name="oldTarget"/>, set only for an entry under its name's
    /// default target) once the save has put the password elsewhere: another name, or the file store — unless an entry still reads it
    /// (<paramref name="inUse"/>, 2026-10-24, the database review's N12: renaming the profile's <c>prod</c> deleted the default target the home's
    /// <c>prod</c> still signed in with, for every profile).
    /// </summary>
    private static void ForgetOldCredential(string? oldTarget, string? newTarget, Func<string, bool>? inUse = null)
    {
        if (oldTarget is not null && !string.Equals(oldTarget, newTarget, StringComparison.OrdinalIgnoreCase) && inUse?.Invoke(oldTarget) != true)
        {
            WindowsCredentials.DeleteGeneric(oldTarget);
        }
    }

    /// <summary>
    /// The row of a choice page the stored word names, case and spaces ignored as the entry's own check reads it; the first row for none
    /// (2026-10-24, the database review's N7: an exact match opened a stored <c>Verify-Full</c> on the first row, and Enter wrote the default,
    /// silently dropping the certificate check).
    /// </summary>
    internal static int WordIndex(IReadOnlyList<string> words, string? stored) =>
        Math.Max(0, words.ToList().FindIndex(w => string.Equals(w, stored?.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>A wizard's save refusal: "could not add" for a new entry, "could not change" for an edit (2026-10-24, the database review's K9).</summary>
    private static string WizardSaveFailed(string? original, string name, string why) =>
        original is null ? SqlText.ConnectionAddFailed(name, why) : SqlText.ConnectionChangeFailed(name, why);

    /// <summary>
    /// The database wizards' one save (2026-10-24, the database review's B13): the entry and its password in one write — a <c>file</c> store's
    /// password sealed first and spliced in with the entry, a <c>credman</c> one written to the store before the entry, so a failed step leaves
    /// the old entry and its password as they were (an edit replaced the entry without its password, then a failed password save left it with
    /// none). Then the old credential of a rename let go (unless another entry in either file still reads it, N12). False when nothing was
    /// written; each step's notice or error said. <paramref name="passwordOnly"/> (<see cref="WizardPasswordOnly"/>, 2026-11-26): the
    /// password alone to its store (<see cref="ConnectionFamily{TConfig, TNamed}.Save"/>: the <c>password</c> token, or the credential), the
    /// entry untouched; the target is unchanged, so no old credential is let go.
    /// </summary>
    private bool SaveWizardEntry<TConfig, TNamed>(ConnectionFamily<TConfig, TNamed> family, string path, string otherPath, string? original, string name, TConfig config, string password, string? originalTarget,
        bool passwordOnly = false)
        where TConfig : class, ISignInConfig
        where TNamed : class, INamedConnection<TConfig>
    {
        if (passwordOnly)
        {
            var (stored, storedNotice) = family.Save(family.Named(name, config, path), password);
            if (!stored)
            {
                Sink.Error(storedNotice);
                return false;
            }

            Sink.Notice(storedNotice);
            return true;
        }

        string? sealedPassword = null;
        string? storeNotice = null;
        if (config.NeedsPassword)
        {
            if (config.InCredentialManager)
            {
                var (saved, notice) = family.Save(family.Named(name, config, path), password);
                if (!saved)
                {
                    Sink.Error(notice);
                    return false;
                }

                storeNotice = notice;
            }
            else
            {
                var encrypted = WindowsCredentials.Protect(password);
                if (encrypted.Error is { } failed)
                {
                    Sink.Error(SqlText.PasswordSaveFailed(name, failed));
                    return false;
                }

                sealedPassword = encrypted.Value;
                storeNotice = SqlText.PasswordSavedToFile(name, path);
            }
        }

        if ((original is { } old ? family.ReplaceConnection(path, old, name, config, sealedPassword) : family.AddConnection(path, name, config, sealedPassword)) is { } error)
        {
            Sink.Error(WizardSaveFailed(original, name, error));
            return false;
        }

        Sink.Notice(original is null ? SqlText.ConnectionAdded(name, path) : SqlText.ConnectionChanged(name, path));
        if (storeNotice is not null)
        {
            Sink.Notice(storeNotice);
        }

        ForgetOldCredential(originalTarget, config.NeedsPassword && config.InCredentialManager ? config.CredentialTarget(name) : null, target => CredentialInUse(family, target, path, otherPath));
        return true;
    }

    /// <summary>Whether an entry of either file (usable or refused) still reads its password from <paramref name="target"/> (N12).</summary>
    internal static bool CredentialInUse<TConfig, TNamed>(ConnectionFamily<TConfig, TNamed> family, string target, params string[] paths)
        where TConfig : class, ISignInConfig
        where TNamed : class, INamedConnection<TConfig>
    {
        foreach (string file in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (connections, problems) = family.Load(file);
            var entries = connections.Select(c => (c.Name, c.Config)).Concat(problems.Where(p => p.Name is not null && p.Entry is TConfig).Select(p => (p.Name!, (TConfig)p.Entry!)));
            if (entries.Any(e => e.Item2.InCredentialManager && string.Equals(e.Item2.CredentialTarget(e.Item1), target, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An edit whose host moved (2026-10-24, the database review's E9, decision DB9): the stored password it read is let go, so the Password page
    /// asks again and the save records the new host; the old one was sent to whatever host the entry now named, on Test and on save.
    /// </summary>
    private void ForgetKeptPasswordOnMove(string? originalHost, string host, bool kept, Action forget)
    {
        if (kept && originalHost is not null && !CredentialMark.SameHost(originalHost, host))
        {
            forget();
            Sink.Notice(SqlWizardHostMovedNotice);
        }
    }

    /// <summary>What an edit says when its host moved and the kept password was let go (E9). Pinned.</summary>
    public const string SqlWizardHostMovedNotice = "The connection points at another server now, so its stored password is not sent there; type the password for it.";

    /// <summary>
    /// An edit's draft as it would be written (2026-11-26, the unsaved-changes question): its name, its file and the entry through its
    /// serialiser with the password dropped (<see cref="CloneEntry"/>), so no secret ever sits in it. Taken when the edit opens and again at
    /// ESC or Cancel: a difference is a change Save would write (a step's same value picked can make an implicit default explicit, and reads
    /// as one). A typed password is checked beside it.
    /// </summary>
    internal static string ConnectionStamp<T>(string name, bool global, T config, JsonTypeInfo<T> info, Action<T> dropPassword)
        where T : class =>
        string.Join('\n', name, global ? "global" : "profile", JsonSerializer.Serialize(CloneEntry(config, info, dropPassword), info));

    /// <summary>
    /// Whether an edit's one change is a typed password (2026-11-26, the user's call when the per-family "set password" rows went): the entry
    /// as it would be written equals the one that opened (<see cref="ConnectionStamp"/>: the same name, file and entry, its password dropped),
    /// and a new password was typed. Such a save writes only the password, the old row's way, so the file's other bytes (comments inside the
    /// entry, key order) stay as they are. Taken before the save's own normalizing, which can change the stamp.
    /// </summary>
    internal static bool WizardPasswordOnly(string? original, string stampNow, string? opened, bool passwordKept, string password) =>
        original is not null && string.Equals(stampNow, opened, StringComparison.Ordinal) && !passwordKept && password.Length > 0;

    /// <summary>
    /// A password-only save's end (<see cref="WizardPasswordOnly"/>): the store's own line, or its error with the summary kept (null); then the
    /// Save row's offered or hidden choice, as after any save. No "changed" line: only the password was written.
    /// </summary>
    private bool? WizardPasswordOnlySave((bool Saved, string Notice) stored, Func<bool?> offered)
    {
        if (!stored.Saved)
        {
            Sink.Error(stored.Notice);
            return null;
        }

        Sink.Notice(stored.Notice);
        return offered();
    }

    /// <summary>What a summary's ESC or Cancel came to (<see cref="ConnectionWizardLeaveAsync"/>): stay, saved (whether a setting changed), or left.</summary>
    private readonly record struct WizardLeave(bool Stay, bool? Saved)
    {
        public static WizardLeave Kept => new(true, null);

        public static WizardLeave Left => new(false, null);
    }

    /// <summary>
    /// ESC (<paramref name="cancelRow"/> false) or Cancel on a wizard's summary (2026-11-26, the user's ask). A new entry: ESC goes back a page as
    /// before, Cancel asks <see cref="NewDiscardQuestion"/> (No stays). An edit: without changes it leaves (Cancel saying so); with them it asks
    /// <see cref="AskLeaveAsync"/>, named by the saved entry's name (<paramref name="original"/>: a rename has already moved the draft's).
    /// Save is <paramref name="save"/>, the family's save with the entry offered or hidden as it is now; a refused one (said) stays. A pane
    /// dismissed by a click off it, or the token, leaves without asking.
    /// </summary>
    private async Task<WizardLeave> ConnectionWizardLeaveAsync(bool cancelRow, string? original, bool dirty, string noun, string cancelledNotice, Func<bool?> save, CancellationToken cancellationToken)
    {
        bool Gone() => _pane.Dismissed || cancellationToken.IsCancellationRequested;
        if (original is null)
        {
            if (!cancelRow)
            {
                return WizardLeave.Left;   // a new entry's ESC: back a page, nothing lost
            }

            if (!Gone() && !await ConfirmAsync(NewDiscardQuestion(noun), cancellationToken, close: false).ConfigureAwait(false) && !Gone())
            {
                return WizardLeave.Kept;
            }

            Sink.Notice(cancelledNotice);
            return WizardLeave.Left;
        }

        if (!dirty)
        {
            if (cancelRow)
            {
                Sink.Notice(ConnectionWizardUnchangedNotice);
            }

            return WizardLeave.Left;
        }

        var choice = Gone() ? LeaveChoice.Discard : await AskLeaveAsync(original, cancellationToken).ConfigureAwait(false);
        if (Gone() || choice == LeaveChoice.Discard)
        {
            Sink.Notice(ConnectionWizardDiscardedNotice);
            return WizardLeave.Left;
        }

        return choice == LeaveChoice.Save && save() is { } changed ? new WizardLeave(false, changed) : WizardLeave.Kept;
    }

    /// <summary>A copy of an entry through its serialiser, its <c>password</c> dropped (the draft holds the plain one).</summary>
    private static T CloneEntry<T>(T config, JsonTypeInfo<T> info, Action<T> dropPassword)
        where T : class
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, info), info)!;
        dropPassword(copy);
        return copy;
    }

    /// <summary>
    /// The entries the first page lists: the profile's file and the home's (when it is another file), each by its load, A to Z by
    /// <paramref name="name"/> (2026-10-11, the user's ask, after the offered and password panes; <see cref="ByName"/> is stable, so
    /// a name in both files lists the profile's first).
    /// </summary>
    private static List<(T Named, bool Global)> ConnectionWizardEntries<T>(string profilePath, string globalPath, Func<string, IEnumerable<T>> load, Func<T, string> name)
    {
        var entries = load(profilePath).Select(named => (named, false)).ToList();
        if (!string.Equals(Path.GetFullPath(profilePath), Path.GetFullPath(globalPath), StringComparison.OrdinalIgnoreCase))
        {
            entries.AddRange(load(globalPath).Select(named => (named, true)));
        }

        return ByName<(T Named, bool Global)>(entries, e => name(e.Named));
    }

    /// <summary>The first page: <see cref="ConnectionWizardNewRow"/>, then each entry. -1 for new, the entry's index, or null for ESC.</summary>
    private async Task<int?> ConnectionWizardPickAsync(string title, string noun, IReadOnlyList<(string Name, bool Global)> entries, Func<bool, string> pathOf, CancellationToken cancellationToken)
    {
        int width = entries.Count == 0 ? 0 : entries.Max(e => e.Name.Length);   // only the new row: never a crash on an empty list (2026-10-15)
        // A rule between the new row and the entries (2026-11-24, the user's ask): the pick maps back through the ruled rows.
        var ruled = RuledRows.Of([[Markup.Escape(ConnectionWizardNewRow(noun))], entries.Select(e => Markup.Escape(ConnectionWizardEntryRow(e.Name, width, e.Global, pathOf(e.Global)))).ToList()]);
        var page = new MenuPage(title, ruled.Rows, PickKeys) { Caption = ConnectionWizardPickQuestion, Headings = ruled.Headings };
        if (!_pane.Enabled)
        {
            Flow.Notice(ConnectionWizardPickQuestion);
        }

        return await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is { } picked ? ruled.ItemOf(picked) - 1 : null;
    }

    /// <summary>An edit's stored password for its draft: the value, or "" with the reason as a warning (the Password page then asks).</summary>
    private string StoredPassword(CredentialResult resolved)
    {
        if (resolved.Value is { } value)
        {
            return value;
        }

        if (resolved.Error is { } error)
        {
            Sink.Warning(error);
        }

        return "";
    }
}
