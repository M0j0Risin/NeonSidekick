#:project ../src/NeonSidekick/NeonSidekick.csproj
#:property PublishAot=false

// UpdateSim: the whole update flow on one machine, against a fake GitHub on loopback, away from the real install and profile (2026-11-04, self-update).
//
//     .\build.ps1 -Publish -Package -UpdateSim          the simulation build's package, whose exe knows the --update-* flags
//     dotnet run tools/UpdateSim.cs                     every unattended scenario, on that package
//     dotnet run tools/UpdateSim.cs -- --only rollback,kill-swap --package <folder> --scratch <folder> --keep
//     dotnet run tools/UpdateSim.cs -- --serve          the by-hand run: keeps the fake GitHub up and opens the real interface over it, for the marker, its clicks and a window close
//     dotnet run tools/UpdateSim.cs -- --serve --move-all   the same with every file moving, the exe too: a window close's worst case
//
// Each scenario gets a fresh copy of the package as its install folder and a scratch NEONSIDEKICK_HOME. The key pair is new each run, so no private key is ever committed to this
// public repo. The server is a hand-served TcpListener, as the repo's other loopback servers are: it answers range requests with a 206 and redirects assets once, as GitHub does.
//
// What this can't prove: the shipped build, the real GitHub API and the real key (the plan's last Verification step), and what's only on the screen (the marker, its clicks, a window close: by hand).
// Afterward publish/output holds the test build: publish normally before daily use.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeonSidekick.Updates;

string? packageArgument = null;
string? scratchArgument = null;
HashSet<string>? only = null;
bool keep = false;
bool serve = false;
bool moveAll = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--package" when i + 1 < args.Length:
            packageArgument = args[++i];
            break;
        case "--scratch" when i + 1 < args.Length:
            scratchArgument = args[++i];
            break;
        case "--only" when i + 1 < args.Length:
            only = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)];
            break;
        case "--keep":
            keep = true;
            break;
        case "--serve":
            serve = true;
            break;
        case "--move-all":
            moveAll = true;
            break;
        default:
            Console.Error.WriteLine($"Usage: dotnet run tools/UpdateSim.cs -- [--package <folder>] [--scratch <folder>] [--only a,b] [--keep] [--serve [--move-all]] (not {args[i]})");
            return 2;
    }
}

string package = packageArgument ?? Sim.NewestPackage();
string scratch = Path.Combine(scratchArgument ?? Path.GetTempPath(), "neon-update-sim-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(scratch);
using var key = UpdateSignature.NewKey();
using var github = new FakeGitHub();
var sim = new Sim(package, scratch, key, github);
Console.WriteLine($"package {package}, version {sim.Base}; scratch {scratch}; fake GitHub at {github.Origin}");

if (serve)
{
    sim.ServeMovesAll = moveAll;
    ByHand.Run(sim);
    if (!keep)
    {
        Sim.Delete(scratch);
    }

    return 0;
}

var scenarios = Scenarios.All(sim);
int failed = 0;
int ran = 0;
var clock = Stopwatch.StartNew();
foreach (var (name, run) in scenarios)
{
    if (only is not null && !only.Contains(name))
    {
        continue;
    }

    ran++;
    var one = Stopwatch.StartNew();
    try
    {
        sim.Begin(name);
        await run();
        Console.WriteLine($"  PASS {name} ({one.Elapsed.TotalSeconds:F1} s)");
    }
    catch (SkipScenario skip)
    {
        ran--;
        Console.WriteLine($"  SKIP {name}: {skip.Message}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  FAIL {name}: {ex.Message}");
        foreach (string line in sim.LastOutput)
        {
            Console.WriteLine($"       {line}");
        }
    }
    finally
    {
        sim.End();
    }
}

Console.WriteLine();
Console.WriteLine($"{ran - failed} of {ran} passed in {clock.Elapsed.TotalMinutes:F1} min.");
if (!keep)
{
    Sim.Delete(scratch);
}

return failed == 0 ? 0 : 1;

/// <summary>
/// The by-hand run (--serve, 2026-11-04, the user's call): what the scenarios can't see, on the real interface. One install folder and one scratch home; each start runs as the version the
/// folder holds, since one binary has one version, so the second start says "Updated to X". Only the files that differ move, the marker and the release's own, unless --move-all asks for every file, the exe too: what a window close's 4 s has to fit at worst
/// (2026-11-04: the first window close moved only the few, which fit easily and proved little about a real update's time).
/// </summary>
static class ByHand
{
    /// <summary>Long enough to see the marker's percentage and to try /update cancel in the middle.</summary>
    private const double DownloadSeconds = 45;

    private static readonly string Notes = """
        ## What's new in this simulated release

        These notes are long on purpose, so the pane has to scroll, as a real release's do.

        ### New
        * First new thing, with a line long enough to wrap in a narrow window, so you can see how a pane's text wraps when the notes run past the edge of the window it's in.
        * Second new thing.
        * Third new thing.

        ### Fixed
        * First fix.
        * Second fix.
        * Third fix.
        * Fourth fix.
        * Fifth fix.
        * Sixth fix.

        ### Safety
        * The next line holds a terminal color code, which should show as a plain mark and color nothing: {ESC}[31mstill plain text{ESC}[0m
        * [markup in brackets] should show as typed.

        ### More
        * Line 1 of more, to make it scroll.
        * Line 2 of more.
        * Line 3 of more.
        * Line 4 of more.
        * Line 5 of more.
        * Line 6 of more.
        * Line 7 of more.
        * Line 8 of more.
        * Line 9 of more.
        * Line 10 of more.
        * Line 11 of more.
        * Line 12 of more.
        * Line 13 of more.
        * Line 14 of more.
        * Line 15 of more.
        * Line 16 of more.
        * Line 17 of more.
        * Line 18 of more.
        * Line 19 of more.
        * Line 20 of more.
        * Line 21 of more.
        * Line 22 of more.

        ### Updating
        Use /update. These are the last lines, so seeing them means the pane scrolled all the way.
        """.Replace("{ESC}", "\u001b", StringComparison.Ordinal);

    public static void Run(Sim sim)
    {
        sim.Begin("serve");
        sim.GitHub.Notes = Notes;
        sim.GitHub.SendSeconds = DownloadSeconds;
        sim.GitHub.Log = line => Console.WriteLine("  fake GitHub: " + line);
        string latest = sim.Next;
        sim.Publish(latest);
        Console.WriteLine($"Published {latest}, signed by this run's key. The install is {sim.Folder.Install}");
        Console.WriteLine("The scratch home is new, so the app asks to connect a model first: Esc says not now, and then the \"is out\" line shows.");
        sim.OpenWindow();
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine(sim.ServeStatus(latest));
            Console.WriteLine("  Enter  start the app again, as the version the folder holds");
            Console.WriteLine("  n      publish a newer release");
            Console.WriteLine("  q      stop any copy still running from here, and delete it all (its window stays until you close it)");
            string? answer = Console.ReadLine()?.Trim();
            if (answer is null or "q")
            {
                break;
            }

            if (answer == "n")
            {
                latest = Sim.After(latest);
                sim.Publish(latest);
                Console.WriteLine($"Published {latest}. A start finds it, and so does /update.");
            }
            else if (answer.Length == 0)
            {
                sim.OpenWindow();
            }
        }

        sim.StopServedCopies();
        sim.End();
    }
}

