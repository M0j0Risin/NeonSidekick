using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the UNC tools share (2026-09-30, the user's ask): the <see cref="UncAccess"/> door, the settings in force at each call,
/// and the optional <c>share</c> — a name from <c>unc.json</c>; left out, the share a full <c>path</c> lies in, else the setting
/// <c>UNC default share</c>, else the first. A read runs as the share's account and is abandoned on ESC; a change is refused unless
/// both keys allow it (<c>UNC writes</c> and the share's <c>readwrite</c>), runs to its end, and leaves an audit line. A file
/// operation's result is <see cref="FileText"/>'s, its root named for the share (<see cref="UncText.Scoped"/>).
/// </summary>
public abstract class UncTool : AIFunction
{
    public const string ShareArgument = "share";
    public const string PathArgument = FileTool.PathArgument;

    /// <summary>The <c>share</c> property every schema but <c>unc_shares</c>' carries.</summary>
    public const string ShareProperty = "\"share\": { \"type\": \"string\", \"description\": \"The share's name (unc_shares lists them); leave it out for the default share, or when path is a full \\\\\\\\server\\\\share path or a folder under one.\" }";

    private readonly UncAccess _unc;
    private readonly Func<AppSettingsData> _effective;

    protected UncTool(UncAccess unc, Func<AppSettingsData> effective)
    {
        _unc = unc ?? throw new ArgumentNullException(nameof(unc));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    protected UncAccess Unc => _unc;

    protected AppSettingsData Effective => _effective();

    /// <summary>The <c>share</c> argument, or null when blank.</summary>
    protected static string? ReadShare(AIFunctionArguments arguments) =>
        ToolArguments.ReadString(arguments, ShareArgument).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// A read on the share the call means: <paramref name="describe"/> over the share's sandbox and the path as given, as the
    /// share's account, its result scoped to the share — or the refusal that kept it from running.
    /// </summary>
    protected Task<string> ReadAsync(string? share, string path, Func<WorkingDirectory, string, string> describe, CancellationToken cancellationToken) =>
        ReadAsync(share, path, (files, relative, target) => UncText.Scoped(describe(files, relative), target), cancellationToken);

    /// <summary>
    /// <see cref="ReadAsync(string?, string, Func{WorkingDirectory, string, string}, CancellationToken)"/> for a tool that words its own
    /// result (<c>unc_fetch</c>, whose other end is the working directory): <paramref name="describe"/> gets the share too, and its
    /// text is returned as it stands.
    /// </summary>
    protected async Task<string> ReadAsync(string? share, string path, Func<WorkingDirectory, string, UncNamedShare, string> describe, CancellationToken cancellationToken)
    {
        var target = Unc.Resolve(share, path, Effective.UncDefaultShare, out string relative, out string? error);
        if (target is null)
        {
            return error!;
        }

        var result = await Unc.RunAsync(target, write: false, files => describe(files, relative, target), cancellationToken).ConfigureAwait(false);
        return result.Error ?? result.Value!;
    }

    /// <summary>
    /// A change on the share the call means: refused unless <c>UNC writes</c> is on and the share is <c>readwrite</c>
    /// (<see cref="UncAccess.WriteRefusal"/>); else <paramref name="change"/> over the share's sandbox as the share's account, waited
    /// out, and on success an audit line naming <paramref name="action"/> and the path. The result scoped to the share.
    /// </summary>
    protected Task<string> WriteAsync(string? share, string path, string action, Func<WorkingDirectory, string, (string Text, bool Done)> change, CancellationToken cancellationToken) =>
        WriteAsync(share, path, action, (files, relative, target) =>
        {
            var (text, done) = change(files, relative);
            return (UncText.Scoped(text, target), done);
        }, cancellationToken);

    /// <summary>
    /// <see cref="WriteAsync(string?, string, string, Func{WorkingDirectory, string, ValueTuple{string, bool}}, CancellationToken)"/> for a
    /// tool that words its own result (<c>unc_put</c>, whose other end is the working directory): <paramref name="change"/> gets the
    /// share too, and its text is returned as it stands.
    /// </summary>
    protected async Task<string> WriteAsync(string? share, string path, string action, Func<WorkingDirectory, string, UncNamedShare, (string Text, bool Done)> change, CancellationToken cancellationToken)
    {
        var target = Unc.Resolve(share, path, Effective.UncDefaultShare, out string relative, out string? error);
        if (target is null)
        {
            return error!;
        }

        if (UncAccess.WriteRefusal(target, Effective.UncWrites) is { } refused)
        {
            return refused;
        }

        var result = await Unc.RunAsync(target, write: true, files => change(files, relative, target), cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return failed;
        }

        if (result.Value.Done)
        {
            UncAccess.Audit(target, action, relative);
        }

        return result.Value.Text;
    }
}
