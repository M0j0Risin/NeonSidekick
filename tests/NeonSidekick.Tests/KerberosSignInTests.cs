using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// A Mac's <c>runas</c> (2026-10-11): the account it can sign in as, <c>name@REALM</c> with the realm in capitals
/// (<see cref="KerberosSignIn.Principal"/>, portable), and the account rule the wizard asks per system
/// (<see cref="SqlConnectionConfig.RunAsAccountProblem"/>). The sign-in itself needs a KDC: the live proof is the spike's and
/// <c>--sql-check</c>'s against the user's server.
/// </summary>
public sealed class KerberosSignInTests
{
    [Theory]
    [InlineData("svc-reader@ad.example.edu", "svc-reader@AD.EXAMPLE.EDU")]
    [InlineData("  svc-sql@CORP.EXAMPLE.COM ", "svc-sql@CORP.EXAMPLE.COM")]
    [InlineData("Jane.Doe@Ad.Example.Edu", "Jane.Doe@AD.EXAMPLE.EDU")]   // the name as typed, the realm in capitals
    public void Principal_PutsTheRealmInCapitals(string account, string expected) =>
        Assert.Equal(expected, KerberosSignIn.Principal(account));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("svc")]
    [InlineData(@"CONTOSO\svc")]                 // a NetBIOS domain names no realm
    [InlineData(@"CONTOSO\svc@contoso.com")]
    [InlineData("@AD.EXAMPLE.EDU")]
    [InlineData("svc@")]
    [InlineData("svc@a@b")]
    [InlineData("svc reader@AD.EXAMPLE.EDU")]
    public void Principal_RefusesWhatIsNoKerberosName(string? account) =>
        Assert.Null(KerberosSignIn.Principal(account));

    [MacFact]
    public void OnAMac_RunAs_TakesNameAtRealmAlone()
    {
        Assert.True(KerberosSignIn.Offered);
        Assert.Null(SqlConnectionConfig.RunAsAccountProblem("svc@ad.example.edu"));
        Assert.Equal(SqlText.RunAsNeedsRealm(@"CONTOSO\svc"), SqlConnectionConfig.RunAsAccountProblem(@" CONTOSO\svc "));
        Assert.Equal(SqlText.RunAsNeedsRealm(""), SqlConnectionConfig.RunAsAccountProblem(null));
        Assert.Equal(
            "on a Mac, runas signs in through Kerberos and needs the account as name@REALM in \"user\", e.g. svc-reader@AD.EXAMPLE.EDU (got 'svc')",
            SqlText.RunAsNeedsRealm("svc"));
        Assert.Equal("The account to sign in as through Kerberos: name@REALM (e.g. svc-reader@AD.EXAMPLE.EDU).", App.SettingsMenu.SqlWizardRunAsUserQuestion);

        // The file's rule stays Windows' everywhere, so a sql.json shared with a Windows PC loads whole; the connect refuses it here.
        Assert.Null(new SqlConnectionConfig { Server = "x", Database = "db", Auth = "runas", User = @"CONTOSO\svc" }.Problem);

        // What it refuses it says in a Mac's words, never Windows' DOMAIN\name hint (code review of 522af7e).
        Assert.Equal(SqlText.RunAsNeedsRealm("svc"), new SqlConnectionConfig { Server = "x", Database = "db", Auth = "runas", User = "svc" }.Problem);
        Assert.Equal(SqlText.RunAsNeedsRealm(""), new SqlConnectionConfig { Server = "x", Database = "db", Auth = "runas" }.Problem);
    }

    [WindowsFact]
    public void OnWindows_RunAs_TakesEitherForm_AsBefore()
    {
        Assert.False(KerberosSignIn.Offered);
        Assert.Null(SqlConnectionConfig.RunAsAccountProblem(@"CONTOSO\svc"));
        Assert.Null(SqlConnectionConfig.RunAsAccountProblem("svc@contoso.com"));
        Assert.Equal(SqlText.RunAsNeedsDomain("svc"), SqlConnectionConfig.RunAsAccountProblem("svc"));
        Assert.Equal("The Windows account to sign in as: DOMAIN\\name or name@domain.", App.SettingsMenu.SqlWizardRunAsUserQuestion);
    }

    [Fact]
    public void AFailedKerberosSignIn_SaysWhoseAndTheStatus_ClaimingNoCause() =>
        Assert.Equal(
            "the Kerberos sign-in as svc@AD.EXAMPLE.EDU failed (GenericFailure); check its stored password, that the realm is right, and that the account may sign in to this server",
            SqlText.KerberosFailed("svc@AD.EXAMPLE.EDU", "GenericFailure"));
}
