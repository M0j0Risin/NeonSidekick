using System.Globalization;
using NeonSidekick.EventLog;
using NeonSidekick.Files;
using NeonSidekick.UI;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// What a <c>/ad</c> line asks for: the verb (user, group, computer, find, ou, domain; bare words a name search), its argument, the domain, and
/// (2026-10-15, the AD gaps plan) a group's <c>members:</c>, a find's <c>type:</c>, <c>ou:</c> and <c>field:</c>, and <c>to:</c>.
/// </summary>
public sealed record DirectoryCommandArgs(string Verb, string Argument, string? Domain, bool Nested, int Days, string? Members = null, string? Type = null, string? Ou = null,
    string? Field = null, string? To = null);

/// <summary>
/// What a <c>/ad</c> line came to: the answer (the card's; null on a refusal before any read), its lines without the pane (the card unbounded, or
/// the refusal), whether it failed, the PDC emulator a locked user's "Search 4740" searches, the card's title, the line read and the domain.
/// </summary>
public sealed record DirectoryCommandResult(DirectoryAnswer? Answer, IReadOnlyList<string> Lines, bool Failed, string? LockedPdc = null, string? Title = null, DirectoryCommandArgs? Args = null, string? Domain = null);

/// <summary>
/// <c>/ad &lt;words&gt;</c> or <c>/ad user|group|computer|find|ou|domain …</c> [<c>domain:x</c>] (2026-10-15, the admin-tools plan; the AD gaps plan
/// the same day): the user's own hand over the directory, read-only, the same answers the tools give. On the pane the answer is a card in columns
/// with copy and ask (and on a locked user the "Search 4740" button, on a group the nested and users-only switches; a double-click on a row opens
/// its card); elsewhere its lines. <c>to:</c> saves a list as a CSV in the working directory: the user's own hand, so neither plan mode nor
/// *File tools* applies, but the write goes through <see cref="WorkingDirectory"/> (the sandbox and the database guard hold).
/// </summary>
public static class DirectoryCommand
{
    public const string Word = "/ad";

    public static readonly IReadOnlyList<string> Verbs = ["user", "group", "computer", "find", "ou", "domain"];

    /// <summary>The key words lifted from anywhere on the line (only these: a name with another colon stays a name). Pinned.</summary>
    public static readonly IReadOnlyList<string> Keys = ["domain", "type", "ou", "field", "members", "to"];

    /// <summary>Which verbs take which key (domain: every one).</summary>
    private static bool Takes(string verb, string key) => key switch
    {
        "domain" => true,
        "members" => verb == "group",
        "type" or "field" or "ou" => verb == "find",
        "to" => verb is "group" or "find" or "ou",
        _ => false,
    };

    /// <summary>A <c>key:value</c> word of <see cref="Keys"/> (the first colon splits, so <c>to:D:\x.csv</c> keeps its drive).</summary>
    public static (string Key, string Value)? KeyWord(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        int colon = word.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        string key = word[..colon].ToLowerInvariant();
        return Keys.Contains(key) ? (key, word[(colon + 1)..]) : null;
    }

    public static (DirectoryCommandArgs? Args, string? Error) Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        bool nested = false;
        var words = new List<string>();
        foreach (string word in EventLogCommand.Words(args))
        {
            if (KeyWord(word) is { } key)
            {
                keys[key.Key] = key.Value;
            }
            else if (string.Equals(word, "nested", StringComparison.OrdinalIgnoreCase))
            {
                nested = true;
            }
            else
            {
                words.Add(word);
            }
        }

        string? Key(string name) => keys.TryGetValue(name, out string? value) && value.Trim().Length > 0 ? value.Trim() : null;
        string? domain = Key("domain");
        if (words.Count == 0)
        {
            return (null, Usage);
        }

        string verb = words[0].ToLowerInvariant();
        if (!Verbs.Contains(verb))
        {
            return keys.Keys.FirstOrDefault(k => k != "domain") is { } stray
                ? (null, KeyNotTaken(stray, "<name>"))
                : (new DirectoryCommandArgs("name", string.Join(' ', words), domain, false, 0), null);
        }

        if (keys.Keys.FirstOrDefault(k => !Takes(verb, k)) is { } refused)
        {
            return (null, KeyNotTaken(refused, verb));
        }

        var rest = words.Skip(1).ToList();
        string? to = Key("to");
        switch (verb)
        {
            case "domain":
                return (new DirectoryCommandArgs(verb, "", domain, false, 0), null);
            case "ou":
                return (new DirectoryCommandArgs(verb, string.Join(' ', rest), domain, false, 0, To: to), null);
        }

        if (rest.Count == 0)
        {
            return (null, Usage);
        }

