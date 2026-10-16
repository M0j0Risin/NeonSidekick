using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.ActiveDirectory;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the five directory tools share (2026-10-15, the admin-tools plan): the <see cref="DirectoryAccess"/> door, the arguments' readers and the
/// redacted twin. All read, all are offered headless too, none changes anything (the native layer cannot). A refusal is an <c>Error:</c>
/// sentence, never a throw.
/// </summary>
public abstract class DirectoryTool : AIFunction, IRedactedTwin
{
    public const string NameArgument = "name";
    public const string DomainArgument = "domain";

    protected DirectoryTool(DirectoryAccess access, bool forceRedacted)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        ForceRedacted = forceRedacted;
    }

    protected DirectoryAccess Access { get; }

    public bool ForceRedacted { get; }

    public abstract AIFunction AsRedacted();

    protected TimeZoneInfo Zone => Access.Time.LocalTimeZone;

    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    protected static (bool Value, string? Error) Flag(AIFunctionArguments arguments, string name, bool fallback)
    {
        if (!ToolArguments.TryReadBoolean(arguments, name, out bool? value, out string raw))
        {
            return (fallback, DirectoryText.NotBoolean(name, raw));
        }

        return (value ?? fallback, null);
    }

    protected static (int Value, string? Error) Number(AIFunctionArguments arguments, string name, int min, int max, int fallback)
    {
        if (!ToolArguments.TryReadInt64(arguments, name, out long? value, out string raw) || value is { } n && (n < min || n > max))
        {
            string given = raw.Length > 0 ? raw : value?.ToString(CultureInfo.InvariantCulture) ?? "";
            return (fallback, DirectoryText.OutOfRange(name, given, min, max));
        }

        return ((int)(value ?? fallback), null);
    }

    /// <summary>The domain the call names, then <paramref name="work"/> on a bound session; the answer or the refusal.</summary>
    protected async Task<string> AskAsync(AIFunctionArguments arguments, Func<IDirectorySession, DirectorySource, string> work, CancellationToken cancellationToken)
    {
        var (source, refused) = Access.Domain(Optional(arguments, DomainArgument), ForceRedacted);
        if (source is null)
        {
            return refused!;
        }

        var result = await Access.RunAsync(source, session => work(session, source), cancellationToken).ConfigureAwait(false);
        return result.Error ?? result.Value!;
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
            "domain": { "type": "string", "description": "Which domain, as the ticked ones are named; leave out for this PC's (or the only one)." }
          },
          "required": ["name"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Reads an Active Directory user: enabled, locked and when, bad passwords, password expiry, last sign-in, groups (nested), manager, title, department and OU.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdUserTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return DirectoryText.Missing(NameArgument);
        }

        return await AskAsync(arguments, (session, source) => DirectoryQueries.User(session, source, name, Access.Time.GetUtcNow(), Zone), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>ad_group(name, nested?, limit?, domain?)</c> (2026-10-15): a group's members.</summary>
public sealed class AdGroupTool(DirectoryAccess access, bool forceRedacted = false) : DirectoryTool(access, forceRedacted)
{
    public const string ToolName = "ad_group";
    public const string NestedArgument = "nested";
    public const string LimitArgument = "limit";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The group's account name, cn or display name." },
            "nested": { "type": "boolean", "description": "Include the members of member groups (default false: direct members)." },
            "limit": { "type": "integer", "description": "The most members to list, 1 to 1000 (default 100)." },
            "domain": { "type": "string", "description": "Which domain; leave out for this PC's (or the only one)." }
          },
          "required": ["name"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Lists an Active Directory group's members, direct or nested.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdGroupTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return DirectoryText.Missing(NameArgument);
        }

        var (nested, nestedError) = Flag(arguments, NestedArgument, fallback: false);
        var (limit, limitError) = Number(arguments, LimitArgument, 1, DirectoryLimits.MaxEntries, 100);
        if ((nestedError ?? limitError) is { } error)
        {
            return error;
        }

        return await AskAsync(arguments, (session, source) => DirectoryQueries.Group(session, source, name, nested, limit), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>ad_computer(name, domain?)</c> (2026-10-15): a computer's card.</summary>
public sealed class AdComputerTool(DirectoryAccess access, bool forceRedacted = false) : DirectoryTool(access, forceRedacted)
{
    public const string ToolName = "ad_computer";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The computer's name or DNS name." },
            "domain": { "type": "string", "description": "Which domain; leave out for this PC's (or the only one)." }
          },
          "required": ["name"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Reads an Active Directory computer: its operating system, whether it is enabled, its last sign-in, OU and description.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdComputerTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return DirectoryText.Missing(NameArgument);
        }

        return await AskAsync(arguments, (session, source) => DirectoryQueries.Computer(session, source, name, Zone), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>ad_find(kind, days?, words?, limit?, domain?)</c> (2026-10-15): structured searches only — the model never writes a filter.</summary>
public sealed class AdFindTool(DirectoryAccess access, bool forceRedacted = false) : DirectoryTool(access, forceRedacted)
{
    public const string ToolName = "ad_find";
    public const string KindArgument = "kind";
    public const string DaysArgument = "days";
    public const string WordsArgument = "words";
    public const string LimitArgument = "limit";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["locked", "password_expiring", "disabled", "stale_users", "stale_computers", "name"] },
            "days": { "type": "integer", "description": "For password_expiring: within this many days (default 14); for the stale kinds: not signed in for this many days (default 90)." },
            "words": { "type": "string", "description": "For name: the text the account name, display name, cn, mail or UPN contains." },
            "limit": { "type": "integer", "description": "The most to list, 1 to 1000 (default 50)." },
            "domain": { "type": "string", "description": "Which domain; leave out for this PC's (or the only one)." }
          },
          "required": ["kind"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Finds Active Directory accounts: locked users, passwords about to expire, disabled users, users or computers not signed in lately, or names holding some words.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdFindTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string kind = Optional(arguments, KindArgument)?.ToLowerInvariant() ?? "";
        if (!DirectoryQueries.FindKinds.Contains(kind))
        {
            return kind.Length == 0 ? DirectoryText.Missing(KindArgument) : DirectoryText.BadKind(kind);
        }

        string? words = Optional(arguments, WordsArgument);
        if (kind == "name" && words is null)
        {
            return DirectoryText.WordsNeeded;
        }

        var (days, daysError) = Number(arguments, DaysArgument, 1, 3650, kind == "password_expiring" ? 14 : 90);
        var (limit, limitError) = Number(arguments, LimitArgument, 1, DirectoryLimits.MaxEntries, 50);
        if ((daysError ?? limitError) is { } error)
        {
            return error;
        }

        return await AskAsync(arguments, (session, source) => DirectoryQueries.Find(session, source, kind, days, words, limit, Access.Time.GetUtcNow(), Zone), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>ad_domain(domain?)</c> (2026-10-15): the domain controllers, the levels, the policy and the role holders.</summary>
public sealed class AdDomainTool(DirectoryAccess access, bool forceRedacted = false) : DirectoryTool(access, forceRedacted)
{
    public const string ToolName = "ad_domain";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "domain": { "type": "string", "description": "Which domain; leave out for this PC's (or the only one)." }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Reads an Active Directory domain: its domain controllers, functional levels, password and lockout policy, and the role holders including the PDC emulator.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new AdDomainTool(Access, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await AskAsync(arguments, (session, source) => DirectoryQueries.Domain(session, source), cancellationToken).ConfigureAwait(false);
}
