using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// UNC shares on a Mac (2026-10-11): the paths a share may name on either OS (<see cref="UncPath"/>), the mounts' rules
/// (<see cref="MacMountRules"/>), the run's mounts over a fake system (<see cref="ShareMounts"/>) — one per share and account, a
/// runas mount that lands as someone else refused, a Finder mount used and left be, a slow mount still taken at exit, the sweep
/// taking only the very mount an app that is gone left, on every OS (a share's root read as a Mac spells it) — and, on a Mac,
/// <see cref="UncAccess"/> over them.
/// </summary>
public sealed class UncMacTests : IDisposable
{
    private const uint Uid = 504;
    private const int Owner = 4242;
    private const long OwnerStart = 638_000_000_000_000_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly FakeMountSystem _system;
    private readonly Dictionary<int, long> _alive = new() { [Environment.ProcessId] = OwnerStart + 1 };

    public UncMacTests()
    {
        _home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(_home);
        _system = new FakeMountSystem(Path.Combine(_dir, "Volumes"), Uid);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ShareMounts Mounts(TimeSpan? wait = null, TimeSpan? exitWait = null) =>
        new(_system, _home, Uid, pid => _alive.TryGetValue(pid, out long ticks) ? ticks : null, wait ?? TimeSpan.FromSeconds(10), exitWait);

    private static UncNamedShare Share(string name, string path, string? access = null) => new(name, new UncShareConfig { Path = path, Access = access }, "test");

    private static UncNamedShare RunAs(string name, string path, string user, string? access = null) =>
        new(name, new UncShareConfig { Path = path, Auth = "runas", User = user, Access = access }, "test");

    private string RecordsFolder => Path.Combine(_home, MacMountRules.FolderName);

    // ── the spelling ──

    [Fact]
    public void OnAMac_ANetworkShare_IsSmbAndEveryFormReadsAsIt_ALocalFolderIsAPosixPath()
    {
        Assert.Equal("smb://fs01/eng/specs", UncPath.Spell(@"\\fs01\eng\specs", windows: false));
        Assert.Equal("smb://fs01/eng/specs", UncPath.Spell("//fs01/eng/specs", windows: false));
        Assert.Equal("smb://fs01/eng/specs", UncPath.Spell(" SMB://fs01/eng/specs ", windows: false));
        Assert.Equal("/Users/Shared/Data", UncPath.Spell("/Users/Shared/Data", windows: false));
        Assert.Equal(@"sub\a.txt", UncPath.Spell(@"sub\a.txt", windows: false));   // a relative path keeps its backslash: a character there

        Assert.True(UncPath.IsAbsolute("smb://fs01/eng", windows: false));
        Assert.True(UncPath.IsAbsolute("/Volumes/x", windows: false));
        Assert.False(UncPath.IsAbsolute(@"D:\Data", windows: false));
        Assert.True(UncPath.IsNetwork("smb://fs01/eng", windows: false));
        Assert.False(UncPath.IsNetwork("/Volumes/eng", windows: false));

        Assert.Equal("smb://fs01/eng/b", UncPath.Normalize("smb://fs01/eng/./a/../b/", windows: false));
        Assert.Equal("smb://fs01:445/eng", UncPath.Normalize("smb://fs01:445/eng", windows: false));
        Assert.Throws<ArgumentException>(() => UncPath.Normalize("smb://fs01/eng/../fin", windows: false));
        Assert.Equal(("fs01", "Name’s Public Folder", "a/b"), UncPath.Split("smb://fs01/Name’s Public Folder/a/b"));
        Assert.Null(UncPath.Split("smb://fs01"));
    }

    [Fact]
    public void OnWindows_TheSpellingIsAsBefore_AndAMacsSmbReadsAsUnc()
    {
        Assert.Equal(@"\\fs01\eng\specs", UncPath.Spell("//fs01/eng/specs", windows: true));
        Assert.Equal(@"\\fs01\eng", UncPath.Spell("smb://fs01/eng", windows: true));
        Assert.Equal(@"D:\Data", UncPath.Spell("D:/Data", windows: true));
        Assert.True(UncPath.IsAbsolute(@"D:\Data", windows: true));
        Assert.True(UncPath.IsAbsolute(@"\\fs01\eng", windows: true));
        Assert.False(UncPath.IsAbsolute("/Users/x", windows: true));
        Assert.Null(UncShareConfig.PathProblem("smb://fs01/eng", windows: true));
    }

    [Fact]
    public void OnAMac_ThePathProblems_NameTheMacsForms()
    {
        Assert.Null(UncShareConfig.PathProblem("smb://fs01/eng", windows: false));
        Assert.Null(UncShareConfig.PathProblem(@"\\fs01\eng\specs", windows: false));
        Assert.Null(UncShareConfig.PathProblem("/Users/Shared/Data", windows: false));
        Assert.Equal(UncText.NoPath, UncShareConfig.PathProblem("  ", windows: false));
        Assert.Equal(UncText.DriveNotHere(@"D:\Data"), UncShareConfig.PathProblem(@"D:\Data", windows: false));
        Assert.Equal(UncText.DevicePathHere(@"\\?\C:\data"), UncShareConfig.PathProblem(@"\\?\C:\data", windows: false));
        Assert.Equal(UncText.NoShareNameHere("smb://fs01"), UncShareConfig.PathProblem(@"\\fs01", windows: false));
        Assert.Equal(UncText.NotAbsoluteHere("data/reports"), UncShareConfig.PathProblem("data/reports", windows: false));
        Assert.Equal(UncText.WholeDisk, UncShareConfig.PathProblem("/", windows: false));
        Assert.Equal(UncText.BadPath("smb://fs01/eng/../x"), UncShareConfig.PathProblem("smb://fs01/eng/../x", windows: false));

        Assert.Equal(
            "\"path\" is 'D:\\Data', a drive path, which only Windows has; on a Mac give the share as smb://server/share or the folder as /folder",
            UncText.DriveNotHere(@"D:\Data"));
    }

    [Fact]
    public void ARunAsAccount_IsWindowsFormOnWindows_AndAnyNameOnAMac()
    {
        Assert.Null(UncShareConfig.RunAsAccountProblem(@"CORP\svc", windows: true));
        Assert.Null(UncShareConfig.RunAsAccountProblem("svc@corp.local", windows: true));
        Assert.Equal(Sql.SqlText.RunAsNeedsDomain("svc"), UncShareConfig.RunAsAccountProblem("svc", windows: true));
        Assert.Null(UncShareConfig.RunAsAccountProblem("svc", windows: false));
        Assert.Null(UncShareConfig.RunAsAccountProblem("svc@CORP.LOCAL", windows: false));
        Assert.Null(UncShareConfig.RunAsAccountProblem(@"CORP\svc", windows: false));
        Assert.Equal(UncText.BadAccountHere("s vc"), UncShareConfig.RunAsAccountProblem("s vc", windows: false));
        Assert.Equal(UncText.RunAsNoUserHere, UncShareConfig.RunAsAccountProblem(" ", windows: false));
        Assert.Equal(UncText.RunAsNoUser, UncShareConfig.RunAsAccountProblem(" ", windows: true));
    }

    /// <summary>Finder's Connect to Server hands out <c>My%20Share</c> (2026-10-11, code review): decoded once, segment by segment, before the fold.</summary>
    [Fact]
    public void APercentEncodedPath_IsDecodedOnce_AndNoSpellingClimbsOutOfTheShare()
    {
        Assert.Equal(("fs01", "My Share", "Q3 Docs/a b"), UncPath.Split("smb://fs01/My%20Share/Q3%20Docs/a%20b"));
        Assert.Equal(("fs01:445", "eng", ""), UncPath.Split("smb://fs01:445/eng"));
        Assert.Throws<ArgumentException>(() => UncPath.Split("smb://fs01/eng/%2E%2E"));
        Assert.Equal(("fs01", "eng", "b"), UncPath.Split("smb://fs01/eng/a/%2e%2e/b"));
        Assert.Null(UncPath.Split("smb://fs01/eng/a%2Fb"));
        Assert.Null(UncPath.Split("smb://fs01/a%2F..%2Fother"));
        Assert.Null(UncPath.Split("smb://fs01/%2E%2E"));

        // A root keeps a % as %25, so splitting it again names the same share.
        Assert.Equal("smb://fs01/My Share/Q3 Docs", UncPath.Normalize("smb://fs01/My%20Share/Q3%20Docs", windows: false));
        Assert.Equal("smb://fs01/100%25/a%2541", UncPath.Normalize("smb://fs01/100%25/a%2541", windows: false));
        Assert.Equal(("fs01", "100%", "a%41"), UncPath.Split(UncPath.Normalize("smb://fs01/100%25/a%2541", windows: false)));

        Assert.Equal(@"\\fs01\My Share\Q3 Docs", UncPath.Spell("smb://fs01/My%20Share/Q3%20Docs", windows: true));
    }

    // ── the mounts' rules ──

    [Fact]
    public void TheMountsRules_ArePinned()
    {
        Assert.Equal("smb://fs01/Name%E2%80%99s%20Public%20Folder", MacMountRules.MountUrl("fs01", "Name’s Public Folder"));
        Assert.Equal("fs01|eng||ro", MacMountRules.Key("FS01", "Eng", null, readOnly: true));
        Assert.Equal("fs01|eng|svc@corp|rw", MacMountRules.Key("fs01", "eng", " SVC@corp ", readOnly: false));

        Assert.Equal("svc", MacMountRules.FromUser("//svc@fs01/eng"));
        Assert.Equal("CORP;svc", MacMountRules.FromUser("//CORP%3Bsvc@fs01/eng"));
        Assert.Null(MacMountRules.FromUser("//fs01/eng"));
        Assert.Null(MacMountRules.FromUser("/dev/disk3s1"));

        Assert.True(MacMountRules.IsSignedInAs("//svc@fs01/eng", "svc"));
        Assert.True(MacMountRules.IsSignedInAs("//svc@fs01/eng", "svc@CORP.LOCAL"));
        Assert.True(MacMountRules.IsSignedInAs("//svc@fs01/eng", @"CORP\svc"));
        Assert.True(MacMountRules.IsSignedInAs("//CORP%3Bsvc@fs01/eng", @"CORP\SVC"));
        Assert.False(MacMountRules.IsSignedInAs("//jdoe@fs01/eng", "svc"));
        // The forms macOS recorded for a runas mount in the spike (2026-10-11, loopback, a local account): DOMAIN\name becomes DOMAIN;name, a name@x is kept percent-encoded.
        Assert.True(MacMountRules.IsSignedInAs("//WORKGROUP;nstest@127.0.0.1/Public", @"WORKGROUP\nstest"));
        Assert.True(MacMountRules.IsSignedInAs("//nstest%40127.0.0.1@127.0.0.1/Public", "nstest@127.0.0.1"));
        Assert.True(MacMountRules.IsSignedInAs("//nstest@127.0.0.1/Public", "nstest"));
        Assert.False(MacMountRules.IsSignedInAs("//com.apple.idms.appleid.prd.0-1@127.0.0.1/Public", "nstest"));
        Assert.False(MacMountRules.IsSignedInAs("//fs01/eng", "svc"));

        Assert.Equal("00000000000000ff.txt", MacMountRules.RecordName(255));
    }

    [Fact]
    public void ARecord_RoundTrips_AndAnythingElseIsNone()
    {
        var record = new MountRecord(Owner, OwnerStart, "/Volumes/Name’s Folder-1", -5, "//svc@fs01/Name%E2%80%99s%20Folder");
        string text = MacMountRules.Format(record);

        Assert.Equal("owner 4242 638000000000000000\nfsid -5\nfrom //svc@fs01/Name%E2%80%99s%20Folder\non /Volumes/Name’s Folder-1\n", text);
        Assert.Equal(record, MacMountRules.Parse(text));
        Assert.Null(MacMountRules.Parse("owner 4242 1\nfsid 5\n"));

        // The owner line is RecordFiles.Line's and is read by RecordFiles.Pairs (2026-10-20, the code review): in any order, CRLF too, the last kept.
        Assert.StartsWith(NeonSidekick.Files.RecordFiles.Line("owner", Owner, OwnerStart), text, StringComparison.Ordinal);
        Assert.Equal(record, MacMountRules.Parse("owner 1 2\r\nfsid -5\r\nfrom //svc@fs01/Name%E2%80%99s%20Folder\r\non /Volumes/Name’s Folder-1\r\nowner 4242 638000000000000000\r\n"));
        Assert.Null(MacMountRules.Parse("owner 4242 x\nfsid -5\nfrom //a@b/c\non /Volumes/c\n"));
        Assert.Null(MacMountRules.Parse(""));
    }

    [Fact]
    public void TheSweep_TakesOnlyTheVeryMountAGoneAppLeft()
    {
        var record = new MountRecord(Owner, OwnerStart, "/Volumes/eng", 7, "//me@fs01/eng");
        var same = new MountInfo(7, Uid, MacMountRules.SmbType, "//me@fs01/eng", "/Volumes/eng");

        Assert.True(MacMountRules.SweepTakes(record, ownerAlive: false, same, Uid));
        Assert.False(MacMountRules.SweepTakes(record, ownerAlive: true, same, Uid));
        Assert.False(MacMountRules.SweepTakes(record, ownerAlive: false, null, Uid));
        Assert.False(MacMountRules.SweepTakes(record, ownerAlive: false, same with { Fsid = 8 }, Uid));          // remounted since: the user's
        Assert.False(MacMountRules.SweepTakes(record, ownerAlive: false, same with { Owner = 0 }, Uid));
        Assert.False(MacMountRules.SweepTakes(record, ownerAlive: false, same with { Type = "apfs" }, Uid));
        Assert.False(MacMountRules.SweepTakes(record, ownerAlive: false, same with { On = "/Volumes/eng-1" }, Uid));
    }

    [Fact]
    public void AMountsRefusal_IsNamedFromItsStatus_NeverTheMessage()
    {
        var share = Share("eng", @"\\fs01\eng");
        string root = share.Config.Root;
        Assert.Equal(UncText.RootMissing("eng", root), MacMountRules.Refusal(2, share, "me"));
        Assert.Equal(UncText.RootMissing("eng", root), MacMountRules.Refusal(-5998, share, "me"));
        Assert.Equal(UncText.AccessDenied("eng", root, "me"), MacMountRules.Refusal(13, share, "me"));
        Assert.Equal(UncText.Unreachable("eng", root, "no route to the server"), MacMountRules.Refusal(65, share, "me"));
        Assert.Equal(UncText.Unreachable("eng", root, "the connection timed out"), MacMountRules.Refusal(60, share, "me"));
        Assert.Equal(UncText.BadAccount("eng", "svc"), MacMountRules.Refusal(80, share, "svc"));
        Assert.Equal(UncText.AccountRefused("eng", "svc", "the password must be changed first"), MacMountRules.Refusal(-5045, share, "svc"));
        Assert.Equal(UncText.ReachFailed("eng", root, "the mount failed (-6600)"), MacMountRules.Refusal(-6600, share, "me"));
    }

    // ── the run's mounts ──

    [Fact]
    public void AShare_IsMountedOnce_ReadOnly_AndItsFolderIsUnderTheMount()
    {
        var mounts = Mounts();
        var specs = Share("specs", "smb://fs01/eng/specs");
        var whole = Share("eng", "smb://fs01/eng");

        var first = mounts.Root(specs, password: null);
        var second = mounts.Root(whole, password: null);

        Assert.Null(first.Error);
        Assert.Equal(Path.Combine(_system.Volumes, "eng", "specs"), first.Value);
        Assert.Equal(Path.Combine(_system.Volumes, "eng"), second.Value);
        Assert.Equal([("smb://fs01/eng", (string?)null, (string?)null, true, true)], _system.Mounts);   // a session of its own, always
        string record = Assert.Single(Directory.GetFiles(RecordsFolder));
        Assert.Equal(Path.Combine(_system.Volumes, "eng"), MacMountRules.Parse(File.ReadAllText(record))!.MountPoint);
        Assert.Equal(UncText.MountDescription(Path.Combine(_system.Volumes, "eng"), "//me@fs01/eng", ours: true, readOnly: true), mounts.Describe(whole));
    }

    [Fact]
    public void ARunAsShare_MountsAsItsAccount_InASessionOfItsOwn_AndAReadWriteShareIsAMountOfItsOwn()
    {
        var mounts = Mounts();
        Assert.Null(mounts.Root(Share("eng", "smb://fs01/eng"), password: null).Error);
        // Read-write only for a call that may write (2026-10-22, review H4: the share's access alone decided).
        var share = RunAs("fin", "smb://fs01/eng", "svc@CORP.LOCAL", "readwrite");
        var fin = mounts.Root(share, "s3cret", writable: true);

        Assert.Null(fin.Error);
        Assert.Equal(("smb://fs01/eng", "svc@CORP.LOCAL", "s3cret", false, true), _system.Mounts[1]);
        Assert.Equal(Path.Combine(_system.Volumes, "eng-1"), fin.Value);

        // The write's end lets the runas read-write mount go; a read gets a read-only one.
        mounts.EndWrite(share);
        Assert.Equal([(Path.Combine(_system.Volumes, "eng-1"), false)], _system.Unmounts);
        Assert.Null(mounts.Root(share, "s3cret").Error);
        Assert.True(_system.Mounts[2].ReadOnly);
    }

    [Fact]
    public void ARunAsMount_ThatLandsAsAnotherAccount_IsRefused_AndLetGo()
    {
        var mounts = Mounts();
        _system.LandsAs = "jdoe";

        var fin = mounts.Root(RunAs("fin", "smb://fs01/fin", "svc"), "s3cret");

        Assert.Equal(UncText.MountedAsOther("fin", "svc"), fin.Error);
        Assert.Single(_system.Unmounts);
        Assert.False(Directory.Exists(RecordsFolder) && Directory.GetFiles(RecordsFolder).Length > 0);
    }

    /// <summary>A share Finder mounted already: the app's mount is a session of its own beside it (measured: <c>…-1</c>), and only the app's goes at exit.</summary>
    [Fact]
    public void AFinderMount_GetsTheAppsOwnMountBesideIt_AndOnlyTheAppsGoesAtExit()
    {
        var finder = _system.Existing("eng", "//jdoe@fs01/eng");
        var mounts = Mounts();

        var eng = mounts.Root(Share("eng", "smb://fs01/eng"), password: null);
        mounts.UnmountAll();

        Assert.Equal(Path.Combine(_system.Volumes, "eng-1"), eng.Value);
        Assert.Equal([(Path.Combine(_system.Volumes, "eng-1"), false)], _system.Unmounts);
        Assert.NotNull(_system.Info(finder.On));
    }

    /// <summary>Should NetFS answer EEXIST under a new session anyway, the mount there is found by where it is from and used as you, never unmounted; a runas share refuses it.</summary>
    [Fact]
    public void AnEexist_FindsTheMountThere_UsedAsYouOnly_AndLeftAtExit()
    {
        // This user's own mount, signed in as them (2026-10-22, review H20: any mount of the share was taken, an admin's Finder mount too).
        var finder = _system.Existing("eng", "//" + Environment.UserName + "@fs01/eng", readOnly: true);
        _system.ExistsAnyway = true;
        var mounts = Mounts();

        var eng = mounts.Root(Share("eng", "smb://fs01/eng"), password: null);
        var fin = mounts.Root(RunAs("fin", "smb://fs01/eng", "svc"), "s3cret");
        mounts.UnmountAll();

        Assert.Equal(finder.On, eng.Value);
        Assert.Equal(UncText.MountedAsOther("fin", "svc"), fin.Error);
        Assert.Empty(_system.Unmounts);
        Assert.NotNull(_system.Info(finder.On));
        Assert.False(Directory.Exists(RecordsFolder) && Directory.GetFiles(RecordsFolder).Length > 0);
    }

    /// <summary>On EEXIST, a mount signed in as someone else is never taken as you (2026-10-22, review H20).</summary>
    [Fact]
    public void AnEexist_NeverTakesAMountSignedInAsSomeoneElse_AsYou()
    {
        _system.Existing("eng", "//admin@fs01/eng", readOnly: true);
        _system.ExistsAnyway = true;
        var eng = Share("eng", "smb://fs01/eng");

        Assert.Equal(UncText.ReachFailed("eng", eng.Config.Root, UncText.MountStatus(17)), Mounts().Root(eng, password: null).Error);
        var mine = new MountInfo(1, Uid, MacMountRules.SmbType, "//" + Environment.UserName + "@fs01/eng", "/Volumes/eng");
        Assert.True(MacMountRules.IsOwnSignIn(mine, Uid, Environment.UserName));
        Assert.True(MacMountRules.IsOwnSignIn(mine with { From = "//fs01/eng" }, Uid, Environment.UserName));
        Assert.False(MacMountRules.IsOwnSignIn(mine with { From = "//admin@fs01/eng" }, Uid, Environment.UserName));
        Assert.False(MacMountRules.IsOwnSignIn(mine with { Owner = Uid + 1 }, Uid, Environment.UserName));
    }

    /// <summary>An EEXIST mount is taken only with the share's read-only flag, and never one of the run's own under another account (2026-10-11, code review).</summary>
    [Fact]
    public void AnEexist_NeverTakesAMountOfTheOtherFlag_NorTheRunsOwnRunAsMount()
    {
        _system.Existing("eng", "//jdoe@fs01/eng");   // Finder's, writable
        _system.ExistsAnyway = true;
        var mounts = Mounts();

        var eng = Share("eng", "smb://fs01/eng");
        var read = mounts.Root(eng, password: null);

        Assert.Equal(UncText.ReachFailed("eng", eng.Config.Root, UncText.MountStatus(17)), read.Error);   // the root as this OS spells it

        // The run's own runas mount of the share (made before EEXIST began), then an as-you share of the same flag: not taken.
        var other = new FakeMountSystem(Path.Combine(_dir, "Volumes2"), Uid);
        var mounts2 = new ShareMounts(other, Path.Combine(_dir, "home2"), Uid, pid => _alive.TryGetValue(pid, out long ticks) ? ticks : null, TimeSpan.FromSeconds(10));
        Assert.Null(mounts2.Root(RunAs("fin", "smb://fs01/eng", "svc"), "s3cret").Error);
        other.ExistsAnyway = true;
        var asYou = mounts2.Root(eng, password: null);
        Assert.Equal(UncText.ReachFailed("eng", eng.Config.Root, UncText.MountStatus(17)), asYou.Error);
    }

    /// <summary>A mount NetFS reports made that statfs cannot find at its point is unmounted again and refused, never left unrecorded (2026-10-11, code review).</summary>
    [Fact]
    public void AMountThatCannotBeFoundAtItsPoint_IsUnmountedAgain_AndRefused()
    {
        var mounts = Mounts();
        _system.Unreadable = true;

        var share = Share("eng", "smb://fs01/eng");
        var eng = mounts.Root(share, password: null);

        Assert.Equal(UncText.ReachFailed("eng", share.Config.Root, UncText.MountStatus(int.MinValue)), eng.Error);   // the root as this OS spells it
        Assert.Equal([(Path.Combine(_system.Volumes, "eng"), false)], _system.Unmounts);
        Assert.False(Directory.Exists(RecordsFolder) && Directory.GetFiles(RecordsFolder).Length > 0);
    }

    /// <summary>Finder's <c>My%20Share</c> is mounted as <c>My%20Share</c>, never <c>My%2520Share</c>, and its folder found under the mount (2026-10-11, code review).</summary>
    [Fact]
    public void APercentEncodedShare_IsMountedByItsName_AndItsFolderIsUnderTheMount()
    {
        var mounts = Mounts();

        var docs = mounts.Root(Share("docs", "smb://fs01/My%20Share/Q3%20Docs"), password: null);

        Assert.Equal("smb://fs01/My%20Share", Assert.Single(_system.Mounts).Url);
        Assert.Equal(Path.Combine(_system.Volumes, "My Share", "Q3 Docs"), docs.Value);
    }

    /// <summary>The runas password is asked only for a mount to be made (2026-10-11, code review), and its refusal is the call's.</summary>
    [Fact]
    public void ThePassword_IsAskedOnlyToMount_AndItsRefusalIsTheCalls()
    {
        var mounts = Mounts();
        var fin = RunAs("fin", "smb://fs01/fin", "svc");
        int asked = 0;

        Assert.Equal("no password", mounts.Root(fin, () => { asked++; return UncResult<string?>.Failed("no password"); }).Error);
        Assert.Empty(_system.Mounts);
        Assert.Null(mounts.Root(fin, () => { asked++; return UncResult<string?>.Ok("s3cret"); }).Error);
        Assert.Null(mounts.Root(fin, () => { asked++; return UncResult<string?>.Failed("gone from the Keychain"); }).Error);

        Assert.Equal(2, asked);
        Assert.Equal("s3cret", Assert.Single(_system.Mounts).Password);
    }

    /// <summary>
    /// After the exit's unmount nothing is mounted, and a mount still under way that lands later is unmounted as it lands, never
    /// kept or recorded (2026-10-11, code review).
    /// </summary>
    [Fact]
    public void AfterTheExit_NoMountIsBegun_AndOneLandingLateIsUnmounted()
    {
        var mounts = Mounts(TimeSpan.FromMilliseconds(50), exitWait: TimeSpan.FromMilliseconds(50));
        using var gate = new ManualResetEventSlim(false);
        _system.Gate = gate;
        var eng = Share("eng", "smb://fs01/eng");
        Assert.NotNull(mounts.Root(eng, password: null).Error);   // given up: still under way

        mounts.UnmountAll();
        gate.Set();
        SpinWait.SpinUntil(() => _system.Unmounts.Count == 1, TimeSpan.FromSeconds(10));

        Assert.Equal([(Path.Combine(_system.Volumes, "eng"), false)], _system.Unmounts);
        Assert.Empty(_system.SmbMounts());
        Assert.False(Directory.Exists(RecordsFolder) && Directory.GetFiles(RecordsFolder).Length > 0);
        Assert.Equal(UncText.MountsClosed("fin"), mounts.Root(Share("fin", "smb://fs01/fin"), password: null).Error);
        Assert.Single(_system.Mounts);
    }

    [Fact]
    public void ARecord_IsWrittenWhole_NothingHalfDoneLeftBeside()
    {
        var mounts = Mounts();
        Assert.Null(mounts.Root(Share("eng", "smb://fs01/eng"), password: null).Error);

        string record = Assert.Single(Directory.GetFiles(RecordsFolder));
        Assert.EndsWith(".txt", record, StringComparison.Ordinal);
        Assert.NotNull(MacMountRules.Parse(File.ReadAllText(record)));
    }

    [Fact]
    public void AReadAndAReadWriteShare_AsYou_EachGetTheirOwnMount()
    {
        var mounts = Mounts();

        var read = mounts.Root(Share("eng", "smb://fs01/eng"), password: null);
        // A readwrite share read is on the read-only mount (2026-10-22, review H4); only a call that may write mounts read-write.
        var readOfWritable = mounts.Root(Share("engw", "smb://fs01/eng", "readwrite"), password: null);
        var write = mounts.Root(Share("engw", "smb://fs01/eng", "readwrite"), password: null, writable: true);

        Assert.Equal(Path.Combine(_system.Volumes, "eng"), read.Value);
        Assert.Equal(Path.Combine(_system.Volumes, "eng"), readOfWritable.Value);
        Assert.Equal(Path.Combine(_system.Volumes, "eng-1"), write.Value);
        Assert.Equal([true, false], _system.Mounts.Select(m => m.ReadOnly));
    }

    [Fact]
    public void FindingAMountByWhereItIsFrom_IsPinned()
    {
        var mine = new MountInfo(1, Uid, MacMountRules.SmbType, "//jdoe@FS01/Name%E2%80%99s%20Folder", "/Volumes/x");
        var guest = new MountInfo(2, Uid, MacMountRules.SmbType, "//fs01/eng", "/Volumes/eng");

        Assert.Equal(mine, MacMountRules.Find([guest, mine], "fs01", "Name’s Folder"));
        Assert.Equal(guest, MacMountRules.Find([guest, mine], "fs01", "eng"));
        Assert.Null(MacMountRules.Find([guest, mine], "fs01", "en"));
        Assert.Null(MacMountRules.Find([guest, mine], "fs01", "eng", readOnly: true));
        Assert.Equal(guest, MacMountRules.Find([guest, mine], "fs01", "eng", readOnly: false));
        // Decoded before the compare (2026-10-22, review U14): the kernel's record need not escape !'()* as EscapeDataString does.
        var bob = new MountInfo(3, Uid, MacMountRules.SmbType, "//me@fs01/Bob's%20Files%20(2024)", "/Volumes/b");
        Assert.Equal(bob, MacMountRules.Find([bob], "fs01", "Bob's Files (2024)"));
        Assert.NotNull(MacMountRules.Find([bob with { From = "//me@fs01/Bob%27s%20Files%20%282024%29" }], "fs01", "Bob's Files (2024)"));
        Assert.Equal(("fs01", "eng"), MacMountRules.FromShare("//a@b@fs01/eng/"));
        Assert.Null(MacMountRules.FromShare("fs01/eng"));
    }

    [Fact]
    public void AFailedMount_IsItsSentence_AndAnEjectedOneIsMountedAgain()
    {
        var mounts = Mounts();
        _system.Status = 65;
        var eng = Share("eng", "smb://fs01/eng");
        Assert.Equal(UncText.Unreachable("eng", eng.Config.Root, "no route to the server"), mounts.Root(eng, password: null).Error);

        _system.Status = 0;
        string point = mounts.Root(Share("eng", "smb://fs01/eng"), password: null).Value!;
        _system.Eject(point);
        Assert.Null(mounts.Root(Share("eng", "smb://fs01/eng"), password: null).Error);

        Assert.Equal(3, _system.Mounts.Count);
        Assert.Single(Directory.GetFiles(RecordsFolder));   // the ejected mount's record went with it
    }

    [Fact]
    public void ASlowMount_IsGivenUpAsUnreachable_ButStillTakenAtExit()
    {
        var mounts = Mounts(TimeSpan.FromMilliseconds(50));
        using var gate = new ManualResetEventSlim(false);
        _system.Gate = gate;

        var eng = Share("eng", "smb://fs01/eng");
        var slow = mounts.Root(eng, password: null);
        Assert.Equal(UncText.Unreachable("eng", eng.Config.Root, UncText.TimedOut(TimeSpan.FromMilliseconds(50))), slow.Error);

        gate.Set();
        SpinWait.SpinUntil(() => Directory.Exists(RecordsFolder) && Directory.GetFiles(RecordsFolder).Length == 1, TimeSpan.FromSeconds(10));
        mounts.UnmountAll();

        Assert.Single(_system.Mounts);
        Assert.Equal([(Path.Combine(_system.Volumes, "eng"), false)], _system.Unmounts);
        Assert.Empty(Directory.GetFiles(RecordsFolder));
    }

    [Fact]
    public void AtExit_ABusyMountIsForced()
    {
        var mounts = Mounts();
        string point = mounts.Root(Share("eng", "smb://fs01/eng"), password: null).Value!;
        _system.UnmountErrno = 16;

        mounts.UnmountAll();

        Assert.Equal([(point, false), (point, true)], _system.Unmounts);
        Assert.Null(_system.Info(point));
        Assert.Empty(Directory.GetFiles(RecordsFolder));
    }

    [Fact]
    public void TheSweep_UnmountsWhatAGoneAppLeft_AndKeepsALiveOnesAndTheUsers()
    {
        Directory.CreateDirectory(RecordsFolder);
        var left = _system.Existing("left", "//me@fs01/left");
        var live = _system.Existing("live", "//me@fs01/live");
        var remounted = _system.Existing("again", "//me@fs01/again");
        File.WriteAllText(Path.Combine(RecordsFolder, MacMountRules.RecordName(left.Fsid)), MacMountRules.Format(new MountRecord(Owner, OwnerStart, left.On, left.Fsid, left.From)));
        File.WriteAllText(Path.Combine(RecordsFolder, MacMountRules.RecordName(live.Fsid)), MacMountRules.Format(new MountRecord(Environment.ProcessId, OwnerStart + 1, live.On, live.Fsid, live.From)));
        File.WriteAllText(Path.Combine(RecordsFolder, "0000000000000999.txt"), MacMountRules.Format(new MountRecord(Owner, OwnerStart, remounted.On, 999, remounted.From)));
        File.WriteAllText(Path.Combine(RecordsFolder, "junk.txt"), "not a record");

        var taken = Mounts().Sweep();

        Assert.Equal([left.On], taken);
        Assert.Null(_system.Info(left.On));
        Assert.NotNull(_system.Info(live.On));
        Assert.NotNull(_system.Info(remounted.On));
        Assert.Equal([MacMountRules.RecordName(live.Fsid)], Directory.GetFiles(RecordsFolder).Select(Path.GetFileName));
    }

    // ── UncAccess on a Mac ──

    [UnixFact]
    public async Task OnAMac_ANetworkShare_IsReadUnderItsMount_ByAFullPathInEitherSpelling()
    {
        var mounts = Mounts();
        string specs = Path.Combine(_system.Volumes, "eng", "specs");
        _system.Existing("eng", "//me@fs01/eng", readOnly: true);
        _system.ExistsAnyway = true;   // the files are in the mount made here
        Directory.CreateDirectory(specs);
        File.WriteAllText(Path.Combine(specs, "a.txt"), "alpha needle");
        var share = Share("specs", @"\\fs01\eng\specs");
        var access = new UncAccess(() => new UncCatalog([share], []), TimeProvider.System, mounts);

        var located = access.Resolve(null, @"\\fs01\eng\specs\a.txt", out string relative, out string? error);
        var read = await access.RunAsync(located!, write: false, files => files.ReadText(relative, null, null), CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("smb://fs01/eng/specs", share.Config.Root);
        Assert.True(share.Config.IsUnc);
        Assert.Equal("a.txt", relative);
        Assert.Null(read.Error);
        Assert.Contains("alpha needle", read.Value!.Text);
        Assert.Null(access.Resolve("specs", "smb://fs01/eng/other/a.txt", out _, out string? outside));
        Assert.Equal(UncText.NotInShare("smb://fs01/eng/other/a.txt", share), outside);
        Assert.Equal(".", UncAccess.ForFiles(share, "//fs01/eng/specs"));
    }

    [UnixFact]
    public async Task OnAMac_ALocalFolder_IsReadAsYou_AndANetworkShareWithoutMountsIsRefused()
    {
        string data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "b.txt"), "beta");
        var local = Share("data", data);
        var network = Share("eng", "smb://fs01/eng");
        var access = new UncAccess(() => new UncCatalog([local, network], []), TimeProvider.System, mounts: null);
        UncAccess.Mounts = null;

        var listed = await access.RunAsync(local, write: false, files => files.List(""), CancellationToken.None);
        var refused = await access.RunAsync(network, write: false, files => files.List(""), CancellationToken.None);
        var located = access.Resolve(null, Path.Combine(data, "b.txt"), out string relative, out _);

        Assert.Null(listed.Error);
        Assert.Contains(listed.Value!.Entries, e => e.Name == "b.txt");
        Assert.Equal(UncText.NoMountsHere("eng"), refused.Error);
        Assert.Equal(local, located);
        Assert.Equal(Path.Combine(data, "b.txt"), relative);
        Assert.False(local.Config.IsUnc);
        Assert.Null(local.Config.Warning);
    }

