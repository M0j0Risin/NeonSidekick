using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NeonSidekick.Mcp;

/// <summary>
/// One MCP server's tool as the model sees it (2026-09-20): an <see cref="AIFunction"/> named
/// <c>&lt;server&gt;__&lt;tool&gt;</c> (<see cref="McpToolName"/>), described by the server's own sentence on one
/// line, with the server's own JSON schema, that calls the server on invoke and folds the result into
/// the one string the turn loop expects — the text blocks joined, every other block a pinned
/// placeholder, an <c>isError</c> result prefixed <c>Error:</c> so the trace, the log and the session
/// telemetry see it. The SDK's own <see cref="McpClientTool"/> is never handed to the model: its result
/// is the raw protocol object, which the turn loop would stringify. A transport failure throws and the
/// loop answers <c>Error: &lt;name&gt; failed: …</c> as for any tool. Since 2026-11-19 (feature idea #30) a tool the session built
/// calls through the session's caller by server name, so a drop reconnects and comes back as an <c>Error:</c> result
/// (<see cref="McpCallOutcome"/>); only a tool built without a caller (a test's, the smoke probe's) still throws.
/// </summary>
public sealed class McpTool : AIFunction
{
    private readonly McpClient _client;
    private readonly McpClientTool _inner;
    private readonly string _name;
    private readonly string _description;
    private readonly Func<McpTool, CallToolRequestParams, CancellationToken, ValueTask<McpCallOutcome>>? _caller;

    /// <param name="server">The configured server's name (unsanitised; the prefix is <paramref name="name"/>'s).</param>
    /// <param name="client">The client at build time: the call goes through it only when there is no <paramref name="caller"/>.</param>
    /// <param name="inner">The server's tool as the SDK listed it.</param>
    /// <param name="name">The prefixed, de-duplicated name (<see cref="McpToolName.Unique"/>).</param>
    /// <param name="caller">The session's call (2026-11-19): it finds the server's current client, reconnects a dropped one, and answers.</param>
    public McpTool(string server, McpClient client, McpClientTool inner, string name, Func<McpTool, CallToolRequestParams, CancellationToken, ValueTask<McpCallOutcome>>? caller = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ServerName = server;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _name = name;
        _caller = caller;
        string described = McpText.OneLine(inner.Description);
        _description = described.Length > 0 ? described : McpText.NoDescription;
    }

    /// <summary>The configured server's name.</summary>
    public string ServerName { get; }

    /// <summary>The tool's name on the server (what the call carries).</summary>
    public string ServerToolName => _inner.Name;

    /// <summary>The tool as the server listed it: its name, description and schema (what a changed list is compared by).</summary>
    public Tool ProtocolTool => _inner.ProtocolTool;

    /// <summary>
    /// Whether the server marks the tool <c>readOnlyHint: true</c> (2026-11-19, feature idea #30): a call cut off by a drop is sent again
    /// only then. Since later on 2026-11-19 (feature idea #28, MCP calls ask) a tool that also says <c>destructiveHint: true</c> outright is
    /// not read-only: a server that says both is unsure or wrong, so the call asks and isn't sent again after a drop (Codex CLI lets
    /// destructive win too). Without the explicit <c>true</c> the spec's destructive default applies only to a tool that isn't read-only,
    /// so a plain <c>readOnlyHint: true</c> stays read-only. Through the session, a call that isn't read-only is judged by its
    /// server's <c>ask</c> first (<see cref="McpAskRule"/>).
    /// </summary>
    public bool ReadOnly => _inner.ProtocolTool.Annotations is { ReadOnlyHint: true } hints && hints.DestructiveHint != true;

    /// <summary>
    /// Whether the tool may delete or overwrite things by the spec (2026-11-19, feature idea #28): not <see cref="ReadOnly"/>, and
    /// <c>destructiveHint</c> isn't <c>false</c> (the spec's default is true, and applies only to a tool that isn't read-only).
    /// </summary>
    public bool Destructive => !ReadOnly && _inner.ProtocolTool.Annotations?.DestructiveHint != false;

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var request = new CallToolRequestParams { Name = ServerToolName, Arguments = Arguments(arguments) };
        if (_caller is not null)
        {
            var outcome = await _caller(this, request, cancellationToken).ConfigureAwait(false);
            return outcome.Render();
        }

