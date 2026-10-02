using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_pull(image, tag?)</c> (2026-10-02): an image fetched from its registry — a public one; no registry credentials
/// are sent. Offered only under <c>Docker writes</c>, and every call waits for the user's yes. ESC stops the wait; the
/// engine may finish the pull on its own.
/// </summary>
public sealed class DockerPullTool : DockerTool
{
    public const string ToolName = "docker_pull";
    public const string ImageArgument = "image";
    public const string TagArgument = "tag";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "image": { "type": "string", "description": "The image, e.g. nginx, ghcr.io/owner/app, or postgres:16 (a tag in the name is used when tag is left out)." },
            "tag": { "type": "string", "description": "The tag (default latest)." }
          },
          "required": ["image"]
        }
        """);

    public DockerPullTool(DockerSession docker, Func<string, CancellationToken, Task<bool?>>? confirm) : base(docker, confirm)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Pulls a public Docker image from its registry. The user is asked to approve each call.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, ImageArgument) is not { } named)
        {
            return DockerText.Missing(ImageArgument);
        }

        var (image, tag) = Split(named, Optional(arguments, TagArgument));
        string reference = Reference(image, tag);
        if (await GateWriteAsync(DockerText.PullAct(reference), cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var reply = await Docker.Client().PullAsync(image, tag, DockerSession.PullTimeout, cancellationToken).ConfigureAwait(false);
        var (status, error) = reply.Ok ? DockerJson.PullResult(reply.Body) : (null, reply.Error);
        string outcome = error is null ? status ?? "done" : error;
        DiagnosticLog.Info(DockerText.Category, DockerText.AuditLine(DockerText.ByModel, "pull " + reference, outcome));
        if (error is not null)
        {
            return error.StartsWith("Error:", StringComparison.Ordinal) ? error : DockerText.PullFailed(reference, error);
        }

        return DockerText.Pulled(reference, status);
    }

    /// <summary>
    /// An image and its tag: <paramref name="tag"/> when given, else the one in the name (<c>postgres:16</c>; a port in a
    /// registry host, <c>localhost:5000/app</c>, is not a tag), else <c>latest</c>; a digest (<c>@sha256:…</c>) keeps the name
    /// whole and no tag. Pure.
    /// </summary>
    public static (string Image, string Tag) Split(string named, string? tag)
    {
        ArgumentNullException.ThrowIfNull(named);
        string image = named.Trim();
        if (image.Contains('@', StringComparison.Ordinal))
        {
            return (image, "");
        }

        int colon = image.LastIndexOf(':');
        if (colon > image.LastIndexOf('/'))
        {
            string inName = image[(colon + 1)..];
            image = image[..colon];
            return (image, string.IsNullOrWhiteSpace(tag) ? inName : tag.Trim());
        }

        return (image, string.IsNullOrWhiteSpace(tag) ? "latest" : tag.Trim());
    }

    /// <summary><c>image:tag</c>, or the digest reference as it is.</summary>
    public static string Reference(string image, string tag) => tag.Length == 0 ? image : image + ":" + tag;
}
