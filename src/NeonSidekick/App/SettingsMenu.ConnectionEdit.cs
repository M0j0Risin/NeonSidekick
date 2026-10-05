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
/// first. ESC on an edit's summary, or on a new one's File page, comes back to this list; ESC here ends the visit.
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
    /// default target) once the save has put the password elsewhere: another name, or the file store.
    /// </summary>
    private static void ForgetOldCredential(string? oldTarget, string? newTarget)
    {
        if (oldTarget is not null && !string.Equals(oldTarget, newTarget, StringComparison.OrdinalIgnoreCase))
        {
            WindowsCredentials.DeleteGeneric(oldTarget);
        }
    }

    /// <summary>A copy of an entry through its serialiser, its <c>password</c> dropped (the draft holds the plain one).</summary>
    private static T CloneEntry<T>(T config, JsonTypeInfo<T> info, Action<T> dropPassword)
        where T : class
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, info), info)!;
        dropPassword(copy);
        return copy;
    }

    /// <summary>The entries the first page lists: the profile's file, then the home's (when it is another file), each by its load.</summary>
    private static List<(T Named, bool Global)> ConnectionWizardEntries<T>(string profilePath, string globalPath, Func<string, IEnumerable<T>> load)
    {
        var entries = load(profilePath).Select(named => (named, false)).ToList();
        if (!string.Equals(Path.GetFullPath(profilePath), Path.GetFullPath(globalPath), StringComparison.OrdinalIgnoreCase))
        {
            entries.AddRange(load(globalPath).Select(named => (named, true)));
        }

        return entries;
    }

    /// <summary>The first page: <see cref="ConnectionWizardNewRow"/>, then each entry. -1 for new, the entry's index, or null for ESC.</summary>
    private async Task<int?> ConnectionWizardPickAsync(string title, string noun, IReadOnlyList<(string Name, bool Global)> entries, Func<bool, string> pathOf, CancellationToken cancellationToken)
    {
        int width = entries.Max(e => e.Name.Length);
        var rows = new List<string> { ConnectionWizardNewRow(noun) };
        rows.AddRange(entries.Select(e => ConnectionWizardEntryRow(e.Name, width, e.Global, pathOf(e.Global))));
        var page = new MenuPage(title, rows.Select(Markup.Escape).ToList(), PickKeys) { Caption = ConnectionWizardPickQuestion };
        if (!_pane.Enabled)
        {
            Flow.Notice(ConnectionWizardPickQuestion);
        }

        return await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is { } picked ? picked - 1 : null;
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
