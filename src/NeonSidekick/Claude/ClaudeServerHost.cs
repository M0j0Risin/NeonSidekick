using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Shell;

namespace NeonSidekick.Claude;

/// <summary>
/// The Claude CLI server's process (2026-09-30): what <see cref="ClaudeCliChatClient"/> drives. The app's is
/// <see cref="ClaudeServerHost"/>; the tests' a fake that plays a fixture's lines.
/// </summary>
public interface IClaudeServerHost : IAsyncDisposable
{
    /// <summary>
    /// Makes sure a CLI runs with <paramref name="launch"/>: the one running is kept when it was started with the same,
    /// else stopped and a new one started — <c>--resume</c> on the session when <paramref name="resume"/>, else
    /// <c>--session-id</c>. <paramref name="tools"/> are what its MCP server lists. True when a new one started.
    /// Throws <see cref="ClaudeStartException"/> when the CLI cannot be started.
    /// </summary>
    Task<bool> EnsureRunningAsync(ClaudeServerLaunch launch, bool resume, IReadOnlyList<AIFunction> tools, CancellationToken cancellationToken);

    /// <summary>Writes one <c>stream-json</c> line to the CLI's stdin, which stays open.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>The CLI's next stdout line, in order; null when the process ended (its stdout closed).</summary>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>The last of what the CLI wrote on stderr, for an error that says why.</summary>
    string StderrTail { get; }

    /// <summary>The exit code of a CLI that ended, else null.</summary>
    int? ExitCode { get; }

    /// <summary>Whether a CLI is running.</summary>
    bool Running { get; }

    /// <summary>The model's tool calls as the MCP server takes them: the client sets it once, before the first launch.</summary>
    Func<ClaudeToolCall, CancellationToken, Task<ClaudeToolAnswer>>? OnToolCall { get; set; }

    /// <summary>Stops the CLI, if one runs: stdin closed (a clean exit), then its tree killed when it is still there after a grace.</summary>
    void Stop();
}

/// <summary>
/// The app's <see cref="IClaudeServerHost"/> (2026-09-30): one long-lived <c>claude -p --input-format stream-json</c> child,
/// kept while the turns ask for the same launch and restarted with <c>--resume</c> on the same session when anything in it
/// changed (<see cref="ClaudeServerLaunch"/>) or it ended — a restart is the one recovery for every trouble, and it is
/// cheap: the CLI keeps the session on disk. The child is started where every <c>claude</c> is
/// (<see cref="ClaudeProcess.Start"/>) and joins the app's kill-on-close job (<see cref="ChildJob"/>), so a crashed app
/// takes it down with its MCP relay. stdout is pumped line by line into a channel the client reads a turn at a time;
/// stderr into a tail. The system prompt goes in a file of its own per launch (deleted at the stop), the MCP config inline.
///
/// <para>The MCP listener (<see cref="ClaudeMcpServer"/>) lives as long as the host: each new CLI starts a new relay that
/// connects to it with the same key, and lists the launch's tools.</para>
/// </summary>
public sealed class ClaudeServerHost : IClaudeServerHost
{
    /// <summary>How long a stop waits for the CLI to leave on its own after its stdin closed (the spike's took 0.3 s).</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly object _state = new();
    private readonly Func<IReadOnlyList<string>> _relayCommand;
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private ClaudeServerLaunch? _launch;
    private Channel<string?> _lines = Channel.CreateUnbounded<string?>();
    private IReadOnlyList<AIFunction> _tools = [];
    private ClaudeMcpServer? _mcp;
    private string? _promptFile;
    private int? _exitCode;

    /// <param name="relayCommand">
    /// The command that starts this app's relay, executable first (<see cref="McpRelay"/>'s flag and its two arguments
    /// follow): in the app, the running executable. Asked at each start.
    /// </param>
    public ClaudeServerHost(Func<IReadOnlyList<string>> relayCommand)
    {
        _relayCommand = relayCommand ?? throw new ArgumentNullException(nameof(relayCommand));
    }

    /// <summary>The relay command of the running app: its own executable (the published exe, or the apphost under <c>dotnet run</c>).</summary>
    public static IReadOnlyList<string> OwnRelayCommand() =>
        Environment.ProcessPath is { Length: > 0 } path ? [path] : throw new ClaudeStartException(ClaudeCliText.NoOwnExecutable);

    /// <summary>
    /// The MCP call timeout the child gets (<c>MCP_TOOL_TIMEOUT</c>, milliseconds): a day. A call waits for the app's own
    /// loop, and that can be an approval pane the user has not answered yet; the turn's own budget and ESC end a turn.
    /// </summary>
    public const string McpToolTimeout = "86400000";

    public Func<ClaudeToolCall, CancellationToken, Task<ClaudeToolAnswer>>? OnToolCall { get; set; }