        if (verb == "find")
        {
            string kind = rest[0].ToLowerInvariant();
            int days = DirectoryQueries.DefaultDays(kind);
            bool daysGiven = false;
            var others = rest.Skip(1).ToList();
            if (DirectoryQueries.DaysKinds.Contains(kind) && others.Count > 0 && int.TryParse(others[0], NumberStyles.None, CultureInfo.InvariantCulture, out int given) && given is > 0 and <= 3650)
            {
                days = given;
                daysGiven = true;
                others.RemoveAt(0);
            }

            string? findWords = others.Count > 0 ? string.Join(' ', others) : null;
            string? type = Key("type")?.ToLowerInvariant();
            string? field = Key("field")?.ToLowerInvariant();
            string? ou = Key("ou");
            if (DirectoryQueries.FindProblem(kind, daysGiven, findWords, type, field, ou, redacted: false) is { } problem)
            {
                return (null, Bare(problem));
            }

            return (new DirectoryCommandArgs("find:" + kind, findWords ?? "", domain, false, days, Type: type, Ou: ou, Field: field, To: to), null);
        }

        string? members = Key("members")?.ToLowerInvariant();
        if (members is not null && !DirectoryQueries.MemberChoices.Contains(members))
        {
            return (null, Bare(DirectoryText.BadChoice("members", members, DirectoryQueries.MemberChoices)));
        }

