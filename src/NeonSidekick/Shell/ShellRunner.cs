using System.Diagnostics;
using System.Globalization;
using System.Text;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>A child that could not be started: the sentence names the executable and Windows' reason.</summary>
public sealed class ShellStartException(string message) : Exception(message);

/// <summary>
/// The one place the shell tools start a process (2026-09-21) — the fourth counted process-start
/// site after <c>PersonaFile</c>, <c>HeadlessBrowser</c> and the MCP SDK's stdio transport; the code
/// tool and its bridge build a <see cref="ProcessLaunch"/> and come through here too. The
/// <c>HeadlessBrowser.RunAsync</c> shape: no shell-execute, no window (the child gets a hidden
/// console of its own, so the TUI's Ctrl+C — a key here — never reaches it and its own never
/// reaches us), the three streams redirected as UTF-8, the environment from
/// <see cref="ChildEnvironment"/>. Both output pumps start before anything waits (reading one
/// stream to its end before the other deadlocks a child that fills the other pipe), each line
/// landing in the session's <see cref="OutputBuffer"/> tagged by stream; the session completes
/// once the process is gone and both pumps have drained.
///
/// <para>On a Mac the same site starts the child with libc's <c>posix_spawnp</c> rather than <c>Process.Start</c> (2026-10-12, the
/// user's call: <see cref="GroupChild"/>), as the leader of a session of its own, so a kill ends its whole process group — a grandchild
/// launchd adopted too, which the tree walk misses. Elsewhere <see cref="ProcessChild"/>, as before.</para>
/// </summary>
public sealed class ShellRunner
{
    private readonly TimeProvider _time;

