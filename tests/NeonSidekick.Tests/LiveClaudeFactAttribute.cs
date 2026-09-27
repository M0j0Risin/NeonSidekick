using NeonSidekick.Claude;

namespace NeonSidekick.Tests;

/// <summary>
/// The real Claude Code CLI for <c>/claude</c>'s live facts (2026-09-27): opt-in — each run bills the signed-in account
/// — with <see cref="Variable"/> = <c>1</c>, and found as the app finds it (PATH, then the installer's folder).
/// A local gate, never something CI relies on.
/// </summary>
internal static class LiveClaudeCli
{
    public const string Variable = "NEONSIDEKICK_TEST_CLAUDE";

    public static readonly string? Executable;
    public static readonly string Unavailable;

    static LiveClaudeCli()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Unavailable = $"Set {Variable}=1 to run the live Claude Code facts (each run is billed).";
            return;
        }

        Executable = ClaudeExecutable.Locate(null, Environment.GetEnvironmentVariable, File.Exists);
        Unavailable = Executable is null ? "Claude Code was not found on the PATH or in %USERPROFILE%\\.local\\bin." : "";
    }
}

/// <summary>A fact that runs only when <see cref="LiveClaudeCli"/> found the CLI and the opt-in is set.</summary>
public sealed class LiveClaudeFactAttribute : FactAttribute
{
    public LiveClaudeFactAttribute()
    {
        if (LiveClaudeCli.Executable is null)
        {
            Skip = LiveClaudeCli.Unavailable;
        }
    }
}
