using Microsoft.Extensions.AI;
using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/code-audit</c>'s report lock on the screen (2026-10-29: two apps auditing one report each saved the whole file, and one overwrote the
/// other's findings): held while the passes run and gone after, resume, fix and discard refused while another live NeonSidekick holds it, a lock
/// that can't be written refused in its own words, and an audit whose lock another app took over mid-pass stopped with nothing more written.
/// </summary>
public partial class ChatScreenTests
{
    private const int OtherAuditPid = 424242;

    private string AuditLockFile(string report) => Path.Combine(AuditRoot, NeonSidekick.Audits.AuditFiles.LockPathFor(report).Replace('/', Path.DirectorySeparatorChar));

    private void WriteOtherAuditLock(string report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuditLockFile(report))!);
        File.WriteAllText(AuditLockFile(report), "pid: 424242\nstarted: 2026-10-29T09:00:00.0000000+00:00\n");
    }

    /// <summary><paramref name="body"/> with <see cref="OtherAuditPid"/> as another live NeonSidekick (every other owner but this one gone).</summary>
    private static async Task WithOtherAuditAppAsync(Func<Task> body)
    {
        var alive = OwnerLock.LiveOwner;
        OwnerLock.LiveOwner = owner => owner.ProcessId == OtherAuditPid || owner == OwnerLock.Me;
        try
        {
            await body();
        }
        finally
        {
            OwnerLock.LiveOwner = alive;
        }
    }

    /// <summary>A stopped report with one open finding, saved under the fixture's working directory: its path.</summary>
    private string SaveStoppedAuditReport()
    {
        var now = _time.GetLocalNow();
        var files = AuditFiles();
        string path = NeonSidekick.Audits.AuditFiles.Choose(files, "src", now);
        var report = new AuditReport
        {
            Label = "src",
            Header = new AuditHeader(AuditStatus.Stopped, "1 pass", "src/", AuditFocus.Dimensions, "", "model", 2, now, now),
        };
        report.Findings.Add(new AuditFinding("A1", AuditSeverity.High, "security", "src/a.cs", 3, "SQL built from input", "The name is joined into the query.", null, null, 1, false, false, null, []));
        Assert.Null(NeonSidekick.Audits.AuditFiles.Save(files, path, report, now));
        return path;
    }

    [Fact]
    public async Task CodeAudit_TheLock_IsHeldWhileThePassesRun_AndGoneAfter()
    {
        AuditFixture(Line("/code-audit src security"), Key(Keys.Enter), Line("/exit"));
        ThreePasses();
        bool heldDuringPass = false;
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 3 && update == 0)
            {
                heldDuringPass = File.Exists(AuditLockFile(NeonSidekick.Audits.AuditFiles.List(AuditFiles()).Single().Path));
            }

            return Task.CompletedTask;
        };

        await RunAsync();

        Assert.True(heldDuringPass);
        Assert.False(File.Exists(AuditLockFile(AuditPath)));
    }

    [Fact]
    public Task CodeAudit_ResumeAndFix_AreRefused_WhileAnotherAppHoldsTheReport() => WithOtherAuditAppAsync(async () =>
    {
        AuditFixture(Line("/code-audit resume"), Line("/code-audit fix"), Line("/exit"));
        string path = SaveStoppedAuditReport();
        WriteOtherAuditLock(path);
        string before = File.ReadAllText(Path.Combine(AuditRoot, path));

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Equal(2, CountOf(output, AuditText.LockedError(path)));
        Assert.Equal(before, File.ReadAllText(Path.Combine(AuditRoot, path)));
        Assert.True(File.Exists(AuditLockFile(path)));
    });

    [Fact]
    public async Task CodeAudit_ALockThatCantBeWritten_IsRefusedInItsOwnWords()
    {
        AuditFixture(Line("/code-audit src/"), Line("/exit"));
        Directory.CreateDirectory(Path.Combine(AuditRoot, ".neon"));
        File.WriteAllText(Path.Combine(AuditRoot, ".neon", "audits"), "a file where the folder would go");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains("so the audit didn't start", output);
        Assert.DoesNotContain("Another NeonSidekick", output);
    }

    [Fact]
    public Task CodeAudit_Discard_IsRefused_WhenAnotherAppTookTheReportSinceItEnded() => WithOtherAuditAppAsync(async () =>
    {
        string? path = null;
        AuditFixture(Line("/code-audit src/"), input =>
        {
            // At the done pane: the audit's own lock is gone, and another NeonSidekick resumes the report meanwhile.
            path = NeonSidekick.Audits.AuditFiles.List(AuditFiles()).Single().Path;
            WriteOtherAuditLock(path);
            input.Push([Keys.Char('d'), Keys.Enter]);
        }, Keys2(Keys.Char('y'), Keys.Enter), Line("/exit"));
        ThreePasses();

        string output = await RunAsync();

        Assert.Contains(AuditText.LockedError(path!), output);
        Assert.True(File.Exists(Path.Combine(AuditRoot, path!)));
        Assert.True(File.Exists(AuditLockFile(path!)));
    });

    [Fact]
    public Task CodeAudit_ALockTakenOverMidPass_StopsTheAudit_AndWritesNoMore() => WithOtherAuditAppAsync(async () =>
    {
        AuditFixture(Line("/code-audit src security"), Line("/exit"));
        _chat.Enqueue(ReadCall("r1"));        // 0: pass 1 reads the file
        _chat.Enqueue(FindingCall("a1"));     // 1: another app takes the lock first; then present_audit
        _chat.EnqueueText("Pass 1 done.");    // 2
        string? path = null;
        string? before = null;
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 1 && update == 0)
            {
                path = NeonSidekick.Audits.AuditFiles.List(AuditFiles()).Single().Path;
                File.Delete(AuditLockFile(path));
                WriteOtherAuditLock(path);
                before = File.ReadAllText(Path.Combine(AuditRoot, path));
            }

            return Task.CompletedTask;
        };

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);   // no second pass
        Assert.Contains(AuditText.LockLostNotice(Path.GetFileNameWithoutExtension(path!)), output);
        Assert.Contains(_chat.Requests[2], m => m.Contents.OfType<FunctionResultContent>().Any(r => r.Result?.ToString() == AuditText.CouldNotSaveResult(AuditText.LockedError(path!))));
        Assert.Equal(before, File.ReadAllText(Path.Combine(AuditRoot, path!)));   // the report as the other app has it
        Assert.False(File.Exists(Path.Combine(AuditRoot, NeonSidekick.Audits.AuditFiles.CoveragePath(path!).Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(AuditLockFile(path!)));   // the other's
        Assert.DoesNotContain(AuditText.DoneKeys(hasFix: true), output);
    });
}
