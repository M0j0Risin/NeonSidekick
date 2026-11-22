using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Mcp;

/// <summary>A server's resources and resource templates as listed, up to the tool's cap of each, and whether there were more.</summary>
public sealed record McpResourceList(IReadOnlyList<Resource> Resources, IReadOnlyList<ResourceTemplate> Templates, bool More);

/// <summary>What a resource request came to: a list, a read, or the refusal the call returns instead.</summary>
public sealed record McpResourceAnswer(McpResourceList? Listing, ReadResourceResult? Read, McpCallOutcome? Refusal);

/// <summary>
/// The session's resource side as <see cref="McpReadTool"/> needs it (2026-11-21, feature idea #33): a seam, so the tool's tests need no
/// session. <c>App/McpSession</c> is the real one.
/// </summary>
public interface IMcpResources
{
    /// <summary>The connected servers that declare resources, in the catalog's order.</summary>
    IReadOnlyList<string> ResourceServers { get; }

    Task<McpResourceAnswer> ListResourcesAsync(string server, int max, CancellationToken cancellationToken);

    Task<McpResourceAnswer> ReadResourceAsync(string server, string uri, CancellationToken cancellationToken);

    /// <summary>The context a read's pictures take (2026-11-22, the review's #60, #131): the conversation's counter, whether they're seen.</summary>
    McpRenderContext ReadContext() => McpRenderContext.Plain(McpReadTool.ToolName);
}

/// <summary>
/// <c>mcp_read(server, uri?, start?)</c> (2026-11-21, feature idea #33, trimmed to the tool alone): without <c>uri</c>, a server's
/// resources and resource templates, one line each, the server's words fenced, at most <see cref="MaxListed"/> of each; with it, the
/// resource read. A text part is paged as <c>web_fetch</c> pages a page (<see cref="WorkingDirectory.MaxReadChars"/> characters of scrubbed
/// text, the next <c>start</c> where the window stopped, since an escape is longer than its character); several text parts are joined in
/// order, each led by its own <c>uri · type</c> line, and <c>start</c> pages the join, while a single part has no lead line, so an embedded
/// block's cut note (<see cref="McpTool.Render(CallToolResult, McpRenderContext)"/>) names the same character. When the uri names one of
/// several parts, that part alone is paged and the others are named (2026-11-22, the review's #118), so a cut note's start lands there too.
/// A picture blob is a picture (<see cref="McpPictures"/>), any other blob is named; both on the first page alone (#74), a later page saying
/// they came with it. Offered while a connected server declares resources; a read asks only under that
/// server's <c>"ask": "always"</c>. Its description names the servers, so it's built for each turn's list.
/// </summary>
public sealed class McpReadTool : AIFunction
{
    public const string ToolName = "mcp_read";
    public const string ServerArgument = "server";
    public const string UriArgument = "uri";
    public const string StartArgument = "start";

    /// <summary>The most resources, and the most templates, a list shows.</summary>
    public const int MaxListed = 200;

