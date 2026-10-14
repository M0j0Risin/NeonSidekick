using Microsoft.Extensions.Hosting;

namespace NeonSidekick.WebUi;

/// <summary>
/// The Web UI host's lifetime (2026-10-14): nothing. A host built by <c>WebApplication</c> registers <c>ConsoleLifetime</c>, which hooks
/// <see cref="Console.CancelKeyPress"/> and the POSIX SIGINT/SIGTERM/SIGQUIT to stop itself. In this app Ctrl+C is a key (the raw console
/// input on Windows, termios on a Mac) and the app owns its own shutdown, so the web server never touches the console or the signals:
/// it starts and stops when <see cref="WebUiHost"/> says.
/// </summary>
public sealed class WebUiLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
