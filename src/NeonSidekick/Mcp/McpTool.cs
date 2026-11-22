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
    private readonly Func<McpTool, McpRenderContext>? _renderContext;

    /// <param name="server">The configured server's name (unsanitised; the prefix is <paramref name="name"/>'s).</param>
    /// <param name="client">The client at build time: the call goes through it only when there is no <paramref name="caller"/>.</param>
    /// <param name="inner">The server's tool as the SDK listed it.</param>
    /// <param name="name">The prefixed, de-duplicated name (<see cref="McpToolName.Unique"/>).</param>
    /// <param name="caller">The session's call (2026-11-19): it finds the server's current client, reconnects a dropped one, and answers.</param>
    /// <param name="readOffered">
    /// Whether <c>mcp_read</c> is offered for a server this turn (2026-11-21, feature idea #33), asked at each call: a result's resource
    /// links and cut notes point to it only then (<see cref="McpRenderContext.ReadOffered"/>). Null: never.
    /// </param>
    public McpTool(string server, McpClient client, McpClientTool inner, string name, Func<McpTool, CallToolRequestParams, CancellationToken, ValueTask<McpCallOutcome>>? caller = null, Func<McpTool, McpRenderContext>? renderContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ServerName = server;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _name = name;
        _caller = caller;
        _renderContext = renderContext;
        string described = McpText.OneLine(inner.Description);
        _description = described.Length > 0 ? described : McpText.NoDescription;
    }

    /// <summary>The configured server's name.</summary>
    public string ServerName { get; }

    /// <summary>The tool's name on the server (what the call carries).</summary>
    public string ServerToolName => _inner.Name;

    /// <summary>The tool as the server listed it: its name, description and schema (what a changed list is compared by).</summary>
    public Tool ProtocolTool => _inner.ProtocolTool;

    private string? _definitionKey;

    /// <summary>
    /// <see cref="DefinitionKeyOf"/> of this tool's <see cref="ProtocolTool"/>, worked out once (a listing builds new tools, so it never goes
    /// stale).
    /// </summary>
    public string DefinitionKey => _definitionKey ??= DefinitionKeyOf(_inner.ProtocolTool);

    /// <summary>
    /// One key for everything a server says of a tool (2026-11-20, the mcp-ask review's #87 and #116): a SHA-256 in lower hex over its
    /// name, description, input schema as raw JSON and every annotation (<c>readOnlyHint</c>, <c>destructiveHint</c>,
    /// <c>idempotentHint</c>, <c>openWorldHint</c>, <c>title</c>), in that order, joined with <c>\0</c> so text moved from one part to
    /// the next changes it. A hint not set is empty. A changed list is applied when a key differs, and a session allow holds it, so a
    /// redefined tool, a changed hint included, asks again. Pure.
    /// </summary>
    public static string DefinitionKeyOf(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var hints = tool.Annotations;
        string joined = string.Join('\0',
            tool.Name,
            tool.Description ?? "",
            tool.InputSchema.ValueKind == JsonValueKind.Undefined ? "" : tool.InputSchema.GetRawText(),
            Hint(hints?.ReadOnlyHint),
            Hint(hints?.DestructiveHint),
            Hint(hints?.IdempotentHint),
            Hint(hints?.OpenWorldHint),
            hints?.Title ?? "");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(joined)));

        static string Hint(bool? value) => value switch
        {
            true => "true",
            false => "false",
            null => "",
        };
    }

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
            return outcome.Render(RenderContext()).ToolResult();
        }

        var result = await _client.CallToolAsync(request, cancellationToken).ConfigureAwait(false);
        return Render(result, RenderContext()).ToolResult();
    }

    /// <summary>This tool's context for its result: its own name labels the pictures, and <c>mcp_read</c> is named when offered for its server.</summary>
    private McpRenderContext RenderContext() => _renderContext?.Invoke(this) ?? McpRenderContext.Plain(_name);

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

    /// <summary><see cref="Render(CallToolResult, McpRenderContext)"/>'s text alone, under <see cref="McpRenderContext.Plain"/>: the plain shape tests pin.</summary>
    public static string Render(CallToolResult result) => Render(result, McpRenderContext.Plain("mcp")).Text;

    /// <summary>
    /// The result as the model sees it: one string and the pictures that follow it. The text blocks are joined by a line break, nothing at
    /// all is <see cref="McpText.NoContent"/>. Since 2026-11-15 (the web-tools plan's §3, feature idea #39) each text block is the server's
    /// and fenced on its own (<see cref="Llm.Tools.UntrustedText.Fenced"/>), the app's notes between them bare, and
    /// <see cref="Llm.Tools.UntrustedText.QuoteNote"/> is the last line. An <c>isError</c> result, MCP's signal for a failed call, always
    /// starts <see cref="McpText.ErrorPrefix"/>, ahead of the first fence (it was left off one whose text began "Error" until then: fenced,
    /// that text would no longer read as failed). A result without <c>isError</c> whose text says "Error" reads as the server's text.
    /// Since 2026-11-16: a blank text block is skipped, so a result of nothing else is <see cref="McpText.NoContent"/> (the web-tools review's
    /// #107), and an <c>isError</c> result's own leading "Error:" is taken off its first block, so neither the model nor the user reads
    /// "Error: Error:" (#145).
    /// <para>Since 2026-11-21 (feature ideas #31 and #33) the other blocks reach the model, where every image block was "(an image block was
    /// dropped)" until then: an image block, or an embedded resource holding a picture, is decoded into a picture
    /// (<see cref="McpPictures"/>) and leaves <see cref="McpText.PictureAttached"/> in its place, up to <see cref="McpText.PicturesPerCall"/>;
    /// an embedded resource's text is fenced under <see cref="McpText.ResourceHeader"/> and cut at <see cref="Files.WorkingDirectory.MaxReadChars"/>
    /// the way <c>mcp_read</c> pages it, so its cut note's start is where <c>mcp_read</c> goes on; any other embedded blob is
    /// <see cref="McpText.ResourceNotRead"/>; a resource link is <see cref="McpText.ResourceLink"/>. Audio and kinds still unknown stay
    /// <see cref="McpText.BlockDropped"/>. An <c>isError</c> result's pictures still attach. Pure.</para>
    /// </summary>
    public static McpRendered Render(CallToolResult result, McpRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        var sb = new StringBuilder();
        var pictures = McpPictures.For(context);
        bool texted = false;
        bool error = result.IsError == true;
        // One budget for every embedded text (2026-11-22, the review's #56): twenty embedded documents never come to twenty windows.
        int budget = Files.WorkingDirectory.MaxReadChars;
        foreach (var block in result.Content ?? [])
        {
            string piece;
            switch (block)
            {
                case TextContentBlock text:
                    string words = text.Text ?? "";
                    if (string.IsNullOrWhiteSpace(words))
                    {
                        continue;
                    }

                    if (error && !texted)
                    {
                        words = WithoutErrorPrefix(words);
                    }

                    piece = Llm.Tools.UntrustedText.Fenced(words);
                    texted = true;
                    break;
                case ImageContentBlock image:
                    piece = pictures.Add(image.Data, () => image.DecodedData);
                    break;
                case EmbeddedResourceBlock embedded:
                    piece = Embedded(embedded.Resource, pictures, context.ReadOffered, ref budget);
                    // Text came first (2026-11-22, the review's #130): a later text block's own "Error:" stays as the server wrote it.
                    texted |= embedded.Resource is TextResourceContents;
                    break;
                case ResourceLinkBlock link:
                    piece = McpText.ResourceLink(link.Name, link.Uri ?? "", link.Description, context.ReadOffered);
                    break;
                default:
                    piece = McpText.BlockDropped(block.Type ?? "unknown");
                    break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(piece);
        }

        string rendered = Llm.Tools.UntrustedText.WithNote(sb.Length > 0 ? sb.ToString() : McpText.NoContent);
        return new McpRendered(error ? McpText.ErrorPrefix + rendered : rendered, pictures.Taken);
    }

    /// <summary>
    /// An embedded resource as its piece of the result: its text fenced and cut as <c>mcp_read</c> pages it, out of the result's one
    /// <paramref name="budget"/> (a text past it is named, #56), a picture, or named only.
    /// </summary>
    private static string Embedded(ResourceContents? resource, McpPictures pictures, bool readOffered, ref int budget)
    {
        switch (resource)
        {
            case TextResourceContents text when budget <= 0:
                return McpText.ResourceLeftOut(text.Uri ?? "", (text.Text ?? "").Length, readOffered);
            case TextResourceContents text:
                string body = text.Text ?? "";
                var (shown, consumed) = Llm.Tools.UntrustedText.ScrubPrefix(body, 0, budget);
                budget -= consumed;
                string piece = McpText.ResourceHeader(text.Uri ?? "", text.MimeType) + "\n" + Llm.Tools.UntrustedText.Quote(shown);
                return consumed < body.Length ? piece + "\n" + McpText.ResourceCut(consumed, body.Length, readOffered) : piece;
            case BlobResourceContents blob when McpPictures.IsPicture(blob.MimeType):
                return pictures.Add(blob.Blob, () => blob.DecodedData);
            case BlobResourceContents blob:
                return McpText.ResourceNotRead(blob.Uri ?? "", blob.MimeType, McpPictures.DecodedLength(blob.Blob));
            default:
                return McpText.BlockDropped("resource");
        }
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
