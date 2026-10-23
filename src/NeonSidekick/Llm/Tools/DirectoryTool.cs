using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.ActiveDirectory;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the six directory tools share (2026-10-15, the admin-tools plan; <c>ad_ou</c> and <c>to</c> the AD gaps plan the same day): the
/// <see cref="DirectoryAccess"/> door, the arguments' readers, the redacted twin and the <c>to</c> save. All read the directory, all are offered
/// headless too, none changes it (the native layer cannot); <c>to</c> writes only a CSV in the working directory, asked of
/// <see cref="SaveRoot"/> per call (*File tools* on, plan mode off), and a twin has no root. A refusal is an <c>Error:</c> sentence, never a throw.
/// </summary>
public abstract class DirectoryTool : AIFunction, IRedactedTwin
{
    public const string NameArgument = "name";
    public const string DomainArgument = "domain";
    public const string ToArgument = "to";
    public const string LimitArgument = "limit";

    protected DirectoryTool(DirectoryAccess access, bool forceRedacted, Func<DirectorySaveRoot>? saveRoot = null)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        ForceRedacted = forceRedacted;
        SaveRoot = forceRedacted ? null : saveRoot;
    }

    protected DirectoryAccess Access { get; }

    public bool ForceRedacted { get; }

    /// <summary>Where a <c>to</c> may write, asked per call; null (a twin's) refuses every save.</summary>
    protected Func<DirectorySaveRoot>? SaveRoot { get; }

    public abstract AIFunction AsRedacted();

    protected TimeZoneInfo Zone => Access.Time.LocalTimeZone;

    // The shared readers (2026-10-23, the admin review's R18, E7, K6: AdminArguments).
    protected static string? Optional(AIFunctionArguments arguments, string name) => AdminArguments.Optional(arguments, name);

    protected static bool Given(AIFunctionArguments arguments, string name) => AdminArguments.Given(arguments, name);

    protected static (bool Value, string? Error) Flag(AIFunctionArguments arguments, string name, bool fallback) => AdminArguments.Flag(arguments, name, fallback);

    protected static (int Value, string? Error) Number(AIFunctionArguments arguments, string name, int min, int max, int fallback) => AdminArguments.Number(arguments, name, min, max, fallback);

    /// <summary>A choice argument read lower-cased: null when not given, the refusal when it is no choice.</summary>
    protected static (string? Value, string? Error) Choice(AIFunctionArguments arguments, string name, IReadOnlyList<string> choices)
    {
        string? word = Optional(arguments, name)?.ToLowerInvariant();
        return word is null || choices.Contains(word) ? (word, null) : (null, DirectoryText.BadChoice(name, word, choices));
    }

    /// <summary>
    /// The domain the call names, then <paramref name="work"/> on a bound session under the call's deadline (2026-10-20, a review's finding: the
    /// cards ran with none, so ESC and the timeout left their searches running); the answer or the refusal.
    /// </summary>
    protected async Task<string> AskAsync(AIFunctionArguments arguments, Func<IDirectorySession, DirectorySource, DirectoryDeadline, string> work, CancellationToken cancellationToken)
    {
        var (source, refused) = Access.Domain(Optional(arguments, DomainArgument), ForceRedacted);
        if (source is null)
        {
            return refused!;
        }

        var result = await Access.RunAsync(source, (session, deadline) => work(session, source, deadline), cancellationToken).ConfigureAwait(false);
        return result.Error ?? result.Value!;
    }

    /// <summary>
    /// A list's answer, and its <c>to</c> save (2026-10-15, the AD gaps plan, §1b): the save root, the path and a named file's existence checked
    /// before any search (a refusal refuses the whole call, never 10,000 entries read and then not saved); then <paramref name="work"/> under the
    /// call's deadline, handed the cap (<see cref="DirectoryLimits.MaxCounted"/> with a save) and whether a cap's note may offer one; then the
    /// rows written, the save's line the answer's last.
    /// </summary>
    protected async Task<string> AskListAsync(AIFunctionArguments arguments, string question, Func<IDirectorySession, DirectorySource, DirectoryDeadline, int, bool, DirectoryAnswer> work, CancellationToken cancellationToken)
    {
        DirectorySaveTarget? target = null;
        if (Optional(arguments, ToArgument) is { } to)
        {
            var root = SaveRoot?.Invoke() ?? DirectorySaveRoot.Refused(DirectoryText.SaveNotHere);
            if (root.Root is null)
            {
                return root.Refusal ?? DirectoryText.SaveNotHere;
            }

            var (checkedTarget, error) = DirectoryCsv.Check(root.Root, to, question);
            if (checkedTarget is null)
            {
                return error!;
            }

            target = checkedTarget;
        }

        var (source, refused) = Access.Domain(Optional(arguments, DomainArgument), ForceRedacted);
        if (source is null)
        {
            return refused!;
        }

        bool canSave = target is null && SaveRoot?.Invoke() is { Root: not null };
        var result = await Access.RunAsync(source, (session, deadline) => work(session, source, deadline, target is null ? DirectoryLimits.MaxEntries : DirectoryLimits.MaxCounted, canSave), cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return failed;
        }

        var answer = result.Value!;
        if (target is not null && !answer.Failed)
        {
            // The save root judged again as the rows are written (2026-10-23, the admin review's F7: File tools switched off, or plan mode on,
            // during a 40 s search still wrote).
            var now = SaveRoot?.Invoke() ?? DirectorySaveRoot.Refused(DirectoryText.SaveNotHere);
            answer.Saved = now.Root is null ? DirectoryText.SaveClosedLate(now.Refusal ?? DirectoryText.SaveNotHere) : DirectoryCsv.Save(target, answer, answer.Partial);
        }

        return DirectoryRender.ForModel(answer);
    }
}

