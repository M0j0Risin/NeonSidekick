using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Unc;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>open(path?, share?)</c>: hands a file to the user's own editor or viewer, or a folder to Explorer,
/// through the injected opener (<c>PersonaFile.OpenInEditor</c>: shell execute, never waits).
/// No path is the working directory itself.
///
/// <para><b>On a UNC share</b> (2026-10-01, the user's ask: "make it so that the open tool can open files in either of the
/// connected UNC shares"): while the UNC group is offered (<see cref="UncAccess.IsOffered"/>) the schema carries <c>share</c>,
/// and a <c>share</c> or a full <c>\\server\share</c> path outside the working directory opens there — resolved, checked and
/// worded as the UNC tools' reads are (<see cref="UncAccess.Resolve"/>, the preflight, <see cref="UncText.Scoped"/>). A
/// <c>runas</c> share on the network is refused (the user's call): its sign-in is a netonly token per call, and the program
/// the shell starts runs with the user's own token, so Explorer or the editor would knock with the wrong account — the model
/// is pointed to <c>unc_fetch</c> and then <c>open</c> on the copy. A local folder under <c>runas</c> is the user's own
/// anyway, so it opens. Without a share and with a relative path, nothing changed: the working directory.</para>
/// </summary>
public sealed class OpenTool : FileTool
{
    public const string ToolName = "open";
    public const string ShareArgument = UncTool.ShareArgument;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file or folder to open, relative to the working directory. Leave it out to open the working directory itself." }
          }
        }
        """);

    /// <summary>The schema while a UNC share is offered: <see cref="Schema"/> and the <c>share</c>.</summary>
    private static readonly JsonElement ShareSchema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file or folder to open, relative to the working directory (or to the share when share is given). Leave it out to open the working directory (or the share's root) itself." },
            "share": { "type": "string", "description": "A UNC share's name (unc_shares lists them) to open the file or folder there instead of the working directory; not needed when path is a full \\\\server\\share path under one." }
          }
        }
        """);

    private const string BaseDescription =
        "Opens a file in the user's own editor or viewer, or a folder in Explorer, on their screen; no path opens the working directory (the user's cwd / current directory) itself. " +
        "Use it when they ask to open, show or see something rather than to have it read out.";

    private const string ShareDescription =
        " With share, or a full \\\\server\\share path, it opens the file or folder on that UNC share instead; a share signed in as another account cannot be opened this way (unc_fetch the file, then open the copy).";

    private readonly Action<string> _opener;
    private readonly UncAccess? _unc;
    private readonly Func<AppSettingsData>? _effective;

    /// <param name="files">The working directory.</param>
    /// <param name="opener">The shell's "open with".</param>
    /// <param name="unc">The UNC shares' door (2026-10-01); null = the working directory alone.</param>
    /// <param name="effective">The settings in force, read at each call for <c>UNC tools</c> and the offered shares; needed with <paramref name="unc"/>.</param>
    public OpenTool(WorkingDirectory files, Action<string> opener, UncAccess? unc = null, Func<AppSettingsData>? effective = null) : base(files)
    {
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        _unc = unc;
        _effective = effective;
    }

    public override string Name => ToolName;

    public override string Description => SharesOffered ? BaseDescription + ShareDescription : BaseDescription;

    public override JsonElement JsonSchema => SharesOffered ? ShareSchema : Schema;

    /// <summary>Whether a UNC share can be named now: the door given, <c>UNC tools</c> on, a share offered.</summary>
    private bool SharesOffered => _unc is not null && _effective is not null && _unc.IsOffered(_effective());

    public string Describe(string path) => FileText.Opened(Files.Open(path, _opener));

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string path = ReadPath(arguments);
        string? share = ToolArguments.ReadString(arguments, ShareArgument).Trim() is { Length: > 0 } named ? named : null;
        if (share is null && !OnAShare(path))
        {
            return Describe(path);
        }

        return await OpenOnShareAsync(share, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a call without a share means one: a full path outside the working directory while a share is offered.</summary>
    private bool OnAShare(string path)
    {
        string spelled = path.Trim().Replace('/', '\\');
        return UncCatalog.IsAbsolute(spelled) && SharesOffered && Files.Resolve(spelled, forWrite: false, out _) != FileOutcome.Ok;
    }

    /// <summary>The open on the share the call means, or the refusal: no share offered, the share unknown, a network <c>runas</c> share.</summary>
    private async Task<string> OpenOnShareAsync(string? share, string path, CancellationToken cancellationToken)
    {
        if (!SharesOffered)
        {
            return UncText.NoSharesOffered;
        }

        var target = _unc!.Resolve(share, path, _effective!().UncDefaultShare, out string relative, out string? error);
        if (target is null)
        {
            return error!;
        }

        if (target.Config.IsRunAs && target.Config.IsUnc)
        {
            return UncText.OpenRunAsRefused(target.Name);
        }

        // As the user (signIn false): a local runas folder needs no token, and the opened program runs as the user anyway.
        var result = await _unc.RunAsync(target, write: false, files => UncText.Scoped(FileText.Opened(files.Open(relative, _opener)), target), cancellationToken, signIn: false).ConfigureAwait(false);
        return result.Error ?? result.Value!;
    }
}
