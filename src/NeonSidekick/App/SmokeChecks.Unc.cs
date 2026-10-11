using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using NeonSidekick.Files;
using NeonSidekick.Sql;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>unc:impersonation</c> (2026-09-30, the UNC tools): what a <c>runas</c> share's call does, on the published binary, with
    /// no share. A netonly token (<see cref="WindowsCredentials.LogonNetOnly"/>, for an account that need not exist — Windows checks
    /// it only at a network sign-in) is impersonated around a <see cref="WorkingDirectoryOptions.Share"/> sandbox over a temp
    /// folder: a parallel loop must see the impersonation on every worker thread (.NET flows it through the execution context —
    /// the UNC tools' content search leans on that), then the share's listing and its four-reader search must read the folder.
    /// No new native library: advapi32 is <c>sql:credentials</c>' already; this line proves the flow and the share's options.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeUnc()
    {
        const string name = "unc:impersonation";
        string folder = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke", "unc-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            for (int i = 0; i < 8; i++)
            {
                File.WriteAllText(Path.Combine(folder, $"f{i}.txt"), "smoke needle");
            }

            using var token = WindowsCredentials.LogonNetOnly(@"NEONSIDEKICK-SMOKE\nobody", "smoke", out string? logonError);
            if (token is null)
            {
                return new SmokeCheck(name, false, "NEW_CREDENTIALS logon failed: " + logonError);
            }

            var share = new WorkingDirectory(() => folder, TimeProvider.System, WorkingDirectoryOptions.Share);
            return WindowsIdentity.RunImpersonated(token, () =>
            {
                var flags = new ConcurrentBag<(int Thread, bool Impersonating)>();
                Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
                {
                    Thread.Sleep(1);
                    flags.Add((Environment.CurrentManagedThreadId, WindowsIdentity.GetCurrent(ifImpersonating: true) is not null));
                });
                if (flags.Any(f => !f.Impersonating))
                {
                    return new SmokeCheck(name, false, "a parallel worker ran without the netonly token");
                }

                var list = share.List("");
                var search = share.Search("needle", "", null, false, CancellationToken.None, limit: 50);
                int threads = flags.Select(f => f.Thread).Distinct().Count();
                return list.Outcome == FileOutcome.Ok && list.Entries.Count == 8 && search.FilesMatched == 8
                    ? new SmokeCheck(name, true, $"netonly token on {threads} worker threads; the share sandbox listed 8 and searched 8")
                    : new SmokeCheck(name, false, $"the share sandbox read {list.Outcome} {list.Entries.Count} entries, {search.Outcome} {search.FilesMatched} matches {list.Detail}{search.Detail}");
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SmokeCheck(name, false, ex.Message);
        }
        finally
        {
            Files.WorkingDirectory.DeleteDirectoryQuietly(folder);
        }
    }

    /// <summary>
    /// <c>unc:netfs</c> (2026-10-11, UNC shares on a Mac): NetFS, <c>getfsstat</c>, <c>unmount</c> and <c>geteuid</c> bound on the
    /// published binary, the mount's URLs built through CoreFoundation, the mounted file systems listed (the root's among them)
    /// and an unmount of a path that is no mount answered — nothing is mounted, nothing asked of a server. No library ships: all
    /// of it is the system's.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static SmokeCheck ProbeUncNetFs()
    {
        const string name = "unc:netfs";
        try
        {
            if (Unc.NetFSNative.Probe() is { } failed)
            {
                return new SmokeCheck(name, false, failed);
            }

            var mounts = Unc.NetFSNative.Mounts();
            uint uid = Unc.MacMountSystem.UserId;
            return mounts.Any(m => m.On == "/")
                ? new SmokeCheck(name, true, $"bound; {mounts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} file systems mounted, {mounts.Count(m => m.Type == Unc.MacMountRules.SmbType).ToString(System.Globalization.CultureInfo.InvariantCulture)} SMB; uid {uid.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
                : new SmokeCheck(name, false, "getfsstat listed no root file system");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException or InvalidOperationException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
