using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NeonSidekick.Network;

/// <summary>One entry of a host list: a name, a glob of whole labels, or a CIDR block.</summary>
public sealed record HostRule(string Text, HostRuleKind Kind, string? Name, IPAddress? Network, int Prefix)
{
    /// <summary>
    /// Whether this entry matches the normalized <paramref name="name"/> (<see cref="HostRules.NormalizeName"/>); with <paramref name="apex"/> a
    /// glob matches its own domain too (the denied list's reading, 2026-10-23, the admin review's H8: a denied <c>*.corp.example</c> let
    /// <c>corp.example</c> itself, the DCs' name, through).
    /// </summary>
    public bool MatchesName(string name, bool apex = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Kind switch
        {
            HostRuleKind.Name => string.Equals(Name, name, StringComparison.Ordinal),
            HostRuleKind.Glob => HostRules.GlobMatches(Name!, name) || (apex && string.Equals(Name, name, StringComparison.Ordinal)),
            HostRuleKind.Cidr => IPAddress.TryParse(name, out var literal) && MatchesAddress(literal),
            _ => false,
        };
    }

    /// <summary>Whether this entry matches <paramref name="address"/>: a CIDR by its block, a name by its own literal spelling.</summary>
    public bool MatchesAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = HostRules.Canonical(address);
        return Kind switch
        {
            HostRuleKind.Cidr => HostRules.InBlock(address, Network!, Prefix),
            HostRuleKind.Name => IPAddress.TryParse(Name, out var literal) && HostRules.Canonical(literal).Equals(address),
            _ => false,
        };
    }
}

public enum HostRuleKind
{
    Name,
    Glob,
    Cidr,
}

/// <summary>
/// The settings <c>Network tools allowed hosts</c> and <c>Network tools denied hosts</c> read (2026-10-15, the admin-tools plan, the user's
/// call): each entry a name (<c>fs01.corp.example</c>), a glob of whole labels (<c>*.corp.example</c> matches <c>a.corp.example</c>, not
/// <c>corp.example</c>), or a CIDR (<c>10.20.0.0/16</c>, <c>fd00::/8</c>). Names are compared normalized — lowercase, a trailing dot dropped,
/// an IDN as punycode — and an IPv4-mapped IPv6 address as its IPv4 (<c>LanPolicy</c>'s rule). <see cref="Parse"/> refuses a bad entry with its
/// sentence (the menu's typed-list page shows it on entry); a saved list with one is read without it and warns. Pure.
/// </summary>
public static class HostRules
{
    private static readonly IdnMapping Idn = new();

    /// <summary>One entry read, or the sentence saying why it cannot be one.</summary>
    public static (HostRule? Rule, string? Error) Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string entry = text.Trim();
        if (entry.Length == 0)
        {
            return (null, NetworkText.BlankHostEntry);
        }

        int slash = entry.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            if (!TryParseAddress(entry[..slash], out var network)
                || !int.TryParse(entry[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix))
            {
                return (null, NetworkText.BadCidr(entry));
            }

            network = Canonical(network);
            int max = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefix < 0 || prefix > max)
            {
                return (null, NetworkText.BadCidr(entry));
            }