    public ShellRunner(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>A fresh id: <paramref name="prefix"/> and six lower hex digits (<c>proc_3f2a1b</c>, <c>run_9c04e1</c>).</summary>
    public static string NewId(Random random, string prefix)
    {
        ArgumentNullException.ThrowIfNull(random);
        return prefix + random.Next(0, 0x1000000).ToString("x6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Starts <paramref name="launch"/> as <paramref name="id"/>; the session is the caller's to wait on, kill and dispose. On Windows
    /// the child runs in a job of its own (2026-10-12, the shell review), so a kill ends everything it started; with <paramref name="tiedToApp"/>
    /// (a background command, the user's call) the job is kill-on-close, so the app's end, a crash too, and the session's dispose end
    /// what is left of it. It is started suspended inside the job (2026-10-19, the third shell review, <see cref="EmbeddedLlm.ChildJob.StartPiped"/>),
    /// so nothing it starts is born outside; only where that cannot be done (.NET's process-start lock not found, an executable that is
    /// not a full path) is it put in one right after <c>Process.Start</c>, as before (<see cref="EmbeddedLlm.ChildJob.CreateOwn"/>). On a Mac the child
    /// leads a session of its own instead (<see cref="GroupChild"/>): a kill ends its group, and with <paramref name="tiedToApp"/> the
    /// session's dispose ends what is left of it; a crash does not.
    /// </summary>
    /// <exception cref="ShellStartException">The program could not be started (missing, refused, not a program).</exception>
    public ProcessSession Start(ProcessLaunch launch, string id, bool tiedToApp = false)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(id);
        ShellChild child = OperatingSystem.IsMacOS() ? GroupChild.Start(launch, tiedToApp) : StartProcess(launch, tiedToApp);
        var session = new ProcessSession(id, launch, child, _time);
        DiagnosticLog.Debug(ShellKinds.Category, $"{id}: started pid {child.Pid.ToString(CultureInfo.InvariantCulture)} ({launch.Kind}) {launch.Label}");
        _ = PumpAndCompleteAsync(session, child);
        return session;
    }

    private static ProcessChild StartProcess(ProcessLaunch launch, bool tiedToApp)
    {
        if (OperatingSystem.IsWindows() && Path.IsPathFullyQualified(launch.Executable))
        {
            var environment = new ProcessStartInfo();
            ChildEnvironment.Apply(environment, launch);
            var piped = EmbeddedLlm.ChildJob.StartPiped(launch.Executable, WindowsCommandLine(launch), launch.WorkingDirectory, environment.Environment, tiedToApp, out string error);
            if (piped is not null)
            {
                return new ProcessChild(piped);
            }

            if (error.Length > 0)
            {
                throw new ShellStartException(ShellText.CouldNotStart(Path.GetFileName(launch.Executable), error));
            }
        }


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
        if (launch.ArgumentList is { } list)
        {
            foreach (string argument in list)
            {
                start.ArgumentList.Add(argument);
            }
        }
        else
        {
            start.Arguments = launch.Arguments ?? "";
        }

        ChildEnvironment.Apply(start, launch);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ShellStartException(ShellText.CouldNotStart(Path.GetFileName(launch.Executable), ex.Message));
        }

        if (process is null)
        {
            throw new ShellStartException(ShellText.CouldNotStart(Path.GetFileName(launch.Executable), "no process"));
        }

        return new ProcessChild(process, killOnClose: tiedToApp);
    }

    /// <summary>The command line <c>Process.Start</c> would give <paramref name="launch"/>: the executable quoted, then its argument list or its raw arguments.</summary>
    internal static string WindowsCommandLine(ProcessLaunch launch)
    {
        if (launch.ArgumentList is { } list)
        {
            return EmbeddedLlm.ChildJob.CommandLine(launch.Executable, list);
        }

        string arguments = launch.Arguments ?? "";
        string executable = launch.Executable.StartsWith('"') ? launch.Executable : "\"" + launch.Executable + "\"";
        return arguments.Length == 0 ? executable : executable + " " + arguments;
    }

    private static async Task PumpAndCompleteAsync(ProcessSession session, ShellChild child)
    {
        var stdout = PumpAsync(child.StandardOutput, session.Output, isError: false);
        var stderr = PumpAsync(child.StandardError, session.Output, isError: true);
        int code;
        try
        {
            await child.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            code = child.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // Disposed under us (the app's exit): the code is whatever a killed child gives.
            code = -1;
        }

        DiagnosticLog.Debug(ShellKinds.Category, $"{session.Id}: exit {code.ToString(CultureInfo.InvariantCulture)} after {session.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s, {session.Output.TotalLines.ToString(CultureInfo.InvariantCulture)} lines");
        session.Complete(code);
    }

    /// <summary>How many chars one read of a stream takes.</summary>
    internal const int PumpChunk = 4096;

    /// <summary>
    /// One stream into the buffer, a chunk at a time (2026-10-12, the shell review; <c>ReadLineAsync</c> until then). A line ends at
    /// <c>\n</c> or <c>\r\n</c>. A lone <c>\r</c> starts the line over, as a terminal draws it, so a progress bar (pip, npm, curl)
    /// is the one line it ends as rather than a line per redraw pushing the real output out of the ring. A line is held to
    /// <see cref="OutputBuffer.MaxLineChars"/> as it arrives and the rest of it dropped, so a single line of hundreds of MB (a
    /// minified file, a binary typed) never sits whole in memory as it did. What a read leaves without its newline is the stream's
    /// unfinished line (<see cref="OutputBuffer.SetUnfinished"/>): a prompt waiting for input is seen; the end of the stream appends it.
    /// </summary>
    internal static async Task PumpAsync(TextReader reader, OutputBuffer output, bool isError)
    {
        var chunk = new char[PumpChunk];
        var line = new StringBuilder();
        bool carriage = false;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(chunk.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                // Whether this read changed the unfinished line: a line past its cap, all of it dropped, is told once, not rebuilt and
                // compared after every read (the fourth shell review, 2026-10-19: a huge line with no newline made ~16 KB of strings per 4 KB).
                bool changed = false;
                for (int i = 0; i < read; i++)
                {
                    char c = chunk[i];
                    if (carriage)
                    {
                        carriage = false;
                        if (c != '\n' && c != '\r')
                        {
                            // A second \r keeps the carriage pending and leaves the line: Python on Windows writes \r\n
                            // to a text-mode stdout as \r\r\n (the csv module does by default), and clearing here on the
                            // second \r lost every such line's text (the code review, 2026-10-19). A real redraw (\r then
                            // text) still clears, as a terminal overwrites the line.
                            changed |= line.Length > 0;
                            line.Clear();
                        }
                    }

                    if (c == '\n')
                    {
                        output.Append(line.ToString(), isError);
                        changed = true;
                        line.Clear();
                    }
                    else if (c == '\r')
                    {
                        carriage = true;
                    }
                    else if (line.Length <= OutputBuffer.MaxLineChars)
                    {
                        // One past the cap, so Append cuts it with the mark; the rest of the line is dropped as it comes.
                        line.Append(c);
                        changed = true;
                    }
                }

                if (changed)
                {
                    output.SetUnfinished(line.Length > 0 ? line.ToString() : null, isError);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe went with the process (a kill, a dispose): what arrived is kept.
        }

        output.SetUnfinished(null, isError);
        if (line.Length > 0)
        {
            output.Append(line.ToString(), isError);
        }
    }
}
