using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.Mcp;

/// <summary>
/// One server of <c>mcp.json</c> (2026-09-20), in the ecosystem's own spelling so a block pasted from
/// Claude Desktop or Claude Code loads as it is: a stdio server names a <see cref="Command"/> (with
/// <see cref="Args"/>, <see cref="Env"/> and <see cref="Cwd"/> optional), a streamable-HTTP server a
/// <see cref="Url"/> (with <see cref="Headers"/> optional); <see cref="Type"/> is read and ignored — the
/// two keys decide. Neither or both is a problem the catalog reports (<see cref="McpCatalog"/>), never a
/// throw. Read through <see cref="McpJsonContext"/> alone (camel-case keys, comments and a trailing comma
/// tolerated).
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>The transport word other clients write (<c>stdio</c>, <c>http</c>, <c>sse</c>); kept for the round trip, never consulted.</summary>
    public string? Type { get; set; }

    /// <summary>The stdio server's executable, resolved by the OS as a process start would (<c>docker</c>, <c>npx</c>, a full path).</summary>
    public string? Command { get; set; }

    /// <summary>The stdio server's arguments, one per element.</summary>
    public List<string>? Args { get; set; }

    /// <summary>Variables set for the stdio server on top of the app's own environment.</summary>
    public Dictionary<string, string>? Env { get; set; }

    /// <summary>The stdio server's working directory; the app's own when absent.</summary>
    public string? Cwd { get; set; }

    /// <summary>The streamable-HTTP server's endpoint (<c>http://localhost:8811/mcp</c>).</summary>
    public string? Url { get; set; }

    /// <summary>Headers sent with every request to the HTTP server (<c>Authorization</c> and the like).</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// When the server's calls ask (2026-11-19, feature idea #28): <c>writes</c> (the default, also when absent), <c>always</c> or
    /// <c>never</c>, read without regard to case. Any other word is a <see cref="Problem"/>, never quietly <c>never</c>. Kept as written
    /// for the round trip; <see cref="AskMode"/> is the parsed value.
    /// </summary>
    public string? Ask { get; set; }

    /// <summary>
    /// Whether the server's own instructions reach the model (2026-11-19, feature idea #59): null or true is on, <c>false</c> leaves them
    /// out. A server's instructions are its own text, so this is the opt-out for one you don't trust.
    /// </summary>
    public bool? Instructions { get; set; }

    /// <summary>
    /// <see cref="Ask"/> parsed (<see cref="McpAskRule.TryParse"/>); <see cref="McpAskMode.Writes"/> for a bad word too, which is a
    /// <see cref="Problem"/> and never started. Ignored by the context (2026-11-19): <see cref="Fingerprint"/> and <c>/profile push mcp</c>
    /// serialize this object, and a push would otherwise write an <c>askMode</c> key into the other profile's file.
    /// </summary>
    [JsonIgnore]
    public McpAskMode AskMode => McpAskRule.TryParse(Ask, out var mode) ? mode : McpAskMode.Writes;

    /// <summary>Whether the server's instructions are kept: not <c>"instructions": false</c>.</summary>
    [JsonIgnore]
    public bool InstructionsOn => Instructions != false;

    /// <summary>Whether this is a stdio server: a non-blank <see cref="Command"/>.</summary>
    [JsonIgnore]
    public bool IsStdio => !string.IsNullOrWhiteSpace(Command);

    /// <summary>Whether this is an HTTP server: a non-blank <see cref="Url"/>.</summary>
    [JsonIgnore]
    public bool IsHttp => !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// The reason this entry cannot be started, or null: neither key (<see cref="McpText.NeitherCommandNorUrl"/>),
    /// both (<see cref="McpText.BothCommandAndUrl"/>), a <see cref="Url"/> that is not an absolute http(s)
    /// address (<see cref="McpText.BadUrl"/>), or an <see cref="Ask"/> that isn't one of its three words (<see cref="McpText.BadAsk"/>,
    /// 2026-11-19). Pure.
    /// </summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (IsStdio && IsHttp)
            {
                return McpText.BothCommandAndUrl;
            }

            if (!IsStdio && !IsHttp)
            {
                return McpText.NeitherCommandNorUrl;
            }

            // A url holding a ${NAME} is checked at connect, once expanded (2026-11-20, the mcp-ask review's #72).
            if (IsHttp && !Url!.Contains("${", StringComparison.Ordinal) && (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                return McpText.BadUrl(Url!);
            }

            return McpAskRule.TryParse(Ask, out _) ? null : McpText.BadAsk;
        }
    }

    /// <summary>
    /// The entry as canonical JSON, so a reload can tell a changed server from an untouched one
    /// (the file's own spacing and key order never count). Through the source-generated context.
    /// </summary>
    public string Fingerprint() => JsonSerializer.Serialize(this, McpJsonContext.Default.McpServerConfig);

    /// <summary>
    /// The connection's shape only (2026-11-20, the mcp-ask review's #16): <see cref="Fingerprint"/> without <see cref="Ask"/> and
    /// <see cref="Instructions"/>, which a kept connection takes on in place. A reload restarts a server only when this differs.
    /// </summary>
    public string ConnectionFingerprint() => WithoutAskOrNotes().Fingerprint();

    /// <summary>
    /// A copy with the given fields replaced (2026-11-20, the mcp-ask review's #18), every other field the same; a null argument keeps the
    /// original's. Shallow (<see cref="object.MemberwiseClone"/>): a caller that edits a list or dictionary passes its own new one, so a
    /// config the load cache shares is never changed.
    /// </summary>
    public McpServerConfig With(List<string>? args = null, Dictionary<string, string>? env = null, string? command = null, string? url = null,
        Dictionary<string, string>? headers = null, string? ask = null, bool? instructions = null)
    {
        var copy = (McpServerConfig)MemberwiseClone();
        copy.Args = args ?? Args;
        copy.Env = env ?? Env;
        copy.Command = command ?? Command;
        copy.Url = url ?? Url;
        copy.Headers = headers ?? Headers;
        copy.Ask = ask ?? Ask;
        copy.Instructions = instructions ?? Instructions;
        return copy;
    }

    private McpServerConfig WithoutAskOrNotes()
    {
        var copy = (McpServerConfig)MemberwiseClone();
        copy.Ask = null;
        copy.Instructions = null;
        return copy;
    }

    /// <summary>What the pane says of the transport: <c>stdio: docker mcp gateway run</c> or <c>http: http://…</c>. Pinned through <see cref="McpText.Transport"/>.</summary>
    public string Describe() => McpText.Transport(this);
}
