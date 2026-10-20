using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Network;
using NeonSidekick.Services;
using NeonSidekick.Tasks;

namespace NeonSidekick.Tests;

/// <summary>
/// The admin families' real readers on this PC (2026-10-15, the admin-tools plan), read-only: the Service Control Manager's list and one
/// service whole, the Task Scheduler's tasks and one definition parsed, an echo to loopback, the current user's certificate store, and the
/// domain join's question. Nothing is started, stopped, run or changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdminWindowsTests
{
    [WindowsFact]
    public void Services_TheListAndTheEventLogServiceWhole()
    {
        var system = new WindowsServiceSystem();
        var (list, detail, rpc) = system.Run(ServiceTarget.Local, s => (s.List(drivers: false), s.Detail("EventLog"), s.Detail("RpcSs")));

        Assert.Contains(list, e => e.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase) && e.State == ServiceRunState.Running);
        Assert.DoesNotContain(list, e => e.IsDriver);
        Assert.Equal(ServiceRunState.Running, detail.Status.State);
        Assert.Contains("svchost", detail.Config.ImagePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(rpc.DependentsKnown ? rpc.Dependents.Count > 0 : !LiveAdmin.Elevated);   // RpcSs lists its dependents only to an elevated account
        Assert.Contains(system.Run(ServiceTarget.Local, s => s.List(drivers: true)), e => e.IsDriver);
        Assert.Equal(1060, Assert.Throws<ServiceException>(() => system.Run(ServiceTarget.Local, s => s.Detail("NoSuchServiceAnywhere"))).Code);
    }

    [WindowsFact]
    public void Tasks_WindowsOwn_AndOneDefinitionParsed()
    {
        var system = new WindowsTaskSystem();
        var tasks = system.Run(TaskTarget.Local, s => s.List(@"\Microsoft\Windows", recursive: true));
        Assert.NotEmpty(tasks);
        Assert.All(tasks, t => Assert.StartsWith(@"\Microsoft\Windows\", t.Path, StringComparison.OrdinalIgnoreCase));
        var first = tasks[0];
        var detail = system.Run(TaskTarget.Local, s => s.Detail(first.Path));
        var definition = TaskXml.Parse(detail.Xml);
        Assert.NotNull(definition.Principal);
        Assert.Equal(first.Path, detail.Entry.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Throws<TaskException>(() => system.Run(TaskTarget.Local, s => s.Detail(@"\No\Such\Task")));
    }

    /// <summary>
    /// The session's own account (2026-10-20, a review: another account's task is privileged): ConnectedUser and ConnectedDomain read from
    /// their vtable slots, this PC's token's SID with them; a task's entry read alone; a service name holding a NUL refused at the floor.
    /// </summary>
    [WindowsFact]
    public void Tasks_TheSessionsOwnAccount_AndAnEntryAlone()
    {
        var system = new WindowsTaskSystem();
        var (self, entry, first) = system.Run(TaskTarget.Local, s =>
        {
            var listed = s.List(@"\Microsoft\Windows", recursive: true)[0];
            return (s.Self, s.Entry(listed.Path), listed);
        });
        Assert.Equal(string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase), self.Contains(Environment.UserName, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(Environment.UserDomainName + "\\" + Environment.UserName, self, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, self);
        Assert.Equal(first.Path, entry.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(123, Assert.Throws<ServiceException>(() => new WindowsServiceSystem().Run(ServiceTarget.Local, s => s.Status("EventLog\0x"))).Code);
    }

    [WindowsFact]
    public async Task Ping_LoopbackAnswers()
    {
        var hop = await new WindowsPinger().PingAsync(IPAddress.Loopback, 64, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(PingStatus.Reply, hop.Status);
        Assert.Equal(IPAddress.Loopback, hop.From);
    }

    [WindowsFact]
    public void Certificates_TheUsersStoreReads()
    {
        var listing = CertificateStores.Read(CertificateStores.StoreName("root")!, StoreLocation.CurrentUser);
        Assert.Null(listing.Error);
        Assert.NotEmpty(listing.Certificates);
    }

    [WindowsFact]
    public void DomainJoin_AnswersWithoutThrowing()
    {
        string? joined = DomainJoin.Current;
        Assert.True(joined is null || joined.Trim().Length > 0);   // a joined PC's DNS domain, or null on a workgroup PC like this one
    }
}
