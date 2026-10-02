using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NeonSidekick.Docker;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>docker_logs(container, tail?, since?, grep?, stream?, timestamps?)</c> (2026-10-02): a container's last log lines —
/// the last <c>tail</c> (100, up to <see cref="MaxTail"/>), from <c>since</c> on (an age such as <c>10m</c> or a moment),
/// only stdout or stderr. With <c>grep</c> the last <see cref="GrepWindow"/> lines are read and the last <c>tail</c> that
/// hold the text (case ignored) kept. The answer is held to <see cref="MaxChars"/>, the oldest lines going first. Logs are
/// not redacted: the rule sentence says they may hold secrets.
/// </summary>
public sealed partial class DockerLogsTool : DockerTool
{
    public const string ToolName = "docker_logs";
    public const string ContainerArgument = "container";
    public const string TailArgument = "tail";
    public const string SinceArgument = "since";
    public const string GrepArgument = "grep";
    public const string StreamArgument = "stream";
    public const string TimestampsArgument = "timestamps";

    public const int DefaultTail = 100;
    public const int MaxTail = 2000;

    /// <summary>How many of the last lines a grep looks through.</summary>
    public const int GrepWindow = 5000;

    /// <summary>The most characters of log one answer carries.</summary>
    public const int MaxChars = 16_000;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "container": { "type": "string", "description": "The container's name (or part of it) or id." },
            "tail": { "type": "integer", "description": "How many of the last lines, 1 to 2000 (default 100)." },
            "since": { "type": "string", "description": "Only lines from then on: an age (30s, 10m, 2h, 1d) or a moment (2026-10-02T14:00:00Z)." },
            "grep": { "type": "string", "description": "Only lines containing this text (case ignored)." },
            "stream": { "type": "string", "enum": ["both", "stdout", "stderr"], "description": "Which output (default both)." },
            "timestamps": { "type": "boolean", "description": "Prefix each line with its time (default false)." }
          },
          "required": ["container"]
        }
        """);

    public DockerLogsTool(DockerSession docker) : base(docker, confirm: null)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Reads a Docker container's recent log lines, optionally only those since a time or containing a text. Use it to see why a container failed or what it is doing.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, ContainerArgument) is not { } target)
        {
            return DockerText.Missing(ContainerArgument);
        }

        var (tailValue, tailError) = Number(arguments, TailArgument, 1, MaxTail, DefaultTail);
        if (tailError is not null)
        {
            return tailError;
        }

        long? since = null;
        if (Optional(arguments, SinceArgument) is { } sinceText)
        {
            if (ParseSince(sinceText, Docker.Time.GetUtcNow()) is not { } moment)
            {
                return DockerText.BadSince(sinceText);
            }

            since = moment;
        }

        var streams = DockerLogStreams.Both;
        if (Optional(arguments, StreamArgument) is { } streamText)
        {
            switch (streamText.ToLowerInvariant())
            {
                case "both":
                    break;
                case "stdout":
                    streams = DockerLogStreams.Stdout;
                    break;
                case "stderr":
                    streams = DockerLogStreams.Stderr;
                    break;
                default:
                    return DockerText.BadChoice(StreamArgument, streamText, ["both", "stdout", "stderr"]);
            }
        }

        var (timestamps, flagError) = Flag(arguments, TimestampsArgument, fallback: false);
        if (flagError is not null)
        {
            return flagError;
        }

        var (container, _, error) = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return error;
        }

        string? grep = Optional(arguments, GrepArgument);
        int tail = tailValue!.Value;
        var logs = await Docker.Client().LogsAsync(container.Id, grep is null ? tail : GrepWindow, since, timestamps, streams, DockerSession.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (logs.Error is not null)
        {
            return logs.Error;
        }

        return Render(container.Name, logs.Lines, tail, grep, logs.Capped);
    }

    /// <summary>The answer: the header, then the lines (the grep's last <paramref name="tail"/> matches), the oldest cut to fit <see cref="MaxChars"/>. Pure.</summary>
    public static string Render(string name, IReadOnlyList<string> lines, int tail, string? grep, bool capped)
    {
        ArgumentNullException.ThrowIfNull(lines);
        IReadOnlyList<string> kept = grep is null ? lines.TakeLast(tail).ToList() : lines.Where(l => l.Contains(grep, StringComparison.OrdinalIgnoreCase)).TakeLast(tail).ToList();
        if (kept.Count == 0)
        {
            return grep is null ? DockerText.NoLogLines(name) : DockerText.LogsHeader(name, 0, tail, grep, capped);
        }

        int budget = MaxChars;
        int from = kept.Count;
        while (from > 0 && budget - (kept[from - 1].Length + 1) >= 0)
        {
            budget -= kept[from - 1].Length + 1;
            from--;
        }

        var body = kept.Skip(from).Select(l => l.Length > MaxChars ? DockerText.Clip(l, MaxChars) : l).ToList();
        if (body.Count == 0)
        {
            body.Add(DockerText.Clip(kept[^1], MaxChars));
            from = kept.Count - 1;
        }

        string header = DockerText.LogsHeader(name, body.Count, grep is null ? tail : null, grep, capped);
        return header + "\n" + (from > 0 ? DockerText.LogsCut(from) + "\n" : "") + string.Join('\n', body);
    }

    /// <summary>
    /// A <c>since</c> as unix seconds: an age before <paramref name="now"/> (<c>30s</c>, <c>10m</c>, <c>2h</c>, <c>1d</c>,
    /// <c>1w</c>, combined as <c>1h30m</c>), or a moment (ISO 8601, UTC when no offset is given); null for neither. Pure.
    /// </summary>
    public static long? ParseSince(string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        if (Age().Match(trimmed) is { Success: true } match)
        {
            var total = TimeSpan.Zero;
            foreach (Capture part in match.Groups["part"].Captures)
            {
                var piece = AgePart().Match(part.Value);
                if (!double.TryParse(piece.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                {
                    return null;
                }

                total += piece.Groups["u"].Value.ToLowerInvariant() switch
                {
                    "s" => TimeSpan.FromSeconds(n),
                    "m" => TimeSpan.FromMinutes(n),
                    "h" => TimeSpan.FromHours(n),
                    "d" => TimeSpan.FromDays(n),
                    _ => TimeSpan.FromDays(n * 7),
                };
            }

            return (now - total).ToUnixTimeSeconds();
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment) ? moment.ToUnixTimeSeconds() : null;
    }

    [GeneratedRegex(@"^(?<part>\d+(?:\.\d+)?\s*[smhdw])+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Age();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<u>[smhdw])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgePart();
}
