using System.Globalization;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;

namespace NeonSidekick.ActiveDirectory;

/// <summary>What a <c>/ad</c> line asks for: the verb (user, group, computer, find, domain; bare words a name search), its argument, the domain.</summary>
public sealed record DirectoryCommandArgs(string Verb, string Argument, string? Domain, bool Nested, int Days);

public sealed record DirectoryCommandResult(IReadOnlyList<string> Lines, bool Failed, string? LockedPdc = null, string? Title = null);

/// <summary>
/// <c>/ad &lt;words&gt;</c> or <c>/ad user|group|computer|find|domain …</c> [<c>domain:x</c>] (2026-10-15, the admin-tools plan): the user's own
/// hand over the directory, read-only, the same answers the tools give without their fences. On the pane the answer is a card with copy and
/// ask (and on a locked user the "Search 4740" button); elsewhere its lines.
/// </summary>
public static class DirectoryCommand
{
    public const string Word = "/ad";

    public static readonly IReadOnlyList<string> Verbs = ["user", "group", "computer", "find", "domain"];

    public static (DirectoryCommandArgs? Args, string? Error) Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? domain = null;
        bool nested = false;
        var words = new List<string>();
        foreach (string word in args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
            {
                domain = word[7..];
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

        if (words.Count == 0)
        {
            return (null, Usage);
        }

        string verb = words[0].ToLowerInvariant();
        if (!Verbs.Contains(verb))
        {
            return (new DirectoryCommandArgs("name", string.Join(' ', words), domain, false, 0), null);
        }

        var rest = words.Skip(1).ToList();
        if (verb == "domain")
        {
            return (new DirectoryCommandArgs(verb, "", domain, false, 0), null);
        }

        if (rest.Count == 0)
        {
            return (null, Usage);
        }

        if (verb == "find")
        {
            string kind = rest[0].ToLowerInvariant();
            if (!DirectoryQueries.FindKinds.Contains(kind))
            {
                return (null, DirectoryText.BadKind(kind).Replace("Error: ", "", StringComparison.Ordinal));
            }

            int days = kind == "password_expiring" ? 14 : 90;
            var others = rest.Skip(1).ToList();
            if (others.Count > 0 && int.TryParse(others[0], NumberStyles.None, CultureInfo.InvariantCulture, out int given) && given is > 0 and <= 3650)
            {
                days = given;
                others.RemoveAt(0);
            }

            return (new DirectoryCommandArgs("find:" + kind, string.Join(' ', others), domain, false, days), null);
        }

        return (new DirectoryCommandArgs(verb, string.Join(' ', rest), domain, nested, 0), null);
    }

    public const string Usage = "Usage: /ad <name> · user <name> · group <name> [nested] · computer <name> · find locked|password_expiring|disabled|stale_users|stale_computers|name [days] [words] · domain [domain:<name>]";

    public static async Task<DirectoryCommandResult> RunAsync(DirectoryAccess access, string args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!OperatingSystem.IsWindows() || access.System is null)
        {
            return Failed(DirectoryText.NoneHere);
        }

        var (line, error) = Parse(args);
        if (line is null)
        {
            return Failed(error!);
        }

        var (source, refused) = access.Domain(line.Domain, forceRedacted: false);
        if (source is null)
        {
            return Failed(refused!);
        }

        var now = access.Time.GetUtcNow();
        var zone = access.Time.LocalTimeZone;
        string? pdc = null;
        var result = await access.RunAsync(source, session =>
        {
            string text = line.Verb switch
            {
                "user" => DirectoryQueries.User(session, source, line.Argument, now, zone),
                "group" => DirectoryQueries.Group(session, source, line.Argument, line.Nested, DirectoryLimits.MaxEntries),
                "computer" => DirectoryQueries.Computer(session, source, line.Argument, zone),
                "domain" => DirectoryQueries.Domain(session, source),
                "name" => DirectoryQueries.Find(session, source, "name", 0, line.Argument, DirectoryLimits.MaxEntries, now, zone),
                _ => DirectoryQueries.Find(session, source, line.Verb[5..], line.Days, line.Argument.Length > 0 ? line.Argument : null, DirectoryLimits.MaxEntries, now, zone),
            };
            if (line.Verb == "user" && text.Contains("\n  locked:", StringComparison.Ordinal))
            {
                pdc = DirectoryQueries.RoleHolder(session, source.Domain.BaseDn);
            }

            return text;
        }, cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return Failed(failed);
        }

        string body = UntrustedText.Unfenced(result.Value!);
        bool refusal = body.StartsWith("Error:", StringComparison.Ordinal);
        var lines = body.Replace("Error: ", "", StringComparison.Ordinal).Split('\n');
        return new DirectoryCommandResult(lines, refusal, pdc, line.Verb + (line.Argument.Length > 0 ? " " + line.Argument : ""));
    }

    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<string> domains)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int space = argText.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            return MentionCompleter.Matches(Verbs.Select(v => new CompletionItem(v, "")).Concat(domains.Select(d => new CompletionItem("domain:" + d, ""))).ToList(), argText);
        }

        string verb = argText[..space].ToLowerInvariant();
        int last = argText.LastIndexOf(' ');
        string head = argText[..(last + 1)];
        IEnumerable<string> pool = verb == "find" && last == space ? DirectoryQueries.FindKinds : verb == "group" ? ["nested"] : [];
        return MentionCompleter.Matches(pool.Select(p => new CompletionItem(head + p, "")).ToList(), argText);
    }

    private static DirectoryCommandResult Failed(string error) => new([error.Replace("Error: ", "", StringComparison.Ordinal)], true);

    /// <summary>The draft an answer's <c>ask</c> leaves. Pinned.</summary>
    public static string AskDraft(string what, string domain) => "Look up " + what + " in Active Directory (" + domain + ") and explain what you find";

    /// <summary>The <c>/eventlog</c> line the "Search 4740" button opens: the Security log's lockouts on the PDC emulator.</summary>
    public static string LockoutLine(string computer) => "Security computer:" + computer + " id:4740 audit:any";

    /// <summary>The button's label. Pinned.</summary>
    public static string LockoutButton(string pdc) => "Search 4740 on " + pdc;
}
