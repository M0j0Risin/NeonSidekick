using System.Text.RegularExpressions;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.EventLog;

/// <summary>
/// What the <c>redacted</c> detail hides before the model reads an event (2026-10-15, the event-log plan), over
/// <see cref="ToolRedaction"/>'s placeholder and URL rule. Two kinds of rule:
///
/// <para><b>Field names</b> (<see cref="IsSecretField"/>): a strict set — <c>password</c>, <c>passwd</c>, <c>pwd</c>, <c>secret</c>,
/// <c>apikey</c>, <c>privatekey</c>, <c>clientsecret</c>, <c>connectionstring</c> — never Docker's wide <c>IsSecretName</c>, which
/// would hide the Security log's <c>AuthenticationPackageName</c>, <c>KeyLength</c> and <c>TokenElevationType</c>, the very fields a
/// sign-in question needs.</para>
///
/// <para><b>Free text</b> (<see cref="Text"/>): the forms a secret takes on a command line or in a script — a URL's password,
/// <c>password=…</c> and its kin (<c>Pwd=</c>, <c>AccountKey=</c>, <c>client_secret=</c>, <c>api_key=</c>, a SAS's <c>sig=</c>),
/// <c>-Password x</c> and <c>--password x</c>, <c>/pass:x</c> and <c>/p:x</c>, <c>-p x</c>, <c>-P x</c> and <c>-pX</c>, <c>net user u
/// &lt;pw&gt;</c>, <c>net use … /user:u &lt;pw&gt;</c>, <c>schtasks /RP x</c>, <c>ConvertTo-SecureString '…' -AsPlainText</c>, the
/// <c>PSCredential</c> constructor, <c>$password = '…'</c>, <c>Authorization: Bearer|Basic x</c>, a JWT, a PEM block.</para>
///
/// Applied to every field value and, through the inserts (<see cref="Insert"/>), to the message, so a hidden value never reaches
/// the model by another road. Best-effort: no rule can know every secret in free text, and the docs say so. Pure.
/// </summary>
public static partial class EventLogRedaction
{
    /// <summary>What a hidden value reads as.</summary>
    public const string Placeholder = ToolRedaction.Placeholder;

    /// <summary>Whether a field's name says it holds a secret: the strict set, read with case and <c>_ - .</c> ignored.</summary>
    public static bool IsSecretField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string folded = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return folded.Contains("password", StringComparison.Ordinal) || folded.Contains("passwd", StringComparison.Ordinal)
            || folded == "pwd" || folded.EndsWith("pwd", StringComparison.Ordinal) || folded.Contains("secret", StringComparison.Ordinal)
            || folded.Contains("apikey", StringComparison.Ordinal) || folded.Contains("privatekey", StringComparison.Ordinal)
            || folded.Contains("connectionstring", StringComparison.Ordinal);
    }

    /// <summary>A field's value as the model reads it: all of it hidden for a secret's name, else its free text's secrets.</summary>
    public static string Field(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        return IsSecretField(name) ? Placeholder : Text(value);
    }

    /// <summary>
    /// An insert of the message as the model reads it (<see cref="RawEvent.Format"/>'s rewrite): the field at its position in
    /// <paramref name="fields"/> names it, so a secret's name hides it all; any other has its free text's secrets hidden.
    /// </summary>
    public static string Insert(IReadOnlyList<EventField> fields, int index, string value)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(value);
        return index >= 0 && index < fields.Count ? Field(fields[index].Name, value) : Text(value);
    }

    /// <summary><paramref name="text"/> with every secret the free-text rules know hidden.</summary>
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
        result = ToolRedaction.Url(result);
        result = NetUser().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = NetUse().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = SchtasksPassword().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = SecureString().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = PsCredential().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = Assignment().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = KeyValue().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = SlashSwitch().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = DashFlag().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = DashP().Replace(result, m => m.Groups["head"].Value + Placeholder);
        result = DashPAttached().Replace(result, m => m.Groups["head"].Value + Placeholder);
        return result;
    }

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

    [GeneratedRegex(@"(?<head>(?<![\w\-$/])(?:password|passwd|pwd|pass|secret|client[_\-]?secret|api[_\-]?key|access[_\-]?key|account[_\-]?key|shared[_\-]?access[_\-]?key|sig)\s*[=:]\s*)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?<head>(?<![\w/])/(?:password|pass|pwd|p):)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlashSwitch();

    [GeneratedRegex(@"(?<head>(?<![\w\-])--?(?:password|passwd|pwd|pass|secret|token|api-?key|client-?secret)(?:\s+|=))(?!-)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DashFlag();

    [GeneratedRegex(@"(?<head>(?<![\w\-])-[pP]\s+)(?!-)" + Value, RegexOptions.CultureInvariant)]
    private static partial Regex DashP();

    // -pSecret (mysql's): the rest of the word after a lowercase -p, unless it is a plain lowercase word (-path, -print).
    [GeneratedRegex(@"(?<head>(?<![\w\-])-p)(?![a-z]+(?:\s|$))(?=[^\s\-])[^\s""';,&]+", RegexOptions.CultureInvariant)]
    private static partial Regex DashPAttached();
}