    private static readonly JsonElement Schema = ToolSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "server": { "type": "string", "description": "The MCP server, as named in this tool's description." },
            "uri": { "type": "string", "description": "The resource to read: a uri from the list, a resource link in a tool's result, or a template with its {…} filled in. Leave it out to list the server's resources." },
            "start": { "type": "integer", "description": "With uri only: where to continue a long resource, in characters, the number the previous result's header or cut note named. Leave it out to start at the top." }
          },
          "required": ["server"],
          "additionalProperties": false
        }
        """);

    private readonly IMcpResources _resources;
    private readonly IReadOnlyList<string> _servers;

    /// <param name="resources">The session's resource side.</param>
    /// <param name="servers">The servers this turn offers it for (the description names them).</param>
    public McpReadTool(IMcpResources resources, IReadOnlyList<string> servers)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Reads an MCP server's resources: files, pages, records it publishes. Without uri, lists them and their templates; with uri, reads one, "
        + "and start continues a long one. Servers: " + string.Join(", ", _servers) + ".";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string server = ToolArguments.ReadName(arguments, ServerArgument).Trim();
        string uri = ToolArguments.ReadString(arguments, UriArgument).Trim();
        if (!ToolArguments.TryReadInt32(arguments, StartArgument, out var start, out var raw))
        {
            return Llm.Tools.ClockText.BadInteger(StartArgument, raw);
        }

        if (start is < 0)
        {
            return McpText.BadStart(start.Value);
        }

        // A start pages one resource's read (2026-11-22, the review's #89): never a list.
        if (start is not null && uri.Length == 0)
        {
            return McpText.StartNeedsUri;
        }

        var offered = _resources.ResourceServers;
        string? name = offered.FirstOrDefault(s => string.Equals(s, server, StringComparison.Ordinal))
            ?? offered.FirstOrDefault(s => string.Equals(s, server, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return McpText.ErrorPrefix + McpText.NoResourcesError(server.Length == 0 ? McpText.NoServerNamed : UntrustedText.Name(server), offered);
        }

        try
        {
            if (uri.Length == 0)
            {
                var listed = await _resources.ListResourcesAsync(name, MaxListed, cancellationToken).ConfigureAwait(false);
                return listed.Refusal is { } refused ? refused.Render() : List(name, listed.Listing!);
            }

            var read = await _resources.ReadResourceAsync(name, uri, cancellationToken).ConfigureAwait(false);
            return read.Refusal is { } no ? no.Render() : Read(read.Read!, uri, start ?? 0, _resources.ReadContext()).ToolResult();
        }
        catch (Exception ex) when (ex is ModelContextProtocol.McpException or HttpRequestException or IOException or TimeoutException)
        {
            // The server's own error (no such resource, a bad template value), or the way there failing (2026-11-22, the review's #61): its
            // words, fenced.
            return UntrustedText.WithNote(McpText.ErrorPrefix + McpText.ReadFailed(name, UntrustedText.Fenced(ex.Message)));
        }
    }

    /// <summary>The list: one line a resource (<c>uri · name · type · size</c>) and a template, the server's words fenced. Pure.</summary>
    public static string List(string server, McpResourceList listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        if (listing.Resources.Count == 0 && listing.Templates.Count == 0)
        {
            return McpText.NoResourcesListed(server);
        }

        var sb = new StringBuilder(McpText.ResourcesHeader(server, listing.Resources.Count, listing.Templates.Count));
        foreach (var resource in listing.Resources)
        {
            sb.Append("\n- ").Append(Line(resource.Uri, resource.Name, resource.Title, resource.MimeType, resource.Size, resource.Description));
        }

        if (listing.Templates.Count > 0)
        {
            sb.Append('\n').Append(McpText.TemplatesHeader);
            foreach (var template in listing.Templates)
            {
                sb.Append("\n- ").Append(Line(template.UriTemplate, template.Name, template.Title, template.MimeType, null, template.Description));
            }
        }

        if (listing.More)
        {
            sb.Append('\n').Append(McpText.MoreResources(MaxListed));
        }

        return UntrustedText.WithNote(sb.ToString());
    }

    private static string Line(string? uri, string? name, string? title, string? mime, long? size, string? description)
    {
        var parts = new List<string> { UntrustedText.QuoteOr(uri, "(no uri)") };
        string? shown = string.IsNullOrWhiteSpace(title) ? name : title;
        if (!string.IsNullOrWhiteSpace(shown))
        {
            parts.Add(UntrustedText.Fenced(UntrustedText.OneLine(shown)));
        }

        if (!string.IsNullOrWhiteSpace(mime))
        {
            parts.Add(UntrustedText.Name(mime));
        }

        if (size is { } bytes)
        {
            parts.Add(bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
        }

        string line = string.Join(" · ", parts);
        return string.IsNullOrWhiteSpace(description) ? line : line + ": " + UntrustedText.Fenced(UntrustedText.Cut(UntrustedText.OneLine(description), 300));
    }

    /// <summary>
    /// A read as the model sees it: the text parts joined and paged from <paramref name="start"/>, the pictures, and a line for any other
    /// blob. Pure but for the picture decode.
    /// </summary>
    public static McpRendered Read(ReadResourceResult result, string uri, int start, McpRenderContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(uri);
        var contents = result.Contents ?? [];
        var texts = contents.OfType<TextResourceContents>().ToList();
        var blobs = contents.OfType<BlobResourceContents>().ToList();
        // The uri names one of several parts (#118): that part is paged, the others named.
        var others = new List<TextResourceContents>();
        if (texts.Count > 1 && texts.Count(t => string.Equals(t.Uri, uri, StringComparison.Ordinal)) == 1)
        {
            others = texts.Where(t => !string.Equals(t.Uri, uri, StringComparison.Ordinal)).ToList();
            texts = texts.Where(t => string.Equals(t.Uri, uri, StringComparison.Ordinal)).ToList();
        }

        var pictures = McpPictures.For(context ?? McpRenderContext.Plain(ToolName));
        var notes = new List<string>();
        if (start == 0)
        {
            foreach (var blob in blobs)
            {
                notes.Add(McpPictures.IsPicture(blob.MimeType)
                    ? pictures.Add(blob.Blob, () => blob.DecodedData)
                    : McpText.ResourceNotRead(blob.Uri ?? "", blob.MimeType, McpPictures.DecodedLength(blob.Blob)));
            }

            notes.AddRange(others.Select(o => McpText.OtherPart(o.Uri ?? "", o.MimeType, (o.Text ?? "").Length)));
        }
        else if (blobs.Count > 0 || others.Count > 0)
        {
            // A later page never attaches the pictures again (2026-11-22, the review's #74, #32): one line says they came with the first.
            int shown = blobs.Count(b => McpPictures.IsPicture(b.MimeType));
            notes.Add(McpText.CameWithFirstPage(shown, blobs.Count - shown + others.Count));
        }

        var sb = new StringBuilder();
        if (texts.Count > 0)
        {
            string joined = texts.Count == 1
                ? texts[0].Text ?? ""
                : string.Join("\n\n", texts.Select(t => (t.Uri ?? "") + " · " + (string.IsNullOrWhiteSpace(t.MimeType) ? "text" : t.MimeType) + "\n" + (t.Text ?? "")));
            int total = joined.Length;
            string mime = texts.Count == 1 ? texts[0].MimeType ?? "" : "";
            if (total == 0)
            {
                // An empty text (2026-11-22, the review's #54): said so, never "characters 1–0 of 0".
                sb.Append(McpText.ReadHeader(uri, mime, 0, 0, 0, null)).Append('\n').Append(McpText.ResourceEmpty);
            }
            else if (start >= total)
            {
                // Past the end: still what else the resource held (#32), never its pictures again.
                if (notes.Count == 0)
                {
                    return new McpRendered(McpText.StartPastEnd(start, total), []);
                }

                sb.Append(McpText.StartPastEnd(start, total));
            }
            else
            {
                var (window, consumed) = UntrustedText.ScrubPrefix(joined, start, WorkingDirectory.MaxReadChars);
                int end = start + consumed;
                sb.Append(McpText.ReadHeader(uri, mime, start + 1, end, total, end < total ? end : null))
                    .Append('\n').Append(UntrustedText.Quote(window));
            }
        }
        else if (contents.Count == 0)
        {
            sb.Append(McpText.NoContent);
        }
        else
        {
            sb.Append(McpText.ReadHeader(uri, null, 0, 0, 0, null));
        }

        foreach (string note in notes)
        {
            sb.Append('\n').Append(note);
        }

        return new McpRendered(UntrustedText.WithNote(sb.ToString()), pictures.Taken);
    }
}
