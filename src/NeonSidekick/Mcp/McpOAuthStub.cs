using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeonSidekick.Mcp;

/// <summary>
/// A hosted MCP server that wants a sign-in, played in-process (2026-11-21, feature idea #32), for the tests and the <c>--smoke</c> check:
/// an <see cref="HttpMessageHandler"/> the transport's own client sends through, so the SDK's whole OAuth path runs with no network. It
/// answers the first unsigned MCP request 401 with a <c>WWW-Authenticate: Bearer resource_metadata=…</c> challenge, serves the protected
/// resource's and the authorization server's metadata (PKCE S256, an <c>issuer</c> equal to the server the resource names), registers a
/// client, issues a code at <c>/authorize</c> (a 302 to the redirect with <c>code</c>, <c>state</c> and <c>iss</c>), checks the code's
/// PKCE at <c>/token</c> and refreshes; then speaks streamable-HTTP JSON-RPC (<c>initialize</c>, <c>tools/list</c>, an <c>echo</c> call)
/// to a valid bearer token. <see cref="BrowseAsync"/> is the browser: it asks <c>/authorize</c> here and follows the redirect into the
/// app's real loopback listener. JSON is built by hand (no reflection). Counters say what was asked.
/// </summary>
public sealed class McpOAuthStub : HttpMessageHandler
{
    /// <summary>The MCP endpoint, the <c>url</c> an entry names.</summary>
    public const string Endpoint = "https://mcp.stub.test/mcp";

    /// <summary>The authorization server, its <c>issuer</c>.</summary>
    public const string Issuer = "https://auth.stub.test";