/// <summary>The non-interactive scenarios of the plan's table. The rest (the marker, double-clicks, a window close) are by hand.</summary>
static class Scenarios
{
    public static List<(string Name, Func<Task> Run)> All(Sim s) =>
    [
        ("move-all", async () =>
        {
            s.Publish(s.Next);
            var run = await s.Run("all", Sim.MoveAll);
            s.Says(run, "stage: ready " + s.Next, "exit: done");
            s.Has("sim-marker.txt");
            s.ManifestIs(s.Next);
            s.InPrevious(s.ExeName);
            var after = await s.Run("start", asVersion: s.Next);
            s.Says(after, "Updated to " + s.Next);
        }),
        ("difference-only", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("all"), "exit: done");
            s.Has("sim-marker.txt");
            s.NotInPrevious(s.ExeName);
        }),
        ("altered-file-replaced", async () =>
        {
            s.Publish(s.Next);
            s.AlterInstalled(s.SmallFile, "altered by hand");
            s.Says(await s.Run("all"), "exit: done");
            s.InstalledIs(s.SmallFile, s.BaseText(s.SmallFile));
            s.KeptIs(s.Base, s.SmallFile, "altered by hand");
            s.NotInPrevious(s.SmallFile);
        }),
        ("cancel-keeps-rollback", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("all"), "exit: done");
            s.Publish(s.AfterNext);
            s.Says(await s.Run("stage", asVersion: s.Next), "stage: ready " + s.AfterNext);
            s.Says(await s.Run("cancel", asVersion: s.Next), "cancel: done");
            s.Says(await s.Run("rollback", asVersion: s.Next), "rollback: to " + s.Base);
            s.ManifestIs(s.Base);
        }),
        ("newer-while-staged", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            s.Publish(s.AfterNext);
            s.Says(await s.Run("all"), "stage: ready " + s.AfterNext, "exit: done");
            s.ManifestIs(s.AfterNext);
        }),
        ("same-or-older", async () =>
        {
            foreach (string tag in new[] { "v" + s.Base, "v0.0.1" })
            {
                s.GitHub.Reset();
                s.GitHub.Latest = FakeGitHub.Json(tag, []);
                s.Says(await s.Run("all"), "check: Latest");
                s.ManifestIs(s.Base);
            }

            // A tag it can't read says nothing of what's newest (review P13-11).
            foreach (string tag in new[] { s.Next + "-beta", "v" + s.Next + "-beta" })
            {
                s.GitHub.Reset();
                s.GitHub.Latest = FakeGitHub.Json(tag, []);
                s.Says(await s.Run("all"), "check: Failed", UpdateText.TagNotPlain(tag));
                s.ManifestIs(s.Base);
            }
        }),
        ("unsigned", async () =>
        {
            s.Publish(s.Next);
            s.GitHub.Hide(".sig");
            s.Says(await s.Run("all"), "check: Unsigned");
            s.NoStage();
        }),
        ("wrong-signature", async () =>
        {
            using var other = UpdateSignature.NewKey();
            s.Publish(s.Next, signer: other);
            s.Refused(await s.Run("all"));
            s.ManifestIs(s.Base);
        }),
        ("altered-package", async () =>
        {
            s.Publish(s.Next, afterSign: archive => archive[archive.Length / 2] ^= 1);
            s.Refused(await s.Run("all"));
            s.ManifestIs(s.Base);
        }),
        ("replayed-older-signature", async () =>
        {
            s.Publish(s.Next, shaNames: "NeonSidekick-v" + s.Base + "-" + s.Rid + s.ArchiveExtension);
            s.Refused(await s.Run("all"));
            s.ManifestIs(s.Base);
        }),
        ("manifest-wrong-version", async () =>
        {
            s.Publish(s.Next, manifestVersion: s.AfterNext);
            s.Refused(await s.Run("all"));
            s.NoStage();
        }),
        ("manifest-unknown-format", async () =>
        {
            s.Publish(s.Next, format: 99);
            s.Refused(await s.Run("all"));
            s.NoStage();
        }),
        ("file-not-its-hash", async () =>
        {
            s.Publish(s.Next, tamper: entries => entries[s.SmallFile] = Encoding.UTF8.GetBytes("not what the manifest says"));
            s.Refused(await s.Run("all"));
            s.NoStage();
        }),
        ("rate-limited-and-friends", async () =>
        {
            foreach (var (status, word) in new[] { (403, UpdateText.RateLimited), (429, UpdateText.RateLimited), (404, UpdateText.NoReleaseFound) })
            {
                s.GitHub.Reset();
                s.GitHub.LatestStatus = status;
                s.Says(await s.Run("all"), "check: Failed", word);
            }

            s.GitHub.Reset();
            s.GitHub.Latest = "{ not json";
            s.Says(await s.Run("all"), "check: Failed", UpdateText.AnswerUnreadable);
        }),
        ("server-down", async () => s.Says(await s.Run("all", source: "http://127.0.0.1:9/"), "check: Failed")),
        ("quit-during-download", async () =>
        {
            s.Publish(s.Next);
            s.GitHub.SlowAfter = 0.3;
            var first = s.Start("stage");
            await s.Until(() => Directory.Exists(s.Folder.StagingParts) && s.GitHub.Sent(s.Archive(s.Next)) > 0, first);
            await Task.Delay(500);
            s.Kill(first);
            s.GitHub.SlowAfter = null;
            s.NothingInstalled();
            s.Says(await s.Run("all"), "stage: ready " + s.Next, "exit: done");
            s.True(s.GitHub.RangeRequests > 0, "the next try sent no range request, so it didn't resume");
        }),
        ("dropped-at-half", async () =>
        {
            s.Publish(s.Next);
            s.GitHub.DropAt = 0.5;
            s.True((await s.Run("stage")).Code != 0, "a dropped connection should fail the stage");
            s.GitHub.DropAt = null;
            s.Says(await s.Run("all"), "stage: ready " + s.Next);
            s.True(s.GitHub.RangeRequests > 0, "the next try sent no range request, so it didn't resume");
        }),
        ("stalled-connection", async () =>
        {
            s.Publish(s.Next);
            s.GitHub.StallAt = 0.5;
            var run = await s.Run("stage", limitSeconds: 120);
            s.Says(run, "stage: failed", UpdateText.Stalled);
            s.GitHub.StallAt = null;
            s.Says(await s.Run("all"), "stage: ready " + s.Next);
            s.True(s.GitHub.RangeRequests > 0, "the next try sent no range request, so it didn't resume");
        }),
        ("missing-library", async () =>
        {
            s.Publish(s.Next, tamper: entries => entries.Remove(s.NativeLibrary), manifestFromEntries: true);
            s.Refused(await s.Run("all"));
            s.NoStage();
            s.False(Directory.Exists(s.Folder.Staging), "a package missing a file is the package's fault, so it's dropped");
        }),
        ("garbage-library", async () =>
        {
            // In time, since a "Bad Image" box left open would hold the probe to its 30 s limit, over the user's chat.
            s.Publish(s.Next, tamper: entries => entries[s.NativeLibrary] = Encoding.UTF8.GetBytes("not a library"), manifestFromEntries: true);
            var clock = Stopwatch.StartNew();
            s.Refused(await s.Run("all"));
            s.True(clock.Elapsed < TimeSpan.FromSeconds(20), "a damaged library should fail at once, with no box to wait on");
            s.NoStage();
            s.StageKept();
            s.ManifestIs(s.Base);
        }),
        ("probe-crashes", async () =>
        {
            s.Publish(s.Next, tamper: entries => entries[Sim.CrashMarker] = [], manifestFromEntries: true);
            var clock = Stopwatch.StartNew();
            s.Refused(await s.Run("all"));
            s.True(clock.Elapsed < TimeSpan.FromSeconds(20), "a crash should fail at once, with no box to wait on");
            s.NoStage();
            s.StageKept();
        }),
        ("failed-probe-retries-with-no-download", async () =>
        {
            s.Publish(s.Next, tamper: entries => entries[Sim.CrashMarker] = [], manifestFromEntries: true);
            s.Refused(await s.Run("stage"));
            s.StageKept();
            long sent = s.GitHub.Sent(s.Archive(s.Next));
            s.Refused(await s.Run("stage"));
            s.True(s.GitHub.Sent(s.Archive(s.Next)) == sent, "a retry after a failed test run should download nothing");
            s.StageKept();
            s.Says(await s.Run("cancel"), "cancel: done");
            s.False(Directory.Exists(s.Folder.Staging), "/update cancel should delete a kept stage");
        }),
        ("probe-hangs", async () =>
        {
            s.Publish(s.Next, tamper: entries => entries[Sim.HangMarker] = [], manifestFromEntries: true);
            s.Refused(await s.Run("all", limitSeconds: 90));
            s.NoStage();
            s.StageKept();
        }),
        ("probe-hung-with-no-parent", async () =>
        {
            // The probe's own limit (review P10-10): the app's 30 s kill dies with the app, and a Mac has no job object, so nothing else ends it.
            File.WriteAllBytes(s.Folder.Full(Sim.HangMarker), []);
            var clock = Stopwatch.StartNew();
            var run = s.RunPlain(UpdateProbe.Flag, Path.Combine(s.Scratch, "probe-with-no-parent-" + s.Name));
            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(90)));
            s.True(finished == run, "a hung probe should end itself at its limit");
            s.True(clock.Elapsed >= UpdateProbe.SelfLimit - TimeSpan.FromSeconds(5), $"it ended after {clock.Elapsed.TotalSeconds:F0} s, before its limit, so something else ended it");
        }),
        ("library-fails-in-both", async () =>
        {
            s.Publish(s.Next, tamper: entries => entries[s.NativeLibrary] = Encoding.UTF8.GetBytes("not a library"), manifestFromEntries: true);
            s.AlterInstalled(s.NativeLibrary, "also not a library");
            var clock = Stopwatch.StartNew();
            s.Says(await s.Run("all"), "stage: ready " + s.Next, "exit: done");
            s.True(clock.Elapsed < TimeSpan.FromSeconds(25), "a damaged library should fail at once in both runs, with no box to wait on");
        }),
        ("kill-swap", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            var run = s.Start("exit", "--update-swap-delay", "300", "--update-move-all");
            await s.Until(() => File.Exists(s.Folder.Journal), run);
            await Task.Delay(1000);
            s.Kill(run);
            s.True(File.Exists(s.Folder.Journal), "the journal should survive the kill");
            s.Says(await s.Run("start", Sim.MoveAll), "start:");
            s.False(File.Exists(s.Folder.Journal), "the start should undo it");
            s.ManifestIs(s.Base);
            s.IntactAsBase();
            s.Says(await s.Run("exit", Sim.MoveAll), "exit: done");
            s.ManifestIs(s.Next);
        }),
        ("kill-swap-staged-file-gone", async () =>
        {
            // A vanished source's step reads as neither there (review P12-2): the undo goes on past it, and it must leave every listed file whole.
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            var run = s.Start("exit", "--update-swap-delay", "300", "--update-move-all");
            await s.Until(() => File.Exists(s.Folder.Journal), run);
            await Task.Delay(1000);
            s.Kill(run);
            File.Delete(Directory.EnumerateFiles(s.Folder.Staging, "*", SearchOption.AllDirectories).First(file => Path.GetFileName(file) != UpdateManifest.FileName));
            s.Says(await s.Run("start", Sim.MoveAll), "start:");
            s.False(File.Exists(s.Folder.Journal), "the start should undo it");
            s.ManifestIs(s.Base);
            s.IntactAsBase();
        }),
        ("package-unpacked-over-a-killed-swap", async () =>
        {
            // Both paths of the steps that ran are there now, which reads as a conflict, and the install is whole: the hash check lets the journal go (review P15-2).
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            var run = s.Start("exit", "--update-swap-delay", "300", "--update-move-all");
            await s.Until(() => File.Exists(s.Folder.Journal), run);
            await Task.Delay(1000);
            s.Kill(run);
            s.UnpackBaseOver();
            var started = await s.Run("start", Sim.MoveAll);
            s.True(!started.Text.Contains(UpdateText.MixedLine, StringComparison.Ordinal), "a whole install shouldn't read as a mix");
            s.False(File.Exists(s.Folder.Journal), "the journal should go");
            s.IntactAsBase();
        }),
        ("kill-undo", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            var swap = s.Start("exit", "--update-swap-delay", "300", "--update-move-all");
            await s.Until(() => File.Exists(s.Folder.Journal), swap);
            await Task.Delay(1500);
            s.Kill(swap);
            var undo = s.Start("start", "--update-swap-delay", "300", "--update-move-all");
            await Task.Delay(600);
            s.Kill(undo);
            s.True(File.Exists(s.Folder.Journal), "an interrupted undo keeps the journal");
            s.Says(await s.Run("start", Sim.MoveAll), "start:");
            s.False(File.Exists(s.Folder.Journal), "the start after it should finish the undo");
            s.IntactAsBase();
            s.Says(await s.Run("exit", Sim.MoveAll), "exit: done");
            s.ManifestIs(s.Next);
        }),
        ("locked-file", async () =>
        {
            s.OnWindowsOnly();
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            using (new FileStream(s.Folder.Full(s.SmallFile), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                s.Says(await s.Run("exit", Sim.MoveAll, limitSeconds: 60), "exit: done");
            }

            s.ManifestIs(s.Base);
            s.IntactAsBase();
            s.Says(await s.Run("disk"), "disk: ready " + s.Next);
        }),
        ("staged-file-changed", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            File.WriteAllText(Path.Combine(s.Folder.Staging, "sim-marker.txt"), "changed after it passed");
            s.Says(await s.Run("exit"), "exit: done");
            s.ManifestIs(s.Base);
            s.NoStage();
        }),
        ("staged-file-and-manifest-changed", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            string marker = Path.Combine(s.Folder.Staging, "sim-marker.txt");
            File.WriteAllText(marker, "changed after it passed");
            string manifest = s.Folder.StagingManifest;
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(s.MarkerHash(s.Next), UpdateHash.OfFile(marker), StringComparison.Ordinal));
            s.Says(await s.Run("exit"), "exit: done");
            s.ManifestIs(s.Base);
            s.NoStage();
        }),
        ("planted-ready", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            File.Delete(s.Folder.StagedSig);
            File.WriteAllText(s.Folder.Ready, "");
            s.Says(await s.Run("exit"), "exit: done");
            s.ManifestIs(s.Base);
            s.NoStage();
        }),
        ("killed-unpacking", async () =>
        {
            string left = s.Folder.Staging + "." + Guid.NewGuid().ToString("N") + ".extracting";
            Directory.CreateDirectory(left);
            File.WriteAllText(Path.Combine(left, "half"), "half");
            s.Says(await s.Run("start"), "start:");
            s.False(Directory.Exists(left), "the next start should remove the *.extracting folder");
        }),
        ("planted-journal", async () =>
        {
            Directory.CreateDirectory(s.Folder.Root);
            File.WriteAllText(s.Folder.Journal, "{ not json");
            s.Says(await s.Run("start"), "start:");
            s.False(File.Exists(s.Folder.Journal), "a journal that isn't JSON should be deleted");

            string outside = Path.Combine(s.Scratch, "outside-" + s.Name);
            File.WriteAllText(outside, "untouched");
            string outsideJson = JsonSerializer.Serialize(outside);
            File.WriteAllText(s.Folder.Journal, "{\"format\":1,\"source\":\"" + s.Base + "\",\"target\":\"" + s.Next + "\",\"steps\":[{\"kind\":\"move\",\"from\":" + outsideJson + ",\"to\":\"stolen\"}]}");
            s.Says(await s.Run("start"), "start:");
            s.False(File.Exists(s.Folder.Journal), "a journal with an outside path should be deleted");
            s.True(File.Exists(outside), "nothing outside the folder should move");
            s.ManifestIs(s.Base);
        }),
        ("second-copy-open", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            using (var open = s.OpenHeadless())
            {
                await open.Started;
                s.Says(await s.Run("exit"), UpdateText.OtherCopyOpen);
                s.ManifestIs(s.Base);
            }

            s.Says(await s.Run("exit"), "exit: done");
            s.ManifestIs(s.Next);
        }),
        ("second-copy-during-download", async () =>
        {
            s.Publish(s.Next);
            s.GitHub.StallAt = 0.2;
            var first = s.Start("stage");
            await s.Until(() => s.GitHub.Sent(s.Archive(s.Next)) > 0, first);
            await Task.Delay(500);
            s.Says(await s.Run("stage"), "stage: failed", UpdateText.AnotherCopyStaging);
            s.Kill(first);
            s.GitHub.StallAt = null;
        }),
        ("second-copy-finds-the-stage", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            s.Says(await s.Run("disk"), "disk: ready " + s.Next);
        }),
        ("unpacked-over-by-hand", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("all"), "exit: done");
            s.Publish(s.AfterNext);
            s.Says(await s.Run("stage", asVersion: s.Next), "stage: ready " + s.AfterNext);
            s.UnpackBaseOver();
            s.Says(await s.Run("start"), "start:");
            s.NoStage();
            s.Says(await s.Run("notes"), "notes: none");
            s.True((await s.Run("rollback")).Code != 0 || s.ManifestVersion == s.Base, "a rollback must not leave a mix");
        }),
        ("headless-after-a-swap", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("all"), "exit: done");
            s.True(File.Exists(s.Folder.Journal), "a finished swap leaves its journal for the next start");
            await s.RunHeadless(asVersion: s.Next);
            s.True(File.Exists(s.Folder.Journal), "a headless start leaves it for the screen");
            s.Says(await s.Run("start", asVersion: s.Next), "Updated to " + s.Next);
            s.False(File.Exists(s.Folder.Journal), "an interactive start clears it");
        }),
        ("old-copy-just-after-a-swap", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("all"), "exit: done");
            var old = await s.Run("start");
            s.True(old.Code == 2, $"the old exe over new files should exit with code 2, not {old.Code}");
            s.Says(old, UpdateText.StartAgainLine(s.Next));
            s.True(!old.Text.Contains(UpdateText.WaitingLine, StringComparison.Ordinal), "nothing held the lock, so it had nothing to wait for");
            s.True(File.Exists(s.Folder.Journal), "the journal stays for the new exe's line");
            s.Says(await s.Run("start", asVersion: s.Next), "Updated to " + s.Next);
        }),
        ("stale-journal-after-a-hand-unpack", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("all"), "exit: done");
            s.UnpackBaseOver();
            var start = await s.Run("start");
            s.True(start.Code == 0, $"a stale journal must not lock the app out, and this exited {start.Code}");
            s.False(File.Exists(s.Folder.Journal), "nothing is left for a stale journal to say");
            s.ManifestIs(s.Base);
        }),
        ("kept-files-on-an-update-and-a-rollback", async () =>
        {
            s.Publish(s.Next);
            s.AlterInstalled("sim-marker.txt", "a stray at a path the new version adds");
            s.AlterInstalled(s.SmallFile, "edited by hand");
            s.Says(await s.Run("all"), "exit: done");
            s.KeptIs(s.Base, "sim-marker.txt", "a stray at a path the new version adds");
            s.KeptIs(s.Base, s.SmallFile, "edited by hand");
            s.Says(await s.Run("start", asVersion: s.Next), "Updated to " + s.Next, UpdateText.KeptFilesLine(s.KeptFolder(s.Base)));
            s.Says(await s.Run("start", asVersion: s.Next), "start: nothing to say");

            s.AlterInstalled("sim-marker.txt", "edited again");
            s.Says(await s.Run("rollback", asVersion: s.Next), "rollback: to " + s.Base, "exit: done");
            s.KeptIs(s.Next, "sim-marker.txt", "edited again");
            s.Says(await s.Run("start"), UpdateText.BackOnLine(s.Base), UpdateText.KeptFilesLine(s.KeptFolder(s.Next)));
            s.KeptIs(s.Base, s.SmallFile, "edited by hand");
        }),
        ("headless-exit-keeps-the-stage", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            await s.RunHeadless();
            s.ManifestIs(s.Base);
            s.Says(await s.Run("disk"), "disk: ready " + s.Next);
        }),
        ("headless-undoes-a-journal-quietly", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            var swap = s.Start("exit", "--update-swap-delay", "300", "--update-move-all");
            await s.Until(() => File.Exists(s.Folder.Journal), swap);
            await Task.Delay(1000);
            s.Kill(swap);
            var run = await s.RunHeadless();
            s.False(File.Exists(s.Folder.Journal), "headless should undo an unfinished journal");
            s.True(!run.Text.Contains("[Update]", StringComparison.Ordinal) && !run.Text.Contains(UpdateText.WaitingLine, StringComparison.Ordinal), "headless stdout should have nothing added");
            s.IntactAsBase();
        }),
        ("started-during-a-swap", async () =>
        {
            // Only what differs moves: a pause per move over every file would outlast the 10 s wait, and this copy would rightly start anyway.
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            var swap = s.Start("exit", "--update-swap-delay", "500");
            await s.Until(() => File.Exists(s.Folder.Journal), swap);
            var second = await s.Run("start", limitSeconds: 30);
            s.True(second.Code == 2, $"it should exit with code 2, not {second.Code}");
            s.Says(second, UpdateText.WaitingLine, UpdateText.StartAgainLine(s.Next));
            await swap.Finished;
            s.ManifestIs(s.Next);
        }),
        ("headless-started-during-a-swap", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            var swap = s.Start("exit", "--update-swap-delay", "500");
            await s.Until(() => File.Exists(s.Folder.Journal), swap);
            var run = await s.RunHeadless();
            s.True(!run.Text.Contains(UpdateText.WaitingLine, StringComparison.Ordinal), "headless stdout should have nothing added");
            await swap.Finished;
        }),
        ("started-during-a-swap-undone", async () =>
        {
            s.OnWindowsOnly();

            // The locked file differs, so the swap reaches it, and the few moves keep it inside the 10 s wait: the line below shows this copy waited.
            s.Publish(s.Next, tamper: entries => entries[s.SmallFile] = Encoding.UTF8.GetBytes("this release's " + s.SmallFile), manifestFromEntries: true);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            using var locked = new FileStream(s.Folder.Full(s.SmallFile), FileMode.Open, FileAccess.Read, FileShare.None);
            var swap = s.Start("exit", "--update-swap-delay", "500");
            await s.Until(() => File.Exists(s.Folder.Journal), swap);
            var second = await s.Run("start", limitSeconds: 30);
            s.True(second.Code == 0, $"it should start normally, not exit {second.Code}");
            s.Says(second, UpdateText.WaitingLine);
            await swap.Finished;
            s.ManifestIs(s.Base);
        }),
        ("swap-outlasts-the-wait", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            var swap = s.Start("exit", "--update-swap-delay", "2000", "--update-move-all");
            await s.Until(() => File.Exists(s.Folder.Journal), swap);
            var second = await s.Run("start", Sim.MoveAll, limitSeconds: 30);
            s.True(second.Code == 0, $"it should start anyway, not exit {second.Code}");
            s.Kill(swap);
            s.Says(await s.Run("start", Sim.MoveAll), "start:");
        }),
        ("version-takes-no-lock", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("stage"), "stage: ready " + s.Next);
            var version = await s.RunPlain("--version");
            s.True(version.Code == 0, "--version should run");
            s.Says(await s.Run("exit"), "exit: done");
            s.ManifestIs(s.Next);
        }),
        ("renamed-exe", async () =>
        {
            string renamed = s.Folder.Full("renamed" + Path.GetExtension(s.ExeName));
            File.Copy(s.Folder.Full(s.ExeName), renamed);
            s.Publish(s.Next);
            s.Says(await s.Run("all", exe: renamed), "preflight: refused");
            s.NoStage();
        }),
        ("read-only-folder", async () =>
        {
            if (OperatingSystem.IsWindows())
            {
                throw new SkipScenario("Unix only");
            }

            s.Publish(s.Next);
            var mode = File.GetUnixFileMode(s.Folder.Install);
            Directory.CreateDirectory(s.Folder.Root);
            File.SetUnixFileMode(s.Folder.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                s.Says(await s.Run("all"), "preflight: refused");
            }
            finally
            {
                File.SetUnixFileMode(s.Folder.Root, mode);
            }
        }),
        ("symlink-on-path", async () =>
        {
            if (OperatingSystem.IsWindows())
            {
                throw new SkipScenario("Unix only");
            }

            s.Publish(s.Next);
            string link = Path.Combine(s.Scratch, "link-" + s.Name);
            File.CreateSymbolicLink(link, s.Folder.Full(s.ExeName));
            s.Says(await s.Run("all", exe: link), "exit: done");
            s.ManifestIs(s.Next);
            File.Delete(link);
        }),
        ("rollback", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("all"), "exit: done");
            s.Says(await s.Run("start", asVersion: s.Next), "Updated to " + s.Next);
            s.Says(await s.Run("rollback", asVersion: s.Next), "rollback: to " + s.Base, "exit: done");
            s.ManifestIs(s.Base);
            s.Says(await s.Run("start"), UpdateText.BackOnLine(s.Base));
            s.IntactAsBase();
        }),
        ("rollback-nothing", async () =>
        {
            s.Says(await s.Run("rollback"), "rollback: refused: " + UpdateText.NothingToGoBackTo);
            s.Publish(s.Next);
            s.Says(await s.Run("all"), "exit: done");
            s.Says(await s.Run("rollback", asVersion: s.Next), "exit: done");
            s.Says(await s.Run("rollback"), "rollback: refused: " + UpdateText.NothingToGoBackTo);
        }),
        ("rollback-altered-previous", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("all", Sim.MoveAll), "exit: done");
            File.WriteAllText(Path.Combine(s.Folder.Previous, s.SmallNonDoc.Replace('/', Path.DirectorySeparatorChar)), "altered");
            s.Says(await s.Run("rollback", asVersion: s.Next), "rollback: refused");
            s.ManifestIs(s.Next);
            s.True(Directory.Exists(s.Folder.Previous), "previous stays");
        }),
        ("rollback-past-an-edited-doc", async () =>
        {
            s.Publish(s.Next);
            s.Says(await s.Run("all", Sim.MoveAll), "exit: done");
            File.WriteAllText(Path.Combine(s.Folder.Previous, s.SmallFile.Replace('/', Path.DirectorySeparatorChar)), "altered");
            s.Says(await s.Run("rollback", asVersion: s.Next), "rollback: to " + s.Base, "docs staying: " + s.SmallFile, "exit: done");
            s.ManifestIs(s.Base);
            s.IntactAsBase();
        }),
    ];
}

