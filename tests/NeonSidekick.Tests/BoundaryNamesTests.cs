using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="BoundaryNames"/> (2026-10-09, the review of the lock): a connection's names read once a minute, a failed read never kept,
/// each connection's own.
/// </summary>
public sealed class BoundaryNamesTests
{
    [Fact]
    public async Task TheNames_AreKeptAMinute_PerConnection_AndAFailureIsNot()
    {
        var clock = new ManualTimeProvider();
        var names = new BoundaryNames(clock);
        int reads = 0;
        Task<SqlRun> Read(CancellationToken _)
        {
            reads++;
            return Task.FromResult(new SqlRun(SqlOutcome.Ok, "", "c", "", [new SqlGrid(["name"], [["shop"], ["payroll"]], false)], TimeSpan.Zero));
        }

        var (rows, failed) = await names.GetAsync(BoundaryNames.Key("c", "h", "shop"), Read, CancellationToken.None);
        Assert.Null(failed);
        Assert.Equal(["shop", "payroll"], rows!.Select(r => r[0]));
        await names.GetAsync(BoundaryNames.Key("c", "h", "shop"), Read, CancellationToken.None);
        Assert.Equal(1, reads);
        await names.GetAsync(BoundaryNames.Key("c", "h2", "shop"), Read, CancellationToken.None);   // an entry that points elsewhere reads afresh
        Assert.Equal(2, reads);
        clock.Advance(BoundaryNames.Lifetime);
        await names.GetAsync(BoundaryNames.Key("c", "h", "shop"), Read, CancellationToken.None);
        Assert.Equal(3, reads);

        int tries = 0;
        Task<SqlRun> Fail(CancellationToken _)
        {
            tries++;
            return Task.FromResult(SqlRun.Refused(SqlOutcome.ConnectFailed, "down", "d"));
        }

        Assert.Equal(SqlOutcome.ConnectFailed, (await names.GetAsync("d", Fail, CancellationToken.None)).Failed!.Outcome);
        await names.GetAsync("d", Fail, CancellationToken.None);
        Assert.Equal(2, tries);
    }
}
