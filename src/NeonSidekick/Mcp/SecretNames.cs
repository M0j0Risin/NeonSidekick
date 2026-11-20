namespace NeonSidekick.Mcp;

/// <summary>
/// Whether a name says its value is a secret (2026-11-20, the mcp-ask review's #21, #47 and #32): one rule for an <c>mcp.json</c> header and
/// env name alike, and for an argument's name in the audit line (<see cref="McpText.ArgumentsJson"/>). The old rule matched a short word
/// anywhere, so <c>MAX_TOKENS</c>, <c>TOKENIZERS_PARALLELISM</c> and <c>CONNECTION_TIMEOUT</c> were sealed and <c>DB_PASS</c> wasn't.
/// <list type="bullet">
/// <item>A long word anywhere in the name, its separators taken out: <c>PASSWORD</c>, <c>SECRET</c>, <c>APIKEY</c> (<c>X-API-Key</c>),
/// <c>AUTHORIZATION</c>, <c>COOKIE</c> and their kin.</item>
/// <item>A short word only whole, the name split at each separator and where a lowercase letter meets an uppercase one: <c>TOKEN</c>,
/// <c>AUTH</c>, <c>PASS</c>, <c>PWD</c>, <c>PAT</c>, <c>DSN</c>; <c>KEY</c> only as the last word (<c>SSH_KEY</c>, never <c>KEY_ID</c>).</item>
/// </list>
/// Without regard to case. Pure, and no array is built per call.
/// </summary>
public static class SecretNames
{
    private static readonly string[] LongWords =
    [
        "PASSWORD", "PASSWD", "PASSPHRASE", "SECRET", "CREDENTIAL", "APIKEY", "ACCESSKEY", "PRIVATEKEY", "ACCOUNTKEY", "SHAREDKEY",
        "CONNECTIONSTRING", "AUTHORIZATION", "COOKIE",
    ];

    private static readonly string[] ShortWords = ["TOKEN", "AUTH", "PASS", "PWD", "PAT", "DSN"];

    private const string LastWord = "KEY";

    /// <summary>The longest name squashed on the stack; a longer one gets a heap buffer.</summary>
    private const int StackChars = 256;

    /// <summary>Whether <paramref name="name"/> says its value is a secret, by the rules above.</summary>
    public static bool IsSecret(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return false;
        }

        Span<char> buffer = name.Length <= StackChars ? stackalloc char[name.Length] : new char[name.Length];
        int length = 0;
        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                buffer[length++] = c;
            }
        }

        ReadOnlySpan<char> squashed = buffer[..length];
        foreach (string word in LongWords)
        {
            if (squashed.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        ReadOnlySpan<char> last = default;
        int i = 0;
        while (i < name.Length)
        {
            if (!char.IsAsciiLetterOrDigit(name[i]))
            {
                i++;
                continue;
            }

            int start = i++;
            while (i < name.Length && char.IsAsciiLetterOrDigit(name[i]) && !(char.IsAsciiLetterLower(name[i - 1]) && char.IsAsciiLetterUpper(name[i])))
            {
                i++;
            }

            var word = name.AsSpan(start, i - start);
            foreach (string whole in ShortWords)
            {
                if (word.Equals(whole, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            last = word;
        }

        return last.Equals(LastWord, StringComparison.OrdinalIgnoreCase);
    }
}
