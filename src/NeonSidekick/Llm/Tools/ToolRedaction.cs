using System.Text.RegularExpressions;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What every tool that hides a secret from the model shares (2026-10-15, the event-log plan: the generic rules of
/// <c>Docker/DockerRedaction.cs</c> moved out so the event-log redaction stands on the same ground): the placeholder a hidden value
/// reads as, a URL's password hidden wherever it stands, and (later on 2026-10-15, the admin-tools plan: moved out of
/// <c>EventLog/EventLogRedaction</c> so a service's image path, a task's command line and a failure command are read the same way)
/// the free-text rules of <see cref="Text"/>. <see cref="Docker.DockerRedaction"/> keeps its own name rules over these;
/// <see cref="EventLog.EventLogRedaction"/> its own field names. Pure.
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

    /// <summary>
    /// <paramref name="text"/> with every secret the free-text rules know hidden: the forms a secret takes on a command line or in a
    /// script — a URL's password, <c>password=…</c> and its kin (<c>Pwd=</c>, <c>AccountKey=</c>, <c>client_secret=</c>, <c>api_key=</c>,
    /// a SAS's <c>sig=</c>), <c>-Password x</c> and <c>--password x</c>, <c>/pass:x</c> and <c>/p:x</c>, <c>-p x</c>, <c>-P x</c> and
    /// <c>-pX</c>, <c>net user u &lt;pw&gt;</c>, <c>net use … /user:u &lt;pw&gt;</c>, <c>schtasks /RP x</c>,
    /// <c>ConvertTo-SecureString '…' -AsPlainText</c>, the <c>PSCredential</c> constructor, <c>$password = '…'</c>,
    /// <c>Authorization: Bearer|Basic x</c>, a JWT, a PEM block. Best-effort: no rule can know every secret in free text.
    /// Since 2026-10-23 (the admin review's A3, C5) also <c>--api_key=</c>, <c>--client_secret=</c>, <c>--account-key</c>,
    /// <c>--access-key</c>, <c>--auth-token</c> and the other token flags; a key in quotes (JSON's <c>"password": "x"</c>); the token keys
    /// (<c>?access_token=…</c>, <c>refresh_token=</c>); an XML element (<c>&lt;Password&gt;x&lt;/Password&gt;</c>). Their neighbors that
    /// hold no secret (<c>token_type</c>, <c>passwordLastSet</c>) are left alone.
    /// </summary>
    public static string Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        string result = Pem().Replace(text, Placeholder);
        result = Jwt().Replace(result, Placeholder);
        result = Authorization().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = Url(result);
        result = NetUser().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = NetUse().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = SchtasksPassword().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = SecureString().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = PsCredential().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = Assignment().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = XmlElement().Replace(result, m => m.Groups["head"].Value + Placeholder + m.Groups["tail"].Value);
        result = KeyValue().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = SlashSwitch().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = DashFlag().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = DashP().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = DashPAttached().Replace(result, m => m.Groups["head"].Value + Placeholder);
        return result;
    }

    [GeneratedRegex(@"(?<head>[a-z][a-z0-9+.\-]*://[^:/@\s]+:)[^@\s/]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    // A quoted or bare value, the shape every rule below hides.
    private const string Value = "(?:\"[^\"]*\"|'[^']*'|[^\\s\"';,&]+)";

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----[\s\S]*?-----END [A-Z0-9 ]+-----", RegexOptions.CultureInvariant)]
    private static partial Regex Pem();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?<head>\bAuthorization\s*[:=]\s*(?:Bearer|Basic|Digest|Negotiate|NTLM)\s+)[^\s""',;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Authorization();

    [GeneratedRegex(@"(?<head>\bnet1?(?:\.exe)?\s+user\s+(?:""[^""]+""|[^\s/""]+)\s+)(?!/|\*(?:\s|$))" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetUser();

    [GeneratedRegex(@"(?<head>\bnet1?(?:\.exe)?\s+use\b[^\r\n]*?/user:(?:""[^""]+""|\S+)\s+)(?!/|\*(?:\s|$))" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetUse();

    [GeneratedRegex(@"(?<head>(?<![\w/])/RP\s+)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchtasksPassword();

    [GeneratedRegex(@"(?<head>\bConvertTo-SecureString\s+(?:-String\s+)?)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecureString();

    [GeneratedRegex(@"(?<head>PSCredential\s*\(\s*(?:""[^""]*""|'[^']*'|\$[\w:]+)\s*,\s*)[^)]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PsCredential();

    [GeneratedRegex(@"(?<head>\$[\w:]*(?:password|passwd|pwd|secret|token|apikey|api_key)[\w]*\s*=\s*)(?:""[^""]*""|'[^']*')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    // The key names that carry a secret: the password's spellings, secrets and keys, and the tokens (2026-10-23, C5).
    private const string SecretKey = @"(?:password|passwd|pwd|pass|secret|client[_\-]?secret|api[_\-]?key|access[_\-]?key|account[_\-]?key|shared[_\-]?access[_\-]?key|private[_\-]?key|sig"
        + @"|token(?=[""']?\s*=)|(?:access|auth|refresh|id|bearer|session|sas|api)[_\-]?token)";   // a bare token only before = ("Elevated Token: Yes" is a Security event's own words)

    // A key, perhaps in quotes (JSON's "password": "x"), then = or :, then its value (C5: the quote after the key missed the rule).
    [GeneratedRegex(@"(?<head>(?<![\w\-$/])" + SecretKey + @"[""']?\s*[=:]\s*)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    // An XML element of a secret key's name: <Password>x</Password> (C5).
    [GeneratedRegex(@"(?<head><(?<tag>" + SecretKey + @")>)[^<]*(?<tail></\k<tag>>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XmlElement();

    [GeneratedRegex(@"(?<head>(?<![\w/])/(?:password|pass|pwd|p):)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlashSwitch();

    // A flag of a secret's name with its value after a space or =; - and _ alike (2026-10-23, A3: --api_key=, --client_secret=,
    // --account-key, --access-key and --auth-token passed).
    [GeneratedRegex(@"(?<head>(?<![\w\-])--?(?:password|passwd|pwd|pass|secret|token|api[_\-]?key|client[_\-]?secret|account[_\-]?key|access[_\-]?key|private[_\-]?key|shared[_\-]?key"
        + @"|(?:auth|access|refresh|id|bearer|session|sas|api)[_\-]?token)(?:\s+|=))(?!-)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DashFlag();

    [GeneratedRegex(@"(?<head>(?<![\w\-])-[pP]\s+)(?!-)" + Value, RegexOptions.CultureInvariant)]
    private static partial Regex DashP();

    // -pSecret (mysql's): the rest of the word after a lowercase -p, unless it is a plain lowercase word (-path, -print).
    [GeneratedRegex(@"(?<head>(?<![\w\-])-p)(?![a-z]+(?:\s|$))(?=[^\s\-])[^\s""';,&]+", RegexOptions.CultureInvariant)]
    private static partial Regex DashPAttached();
}
