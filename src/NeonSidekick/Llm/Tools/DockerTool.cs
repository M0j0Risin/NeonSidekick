using Microsoft.Extensions.AI;
using NeonSidekick.Docker;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the ten Docker tools share (2026-10-02, the <see cref="HaTool"/> and <see cref="UncTool"/> shapes): the
/// <see cref="DockerSession"/> door, the settings in force at each call, the container lookup, and the gate every changing
/// tool passes before it acts — <c>Docker writes</c> read again (<see cref="DockerPolicy.WriteRefusal"/>), then the user's yes
/// through <c>confirm</c> (true yes, false no, null nothing could ask; the seam itself null where nothing ever can,
/// headless). Every change asks: there is no policy that lets one through unasked. A refusal is an <c>Error:</c> sentence,
/// never a throw.
/// </summary>
public abstract class DockerTool : AIFunction
{
    private readonly DockerSession _docker;
    private readonly Func<string, CancellationToken, Task<bool?>>? _confirm;

    protected DockerTool(DockerSession docker, Func<string, CancellationToken, Task<bool?>>? confirm)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _confirm = confirm;
    }

    protected DockerSession Docker => _docker;

    protected AppSettingsData Effective => _docker.Effective;

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A boolean argument: <paramref name="fallback"/> when missing, null with the refusal when it is not one.</summary>
    protected static (bool Value, string? Error) Flag(AIFunctionArguments arguments, string name, bool fallback)
    {
        if (!ToolArguments.TryReadBoolean(arguments, name, out bool? value, out string raw))
        {
            return (fallback, DockerText.NotBoolean(name, raw));
        }

        return (value ?? fallback, null);
    }

    /// <summary>A whole-number argument in <paramref name="min"/>..<paramref name="max"/>: <paramref name="fallback"/> when missing, the refusal when out of range or not a number.</summary>
    protected static (int? Value, string? Error) Number(AIFunctionArguments arguments, string name, int min, int max, int? fallback)
    {
        if (!ToolArguments.TryReadInt32(arguments, name, out int? value, out string raw) || value is { } n && (n < min || n > max))
        {
            string given = raw.Length > 0 ? raw : value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return (null, DockerText.OutOfRange(name, given, min, max));
        }

        return (value ?? fallback, null);
    }

    /// <summary>The one container <paramref name="target"/> names, over a fresh list; or the refusal.</summary>
    protected async Task<(DockerContainer? Container, IReadOnlyList<DockerContainer> All, string? Error)> ResolveAsync(string target, CancellationToken cancellationToken)
    {
        var (all, error) = await _docker.ContainersAsync(cancellationToken).ConfigureAwait(false);
        if (all is null)
        {
            return (null, [], error);
        }

        var match = DockerTargets.Resolve(all, target);
        return match.Error is not null ? (null, all, match.Error) : (match.Containers[0], all, null);
    }

    /// <summary>
    /// Whether the change <paramref name="act"/> (in words: <c>stop container mysql_dev (…)</c>) may run: null when it may,
    /// else the refusal — writes off, the user's no, nobody to ask.
    /// </summary>
    protected async Task<string?> GateWriteAsync(string act, CancellationToken cancellationToken)
    {
        if (DockerPolicy.WriteRefusal(Effective.DockerWrites) is { } refused)
        {
            return refused;
        }

        bool? yes = _confirm is null ? null : await _confirm(DockerText.ConfirmQuestion(act), cancellationToken).ConfigureAwait(false);
        return yes switch
        {
            true => null,
            false => DockerText.Declined,
            null => DockerText.NotAsked,
        };
    }
}
