using System.Diagnostics;
using System.Globalization;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// The guard between the app and its <c>llama-server</c> on a Mac (2026-10-07, the user's call; the third of the three layers
/// there, with the exit signals' kill and <see cref="LlamaRecords"/>). Windows' kill-on-close job (<see cref="ChildJob"/>) ends
/// the server however the app ends; macOS has no such thing, and <c>llama-server</c> watches nothing — no flag for its parent
/// or its stdin, and it outlives a dead stdout reader (checked that day: three requests after the reader's kill -9). So the
/// app starts its own executable as <c>NeonSidekick --llama-guard &lt;records&gt; &lt;owner pid&gt; &lt;llama-server&gt;
/// &lt;args…&gt;</c> with stdin a pipe it holds open (the <c>--mcp-relay</c> shape); the guard starts the server, records it,
/// and waits for one of two things:
/// <list type="bullet">
/// <item>its stdin reaching end of file — the app closed the pipe to stop the server, or the app is gone, whose end the kernel
/// turns into a closed pipe however it came (a crash, <c>kill -9</c>): the server is killed (SIGKILL, so the gigabytes are free
/// at once), the record removed, and the guard exits;</item>
/// <item>the server exiting on its own: the record removed, and the guard exits with the server's code, which the host reads
/// as the server's.</item>
/// </list>
/// The server's stdout and stderr are the guard's — the app's pipes — so the host reads its lines as before. Its stdin is a
/// pipe closed at once: the server never holds the guard's. One of the counted process-start sites (the guard starting the
/// server; the host starting the guard is <see cref="LlamaServerHost"/>'s), and Mac only.
/// </summary>
public static class LlamaGuard
{
    /// <summary>The argument that makes the executable a guard. Pinned.</summary>
    public const string Flag = "--llama-guard";

    /// <summary>Whether <paramref name="args"/> ask for the guard: the flag, the records folder, the owner pid and the server's path at least.</summary>
    public static bool Asked(string[] args) =>
        args is { Length: >= 4 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    /// <summary>
    /// The guard's executable: this app's own, when it is the app (<c>NeonSidekick</c>); null otherwise — a test host, which
    /// cannot be a guard, starts the server directly and records it itself.
    /// </summary>
    public static string? DefaultExecutable()
    {
        string? path = Environment.ProcessPath;
        return path is not null && string.Equals(Path.GetFileNameWithoutExtension(path), "NeonSidekick", StringComparison.Ordinal) ? path : null;
    }

    /// <summary>The guard's command line after its executable: the flag, the records folder, the owner (this process), the server and its arguments.</summary>
    public static IReadOnlyList<string> Arguments(string recordsFolder, string serverExecutable, IEnumerable<string> serverArguments) =>
        [Flag, recordsFolder, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), serverExecutable, .. serverArguments];

    /// <summary>The guard's run (<paramref name="args"/> as <see cref="Asked"/> takes them); the server's exit code, or 1 when it was killed, 127 when it did not start.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string records = args[1];
        if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int owner))
        {
            await Console.Error.WriteLineAsync("llama-guard: the owner pid is not a number").ConfigureAwait(false);
            return 2;
        }

        string executable = args[3];
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,   // closed below: the server gets end of file, never the guard's pipe
            WorkingDirectory = Path.GetDirectoryName(executable) ?? ".",
        };
        foreach (var argument in args.Skip(4))
        {
            start.ArgumentList.Add(argument);
        }

        Process server;
        try
        {
            server = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync("llama-guard: " + Path.GetFileName(executable) + " did not start: " + ex.Message).ConfigureAwait(false);
            return 127;
        }

        using (server)
        {
            server.StandardInput.Close();
            string? record = LlamaRecords.Write(records, owner, server, executable);
            try
            {
                using var stdin = Console.OpenStandardInput();
                var released = Task.Run(() =>
                {
                    var buffer = new byte[256];
                    while (stdin.Read(buffer) > 0)
                    {
                        // Nothing is sent: only the end of the pipe means anything.
                    }
                });
                var exited = server.WaitForExitAsync();
                await Task.WhenAny(released, exited).ConfigureAwait(false);
                if (!server.HasExited)
                {
                    server.Kill();
                    await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    return 1;
                }

                return server.ExitCode;
            }
            finally
            {
                LlamaRecords.Remove(record);
            }
        }
    }
}
