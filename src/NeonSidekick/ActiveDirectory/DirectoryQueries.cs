using System.Diagnostics;
using System.Globalization;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.EventLog;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.ActiveDirectory;

/// <summary>The domain's password and lockout policy: the domain head's, or a fine-grained one's.</summary>
/// <remarks><see cref="UnreadPso"/>: the fine-grained policy that applies but whose settings this account cannot read, the domain's used in its place.</remarks>
public sealed record AdPolicy(string? Name, AdInterval? MaxPasswordAge, AdInterval? LockoutDuration, long? LockoutThreshold, AdInterval? ObservationWindow, long? MinLength, long? History, bool Complex,
    string? UnreadPso = null);

/// <summary>A lockout and a password expiry worked out, and how.</summary>
public sealed record AdAccountState(bool Locked, DateTimeOffset? LockedAt, DateTimeOffset? Unlocks, bool LockedForever, string LockedHow, DateTimeOffset? PasswordSet, DateTimeOffset? Expires, bool NeverExpires, bool MustChange, string ExpiresHow);

/// <summary>
/// One <c>ad_find</c> (2026-10-15, the AD gaps plan): the kind, its days and words, the type (user, group, computer, ou, contact, service), the
/// field, the OU it is held to, how many to list, how many to read at most (<see cref="DirectoryLimits.MaxEntries"/>, or
/// <see cref="DirectoryLimits.MaxCounted"/> for a <c>to</c>) and whether the cap's note may offer a save.
/// </summary>
public sealed record FindOptions(string Kind, int Days, string? Words, int Limit, string? Type = null, string? Ou = null, string? Field = null, int Cap = DirectoryLimits.MaxEntries, bool CanSave = false);

/// <summary>
/// The questions the directory tools answer (2026-10-15, the admin-tools plan, and the AD gaps plan the same day): a user's card, a group's
/// members, a computer's card, a structured find, an OU's tree or contents, the domain's facts — each built once as a <see cref="DirectoryAnswer"/>
/// and drawn by <see cref="DirectoryRender"/> (the tools' text), the card and the CSV. Constructed attributes are read when the domain computes
/// them (<c>msDS-User-Account-Control-Computed</c>, <c>msDS-UserPasswordExpiryTimeComputed</c>, <c>tokenGroups</c>, <c>msDS-ResultantPSO</c>)
/// and worked out from the policy when it does not (Samba sets no <c>ADS_UF_PASSWORD_EXPIRED</c>): locked when <c>lockoutTime</c> &gt; 0 and
/// the duration is forever or not yet over; the expiry never with <c>DONT_EXPIRE_PASSWORD</c> or a never-aging policy, "must change" with
/// <c>pwdLastSet</c> 0, else <c>pwdLastSet</c> + the maximum age. Every card says which way each was found. A stranger's text — names, titles,
/// descriptions, DNs — is fenced in what the model reads. Pure over <see cref="IDirectorySession"/>.
/// </summary>
public static class DirectoryQueries
{
    public const int MaxChars = 16_000;

    /// <summary>The most OUs the tree reads.</summary>
    public const int MaxTree = 500;

    // ── The choices (one list each: the refusals, /ad's words, the help's form and the schemas' enums are held to them) ─────

    public static readonly IReadOnlyList<string> FindKinds =
        ["locked", "password_expiring", "disabled", "stale_users", "stale_computers", "name", "field", "os", "password_never_expires", "account_expiring", "created", "privileged", "empty_groups"];

    public static readonly IReadOnlyList<string> FindTypes = ["user", "group", "computer", "ou", "contact", "service"];

    public static readonly IReadOnlyList<string> FieldNames = ["department", "title", "company", "office", "description"];

    public static readonly IReadOnlyList<string> MemberChoices = ["all", "users", "computers", "groups"];

    public static readonly IReadOnlyList<string> WordsKinds = ["name", "field", "os"];

    public static readonly IReadOnlyList<string> DaysKinds = ["password_expiring", "account_expiring", "stale_users", "stale_computers", "created"];

    public static readonly IReadOnlyList<string> TypeKinds = ["name", "field", "created"];

    public static readonly IReadOnlyList<string> FieldKinds = ["field"];

    public static readonly IReadOnlyList<string> OuKinds = [.. FindKinds.Where(k => k != "privileged")];

    /// <summary>A kind's days when none are given, for the tool and <c>/ad</c> alike.</summary>
    public static int DefaultDays(string kind) => kind switch
    {
        "password_expiring" => 14,
        "account_expiring" or "created" => 30,
        _ => 90,
    };

    /// <summary>The fields a type has: a user and a contact all five; a group, a computer, an OU and a service account a description alone.</summary>
    public static IReadOnlyList<string> FieldsFor(string type) => type is "user" or "contact" ? FieldNames : ["description"];

    /// <summary>
    /// Why a find cannot be asked as given, before anything is read; null when it can (2026-10-15, the AD gaps plan): a kind it is not, an argument
    /// the kind takes not (naming the kinds that do), words missing, a type or field it is not, a field the type has not, <c>office</c> redacted.
    /// </summary>
    public static string? FindProblem(string kind, bool daysGiven, string? words, string? type, string? field, string? ou, bool redacted)
    {
        if (!FindKinds.Contains(kind))
        {
            return DirectoryText.BadKind(kind);
        }

        if (daysGiven && !DaysKinds.Contains(kind))
        {
            return DirectoryText.NotTaken("days", kind, DaysKinds);
        }

        if (words is not null && !WordsKinds.Contains(kind))
        {
            return DirectoryText.NotTaken("words", kind, WordsKinds);
        }

        if (type is not null && !TypeKinds.Contains(kind))
        {
            return DirectoryText.NotTaken("type", kind, TypeKinds);
        }

        if (field is not null && !FieldKinds.Contains(kind))
        {
            return DirectoryText.NotTaken("field", kind, FieldKinds);
        }

        if (ou is not null && !OuKinds.Contains(kind))
        {
            return DirectoryText.NotTaken("ou", kind, OuKinds);
        }

        if (WordsKinds.Contains(kind) && words is null)
        {
            return DirectoryText.WordsNeeded(kind);
        }

        if (type is not null && !FindTypes.Contains(type))
        {
            return DirectoryText.BadChoice("type", type, FindTypes);
        }

        if (kind == "field")
        {
            if (field is null)
            {
                return DirectoryText.Missing("field");
            }

            if (!FieldNames.Contains(field))
            {
                return DirectoryText.BadChoice("field", field, FieldNames);
            }

            string of = type ?? "user";
            if (!FieldsFor(of).Contains(field))
            {
                return DirectoryText.FieldNotForType(field, of, FieldsFor(of));
            }

            if (field == "office" && redacted)
            {
                return DirectoryText.OfficeRedacted;
            }
        }

        return null;
    }

    // ── Lookups ─────────────────────────────────────────────────────────────

    /// <summary>The one object a name means; the candidates when several match; null for none.</summary>
    public static (DirectoryEntry? Found, IReadOnlyList<string> Candidates) One(IDirectorySession session, string baseDn, string filter, IReadOnlyList<string> attributes)
    {
        var (found, candidates) = OneOf(session, baseDn, filter, attributes);
        return (found, candidates.Select(CandidateLine).ToList());
    }

    /// <summary><see cref="One"/>'s entries: the one, or the candidates as entries (the card offers them).</summary>
    public static (DirectoryEntry? Found, IReadOnlyList<DirectoryEntry> Candidates) OneOf(IDirectorySession session, string baseDn, string filter, IReadOnlyList<string> attributes, TimeSpan? timeLimit = null)
    {
        var results = session.Search(baseDn, DirectoryScope.Subtree, filter, attributes, 20, paged: false, timeLimit);
        return results.Entries.Count switch
        {
            0 => (null, []),
            1 => (results.Entries[0], []),
            _ => (null, results.Entries),
        };
    }

    private static string CandidateLine(DirectoryEntry e) => (e.Text("sAMAccountName") ?? AdValues.FirstName(e.Dn)) + "  " + UntrustedText.Fenced(UntrustedText.Cut(e.Dn, 300));

    /// <summary>A name several accounts share (2026-10-15, the AD gaps plan): the model's refusal as ever, and the candidates as a list the card offers.</summary>
    private static DirectoryAnswer Ambiguous(string name, IReadOnlyList<DirectoryEntry> candidates)
    {
        var table = new DirectoryTable([new(DirectoryText.ColAccount), new(DirectoryText.ColName, Flexible: true), new(DirectoryText.ColOu, Flexible: true)]);
        foreach (var e in candidates)
        {
            table.Rows.Add(new DirectoryRow(CandidateLine(e), [e.Text("sAMAccountName"), Display(e), AdValues.Parent(e.Dn)], e.Dn, Display(e), Opens: true));
        }

        return new DirectoryAnswer
        {
            Error = DirectoryText.Ambiguous(name, candidates.Select(CandidateLine).ToList()),
            Candidates = table,
            CandidatesTitle = DirectoryText.AmbiguousTitle(name, candidates.Count),
        };
    }

    /// <summary>
    /// The domain head's policy, and a fine-grained one's over it when <paramref name="psoDn"/> names one that can be read. A PSO this account
    /// reads the object of but none of its settings (an ordinary account on Samba, 2026-10-15; <see cref="SettingsUnreadable"/>) or may not read at
    /// all is the domain's policy with <see cref="AdPolicy.UnreadPso"/> naming it (2026-10-20, a review's finding: its all-null intervals had read a
    /// stale <c>lockoutTime</c> as locked forever); <paramref name="domain"/>, when the caller has the domain's already, saves the head's read.
    /// </summary>
    public static AdPolicy Policy(IDirectorySession session, string baseDn, string? psoDn, DirectoryDeadline deadline = default, AdPolicy? domain = null)
    {
        string? unread = null;
        if (psoDn is { Length: > 0 })
        {
            unread = AdValues.FirstName(psoDn);
            try
            {
                var pso = session.Search(psoDn, DirectoryScope.Base, "(objectClass=*)", AdAttributes.PasswordSettings, 1, paged: false, deadline.Limit);
                if (pso.Entries.Count > 0)
                {
                    var p = pso.Entries[0];
                    unread = p.Text("name") ?? unread;
                    if (!SettingsUnreadable(p))
                    {
                        return new AdPolicy(unread, AdValues.Interval(p.Text("msDS-MaximumPasswordAge")), AdValues.Interval(p.Text("msDS-LockoutDuration")),
                            AdValues.Integer(p.Text("msDS-LockoutThreshold")), AdValues.Interval(p.Text("msDS-LockoutObservationWindow")), AdValues.Integer(p.Text("msDS-MinimumPasswordLength")), null, false);
                    }
                }
            }
            catch (DirectoryException)
            {
                // A PSO the account may not read: the domain's policy below.
            }
        }

        var fallback = domain ?? DomainPolicy(session, baseDn, deadline);
        return unread is null ? fallback : fallback with { Name = null, UnreadPso = unread };
    }

    /// <summary>
    /// Whether a PSO's entry came back without its settings (every one is a must-have of <c>msDS-PasswordSettings</c>, so none read means none
    /// readable). Shared by the user's policy and the domain card's list.
    /// </summary>
    private static bool SettingsUnreadable(DirectoryEntry p) =>
        p.Text("msDS-PasswordSettingsPrecedence") is null && p.Text("msDS-MaximumPasswordAge") is null && p.Text("msDS-MinimumPasswordLength") is null
        && p.Text("msDS-LockoutDuration") is null && p.Text("msDS-LockoutThreshold") is null;

    /// <summary>
    /// Each account's policy for a find (2026-10-20, a review's finding: <c>locked</c> read a PSO per candidate and <c>password_expiring</c> none):
    /// the domain's once, each PSO once by its DN; null when one not yet read cannot start under the deadline.
    /// </summary>
    private sealed class PolicyCache(IDirectorySession session, string baseDn, DirectoryDeadline deadline)
    {
        private readonly Dictionary<string, AdPolicy> _psos = new(StringComparer.OrdinalIgnoreCase);
        private AdPolicy? _domain;

