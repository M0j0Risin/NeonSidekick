using NeonSidekick.Files;
using NeonSidekick.Git;

namespace NeonSidekick.Tests.Fakes;

/// <summary>A temp working directory for the code audit's tests (2026-10-18): the sandbox, its git door, and files written by relative path.</summary>
internal sealed class AuditSandbox : IDisposable
{
    public AuditSandbox()
    {
        Dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Dir, "files");
        Directory.CreateDirectory(Root);
        Time = new ManualTimeProvider();
        Files = new WorkingDirectory(() => Root, Time);
        Git = new GitAccess(Files, Time);
    }

    public string Dir { get; }

    public string Root { get; }

    public ManualTimeProvider Time { get; }

    public WorkingDirectory Files { get; }

    public GitAccess Git { get; }

    /// <summary><paramref name="relative"/> (with <c>/</c>) written under the root; its full path.</summary>
    public string Write(string relative, string content)
    {
        string full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>A binary file (a NUL early on).</summary>
    public string WriteBinary(string relative)
    {
        string full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [0x50, 0x00, 0x01, 0x02]);
        return full;
    }

    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The word's audit path when it names a file or folder here, as the screen's <c>ExistingAuditPath</c> answers.</summary>
    public string? Existing(string word)
    {
        if (Audits.AuditPath.Of(Files, word, out _) is not { } path || Files.Resolve(path, forWrite: false, out string full) != FileOutcome.Ok)
        {
            return null;
        }

        return File.Exists(full) || Directory.Exists(full) ? path : null;
    }

    public void Dispose() => GitAccessTests.DeleteTree(Dir);
}