        var result = await _client.CallToolAsync(request, cancellationToken).ConfigureAwait(false);
        return Render(result);
    }

    /// <summary>
    /// The call's arguments as JSON elements: a <see cref="JsonElement"/> from the wire is cloned as it is;
    /// a CLR value (a test's) is written by hand — string, bool, the integer and floating kinds, null,
    /// anything else its <c>ToString()</c> — and parsed back, so nothing is serialised by runtime type.
    /// </summary>
    public static Dictionary<string, JsonElement> Arguments(IEnumerable<KeyValuePair<string, object?>> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            result[key] = value is JsonElement element ? element.Clone() : ToElement(value);
        }

        return result;
    }

    private static JsonElement ToElement(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (value)
            {
                case null: writer.WriteNullValue(); break;
                case string s: writer.WriteStringValue(s); break;
                case bool b: writer.WriteBooleanValue(b); break;
                case int i: writer.WriteNumberValue(i); break;
                case long l: writer.WriteNumberValue(l); break;
                case double d: writer.WriteNumberValue(d); break;
                case float f: writer.WriteNumberValue(f); break;
                case decimal m: writer.WriteNumberValue(m); break;
                default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
            }
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The result as the model's one string: the text blocks joined by a line break, an image block
    /// <see cref="McpText.ImageDropped"/>, any other kind <see cref="McpText.BlockDropped"/>, nothing at all
    /// <see cref="McpText.NoContent"/>. Since 2026-11-15 (the web-tools plan's §3, feature idea #39) each text block is the server's and
    /// fenced on its own (<see cref="Llm.Tools.UntrustedText.Fenced"/>), the app's notes between them bare, and
    /// <see cref="Llm.Tools.UntrustedText.QuoteNote"/> is the last line. An <c>isError</c> result, MCP's signal for a failed call, always
    /// starts <see cref="McpText.ErrorPrefix"/>, ahead of the first fence (it was left off one whose text began "Error" until then: fenced,
    /// that text would no longer read as failed). A result without <c>isError</c> whose text says "Error" reads as the server's text.
    /// Since 2026-11-16: a blank text block is skipped, so a result of nothing else is <see cref="McpText.NoContent"/> (the web-tools review's
    /// #107), and an <c>isError</c> result's own leading "Error:" is taken off its first block, so neither the model nor the user reads
    /// "Error: Error:" (#145). Pure.
    /// </summary>
    public static string Render(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder();
        bool fenced = false;
        bool error = result.IsError == true;
        foreach (var block in result.Content ?? [])
        {
            string piece;
            if (block is TextContentBlock text)
            {
                string words = text.Text ?? "";
                if (string.IsNullOrWhiteSpace(words))
                {
                    continue;
                }

                if (error && !fenced)
                {
                    words = WithoutErrorPrefix(words);
                }

                piece = Llm.Tools.UntrustedText.Fenced(words);
                fenced = true;
            }
            else
            {
                piece = block is ImageContentBlock ? McpText.ImageDropped : McpText.BlockDropped(block.Type ?? "unknown");
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(piece);
        }

        string rendered = sb.Length > 0 ? sb.ToString() : McpText.NoContent;
        if (fenced)
        {
            rendered += "\n" + Llm.Tools.UntrustedText.QuoteNote;
        }

        return error ? McpText.ErrorPrefix + rendered : rendered;
    }

    // A server's own "Error:" (any case) off the front of its failed call's text, the rest kept; the app's prefix says it once.
    private static string WithoutErrorPrefix(string text)
    {
        string trimmed = text.TrimStart();
        if (!trimmed.StartsWith("error", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        string rest = trimmed[5..].TrimStart();
        if (!rest.StartsWith(':'))
        {
            return text;
        }

        rest = rest[1..].TrimStart();
        return rest.Length > 0 ? rest : text;
    }
}
