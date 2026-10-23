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
public static class EventLogRedaction
{
    /// <summary>What a hidden value reads as.</summary>
    public const string Placeholder = ToolRedaction.Placeholder;

    /// <summary>Whether a field's name says it holds a secret: the strict set, read with case and <c>_ - .</c> ignored.</summary>
    /// <remarks>
    /// Since 2026-10-23 (the admin review's W5) also a token by name (<c>token</c>, <c>accesstoken</c>, <c>refreshtoken</c>,
    /// <c>sastoken</c>: a name ending in <c>token</c>, so <c>TokenElevationType</c> is kept), a credential, a passphrase, an account,
    /// access or shared key, and a PIN. Never Security's <c>ElevatedToken</c> (4624's yes or no, 2026-10-23, the branch review of DP7: an
    /// audit fact a security review asks about, hidden by the token rule).
    /// </remarks>
    public static bool IsSecretField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string folded = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (folded == "elevatedtoken")
        {
            return false;
        }

        return folded.Contains("password", StringComparison.Ordinal) || folded.Contains("passwd", StringComparison.Ordinal)
            || folded == "pwd" || folded.EndsWith("pwd", StringComparison.Ordinal) || folded.Contains("secret", StringComparison.Ordinal)
            || folded.Contains("apikey", StringComparison.Ordinal) || folded.Contains("privatekey", StringComparison.Ordinal)
            || folded.Contains("connectionstring", StringComparison.Ordinal)
            || folded.EndsWith("token", StringComparison.Ordinal) || folded.EndsWith("tokens", StringComparison.Ordinal)
            || folded is "credential" or "credentials" || folded.EndsWith("credential", StringComparison.Ordinal) || folded.EndsWith("credentials", StringComparison.Ordinal)
            || folded.Contains("passphrase", StringComparison.Ordinal)
            || folded.Contains("accountkey", StringComparison.Ordinal) || folded.Contains("accesskey", StringComparison.Ordinal) || folded.Contains("sharedkey", StringComparison.Ordinal)
            || folded is "pin" or "pincode" or "userpin" or "smartcardpin";
    }

    /// <summary>A field's value as the model reads it: all of it hidden for a secret's name, else its free text's secrets.</summary>
    public static string Field(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        return IsSecretField(name) ? Placeholder : Text(value);
    }

    /// <summary>
    /// A field as the redacted detail shows it: <see cref="Field(string, string)"/>, and a classic event's raw <c>Binary</c> as its length
    /// alone (2026-10-23, the admin review's W3, decision DP5: hex passed the free-text rules, so a secret written as raw data was decodable).
    /// </summary>
    public static string Field(EventField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Binary ? EventLogText.BinaryLength(field.Value.Count(Uri.IsHexDigit) / 2) : Field(field.Name, field.Value);
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

    /// <summary>The shortest secret value <see cref="Rendered"/> takes out of a text; a shorter one drops the text instead.</summary>
    public const int MinRenderedSecret = 4;

    /// <summary>
    /// An event's own rendered text (<c>RenderingInfo/Message</c>, a forwarded event's) as the redacted detail may show it, or null
    /// (2026-10-20, a review): it was formatted from the raw values, so each secret-named field's value (<paramref name="rawFields"/>) is
    /// replaced wherever it stands; a value too short to replace safely (under <see cref="MinRenderedSecret"/>) drops the text whole.
    /// The free-text rules run after, as on any message.
    /// </summary>
    public static string? Rendered(string text, IReadOnlyList<EventField> rawFields)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rawFields);
        foreach (var field in rawFields)
        {
            string value = field.Value.Trim();
            if (value.Length == 0 || !IsSecretField(field.Name))
            {
                continue;
            }

            if (value.Length < MinRenderedSecret)
            {
                return null;
            }

            text = text.Replace(value, Placeholder, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary><paramref name="text"/> with every secret the free-text rules know hidden (<see cref="ToolRedaction.Text"/>'s since later on 2026-10-15).</summary>
    public static string Text(string text) => ToolRedaction.Text(text);
}
