using System.Globalization;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Computers;
using NeonSidekick.Network;
using NeonSidekick.Services;
using NeonSidekick.Tasks;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--network-check</c>, <c>--services-check</c>, <c>--tasks-check</c> and <c>--ad-check</c> (2026-10-15, the admin-tools plan,
/// <see cref="EventLogCheck"/>'s shape): each family's proof on the published binary, nothing changed. The computer or domain is found in the
/// whole catalog (ticked or not), as the other checks read it. Exit 0 only when every line passes. Not part of the build gate.
/// </summary>
internal static class AdminChecks
{
    /// <summary>The network checks against <paramref name="host"/>, by the user's hand (never judged): DNS, a ping, port 443, TLS, the user store.</summary>
    public static async Task<int> NetworkAsync(IAnsiConsole console, NetworkAccess access, string host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(access);
        console.MarkupLine(Markup.Escape($"Network check: host '{host}' — nothing is changed."));
        var checks = new List<SmokeCheck>();
        var dns = await NetworkChecks.DnsAsync(access, null, host, DnsWire.A, null, cancellationToken).ConfigureAwait(false);
        checks.Add(Line("net:dns", dns));
        var ping = await NetworkChecks.PingAsync(access, null, host, 2, cancellationToken).ConfigureAwait(false);
        // A ping with no reply and a port not open fail (2026-10-23, the admin review's I3: anything short of "Error:" passed).
        checks.Add(new SmokeCheck("net:ping", !ping.Text.StartsWith("Error:", StringComparison.Ordinal) && !ping.Text.Contains(", 0 received", StringComparison.Ordinal), First(ping.Text)));
        var port = await NetworkChecks.PortsAsync(access, null, host, [443], cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("net:port", !port.Text.StartsWith("Error:", StringComparison.Ordinal) && port.Text.Contains(" open", StringComparison.Ordinal), First(port.Text)));
        var tls = await NetworkChecks.TlsAsync(access, null, host, 443, null, null, cancellationToken).ConfigureAwait(false);
        checks.Add(Line("net:tls", tls));
        var store = CertificateStores.Read("My", System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
        checks.Add(new SmokeCheck("net:certstore", store.Error is null, store.Error ?? $"{store.Certificates.Count.ToString(CultureInfo.InvariantCulture)} certificates in the user's My store"));
        return Report(console, "NETWORK", checks);
    }

    public static async Task<int> ServicesAsync(IAnsiConsole console, IServiceSystem? system, ComputerCatalog<ServiceComputerConfig> loaded, string name, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(loaded);
        console.MarkupLine(Markup.Escape($"Services check: computer '{name}' — nothing is changed."));
        var checks = new List<SmokeCheck>();
        if (system is null)
        {
            checks.Add(new SmokeCheck("services:computer", false, ServiceText.NoneHere));
            return Report(console, "SERVICES", checks);
        }

        if (loaded.Named(name) is not { } computer)
        {
            checks.Add(new SmokeCheck("services:computer", false, ComputerText.UnknownComputer(name.Trim(), string.Join(", ", loaded.Computers.Select(c => c.Name)), ServiceConfigFile.Info)));
            return Report(console, "SERVICES", checks);
        }

        checks.Add(new SmokeCheck("services:computer", true, $"{computer.Where} as {computer.Account}, {(computer.Config.ReadWrite ? "readwrite" : "read")}"));
        var (ok, text) = await ServiceAccess.TestAsync(system, computer, time, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("services:list", ok, text));
        if (ok)
        {
            var access = new ServiceAccess(system, () => new ComputerCatalog<ServiceComputerConfig>([computer], [], ServiceConfigFile.Info), () => [computer.Name], time);
            var (source, _) = access.Computer(computer.Name, forceRedacted: true);
            var detail = await access.ReadAsync(source!, "EventLog", s => s.Detail("EventLog"), cancellationToken).ConfigureAwait(false);
            checks.Add(new SmokeCheck("services:detail", detail.Error is null, detail.Error ?? $"EventLog: {ServiceText.StateWord(detail.Value!.Status.State)}, {ServiceText.StartWord(detail.Value.Config.StartType, detail.Value.Config.DelayedAutoStart)}"));
        }

        return Report(console, "SERVICES", checks);
    }

    public static async Task<int> TasksAsync(IAnsiConsole console, ITaskSystem? system, ComputerCatalog<TaskComputerConfig> loaded, string name, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(loaded);
        console.MarkupLine(Markup.Escape($"Tasks check: computer '{name}' — nothing is changed."));
        var checks = new List<SmokeCheck>();
        if (system is null)
        {
            checks.Add(new SmokeCheck("tasks:computer", false, TaskText.NoneHere));
            return Report(console, "TASKS", checks);
        }

        if (loaded.Named(name) is not { } computer)
        {
            checks.Add(new SmokeCheck("tasks:computer", false, ComputerText.UnknownComputer(name.Trim(), string.Join(", ", loaded.Computers.Select(c => c.Name)), TaskConfigFile.Info)));
            return Report(console, "TASKS", checks);
        }

        checks.Add(new SmokeCheck("tasks:computer", true, $"{computer.Where} as {computer.Account}, {(computer.Config.ReadWrite ? "readwrite" : "read")}"));
        var (ok, text) = await TaskAccess.TestAsync(system, computer, time, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("tasks:list", ok, text));
        return Report(console, "TASKS", checks);
    }

    public static async Task<int> DirectoryAsync(IAnsiConsole console, IDirectorySystem? system, DirectoryCatalog loaded, string name, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(loaded);
        console.MarkupLine(Markup.Escape($"Directory check: domain '{name}' — nothing is changed."));
        var checks = new List<SmokeCheck>();
        if (system is null)
        {
            checks.Add(new SmokeCheck("ad:domain", false, DirectoryText.NoneHere));
            return Report(console, "DIRECTORY", checks);
        }

        if (loaded.Domains.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } domain)
        {
            checks.Add(new SmokeCheck("ad:domain", false, DirectoryText.UnknownDomain(name.Trim(), string.Join(", ", loaded.Domains.Select(d => d.Name)))));
            return Report(console, "DIRECTORY", checks);
        }

        checks.Add(new SmokeCheck("ad:domain", true, $"{domain.DomainName} at {domain.Server}:{domain.Port.ToString(CultureInfo.InvariantCulture)} as {domain.Account}"));
        var (ok, text) = await DirectoryAccess.TestAsync(system, domain, time, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("ad:bind", ok, text));
        return Report(console, "DIRECTORY", checks);
    }

    private static SmokeCheck Line(string name, NetworkAnswer answer) => new(name, !answer.Text.StartsWith("Error:", StringComparison.Ordinal), First(answer.Text));

    private static string First(string text) => text.Split('\n')[0];

    private static int Report(IAnsiConsole console, string family, IReadOnlyList<SmokeCheck> checks)
    {
        int failed = 0;
        foreach (var check in checks)
        {
            failed += check.Passed ? 0 : 1;
            string verdict = check.Passed ? Theme.ColorMarkup(Theme.Good, "PASS") : Theme.ColorMarkup(Theme.Bad, "FAIL");
            console.MarkupLine($"  {verdict}  {Markup.Escape(check.Name)}  {Theme.DimMarkup(check.Detail)}");
        }

        console.WriteLine();
        console.MarkupLine(failed == 0
            ? Theme.ColorMarkup(Theme.Good, $"{family} CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"{family} CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