sealed class SkipScenario(string why) : Exception(why);

sealed record RunResult(int Code, string Text);

sealed class RunningCopy
{
    public required Process Process { get; init; }

    public required Task<RunResult> Finished { get; init; }
}

sealed class OpenCopy(Process process, Task started) : IDisposable
{
    public Task Started { get; } = started;

    public void Dispose()
    {
        process.StandardInput.Close();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        process.Dispose();
    }
}

/// <summary>What a scenario works with: its install folder, the releases it publishes and what it checks.</summary>
sealed class Sim
{
    /// <summary>The flag that moves every file, the exe with them: one binary's hash doesn't change between "versions".</summary>
    public static readonly string[] MoveAll = ["--update-move-all"];

    /// <summary>A simulation build's probe crashes with this file beside it (UpdateProbe.CrashMarker, which only that build compiles, and this tool builds against the plain app).</summary>
    public const string CrashMarker = "probe-crash";

    /// <summary>And hangs with this one (UpdateProbe.HangMarker).</summary>
    public const string HangMarker = "probe-hang";

    private readonly string _package;
    private readonly ECDsa _key;
    private readonly string _publicKey;
    private readonly Dictionary<string, byte[]> _baseFiles = new(StringComparer.Ordinal);
    private readonly UpdateManifest _baseManifest;
    private readonly List<string> _output = [];
    private int _windows;

