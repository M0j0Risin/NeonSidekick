using System.Text.RegularExpressions;

namespace NeonSidekick.Docker;

/// <summary>
/// What <c>docker_inspect</c> hides before the model reads a container (2026-10-02, the plan's rule): a container's
/// environment is where its passwords live (<c>MSSQL_SA_PASSWORD</c>, <c>ORACLE_PASSWORD</c>, <c>MYSQL_ROOT_PASSWORD</c>
/// on the user's own), so every value of it goes — the names stay, so the model can say what is set. A label, or a
/// command-line flag, whose name sounds secret (<see cref="IsSecretName"/>) loses its value too, in the <c>--flag=value</c>
/// and the <c>--flag value</c> forms alike, and a URL's password goes wherever it stands. Every value hidden reads
/// <see cref="DockerText.Redacted"/>. A log is not redacted: no rule can know a secret in free text, which the rule
/// sentence says. Pure.
/// </summary>
public static partial class DockerRedaction
{
    /// <summary>Whether a variable, label or flag name sounds like it holds a secret.</summary>
    public static bool IsSecretName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return SecretWords().IsMatch(name);
    }

    /// <summary>An environment entry with its value hidden: <c>KEY=&lt;redacted&gt;</c>; an entry with no <c>=</c> as it is.</summary>
    public static string Env(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int equals = entry.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? entry : entry[..(equals + 1)] + DockerText.Redacted;
    }

    /// <summary>A label's value, hidden when its key sounds secret, a URL's password hidden either way.</summary>
    public static string Label(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return IsSecretName(key) ? DockerText.Redacted : Url(value);
    }

    /// <summary>
    /// A command line's words with the secret ones hidden: <c>--password=x</c> and <c>-token=x</c> keep the flag,
    /// <c>--password x</c> loses the word after it (unless that word is itself a flag), <c>DB_PASSWORD=x</c> keeps the
    /// name, and a URL's password goes in every word.
    /// </summary>
    public static IReadOnlyList<string> Args(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = new List<string>(args.Count);
        bool hideNext = false;
        foreach (string arg in args)
        {
            if (hideNext && !arg.StartsWith('-'))
            {
                result.Add(DockerText.Redacted);
                hideNext = false;
                continue;
            }

            hideNext = false;
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && IsSecretName(arg[..equals]))
            {
                result.Add(arg[..(equals + 1)] + DockerText.Redacted);
                continue;
            }

            if (arg.StartsWith('-') && equals < 0 && IsSecretName(arg.TrimStart('-')))
            {
                hideNext = true;
            }

            result.Add(Url(arg));
        }

        return result;
    }

    /// <summary>One shell line (<c>CMD-SHELL</c>'s) with its secret words hidden, split and joined on spaces.</summary>
    public static string Line(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return string.Join(' ', Args(line.Split(' ')));
    }

    /// <summary>Every <c>scheme://user:password@</c> in <paramref name="text"/> with the password hidden.</summary>
    public static string Url(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UrlCredentials().Replace(text, m => m.Groups["head"].Value + DockerText.Redacted + "@");
    }

    [GeneratedRegex(@"pass|pwd|secret|token|key|credential|auth|cert|private", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretWords();

    [GeneratedRegex(@"(?<head>[a-z][a-z0-9+.\-]*://[^:/@\s]+:)[^@\s/]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();
}