        return (new DirectoryCommandArgs(verb, string.Join(' ', rest), domain, nested, 0, Members: members is "all" ? null : members, To: to), null);
    }

    /// <summary>A key word on a verb that takes none of it. Pinned.</summary>
    public static string KeyNotTaken(string key, string verb) => $"{Word} {verb} takes no {key}: word";

    /// <summary>The usage line: the kinds from <see cref="DirectoryQueries.FindKinds"/>, so it can never name one the tool lacks. Pinned.</summary>
    public static string Usage =>
        "Usage: /ad <name> · user <name> · group <name> [nested] [members:users|computers|groups] · computer <name> · ou [<path>] · find "
        + string.Join('|', DirectoryQueries.FindKinds) + " [days] [words] [type:<type>] [field:<field>] [ou:<path>] · domain · domain:<name> and to:<file-or-folder> (a .csv file or a folder) may stand anywhere";

    private static string Bare(string error) => error.Replace("Error: ", "", StringComparison.Ordinal);

    /// <summary>The question a made-up <c>to</c> file is named from (<c>ad-group-staff.csv</c>).</summary>
    public static string Question(DirectoryCommandArgs line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Verb switch
        {
            "group" => "group " + line.Argument + (line.Nested ? " nested" : "") + (line.Members is { } m ? " " + m : ""),
            "ou" => "ou " + (line.Argument.Length > 0 ? line.Argument : "tree"),
            _ when line.Verb.StartsWith("find:", StringComparison.Ordinal) => "find " + line.Verb[5..] + (line.Type is { } t ? " " + t : "") + (line.Argument.Length > 0 ? " " + line.Argument : ""),
            _ => line.Verb + " " + line.Argument,
        };
    }

    /// <summary>
    /// A line run: parsed, its domain, its <c>to</c> checked before any read (in <paramref name="files"/>, the working directory), the answer read
    /// under the call's deadline, the save written. The lines are the card unbounded (the Web UI's card, headless).
    /// </summary>
    public static async Task<DirectoryCommandResult> RunAsync(DirectoryAccess access, string args, CancellationToken cancellationToken, Func<WorkingDirectory?>? files = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.System is null)
        {
            return Failed(DirectoryText.NoneHere);   // no directory here (since 2026-10-18 a Mac has one: the managed client)
        }

        var (line, error) = Parse(args);
        if (line is null)
        {
            return Failed(error!);
        }

        DirectorySaveTarget? target = null;
        if (line.To is { } to)
        {
            if (files?.Invoke() is not { } root)
            {
                return Failed(DirectoryText.SaveNotHere);
            }

            var (checkedTarget, refused) = DirectoryCsv.Check(root, to, Question(line));
            if (checkedTarget is null)
            {
                return Failed(refused!);
            }

            target = checkedTarget;
        }

        var now = access.Time.GetUtcNow();
        var zone = access.Time.LocalTimeZone;
        int cap = target is null ? DirectoryLimits.MaxEntries : DirectoryLimits.MaxCounted;
        var result = await RunCardAsync(access, line.Domain, (session, source, deadline) => line.Verb switch
        {
            "user" => DirectoryQueries.UserAnswer(session, source, line.Argument, now, zone, deadline: deadline),
            "group" => DirectoryQueries.GroupAnswer(session, source, line.Argument, line.Nested, line.Members ?? "all", DirectoryLimits.MaxEntries, zone, deadline),
            "computer" => DirectoryQueries.ComputerAnswer(session, source, line.Argument, zone, deadline: deadline),
            "domain" => DirectoryQueries.DomainAnswer(session, source, deadline),
            "ou" => DirectoryQueries.OuAnswer(session, source, line.Argument.Length > 0 ? line.Argument : null, DirectoryLimits.MaxEntries, deadline, cap: cap, zone: zone),
            "name" => DirectoryQueries.FindAnswer(session, source, new FindOptions("name", 0, line.Argument, DirectoryLimits.MaxEntries, Cap: cap), now, zone, deadline),
            _ => DirectoryQueries.FindAnswer(session, source, new FindOptions(line.Verb[5..], line.Days, line.Argument.Length > 0 ? line.Argument : null, DirectoryLimits.MaxEntries, line.Type, line.Ou, line.Field, cap), now, zone, deadline),
        }, Describe(line), cancellationToken).ConfigureAwait(false);
        if (target is not null && result.Answer is { Failed: false } answer)
        {
            answer.Saved = DirectoryCsv.Save(target, answer, answer.Partial);
            result = result with { Lines = DirectoryRender.CardText(answer) };
        }

        return result with { Args = line };
    }

    /// <summary>
    /// One card read in <paramref name="domain"/> under the call's deadline (a typed line, a clicked row, a group card's switch): the answer, and on a
    /// locked user the PDC emulator.
    /// </summary>
    public static async Task<DirectoryCommandResult> RunCardAsync(DirectoryAccess access, string? domain, Func<IDirectorySession, DirectorySource, DirectoryDeadline, DirectoryAnswer> work, string title, CancellationToken cancellationToken,
        Func<DirectoryAnswer, string?>? titled = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(work);
        var (source, refused) = access.Domain(domain, forceRedacted: false);
        if (source is null)
        {
            return Failed(refused!);
        }

        var result = await access.RunAsync(source, (session, deadline) => work(session, source, deadline), cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return Failed(failed);
        }

        var read = result.Value!;
        string? pdc = read.Kind == "user" && read.Locked ? read.Pdc : null;
        return read.Failed
            ? new DirectoryCommandResult(read, Bare(read.Error!).Split('\n'), true, null, read.CandidatesTitle ?? title, Domain: source.Domain.Name)
            : new DirectoryCommandResult(read, DirectoryRender.CardText(read), false, pdc, titled?.Invoke(read) ?? title, Domain: source.Domain.Name);
    }

    /// <summary>A clicked row's card (2026-10-15, the AD gaps plan, §6b): the object by its DN in the card's own domain.</summary>
    public static Task<DirectoryCommandResult> OpenAsync(DirectoryAccess access, string? domain, DirectoryRowTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(target);
        return RunCardAsync(access, domain, (session, source, deadline) => DirectoryQueries.Open(session, source, target, access.Time.GetUtcNow(), access.Time.LocalTimeZone, deadline, limit: DirectoryLimits.MaxEntries),
            target.Name, cancellationToken, TitleOf);
    }

    /// <summary>A card opened by a click, in the words a typed line's card has ("the user alice"), by what the object turned out to be. Pinned.</summary>
    public static string? TitleOf(DirectoryAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return answer.Self is not { } self ? null : answer.Kind switch
        {
            "user" => "the user " + self.Name,
            "group" => "the group " + self.Name,
            "computer" => "the computer " + self.Name,
            "ou" => "the OU " + self.Name,
            _ => null,
        };
    }

    /// <summary>A group card read again with its switches flipped (the card's <c>n</c> and <c>u</c>), by its DN.</summary>
    public static Task<DirectoryCommandResult> GroupAsync(DirectoryAccess access, string? domain, DirectoryRowTarget group, bool nested, string members, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(group);
        return RunCardAsync(access, domain, (session, source, deadline) =>
            DirectoryQueries.GroupAnswer(session, source, group.Name, nested, members, DirectoryLimits.MaxEntries, access.Time.LocalTimeZone, deadline, dn: group.Dn),
            DescribeGroup(group.Name, nested, members), cancellationToken);
    }

    /// <summary>
    /// The words a line's last word may become (2026-10-15, the AD gaps plan: <c>key:value</c> words may stand anywhere, so the verb is the first
    /// word that is none): the verbs and the domains first; a find's kinds; a group's <c>nested</c> and <c>members:</c>; a key's values.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<string> domains)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(domains);
        int last = argText.LastIndexOf(' ');
        string head = argText[..(last + 1)];
        string current = argText[(last + 1)..];
        var before = head.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => KeyWord(w) is null).ToList();
        IEnumerable<string> pool;
        if (current.Contains(':', StringComparison.Ordinal))
        {
            pool = current[..current.IndexOf(':', StringComparison.Ordinal)].ToLowerInvariant() switch
            {
                "domain" => domains.Select(d => "domain:" + d),
                "type" => DirectoryQueries.FindTypes.Select(t => "type:" + t),
                "field" => DirectoryQueries.FieldNames.Select(f => "field:" + f),
                "members" => DirectoryQueries.MemberChoices.Select(m => "members:" + m),
                _ => [],
            };
        }
        else if (before.Count == 0)
        {
            pool = Verbs.Concat(domains.Select(d => "domain:" + d));
        }
        else
        {
            string verb = before[0].ToLowerInvariant();
            pool = verb switch
            {
                "find" when before.Count == 1 => DirectoryQueries.FindKinds,
                "find" => ["type:", "field:", "ou:", "to:"],
                "group" => ["nested", "members:", "to:"],
                "ou" => ["to:"],
                _ => [],
            };
        }

        return MentionCompleter.Matches(pool.Select(p => new CompletionItem(head + p, "")).ToList(), argText);
    }

    private static DirectoryCommandResult Failed(string error) => new(null, [Bare(error)], true);

    /// <summary>
    /// What a <c>/ad</c> line looked up, in words: the card's title and the ask draft's subject (2026-10-15, the user's report: the draft read
    /// "Look up find:name carol"); a find held to an OU says where. <c>to:</c> is left out: where a list was saved is not what was looked up. Pinned.
    /// </summary>
    public static string Describe(DirectoryCommandArgs line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string days = line.Days.ToString(CultureInfo.InvariantCulture);
        string what = line.Verb switch
        {
            "user" => "the user " + line.Argument,
            "group" => DescribeGroup(line.Argument, line.Nested, line.Members ?? "all"),
            "computer" => "the computer " + line.Argument,
            "domain" => "the domain",
            "ou" => line.Argument.Length > 0 ? "the OU " + line.Argument : "the OU tree",
            "name" => "the accounts whose names hold \"" + line.Argument + "\"",
            "find:name" => line.Type is { } t ? "the " + DirectoryQueries.Plural(t) + " whose names hold \"" + line.Argument + "\"" : "the accounts whose names hold \"" + line.Argument + "\"",
            "find:field" => "the " + DirectoryQueries.Plural(line.Type ?? "user") + " whose " + (line.Field ?? "description") + " holds \"" + line.Argument + "\"",
            "find:os" => "the computers whose OS holds \"" + line.Argument + "\"",
            "find:locked" => "the locked users",
            "find:password_expiring" => "the passwords ending within " + days + " days",
            "find:disabled" => "the disabled users",
            "find:stale_users" => "the users not signed in for " + days + " days",
            "find:stale_computers" => "the computers not signed in for " + days + " days",
            "find:password_never_expires" => "the users whose password never expires",
            "find:account_expiring" => "the accounts expiring within " + days + " days",
            "find:created" => "the " + (line.Type is { } c ? DirectoryQueries.Plural(c) : "objects") + " created in the last " + days + " days",
            "find:privileged" => "the privileged accounts",
            "find:empty_groups" => "the empty groups",
            _ => line.Verb + " " + line.Argument,
        };
        return line.Ou is { } ou ? what + " in the OU " + ou : what;
    }

    /// <summary>A group card's title: what its two switches show ("the users in Staff at any depth"). Pinned.</summary>
    public static string DescribeGroup(string name, bool nested, string members) => members switch
    {
        "users" or "computers" or "groups" => "the " + members + (nested ? " in " + name + " at any depth" : " directly in " + name),
        _ => "the group " + name + (nested ? " and its nested members" : ""),
    };

    /// <summary>The draft an answer's <c>ask</c> leaves: what was looked up and the domain by its entry's name and DNS name. Pinned.</summary>
    public static string AskDraft(string what, string domain, string dnsName) =>
        "Look up " + what + " in Active Directory, domain " + domain + (string.Equals(domain, dnsName, StringComparison.OrdinalIgnoreCase) ? "" : " (" + dnsName + ")") + ", and explain what you find";

    /// <summary>The <c>/eventlog</c> line the "Search 4740" button opens: the Security log's lockouts on the PDC emulator.</summary>
    public static string LockoutLine(string computer) => "Security computer:" + computer + " id:4740 audit:any";

    /// <summary>The button's label. Pinned.</summary>
    public static string LockoutButton(string pdc) => "Search 4740 on " + pdc;
}
