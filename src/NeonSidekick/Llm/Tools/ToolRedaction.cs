using System.Text.RegularExpressions;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What every tool that hides a secret from the model shares (2026-10-15, the event-log plan: the generic rules of
/// <c>Docker/DockerRedaction.cs</c> moved out so the event-log redaction stands on the same ground): the placeholder a hidden value
/// reads as, and a URL's password hidden wherever it stands. <see cref="Docker.DockerRedaction"/> keeps its own name rules over these;
/// <see cref="EventLog.EventLogRedaction"/> its own. Pure.
/// </summary>
public static partial class ToolRedaction
{
    /// <summary>What a hidden value reads as: <c>&lt;redacted&gt;</c> (Docker's word since 2026-10-02). Pinned.</summary>
    public const string Placeholder = "<redacted>";

    /// <summary>Every <c>scheme://user:password@</c> in <paramref name="text"/> with the password hidden.</summary>
    public static string Url(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return UrlCredentials().Replace(text, m => m.Groups["head"].Value + Placeholder + "@");
    }

    [GeneratedRegex(@"(?<head>[a-z][a-z0-9+.\-]*://[^:/@\s]+:)[^@\s/]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();
}
