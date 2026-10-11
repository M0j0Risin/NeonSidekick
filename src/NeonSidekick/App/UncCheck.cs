using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Principal;
using NeonSidekick.UI;
using NeonSidekick.Unc;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--unc-check &lt;share&gt;</c> (2026-09-30, the UNC tools): their proof on the published binary against a real share of the
/// loaded profile's <c>unc.json</c>, nothing written: the share found; its root reached as its account (the preflight's sentence
/// when it is not); under <c>runas</c> the netonly token on every worker of a parallel loop — on a Mac (2026-10-11) where the share
/// is mounted and as whom instead; a listing and a content search with the share's budgets. Exit 0 only when every line passes.
/// Not part of the build gate: it needs a share. A Mac's mount is the run's, so it is unmounted as the check exits.
/// </summary>
internal static class UncCheck
{
    public static string IntroLine(string name) => $"UNC check: share '{name}' — nothing is written.";

    public static async Task<int> RunAsync(IAnsiConsole console, UncCatalog catalog, string name, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(name);
        console.MarkupLine(Markup.Escape(IntroLine(name)));
        var checks = new List<SmokeCheck>();
        var share = string.IsNullOrWhiteSpace(name) ? null : catalog.Find(name);
        if (share is null)
        {
            checks.Add(new SmokeCheck("unc:share", false, catalog.Shares.Count == 0 ? UncText.NoShares : UncText.UnknownShare(name, string.Join(", ", catalog.Shares.Select(s => s.Name)))));
            return Report(console, checks);
        }

        checks.Add(new SmokeCheck("unc:share", true, $"{share.Config.Root} as {UncAccess.Account(share)}{(share.Config.IsReadWrite ? ", readwrite" : ", read")}"));
        var access = new UncAccess(() => catalog, time);
        var reach = await access.RunAsync(share, write: false, files => files.List("", Files.WorkingDirectory.ProbeListLimit), cancellationToken).ConfigureAwait(false);
        if (reach.Error is { } refused)
        {
            checks.Add(new SmokeCheck("unc:reach", false, refused));
            return Report(console, checks);
        }

        checks.Add(new SmokeCheck("unc:reach", reach.Value!.Outcome == Files.FileOutcome.Ok, $"{reach.Value.Entries.Count.ToString(CultureInfo.InvariantCulture)} entries at the root{(reach.Value.Truncated ? " (more)" : "")} {reach.Value.Detail}".TrimEnd()));

        if (!OperatingSystem.IsWindows())
        {
            // A Mac (2026-10-11): no token to flow; the line says where the share is mounted and as whom, or that a folder is read as you.
            checks.Add(!share.Config.IsUnc
                ? new SmokeCheck("unc:mount", true, "a local folder: read as you, nothing mounted")
                : access.MountsInUse?.Describe(share) is { } mounted
                    ? new SmokeCheck("unc:mount", true, mounted)
                    : new SmokeCheck("unc:mount", false, "the share answered, but no mount of it is known"));
        }
        else
        {
            await Impersonation(access, share, checks, cancellationToken).ConfigureAwait(false);
        }

        var search = await access.RunAsync(share, write: false, files => files.Search("e", "", null, false, cancellationToken, limit: 5, maxDepth: 2), cancellationToken).ConfigureAwait(false);
        checks.Add(search.Error is { } searchError
            ? new SmokeCheck("unc:search", false, searchError)
            : new SmokeCheck("unc:search", search.Value!.Outcome == Files.FileOutcome.Ok, $"{search.Value.FilesSearched.ToString(CultureInfo.InvariantCulture)} files read, {search.Value.FilesMatched.ToString(CultureInfo.InvariantCulture)} matched, two levels deep{(search.Value.Budgeted ? ", stopped at the budget" : "")} {search.Value.Detail}".TrimEnd()));
        return Report(console, checks);
    }

    /// <summary>Windows' <c>unc:impersonation</c> line: under <c>runas</c> the netonly token on every worker of a parallel loop.</summary>
    private static async Task Impersonation(UncAccess access, UncNamedShare share, List<SmokeCheck> checks, CancellationToken cancellationToken)
    {
        var flow = await access.RunAsync(share, write: false, _ =>
        {
            var seen = new ConcurrentBag<(int Thread, bool Impersonating)>();
            Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
            {
                Thread.Sleep(1);
                seen.Add((Environment.CurrentManagedThreadId, OperatingSystem.IsWindows() && WindowsIdentity.GetCurrent(ifImpersonating: true) is not null));
            });
            return seen.ToList();
        }, cancellationToken).ConfigureAwait(false);
        if (flow.Error is { } flowError)
        {
            checks.Add(new SmokeCheck("unc:impersonation", false, flowError));
        }
        else
        {
            var seen = flow.Value!;
            int threads = seen.Select(s => s.Thread).Distinct().Count();
            bool all = seen.All(s => s.Impersonating);
            checks.Add(share.Config.IsRunAs
                ? new SmokeCheck("unc:impersonation", all, all ? $"netonly token on {threads.ToString(CultureInfo.InvariantCulture)} worker threads" : "a parallel worker ran without the netonly token")
                : new SmokeCheck("unc:impersonation", true, "windows: as you, no token"));
        }
    }

    private static int Report(IAnsiConsole console, IReadOnlyList<SmokeCheck> checks)
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
            ? Theme.ColorMarkup(Theme.Good, $"UNC CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"UNC CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