        public AdPolicy? For(DirectoryEntry account)
        {
            if (_domain is null)
            {
                if (!deadline.CanStart)
                {
                    return null;
                }

                _domain = DomainPolicy(session, baseDn, deadline);
            }

            if (account.Text("msDS-ResultantPSO") is not { Length: > 0 } pso)
            {
                return _domain;
            }

            if (_psos.TryGetValue(pso, out var known))
            {
                return known;
            }

            if (!deadline.CanStart)
            {
                return null;
            }

            return _psos[pso] = Policy(session, baseDn, pso, deadline, _domain);
        }
    }

    private static AdPolicy DomainPolicy(IDirectorySession session, string baseDn, DirectoryDeadline deadline)
    {
        var head = session.Search(baseDn, DirectoryScope.Base, "(objectClass=*)", AdAttributes.DomainHead, 1, paged: false, deadline.Limit);
        if (head.Entries.Count == 0)
        {
            return new AdPolicy(null, null, null, null, null, null, null, false);
        }

        var h = head.Entries[0];
        long properties = AdValues.Integer(h.Text("pwdProperties")) ?? 0;
        return new AdPolicy(null, AdValues.Interval(h.Text("maxPwdAge")), AdValues.Interval(h.Text("lockoutDuration")), AdValues.Integer(h.Text("lockoutThreshold")),
            AdValues.Interval(h.Text("lockOutObservationWindow")), AdValues.Integer(h.Text("minPwdLength")), AdValues.Integer(h.Text("pwdHistoryLength")), (properties & 1) != 0);
    }

    /// <summary>A user's lockout and password expiry: the computed attributes first, the policy's arithmetic after. Pure.</summary>
    public static AdAccountState State(DirectoryEntry user, AdPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(policy);
        int uac = (int)(AdValues.Integer(user.Text("userAccountControl")) ?? 0);
        var lockedAt = AdValues.FileTime(user.Text("lockoutTime"));
        bool locked;
        bool forever = policy.LockoutDuration is { Endless: true };
        DateTimeOffset? unlocks = null;
        string lockedHow;
        if (AdValues.Integer(user.Text("msDS-User-Account-Control-Computed")) is { } computed)
        {
            locked = (computed & AdValues.Lockout) != 0;
            lockedHow = DirectoryText.Computed;
        }
        else
        {
            locked = lockedAt is { } at && (forever || policy.LockoutDuration?.Span is not { } span || now < at + span);
            lockedHow = DirectoryText.WorkedOut;
        }

        if (locked && lockedAt is { } since && !forever && policy.LockoutDuration?.Span is { } duration)
        {
            unlocks = since + duration;
        }

        var set = AdValues.FileTime(user.Text("pwdLastSet"));
        bool mustChange = AdValues.Integer(user.Text("pwdLastSet")) == 0;
        bool neverExpires = (uac & AdValues.DontExpirePassword) != 0;
        DateTimeOffset? expires = null;
        string expiresHow;
        string? computedExpiry = user.Text("msDS-UserPasswordExpiryTimeComputed");
        if (computedExpiry is not null && !mustChange)
        {
            expires = AdValues.FileTime(computedExpiry);
            neverExpires |= expires is null;
            expiresHow = DirectoryText.Computed;
        }
        else
        {
            expiresHow = DirectoryText.WorkedOut;
            if (policy.MaxPasswordAge is { Endless: true })
            {
                neverExpires = true;
            }
            else if (!neverExpires && !mustChange && set is { } s && policy.MaxPasswordAge?.Span is { } age)
            {
                expires = s + age;
            }
        }

        return new AdAccountState(locked, lockedAt, unlocks, locked && forever, lockedHow, set, expires, neverExpires && !mustChange, mustChange, expiresHow);
    }

    /// <summary>The PDC emulator's DNS name: the domain head's <c>fSMORoleOwner</c> (an NTDS Settings object) → its server's <c>dNSHostName</c>.</summary>
    public static string? RoleHolder(IDirectorySession session, string objectDn, DirectoryDeadline deadline = default)
    {
        try
        {
            if (!deadline.CanStart)
            {
                return null;
            }

            var holder = session.Search(objectDn, DirectoryScope.Base, "(objectClass=*)", ["fSMORoleOwner"], 1, paged: false, deadline.Limit);
            if (holder.Entries.Count == 0 || holder.Entries[0].Text("fSMORoleOwner") is not { } ntds)
            {
                return null;
            }

            string server = AdValues.Parent(ntds);
            if (!deadline.CanStart)
            {
                return AdValues.FirstName(server);
            }

            var host = session.Search(server, DirectoryScope.Base, "(objectClass=*)", ["dNSHostName"], 1, paged: false, deadline.Limit);
            return host.Entries.Count > 0 ? host.Entries[0].Text("dNSHostName") ?? AdValues.FirstName(server) : AdValues.FirstName(server);
        }
        catch (DirectoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The groups an account is in: <c>tokenGroups</c> (a base read, nested, security groups only) resolved 50 SIDs at a time, with the direct
    /// <c>memberOf</c> added so a distribution group shows (2026-10-15, the AD gaps plan); the direct <c>memberOf</c> and the primary group (by
    /// RID) when the domain gives no <c>tokenGroups</c>. Each by its name and DN, by name.
    /// </summary>
    public static (IReadOnlyList<(string Name, string Dn)> Groups, bool Nested, bool Partial) GroupsOf(IDirectorySession session, string baseDn, DirectoryEntry entry, DirectoryDeadline deadline = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var token = session.Search(entry.Dn, DirectoryScope.Base, "(objectClass=*)", ["tokenGroups"], 1, paged: false, deadline.Limit);
        var sids = token.Entries.Count > 0 ? token.Entries[0].Values("tokenGroups") : [];
        var (memberOf, partial) = entry.ValuesOrFirstRange("memberOf");
        var groups = new Dictionary<string, (string Name, string Dn)>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in sids.Chunk(DirectoryLimits.SidBatch))
        {
            if (groups.Count >= DirectoryLimits.MaxEntries || !deadline.CanStart)
            {
                break;
            }

            var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.BySids(batch), ["sAMAccountName", "cn"], DirectoryLimits.SidBatch, paged: false, deadline.Limit);
            foreach (var e in found.Entries)
            {
                groups[e.Dn] = (e.Text("sAMAccountName") ?? e.Text("cn") ?? AdValues.FirstName(e.Dn), e.Dn);
            }
        }

        foreach (string dn in memberOf)
        {
            groups.TryAdd(dn, (AdValues.FirstName(dn), dn));
        }

        if (sids.Count == 0 && PrimaryGroupOf(session, baseDn, entry, deadline) is { } primary)
        {
            groups.TryAdd(primary.Dn, primary);
        }

        return (groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(), sids.Count > 0, partial);
    }