/// <summary><c>ad_user(name, domain?)</c> (2026-10-15): a user's card.</summary>
public sealed class AdUserTool(DirectoryAccess access, bool forceRedacted = false) : DirectoryTool(access, forceRedacted)
{
    public const string ToolName = "ad_user";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The account name, UPN, display name, cn or mail." },
            "domain": { "type": "string", "description": "Which domain, by its ticked name or its DNS name; leave out for this PC's (or the only one)." }
          },
          "required": ["name"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Reads an Active Directory user: enabled, locked and when, bad passwords, password expiry, last sign-in, groups (nested security groups and direct distribution groups), manager, direct reports, title, department and OU.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdUserTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return DirectoryText.Missing(NameArgument);
        }

        return await AskAsync(arguments, (session, source, deadline) => DirectoryQueries.User(session, source, name, Access.Time.GetUtcNow(), Zone, deadline), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>ad_group(name, nested?, members?, limit?, to?, domain?)</c> (2026-10-15): a group's members, direct or nested, and its card.</summary>
public sealed class AdGroupTool(DirectoryAccess access, bool forceRedacted = false, Func<DirectorySaveRoot>? saveRoot = null) : DirectoryTool(access, forceRedacted, saveRoot)
{
    public const string ToolName = "ad_group";
    public const string NestedArgument = "nested";
    public const string MembersArgument = "members";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The group's account name, cn or display name." },
            "nested": { "type": "boolean", "description": "Every member at any depth, through the groups in it, each with the group it comes through (default false: direct members)." },
            "members": { "type": "string", "enum": ["all", "users", "computers", "groups"], "description": "Only one kind of member (default all; only all shows contacts, service accounts and foreign principals)." },
            "limit": { "type": "integer", "description": "The most members to list, 1 to 1000 (default 100); the total is always counted." },
            "to": { "type": "string", "description": "Save every member (up to 10,000) as a CSV: a .csv path in the working directory, or a folder there. Needs File tools; never over an existing file." },
            "domain": { "type": "string", "description": "Which domain, by its ticked name or its DNS name; leave out for this PC's (or the only one)." }
          },
          "required": ["name"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Lists an Active Directory group's members, direct or nested (every depth, with the group each comes through), optionally only users, computers or groups; and its type, owner and the groups it is in.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdGroupTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return DirectoryText.Missing(NameArgument);
        }

        var (nested, nestedError) = Flag(arguments, NestedArgument, fallback: false);
        var (members, membersError) = Choice(arguments, MembersArgument, DirectoryQueries.MemberChoices);
        var (limit, limitError) = Number(arguments, LimitArgument, 1, DirectoryLimits.MaxEntries, 100);
        if ((nestedError ?? membersError ?? limitError) is { } error)
        {
            return error;
        }

        string question = "group " + name + (nested ? " nested" : "") + (members is null or "all" ? "" : " " + members);
        return await AskListAsync(arguments, question, (session, source, deadline, _, canSave) =>
            DirectoryQueries.GroupAnswer(session, source, name, nested, members ?? "all", limit, Zone, deadline, canSave), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>ad_computer(name, domain?)</c> (2026-10-15): a computer's card, or a service account's.</summary>
public sealed class AdComputerTool(DirectoryAccess access, bool forceRedacted = false) : DirectoryTool(access, forceRedacted)
{
    public const string ToolName = "ad_computer";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The computer's (or a managed service account's) name or DNS name." },
            "domain": { "type": "string", "description": "Which domain, by its ticked name or its DNS name; leave out for this PC's (or the only one)." }
          },
          "required": ["name"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Reads an Active Directory computer or managed service account: its operating system, whether it is enabled, its last sign-in, OU, description, owner and groups.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdComputerTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return DirectoryText.Missing(NameArgument);
        }

        return await AskAsync(arguments, (session, source, deadline) => DirectoryQueries.Computer(session, source, name, Zone, deadline), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>ad_find(kind, days?, words?, type?, field?, ou?, limit?, to?, domain?)</c> (2026-10-15): structured searches only — the model never writes a filter.</summary>
public sealed class AdFindTool(DirectoryAccess access, bool forceRedacted = false, Func<DirectorySaveRoot>? saveRoot = null) : DirectoryTool(access, forceRedacted, saveRoot)
{
    public const string ToolName = "ad_find";
    public const string KindArgument = "kind";
    public const string DaysArgument = "days";
    public const string WordsArgument = "words";
    public const string TypeArgument = "type";
    public const string FieldArgument = "field";
    public const string OuArgument = "ou";
    public const string ScanFromArgument = "scan_from";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["locked", "password_expiring", "disabled", "stale_users", "stale_computers", "name", "field", "os", "password_never_expires", "account_expiring", "created", "privileged", "empty_groups"] },
            "days": { "type": "integer", "description": "password_expiring: within this many days (default 14); account_expiring and created: 30; stale_users and stale_computers: not signed in for this many days (90)." },
            "words": { "type": "string", "description": "name: text the account name, display name, cn, mail, UPN or description holds; field: text the field holds; os: text the operating system holds." },
            "type": { "type": "string", "enum": ["user", "group", "computer", "ou", "contact", "service"], "description": "name and created: only this type (name without it searches users, groups and computers); field: the type whose field is searched (default user). service is a managed service account." },
            "field": { "type": "string", "enum": ["department", "title", "company", "office", "description"], "description": "field: which one. A user or contact has all five; a group, computer, OU or service account only description." },
            "ou": { "type": "string", "description": "Only inside this OU (every kind but privileged): a path such as Neon/Engineering, a DN, or a name." },
            "limit": { "type": "integer", "description": "The most to list, 1 to 1000 (default 50)." },
            "scan_from": { "type": "integer", "description": "name and field on a redacted domain: go on matching descriptions from this object (an answer's next: gives it)." },
            "to": { "type": "string", "description": "Save every result (up to 10,000) as a CSV: a .csv path in the working directory, or a folder there. Needs File tools; never over an existing file." },
            "domain": { "type": "string", "description": "Which domain, by its ticked name or its DNS name; leave out for this PC's (or the only one)." }
          },
          "required": ["kind"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Finds Active Directory objects: by name (users, groups, computers, OUs, contacts or service accounts), by a field (department, title, company, office, description) or OS; locked users, passwords and accounts about to expire, passwords that never expire, disabled users, stale users or computers, new objects, privileged accounts, empty groups; optionally inside one OU.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdFindTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string kind = Optional(arguments, KindArgument)?.ToLowerInvariant() ?? "";
        if (kind.Length == 0)
        {
            return DirectoryText.Missing(KindArgument);
        }

        string? words = Optional(arguments, WordsArgument);
        string? type = Optional(arguments, TypeArgument)?.ToLowerInvariant();
        string? field = Optional(arguments, FieldArgument)?.ToLowerInvariant();
        string? ou = Optional(arguments, OuArgument);
        // The domain's own detail, not only the tool's (2026-10-23, the admin review's R5: office on a redacted domain bound and searched for the ou
        // before it was refused); read from the catalog, no password resolved.
        bool redacted = ForceRedacted || Access.Catalog().Find(Optional(arguments, DomainArgument)).Domain is not { Config.Full: true };
        if (DirectoryQueries.FindProblem(kind, Given(arguments, DaysArgument), words, type, field, ou, redacted) is { } problem)
        {
            return problem;
        }

        var (days, daysError) = Number(arguments, DaysArgument, 1, 3650, DirectoryQueries.DefaultDays(kind));
        var (limit, limitError) = Number(arguments, LimitArgument, 1, DirectoryLimits.MaxEntries, 50);
        var (scanFrom, scanError) = Number(arguments, ScanFromArgument, 0, DirectoryLimits.MaxScanFrom, 0);
        if ((daysError ?? limitError ?? scanError) is { } error)
        {
            return error;
        }

        string question = "find " + kind + (type is null ? "" : " " + type) + (field is null ? "" : " " + field) + (words is null ? "" : " " + words);
        return await AskListAsync(arguments, question, (session, source, deadline, cap, canSave) =>
            DirectoryQueries.FindAnswer(session, source, new FindOptions(kind, days, words, limit, type, ou, field, cap, canSave, scanFrom), Access.Time.GetUtcNow(), Zone, deadline), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>ad_ou(ou?, limit?, to?, domain?)</c> (2026-10-15, the AD gaps plan, §4): the OU tree, or one OU's card and contents.</summary>
public sealed class AdOuTool(DirectoryAccess access, bool forceRedacted = false, Func<DirectorySaveRoot>? saveRoot = null) : DirectoryTool(access, forceRedacted, saveRoot)
{
    public const string ToolName = "ad_ou";
    public const string OuArgument = "ou";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "ou": { "type": "string", "description": "The OU: a path such as Neon/Engineering (a / in a name written \\/), a DN, or a name. Leave out for the whole tree." },
            "limit": { "type": "integer", "description": "The most of the OU's objects to list, 1 to 1000 (default 100); they are always counted." },
            "to": { "type": "string", "description": "Save the list (the tree, or the OU's objects) as a CSV: a .csv path in the working directory, or a folder there. Needs File tools; never over an existing file." },
            "domain": { "type": "string", "description": "Which domain, by its ticked name or its DNS name; leave out for this PC's (or the only one)." }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Lists Active Directory's OUs: the whole tree as paths with descriptions, or one OU's owner, linked GPOs and the objects directly in it (users, groups, computers, child OUs), counted by kind.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdOuTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string? ou = Optional(arguments, OuArgument);
        var (limit, limitError) = Number(arguments, LimitArgument, 1, DirectoryLimits.MaxEntries, 100);
        if (limitError is { } error)
        {
            return error;
        }

        return await AskListAsync(arguments, "ou " + (ou ?? "tree"), (session, source, deadline, cap, canSave) =>
            DirectoryQueries.OuAnswer(session, source, ou, limit, deadline, canSave, cap: cap, zone: Zone), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>ad_domain(domain?)</c> (2026-10-15): the domain controllers, the levels, the policies and the role holders.</summary>
public sealed class AdDomainTool(DirectoryAccess access, bool forceRedacted = false) : DirectoryTool(access, forceRedacted)
{
    public const string ToolName = "ad_domain";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "domain": { "type": "string", "description": "Which domain, by its ticked name or its DNS name; leave out for this PC's (or the only one)." }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Reads an Active Directory domain: its domain controllers, functional levels, password and lockout policy, the fine-grained password policies this account may see, and the role holders including the PDC emulator.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdDomainTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await AskAsync(arguments, (session, source, deadline) => DirectoryQueries.Domain(session, source, deadline, Zone), cancellationToken).ConfigureAwait(false);
}
