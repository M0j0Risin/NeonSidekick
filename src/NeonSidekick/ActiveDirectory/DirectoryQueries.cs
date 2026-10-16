using System.Globalization;
using System.Text;
using NeonSidekick.EventLog;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.ActiveDirectory;

/// <summary>The domain's password and lockout policy: the domain head's, or a fine-grained one's.</summary>
public sealed record AdPolicy(string? Name, AdInterval? MaxPasswordAge, AdInterval? LockoutDuration, long? LockoutThreshold, AdInterval? ObservationWindow, long? MinLength, long? History, bool Complex);

/// <summary>A lockout and a password expiry worked out, and how.</summary>
public sealed record AdAccountState(bool Locked, DateTimeOffset? LockedAt, DateTimeOffset? Unlocks, bool LockedForever, string LockedHow, DateTimeOffset? PasswordSet, DateTimeOffset? Expires, bool NeverExpires, bool MustChange, string ExpiresHow);

/// <summary>
/// The five questions the directory tools answer (2026-10-15, the admin-tools plan), as searches over a bound session and their answers' text:
/// a user's card, a group's members, a computer's card, a structured find, the domain's facts. Constructed attributes are read when the domain
/// computes them (<c>msDS-User-Account-Control-Computed</c>, <c>msDS-UserPasswordExpiryTimeComputed</c>, <c>tokenGroups</c>,
/// <c>msDS-ResultantPSO</c>) and worked out from the policy when it does not (Samba sets no <c>ADS_UF_PASSWORD_EXPIRED</c>): locked when
/// <c>lockoutTime</c> &gt; 0 and the duration is forever or not yet over; the expiry never with <c>DONT_EXPIRE_PASSWORD</c> or a never-aging
/// policy, "must change" with <c>pwdLastSet</c> 0, else <c>pwdLastSet</c> + the maximum age. Every card says which way each was found. A
/// stranger's text — names, titles, descriptions, DNs — is fenced. Pure over <see cref="IDirectorySession"/>.
/// </summary>
public static class DirectoryQueries
{
    public const int MaxChars = 16_000;

    // ── Lookups ─────────────────────────────────────────────────────────────

    /// <summary>The one object a name means; the candidates when several match; null for none.</summary>
    public static (DirectoryEntry? Found, IReadOnlyList<string> Candidates) One(IDirectorySession session, string baseDn, string filter, IReadOnlyList<string> attributes)
    {
        var results = session.Search(baseDn, DirectoryScope.Subtree, filter, attributes, 20, paged: false);
        return results.Entries.Count switch
        {
            0 => (null, []),
            1 => (results.Entries[0], []),
            _ => (null, results.Entries.Select(e => (e.Text("sAMAccountName") ?? AdValues.FirstName(e.Dn)) + "  " + UntrustedText.Fenced(UntrustedText.Cut(e.Dn, 300))).ToList()),
        };
    }

