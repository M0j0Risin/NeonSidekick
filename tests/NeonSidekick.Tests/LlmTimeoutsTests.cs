using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class LlmTimeoutsTests
{
    [Fact]
    public void Defaults_Are3600And21600_TheCeilingsThemselves()
    {
        var resolved = LlmTimeouts.Resolve(new AppSettingsData());
        Assert.Equal(TimeSpan.FromSeconds(3600), resolved.Request);
        Assert.Equal(TimeSpan.FromSeconds(21600), resolved.Turn);
        Assert.Equal(LlmTimeouts.Default, resolved);
        Assert.Equal(LlmTimeouts.MaxRequestSeconds, LlmTimeouts.DefaultRequest.TotalSeconds);
        Assert.Equal(LlmTimeouts.MaxTurnSeconds, LlmTimeouts.DefaultTurn.TotalSeconds);
    }

    [Fact]
    public void Ceilings_AreAnHourForARequest_SixForATurn()
    {
        Assert.Equal(3600, LlmTimeouts.MaxRequestSeconds);
        Assert.Equal(21600, LlmTimeouts.MaxTurnSeconds);
    }

    [Fact]
    public void RequestNotBelowTurn_IsAdjustedAndWarned()
    {
        var warnings = Capture(out var unsubscribe);
        try
        {
            var resolved = LlmTimeouts.Resolve(new AppSettingsData { LlmRequestTimeoutSeconds = 100, LlmTurnTimeoutSeconds = 90 });

            Assert.Equal(TimeSpan.FromSeconds(75), resolved.Request);   // floor(90 * 75/90)
            Assert.Equal(TimeSpan.FromSeconds(90), resolved.Turn);
            var warning = Assert.Single(warnings, w => w.Message.Contains("not below the turn budget", StringComparison.Ordinal));
            Assert.Contains(EnvironmentOverrides.TurnTimeoutVariable, warning.Message);
        }
        finally
        {
            unsubscribe();
        }
    }

    [Fact]
    public void LoweringOnlyTheTurn_LeavesTheRequestAlone_WhileItStaysAbove()
    {
        var resolved = LlmTimeouts.Resolve(new AppSettingsData { LlmTurnTimeoutSeconds = 7200 });
        Assert.Equal(TimeSpan.FromSeconds(3600), resolved.Request);
        Assert.Equal(TimeSpan.FromSeconds(7200), resolved.Turn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(21601)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void OutOfRangeSavedValue_FallsBackToDefault_NeverClamped(double seconds)
    {
        var warnings = Capture(out var unsubscribe);
        try
        {
            var resolved = LlmTimeouts.Resolve(new AppSettingsData { LlmRequestTimeoutSeconds = seconds, LlmTurnTimeoutSeconds = seconds });
            Assert.Equal(LlmTimeouts.Default, resolved);
            Assert.Single(warnings, w => w.Message.Contains("out of range (more than 0, at most 3600 seconds)", StringComparison.Ordinal));
            Assert.Single(warnings, w => w.Message.Contains("out of range (more than 0, at most 21600 seconds)", StringComparison.Ordinal));
        }
        finally
        {
            unsubscribe();
        }
    }

    [Fact]
    public void TheCeilingsAreSeparate_AnHourLongRequestIsRefused_WhereTheTurnIsNot()
    {
        var warnings = Capture(out var unsubscribe);
        try
        {
            var resolved = LlmTimeouts.Resolve(new AppSettingsData { LlmRequestTimeoutSeconds = 3601, LlmTurnTimeoutSeconds = 3601 });

            Assert.Equal(LlmTimeouts.DefaultRequest, resolved.Request);
            Assert.Equal(TimeSpan.FromSeconds(3601), resolved.Turn);
            var warning = Assert.Single(warnings);
            Assert.Contains("LlmRequestTimeoutSeconds=3601 is out of range (more than 0, at most 3600 seconds)", warning.Message);
        }
        finally
        {
            unsubscribe();
        }
    }

    [Fact]
    public void EnvironmentValues_ReachTheResolverThroughApplyTo()
    {
        var env = new EnvironmentOverrides(name => name switch
        {
            EnvironmentOverrides.RequestTimeoutVariable => "150",
            EnvironmentOverrides.TurnTimeoutVariable => "200.5",
            _ => null,
        });

        var resolved = LlmTimeouts.Resolve(env.ApplyTo(new AppSettingsData { LlmRequestTimeoutSeconds = 10, LlmTurnTimeoutSeconds = 20 }));

        Assert.Equal(TimeSpan.FromSeconds(150), resolved.Request);
        Assert.Equal(TimeSpan.FromSeconds(200.5), resolved.Turn);
    }

    [Fact]
    public void Format_IsInvariantWithUpToTwoDecimals()
    {
        Assert.Equal("7.5s", LlmTimeouts.Format(TimeSpan.FromSeconds(7.5)));
        Assert.Equal("90s", LlmTimeouts.Format(TimeSpan.FromSeconds(90)));
        Assert.Equal("0.33s", LlmTimeouts.Format(TimeSpan.FromSeconds(1.0 / 3)));
    }

    private static List<DiagnosticEvent> Capture(out Action unsubscribe)
    {
        var events = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> handler = e =>
        {
            if (e.Category == "Llm" && e.Level >= DiagnosticLevel.Warning)
            {
                lock (events) events.Add(e);
            }
        };
        DiagnosticLog.Emitted += handler;
        unsubscribe = () => DiagnosticLog.Emitted -= handler;
        return events;
    }
}