    public string StderrTail
    {
        get
        {
            lock (_stderr)
            {
                string text = _stderr.ToString().Trim();
                return text.Length <= ClaudeProcess.StderrTailChars ? text : text[^ClaudeProcess.StderrTailChars..];
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_state)
            {
                return _exitCode;
            }
        }
    }

    public bool Running
    {
        get
        {
            lock (_state)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public Task<bool> EnsureRunningAsync(ClaudeServerLaunch launch, bool resume, IReadOnlyList<AIFunction> tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(tools);
        lock (_state)
        {
            if (_process is { HasExited: false } && _launch == launch)
            {
                return Task.FromResult(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Stop();
        Start(launch, resume, tools);
        return Task.FromResult(true);
    }

    private void Start(ClaudeServerLaunch launch, bool resume, IReadOnlyList<AIFunction> tools)
    {
        _mcp ??= ClaudeMcpServer.Start(() => Volatile.Read(ref _tools), (call, token) => OnToolCall is { } handler
            ? handler(call, token)
            : Task.FromResult(new ClaudeToolAnswer(ClaudeCliText.NoTurnError, [], IsError: true)));
        Volatile.Write(ref _tools, tools);

        string promptFile = Path.Combine(Path.GetTempPath(), "neonsidekick-claude-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(promptFile, launch.SystemPrompt, new UTF8Encoding(false));
        var relay = _relayCommand();
        string config = ClaudeArguments.McpConfig(relay[0], [.. relay.Skip(1), .. McpRelay.Arguments(_mcp.Address, _mcp.Token)]);
        Directory.CreateDirectory(launch.WorkingDirectory);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["MCP_TOOL_TIMEOUT"] = McpToolTimeout };
        var start = new ProcessLaunch(launch.Executable, ClaudeArguments.BuildServer(launch, resume, promptFile, config), null, launch.WorkingDirectory, "claude server", "claude", environment);

        Process process;
        try
        {
            process = ClaudeProcess.Start(start);
        }
        catch
        {
            TryDelete(promptFile);
            throw;
        }

        ChildJob.Assign(process);
        var lines = Channel.CreateUnbounded<string?>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        lock (_stderr)
        {
            _stderr.Clear();
        }

        lock (_state)
        {
            _process = process;
            _launch = launch;
            _lines = lines;
            _promptFile = promptFile;
            _exitCode = null;
        }

        DiagnosticLog.Info(ClaudeText.Category, ClaudeCliText.StartedLog(process.Id, launch, resume, tools.Count));
        _ = PumpAsync(process, lines);
        _ = DrainErrorsAsync(process);
    }

    private async Task PumpAsync(Process process, Channel<string?> lines)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                lines.Writer.TryWrite(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe went with the process.
        }

        int? code = null;
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(StopGrace).ConfigureAwait(false);
            code = process.ExitCode;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
        }

        lock (_state)
        {
            if (ReferenceEquals(_process, process))
            {
                _exitCode = code;
            }
        }

        DiagnosticLog.Info(ClaudeText.Category, ClaudeCliText.EndedLog(code));
        lines.Writer.TryWrite(null);
        lines.Writer.TryComplete();
    }

    private async Task DrainErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                lock (_stderr)
                {
                    _stderr.Append(line).Append('\n');
                    if (_stderr.Length > ClaudeProcess.StderrTailChars * 2)
                    {
                        _stderr.Remove(0, _stderr.Length - ClaudeProcess.StderrTailChars);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        Process? process;
        lock (_state)
        {
            process = _process;
        }

        if (process is null)
        {
            throw new InvalidOperationException(ClaudeCliText.NotRunningError);
        }

        try
        {
            await process.StandardInput.WriteAsync((line + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new InvalidOperationException(ClaudeCliText.EndedError(ExitCode, StderrTail), ex);
        }
    }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        Channel<string?> lines;
        lock (_state)
        {
            lines = _lines;
        }

        try
        {
            return await lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public void Stop()
    {
        Process? process;
        string? promptFile;
        lock (_state)
        {
            process = _process;
            promptFile = _promptFile;
            _process = null;
            _launch = null;
            _promptFile = null;
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                    {
                    }

                    if (!process.WaitForExit(StopGrace))
                    {
                        ClaudeProcess.Kill(process);
                    }
                }

                DiagnosticLog.Info(ClaudeText.Category, ClaudeCliText.StoppedLog);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        if (promptFile is not null)
        {
            TryDelete(promptFile);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_mcp is { } mcp)
        {
            _mcp = null;
            await mcp.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static string Describe(ClaudeServerLaunch launch) =>
        string.Create(CultureInfo.InvariantCulture, $"model {launch.Model}{(launch.Effort is { } effort ? ", effort " + effort : "")}");
}
