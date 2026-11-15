using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the thirteen git tools share (2026-09-20; eleven until 2026-11-15): the <see cref="GitAccess"/> door, the settings in force
/// at each call, the optional <c>path</c> every one takes (the sandbox-relative file or folder the call is
/// about, and where the repository is looked for — blank for the working directory itself), and the
/// synchronous act run off the turn's thread (<see cref="SessionManagerTool"/>'s shape). A refused
/// <see cref="GitOutcome"/> is <see cref="GitText.Error"/>'s sentence, every one starting <c>Error:</c>.
/// </summary>
public abstract class GitTool : AIFunction
{
    public const string PathArgument = "path";
    public const string ReferenceArgument = "ref";

    /// <summary>The <c>path</c> property every schema carries.</summary>
    public const string PathProperty = "\"path\": { \"type\": \"string\", \"description\": \"A file or folder relative to the working directory to narrow the call to (and where the repository is looked for); leave it out for the whole working directory.\" }";

    private readonly GitAccess _git;
    private readonly Func<AppSettingsData> _effective;

    protected GitTool(GitAccess git, Func<AppSettingsData> effective)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    protected GitAccess Git => _git;

    protected AppSettingsData Effective => _effective();

    /// <summary>The zone the moments are shown in: the clock's.</summary>
    protected TimeZoneInfo Zone => _git.Zone;

    protected static string ReadPath(AIFunctionArguments arguments) => ToolArguments.ReadString(arguments, PathArgument);

    /// <summary>The act off the caller's thread: libgit2 is synchronous, and the turn loop is the UI's.</summary>
    protected static async ValueTask<object?> OffThread(Func<string> act, CancellationToken cancellationToken) =>
        await Task.Run(act, cancellationToken).ConfigureAwait(false);
}
