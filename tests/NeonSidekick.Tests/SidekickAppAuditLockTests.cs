using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/code-audit</c>'s report lock headless (2026-10-29), as on the screen: let go at the run's end, resume and fix refused while another live
/// NeonSidekick holds it, and an audit whose lock another app took over mid-pass stopped with nothing more written.
/// </summary>
public partial class SidekickAppTests
{
    private const int OtherHeadlessPid = 424242;

    private string HeadlessAuditLock(string report) => Path.Combine(HeadlessAuditRoot, AuditFiles.LockPathFor(report).Replace('/', Path.DirectorySeparatorChar));

    private void WriteHeadlessOtherLock(string report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HeadlessAuditLock(report))!);
        File.WriteAllText(HeadlessAuditLock(report), "pid: 424242\nstarted: 2026-10-29T09:00:00.0000000+00:00\n");
    }

    private static async Task WithOtherHeadlessAppAsync(Func<Task> body)
    {
        var alive = OwnerLock.LiveOwner;
        OwnerLock.LiveOwner = owner => owner.ProcessId == OtherHeadlessPid || owner == OwnerLock.Me;
        try
        {
            await body();
        }
        finally
        {
            OwnerLock.LiveOwner = alive;
        }
    }

    [Fact]
    public Task Headless_CodeAudit_TheLockGoesWithTheRun_AndResumeAndFixAreRefusedWhileAnotherAppHoldsIt() => WithOtherHeadlessAppAsync(async () =>
    {
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _chat.EnqueueText("Pass 1.");   // no present_audit: the audit stops after pass 1 and its follow-up, its report kept
        _chat.EnqueueText("Still pass 1.");

        await Headless("/code-audit src\n");
        string path = HeadlessAuditPath;
        Assert.False(File.Exists(HeadlessAuditLock(path)));   // let go at the run's end

        WriteHeadlessOtherLock(path);
        string output = await Headless("/code-audit resume\n/code-audit fix\n", exitCode: 4);

        Assert.Equal(2, output.Split("[error] " + AuditText.LockedError(path)).Length - 1);   // the error lines; the failed summary names it once more
        Assert.Equal(2, _chat.Requests.Count);   // the pass and its follow-up; nothing after
        Assert.True(File.Exists(HeadlessAuditLock(path)));
    });

    [Fact]
    public Task Headless_CodeAudit_ALockTakenOverMidPass_StopsIt_AndWritesNoMore() => WithOtherHeadlessAppAsync(async () =>
    {
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _chat.Enqueue(FakeChatClient.Call("r1", ReadFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "src/a.cs" }));
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"SQL built from input\",\"detail\":\"Joined.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        string? path = null;
        string? before = null;
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            if (request == 1 && update == 0)
            {
                path = HeadlessAuditPath;
                File.Delete(HeadlessAuditLock(path));
                WriteHeadlessOtherLock(path);
                before = File.ReadAllText(Path.Combine(HeadlessAuditRoot, path));
            }

            return Task.CompletedTask;
        };

        string output = await Headless("/code-audit src\n", exitCode: 4);   // a stopped audit is a failed run (2026-11-21)

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(AuditText.LockLostNotice(Path.GetFileNameWithoutExtension(path!)), output);
        Assert.Equal(before, File.ReadAllText(Path.Combine(HeadlessAuditRoot, path!)));
        Assert.True(File.Exists(HeadlessAuditLock(path!)));
    });
}
