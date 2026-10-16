using System.Globalization;
using NeonSidekick.UI;

namespace NeonSidekick.Network;

/// <summary>What <c>/net</c> came to: the answer's lines, whether it failed, and the dim note when the model's call would have been refused.</summary>
public sealed record NetworkCommandResult(IReadOnlyList<string> Lines, bool Failed, string? Note = null);

/// <summary>
/// <c>/net ping|trace|port|dns|tls|certs …</c> (2026-10-15, the admin-tools plan): the user's own hand over the same checks the tools make
/// (<see cref="NetworkChecks"/>) — never judged by <c>Network tools reach</c>, the host lists or the turn's budget, and working whether the
/// tools are on or not; when the model's call would have been refused, a dim note says so. Its forms:
/// <c>ping &lt;host&gt; [count]</c>, <c>trace &lt;host&gt; [names]</c>, <c>port &lt;host&gt; &lt;ports&gt;</c>, <c>dns &lt;name&gt; [type] [server]</c>,
/// <c>tls &lt;host&gt; [port] [starttls]</c>, <c>certs [store] [user|machine] [filter]</c>. The screen shows the lines on the info pane, headless
/// prints them, the Web UI as a list card.
/// </summary>
public static class NetworkCommand
{
    /// <summary>The command's word.</summary>
    public const string Word = "/net";

    /// <summary>Runs one <c>/net</c> line.</summary>
    public static async Task<NetworkCommandResult> RunAsync(NetworkAccess access, string args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(args);
        var words = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return Usage();
        }

        string verb = words[0].ToLowerInvariant();
        var rest = words.Skip(1).ToArray();
        NetworkAnswer answer;
        switch (verb)
        {
            case "ping" when rest.Length is 1 or 2:
                int count = NetworkChecks.DefaultCount;
                if (rest.Length == 2 && (!int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > NetworkChecks.MaxCount))
                {
                    return Failed(NetworkText.OutOfRange("count", rest[1], 1, NetworkChecks.MaxCount));
                }

                answer = await NetworkChecks.PingAsync(access, null, rest[0], count, cancellationToken).ConfigureAwait(false);
                break;
            case "trace" when rest.Length is 1 or 2:
                bool names = rest.Length == 2 && string.Equals(rest[1], "names", StringComparison.OrdinalIgnoreCase);
                if (rest.Length == 2 && !names)
                {
                    return Usage();
                }

                answer = await NetworkChecks.TraceAsync(access, null, rest[0], NetworkChecks.DefaultHops, names, cancellationToken).ConfigureAwait(false);
                break;
            case "port" or "ports" when rest.Length >= 2:
                var (ports, portsError) = Llm.Tools.NetworkTool.Ports(string.Join(',', rest.Skip(1)));
                if (portsError is not null)
                {
                    return Failed(portsError);
                }

                answer = await NetworkChecks.PortsAsync(access, null, rest[0], ports, cancellationToken).ConfigureAwait(false);
                break;
            case "dns" when rest.Length is >= 1 and <= 3:
                string typeWord = rest.Length >= 2 ? rest[1] : "A";
                if (!DnsWire.Types.TryGetValue(typeWord, out ushort type))
                {
                    return Failed(NetworkText.BadType(typeWord));
                }

                answer = await NetworkChecks.DnsAsync(access, null, rest[0], type, rest.Length == 3 ? rest[2] : null, cancellationToken).ConfigureAwait(false);
                break;
            case "tls" when rest.Length is >= 1 and <= 3:
                int port = 443;
                if (rest.Length >= 2 && (!int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535))
                {
                    return Failed(NetworkText.OutOfRange("port", rest[1], 1, 65535));
                }

                StartTls? preamble = null;
                if (rest.Length == 3)
                {
                    if (!TlsPreamble.Words.TryGetValue(rest[2], out var kind))
                    {
                        return Failed(NetworkText.BadStartTls(rest[2]));
                    }

                    preamble = kind;
                }

                answer = await NetworkChecks.TlsAsync(access, null, rest[0], port, null, preamble, cancellationToken).ConfigureAwait(false);
                break;
            case "certs":
                string store = "My";
                var location = System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser;
                var filter = new List<string>();
                foreach (string word in rest)
                {
                    if (CertificateStores.StoreName(word) is { } named && filter.Count == 0)
                    {
                        store = named;
                    }
                    else if (CertificateStores.Location(word) is { } where && filter.Count == 0)
                    {
                        location = where;
                    }
                    else
                    {
                        filter.Add(word);
                    }
                }

                answer = NetworkChecks.Certificates(access, store, location, filter.Count == 0 ? null : string.Join(' ', filter), null, NetworkChecks.MaxCertLimit);
                break;
            default:
                return Usage();
        }

        bool failed = answer.Text.StartsWith("Error:", StringComparison.Ordinal);
        var lines = answer.Text.Split('\n').Select(l => failed ? l.Replace("Error: ", "", StringComparison.Ordinal) : l).ToList();
        return new NetworkCommandResult(lines, failed, answer.ModelNote);
    }

    /// <summary>Completion after <c>/net</c>: the verbs; after <c>dns &lt;name&gt;</c> the types; after <c>certs</c> the stores and locations.</summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        int space = argText.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            return MentionCompleter.Matches(NetworkText.Verbs.Select(v => new CompletionItem(v, NetworkText.VerbNote(v))).ToList(), argText);
        }

        string verb = argText[..space].ToLowerInvariant();
        int last = argText.LastIndexOf(' ');
        string head = argText[..(last + 1)];
        int words = argText[..last].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        IEnumerable<string> pool = verb switch
        {
            "dns" when words == 2 => DnsWire.Types.Keys,
            "tls" when words == 3 => TlsPreamble.Words.Keys,
            "trace" when words == 2 => ["names"],
            "certs" => CertificateStores.StoreWords.Concat(CertificateStores.LocationWords),
            _ => [],
        };
        return MentionCompleter.Matches(pool.Select(w => new CompletionItem(head + w, "")).ToList(), argText);
    }

    private static NetworkCommandResult Usage() => new([NetworkText.Usage], Failed: true);

    private static NetworkCommandResult Failed(string error) => new([error.Replace("Error: ", "", StringComparison.Ordinal)], Failed: true);

    /// <summary>The note as the transcript dims it.</summary>
    public static string DimNote(string note) => Theme.DimMarkup(note);
}