    [UnixFact]
    public async Task OnAMac_ARunAsShare_MountsWithItsStoredPassword_AndSaysTheMountIsTheMachines()
    {
        var mounts = Mounts();
        var share = new UncNamedShare("fin", new UncShareConfig { Path = "smb://fs02/fin", Auth = "runas", User = "svc@CORP.LOCAL", Password = "plain-for-the-test" }, "test");
        var access = new UncAccess(() => new UncCatalog([share], []), TimeProvider.System, mounts);

        var listed = await access.RunAsync(share, write: false, files => files.List(""), CancellationToken.None);

        Assert.Null(listed.Error);
        Assert.Equal(("smb://fs02/fin", "svc@CORP.LOCAL", "plain-for-the-test", true, true), Assert.Single(_system.Mounts));
        Assert.Equal(UncText.RunAsMountedWarning("svc@CORP.LOCAL"), share.Config.Warning);
        Assert.Equal("svc@CORP.LOCAL", UncAccess.Account(share));
    }

    [UnixFact]
    public void OnAMac_AWindowsFile_Loads_ItsDriveEntrySkippedWithTheReason()
    {
        string file = Path.Combine(_dir, "unc.json");
        File.WriteAllText(file, """
            { "shares": {
                "eng": { "path": "\\\\fs01\\eng" },
                "data": { "path": "D:\\Data", "access": "readwrite" },
                "fin": { "path": "//fs02/fin", "auth": "runas", "user": "CORP\\svc", "password": "keychain:not-real" }
            } }
            """);

        var loaded = UncConfigFile.Load(file);

        Assert.Equal(["eng", "fin"], loaded.Shares.Select(s => s.Name));
        Assert.Equal("smb://fs01/eng", loaded.Shares[0].Config.Root);
        Assert.Equal("smb://fs02/fin", loaded.Shares[1].Config.Root);
        var problem = Assert.Single(loaded.Problems);
        Assert.Contains(UncText.DriveNotHere(@"D:\Data"), problem.Reason);
    }

    /// <summary>
    /// The native read on a real Mac (2026-10-11, code review): <c>f_flags</c> at 64 reads the sealed system volume <c>/</c> as
    /// read-only and the data volume as writable, and a mount point is found whatever its Unicode form.
    /// </summary>
    [MacFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void OnAMac_TheKernelsMounts_CarryTheReadOnlyFlag_AndAPointIsFoundInEitherForm()
    {
        var mounts = NetFSNative.Mounts();

        Assert.True(Assert.Single(mounts, m => m.On == "/").ReadOnly);
        Assert.False(Assert.Single(mounts, m => m.On == "/System/Volumes/Data").ReadOnly);
        Assert.NotNull(new MacMountSystem().Info("/System/Volumes/Data".Normalize(System.Text.NormalizationForm.FormD)));
    }
}
