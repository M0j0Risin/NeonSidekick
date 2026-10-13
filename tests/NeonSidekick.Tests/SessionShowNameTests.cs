using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class SessionShowNameTests
{
    [Theory]
    [InlineData("all", SessionNameDisplay.AllNames, true)]
    [InlineData("ALL-NAMES", SessionNameDisplay.AllNames, true)]
    [InlineData(" model-written ", SessionNameDisplay.ModelWritten, true)]
    [InlineData("none", SessionNameDisplay.None, true)]
    [InlineData("everything", SessionNameDisplay.AllNames, false)]
    [InlineData("", SessionNameDisplay.AllNames, false)]
    [InlineData(null, SessionNameDisplay.AllNames, false)]
    public void TryParse_AcceptsTheThreeWords(string? text, SessionNameDisplay expected, bool ok)
    {
        Assert.Equal(ok, SessionShowName.TryParse(text, out var display));
        Assert.Equal(expected, display);
    }

    [Fact]
    public void Names_Default_Name_AndDescribe_ArePinned()
    {
        Assert.Equal(new[] { "all", "model-written", "none" }, SessionShowName.Names);
        Assert.Equal("all", SessionShowName.Default);   // the user's call, 2026-09-18
        Assert.Equal("all", SessionShowName.Name(SessionNameDisplay.AllNames));
        Assert.Equal("model-written", SessionShowName.Name(SessionNameDisplay.ModelWritten));
        Assert.Equal("none", SessionShowName.Name(SessionNameDisplay.None));
        Assert.Equal("every session name shows on the rule above the input row", SessionShowName.Describe("all"));
        Assert.Equal("only a model-written or typed name shows; the first line never does", SessionShowName.Describe("model-written"));
        Assert.Equal("the rule stays bare", SessionShowName.Describe("none"));
        Assert.Equal("", SessionShowName.Describe("other"));
        Assert.Equal(SessionShowName.Default, new AppSettingsData().SessionShowName);
    }

    [Fact]
    public void Resolve_WarnsOnAHandEditedValue_AndUsesTheDefault()
    {
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Sessions") { warnings.Add(e); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(SessionNameDisplay.None, SessionShowName.Resolve(new AppSettingsData { SessionShowName = "none" }));
            Assert.Empty(warnings);
            Assert.Equal(SessionNameDisplay.AllNames, SessionShowName.Resolve(new AppSettingsData { SessionShowName = "some" }));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Equal(DiagnosticLevel.Warning, warning.Level);
        Assert.Equal("SessionShowName='some' is not one of all, model-written, none. Using all.", warning.Message);
    }
}