    /// <summary>An account's primary group (its <c>primaryGroupID</c> under its own SID's domain), by name and DN; null when it has none or none reads.</summary>
    public static (string Name, string Dn)? PrimaryGroupOf(IDirectorySession session, string baseDn, DirectoryEntry entry, DirectoryDeadline deadline = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (AdValues.Integer(entry.Text("primaryGroupID")) is not { } rid || entry.Values("objectSid") is not { Count: > 0 } sids || AdValues.Rid(sids[0]) is null)
        {
            return null;
        }

        byte[] group = [.. sids[0]];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(group.Length - 4), (uint)rid);
        var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.BySids([group]), ["sAMAccountName", "cn"], 1, paged: false, deadline.Limit);
        return found.Entries.Count > 0 ? (found.Entries[0].Text("sAMAccountName") ?? found.Entries[0].Text("cn") ?? AdValues.FirstName(found.Entries[0].Dn), found.Entries[0].Dn) : null;
    }

    // ── A user ──────────────────────────────────────────────────────────────

    public static string User(IDirectorySession session, DirectorySource source, string name, DateTimeOffset now, TimeZoneInfo zone, DirectoryDeadline deadline = default) =>
        DirectoryRender.ForModel(UserAnswer(session, source, name, now, zone, deadline: deadline));

    /// <summary>A user's card, by a name or (a clicked row) by its DN.</summary>
    public static DirectoryAnswer UserAnswer(IDirectorySession session, DirectorySource source, string name, DateTimeOffset now, TimeZoneInfo zone, string? dn = null, DirectoryDeadline deadline = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        string baseDn = source.Domain.BaseDn;
        var (user, candidates) = OneOf(session, baseDn, dn is null ? LdapFilter.UserByName(name) : LdapFilter.ByDn("(&" + LdapFilter.Users + ")", dn), AdAttributes.User, deadline.Limit);
        if (user is null)
        {
            return candidates.Count > 0 ? Ambiguous(name, candidates) : DirectoryAnswer.Refused(dn is null ? DirectoryText.NotFound("user", name, source.Where) : DirectoryText.Gone(name, source.Where));
        }

        var policy = Policy(session, baseDn, user.Text("msDS-ResultantPSO"), deadline);
        var state = State(user, policy, now);
        int uac = (int)(AdValues.Integer(user.Text("userAccountControl")) ?? 0);
        string account = user.Text("sAMAccountName") ?? name;
        var answer = Start("user", now, zone);
        answer.Self = new DirectoryRowTarget(user.Dn, account);
        answer.Head = $"User {Plain(account)} in {source.Where}: " + ((uac & AdValues.AccountDisable) != 0 ? "disabled" : "enabled") + (state.Locked ? ", locked" : "");
        Add(answer, "name", Fence(user.Text("displayName") ?? user.Text("cn") ?? "") + (user.Text("userPrincipalName") is { } upn ? " · upn " + Fence(upn) : "") + (user.Text("mail") is { } mail ? " · mail " + Fence(mail) : ""));
        Add(answer, "dn", Fence(user.Dn));
        if (state.Locked)
        {
            answer.Pdc = RoleHolder(session, baseDn, deadline);
            string pdc = answer.Pdc ?? "the PDC emulator";
            Add(answer, DirectoryText.LockedLabel, (state.LockedAt is { } at ? "since " + Time(at, zone) : "yes") + (state.LockedForever ? ", " + DirectoryText.ForeverWord : state.Unlocks is { } unlocks ? ", unlocks " + Time(unlocks, zone) : "")
                + " (" + state.LockedHow + "); " + DirectoryText.LockoutSource(pdc));
        }

        if (AdValues.Integer(user.Text("badPwdCount")) is { } bad && bad > 0)
        {
            Add(answer, "bad passwords", bad.ToString(CultureInfo.InvariantCulture) + (AdValues.FileTime(user.Text("badPasswordTime")) is { } last ? ", last " + Time(last, zone) : "") + " (this DC's count)");
        }

        Add(answer, "password", (state.PasswordSet is { } set ? "set " + Time(set, zone) + ", " : "")
            + (state.MustChange ? DirectoryText.MustChange : state.NeverExpires ? "never expires" : state.Expires is { } expires ? (expires < now ? "expired " : "expires ") + Time(expires, zone) : "expiry unknown")
            + " (" + state.ExpiresHow + ")");
        Add(answer, "policy", policy.Name is { } pso ? "fine-grained: " + Fence(pso)
            : policy.UnreadPso is { } unread ? DirectoryText.PolicyUnreadOver(Fence(unread))
            : user.Text("msDS-ResultantPSO") is null ? DirectoryText.DomainPolicyMaybe : "the domain policy");
        Add(answer, "last sign-in", (AdValues.FileTime(user.Text("lastLogonTimestamp")) is { } logon ? Time(logon, zone) : DirectoryText.NeverWord) + " (" + DirectoryText.StaleNote + ")");
        Add(answer, "account expires", AdValues.FileTime(user.Text("accountExpires")) is { } ends ? Time(ends, zone) : DirectoryText.NeverWord);
        foreach (string field in (string[])["title", "department", "company", "description"])
        {
            if (user.Text(field) is { } value)
            {
                Add(answer, field, Fence(value));
            }
        }

        if (user.Text("manager") is { } manager)
        {
            answer.Fields.Add(new DirectoryField("manager", Fence(AdValues.FirstName(manager)), new DirectoryRowTarget(manager, AdValues.FirstName(manager))));
        }

        var (reports, reportsPartial) = user.ValuesOrFirstRange("directReports");
        if (reports.Count > 0)
        {
            var sorted = reports.OrderBy(AdValues.FirstName, StringComparer.OrdinalIgnoreCase).ToList();
            string label = "direct reports (" + reports.Count.ToString(CultureInfo.InvariantCulture) + (reportsPartial ? "+" : "") + ")";
            answer.Fields.Add(new DirectoryField(label, string.Join(", ", sorted.Take(50).Select(r => Fence(AdValues.FirstName(r)))) + (sorted.Count > 50 ? " and more" : ""),
                Links: LinkTable(label, sorted.Take(50).Select(r => (AdValues.FirstName(r), r)))));
        }

        Add(answer, "ou", Fence(AdValues.Parent(user.Dn)));
        var flags = AdValues.Flags(uac).Where(f => f != "disabled").ToList();
        if (flags.Count > 0)
        {
            Add(answer, "flags", string.Join(", ", flags));
        }

        if (!source.Redacted)
        {
            foreach (string field in AdAttributes.Personal)
            {
                if (user.Text(field) is { } value)
                {
                    Add(answer, field, Fence(value));
                }
            }
        }

        var (groups, nested, partial) = GroupsOf(session, baseDn, user, deadline);
        string groupsLabel = "groups (" + groups.Count.ToString(CultureInfo.InvariantCulture) + (nested ? ", nested" : ", direct") + ")";
        answer.Fields.Add(new DirectoryField(groupsLabel, string.Join(", ", groups.Take(200).Select(g => Fence(g.Name))) + (groups.Count > 200 ? " and more" : ""),
            Links: LinkTable(groupsLabel, groups.Take(200))));
        if (source.Redacted)
        {
            answer.Notes.Add("phones, addresses and employee numbers left out (redacted)");
        }

        if (partial || reportsPartial)
        {
            answer.Notes.Add(DirectoryText.FirstRangeNote);
        }

        return answer;
    }

    /// <summary>A card's link list (a user's groups, its reports, a group's <c>member of</c>): one column, every row opening its own card.</summary>
    private static DirectoryTable LinkTable(string title, IEnumerable<(string Name, string Dn)> links)
    {
        var table = new DirectoryTable([new(DirectoryText.ColName, Flexible: true)], list: false) { Title = title };
        foreach (var (linkName, dn) in links)
        {
            table.Rows.Add(new DirectoryRow(Fence(linkName), [linkName], dn, linkName, Opens: true));
        }

        return table;
    }

    // ── A group ─────────────────────────────────────────────────────────────

    public static string Group(IDirectorySession session, DirectorySource source, string name, bool nested, int limit) =>
        DirectoryRender.ForModel(GroupAnswer(session, source, name, nested, "all", limit));

    /// <summary>
    /// A group's card and members (2026-10-15, the AD gaps plan, §1): its type, owner, mail, creation and the groups it is in (every one above it
    /// with <paramref name="nested"/>), then its members — direct, or at any depth through the groups below it, each with the group that brings it
    /// in — and the members by primary group, every one read (up to <see cref="DirectoryLimits.MaxCounted"/>), sorted by account, the first
    /// <paramref name="limit"/> listed; <paramref name="members"/> one kind alone (<see cref="MemberChoices"/>).
    /// </summary>
    public static DirectoryAnswer GroupAnswer(IDirectorySession session, DirectorySource source, string name, bool nested, string members, int limit, TimeZoneInfo? zone = null,
        DirectoryDeadline deadline = default, bool canSave = false, string? dn = null, int cap = DirectoryLimits.MaxCounted)
    {
        ArgumentNullException.ThrowIfNull(source);
        zone ??= TimeZoneInfo.Utc;
        string baseDn = source.Domain.BaseDn;
        var (group, candidates) = OneOf(session, baseDn, dn is null ? LdapFilter.GroupByName(name) : LdapFilter.ByDn(LdapFilter.Groups, dn), AdAttributes.Group, deadline.Limit);
        if (group is null)
        {
            return candidates.Count > 0 ? Ambiguous(name, candidates) : DirectoryAnswer.Refused(dn is null ? DirectoryText.NotFound("group", name, source.Where) : DirectoryText.Gone(name, source.Where));
        }

        var walk = Walk(session, baseDn, [group], nested, members, cap, deadline);
        // A cycle (A holds B, B holds A) brings the group back in through a nested one: never listed or counted as its own member (2026-10-20, a review's finding).
        var listed = walk.Members.Values.Where(m => Wanted(m, members) && !string.Equals(m.Dn, group.Dn, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => SortKey(m), StringComparer.OrdinalIgnoreCase).ToList();
        int total = listed.Count;
        int direct = listed.Count(m => m.Direct);
        int primary = listed.Count(m => !m.Direct && m.Primary);
        int through = total - direct - primary;
        string noun = members switch { "users" => "user", "computers" => "computer", "groups" => "group", _ => "member" } + (total == 1 ? "" : "s");
        string count = total.ToString("N0", CultureInfo.InvariantCulture) + (walk.Cut ? "+" : "");
        string paren = nested ? " (" + direct.ToString("N0", CultureInfo.InvariantCulture) + " direct, " + through.ToString("N0", CultureInfo.InvariantCulture) + " through other groups"
                + (primary > 0 ? ", " + primary.ToString("N0", CultureInfo.InvariantCulture) + " by primary group" : "") + ")"
            : primary > 0 ? " (" + direct.ToString("N0", CultureInfo.InvariantCulture) + " in its member list, " + primary.ToString("N0", CultureInfo.InvariantCulture) + " by primary group)" : "";
        var answer = Start("group", DateTimeOffset.UtcNow, zone);
        string account = group.Text("sAMAccountName") ?? name;
        answer.Self = new DirectoryRowTarget(group.Dn, account);
        answer.Partial = walk.Cut || walk.TimedOut;
        answer.Head = $"Group {Fence(account)} in {source.Where}: {count} {noun}, " + (nested ? "nested" : "direct") + paren
            + (total > limit ? "; " + DirectoryText.FirstListed(limit, canSave) : "")
            + (walk.Cut ? "; " + DirectoryText.CapNote(cap, false) : "")
            + (walk.TimedOut ? "; " + DirectoryText.StoppedAtTimeLimit : "")
            + (walk.GroupsCut ? "; " + DirectoryText.GroupsNotWalked : "");
        if (group.Text("description") is { } description)
        {
            Add(answer, "description", Fence(description));
        }

        if (AdValues.Integer(group.Text("groupType")) is { } type)
        {
            Add(answer, "type", AdValues.GroupKind(type));
        }

        if (group.Text("managedBy") is { } owner)
        {
            answer.Fields.Add(new DirectoryField("managed by", Fence(AdValues.FirstName(owner)), new DirectoryRowTarget(owner, AdValues.FirstName(owner))));
        }

        if (group.Text("mail") is { } mail)
        {
            Add(answer, "mail", Fence(mail));
        }

        if (AdValues.GeneralizedTime(group.Text("whenCreated")) is { } created)
        {
            Add(answer, "created", Time(created, zone));
        }

        AddMemberOf(session, answer, group, nested, deadline);
        var table = new DirectoryTable([new(DirectoryText.ColName, Flexible: true), new(DirectoryText.ColKind), new(DirectoryText.ColAccount), new(DirectoryText.ColVia, Flexible: true)])
        {
            Shown = limit,
            CutReserve = 600,
            CutNote = left => "… " + left.ToString(CultureInfo.InvariantCulture) + " more; lower limit or ask for one member",
        };
        foreach (var m in listed)
        {
            string via = m.Direct ? (nested ? DirectoryText.DirectWord : "") : m.Primary ? m.PrimaryGroup + " (" + DirectoryText.PrimaryGroupWord + ")" : m.Via.Count > 0 ? m.Via[0] + (m.Via.Count > 1 ? " +" + (m.Via.Count - 1).ToString(CultureInfo.InvariantCulture) : "") : "";
            string viaModel = m.Direct ? (nested ? " " + DirectoryText.DirectWord : "")
                : m.Primary ? " via " + Fence(m.PrimaryGroup ?? "") + " (" + DirectoryText.PrimaryGroupWord + ")"
                : m.Via.Count > 0 ? " via " + Fence(m.Via[0]) + (m.Via.Count > 1 ? " +" + (m.Via.Count - 1).ToString(CultureInfo.InvariantCulture) : "") : "";
            table.Rows.Add(new DirectoryRow(MemberLine(m) + viaModel, [m.Name, m.KindWord, m.Account, via.Length > 0 ? via : null], m.Dn, m.Name, Opens: m.Opens));
        }

        answer.Tables.Add(table);
        return answer;
    }

    /// <summary>The groups an object is in: its direct <c>memberOf</c>, and with <paramref name="nested"/> every group above, walked upward by base reads (cap 200).</summary>
    private static void AddMemberOf(IDirectorySession session, DirectoryAnswer answer, DirectoryEntry entry, bool nested, DirectoryDeadline deadline)
    {
        var (direct, partial) = entry.ValuesOrFirstRange("memberOf");
        var found = new List<string>(direct);
        var seen = new HashSet<string>(direct, StringComparer.OrdinalIgnoreCase);
        bool stopped = false;
        if (nested)
        {
            var queue = new Queue<string>(direct);
            while (queue.Count > 0 && found.Count < DirectoryLimits.MaxWalkedGroups)
            {
                if (!deadline.CanStart)
                {
                    stopped = true;
                    break;
                }

                string next = queue.Dequeue();
                try
                {
                    var read = session.Search(next, DirectoryScope.Base, "(objectClass=*)", ["memberOf"], 1, paged: false, deadline.Limit);
                    foreach (string above in read.Entries.Count > 0 ? read.Entries[0].Texts("memberOf") : [])
                    {
                        if (seen.Add(above) && found.Count < DirectoryLimits.MaxWalkedGroups)
                        {
                            found.Add(above);
                            queue.Enqueue(above);
                        }
                    }
                }
                catch (DirectoryException)
                {
                    // A group this account may not read: its own groups stay unknown.
                }
            }
        }

        if (found.Count == 0)
        {
            return;
        }

        var sorted = found.OrderBy(AdValues.FirstName, StringComparer.OrdinalIgnoreCase).ToList();
        string label = "member of (" + sorted.Count.ToString(CultureInfo.InvariantCulture) + (nested ? ", nested" : "") + ")";
        answer.Fields.Add(new DirectoryField(label, string.Join(", ", sorted.Select(g => Fence(AdValues.FirstName(g)))), Links: LinkTable(label, sorted.Select(g => (AdValues.FirstName(g), g)))));
        if (partial)
        {
            answer.Notes.Add(DirectoryText.FirstRangeNote);
        }

        if (stopped)
        {
            answer.Notes.Add(DirectoryText.StoppedAtTimeLimit);
        }
    }

    private static bool Wanted(WalkMember m, string members) => members switch
    {
        "users" => m.Kind == "user",
        "computers" => m.Kind == "computer",
        "groups" => m.Kind == "group",
        _ => true,
    };

    private static string SortKey(WalkMember m) => m.Account ?? m.Name;

    /// <summary>A member's line: <c>⟪Display⟫ (kind account)</c>; a contact and a foreign principal have no account, an unread member says why.</summary>
    private static string MemberLine(WalkMember m) =>
        Fence(m.Name) + " (" + m.KindWord + (m.Account is { } account ? " " + Plain(account) : "") + ")";

    /// <summary>A group's direct members by range retrieval (<c>member;range=0-*</c>, then on from where each answer ended), up to <paramref name="limit"/>.</summary>
    public static (IReadOnlyList<string> Dns, bool Truncated) Members(IDirectorySession session, string groupDn, int limit)
    {
        var (dns, truncated, _, _) = MembersOf(session, groupDn, limit, default);
        return (dns, truncated);
    }

    private static (IReadOnlyList<string> Dns, bool Truncated, bool TimedOut, int Searches) MembersOf(IDirectorySession session, string groupDn, int limit, DirectoryDeadline deadline)
    {
        var dns = new List<string>();
        int from = 0;
        int searches = 0;
        for (int round = 0; round < 64; round++)
        {
            if (!deadline.CanStart)
            {
                return (dns, false, true, searches);
            }

            searches++;
            var read = session.Search(groupDn, DirectoryScope.Base, "(objectClass=*)", ["member;range=" + from.ToString(CultureInfo.InvariantCulture) + "-*"], 1, paged: false, deadline.Limit);
            if (read.Entries.Count == 0)
            {
                break;
            }

            var entry = read.Entries[0];
            string? ranged = entry.RangedName("member");
            var values = ranged is null ? entry.Values("member") : entry.Attributes[ranged];
            dns.AddRange(values.Select(v => Encoding.UTF8.GetString(v)));
            if (dns.Count >= limit)
            {
                return (dns.Take(limit).ToList(), true, false, searches);
            }

            if (ranged is null || ranged.EndsWith("-*", StringComparison.Ordinal))
            {
                break;
            }

            int dash = ranged.LastIndexOf('-');
            if (!int.TryParse(ranged[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int last))
            {
                break;
            }

            from = last + 1;
        }

        return (dns, false, false, searches);
    }

    /// <summary>One object a walk found: its entry once resolved, how it is in (direct, through which groups, by primary group), or why it is unread.</summary>
    private sealed class WalkMember(string dn)
    {
        public string Dn { get; } = dn;

        public DirectoryEntry? Entry { get; set; }

        public string Kind { get; set; } = "";

        /// <summary>Why it has no entry: not readable, or in another domain.</summary>
        public string? State { get; set; }

        public bool Direct { get; set; }

        public bool Primary { get; set; }

        public string? PrimaryGroup { get; set; }

        /// <summary>The walked group whose RID is its <c>primaryGroupID</c>, by DN (<c>privileged</c> reaches its members through it).</summary>
        public string? PrimaryGroupDn { get; set; }

        /// <summary>The walked groups whose member list holds it, in the walk's order (the first is its "via").</summary>
        public List<string> Via { get; } = [];

        public string Name => Entry is { } e ? Display(e) : AdValues.FirstName(Dn);

        public string? Account => Entry?.Text("sAMAccountName");

        public string KindWord => State ?? Kind;

        public bool Opens => Entry is not null && Kind is "user" or "group" or "computer" or AdValues.KindServiceAccount;
    }

    private sealed class WalkResult
    {
        public Dictionary<string, WalkMember> Members { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The groups walked, the roots first, by DN, with their names.</summary>
        public List<(string Dn, string Name)> Groups { get; } = [];

        /// <summary>Each walked group's member DNs.</summary>
        public Dictionary<string, IReadOnlyList<string>> Edges { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Cut { get; set; }

        public bool TimedOut { get; set; }

        public bool GroupsCut { get; set; }
    }

    /// <summary>
    /// A group's members read forward (2026-10-15, the AD gaps plan, §0's spike against <c>samba_ad_dev</c>: 5,000 users in 200 groups nested
    /// five deep, the top group's members, the median of 5 runs after a warm-up: the in-chain rule 4,410 ms, it with the users-only term 4,390 ms,
    /// a walk by <c>memberOf</c> equality ORs 5,306 ms over 12 searches, this walk 603 ms over 400 searches; one <c>(memberOf=…)</c> alone took
    /// 62 ms, a scan — Samba does not index the back-link — while a batch of 50 DNs took 4 ms. So the walk reads each group's <c>member</c> by
    /// range retrieval and resolves the DNs 50 at a time (<see cref="LdapFilter.ByDns"/>), breadth first from <paramref name="roots"/>, a visited
    /// set ending cycles, at most <see cref="DirectoryLimits.MaxWalkedGroups"/> groups; then the members by primary group, one indexed
    /// <c>primaryGroupID</c> OR per 50 walked groups. Without <paramref name="nested"/>, the roots alone. A DN under the base that does not
    /// resolve is "not readable", one outside it "in another domain". Every search checks <paramref name="deadline"/> first and takes what it has
    /// left. A Windows DC's numbers are a check for <c>.notes/remote_admin_todo.md</c>.
    /// </summary>
    private static WalkResult Walk(IDirectorySession session, string baseDn, IReadOnlyList<DirectoryEntry> roots, bool nested, string members, int cap, DirectoryDeadline deadline)
    {
        long started = Stopwatch.GetTimestamp();
        var result = new WalkResult();
        var queue = new Queue<DirectoryEntry>(roots);
        var visited = new HashSet<string>(roots.Select(r => r.Dn), StringComparer.OrdinalIgnoreCase);
        var rootDns = new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase);
        var rids = new Dictionary<uint, (string Dn, string Name)>();
        int searches = 0;
        void Walked(DirectoryEntry group)
        {
            result.Groups.Add((group.Dn, Display(group)));
            if (group.Values("objectSid") is { Count: > 0 } sid && AdValues.Rid(sid[0]) is { } rid)
            {
                rids.TryAdd(rid, (group.Dn, Display(group)));
            }
        }

        foreach (var root in roots)
        {
            Walked(root);
        }

        while (queue.Count > 0 && !result.Cut)
        {
            var group = queue.Dequeue();
            var (dns, truncated, timedOut, rounds) = MembersOf(session, group.Dn, cap, deadline);
            searches += rounds;
            result.TimedOut |= timedOut;
            result.Edges[group.Dn] = dns;
            bool root = rootDns.Contains(group.Dn);
            var fresh = new List<string>();
            foreach (string dn in dns)
            {
                if (!result.Members.TryGetValue(dn, out var member))
                {
                    if (result.Members.Count >= cap)
                    {
                        result.Cut = true;
                        break;
                    }

                    member = new WalkMember(dn);
                    result.Members[dn] = member;
                    fresh.Add(dn);
                }

                if (root)
                {
                    member.Direct = true;
                }
                else
                {
                    member.Via.Add(Display(group));
                }
            }

            result.Cut |= truncated;
            foreach (var batch in fresh.Chunk(DirectoryLimits.SidBatch))
            {
                if (!deadline.CanStart)
                {
                    result.TimedOut = true;
                    break;
                }

                searches++;
                var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.ByDns(batch), AdAttributes.Member, batch.Length + 10, paged: false, deadline.Limit);
                result.TimedOut |= found.TimedOut;
                foreach (var e in found.Entries)
                {
                    if (!result.Members.TryGetValue(e.Dn, out var member))
                    {
                        continue;
                    }

                    member.Entry = e;
                    member.Kind = AdValues.KindOf(e.Texts("objectClass"));
                    if (member.Kind == "group" && nested && visited.Add(e.Dn))
                    {
                        if (result.Groups.Count < DirectoryLimits.MaxWalkedGroups)
                        {
                            queue.Enqueue(e);
                            Walked(e);
                        }
                        else
                        {
                            result.GroupsCut = true;
                        }
                    }
                }

                foreach (string dn in batch)
                {
                    var member = result.Members[dn];
                    if (member.Entry is null)
                    {
                        member.State = IsUnder(dn, baseDn) ? DirectoryText.NotReadable : DirectoryText.OtherDomain;
                    }
                }
            }

            if (result.TimedOut)
            {
                break;
            }
        }

        // The members by primary group: in no member list (how every user is in Domain Users), found by the indexed primaryGroupID.
        string? term = members switch { "users" => LdapFilter.Users, "computers" => LdapFilter.Computers, _ => null };
        if (members != "groups" && !result.Cut && !result.TimedOut)
        {
            foreach (var batch in rids.Keys.Chunk(DirectoryLimits.SidBatch))
            {
                if (!deadline.CanStart)
                {
                    result.TimedOut = true;
                    break;
                }

                searches++;
                var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.PrimaryGroupIn(term, batch), [.. AdAttributes.Member, "primaryGroupID"], cap + 1, paged: true, deadline.Limit);
                result.TimedOut |= found.TimedOut;
                foreach (var e in found.Entries)
                {
                    if (result.Members.ContainsKey(e.Dn))
                    {
                        continue;
                    }

                    if (result.Members.Count >= cap)
                    {
                        result.Cut = true;
                        break;
                    }

                    uint rid = (uint)(AdValues.Integer(e.Text("primaryGroupID")) ?? 0);
                    bool known = rids.TryGetValue(rid, out var primaryGroup);
                    result.Members[e.Dn] = new WalkMember(e.Dn)
                    {
                        Entry = e,
                        Kind = AdValues.KindOf(e.Texts("objectClass")),
                        Primary = true,
                        PrimaryGroup = known ? primaryGroup.Name : rid.ToString(CultureInfo.InvariantCulture),
                        PrimaryGroupDn = known ? primaryGroup.Dn : null,
                    };
                }
            }
        }

        DiagnosticLog.Debug(DirectoryText.Category, $"walk of {roots.Count.ToString(CultureInfo.InvariantCulture)} group(s): {result.Groups.Count.ToString(CultureInfo.InvariantCulture)} walked, "
            + $"{searches.ToString(CultureInfo.InvariantCulture)} searches, {result.Members.Count.ToString(CultureInfo.InvariantCulture)} entries, "
            + $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms" + (result.TimedOut ? ", stopped at the time limit" : ""));
        return result;
    }

    private static bool IsUnder(string dn, string baseDn) => dn.EndsWith("," + baseDn, StringComparison.OrdinalIgnoreCase) || string.Equals(dn, baseDn, StringComparison.OrdinalIgnoreCase);

    // ── A computer ──────────────────────────────────────────────────────────

    public static string Computer(IDirectorySession session, DirectorySource source, string name, TimeZoneInfo zone, DirectoryDeadline deadline = default) =>
        DirectoryRender.ForModel(ComputerAnswer(session, source, name, zone, deadline: deadline));

    /// <summary>A computer's card — or a service account's, which <c>ad_computer</c> finds by name too (2026-10-15, the AD gaps plan) — by a name or a DN.</summary>
    public static DirectoryAnswer ComputerAnswer(IDirectorySession session, DirectorySource source, string name, TimeZoneInfo zone, string? dn = null, DirectoryDeadline deadline = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        string baseDn = source.Domain.BaseDn;
        var (computer, candidates) = OneOf(session, baseDn, dn is null ? LdapFilter.ComputerByName(name) : LdapFilter.ByDn("(|" + LdapFilter.Computers + LdapFilter.ServiceAccounts + ")", dn), AdAttributes.Computer, deadline.Limit);
        if (computer is null)
        {
            return candidates.Count > 0 ? Ambiguous(name, candidates) : DirectoryAnswer.Refused(dn is null ? DirectoryText.NotFound("computer", name, source.Where) : DirectoryText.Gone(name, source.Where));
        }

        bool service = AdValues.KindOf(computer.Texts("objectClass")) == AdValues.KindServiceAccount;
        int uac = (int)(AdValues.Integer(computer.Text("userAccountControl")) ?? 0);
        var answer = Start("computer", DateTimeOffset.UtcNow, zone);
        answer.Self = new DirectoryRowTarget(computer.Dn, computer.Text("cn") ?? name);
        answer.Head = (service ? "Service account " : "Computer ") + $"{Fence(computer.Text("cn") ?? name)} in {source.Where}: " + ((uac & AdValues.AccountDisable) != 0 ? "disabled" : "enabled")
            + ((uac & AdValues.ServerTrustAccount) != 0 ? ", a domain controller" : "");
        if (computer.Text("dNSHostName") is { } host)
        {
            Add(answer, "dns name", Fence(host));
        }

        if (computer.Text("operatingSystem") is { } os)
        {
            Add(answer, "os", Fence(os + (computer.Text("operatingSystemVersion") is { } version ? " " + version : "")));
        }

        Add(answer, "last sign-in", (AdValues.FileTime(computer.Text("lastLogonTimestamp")) is { } logon ? Time(logon, zone) : DirectoryText.NeverWord) + " (" + DirectoryText.StaleNote + ")");
        if (AdValues.FileTime(computer.Text("pwdLastSet")) is { } set)
        {
            Add(answer, "machine password set", Time(set, zone));
        }

        Add(answer, "ou", Fence(AdValues.Parent(computer.Dn)));
        foreach (string field in (string[])["description", "location"])
        {
            if (computer.Text(field) is { } value)
            {
                Add(answer, field, Fence(value));
            }
        }

        if (computer.Text("managedBy") is { } owner)
        {
            answer.Fields.Add(new DirectoryField("managed by", Fence(AdValues.FirstName(owner)), new DirectoryRowTarget(owner, AdValues.FirstName(owner))));
        }

        var groups = computer.Texts("memberOf").Select(g => (Name: AdValues.FirstName(g), Dn: g)).ToList();
        if (PrimaryGroupOf(session, baseDn, computer, deadline) is { } primary && !groups.Any(g => string.Equals(g.Dn, primary.Dn, StringComparison.OrdinalIgnoreCase)))
        {
            groups.Add(primary);
        }

        if (groups.Count > 0)
        {
            var sorted = groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            string label = "groups (" + sorted.Count.ToString(CultureInfo.InvariantCulture) + ")";
            answer.Fields.Add(new DirectoryField(label, string.Join(", ", sorted.Select(g => Fence(g.Name))), Links: LinkTable(label, sorted)));
        }

        return answer;
    }

    // ── Finds ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ad_find</c>: the stored attributes filtered on the server, the constructed ones refined here, all inside the 1,000-entry cap (the client
    /// step counted too); never-signed-in accounts listed apart for the stale kinds.
    /// </summary>
    public static string Find(IDirectorySession session, DirectorySource source, string kind, int days, string? words, int limit, DateTimeOffset now, TimeZoneInfo zone) =>
        DirectoryRender.ForModel(FindAnswer(session, source, new FindOptions(kind, days, words, limit), now, zone));

    public static string Find(IDirectorySession session, DirectorySource source, FindOptions options, DateTimeOffset now, TimeZoneInfo zone) =>
        DirectoryRender.ForModel(FindAnswer(session, source, options, now, zone));

    /// <summary>
    /// A find's answer (2026-10-15, the AD gaps plan, §2 and §3): the kinds of <see cref="FindKinds"/>, each a filter built by
    /// <see cref="LdapFilter"/> and a list in the <c>account  ⟪display⟫  detail</c> shape, optionally held to one OU (<see cref="Branch"/>).
    /// </summary>
    public static DirectoryAnswer FindAnswer(IDirectorySession session, DirectorySource source, FindOptions o, DateTimeOffset now, TimeZoneInfo zone, DirectoryDeadline deadline = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(o);
        string baseDn = source.Domain.BaseDn;
        string scope = baseDn;
        string where = source.Where;
        if (o.Ou is { } ou)
        {
            var (branch, candidates) = Branch(session, baseDn, ou, deadline);
            if (branch is null)
            {
                return candidates.Count > 0 ? AmbiguousOu(ou, candidates, baseDn) : DirectoryAnswer.Refused(DirectoryText.NotFound("OU", ou, source.Where));
            }

            scope = branch.Dn;
            where = PathOf(branch, baseDn) + " (" + source.Where + ")";
        }

        if (o.Field == "office" && source.Redacted)
        {
            return DirectoryAnswer.Refused(DirectoryText.OfficeRedacted);
        }

        var answer = Start("find", now, zone);
        var main = new DirectoryTable(Columns(o), list: true) { Shown = o.Limit, CutNote = _ => "… more; lower limit or narrow the search" };
        DirectoryTable? apart = null;
        bool cut = false;
        bool timed = false;
        string what;
        int cap = o.Cap;
        DirectoryResults Search(string filter, IReadOnlyList<string> attributes, int size)
        {
            if (!deadline.CanStart)
            {
                timed = true;
                return new DirectoryResults([], false, true);
            }

            var found = session.Search(scope, DirectoryScope.Subtree, filter, attributes, size, paged: true, deadline.Limit);
            timed |= found.TimedOut;
            cut |= found.Truncated && !found.TimedOut;
            return found;
        }

        switch (o.Kind)
        {
            case "locked":
            {
                what = "locked users";
                var policies = new PolicyCache(session, baseDn, deadline);
                foreach (var e in Search(LdapFilter.LockedCandidates, AdAttributes.Find, cap).Entries)
                {
                    if (policies.For(e) is not { } own)
                    {
                        timed = true;
                        break;
                    }

                    var state = State(e, own, now);
                    if (state.Locked)
                    {
                        main.Rows.Add(Row(Acct(e) + "  " + Fence(Display(e)) + "  locked " + (state.LockedAt is { } at ? Time(at, zone) : "") + (state.Unlocks is { } u ? ", unlocks " + Time(u, zone) : ""),
                            e, [e.Text("sAMAccountName"), Display(e), DirectoryCell.At(state.LockedAt), DirectoryCell.At(state.Unlocks)]));
                    }
                }

                break;
            }

            case "password_expiring":
            {
                what = "passwords ending within " + o.Days.ToString(CultureInfo.InvariantCulture) + " days";
                var policies = new PolicyCache(session, baseDn, deadline);
                var limitDate = now.AddDays(o.Days);
                var states = new List<(DirectoryEntry Entry, AdAccountState State)>();
                foreach (var e in Search(LdapFilter.ExpiringCandidates, AdAttributes.Find, cap).Entries)
                {
                    if (policies.For(e) is not { } own)
                    {
                        timed = true;
                        break;
                    }

                    states.Add((e, State(e, own, now)));
                }

                foreach (var (e, state) in states.Where(p => p.State.Expires is { } x && x <= limitDate).OrderBy(p => p.State.Expires))
                {
                    main.Rows.Add(Row(Acct(e) + "  " + Fence(Display(e)) + "  " + (state.Expires < now ? "expired " : "expires ") + Time(state.Expires!.Value, zone) + " (" + state.ExpiresHow + ")",
                        e, [e.Text("sAMAccountName"), Display(e), DirectoryCell.At(state.Expires), state.ExpiresHow]));
                }

                break;
            }

            case "disabled":
            {
                what = "disabled users";
                foreach (var e in Search(LdapFilter.DisabledUsers, AdAttributes.Find, cap).Entries)
                {
                    main.Rows.Add(Row(Acct(e) + "  " + Fence(Display(e)), e, [e.Text("sAMAccountName"), Display(e)]));
                }

                break;
            }

            case "stale_users":
            case "stale_computers":
            {
                bool computers = o.Kind == "stale_computers";
                what = (computers ? "computers" : "users") + " not signed in for " + o.Days.ToString(CultureInfo.InvariantCulture) + " days";
                long cutoff = AdValues.ToFileTime(now.AddDays(-o.Days));
                var found = Search(LdapFilter.StaleSince(computers, cutoff), AdAttributes.Find, cap);
                var never = Search(LdapFilter.NeverSignedIn(computers), AdAttributes.Find, Math.Max(1, cap - found.Entries.Count));
                string NameOf(DirectoryEntry e) => e.Text(computers ? "dNSHostName" : "displayName") ?? AdValues.FirstName(e.Dn);
                foreach (var e in found.Entries.OrderBy(e => AdValues.Integer(e.Text("lastLogonTimestamp")) ?? 0))
                {
                    var last = AdValues.FileTime(e.Text("lastLogonTimestamp"));
                    main.Rows.Add(Row(Acct(e) + "  " + Fence(NameOf(e)) + "  last " + (last is { } l ? Time(l, zone) : "?"), e, [e.Text("sAMAccountName"), NameOf(e), DirectoryCell.At(last)]));
                }

                apart = new DirectoryTable(main.Columns)
                {
                    Title = "never signed in (" + never.Entries.Count.ToString(CultureInfo.InvariantCulture) + ")",
                    TitleInModel = true,
                    Section = "never signed in",
                    Shown = Math.Max(10, o.Limit / 2),
                    CutReserve = 400,
                };
                foreach (var e in never.Entries)
                {
                    apart.Rows.Add(Row(Acct(e) + "  " + Fence(NameOf(e)), e, [e.Text("sAMAccountName"), NameOf(e), DirectoryCell.None]));
                }

                break;
            }

            case "name":
            {
                what = o.Type is null ? "names holding \"" + (o.Words ?? "") + "\"" : Plural(o.Type) + " whose names or descriptions hold \"" + (o.Words ?? "") + "\"";
                var found = Search(LdapFilter.NameContains(o.Words ?? "", o.Type), o.Type == "ou" ? AdAttributes.Ou : AdAttributes.Find, cap);
                foreach (var e in found.Entries)
                {
                    main.Rows.Add(o.Type switch
                    {
                        null => Row(Acct(e) + "  " + KindOf(e) + "  " + Fence(Display(e)), e, [e.Text("sAMAccountName"), KindOf(e), Display(e)]),
                        "ou" => OuRow(e, baseDn),
                        "group" => Row(Acct(e) + "  " + Fence(Display(e)) + "  " + GroupType(e) + Described(e), e, [e.Text("sAMAccountName"), Display(e), GroupType(e), e.Text("description")]),
                        "contact" => Row(Fence(Display(e)) + Described(e), e, [null, Display(e), e.Text("description")]),
                        _ => Row(Acct(e) + "  " + Fence(Display(e)) + Described(e), e, [e.Text("sAMAccountName"), Display(e), e.Text("description")]),
                    });
                }

                break;
            }

            case "field":
            {
                string type = o.Type ?? "user";
                string field = o.Field ?? "description";
                string attribute = AdAttributes.Fields[field];
                what = Plural(type) + " whose " + field + " holds \"" + (o.Words ?? "") + "\"";
                var found = Search(LdapFilter.FieldContains(type, attribute, o.Words ?? ""), type == "ou" ? AdAttributes.Ou : [.. AdAttributes.Find, attribute], cap);
                foreach (var e in found.Entries)
                {
                    main.Rows.Add(type == "ou" ? OuRow(e, baseDn)
                        : Row((type == "contact" ? "" : Acct(e) + "  ") + Fence(Display(e)) + "  " + Fence(e.Text(attribute) ?? ""), e, [e.Text("sAMAccountName"), Display(e), e.Text(attribute)]));
                }

                break;
            }

            case "os":
            {
                what = "computers whose OS holds \"" + (o.Words ?? "") + "\"";
                foreach (var e in Search(LdapFilter.OsContains(o.Words ?? ""), AdAttributes.Find, cap).Entries)
                {
                    var last = AdValues.FileTime(e.Text("lastLogonTimestamp"));
                    string host = e.Text("dNSHostName") ?? AdValues.FirstName(e.Dn);
                    string system = (e.Text("operatingSystem") ?? "") + (e.Text("operatingSystemVersion") is { } v ? " " + v : "");
                    main.Rows.Add(Row(Acct(e) + "  " + Fence(host) + "  " + Fence(system) + "  last " + (last is { } l ? Time(l, zone) : DirectoryText.NeverWord),
                        e, [e.Text("sAMAccountName"), host, e.Text("operatingSystem"), e.Text("operatingSystemVersion"), DirectoryCell.At(last)]));
                }

                break;
            }

            case "password_never_expires":
            {
                what = "users whose password never expires";
                foreach (var e in Search(LdapFilter.PasswordNeverExpires, AdAttributes.Find, cap).Entries)
                {
                    bool disabled = ((AdValues.Integer(e.Text("userAccountControl")) ?? 0) & AdValues.AccountDisable) != 0;
                    main.Rows.Add(Row(Acct(e) + "  " + Fence(Display(e)) + (disabled ? "  disabled" : ""), e, [e.Text("sAMAccountName"), Display(e), disabled ? "disabled" : null]));
                }

                break;
            }

            case "account_expiring":
            {
                what = "accounts expiring within " + o.Days.ToString(CultureInfo.InvariantCulture) + " days";
                long from = AdValues.ToFileTime(now);
                foreach (var e in Search(LdapFilter.AccountExpiresBetween(from, AdValues.ToFileTime(now.AddDays(o.Days))), AdAttributes.Find, cap).Entries.OrderBy(e => AdValues.Integer(e.Text("accountExpires"))))
                {
                    var ends = AdValues.FileTime(e.Text("accountExpires"));
                    main.Rows.Add(Row(Acct(e) + "  " + Fence(Display(e)) + "  expires " + (ends is { } x ? Time(x, zone) : "?"), e, [e.Text("sAMAccountName"), Display(e), DirectoryCell.At(ends)]));
                }

                var expired = Search(LdapFilter.AccountExpiresBetween(1, from), AdAttributes.Find, Math.Max(1, cap - main.Rows.Count));
                apart = new DirectoryTable(main.Columns)
                {
                    Title = "already expired (" + expired.Entries.Count.ToString(CultureInfo.InvariantCulture) + ")",
                    TitleInModel = true,
                    Section = "already expired",
                    Shown = Math.Max(10, o.Limit / 2),
                    CutReserve = 400,
                };
                foreach (var e in expired.Entries.OrderByDescending(e => AdValues.Integer(e.Text("accountExpires"))))
                {
                    var ends = AdValues.FileTime(e.Text("accountExpires"));
                    apart.Rows.Add(Row(Acct(e) + "  " + Fence(Display(e)) + "  expired " + (ends is { } x ? Time(x, zone) : "?"), e, [e.Text("sAMAccountName"), Display(e), DirectoryCell.At(ends)]));
                }

                break;
            }

            case "created":
            {
                what = (o.Type is null ? "objects" : Plural(o.Type)) + " created in the last " + o.Days.ToString(CultureInfo.InvariantCulture) + " days";
                var found = Search(LdapFilter.CreatedSince(o.Type, now.AddDays(-o.Days)), o.Type == "ou" ? AdAttributes.Ou : AdAttributes.Find, cap);
                foreach (var e in found.Entries.OrderByDescending(e => e.Text("whenCreated"), StringComparer.Ordinal))
                {
                    var made = AdValues.GeneralizedTime(e.Text("whenCreated"));
                    string at = made is { } m ? Time(m, zone) : "?";
                    main.Rows.Add(o.Type == "ou"
                        ? OuRow(e, baseDn, "  created " + at, DirectoryCell.At(made))
                        : Row((o.Type == "contact" ? "" : Acct(e) + "  ") + KindOf(e) + "  " + Fence(Display(e)) + "  created " + at, e, [e.Text("sAMAccountName"), KindOf(e), Display(e), DirectoryCell.At(made)]));
                }

                break;
            }

            case "privileged":
            {
                what = "privileged accounts";
                (apart, var missing) = Privileged(session, baseDn, main, cap, deadline, ref cut, ref timed);
                if (missing.Count > 0)
                {
                    answer.Notes.Add(DirectoryText.MissingProtected(missing));
                }

                break;
            }

            case "empty_groups":
            {
                what = "empty groups";
                var candidates = Search(LdapFilter.EmptyGroupCandidates, AdAttributes.Find, cap).Entries;
                var used = new HashSet<uint>();
                var rids = candidates.Select(e => e.Values("objectSid") is { Count: > 0 } s ? AdValues.Rid(s[0]) : null).OfType<uint>().Distinct().ToList();
                foreach (var batch in rids.Chunk(DirectoryLimits.SidBatch))
                {
                    if (!deadline.CanStart)
                    {
                        timed = true;
                        break;
                    }

                    var primary = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.PrimaryGroupIn(null, batch), ["primaryGroupID"], DirectoryLimits.MaxCounted, paged: true, deadline.Limit);
                    used.UnionWith(primary.Entries.Select(e => (uint)(AdValues.Integer(e.Text("primaryGroupID")) ?? 0)));
                }

                foreach (var e in candidates.Where(e => !(e.Values("objectSid") is { Count: > 0 } s && AdValues.Rid(s[0]) is { } rid && used.Contains(rid))).OrderBy(e => e.Text("sAMAccountName") ?? Display(e), StringComparer.OrdinalIgnoreCase))
                {
                    main.Rows.Add(Row(Acct(e) + "  " + Fence(Display(e)) + "  " + GroupType(e) + Described(e), e, [e.Text("sAMAccountName"), Display(e), GroupType(e), e.Text("description")]));
                }

                break;
            }

            default:
                return DirectoryAnswer.Refused(DirectoryText.BadKind(o.Kind));
        }

        int count = main.Rows.Count;
        answer.Partial = cut || timed;
        answer.Head = $"{what} in {where}: {count.ToString(CultureInfo.InvariantCulture)}" + (count > o.Limit ? $", the first {o.Limit.ToString(CultureInfo.InvariantCulture)} shown" : "")
            + (cut ? "; " + DirectoryText.CapNote(cap, o.CanSave && cap < DirectoryLimits.MaxCounted) : "")
            + (timed ? "; " + DirectoryText.StoppedAtTimeLimit : "");
        answer.Tables.Add(main);
        if (apart is { Rows.Count: > 0 })
        {
            answer.Tables.Add(apart);
        }

        return answer;
    }

    /// <summary>A find's columns: the account and the name, then the kind's own (a kind column where one list can mix kinds); an OU list's path and description.</summary>
    private static IReadOnlyList<DirectoryColumn> Columns(FindOptions o)
    {
        DirectoryColumn account = new(DirectoryText.ColAccount), name = new(DirectoryText.ColName, Flexible: true), kind = new(DirectoryText.ColKind),
            description = new(DirectoryText.ColDescription, Flexible: true);
        if (o.Type == "ou" && o.Kind is "name" or "field" or "created")
        {
            return o.Kind == "created" ? [new(DirectoryText.ColPath, Flexible: true), description, new(DirectoryText.ColCreated, Date: true)] : [new(DirectoryText.ColPath, Flexible: true), description];
        }

        return o.Kind switch
        {
            "locked" => [account, name, new(DirectoryText.ColLockedSince, Date: true), new(DirectoryText.ColUnlocks, Date: true)],
            "password_expiring" => [account, name, new(DirectoryText.ColExpires, Date: true), new(DirectoryText.ColHow)],
            "stale_users" or "stale_computers" => [account, name, new(DirectoryText.ColLastSignIn, Date: true)],
            "name" => o.Type switch
            {
                null => [account, kind, name],
                "group" => [account, name, new(DirectoryText.ColType), description],
                _ => [account, name, description],
            },
            "field" => [account, name, new(o.Field ?? DirectoryText.ColDescription, Flexible: true)],
            "os" => [account, name, new(DirectoryText.ColOs, Flexible: true), new(DirectoryText.ColVersion), new(DirectoryText.ColLastSignIn, Date: true)],
            "password_never_expires" => [account, name, new(DirectoryText.ColState)],
            "account_expiring" => [account, name, new(DirectoryText.ColExpires, Date: true)],
            "created" => [account, kind, name, new(DirectoryText.ColCreated, Date: true)],
            "privileged" => [account, kind, name, new(DirectoryText.ColGroups, Flexible: true), new(DirectoryText.ColVia, Flexible: true)],
            "empty_groups" => [account, name, new(DirectoryText.ColType), description],
            _ => [account, name],
        };
    }

    /// <summary>The protected groups of Microsoft's AdminSDHolder list (Appendix C), by RID under the domain's SID, then the builtin ones.</summary>
    private static readonly (uint Rid, string Name)[] DomainProtected = [(512, "Domain Admins"), (518, "Schema Admins"), (519, "Enterprise Admins"), (526, "Key Admins"), (527, "Enterprise Key Admins")];

    private static readonly (string Sid, string Name)[] BuiltinProtected =
        [("S-1-5-32-544", "Administrators"), ("S-1-5-32-548", "Account Operators"), ("S-1-5-32-549", "Server Operators"), ("S-1-5-32-550", "Print Operators"), ("S-1-5-32-551", "Backup Operators"), ("S-1-5-32-552", "Replicator")];

    /// <summary>
    /// <c>privileged</c> (2026-10-15, the AD gaps plan, §3): the members at any depth of the protected groups (found by SID; Domain Controllers and
    /// Read-only DCs left out, their members being DCs by design) — users, computers, service accounts, each with its groups and the group it
    /// comes through — in one walk over all the roots; then, apart, the objects marked <c>adminCount=1</c> the walk did not reach (not the walked
    /// groups, which carry it too, nor a DC), the built-in Administrator (500) and krbtgt (502) marked expected. A protected group the domain
    /// does not have (Key Admins before 2016, Schema and Enterprise Admins outside the forest root) is named.
    /// </summary>
    private static (DirectoryTable? Apart, IReadOnlyList<string> Missing) Privileged(IDirectorySession session, string baseDn, DirectoryTable main, int cap, DirectoryDeadline deadline, ref bool cut, ref bool timed)
    {
        var head = session.Search(baseDn, DirectoryScope.Base, "(objectClass=*)", ["objectSid"], 1, paged: false, deadline.Limit);
        string? domainSid = head.Entries.Count > 0 && head.Entries[0].Values("objectSid") is { Count: > 0 } sid ? AdValues.Sid(sid[0]) : null;
        var wanted = new List<(string Sid, string Name)>();
        if (domainSid is not null)
        {
            wanted.AddRange(DomainProtected.Select(p => (domainSid + "-" + p.Rid.ToString(CultureInfo.InvariantCulture), p.Name)));
        }

        wanted.AddRange(BuiltinProtected);
        var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.BySids(wanted.Select(w => AdValues.SidBytes(w.Sid)!)), AdAttributes.Member, wanted.Count + 5, paged: false, deadline.Limit);
        var roots = found.Entries.ToList();
        var foundSids = roots.Select(r => r.Values("objectSid") is { Count: > 0 } s ? AdValues.Sid(s[0]) : null).ToHashSet(StringComparer.Ordinal);
        var missing = wanted.Where(w => !foundSids.Contains(w.Sid)).Select(w => w.Name).ToList();
        var walk = Walk(session, baseDn, roots, nested: true, "all", cap, deadline);
        cut |= walk.Cut;
        timed |= walk.TimedOut;

        // Which protected groups reach each member, and through which group: a walk from each root over the edges read.
        var reach = new Dictionary<string, (List<string> Roots, string? Via)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            string rootName = Display(root);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Dn };
            var queue = new Queue<(string Dn, string? Via)>([(root.Dn, null)]);
            while (queue.Count > 0)
            {
                var (dn, via) = queue.Dequeue();
                foreach (string child in walk.Edges.TryGetValue(dn, out var children) ? children : [])
                {
                    if (!seen.Add(child) || !walk.Members.TryGetValue(child, out var member))
                    {
                        continue;
                    }

                    if (!reach.TryGetValue(child, out var known))
                    {
                        reach[child] = known = ([], null);
                    }

                    if (!known.Roots.Contains(rootName))
                    {
                        known.Roots.Add(rootName);
                    }

                    if (known.Via is null && via is not null)
                    {
                        reach[child] = (known.Roots, via);
                    }

                    if (member.Kind == "group")
                    {
                        queue.Enqueue((child, member.Name));
                    }
                }
            }
        }

        // The members by primary group (a Domain Admin whose primaryGroupID is 512 is in no member list): reached by the roots that reach that
        // group, or the root it is (2026-10-20, a review's finding: they were left out, then listed among the leftovers as no longer protected).
        var rootNames = roots.ToDictionary(r => r.Dn, Display, StringComparer.OrdinalIgnoreCase);
        foreach (var m in walk.Members.Values.Where(m => m.Primary && m.PrimaryGroupDn is not null))
        {
            var through = new List<string>();
            if (rootNames.TryGetValue(m.PrimaryGroupDn!, out var rootName))
            {
                through.Add(rootName);
            }

            if (reach.TryGetValue(m.PrimaryGroupDn!, out var above))
            {
                through.AddRange(above.Roots.Where(r => !through.Contains(r)));
            }

            if (through.Count > 0)
            {
                reach[m.Dn] = (through, rootName is null ? m.PrimaryGroup : null);
            }
        }

        var accounts = walk.Members.Values.Where(m => m.Kind is "user" or "computer" or AdValues.KindServiceAccount && reach.ContainsKey(m.Dn)).OrderBy(SortKey, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var m in accounts)
        {
            var (inRoots, via) = reach[m.Dn];
            main.Rows.Add(new DirectoryRow(Plain(m.Account ?? "?") + "  " + m.Kind + "  " + Fence(m.Name) + "  " + string.Join(", ", inRoots.Select(Fence)) + (via is null ? "" : " via " + Fence(via)),
                [m.Account, m.Kind, m.Name, string.Join(", ", inRoots), via], m.Dn, m.Name, Opens: m.Opens));
        }

        // The objects once protected the walk did not reach: neither a member, nor a walked group, nor a DC or the DCs' groups.
        if (!deadline.CanStart)
        {
            timed = true;
            return (null, missing);
        }

        var marked = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.AdminCount, AdAttributes.Find, cap, paged: true, deadline.Limit);
        var walked = walk.Groups.Select(g => g.Dn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apart = new DirectoryTable([new(DirectoryText.ColAccount), new(DirectoryText.ColKind), new(DirectoryText.ColName, Flexible: true), new(DirectoryText.ColNote, Flexible: true)])
        {
            TitleInModel = true,
            Section = DirectoryText.LeftoversSection,
            CutReserve = 400,
        };
        foreach (var e in marked.Entries.OrderBy(e => e.Text("sAMAccountName") ?? Display(e), StringComparer.OrdinalIgnoreCase))
        {
            string kind = KindOf(e);
            uint? rid = e.Values("objectSid") is { Count: > 0 } s ? AdValues.Rid(s[0]) : null;
            int uac = (int)(AdValues.Integer(e.Text("userAccountControl")) ?? 0);
            if (reach.ContainsKey(e.Dn) || walked.Contains(e.Dn) || (kind == "group" && rid is 516 or 521) || (uac & (AdValues.ServerTrustAccount | 0x4000000)) != 0)
            {
                continue;
            }

            string note = rid is 500 or 502 ? DirectoryText.ExpectedNote : DirectoryText.LeftoverNote;
            apart.Rows.Add(Row(Acct(e) + "  " + kind + "  " + Fence(Display(e)) + "  " + note, e, [e.Text("sAMAccountName"), kind, Display(e), note]));
        }

        apart.Title = DirectoryText.LeftoversTitle + " (" + apart.Rows.Count.ToString(CultureInfo.InvariantCulture) + ")";
        return (apart, missing);
    }

    // ── OUs ─────────────────────────────────────────────────────────────────

    /// <summary>The containers the tree hides when the domain marks none advanced-view-only (what Active Directory Users and Computers hides).</summary>
    public static readonly IReadOnlySet<string> AdvancedContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Program Data", "Keys", "LostAndFound", "Infrastructure", "NTDS Quotas", "TPM Devices",
    };

    /// <summary>
    /// An OU a call names (2026-10-15, the AD gaps plan): a DN, a path (<c>Neon/Engineering</c>, a <c>/</c> in a name written <c>\/</c> and a
    /// <c>\</c> as <c>\\</c>, canonicalName's escaping) or a bare name; only an OU, a container or the builtin domain found by a search under the
    /// domain's base (so a Configuration or another domain's DN is refused, never searched). Several → the candidates.
    /// </summary>
    public static (DirectoryEntry? Found, IReadOnlyList<DirectoryEntry> Candidates) Branch(IDirectorySession session, string baseDn, string ou, DirectoryDeadline deadline = default)
    {
        ArgumentNullException.ThrowIfNull(ou);
        string text = ou.Trim();
        if (LooksLikeDn(text))
        {
            var byDn = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.ByDn(LdapFilter.OuLike, text), AdAttributes.Ou, 2, paged: false, deadline.Limit);
            return byDn.Entries.Count == 1 ? (byDn.Entries[0], []) : (null, []);
        }

        var parts = AdValues.PathParts(text);
        if (parts.Count == 0)
        {
            return (null, []);
        }

        var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.OuNamed(parts[^1]), AdAttributes.Ou, 50, paged: false, deadline.Limit).Entries;
        var matches = parts.Count > 1
            ? found.Where(e => AdValues.PathParts(PathOf(e, baseDn)).SequenceEqual(parts, StringComparer.OrdinalIgnoreCase)).ToList()
            : found.ToList();
        return matches.Count == 1 ? (matches[0], []) : (null, matches);
    }

    private static bool LooksLikeDn(string text) =>
        text.Contains('=', StringComparison.Ordinal) && (text.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || text.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) || text.StartsWith("DC=", StringComparison.OrdinalIgnoreCase));

    /// <summary>An OU's path in the domain: its computed <c>canonicalName</c>, else worked out from its DN.</summary>
    private static string PathOf(DirectoryEntry e, string baseDn) => e.Text("canonicalName") is { } canonical ? AdValues.PathOfCanonical(canonical) : AdValues.CanonicalPath(e.Dn, baseDn);

    private static DirectoryAnswer AmbiguousOu(string name, IReadOnlyList<DirectoryEntry> candidates, string baseDn)
    {
        var table = new DirectoryTable([new(DirectoryText.ColPath, Flexible: true), new(DirectoryText.ColDescription, Flexible: true)]);
        foreach (var e in candidates.OrderBy(e => PathOf(e, baseDn), StringComparer.OrdinalIgnoreCase))
        {
            table.Rows.Add(OuRow(e, baseDn));
        }

        return new DirectoryAnswer
        {
            Error = DirectoryText.AmbiguousOu(name, table.Rows.Select(r => Fence(r.Cells[0]?.Text ?? "")).ToList()),
            Candidates = table,
            CandidatesTitle = DirectoryText.AmbiguousOuTitle(name, candidates.Count),
        };
    }

    /// <summary>An OU as a row: its path and description (and a created cell when asked); its card opens on a double-click.</summary>
    private static DirectoryRow OuRow(DirectoryEntry e, string baseDn, string? extraModel = null, DirectoryCell? extra = null, int indent = 0)
    {
        string path = PathOf(e, baseDn);
        string? description = e.Text("description");
        return new DirectoryRow(new string(' ', indent) + Fence(path) + (description is null ? "" : "  " + Fence(description)) + extraModel,
            extra is null ? [new DirectoryCell(path, Indent: indent), description] : [new DirectoryCell(path, Indent: indent), description, extra], e.Dn, path, Opens: true);
    }

    public static string Ou(IDirectorySession session, DirectorySource source, string? ou, int limit) => DirectoryRender.ForModel(OuAnswer(session, source, ou, limit));

    /// <summary>
    /// <c>ad_ou</c> (2026-10-15, the AD gaps plan, §4, the user's question: "tools that can search for and list OUs specifically?"): without an OU
    /// the tree — every OU and the containers one level under the base, as paths, the advanced-view ones hidden, sorted, up to 500; with one (or a
    /// clicked DN) its card — DN, description, owner, linked GPOs — and its direct objects counted by kind, the child OUs first, the first
    /// <paramref name="limit"/> listed.
    /// </summary>
    public static DirectoryAnswer OuAnswer(IDirectorySession session, DirectorySource source, string? ou, int limit, DirectoryDeadline deadline = default, bool canSave = false,
        string? dn = null, int cap = DirectoryLimits.MaxEntries, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        string baseDn = source.Domain.BaseDn;
        var answer = Start("ou", DateTimeOffset.UtcNow, zone ?? TimeZoneInfo.Utc);
        if (ou is null && dn is null)
        {
            return Tree(session, source, answer, deadline);
        }

        DirectoryEntry? branch;
        if (dn is not null)
        {
            var byDn = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.ByDn(LdapFilter.OuLike, dn), AdAttributes.Ou, 2, paged: false, deadline.Limit);
            branch = byDn.Entries.Count == 1 ? byDn.Entries[0] : null;
            if (branch is null)
            {
                return DirectoryAnswer.Refused(DirectoryText.Gone(ou ?? AdValues.FirstName(dn), source.Where));
            }
        }
        else
        {
            var (found, candidates) = Branch(session, baseDn, ou!, deadline);
            if (found is null)
            {
                return candidates.Count > 0 ? AmbiguousOu(ou!, candidates, baseDn) : DirectoryAnswer.Refused(DirectoryText.NotFound("OU", ou!, source.Where));
            }

            branch = found;
        }

        string path = PathOf(branch, baseDn);
        answer.Self = new DirectoryRowTarget(branch.Dn, path);
        bool timed = !deadline.CanStart;
        var contents = timed ? new DirectoryResults([], false, true)
            : session.Search(branch.Dn, DirectoryScope.OneLevel, "(objectClass=*)", AdAttributes.OuContent, cap, paged: true, deadline.Limit);
        timed |= contents.TimedOut;
        bool cut = contents.Truncated && !contents.TimedOut;
        var entries = contents.Entries.Select(e => (Entry: e, Kind: KindOf(e))).ToList();
        var counts = entries.GroupBy(e => e.Kind).OrderBy(g => g.Key is "ou" or "container" ? 0 : 1).ThenByDescending(g => g.Count()).Select(g => KindCount(g.Key, g.Count())).ToList();
        int total = entries.Count;
        answer.Partial = cut || timed;
        answer.Head = $"OU {Fence(path)} in {source.Where}: {total.ToString("N0", CultureInfo.InvariantCulture)}{(cut ? "+" : "")} object" + (total == 1 ? "" : "s")
            + (counts.Count > 0 ? " (" + string.Join(", ", counts) + ")" : "")
            + (total > limit ? $", the first {limit.ToString(CultureInfo.InvariantCulture)} shown" : "")
            + (cut ? "; " + DirectoryText.CapNote(cap, canSave && cap < DirectoryLimits.MaxCounted) : "")
            + (timed ? "; " + DirectoryText.StoppedAtTimeLimit : "");
        Add(answer, "dn", Fence(branch.Dn));
        if (branch.Text("description") is { } description)
        {
            Add(answer, "description", Fence(description));
        }

        if (branch.Text("managedBy") is { } owner)
        {
            answer.Fields.Add(new DirectoryField("managed by", Fence(AdValues.FirstName(owner)), new DirectoryRowTarget(owner, AdValues.FirstName(owner))));
        }

        var (links, disabled, enforced) = AdValues.GpLinks(branch.Text("gPLink"));
        var linkWords = new List<string>(2);
        if (disabled > 0)
        {
            linkWords.Add(disabled.ToString(CultureInfo.InvariantCulture) + " disabled");
        }

        if (enforced > 0)
        {
            linkWords.Add(enforced.ToString(CultureInfo.InvariantCulture) + " enforced");
        }

        Add(answer, "GPOs linked", links == 0 ? "none" : links.ToString(CultureInfo.InvariantCulture) + (linkWords.Count > 0 ? " (" + string.Join(", ", linkWords) + ")" : ""));
        var table = new DirectoryTable([new(DirectoryText.ColAccount), new(DirectoryText.ColKind), new(DirectoryText.ColName, Flexible: true)]) { Shown = limit, CutNote = _ => "… more; lower limit" };
        foreach (var (e, kind) in entries.OrderBy(p => p.Kind is "ou" or "container" ? 0 : 1).ThenBy(p => p.Entry.Text("sAMAccountName") ?? Display(p.Entry), StringComparer.OrdinalIgnoreCase))
        {
            string? account = e.Text("sAMAccountName");
            string display = kind is "ou" or "container" ? e.Text("ou") ?? e.Text("name") ?? AdValues.FirstName(e.Dn) : Display(e);
            table.Rows.Add(new DirectoryRow((account is null ? DirectoryRender.NoValue : Plain(account)) + "  " + kind + "  " + Fence(display), [account, kind, display], e.Dn, display,
                Opens: kind is "user" or "group" or "computer" or AdValues.KindServiceAccount or "ou" or "container"));
        }

        answer.Tables.Add(table);
        return answer;
    }

    private static DirectoryAnswer Tree(IDirectorySession session, DirectorySource source, DirectoryAnswer answer, DirectoryDeadline deadline)
    {
        string baseDn = source.Domain.BaseDn;
        var ous = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.TreeOus, AdAttributes.Ou, MaxTree, paged: true, deadline.Limit);
        var tops = deadline.CanStart ? session.Search(baseDn, DirectoryScope.OneLevel, LdapFilter.TreeContainers, AdAttributes.Ou, 200, paged: false, deadline.Limit) : new DirectoryResults([], false, true);
        var all = ous.Entries.Concat(tops.Entries).GroupBy(e => e.Dn, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        static bool Advanced(DirectoryEntry e) => string.Equals(e.Text("showInAdvancedViewOnly"), "TRUE", StringComparison.OrdinalIgnoreCase);
        bool marked = all.Any(Advanced);
        var shown = all.Where(e => marked ? !Advanced(e) : !(AdValues.PathParts(PathOf(e, baseDn)) is [var top] && AdvancedContainers.Contains(top)))
            .Select(e => (Entry: e, Parts: AdValues.PathParts(PathOf(e, baseDn))))
            .OrderBy(p => p.Parts, PartsOrder.Instance)
            .ToList();
        bool timed = ous.TimedOut || tops.TimedOut;
        answer.Partial = timed || ous.Truncated;
        answer.Head = $"OU tree of {source.Where}: {shown.Count.ToString(CultureInfo.InvariantCulture)} OUs and containers" + (ous.Truncated && !ous.TimedOut ? $"; the first {MaxTree.ToString(CultureInfo.InvariantCulture)} OUs read" : "")
            + (timed ? "; " + DirectoryText.StoppedAtTimeLimit : "");
        var table = new DirectoryTable([new(DirectoryText.ColPath, Flexible: true), new(DirectoryText.ColDescription, Flexible: true)]) { CutNote = left => "… " + left.ToString(CultureInfo.InvariantCulture) + " more; name an OU" };
        foreach (var (e, parts) in shown)
        {
            table.Rows.Add(OuRow(e, baseDn, indent: 2 * Math.Max(0, parts.Count - 1)));
        }

        answer.Tables.Add(table);
        return answer;
    }

    /// <summary>Paths compared part by part, so a branch's OUs follow it (a plain string order puts "Neon Team" between "Neon" and "Neon/Engineering").</summary>
    private sealed class PartsOrder : IComparer<IReadOnlyList<string>>
    {
        public static readonly PartsOrder Instance = new();

        public int Compare(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (x is null || y is null)
            {
                return (x is null ? 0 : 1) - (y is null ? 0 : 1);
            }

            for (int i = 0; i < Math.Min(x.Count, y.Count); i++)
            {
                int c = StringComparer.OrdinalIgnoreCase.Compare(x[i], y[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return x.Count.CompareTo(y.Count);
        }
    }

    private static string KindCount(string kind, int count)
    {
        string noun = kind switch
        {
            "ou" => "OU",
            "foreign" => "foreign principal",
            _ => kind,
        };
        return count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");
    }

    // ── The domain ──────────────────────────────────────────────────────────

    public static string Domain(IDirectorySession session, DirectorySource source, DirectoryDeadline deadline = default) => DirectoryRender.ForModel(DomainAnswer(session, source, deadline));

    /// <summary>The domain's facts, and its fine-grained password policies (2026-10-15, the AD gaps plan, §5): as many as this account may see.</summary>
    public static DirectoryAnswer DomainAnswer(IDirectorySession session, DirectorySource source, DirectoryDeadline deadline = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        string baseDn = source.Domain.BaseDn;
        var root = session.RootDse();
        var policy = Policy(session, baseDn, null, deadline);
        var answer = Start("domain", DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        answer.Head = $"Domain {source.Where} ({Fence(baseDn)}): functional level {AdValues.FunctionalLevel(AdValues.Integer(root.Text("domainFunctionality")))}, forest {AdValues.FunctionalLevel(AdValues.Integer(root.Text("forestFunctionality")))}";
        var dcs = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.DomainControllers, ["dNSHostName", "cn", "operatingSystem"], 200, paged: true, deadline.Limit);
        var dcTable = new DirectoryTable([new(DirectoryText.ColDnsName), new(DirectoryText.ColOs, Flexible: true)], list: false) { Title = "domain controllers (" + dcs.Entries.Count.ToString(CultureInfo.InvariantCulture) + ")" };
        foreach (var e in dcs.Entries)
        {
            string host = e.Text("dNSHostName") ?? e.Text("cn") ?? "?";
            dcTable.Rows.Add(new DirectoryRow(Fence(host), [host, e.Text("operatingSystem")], e.Dn, e.Text("cn") ?? host, Opens: true));
        }

        answer.Fields.Add(new DirectoryField("domain controllers", string.Join(", ", dcs.Entries.Select(e => Fence(e.Text("dNSHostName") ?? e.Text("cn") ?? "?") + (e.Text("operatingSystem") is { } os ? " (" + Fence(os) + ")" : ""))), Links: dcTable));
        string config = root.Text("configurationNamingContext") ?? "";
        string schema = root.Text("schemaNamingContext") ?? "";
        var roles = new List<string>(5)
        {
            "PDC emulator " + Fence(RoleHolder(session, baseDn, deadline) ?? "?"),
            "RID master " + Fence(RoleHolder(session, "CN=RID Manager$,CN=System," + baseDn, deadline) ?? "?"),
            "infrastructure " + Fence(RoleHolder(session, "CN=Infrastructure," + baseDn, deadline) ?? "?"),
        };
        if (schema.Length > 0)
        {
            roles.Add("schema master " + Fence(RoleHolder(session, schema, deadline) ?? "?"));
        }

        if (config.Length > 0)
        {
            roles.Add("naming master " + Fence(RoleHolder(session, "CN=Partitions," + config, deadline) ?? "?"));
        }

        Add(answer, "roles", string.Join(" · ", roles));
        Add(answer, "password policy", PolicyWords(policy));
        Add(answer, "lockout", LockoutWords(policy));
        var policies = PasswordPolicies(session, baseDn, deadline);
        if (policies.Rows.Count == 0)
        {
            Add(answer, DirectoryText.PoliciesLabel, DirectoryText.PoliciesNone);
        }
        else
        {
            answer.Tables.Add(policies);
        }

        return answer;
    }

    /// <summary>
    /// The fine-grained password policies (one level under the Password Settings Container), by precedence. An account without rights reads a
    /// policy's object but none of its settings (checked on Samba as <c>reader</c>, 2026-10-15): such a one is named and said unreadable.
    /// </summary>
    private static DirectoryTable PasswordPolicies(IDirectorySession session, string baseDn, DirectoryDeadline deadline)
    {
        var table = new DirectoryTable([new(DirectoryText.ColName, Flexible: true), new(DirectoryText.ColPrecedence), new(DirectoryText.ColMaxAge), new(DirectoryText.ColMinLength),
            new(DirectoryText.ColLockout, Flexible: true), new(DirectoryText.ColAppliesTo, Flexible: true)], list: true) { TitleInModel = true, CutReserve = 400 };
        IReadOnlyList<DirectoryEntry> found;
        try
        {
            found = session.Search("CN=Password Settings Container,CN=System," + baseDn, DirectoryScope.OneLevel, LdapFilter.PasswordSettingsObjects, AdAttributes.PasswordSettings, 200, paged: false, deadline.Limit).Entries;
        }
        catch (DirectoryException)
        {
            found = [];
        }

        foreach (var p in found.OrderBy(p => AdValues.Integer(p.Text("msDS-PasswordSettingsPrecedence")) ?? long.MaxValue))
        {
            string name = p.Text("name") ?? AdValues.FirstName(p.Dn);
            var precedence = AdValues.Integer(p.Text("msDS-PasswordSettingsPrecedence"));
            if (SettingsUnreadable(p))
            {
                table.Rows.Add(new DirectoryRow(Fence(name) + "  " + DirectoryText.PolicyUnreadable, [name, null, null, null, DirectoryText.PolicyUnreadable, null], p.Dn, name));
                continue;
            }

            var settings = new AdPolicy(name, AdValues.Interval(p.Text("msDS-MaximumPasswordAge")), AdValues.Interval(p.Text("msDS-LockoutDuration")), AdValues.Integer(p.Text("msDS-LockoutThreshold")),
                AdValues.Interval(p.Text("msDS-LockoutObservationWindow")), AdValues.Integer(p.Text("msDS-MinimumPasswordLength")), null, false);
            var applies = p.Texts("msDS-PSOAppliesTo").Select(AdValues.FirstName).ToList();
            string maxAge = Span(settings.MaxPasswordAge, DirectoryText.NeverWord);
            string lockout = LockoutWords(settings);
            table.Rows.Add(new DirectoryRow(Fence(name) + "  precedence " + (precedence?.ToString(CultureInfo.InvariantCulture) ?? "?") + ", maximum age " + maxAge
                    + ", " + (settings.MinLength is { } min ? "at least " + min.ToString(CultureInfo.InvariantCulture) + " characters" : "no minimum length") + ", lockout " + lockout
                    + (applies.Count > 0 ? "; applies to " + string.Join(", ", applies.Select(Fence)) : ""),
                [name, precedence?.ToString(CultureInfo.InvariantCulture), maxAge, settings.MinLength?.ToString(CultureInfo.InvariantCulture), lockout, applies.Count > 0 ? string.Join(", ", applies) : null], p.Dn, name));
        }

        table.Title = DirectoryText.PoliciesTitle(table.Rows.Count);
        return table;
    }

    public static string PolicyWords(AdPolicy policy) =>
        (policy.MinLength is { } min ? "at least " + min.ToString(CultureInfo.InvariantCulture) + " characters" : "no minimum length")
        + (policy.History is { } history ? ", history " + history.ToString(CultureInfo.InvariantCulture) : "")
        + ", maximum age " + Span(policy.MaxPasswordAge, DirectoryText.NeverWord)
        + (policy.Complex ? ", complexity on" : ", complexity off");

    public static string LockoutWords(AdPolicy policy) =>
        policy.LockoutThreshold is null or 0 ? "never (no threshold)"
        : "after " + policy.LockoutThreshold.Value.ToString(CultureInfo.InvariantCulture) + " bad passwords within " + Span(policy.ObservationWindow, "any time")
          + ", for " + Span(policy.LockoutDuration, DirectoryText.ForeverWord);

    private static string Span(AdInterval? interval, string endless) => interval switch
    {
        null => "unknown",
        { Endless: true } => endless,
        { Span: { } span } => span.TotalDays >= 1 ? Math.Round(span.TotalDays).ToString(CultureInfo.InvariantCulture) + " days"
            : span.TotalHours >= 1 ? Math.Round(span.TotalHours).ToString(CultureInfo.InvariantCulture) + " hours"
            : Math.Round(span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes",
        _ => "unknown",
    };

    // ── Open by DN (a clicked row) ──────────────────────────────────────────

    /// <summary>
    /// The card a clicked row opens (2026-10-15, the AD gaps plan, §6b): the object by its DN (one result, never an ambiguous name), its card picked
    /// by its own <c>objectClass</c> — a row's kind is never trusted over the object; a service account opens the computer card. Gone →
    /// <see cref="DirectoryText.Gone"/> by the row's name.
    /// </summary>
    public static DirectoryAnswer Open(IDirectorySession session, DirectorySource source, DirectoryRowTarget target, DateTimeOffset now, TimeZoneInfo zone, DirectoryDeadline deadline = default,
        bool nested = false, string members = "all", int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var found = session.Search(source.Domain.BaseDn, DirectoryScope.Subtree, "(distinguishedName=" + LdapFilter.Escape(target.Dn) + ")", AdAttributes.Open, 2, paged: false, deadline.Limit);
        if (found.Entries.Count != 1)
        {
            return DirectoryAnswer.Refused(DirectoryText.Gone(target.Name, source.Where));
        }

        string dn = found.Entries[0].Dn;
        return AdValues.KindOf(found.Entries[0].Texts("objectClass")) switch
        {
            "user" => UserAnswer(session, source, target.Name, now, zone, dn, deadline),
            "group" => GroupAnswer(session, source, target.Name, nested, members, limit, zone, deadline, dn: dn),
            "computer" or AdValues.KindServiceAccount => ComputerAnswer(session, source, target.Name, zone, dn, deadline),
            "ou" or "container" => OuAnswer(session, source, target.Name, limit, deadline, dn: dn, zone: zone),
            _ => DirectoryAnswer.Refused(DirectoryText.Gone(target.Name, source.Where)),
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DirectoryAnswer Start(string kind, DateTimeOffset now, TimeZoneInfo zone) => new() { Kind = kind, Now = now, Zone = zone };

    private static DirectoryRow Row(string model, DirectoryEntry e, IReadOnlyList<DirectoryCell?> cells) =>
        new(model, cells, e.Dn, Display(e), Opens: KindOf(e) is "user" or "group" or "computer" or AdValues.KindServiceAccount or "ou" or "container");

    private static string KindOf(DirectoryEntry entry) => AdValues.KindOf(entry.Texts("objectClass"));

    private static string Display(DirectoryEntry e) => e.Text("displayName") ?? e.Text("cn") ?? AdValues.FirstName(e.Dn);

    private static string Acct(DirectoryEntry e) => Plain(e.Text("sAMAccountName") ?? "?");

    private static string GroupType(DirectoryEntry e) => AdValues.Integer(e.Text("groupType")) is { } type ? AdValues.GroupKind(type) : "group";

    private static string Described(DirectoryEntry e) => e.Text("description") is { } description ? "  " + Fence(description) : "";

    /// <summary>A find type's plural ("OUs", "service accounts", "users"), for the finds' heads and <c>/ad</c>'s titles.</summary>
    internal static string Plural(string type) => type switch
    {
        "ou" => "OUs",
        "service" => "service accounts",
        _ => type + "s",
    };

    private static string Fence(string text) => UntrustedText.Fenced(UntrustedText.Cut(text));

    /// <summary>An account name: scrubbed, not fenced (its characters are limited by the directory).</summary>
    private static string Plain(string text) => UntrustedText.Scrub(UntrustedText.Cut(text, 120));

    private static string Time(DateTimeOffset moment, TimeZoneInfo zone) => EventLogText.Time(moment, zone);

    private static void Add(DirectoryAnswer answer, string label, string value) => answer.Fields.Add(new DirectoryField(label, value));
}
