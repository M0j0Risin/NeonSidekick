using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Shell;

namespace NeonSidekick.Claude;

/// <summary>
/// Where the Claude Code CLI is (2026-09-27), pure over its inputs: the <c>Claude CLI executable</c> setting when it
/// names a file; else <c>claude.exe</c> on the PATH (the native install), else <c>claude</c> with the PATHEXT
/// extensions (npm's <c>claude.cmd</c> shim); else the native installer's own folder,
/// <c>%USERPROFILE%\.local\bin\claude.exe</c>, which a fresh install has not always put on the PATH yet.
/// </summary>
public static class ClaudeExecutable
{
    /// <summary>The full path, or null when none is found — a set path that is no file included (never a silent fall-through to another CLI).</summary>
    public static string? Locate(string? configured, Func<string, string?> environment, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(exists);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string path = configured.Trim().Trim('"');
            return exists(path) ? path : null;
        }

        string? pathVariable = environment("PATH");
        string? pathExt = environment("PATHEXT");
        if (InterpreterProbe.Find("claude.exe", pathVariable, pathExt, exists) is { } native)
        {
            return native;
        }

        if (InterpreterProbe.Find("claude", pathVariable, pathExt, exists, InterpreterProbe.IsStoreStub) is { } shim)
        {
            return shim;
        }

        // The native installer's place: %USERPROFILE%\.local\bin\claude.exe, or ~/.local/bin/claude off Windows (2026-10-06, the macOS build).
        bool windows = OperatingSystem.IsWindows();
        if (environment(windows ? "USERPROFILE" : "HOME") is { Length: > 0 } home)
        {
            string local = Path.Combine(home, ".local", "bin", windows ? "claude.exe" : "claude");
            if (exists(local))
            {
                return local;
            }
        }

        return null;
    }
}

/// <summary>
/// The app's <see cref="IClaudeCli"/> (2026-09-27): one <c>claude -p</c> child per <c>/claude</c> message. A
/// process-start site of its own — a deliberate one, beside <c>ShellRunner</c>, not through it: the child is the
/// user's own tool started on the user's own command, not the model's, so <c>CommandGate</c> has nothing to ask,
/// and <c>ShellRunner</c>'s <see cref="OutputBuffer"/> cuts a line past 8 KB where one <c>stream-json</c> line (an
/// assistant message with a file in it) is often longer. The launch is the shell's shape all the same
/// (<see cref="ProcessLaunch"/>, the argument list quoted by .NET, <see cref="ChildEnvironment"/> on top of the
/// inherited variables). The prompt goes in on stdin and stdin is closed: no quoting, no length limit, and the CLI
/// starts at once. stdout is read line by line into <see cref="ClaudeStreamParser"/>; stderr is kept (its tail) to
/// say why a run that wrote no result failed. A cancel kills the child's whole tree (the CLI's own tool children with
/// it). Every run waits for the exit before its last event, so no child outlives its reply.
/// </summary>
public sealed class ClaudeProcess : IClaudeCli
{
    /// <summary>The most of stderr kept for an error: its tail.</summary>
    public const int StderrTailChars = 2000;

    private readonly Func<string, string?> _environment;
    private readonly Func<string, bool> _exists;

    /// <param name="environment">Reads a system variable (<c>PATH</c>, <c>PATHEXT</c>, <c>USERPROFILE</c>): <see cref="Settings.EnvironmentOverrides.System"/> in the app.</param>
    /// <param name="exists">The file test; <see cref="File.Exists(string)"/> when null.</param>
    public ClaudeProcess(Func<string, string?> environment, Func<string, bool>? exists = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _exists = exists ?? File.Exists;
    }

    public async IAsyncEnumerable<ClaudeEvent> RunAsync(ClaudeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string executable = ClaudeExecutable.Locate(request.Executable, _environment, _exists)
            ?? throw new ClaudeStartException(string.IsNullOrWhiteSpace(request.Executable) ? ClaudeText.NotFound : ClaudeText.ConfiguredNotFound(request.Executable.Trim()));
        var launch = new ProcessLaunch(executable, ClaudeArguments.Build(request), null, request.WorkingDirectory, "claude -p", "claude");
        await foreach (var evt in RunLaunchAsync(launch, request.Prompt, pid => ClaudeText.StartedLog(pid, request), cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// One <c>claude -p</c> child over <paramref name="launch"/> (2026-09-30: <c>/claude</c>'s and <c>claude_advisor_cli</c>'s,
    /// and the Claude CLI server's one-shot requests, <see cref="ClaudeArguments.BuildOneShot"/>): <paramref name="prompt"/>
    /// on stdin, stdin closed, the reply read off stdout. The same shape and the same guarantees as
    /// <see cref="RunAsync"/>: a <see cref="ClaudeEvent.Result"/> last, the child's tree killed on a cancel.
    /// </summary>
    internal static async IAsyncEnumerable<ClaudeEvent> RunLaunchAsync(ProcessLaunch launch, string prompt, Func<int, string> startedLog, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var process = Start(launch);
        DiagnosticLog.Info(ClaudeText.Category, startedLog(process.Id));

        var stderr = ReadTailAsync(process.StandardError);
        bool exited = false;
        var parser = new ClaudeStreamParser();
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child is gone already (a bad flag, no login): its stderr and exit code say why, below.
            }

            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                foreach (var evt in parser.Read(line))
                {
                    yield return evt;
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            exited = true;
        }
        finally
        {
            if (!exited)
            {
                Kill(process);
            }
        }

        string tail = await stderr.ConfigureAwait(false);
        DiagnosticLog.Info(ClaudeText.Category, ClaudeText.ExitedLog(process.ExitCode));
        if (!parser.SawResult)
        {
            // No result line: the CLI failed before its run (not logged in, a bad model, a crash).
            yield return new ClaudeEvent.Result(null, 0m, default, true, ClaudeText.NoResult(process.ExitCode, tail), "", []);
        }
    }

    /// <summary>
    /// Starts a Claude CLI child with its three streams redirected as UTF-8 (no BOM) and <see cref="ChildEnvironment"/> on
    /// top of the inherited variables: the one place this app starts <c>claude</c>, <c>/claude</c>'s per-message children
    /// and the Claude CLI server's long-lived one (<see cref="ClaudeServerHost"/>, 2026-09-30) alike.
    /// </summary>
    internal static Process Start(ProcessLaunch launch)
    {
        var start = new ProcessStartInfo(launch.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = launch.WorkingDirectory,
        };
        foreach (string argument in launch.ArgumentList ?? [])
        {
            start.ArgumentList.Add(argument);
        }

        ChildEnvironment.Apply(start, launch);
        try
        {
            return Process.Start(start) ?? throw new ClaudeStartException(ClaudeText.CouldNotStart(launch.Executable, "no process"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ClaudeStartException(ClaudeText.CouldNotStart(launch.Executable, ex.Message));
        }
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                DiagnosticLog.Info(ClaudeText.Category, ClaudeText.KilledLog);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Gone between the test and the kill.
        }
    }

    /// <summary>stderr to its end, only the last <see cref="StderrTailChars"/> kept; a broken pipe ends it quietly.</summary>
    internal static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                tail.Append(line).Append('\n');
                if (tail.Length > StderrTailChars * 2)
                {
                    tail.Remove(0, tail.Length - StderrTailChars);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe went with the process.
        }

        string text = tail.ToString().Trim();
        return text.Length <= StderrTailChars ? text : text[^StderrTailChars..];
    }
}
