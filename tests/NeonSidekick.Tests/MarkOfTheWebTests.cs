using System.IO.Compression;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// 2026-10-22, review O5: what unzip takes out of an archive carries the archive's internet mark, so SmartScreen and Protected View still
/// ask. An archive with no mark gives none. (A Mac's quarantine is the smoke's <c>files:quarantine</c>.)
/// </summary>
public sealed class MarkOfTheWebTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly WorkingDirectory _files;

    public MarkOfTheWebTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Archive(string name)
    {
        string path = Path.Combine(_root, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("tool/run.bat").Open());
            writer.Write("echo hi");
        }

        return path;
    }

    [WindowsFact]
    public void Unzip_CarriesTheArchivesMark()
    {
        string archive = Archive("download.zip");
        const string mark = "[ZoneTransfer]\r\nZoneId=3\r\n";
        File.WriteAllText(archive + MarkOfTheWeb.ZoneStream, mark);

        Assert.Equal(FileOutcome.Ok, _files.Unzip("download.zip", "out", overwrite: false).Outcome);
        Assert.Equal(mark, File.ReadAllText(Path.Combine(_root, "out", "tool", "run.bat") + MarkOfTheWeb.ZoneStream));
    }

    [WindowsFact]
    public void Unzip_OfAnUnmarkedArchive_MarksNothing()
    {
        Archive("made.zip");
        Assert.Equal(FileOutcome.Ok, _files.Unzip("made.zip", "out", overwrite: false).Outcome);
        Assert.False(File.Exists(Path.Combine(_root, "out", "tool", "run.bat") + MarkOfTheWeb.ZoneStream));
        Assert.Null(MarkOfTheWeb.Read(Path.Combine(_root, "made.zip")));
    }
}