    public Sim(string package, string scratch, ECDsa key, FakeGitHub github)
    {
        _package = package;
        Scratch = scratch;
        _key = key;
        _publicKey = UpdateSignature.PublicKeyText(key);
        GitHub = github;
        _baseManifest = UpdateManifest.ReadFile(Path.Combine(package, UpdateManifest.FileName), out string? problem) ?? throw new InvalidOperationException(problem ?? "no manifest in " + package);
        Base = _baseManifest.Version;
        Rid = _baseManifest.Rid;
        ExeName = _baseManifest.Exe;
        if (!UpdateVersion.TryParse(Base, out var parsed))
        {
            throw new InvalidOperationException("the package's version doesn't read: " + Base);
        }

        Next = new UpdateVersion(parsed.Major, parsed.Minor, parsed.Patch + 1).ToString();
        AfterNext = new UpdateVersion(parsed.Major, parsed.Minor, parsed.Patch + 2).ToString();
        string? small = _baseManifest.Files.Select(f => f.Path).FirstOrDefault(p => p.EndsWith(".md", StringComparison.Ordinal));
        SmallFile = small ?? throw new InvalidOperationException("the package holds no .md file to alter");
        SmallNonDoc = _baseManifest.Files.Select(f => f.Path).Where(p => !p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).StartsWith("README", StringComparison.OrdinalIgnoreCase) && p != ExeName)
            .OrderBy(p => new FileInfo(Path.Combine(package, p.Replace('/', Path.DirectorySeparatorChar))).Length).ThenBy(p => p, StringComparer.Ordinal).First();
        NativeLibrary = NativeSample();
    }

    public string Scratch { get; }

    public FakeGitHub GitHub { get; }

    public string Base { get; }

    public string Next { get; }

    public string AfterNext { get; }

    public string Rid { get; }

    public string ExeName { get; }

    public string SmallFile { get; }

    /// <summary>A listed file that isn't a doc: an altered copy of one of these still blocks a rollback.</summary>
    public string SmallNonDoc { get; }

    public string NativeLibrary { get; }

    public string Name { get; private set; } = "";

    public UpdateFolder Folder { get; private set; } = null!;

    public IReadOnlyList<string> LastOutput => _output;

    public string ArchiveExtension => OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";

    public string ManifestVersion => UpdateManifest.ReadFile(Folder.Manifest, out _)?.Version ?? "none";

    private string Home => Path.Combine(Scratch, "home-" + Name);

    /// <summary>The version after this one.</summary>
    public static string After(string version) =>
        UpdateVersion.TryParse(version, out var parsed) ? new UpdateVersion(parsed.Major, parsed.Minor, parsed.Patch + 1).ToString() : throw new InvalidOperationException("not a version: " + version);

    /// <summary>The by-hand run moves every file, the exe too.</summary>
    public bool ServeMovesAll { get; set; }

    /// <summary>What the by-hand run's install holds now, for the tool's console.</summary>
    public string ServeStatus(string latest)
    {
        string state = File.Exists(Folder.Ready) ? ", a stage ready" : Directory.Exists(Folder.Staging) ? ", a stage on the way" : "";
        string previous = Directory.Exists(Folder.Previous) && Directory.EnumerateFileSystemEntries(Folder.Previous).Any() ? $", an earlier version to go back to ({Directory.EnumerateFiles(Folder.Previous, "*", SearchOption.AllDirectories).Count()} files)" : "";
        return $"The folder holds {ManifestVersion}{state}{previous}. The fake GitHub's latest is {latest}.";
    }

    /// <summary>
    /// Starts the real interface over the fake GitHub, as the version the folder holds. Through a script, so the home and the flags reach the app through Windows Terminal, and on Windows the
    /// window stays up afterward with the quit lines on it. Its last act leaves the install folder, or a window left open would keep the folder from being deleted.
    /// </summary>
    public void OpenWindow()
    {
        string arguments = $"--update-source {GitHub.Origin} --update-as-version {ManifestVersion} --update-test-key {_publicKey}" + (ServeMovesAll ? " " + MoveAll[0] : "");
        string title = "NeonSidekick update sim " + ++_windows;
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(Scratch, $"start-{_windows}.cmd");
            File.WriteAllText(script, string.Join("\r\n",
                "@echo off",
                "set \"NO_COLOR=\"",
                $"set \"NEONSIDEKICK_HOME={Home}\"",
                $"cd /d \"{Folder.Install}\"",
                $"\"{Folder.Full(ExeName)}\" {arguments}",
                "cd /d \"%TEMP%\"",
                "echo.",
                "pause",
                ""));
            try
            {
                var start = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
                foreach (string argument in (string[])["-w", "new", "--size", "120,40", "--title", title, "--suppressApplicationTitle", "--", "cmd", "/c", script])
                {
                    start.ArgumentList.Add(argument);
                }

                Process.Start(start);
                Console.WriteLine($"Opened \"{title}\" in Windows Terminal.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"Windows Terminal didn't start. In another terminal, run: \"{script}\"");
            }
        }
        else
        {
            string script = Path.Combine(Scratch, $"start-{_windows}.sh");
            File.WriteAllText(script, string.Join("\n",
                "unset NO_COLOR",
                $"export NEONSIDEKICK_HOME='{Home}'",
                $"cd '{Folder.Install}'",
                $"'{Folder.Full(ExeName)}' {arguments}",
                ""));
            Console.WriteLine($"In another iTerm2 or Terminal window, run: zsh '{script}'");
        }
    }

    /// <summary>Ends what the by-hand run started and nothing else: only a copy whose exe is under this run's scratch folder, never the user's own.</summary>
    public void StopServedCopies()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try
            {
                if (process.MainModule?.FileName is { } file && file.StartsWith(Scratch, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Gone already, or not ours to read.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public static string NewestPackage()
    {
        string root = Path.Combine("publish", "package");
        var found = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, UpdateManifest.FileName))).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return found is null ? throw new InvalidOperationException("No package under publish/package. Run .\\build.ps1 -Publish -Package -UpdateSim first.") : Path.GetFullPath(found);
    }

    public static void Delete(string folder)
    {
        for (int tries = 0; tries < 5 && Directory.Exists(folder); tries++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    public void Begin(string name)
    {
        Name = name;
        _output.Clear();
        GitHub.Reset();
        string install = Path.Combine(Scratch, name);
        CopyFolder(_package, install);
        Directory.CreateDirectory(Home);
        Folder = new UpdateFolder(install);
    }

    public void End()
    {
        Delete(Folder.Install);
        Delete(Home);
    }

    public string Archive(string version) => "NeonSidekick-v" + version + "-" + Rid + ArchiveExtension;

    public string MarkerHash(string version) => UpdateHash.Of(Encoding.UTF8.GetBytes(MarkerText(version)));

    private static string MarkerText(string version) => "the simulated " + version;

    /// <summary>
    /// A release of <paramref name="version"/>: the package with a marker file, so a swap is visible, its manifest rewritten, signed with this run's key. <paramref name="tamper"/> changes the files after the manifest is written, or before
    /// with <paramref name="manifestFromEntries"/>, for a file the manifest vouches for; <paramref name="afterSign"/> changes the archive's bytes after signing.
    /// </summary>
    public void Publish(string version, ECDsa? signer = null, Action<byte[]>? afterSign = null, Action<Dictionary<string, byte[]>>? tamper = null, string? shaNames = null, string? manifestVersion = null, int format = 1, bool manifestFromEntries = false)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in _baseManifest.Files)
        {
            entries[file.Path] = BaseBytes(file.Path);
        }

        entries["sim-marker.txt"] = Encoding.UTF8.GetBytes(MarkerText(version));
        if (manifestFromEntries)
        {
            tamper?.Invoke(entries);
        }

        string manifest = ManifestText(manifestVersion ?? version, entries, format);
        if (!manifestFromEntries)
        {
            tamper?.Invoke(entries);
        }

        entries[UpdateManifest.FileName] = Encoding.UTF8.GetBytes(manifest);
        byte[] archive = PackArchive("NeonSidekick-v" + version + "-" + Rid, entries);
        string name = Archive(version);
        string sha = UpdateShaFile.Text(UpdateHash.Of(archive), shaNames ?? name, UpdateHash.Of(Encoding.UTF8.GetBytes(manifest)));
        byte[] shaBytes = Encoding.ASCII.GetBytes(sha);
        string sig = UpdateSignature.SignatureLine(shaBytes, signer ?? _key) + "\n";
        afterSign?.Invoke(archive);
        GitHub.Publish(version, name, archive, shaBytes, Encoding.ASCII.GetBytes(sig));
    }

    public async Task<RunResult> Run(string mode, string[]? flags = null, string? asVersion = null, string? source = null, string? exe = null, int limitSeconds = 45)
    {
        var copy = Start(mode, flags ?? [], asVersion, source, exe);
        var finished = await Task.WhenAny(copy.Finished, Task.Delay(TimeSpan.FromSeconds(limitSeconds))).ConfigureAwait(false);
        if (finished != copy.Finished)
        {
            copy.Process.Kill(entireProcessTree: true);
            throw new TimeoutException($"--update-check {mode} ran past {limitSeconds} s");
        }

        return await copy.Finished.ConfigureAwait(false);
    }

    public RunningCopy Start(string mode, params string[] more) => Start(mode, more, null, null, null);

    public RunningCopy Start(string mode, string[] more, string? asVersion, string? source, string? exe)
    {
        var arguments = new List<string>(more)
        {
            "--update-check", mode,
            "--update-source", source ?? GitHub.Origin,
            "--update-as-version", asVersion ?? Base,
            "--update-test-key", _publicKey,
        };
        return Launch(arguments, exe);
    }

    public Task<RunResult> RunPlain(params string[] arguments) => Launch([.. arguments], null).Finished;

    /// <summary>A headless run with stdin at its end, so it reads nothing and exits as a redirected one does.</summary>
    public Task<RunResult> RunHeadless(string? asVersion = null) => Launch(HeadlessArguments(asVersion), null, closeInput: true).Finished;

    /// <summary>A headless run left open on its stdin, holding the shared lock as any running copy does.</summary>
    public OpenCopy OpenHeadless()
    {
        var start = Info(HeadlessArguments(null), null);
        start.RedirectStandardInput = true;
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new OpenCopy(process, Task.Delay(TimeSpan.FromSeconds(2)));
    }

    private List<string> HeadlessArguments(string? asVersion) =>
    [
        "--headless", "--url", "http://127.0.0.1:9/v1",
        "--update-as-version", asVersion ?? Base,
        "--update-test-key", _publicKey,
    ];

    private ProcessStartInfo Info(List<string> arguments, string? exe)
    {
        var start = new ProcessStartInfo(exe ?? Folder.Full(ExeName)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Home };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["NEONSIDEKICK_HOME"] = Home;
        return start;
    }

    private RunningCopy Launch(List<string> arguments, string? exe, bool closeInput = false)
    {
        var start = Info(arguments, exe);
        start.RedirectStandardInput = closeInput;
        var process = Process.Start(start) ?? throw new InvalidOperationException("the exe didn't start");
        if (closeInput)
        {
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        async Task<RunResult> Wait()
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            string text = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
            lock (_output)
            {
                _output.Add($"-- {string.Join(' ', arguments.Where(a => a != _publicKey))} -> exit {process.ExitCode}");
                _output.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            return new RunResult(process.ExitCode, text);
        }

        return new RunningCopy { Process = process, Finished = Task.Run(Wait) };
    }

    public void Kill(RunningCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (!copy.Process.HasExited)
        {
            copy.Process.Kill(entireProcessTree: true);
        }

        copy.Process.WaitForExit();
    }

    public async Task Until(Func<bool> condition, RunningCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        var limit = Stopwatch.StartNew();
        while (!condition())
        {
            if (copy.Process.HasExited || limit.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new InvalidOperationException("what the scenario waited for didn't happen before the copy ended");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public void Says(RunResult run, params string[] lines)
    {
        foreach (string line in lines)
        {
            True(run.Text.Contains(line, StringComparison.Ordinal), "expected to see: " + line);
        }
    }

    public void Refused(RunResult run)
    {
        True(run.Code != 0 && run.Text.Contains("stage: failed", StringComparison.Ordinal), "expected the stage to fail");
    }

    public void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public void False(bool condition, string message) => True(!condition, message);

    public void Has(string path) => True(File.Exists(Folder.Full(path)), path + " should be installed");

    public void ManifestIs(string version) => True(ManifestVersion == version, $"the installed manifest says {ManifestVersion}, not {version}");

    public void NothingInstalled() => ManifestIs(Base);

    public void NoStage() => False(File.Exists(Folder.Ready), "there should be no ready stage");

    /// <summary>A verified stage kept after a failed test run: a retry tests it again with no download. No ready, so it never installs.</summary>
    public void StageKept() => True(Directory.Exists(Folder.Staging), "a failed test run should keep the stage");

    public string KeptFolder(string version) => Path.Combine(Folder.Kept, version);

    public void KeptIs(string version, string path, string text) => True(File.ReadAllText(Path.Combine(KeptFolder(version), path.Replace('/', Path.DirectorySeparatorChar))) == text, path + " in kept isn't what it should be");

    public void InPrevious(string path) => True(File.Exists(Path.Combine(Folder.Previous, path.Replace('/', Path.DirectorySeparatorChar))), path + " should be in previous");

    public void NotInPrevious(string path) => False(File.Exists(Path.Combine(Folder.Previous, path.Replace('/', Path.DirectorySeparatorChar))), path + " shouldn't have moved");

    public void InstalledIs(string path, string text) => True(File.ReadAllText(Folder.Full(path)) == text, path + " isn't what it should be");

    public string BaseText(string path) => Encoding.UTF8.GetString(BaseBytes(path));

    public void AlterInstalled(string path, string text) => File.WriteAllText(Folder.Full(path), text);

    public void OnWindowsOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SkipScenario("a Mac doesn't lock a file");
        }
    }

    /// <summary>Every listed file as the package has it, and nothing new at the top: the install is back as it was.</summary>
    public void IntactAsBase()
    {
        foreach (var file in _baseManifest.Files)
        {
            string path = Folder.Full(file.Path);
            True(File.Exists(path), file.Path + " is missing");
            True(UpdateHash.Same(UpdateHash.OfFile(path), file.Sha256), file.Path + " isn't the package's");
        }

        False(File.Exists(Folder.Full("sim-marker.txt")), "the marker file shouldn't be installed");
    }

    /// <summary>A package unpacked by hand over the folder, with the app's own <c>.update/</c> left in place.</summary>
    public void UnpackBaseOver()
    {
        foreach (var file in _baseManifest.Files)
        {
            string path = Folder.Full(file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, BaseBytes(file.Path));
        }

        File.Copy(Path.Combine(_package, UpdateManifest.FileName), Folder.Manifest, overwrite: true);
    }

    private byte[] BaseBytes(string path)
    {
        if (!_baseFiles.TryGetValue(path, out byte[]? bytes))
        {
            bytes = File.ReadAllBytes(Path.Combine(_package, path.Replace('/', Path.DirectorySeparatorChar)));
            _baseFiles[path] = bytes;
        }

        return bytes;
    }

    private string ManifestText(string version, Dictionary<string, byte[]> entries, int format)
    {
        var files = entries.Where(e => e.Key != UpdateManifest.FileName).OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new Dictionary<string, string> { ["path"] = e.Key, ["sha256"] = UpdateHash.Of(e.Value) }).ToList();
        var writer = new MemoryStream();
        using (var json = new Utf8JsonWriter(writer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("format", format);
            json.WriteString("version", version);
            json.WriteString("rid", Rid);
            json.WriteString("exe", ExeName);
            json.WriteStartArray("files");
            foreach (var file in files)
            {
                json.WriteStartObject();
                json.WriteString("path", file["path"]);
                json.WriteString("sha256", file["sha256"]);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(writer.ToArray());
    }

    private byte[] PackArchive(string topFolder, Dictionary<string, byte[]> entries)
    {
        using var stream = new MemoryStream();
        if (OperatingSystem.IsWindows())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, bytes) in entries)
                {
                    using var entry = zip.CreateEntry(topFolder + "/" + path, CompressionLevel.NoCompression).Open();
                    entry.Write(bytes);
                }
            }
        }
        else
        {
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, leaveOpen: true))
            {
                foreach (var (path, bytes) in entries)
                {
                    using var data = new MemoryStream(bytes);
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, topFolder + "/" + path) { DataStream = data };
                    string source = Path.Combine(_package, path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(source))
                    {
                        entry.Mode = File.GetUnixFileMode(source);
                    }

                    tar.WriteEntry(entry);
                }
            }
        }

        return stream.ToArray();
    }

    private string NativeSample()
    {
        string name = OperatingSystem.IsWindows() ? "libwinpthread-1.dll" : "libe_sqlite3.dylib";
        return _baseManifest.Files.Select(f => f.Path).FirstOrDefault(p => p.EndsWith(name, StringComparison.Ordinal)) ?? throw new InvalidOperationException($"the package holds no {name}");
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(file));
            }
        }
    }
}