    /// <summary>The domain head's policy, and a fine-grained one's over it when <paramref name="psoDn"/> names one that can be read.</summary>
    public static AdPolicy Policy(IDirectorySession session, string baseDn, string? psoDn)
    {
        if (psoDn is { Length: > 0 })
        {
            try
            {
                var pso = session.Search(psoDn, DirectoryScope.Base, "(objectClass=*)", AdAttributes.PasswordSettings, 1, paged: false);
                if (pso.Entries.Count > 0)
                {
                    var p = pso.Entries[0];
                    return new AdPolicy(p.Text("name") ?? AdValues.FirstName(psoDn), AdValues.Interval(p.Text("msDS-MaximumPasswordAge")), AdValues.Interval(p.Text("msDS-LockoutDuration")),
                        AdValues.Integer(p.Text("msDS-LockoutThreshold")), AdValues.Interval(p.Text("msDS-LockoutObservationWindow")), AdValues.Integer(p.Text("msDS-MinimumPasswordLength")), null, false);
                }
            }
            catch (DirectoryException)
            {
                // A PSO the account may not read: the domain's policy below.
            }
        }

        var head = session.Search(baseDn, DirectoryScope.Base, "(objectClass=*)", AdAttributes.DomainHead, 1, paged: false);
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
    public static string? RoleHolder(IDirectorySession session, string objectDn)
    {
        try
        {
            var holder = session.Search(objectDn, DirectoryScope.Base, "(objectClass=*)", ["fSMORoleOwner"], 1, paged: false);
            if (holder.Entries.Count == 0 || holder.Entries[0].Text("fSMORoleOwner") is not { } ntds)
            {
                return null;
            }

            string server = AdValues.Parent(ntds);
            var host = session.Search(server, DirectoryScope.Base, "(objectClass=*)", ["dNSHostName"], 1, paged: false);
            return host.Entries.Count > 0 ? host.Entries[0].Text("dNSHostName") ?? AdValues.FirstName(server) : AdValues.FirstName(server);
        }
        catch (DirectoryException)
        {
            return null;
        }
    }

    /// <summary>The groups a user is in, nested: <c>tokenGroups</c> (a base read of the user) resolved 50 SIDs at a time; the direct <c>memberOf</c> when the domain gives none.</summary>
    public static (IReadOnlyList<string> Names, bool Nested) Groups(IDirectorySession session, string baseDn, DirectoryEntry user)
    {
        var token = session.Search(user.Dn, DirectoryScope.Base, "(objectClass=*)", ["tokenGroups"], 1, paged: false);
        var sids = token.Entries.Count > 0 ? token.Entries[0].Values("tokenGroups") : [];
        if (sids.Count == 0)
        {
            return (user.Texts("memberOf").Select(AdValues.FirstName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), false);
        }

        var names = new List<string>(sids.Count);
        foreach (var batch in sids.Chunk(DirectoryLimits.SidBatch))
        {
            var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.BySids(batch), ["sAMAccountName", "cn"], DirectoryLimits.SidBatch, paged: false);
            names.AddRange(found.Entries.Select(e => e.Text("sAMAccountName") ?? e.Text("cn") ?? AdValues.FirstName(e.Dn)));
            if (names.Count >= DirectoryLimits.MaxEntries)
            {
                break;
            }
        }

        return (names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), true);
    }

    // ── The answers ─────────────────────────────────────────────────────────

    public static string User(IDirectorySession session, DirectorySource source, string name, DateTimeOffset now, TimeZoneInfo zone)
    {
        string baseDn = source.Domain.BaseDn;
        var (user, candidates) = One(session, baseDn, LdapFilter.UserByName(name), AdAttributes.User);
        if (user is null)
        {
            return candidates.Count > 0 ? DirectoryText.Ambiguous(name, candidates) : DirectoryText.NotFound("user", name, source.Where);
        }

        var policy = Policy(session, baseDn, user.Text("msDS-ResultantPSO"));
        var state = State(user, policy, now);
        int uac = (int)(AdValues.Integer(user.Text("userAccountControl")) ?? 0);
        var text = new StringBuilder($"User {Plain(user.Text("sAMAccountName") ?? name)} in {source.Where}: " + ((uac & AdValues.AccountDisable) != 0 ? "disabled" : "enabled") + (state.Locked ? ", locked" : ""));
        Add(text, "name", Fence(user.Text("displayName") ?? user.Text("cn") ?? "") + (user.Text("userPrincipalName") is { } upn ? " · upn " + Fence(upn) : "") + (user.Text("mail") is { } mail ? " · mail " + Fence(mail) : ""));
        Add(text, "dn", Fence(user.Dn));
        if (state.Locked)
        {
            string pdc = RoleHolder(session, baseDn) ?? "the PDC emulator";
            Add(text, "locked", (state.LockedAt is { } at ? "since " + Time(at, zone) : "yes") + (state.LockedForever ? ", " + DirectoryText.ForeverWord : state.Unlocks is { } unlocks ? ", unlocks " + Time(unlocks, zone) : "")
                + " (" + state.LockedHow + "); " + DirectoryText.LockoutSource(pdc));
        }

        if (AdValues.Integer(user.Text("badPwdCount")) is { } bad && bad > 0)
        {
            Add(text, "bad passwords", bad.ToString(CultureInfo.InvariantCulture) + (AdValues.FileTime(user.Text("badPasswordTime")) is { } last ? ", last " + Time(last, zone) : "") + " (this DC's count)");
        }

        Add(text, "password", (state.PasswordSet is { } set ? "set " + Time(set, zone) + ", " : "")
            + (state.MustChange ? DirectoryText.MustChange : state.NeverExpires ? "never expires" : state.Expires is { } expires ? (expires < now ? "expired " : "expires ") + Time(expires, zone) : "expiry unknown")
            + " (" + state.ExpiresHow + ")");
        Add(text, "policy", policy.Name is { } pso ? "fine-grained: " + Fence(pso) : user.Text("msDS-ResultantPSO") is null ? DirectoryText.DomainPolicyMaybe : "the domain policy");
        Add(text, "last sign-in", (AdValues.FileTime(user.Text("lastLogonTimestamp")) is { } logon ? Time(logon, zone) : DirectoryText.NeverWord) + " (" + DirectoryText.StaleNote + ")");
        Add(text, "account expires", AdValues.FileTime(user.Text("accountExpires")) is { } ends ? Time(ends, zone) : DirectoryText.NeverWord);
        foreach (string field in (string[])["title", "department", "company", "description"])
        {
            if (user.Text(field) is { } value)
            {
                Add(text, field, Fence(value));
            }
        }

        if (user.Text("manager") is { } manager)
        {
            Add(text, "manager", Fence(AdValues.FirstName(manager)));
        }

        Add(text, "ou", Fence(AdValues.Parent(user.Dn)));
        var flags = AdValues.Flags(uac).Where(f => f != "disabled").ToList();
        if (flags.Count > 0)
        {
            Add(text, "flags", string.Join(", ", flags));
        }

        if (!source.Redacted)
        {
            foreach (string field in AdAttributes.Personal)
            {
                if (user.Text(field) is { } value)
                {
                    Add(text, field, Fence(value));
                }
            }
        }

        var (groups, nested) = Groups(session, baseDn, user);
        Add(text, "groups (" + groups.Count.ToString(CultureInfo.InvariantCulture) + (nested ? ", nested" : ", direct") + ")", string.Join(", ", groups.Take(200).Select(Fence)) + (groups.Count > 200 ? " and more" : ""));
        if (source.Redacted)
        {
            text.Append("\nphones, addresses and employee numbers left out (redacted)");
        }

        return Finish(text);
    }

    public static string Group(IDirectorySession session, DirectorySource source, string name, bool nested, int limit)
    {
        string baseDn = source.Domain.BaseDn;
        var (group, candidates) = One(session, baseDn, LdapFilter.GroupByName(name), AdAttributes.Group);
        if (group is null)
        {
            return candidates.Count > 0 ? DirectoryText.Ambiguous(name, candidates) : DirectoryText.NotFound("group", name, source.Where);
        }

        var text = new StringBuilder($"Group {Fence(group.Text("sAMAccountName") ?? name)} in {source.Where}");
        IReadOnlyList<string> members;
        bool cut;
        if (nested)
        {
            var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.NestedMembersOf(group.Dn), ["sAMAccountName", "displayName", "objectClass"], Math.Min(limit, DirectoryLimits.MaxEntries), paged: true);
            members = found.Entries.Select(e => Fence(e.Text("displayName") ?? AdValues.FirstName(e.Dn)) + " (" + Kind(e) + " " + Plain(e.Text("sAMAccountName") ?? "?") + ")").ToList();
            cut = found.Truncated;
        }
        else
        {
            var (dns, truncated) = Members(session, group.Dn, limit);
            members = dns.Select(dn => Fence(AdValues.FirstName(dn))).ToList();
            cut = truncated;
        }

        text.Append(": ").Append(members.Count.ToString(CultureInfo.InvariantCulture)).Append(nested ? " members, nested" : " members, direct").Append(cut ? " (more not shown; " + DirectoryText.CapNote + ")" : "");
        if (group.Text("description") is { } description)
        {
            Add(text, "description", Fence(description));
        }

        for (int i = 0; i < members.Count; i++)
        {
            if (text.Length > MaxChars - 600)
            {
                text.Append("\n  … ").Append((members.Count - i).ToString(CultureInfo.InvariantCulture)).Append(" more; lower limit or ask for one member");
                break;
            }

            text.Append("\n  ").Append(members[i]);
        }

        return Finish(text);
    }

    /// <summary>A group's direct members by range retrieval (<c>member;range=0-*</c>, then on from where each answer ended), up to <paramref name="limit"/>.</summary>
    public static (IReadOnlyList<string> Dns, bool Truncated) Members(IDirectorySession session, string groupDn, int limit)
    {
        var dns = new List<string>();
        int from = 0;
        for (int round = 0; round < 64; round++)
        {
            var read = session.Search(groupDn, DirectoryScope.Base, "(objectClass=*)", ["member;range=" + from.ToString(CultureInfo.InvariantCulture) + "-*"], 1, paged: false);
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
                return (dns.Take(limit).ToList(), true);
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

        return (dns, false);
    }

    public static string Computer(IDirectorySession session, DirectorySource source, string name, TimeZoneInfo zone)
    {
        var (computer, candidates) = One(session, source.Domain.BaseDn, LdapFilter.ComputerByName(name), AdAttributes.Computer);
        if (computer is null)
        {
            return candidates.Count > 0 ? DirectoryText.Ambiguous(name, candidates) : DirectoryText.NotFound("computer", name, source.Where);
        }

        int uac = (int)(AdValues.Integer(computer.Text("userAccountControl")) ?? 0);
        var text = new StringBuilder($"Computer {Fence(computer.Text("cn") ?? name)} in {source.Where}: " + ((uac & AdValues.AccountDisable) != 0 ? "disabled" : "enabled") + ((uac & AdValues.ServerTrustAccount) != 0 ? ", a domain controller" : ""));
        if (computer.Text("dNSHostName") is { } host)
        {
            Add(text, "dns name", Fence(host));
        }

        if (computer.Text("operatingSystem") is { } os)
        {
            Add(text, "os", Fence(os + (computer.Text("operatingSystemVersion") is { } version ? " " + version : "")));
        }

        Add(text, "last sign-in", (AdValues.FileTime(computer.Text("lastLogonTimestamp")) is { } logon ? Time(logon, zone) : DirectoryText.NeverWord) + " (" + DirectoryText.StaleNote + ")");
        if (AdValues.FileTime(computer.Text("pwdLastSet")) is { } set)
        {
            Add(text, "machine password set", Time(set, zone));
        }

        Add(text, "ou", Fence(AdValues.Parent(computer.Dn)));
        foreach (string field in (string[])["description", "location"])
        {
            if (computer.Text(field) is { } value)
            {
                Add(text, field, Fence(value));
            }
        }

        return Finish(text);
    }

    public static readonly IReadOnlyList<string> FindKinds = ["locked", "password_expiring", "disabled", "stale_users", "stale_computers", "name"];

    /// <summary>
    /// <c>ad_find</c>: the stored attributes filtered on the server, the constructed ones refined here, all inside the 1,000-entry cap (the client
    /// step counted too); never-signed-in accounts listed apart for the stale kinds.
    /// </summary>
    public static string Find(IDirectorySession session, DirectorySource source, string kind, int days, string? words, int limit, DateTimeOffset now, TimeZoneInfo zone)
    {
        string baseDn = source.Domain.BaseDn;
        var lines = new List<string>();
        var apart = new List<string>();
        bool cut = false;
        string what;
        switch (kind)
        {
            case "locked":
            {
                what = "locked users";
                var policy = Policy(session, baseDn, null);
                var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.LockedCandidates, AdAttributes.Find, DirectoryLimits.MaxEntries, paged: true);
                cut = found.Truncated;
                foreach (var e in found.Entries)
                {
                    var own = e.Text("msDS-ResultantPSO") is { } pso ? Policy(session, baseDn, pso) : policy;
                    var state = State(e, own, now);
                    if (state.Locked)
                    {
                        lines.Add(Plain(e.Text("sAMAccountName") ?? "?") + "  " + Fence(e.Text("displayName") ?? AdValues.FirstName(e.Dn)) + "  locked " + (state.LockedAt is { } at ? Time(at, zone) : "") + (state.Unlocks is { } u ? ", unlocks " + Time(u, zone) : ""));
                    }
                }

                break;
            }

            case "password_expiring":
            {
                what = "passwords ending within " + days.ToString(CultureInfo.InvariantCulture) + " days";
                var policy = Policy(session, baseDn, null);
                var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.ExpiringCandidates, AdAttributes.Find, DirectoryLimits.MaxEntries, paged: true);
                cut = found.Truncated;
                var limitDate = now.AddDays(days);
                foreach (var (e, state) in found.Entries.Select(e => (e, State(e, policy, now))).Where(p => p.Item2.Expires is { } x && x <= limitDate).OrderBy(p => p.Item2.Expires))
                {
                    lines.Add(Plain(e.Text("sAMAccountName") ?? "?") + "  " + Fence(e.Text("displayName") ?? AdValues.FirstName(e.Dn)) + "  " + (state.Expires < now ? "expired " : "expires ") + Time(state.Expires!.Value, zone) + " (" + state.ExpiresHow + ")");
                }

                break;
            }

            case "disabled":
            {
                what = "disabled users";
                var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.DisabledUsers, AdAttributes.Find, DirectoryLimits.MaxEntries, paged: true);
                cut = found.Truncated;
                lines.AddRange(found.Entries.Select(e => Plain(e.Text("sAMAccountName") ?? "?") + "  " + Fence(e.Text("displayName") ?? AdValues.FirstName(e.Dn))));
                break;
            }

            case "stale_users":
            case "stale_computers":
            {
                bool computers = kind == "stale_computers";
                what = (computers ? "computers" : "users") + " not signed in for " + days.ToString(CultureInfo.InvariantCulture) + " days";
                long cutoff = AdValues.ToFileTime(now.AddDays(-days));
                var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.StaleSince(computers, cutoff), AdAttributes.Find, DirectoryLimits.MaxEntries, paged: true);
                var never = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.NeverSignedIn(computers), AdAttributes.Find, Math.Max(1, DirectoryLimits.MaxEntries - found.Entries.Count), paged: true);
                cut = found.Truncated || never.Truncated;
                lines.AddRange(found.Entries.OrderBy(e => AdValues.Integer(e.Text("lastLogonTimestamp")) ?? 0).Select(e =>
                    Plain(e.Text("sAMAccountName") ?? "?") + "  " + Fence(e.Text(computers ? "dNSHostName" : "displayName") ?? AdValues.FirstName(e.Dn)) + "  last " + (AdValues.FileTime(e.Text("lastLogonTimestamp")) is { } last ? Time(last, zone) : "?")));
                apart.AddRange(never.Entries.Select(e => Plain(e.Text("sAMAccountName") ?? "?") + "  " + Fence(e.Text(computers ? "dNSHostName" : "displayName") ?? AdValues.FirstName(e.Dn))));
                break;
            }

            default:
            {
                what = "names holding \"" + (words ?? "") + "\"";
                var found = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.NameContains(words ?? ""), AdAttributes.Find, DirectoryLimits.MaxEntries, paged: true);
                cut = found.Truncated;
                lines.AddRange(found.Entries.Select(e => Plain(e.Text("sAMAccountName") ?? "?") + "  " + Kind(e) + "  " + Fence(e.Text("displayName") ?? AdValues.FirstName(e.Dn))));
                break;
            }
        }

        var text = new StringBuilder($"{what} in {source.Where}: {lines.Count.ToString(CultureInfo.InvariantCulture)}" + (lines.Count > limit ? $", the first {limit.ToString(CultureInfo.InvariantCulture)} shown" : "") + (cut ? "; " + DirectoryText.CapNote : ""));
        foreach (string line in lines.Take(limit))
        {
            if (text.Length > MaxChars - 800)
            {
                text.Append("\n  … more; lower limit or narrow the search");
                break;
            }

            text.Append("\n  ").Append(line);
        }

        if (apart.Count > 0)
        {
            text.Append("\nnever signed in (").Append(apart.Count.ToString(CultureInfo.InvariantCulture)).Append("):");
            foreach (string line in apart.Take(Math.Max(10, limit / 2)))
            {
                if (text.Length > MaxChars - 400)
                {
                    break;
                }

                text.Append("\n  ").Append(line);
            }
        }

        return Finish(text);
    }

    public static string Domain(IDirectorySession session, DirectorySource source)
    {
        string baseDn = source.Domain.BaseDn;
        var root = session.RootDse();
        var policy = Policy(session, baseDn, null);
        var text = new StringBuilder($"Domain {source.Where} ({Fence(baseDn)}): functional level {AdValues.FunctionalLevel(AdValues.Integer(root.Text("domainFunctionality")))}, forest {AdValues.FunctionalLevel(AdValues.Integer(root.Text("forestFunctionality")))}");
        var dcs = session.Search(baseDn, DirectoryScope.Subtree, LdapFilter.DomainControllers, ["dNSHostName", "cn", "operatingSystem"], 200, paged: true);
        Add(text, "domain controllers", string.Join(", ", dcs.Entries.Select(e => Fence(e.Text("dNSHostName") ?? e.Text("cn") ?? "?") + (e.Text("operatingSystem") is { } os ? " (" + Fence(os) + ")" : ""))));
        string config = root.Text("configurationNamingContext") ?? "";
        string schema = root.Text("schemaNamingContext") ?? "";
        var roles = new List<string>(5)
        {
            "PDC emulator " + Fence(RoleHolder(session, baseDn) ?? "?"),
            "RID master " + Fence(RoleHolder(session, "CN=RID Manager$,CN=System," + baseDn) ?? "?"),
            "infrastructure " + Fence(RoleHolder(session, "CN=Infrastructure," + baseDn) ?? "?"),
        };
        if (schema.Length > 0)
        {
            roles.Add("schema master " + Fence(RoleHolder(session, schema) ?? "?"));
        }

        if (config.Length > 0)
        {
            roles.Add("naming master " + Fence(RoleHolder(session, "CN=Partitions," + config) ?? "?"));
        }

        Add(text, "roles", string.Join(" · ", roles));
        Add(text, "password policy", PolicyWords(policy));
        Add(text, "lockout", LockoutWords(policy));
        return Finish(text);
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

    private static string Kind(DirectoryEntry entry)
    {
        var classes = entry.Texts("objectClass");
        return classes.Contains("computer", StringComparer.OrdinalIgnoreCase) ? "computer" : classes.Contains("group", StringComparer.OrdinalIgnoreCase) ? "group" : "user";
    }

    private static string Fence(string text) => UntrustedText.Fenced(UntrustedText.Cut(text));

    /// <summary>An account name: scrubbed, not fenced (its characters are limited by the directory).</summary>
    private static string Plain(string text) => UntrustedText.Scrub(UntrustedText.Cut(text, 120));

    private static string Time(DateTimeOffset moment, TimeZoneInfo zone) => EventLogText.Time(moment, zone);

    private static void Add(StringBuilder text, string label, string value) => text.Append("\n  ").Append(label).Append(": ").Append(value);

    private static string Finish(StringBuilder text)
    {
        text.Append('\n').Append(UntrustedText.QuoteNote);
        return text.Length > MaxChars ? text.ToString(0, MaxChars - 40) + "\n(cut at " + MaxChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)" : text.ToString();
    }
}
