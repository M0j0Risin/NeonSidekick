using System.Text;
using System.Text.Unicode;

namespace NeonSidekick.Sql;

/// <summary>
/// What the app writes into a stored credential's comment (Windows' <c>CREDENTIALW.Comment</c>, a Mac keychain item's comment attribute) and
/// reads back (2026-10-24, the database review's K3 and H5, decision DB9): <c>NeonSidekick; utf16; host=db1:5432</c> — how the secret is
/// encoded, and the host it was saved for. K3: the encoding was guessed from the blob's length (even meant UTF-16), so an even-length UTF-8
/// secret another tool stored (<c>docker-credential-wincred</c>) read as UTF-16 garbage; the app's own now say, and a credential with no mark
/// is read by its bytes (<see cref="Decode"/>). DB9: a password is sent only to the host it was saved for; one with no recorded host
/// (<c>cmdkey</c>, <c>security</c>, or saved before then) is accepted as before. Pure.
/// </summary>
public readonly record struct CredentialMark(bool? Wide, string? Host)
{
    /// <summary>What every mark the app writes starts with.</summary>
    public const string Lead = "NeonSidekick";

    /// <summary>The comment for a secret stored <paramref name="wide"/> (UTF-16) or UTF-8, for <paramref name="host"/> when one is given.</summary>
    public static string Format(bool wide, string? host) =>
        Lead + "; " + (wide ? "utf16" : "utf8") + (string.IsNullOrWhiteSpace(host) ? "" : "; host=" + host.Trim());

    /// <summary>The mark in <paramref name="comment"/>; neither part known for a comment the app did not write.</summary>
    public static CredentialMark Parse(string? comment)
    {
        if (comment is null || !comment.StartsWith(Lead + ";", StringComparison.Ordinal))
        {
            return default;
        }

        bool? wide = null;
        string? host = null;
        foreach (string part in comment[(Lead.Length + 1)..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("utf16", StringComparison.Ordinal))
            {
                wide = true;
            }
            else if (part.Equals("utf8", StringComparison.Ordinal))
            {
                wide = false;
            }
            else if (part.StartsWith("host=", StringComparison.Ordinal) && part.Length > 5)
            {
                host = part[5..];
            }
        }

        return new CredentialMark(wide, host);
    }

    /// <summary>
    /// A secret with no mark, read by its bytes: valid UTF-8 with no zero byte is UTF-8 (a UTF-16 secret of ASCII letters is every other byte
    /// zero); else an even length is UTF-16 (<c>cmdkey</c>'s, and the app's own before the mark); else UTF-8.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> blob)
    {
        if (Utf8.IsValid(blob) && blob.IndexOf((byte)0) < 0)
        {
            return Encoding.UTF8.GetString(blob);
        }

        return blob.Length % 2 == 0 ? Encoding.Unicode.GetString(blob) : Encoding.UTF8.GetString(blob);
    }

    /// <summary>Whether <paramref name="recorded"/> and <paramref name="host"/> name one host (spaces and case ignored).</summary>
    public static bool SameHost(string recorded, string? host) =>
        string.Equals(recorded.Trim(), host?.Trim(), StringComparison.OrdinalIgnoreCase);
}