/// <summary>GitHub, on loopback: <c>releases/latest</c> and the assets, each behind one redirect, with what a scenario needs to go wrong.</summary>
sealed class FakeGitHub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hidden = [];
    private readonly List<(string Tag, string[] Names)> _releases = [];

    public FakeGitHub()
    {
        _listener.Start();
        Origin = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
        _ = Task.Run(AcceptAsync);
    }

    public string Origin { get; }

    public string? Latest { get; set; }

    public int? LatestStatus { get; set; }

    /// <summary>Closes the connection at this fraction of the archive, once it has been asked for from the start.</summary>
    public double? DropAt { get; set; }

    /// <summary>Holds the connection silent at this fraction of the archive.</summary>
    public double? StallAt { get; set; }

    /// <summary>Sends slowly past this fraction, so a scenario can kill the copy mid-download.</summary>
    public double? SlowAfter { get; set; }

    public int RangeRequests { get; private set; }

    /// <summary>The release's notes, the by-hand run's long ones.</summary>
    public string Notes { get; set; } = "the simulated notes";

    /// <summary>Paces an archive to take about this long, so the by-hand run can watch its percentage.</summary>
    public double? SendSeconds { get; set; }

    /// <summary>Told each request, for the by-hand run's console.</summary>
    public Action<string>? Log { get; set; }

    public static string Json(string tag, IEnumerable<(string Name, long Size, string Url)> assets, string notes = "the simulated notes", string origin = "http://127.0.0.1/")
    {
        var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("tag_name", tag);
            json.WriteString("body", notes);
            json.WriteString("html_url", origin + "sim/releases/tag/" + tag);
            json.WriteStartArray("assets");
            foreach (var (name, size, url) in assets)
            {
                json.WriteStartObject();
                json.WriteString("name", name);
                json.WriteNumber("size", size);
                json.WriteString("browser_download_url", url);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Reset()
    {
        lock (_assets)
        {
            _assets.Clear();
            _sent.Clear();
            _hidden.Clear();
            _releases.Clear();
        }

        Latest = null;
        LatestStatus = null;
        DropAt = null;
        StallAt = null;
        SlowAfter = null;
        SendSeconds = null;
        RangeRequests = 0;
    }

    public void Publish(string version, string archiveName, byte[] archive, byte[] sha, byte[] sig)
    {
        lock (_assets)
        {
            string tag = "v" + version;
            _assets[tag + "/" + archiveName] = archive;
            _assets[tag + "/" + archiveName + ".sha256"] = sha;
            _assets[tag + "/" + archiveName + ".sha256.sig"] = sig;
            _releases.Add((tag, [archiveName, archiveName + ".sha256", archiveName + ".sha256.sig"]));
        }
    }

    /// <summary>Leaves out of the listing every asset whose name ends so.</summary>
    public void Hide(string suffix)
    {
        lock (_assets)
        {
            _hidden.Add(suffix);
        }
    }

    public long Sent(string archiveName)
    {
        lock (_assets)
        {
            return _sent.Where(s => s.Key.EndsWith("/" + archiveName, StringComparison.Ordinal)).Sum(s => s.Value);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private string LatestJson()
    {
        if (Latest is { } planted)
        {
            return planted;
        }

        lock (_assets)
        {
            if (_releases.Count == 0)
            {
                return Json("v0.0.1", []);
            }

            var (tag, names) = _releases[^1];
            return Json(tag, names.Where(n => !_hidden.Any(h => n.EndsWith(h, StringComparison.Ordinal))).Select(n => (n, (long)_assets[tag + "/" + n].Length, Origin + "sim/releases/download/" + tag + "/" + n)), Notes, origin: Origin);
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client));
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (path, rangeFrom) = await ReadRequestAsync(stream).ConfigureAwait(false);
                Log?.Invoke(rangeFrom > 0 ? $"{path} from byte {rangeFrom}" : path);
                if (path == "/repos/sim/releases/latest")
                {
                    await WriteAsync(stream, LatestStatus ?? 200, Encoding.UTF8.GetBytes(LatestJson()), "application/json").ConfigureAwait(false);
                }
                else if (path.StartsWith("/sim/releases/download/", StringComparison.Ordinal))
                {
                    // One redirect, as GitHub answers an asset, so the client's following it is really exercised.
                    string cdn = "/cdn/" + path["/sim/releases/download/".Length..];
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {cdn}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).ConfigureAwait(false);
                }
                else if (path.StartsWith("/cdn/", StringComparison.Ordinal))
                {
                    await ServeAssetAsync(stream, path["/cdn/".Length..], rangeFrom).ConfigureAwait(false);
                }
                else
                {
                    await WriteAsync(stream, 404, [], "text/plain").ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client went away: a scenario's kill, or a cancel.
            }
        }
    }

    private async Task ServeAssetAsync(NetworkStream stream, string asset, long rangeFrom)
    {
        byte[]? bytes;
        lock (_assets)
        {
            _assets.TryGetValue(asset, out bytes);
            if (rangeFrom > 0)
            {
                RangeRequests++;
            }
        }

        if (bytes is null)
        {
            await WriteAsync(stream, 404, [], "text/plain").ConfigureAwait(false);
            return;
        }

        string head = rangeFrom > 0
            ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {rangeFrom}-{bytes.Length - 1}/{bytes.Length}\r\nContent-Length: {bytes.Length - rangeFrom}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n"
            : $"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token).ConfigureAwait(false);
        bool isArchive = bytes.Length > 1 << 16;
        long stopAt = isArchive && DropAt is { } drop ? (long)(bytes.Length * drop) : long.MaxValue;
        long stallAt = isArchive && StallAt is { } stall ? (long)(bytes.Length * stall) : long.MaxValue;
        long slowAt = isArchive && SlowAfter is { } slow ? (long)(bytes.Length * slow) : long.MaxValue;
        const int chunk = 1 << 16;
        var sending = Stopwatch.StartNew();
        for (long at = rangeFrom; at < bytes.Length; at += chunk)
        {
            if (isArchive && SendSeconds is { } seconds)
            {
                double ahead = seconds * (at - rangeFrom) / bytes.Length - sending.Elapsed.TotalSeconds;
                if (ahead > 0.02)
                {
                    await Task.Delay(TimeSpan.FromSeconds(ahead), _stop.Token).ConfigureAwait(false);
                }
            }

            if (at >= stopAt)
            {
                return;   // the connection dropped
            }

            if (at >= stallAt)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
            }

            if (at >= slowAt)
            {
                await Task.Delay(50, _stop.Token).ConfigureAwait(false);
            }

            int count = (int)Math.Min(chunk, bytes.Length - at);
            await stream.WriteAsync(bytes.AsMemory((int)at, count), _stop.Token).ConfigureAwait(false);
            lock (_assets)
            {
                _sent[asset] = _sent.GetValueOrDefault(asset) + count;
            }
        }
    }

    private static async Task<(string Path, long RangeFrom)> ReadRequestAsync(NetworkStream stream)
    {
        var text = new StringBuilder();
        byte[] one = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one).ConfigureAwait(false) == 0)
            {
                break;
            }

            text.Append((char)one[0]);
        }

        string[] lines = text.ToString().Split("\r\n");
        string path = lines[0].Split(' ') is [_, var target, ..] ? target : "/";
        long from = 0;
        foreach (string line in lines)
        {
            if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase) && long.TryParse(line["Range: bytes=".Length..].Split('-')[0], out long parsed))
            {
                from = parsed;
            }
        }

        return (path, from);
    }

    private static async Task WriteAsync(NetworkStream stream, int status, byte[] body, string type)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Sim\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n")).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
    }
}
