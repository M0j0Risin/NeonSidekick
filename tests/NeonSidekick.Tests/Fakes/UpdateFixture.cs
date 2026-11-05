using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Speech;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests.Fakes;

/// <summary>A probe that starts no process: writes what a test says a run would have written.</summary>
public sealed class FakeUpdateProbe : IUpdateProbeRunner
{
    /// <summary>What the staged exe's run writes and returns. Defaults to a clean run of <see cref="Version"/>.</summary>
    public Func<string, (string Text, int? Code)>? Answer { get; set; }

    public string Version { get; set; } = "0.5.7";

    public List<string> Exes { get; } = [];

    /// <summary>The stderr file as each run found it: whether an earlier run's text was still there.</summary>
    public List<bool> StderrWasThere { get; } = [];

    public Task<int?> RunAsync(string exe, string resultFile, string stderrFile, TimeSpan limit, CancellationToken cancellationToken)
    {
        Exes.Add(exe);
        StderrWasThere.Add(File.Exists(stderrFile));
        var (text, code) = Answer?.Invoke(exe) ?? ($"version {Version}\ndone\n", UpdateProbe.Passed);
        File.WriteAllText(resultFile, text);
        return Task.FromResult(code);
    }
}

/// <summary>
/// A throwaway install folder and fake GitHub for the update's tests (2026-11-04, self-update): a key made at run time, a real zip served from a stub, and the same <see cref="UpdateManifest"/> and <see cref="UpdateShaFile"/>
/// shapes <c>build.ps1</c> writes, so the tests walk the flow without a package or a network.
/// </summary>
public sealed class UpdateFixture : IDisposable
{
    public const string Rid = "win-x64";
    public const string Exe = "NeonSidekick.exe";
    private const string Download = "https://github.com/M0j0Risin/NeonSidekick/releases/download/";

    public UpdateFixture()
    {
        Root = Directory.CreateTempSubdirectory("neon-update-").FullName;
        Folder = new UpdateFolder(Root);
        Key = UpdateSignature.NewKey();
        Keys = [UpdateSignature.PublicKeyText(Key)];
        Http = new StubHttpMessageHandler();
        Client = new HttpClient(Http);
    }

    public string Root { get; }

    public UpdateFolder Folder { get; }

    public ECDsa Key { get; }

    public IReadOnlyList<string> Keys { get; }

    public StubHttpMessageHandler Http { get; }

    public HttpClient Client { get; }

    public FakeUpdateProbe Probe { get; } = new();

    public static UpdateVersion V(string text) => UpdateVersion.TryParse(text, out var version) ? version : throw new ArgumentException(text);

    public UpdateContext Context(string running = "0.5.6", string exeName = Exe, bool moveAll = false) => new(Folder, V(running), Keys, exeName, moveAll);

    public UpdateCheck Check(string running = "0.5.6") => new(Client, UpdateSource.GitHub(AboutText.RepositoryUrl), V(running), Rid);

    public UpdateStage Stage(string running = "0.5.6", TimeProvider? time = null, Func<string, long?>? freeBytes = null, string exeName = Exe) =>
        new(Context(running, exeName), new ModelStore(Folder.Root, Client, UpdateJournal.Category) { AvailableBytes = _ => null }, Check(running), Probe, time, freeBytes ?? (_ => null));

    public static string Manifest(string version, IReadOnlyDictionary<string, string> files, int format = 1, string rid = Rid) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["format"] = format,
            ["version"] = version,
            ["rid"] = rid,
            ["exe"] = Exe,
            ["files"] = files.Select(f => new Dictionary<string, string> { ["path"] = f.Key, ["sha256"] = HashOf(f.Value) }).ToList(),
        });

    public static string HashOf(string text) => UpdateHash.Of(Encoding.UTF8.GetBytes(text));

    /// <summary>Lays down an installed version: its files and manifest.</summary>
    public void Install(string version, Dictionary<string, string> files)
    {
        foreach (var (path, text) in files)
        {
            Write(Folder.Full(path), text);
        }

        Write(Folder.Manifest, Manifest(version, files));
    }

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static byte[] Zip(string topFolder, IReadOnlyDictionary<string, string> entries)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(topFolder + "/" + path).Open());
                writer.Write(text);
            }
        }

        return bytes.ToArray();
    }

    /// <summary>A published release, on the stub: a real zip, its signed <c>.sha256</c> and <c>.sig</c>. The arguments bend it the ways the refusals are about.</summary>
    public UpdateRelease Publish(string version, Dictionary<string, string> files, string? manifestText = null, string? shaArchiveName = null, ECDsa? signer = null, string? notes = "the notes", Action<Dictionary<string, string>>? tamper = null)
    {
        string archive = UpdateCheck.ArchiveName(V(version), Rid);
        string manifest = manifestText ?? Manifest(version, files);
        var entries = new Dictionary<string, string>(files) { [UpdateManifest.FileName] = manifest };
        tamper?.Invoke(entries);
        byte[] zip = Zip("NeonSidekick-v" + version + "-" + Rid, entries);
        string sha = UpdateShaFile.Text(UpdateHash.Of(zip), shaArchiveName ?? archive, HashOf(manifest));
        byte[] shaBytes = Encoding.ASCII.GetBytes(sha);
        string sig = UpdateSignature.SignatureLine(shaBytes, signer ?? Key) + "\n";
        string prefix = Download + "v" + version + "/" + archive;
        Http.Map(prefix + ".sha256.sig", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Encoding.ASCII.GetBytes(sig))));
        Http.Map(prefix + ".sha256", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, shaBytes)));
        Http.Map(prefix, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, zip)));
        return new UpdateRelease(V(version), notes, "https://github.com/M0j0Risin/NeonSidekick/releases/tag/v" + version, archive, new Uri(prefix), zip.Length, new Uri(prefix + ".sha256"), new Uri(prefix + ".sha256.sig"));
    }

    /// <summary>The usual pair: 0.5.6 installed, 0.5.7 published, with one changed file, one kept and one new.</summary>
    public UpdateRelease Usual(Dictionary<string, string>? installed = null)
    {
        Install("0.5.6", installed ?? new Dictionary<string, string> { [Exe] = "old exe", ["a.dll"] = "old a", ["same.dll"] = "same" });
        return Publish("0.5.7", new Dictionary<string, string> { [Exe] = "new exe", ["a.dll"] = "new a", ["same.dll"] = "same", ["docs/new.md"] = "new doc" });
    }

    public void Dispose()
    {
        Key.Dispose();
        Client.Dispose();
        Http.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A test's own lock still closing; the temp folder's cleaner gets it.
        }
    }
}
