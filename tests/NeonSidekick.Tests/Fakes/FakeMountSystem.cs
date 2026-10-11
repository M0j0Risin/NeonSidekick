using NeonSidekick.Unc;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A Mac's mounts without NetFS (2026-10-11, <see cref="ShareMounts"/>' tests): a mount is a folder under <see cref="Volumes"/>,
/// made and listed in memory; the status, a slow mount, a mount already there and the account it lands as are the test's to set.
/// </summary>
public sealed class FakeMountSystem : IMountSystem
{
    private readonly Dictionary<string, MountInfo> _mounted = new(StringComparer.Ordinal);
    private long _nextFsid = 100;

    public FakeMountSystem(string volumes, uint uid)
    {
        Volumes = volumes;
        Uid = uid;
        Directory.CreateDirectory(volumes);
    }

    /// <summary>The folder mounts land under (a temp stand-in for <c>/Volumes</c>).</summary>
    public string Volumes { get; }

    public uint Uid { get; }

    /// <summary>What the next mounts answer (0: mounted).</summary>
    public int Status { get; set; }

    /// <summary>Holds every mount until set, when not null.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    /// <summary>The user a mount's <c>f_mntfromname</c> names; the asked user (or <c>me</c>) when null.</summary>
    public string? LandsAs { get; set; }

    /// <summary>Every mount asked: the URL, user, password, read-only and new session.</summary>
    public List<(string Url, string? User, string? Password, bool ReadOnly, bool NewSession)> Mounts { get; } = [];

    /// <summary>Every unmount asked: the point and whether forced.</summary>
    public List<(string Point, bool Force)> Unmounts { get; } = [];

    /// <summary>Answers EEXIST for a share already mounted even under a new session (the fallback's case).</summary>
    public bool ExistsAnyway { get; set; }

    /// <summary>The errno the next plain unmounts answer (16: busy), forced ones always succeeding.</summary>
    public int UnmountErrno { get; set; }

    /// <summary>Puts a mount there as if Finder had made it: the folder, its file system, owned by <paramref name="owner"/>.</summary>
    public MountInfo Existing(string name, string from, uint? owner = null)
    {
        lock (_mounted)
        {
            string point = Path.Combine(Volumes, name);
            Directory.CreateDirectory(point);
            var info = new MountInfo(_nextFsid++, owner ?? Uid, MacMountRules.SmbType, from, point);
            _mounted[point] = info;
            return info;
        }
    }

    /// <summary>Drops a mount behind the app's back (an eject in Finder).</summary>
    public void Eject(string point)
    {
        lock (_mounted)
        {
            _mounted.Remove(point);
        }
    }

    public int Mount(string url, string? user, string? password, bool readOnly, bool newSession, out string? mountPoint)
    {
        Gate?.Wait(TimeSpan.FromSeconds(30));
        lock (_mounted)
        {
            Mounts.Add((url, user, password, readOnly, newSession));
            mountPoint = null;
            if (Status != 0)
            {
                return Status;
            }

            string share = Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
            string server = url[UncPath.SmbScheme.Length..url.LastIndexOf('/')];
            string from = "//" + (LandsAs ?? user ?? "me") + "@" + server + "/" + Uri.EscapeDataString(share);
            var same = _mounted.Values.FirstOrDefault(m => m.From.EndsWith("@" + server + "/" + Uri.EscapeDataString(share), StringComparison.OrdinalIgnoreCase));
            if (same is not null && (!newSession || ExistsAnyway))
            {
                return 17;   // EEXIST and no mount point, as NetFS answers a plain mount of a mounted share (measured 2026-10-11)
            }

            string point = Path.Combine(Volumes, share);
            for (int n = 1; _mounted.ContainsKey(point); n++)
            {
                point = Path.Combine(Volumes, share + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            Directory.CreateDirectory(point);
            _mounted[point] = new MountInfo(_nextFsid++, Uid, MacMountRules.SmbType, from, point);
            mountPoint = point;
            return 0;
        }
    }

    public MountInfo? Info(string path)
    {
        lock (_mounted)
        {
            return _mounted.TryGetValue(path, out var info) ? info : null;
        }
    }

    public IReadOnlyList<MountInfo> SmbMounts()
    {
        lock (_mounted)
        {
            return [.. _mounted.Values];
        }
    }

    public int Unmount(string mountPoint, bool force)
    {
        lock (_mounted)
        {
            Unmounts.Add((mountPoint, force));
            if (!force && UnmountErrno != 0)
            {
                return UnmountErrno;
            }

            _mounted.Remove(mountPoint);
            return 0;
        }
    }
}
