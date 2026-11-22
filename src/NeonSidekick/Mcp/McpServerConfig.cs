using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.Mcp;

/// <summary>
/// A <c>url</c> server's <c>"oauth"</c> object (2026-11-21, feature idea #32): needed only for a server without dynamic registration, which
/// wants an app registered with it beforehand. Without it, a sign-in is still offered whenever such a server answers 401. <see cref="ClientSecret"/>
/// is sealed in place like a token (<see cref="McpSecrets"/>); <c>${NAME}</c> expands in it and in <see cref="ClientId"/>.
/// </summary>
public sealed class McpOAuthConfig
{
    /// <summary>A pre-registered app's client id.</summary>
    public string? ClientId { get; set; }

    /// <summary>A pre-registered app's client secret, if it has one.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The scopes to ask for; the server's own choice when absent.</summary>
    public List<string>? Scopes { get; set; }

    /// <summary>The loopback port the sign-in's redirect comes back to (<c>http://127.0.0.1:&lt;port&gt;/callback</c>); <see cref="McpOAuth.DefaultRedirectPort"/> when absent.</summary>
    public int? RedirectPort { get; set; }
}

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

    /// <summary>A <c>url</c> server's sign-in settings (2026-11-21, feature idea #32); null for none, the usual case.</summary>
    [JsonPropertyName("oauth")]   // the camel-case policy would read "oAuth"
    public McpOAuthConfig? OAuth { get; set; }

    /// <summary>
    /// Whether <see cref="Headers"/> carries an <c>Authorization</c> header, in any case (2026-11-21): the dictionary from JSON compares
    /// keys exactly, a header name doesn't. A server with its own Authorization gets no sign-in.
    /// </summary>
    [JsonIgnore]
    public bool HasAuthorizationHeader => Headers?.Keys.Any(k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Whether a sign-in can serve this server: a <c>url</c> server with no key of its own (2026-11-22, the review's #111): no Authorization
    /// header, no other header named for a secret (<c>X-Api-Key</c>, <c>X-Key</c>), and no key in its address (<c>?api_key=</c>). Such a
    /// server's 401 reads as plain failed, its key the problem, never a sign-in.
    /// </summary>
    [JsonIgnore]
    public bool SignInPossible => IsHttp && !HasAuthorizationHeader && !HasKeyHeader && !HasKeyInUrl;

    /// <summary>A header other than Authorization whose name or value says it carries a key (<see cref="McpSecrets.IsSecret"/>).</summary>
    [JsonIgnore]
    public bool HasKeyHeader => Headers?.Any(h => McpSecrets.IsSecret(h.Key, h.Value)) == true;

    /// <summary>An address that carries a key: a password or a key in its query (<see cref="Llm.Tools.ToolRedaction.Text"/> would redact it).</summary>
    [JsonIgnore]
    public bool HasKeyInUrl => Url is { Length: > 0 } url && Llm.Tools.ToolRedaction.Text(url.Trim()) != url.Trim();

    /// <summary>The loopback port the sign-in listens on: the entry's <c>redirectPort</c>, else <see cref="McpOAuth.DefaultRedirectPort"/>.</summary>
    [JsonIgnore]
    public int RedirectPort => OAuth?.RedirectPort ?? McpOAuth.DefaultRedirectPort;

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

            if (OAuth is { } oauth)
            {
                // The sign-in's settings (2026-11-21): a stdio server has no sign-in; the port must be one a listener can bind.
                if (!IsHttp)
                {
                    return McpText.OAuthNeedsUrl;
                }

                if (oauth.RedirectPort is { } port && port is < 1 or > 65535)
                {
                    return McpText.BadRedirectPort;
                }

                if (!string.IsNullOrWhiteSpace(oauth.ClientSecret) && string.IsNullOrWhiteSpace(oauth.ClientId))
                {
                    return McpText.OAuthSecretNeedsId;   // #79
                }
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
        Dictionary<string, string>? headers = null, string? ask = null, bool? instructions = null, McpOAuthConfig? oauth = null)
    {
        var copy = (McpServerConfig)MemberwiseClone();
        copy.OAuth = oauth ?? OAuth;
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