    public const string ResourceMetadata = "https://mcp.stub.test/.well-known/oauth-protected-resource";
    public const string ToolName = "echo";

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Challenge, string ClientId, string Redirect)> _codes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _access = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refresh = new(StringComparer.Ordinal);
    private int _serial;

    /// <summary>Leaves S256 out of the metadata: the SDK refuses such a server.</summary>
    public bool OmitS256 { get; set; }

    /// <summary>Makes <c>/authorize</c> answer with <c>error=access_denied</c>.</summary>
    public bool DenyAuthorization { get; set; }

    /// <summary>Makes every refresh fail (<c>invalid_grant</c>).</summary>
    public bool RejectRefresh { get; set; }

    /// <summary>An authorization address of another scheme, to prove the app refuses to open it (<c>file:</c>, a custom one).</summary>
    public string? AuthorizationEndpointOverride { get; set; }

    public int Registrations { get; private set; }
    public int CodeExchanges { get; private set; }
    public int Refreshes { get; private set; }
    public int Unauthorized { get; private set; }
    public int Calls { get; private set; }

    /// <summary>Every code, access token and refresh token issued: the log must hold none of them.</summary>
    public IReadOnlyList<string> Secrets { get { lock (_gate) { return [.. _issued]; } } }

    private readonly List<string> _issued = [];

    /// <summary>Makes every access token issued so far invalid, so the next request is a 401 and the SDK refreshes.</summary>
    public void ExpireAccessTokens()
    {
        lock (_gate)
        {
            _access.Clear();
        }
    }

    /// <summary>
    /// The browser's half: GETs <paramref name="authorizationUrl"/> from this stub and follows its 302 to the redirect address with a real
    /// HTTP client, so the app's loopback listener is what answers. Returns the listener's status.
    /// </summary>
    public async Task<HttpStatusCode> BrowseAsync(string authorizationUrl, CancellationToken cancellationToken)
    {
        using var stub = new HttpClient(this, disposeHandler: false);
        using var answer = await stub.GetAsync(new Uri(authorizationUrl), cancellationToken).ConfigureAwait(false);
        if (answer.StatusCode != HttpStatusCode.Found || answer.Headers.Location is not { } next)
        {
            return answer.StatusCode;
        }

        using var real = new HttpClient();
        using var callback = await real.GetAsync(next, cancellationToken).ConfigureAwait(false);
        return callback.StatusCode;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri!;
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string here = uri.GetLeftPart(UriPartial.Path);
        if (here == ResourceMetadata || here == ResourceMetadata + "/mcp")
        {
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["resource"] = Endpoint,
                ["authorization_servers"] = new JsonArray(Issuer),
                ["bearer_methods_supported"] = new JsonArray("header"),
            });
        }

        if (uri.Host == "auth.stub.test")
        {
            return Authorization(request, uri, body);
        }

        if (here == Endpoint)
        {
            return Mcp(request, body);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
    }

    private HttpResponseMessage Authorization(HttpRequestMessage request, Uri uri, string body)
    {
        string path = uri.AbsolutePath;
        if (path.StartsWith("/.well-known/oauth-authorization-server", StringComparison.Ordinal) || path.StartsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
        {
            var metadata = new JsonObject
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = AuthorizationEndpointOverride ?? Issuer + "/authorize",
                ["token_endpoint"] = Issuer + "/token",
                ["registration_endpoint"] = Issuer + "/register",
                ["response_types_supported"] = new JsonArray("code"),
                ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
            };
            if (!OmitS256)
            {
                metadata["code_challenge_methods_supported"] = new JsonArray("S256");
            }

            return Json(HttpStatusCode.OK, metadata);
        }

        if (path == "/register" && request.Method == HttpMethod.Post)
        {
            string clientId;
            lock (_gate)
            {
                Registrations++;
                clientId = "stub-client-" + (++_serial).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var asked = JsonNode.Parse(body) as JsonObject;
            return Json(HttpStatusCode.Created, new JsonObject
            {
                ["client_id"] = clientId,
                ["redirect_uris"] = asked?["redirect_uris"]?.DeepClone() ?? new JsonArray(),
                ["token_endpoint_auth_method"] = "none",
                ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
                ["response_types"] = new JsonArray("code"),
            });
        }

        if (path == "/authorize" && request.Method == HttpMethod.Get)
        {
            var query = McpSignIn.Query(uri.Query);
            string redirect = query.GetValueOrDefault("redirect_uri") ?? "";
            string state = query.GetValueOrDefault("state") ?? "";
            string target;
            if (DenyAuthorization)
            {
                target = redirect + "?error=access_denied&state=" + Uri.EscapeDataString(state);
            }
            else
            {
                string code = Issue("stub-code-");
                lock (_gate)
                {
                    _codes[code] = (query.GetValueOrDefault("code_challenge") ?? "", query.GetValueOrDefault("client_id") ?? "", redirect);
                }

                target = redirect + "?code=" + Uri.EscapeDataString(code) + "&state=" + Uri.EscapeDataString(state) + "&iss=" + Uri.EscapeDataString(Issuer);
            }

            var found = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
            found.Headers.Location = new Uri(target);
            return found;
        }

        if (path == "/token" && request.Method == HttpMethod.Post)
        {
            var form = McpSignIn.Query(body);
            string grant = form.GetValueOrDefault("grant_type") ?? "";
            if (grant == "authorization_code")
            {
                (string Challenge, string ClientId, string Redirect) asked;
                lock (_gate)
                {
                    if (!_codes.Remove(form.GetValueOrDefault("code") ?? "", out asked))
                    {
                        return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" });
                    }
                }

                string verifier = form.GetValueOrDefault("code_verifier") ?? "";
                string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (challenge != asked.Challenge)
                {
                    return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "PKCE" });
                }

                lock (_gate)
                {
                    CodeExchanges++;
                }

                return Tokens();
            }

            if (grant == "refresh_token")
            {
                lock (_gate)
                {
                    Refreshes++;
                    if (RejectRefresh || !_refresh.Remove(form.GetValueOrDefault("refresh_token") ?? ""))
                    {
                        return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" });
                    }
                }

                return Tokens();
            }

            return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "unsupported_grant_type" });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
    }

    private HttpResponseMessage Tokens()
    {
        string access = Issue("stub-access-");
        string refresh = Issue("stub-refresh-");
        lock (_gate)
        {
            _access.Add(access);
            _refresh.Add(refresh);
        }

        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = 3600,
            ["refresh_token"] = refresh,
        });
    }

    private string Issue(string prefix)
    {
        lock (_gate)
        {
            string value = prefix + (++_serial).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            _issued.Add(value);
            return value;
        }
    }

    private HttpResponseMessage Mcp(HttpRequestMessage request, string body)
    {
        string? token = request.Headers.Authorization is { Scheme: "Bearer" } bearer ? bearer.Parameter : null;
        bool valid;
        lock (_gate)
        {
            valid = token is not null && _access.Contains(token);
            if (!valid)
            {
                Unauthorized++;
            }
        }

        if (!valid)
        {
            var refused = new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request, Content = new StringContent("") };
            refused.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", "resource_metadata=\"" + ResourceMetadata + "\""));
            return refused;
        }

        if (request.Method == HttpMethod.Delete)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }

        if (request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { RequestMessage = request };
        }

        using var message = JsonDocument.Parse(body);
        var root = message.RootElement;
        if (!root.TryGetProperty("id", out var id))
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted) { RequestMessage = request };   // a notification
        }

        string method = root.GetProperty("method").GetString() ?? "";
        JsonNode? result = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = root.GetProperty("params").TryGetProperty("protocolVersion", out var version) ? version.GetString() : "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "oauth-stub", ["version"] = "1.0" },
            },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = ToolName,
                    ["description"] = "Echoes its text.",
                    ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } },
                    ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false },
                }),
            },
            "tools/call" => Echo(root),
            "ping" => new JsonObject(),
            _ => null,
        };
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        if (result is null)
        {
            reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" };
        }
        else
        {
            reply["result"] = result;
        }

        var answer = Json(HttpStatusCode.OK, reply);
        if (method == "initialize")
        {
            answer.Headers.Add("Mcp-Session-Id", "stub-session");
        }

        answer.RequestMessage = request;
        return answer;
    }

    private JsonObject Echo(JsonElement root)
    {
        lock (_gate)
        {
            Calls++;
        }

        string text = root.GetProperty("params").TryGetProperty("arguments", out var arguments) && arguments.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "echo: " + text }) };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