            return (new HostRule(entry, HostRuleKind.Cidr, null, Masked(network, prefix), prefix), null);
        }

        if (TryParseAddress(entry.Trim('[', ']'), out var single))
        {
            single = Canonical(single);
            return (new HostRule(entry, HostRuleKind.Name, single.ToString(), null, 0), null);
        }

        if (entry.StartsWith("*.", StringComparison.Ordinal))
        {
            // A suffix whose last label is all digits is no domain (2026-10-23, the admin review's U11: an allowed *.1 matched every x.x.x.1's text).
            string rest = NormalizeName(entry[2..]);
            if (rest.Length == 0 || rest.Contains('*', StringComparison.Ordinal) || !IsHostName(rest) || rest.Split('.')[^1].All(char.IsAsciiDigit))
            {
                return (null, NetworkText.BadGlob(entry));
            }

            return (new HostRule(entry, HostRuleKind.Glob, rest, null, 0), null);
        }

        if (entry.Contains('*', StringComparison.Ordinal))
        {
            return (null, NetworkText.BadGlob(entry));
        }

        string name = NormalizeName(entry);
        return name.Length == 0 || !IsHostName(name) || name.Split('.').All(label => label.All(char.IsAsciiDigit)) ? (null, NetworkText.BadHostEntry(entry)) : (new HostRule(entry, HostRuleKind.Name, name, null, 0), null);
    }

    /// <summary>
    /// An address written out in full: IPv6 as <see cref="IPAddress.TryParse(string?, out IPAddress?)"/> reads it, IPv4 only as four dotted
    /// decimals (2026-10-15, the admin-tools plan). The parser's own legacy forms (<c>10.0.0</c> for 10.0.0.0, <c>10.1</c> for 10.0.0.1, a
    /// bare number, hex and octal parts) would turn a mistyped entry into a different block without a word.
    /// </summary>
    public static bool TryParseAddress(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (text.IndexOfAny(['[', ']']) >= 0)
        {
            // Brackets are the caller's to take off (2026-10-23, the admin review's U13: [fd00::]:80 parsed with its port dropped).
            return false;
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            return IPAddress.TryParse(text, out address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        string[] parts = text.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0')))
        {
            return false;
        }

        return IPAddress.TryParse(text, out address);
    }

    /// <summary>A saved list's usable entries, in order; a bad one is left out (the caller warns once with <see cref="Problems"/>).</summary>
    public static IReadOnlyList<HostRule> ReadList(IReadOnlyList<string>? saved)
    {
        if (saved is null || saved.Count == 0)
        {
            return [];
        }

        var rules = new List<HostRule>(saved.Count);
        foreach (string text in saved)
        {
            if (Parse(text).Rule is { } rule)
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    /// <summary>The entries of a saved list that cannot be read, each with its sentence.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<string>? saved) =>
        saved is null ? [] : saved.Select(t => Parse(t).Error).OfType<string>().ToList();

    /// <summary>The first entry of <paramref name="rules"/> that matches the name or the address; null for none. <paramref name="apex"/>: H8's denied reading.</summary>
    public static HostRule? Match(IReadOnlyList<HostRule> rules, string? name, IPAddress? address, bool apex = false)
    {
        ArgumentNullException.ThrowIfNull(rules);
        string? normalized = name is null ? null : NormalizeName(name);
        foreach (var rule in rules)
        {
            if (normalized is { Length: > 0 } && rule.MatchesName(normalized, apex))
            {
                return rule;
            }

            if (address is not null && rule.MatchesAddress(address))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>A host name as the lists compare it: trimmed, brackets and a trailing dot dropped, lowercase, an IDN as punycode; "" when it cannot be.</summary>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim().Trim('[', ']').TrimEnd('.');
        if (trimmed.Length == 0)
        {
            return "";
        }

        // An address only as written out in full; a legacy spelling (1234, 10.1) is no name either (2026-10-23, the admin review's P4: net_dns
        // name=1234 was judged and asked as 0.0.4.210).
        if (TryParseAddress(trimmed, out var literal))
        {
            return Canonical(literal).ToString();
        }

        if (IPAddress.TryParse(trimmed, out _))
        {
            return "";
        }

        try
        {
            return Idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    /// <summary>
    /// An IPv4-mapped IPv6 address as its IPv4; an IPv6 one without its zone (2026-10-23, the admin review's B7: <c>fe80::1%12</c> got past a
    /// denied <c>fe80::1</c>, since IPAddress.Equals compares the zone); any other as it is.
    /// </summary>
    public static IPAddress Canonical(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4()
            : address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0 ? new IPAddress(address.GetAddressBytes())
            : address;
    }

    /// <summary>Whether <paramref name="name"/> ends with <c>.</c> + <paramref name="suffix"/> with at least one whole label before it.</summary>
    public static bool GlobMatches(string suffix, string name) =>
        name.Length > suffix.Length + 1 && name.EndsWith("." + suffix, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="address"/> lies in <paramref name="network"/>/<paramref name="prefix"/> (both canonical, same family).</summary>
    public static bool InBlock(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        return Masked(address, prefix).Equals(network);
    }

    private static IPAddress Masked(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (int i = 0; i < bytes.Length; i++)
        {
            int bits = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }

        return new IPAddress(bytes);
    }

    /// <summary>Letters, digits, hyphens and dots, labels 1–63 long, 253 in all: what a name in a list may be.</summary>
    public static bool IsHostName(string name)
    {
        if (name.Length == 0 || name.Length > 253)
        {
            return false;
        }

        foreach (string label in name.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (char c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
