using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What every database family's tools share (2026-10-24, the database review's K8, decision DB5: <see cref="SqlTool"/>,
/// <see cref="OracleTool"/>, <see cref="MySqlTool"/>, <see cref="PostgresTool"/> and <see cref="SqliteTool"/> were hand copies of one base,
/// and a shared fix had to be made in each and had already drifted): the family's access door, the settings in force at the call, the
/// retired argument a call is refused for still sending (<c>database</c>, or Oracle's <c>schema</c>; SQLite has none), a blank-is-absent
/// string argument, and the boundary's names read. What differs — the timeout setting, the table lookup's shape, what the boundary asks —
/// stays in the family's base.
/// </summary>
/// <remarks>
/// The settings are read once per call (2026-10-24, the review's P7): the query and catalog tools read <c>Effective</c> three or four times
/// a call, so the row cap, the timeout and the result size could come from different moments of a setting being changed on the pane.
/// <see cref="InvokeCoreAsync"/> takes one snapshot for the whole call (an <see cref="AsyncLocal{T}"/> per tool, set inside an async
/// method so it never leaks to the caller); a direct call to a tool's own <c>RunAsync</c> (the tests', the slash commands') reads the
/// settings as it goes, as before.
/// </remarks>
public abstract class ServerTool<TAccess> : AIFunction where TAccess : class
{
    public const string ConnectionArgument = "connection";
    public const string TableArgument = "table";

    private readonly Func<AppSettingsData> _effective;
    private readonly AsyncLocal<AppSettingsData?> _call = new();

    protected ServerTool(TAccess access, Func<AppSettingsData> effective)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    /// <summary>The family's one door to its connections.</summary>
    protected TAccess Access { get; }

    /// <summary>The settings in force: the call's snapshot inside a model's call, else read now.</summary>
    protected AppSettingsData Effective => _call.Value ?? _effective();

    /// <summary>The argument a call is refused for still sending (the tools took it until 2026-10-09), or null for none.</summary>
    protected abstract string? RetiredArgument { get; }

    /// <summary>The refusal of <see cref="RetiredArgument"/>.</summary>
    protected abstract string RetiredRefusal { get; }

    /// <summary>
    /// Every tool's call: refused when it still names the retired argument (a model that remembers it would otherwise believe it reached
    /// another database or schema), else the tool's own <see cref="InvokeToolAsync"/> under one settings snapshot.
    /// </summary>
    protected sealed override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (RetiredArgument is { } retired && Optional(arguments, retired) is not null)
        {
            return RetiredRefusal;
        }

        _call.Value = _effective();
        return await InvokeToolAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tool's own call, its arguments read.</summary>
    protected abstract ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken);

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// The names the boundary checks a statement against, read through <paramref name="names"/> (kept a minute per connection): the rows,
    /// or the family's <c>Error:</c> sentence for a read that failed — never a pass (each family's boundary has said so since 2026-10-09).
    /// </summary>
    protected static async Task<(IReadOnlyList<string[]>? Rows, string? Error)> ReadNamesAsync(BoundaryNames names, string key, Func<CancellationToken, Task<SqlRun>> read, Func<SqlRun, string> error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(error);
        var (rows, failed) = await names.GetAsync(key, read, cancellationToken).ConfigureAwait(false);
        return failed is not null ? (null, error(failed)) : (rows, null);
    }
}
