using Microsoft.Extensions.AI;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Computers;
using NeonSidekick.EventLog;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Services;
using NeonSidekick.Tasks;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 3 of the admin review's fixes (2026-10-23): what the model reads — untrusted text (A4, B12, C6, the fence sweep), redaction (A3, C5,
/// C3, E2, W3–W5, V10, X1, X2, I1), arguments and echoes (R18, E7, K6), and the refusals' wording (L4, L5).
/// </summary>
public sealed partial class AdminFixesTests
{
    // ── Untrusted text ──────────────────────────────────────────────────────

    /// <summary>A4: free text keeps its joiners and marks but shows a bidi override as typed; a name escapes every disguise.</summary>
    [Fact]
    public void A4_TheBidiOverrides_AreShownAsTyped_TheJoinersKept()
    {
        const string family = "👨\u200D👩\u200D👧";
        const string persian = "می\u200Cخواهم";
        Assert.Equal(family + " " + persian, UntrustedText.Scrub(family + " " + persian));
        Assert.Equal("svc\\u202Eexe.dll", UntrustedText.Scrub("svc\u202Eexe.dll"));
        Assert.Equal("svc\\u202Eexe\\u200Bx", UntrustedText.Name("svc\u202Eexe\u200Bx"));
        string question = ServiceText.ConfirmQuestion("stop", "Spool\u202Ereve", "Print\nSpooler", "this PC", "LocalSystem", "C:\\x.exe");
        Assert.False(question.Contains('\u202E'));
        Assert.Contains("Spool\\u202Ereve", question);
        Assert.False(TaskText.ConfirmRun("\\Ops\\Job\u202E", "this PC", "SYSTEM", "least privilege", "cmd").Contains('\u202E'));
    }

    /// <summary>B12, C6: a cut never splits a surrogate pair, never ends inside a fence, and keeps the notes after it.</summary>
    [Fact]
    public void B12_C6_CutsKeepPairsFencesAndNotes()
    {
        string cut = UntrustedText.Cut(new string('a', 8) + "😀", 9);
        Assert.Equal(new string('a', 8) + "…", cut);
        string body = "head " + UntrustedText.Fenced(new string('x', 200));
        string capped = UntrustedText.Capped(body, "\nnote", 60, "\n(cut)");
        Assert.EndsWith("\n(cut)\nnote", capped);
        Assert.Equal(capped.Split(UntrustedText.QuoteOpen).Length, capped.Split(UntrustedText.QuoteClose).Length);
        Assert.True(capped.Length <= 60);
    }

    // ── Redaction ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("tool --api_key=abc123", "abc123")]
    [InlineData("tool --client_secret=s3cr3t", "s3cr3t")]
    [InlineData("az storage --account-key KEYVALUE", "KEYVALUE")]
    [InlineData("aws --access-key AKIA123", "AKIA123")]
    [InlineData("gh --auth-token ghp_abc", "ghp_abc")]
    [InlineData("{ \"password\": \"hunter2\" }", "hunter2")]
    [InlineData("<Password>hunter2</Password>", "hunter2")]
    [InlineData("https://x.example/cb?access_token=tok123&x=1", "tok123")]
    [InlineData("refresh_token=rt-1", "rt-1")]
    public void A3_C5_TheNewSecretForms_AreHidden(string text, string secret)
    {
        string redacted = ToolRedaction.Text(text);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(ToolRedaction.Placeholder, redacted);
    }

    [Theory]
    [InlineData("token_type=bearer")]
    [InlineData("passwordLastSet: 2026-10-01")]
    [InlineData("the token for the tunnel is renewed hourly")]
    [InlineData("pwdLastSet=0")]
    [InlineData("--token-type id")]
    [InlineData("<TokenElevationType>%%1936</TokenElevationType>")]
    public void A3_C5_TheirHarmlessNeighbors_Stay(string text) => Assert.Equal(text, ToolRedaction.Text(text));

    /// <summary>W5: the new secret field names; TokenElevationType is kept.</summary>
    [Theory]
    [InlineData("AccessToken", true)]
    [InlineData("refresh_token", true)]
    [InlineData("SasToken", true)]
    [InlineData("Credentials", true)]
    [InlineData("Passphrase", true)]
    [InlineData("AccountKey", true)]
    [InlineData("PIN", true)]
    [InlineData("TokenElevationType", false)]
    [InlineData("ElevatedToken", false)]   // DP7: 4624's yes or no
    [InlineData("KeyLength", false)]
    [InlineData("TargetUserName", false)]
    public void W5_SecretFieldNames(string name, bool secret) => Assert.Equal(secret, EventLogRedaction.IsSecretField(name));

    /// <summary>W3, W4: raw data is its length alone under redacted; a UserData Data element is named by its Name.</summary>
    [Fact]
    public void W3_W4_BinaryAndUserDataNames()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><Provider Name="X"/><EventID>1</EventID></System>
              <EventData><Data>a</Data><Binary>414243444546</Binary></EventData>
            </Event>
            """;
        var fields = EventXml.Parse(xml).Fields;
        var binary = Assert.Single(fields, f => f.Binary);
        Assert.Equal(EventLogText.BinaryLength(6), EventLogRedaction.Field(binary));
        const string user = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><Provider Name="X"/><EventID>1</EventID></System>
              <UserData><Thing><Data Name="Password">hunter2</Data></Thing></UserData>
            </Event>
            """;
        var field = Assert.Single(EventXml.Parse(user).Fields);
        Assert.Equal("Password", field.Name);
        Assert.Equal(ToolRedaction.Placeholder, EventLogRedaction.Field(field));
    }

    /// <summary>V10: the trusts' keys and the LSA secrets are never read.</summary>
    [Fact]
    public void V10_TrustAndLsaSecrets_AreNeverRead()
    {
        foreach (string name in new[] { "trustAuthIncoming", "trustAuthOutgoing", "initialAuthIncoming", "initialAuthOutgoing", "currentValue", "priorValue", "msPKI-CredentialRoamingTokens" })
        {
            Assert.True(AdAttributes.NeverReadName(name), name);
        }
    }

    /// <summary>X1, X2: the home is ~ before any delimiter and in any case; other accounts' homes are hidden; /Users/Shared stays.</summary>
    [Fact]
    public void X1_X2_TheMacsHomes()
    {
        var places = MacLogPlaces.Under("/Users/ann");
        Assert.Equal("<string>~</string> PATH=~:/bin a ~. ~/x /Users/anne", places.Tilde("<string>/Users/ann</string> PATH=/Users/ann:/bin a /USERS/ann. /users/ann/x /Users/anne"));
        Assert.Equal("/Users/<redacted>/Library and /Users/Shared/x", MacLogPlaces.OtherHomes("/Users/bob/Library and /Users/Shared/x"));
        Assert.Equal("~ /Users/<redacted>/x", MacFileLogs.Redactor(places, redacted: true)("/Users/ann /Users/bob/x"));
    }

    /// <summary>E2, DA5: under redacted the server is never asked about descriptions; they are matched here on their redacted text.</summary>
    [Fact]
    public void E2_ARedactedDescriptionSearch_NeverAsksTheServer()
    {
        var system = new FakeDirectorySystem();
        const string root = "DC=lab,DC=example";
        system.Add(root);
        system.Add("CN=svc1,OU=Svc," + root, ("objectClass", "user"), ("objectCategory", "person"), ("sAMAccountName", "svc1"), ("description", "password=Hunter2 for the app"));
        system.Add("CN=svc2,OU=Svc," + root, ("objectClass", "user"), ("objectCategory", "person"), ("sAMAccountName", "svc2"), ("description", "the backup account"));
        var domain = new DirectoryDomain("lab", new DirectoryDomainConfig { Domain = "lab.example", Server = "localhost" });
        var access = new DirectoryAccess(system, () => new DirectoryCatalog([domain], []), () => ["lab"], () => null, _time);
        var source = access.Domain(null, forceRedacted: true).Source!;
        DirectoryAnswer Find(string words) => system.Run(source.Target, s => DirectoryQueries.FindAnswer(s, source, new FindOptions("field", 0, words, 50, "user", Field: "description"), _time.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.Empty(Find("Hunter2").Rows());
        Assert.DoesNotContain(system.Filters, f => f.Contains("description=*Hunter2", StringComparison.OrdinalIgnoreCase));
        Assert.Single(Find("backup").Rows());
        string text = DirectoryRender.ForModel(Find("app"));
        Assert.DoesNotContain("Hunter2", text);
        Assert.Contains("matched on their redacted text", text);
    }

    /// <summary>
    /// DP7 (the branch review, 2026-10-23): a scan's window cut is no cap, more matches than the cap are, and a scan the deadline stopped before
    /// its first object offers no next call (it would be the same one).
    /// </summary>
    [Fact]
    public void DP7_ARedactedScan_CountsItsOwnCut_AndNeverRepeatsItself()
    {
        var system = new FakeDirectorySystem();
        const string root = "DC=lab,DC=example";
        system.Add(root);
        for (int i = 0; i < DirectoryLimits.RedactedScan + 3; i++)
        {
            string n = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            system.Add("CN=u" + n + ",OU=People," + root, ("objectClass", "user"), ("objectCategory", "person"), ("sAMAccountName", "u" + n), ("description", i < 2 ? "the app account" : "staff"));
        }

        var domain = new DirectoryDomain("lab", new DirectoryDomainConfig { Domain = "lab.example", Server = "localhost" });
        var access = new DirectoryAccess(system, () => new DirectoryCatalog([domain], []), () => ["lab"], () => null, _time);
        var source = access.Domain(null, forceRedacted: true).Source!;
        DirectoryAnswer Find(string words, int cap, int from = 0) => system.Run(source.Target, s => DirectoryQueries.FindAnswer(s, source,
            new FindOptions("field", 0, words, 50, "user", Field: "description", Cap: cap, ScanFrom: from), _time.GetUtcNow(), TimeZoneInfo.Utc));

        var windowed = Find("app", cap: 50);
        Assert.Equal(2, windowed.Rows().Count());
        Assert.False(windowed.Partial);
        Assert.Contains("more are left, next:", DirectoryRender.ForModel(windowed));
        Assert.True(Find("app", cap: 1).Partial);

        system.TimesOut = _ => true;
        string stalled = DirectoryRender.ForModel(Find("app", cap: 50, from: DirectoryLimits.RedactedScan));
        Assert.Contains(DirectoryText.ScanUnreached("description", DirectoryLimits.RedactedScan), stalled);
        Assert.DoesNotContain("next:", stalled);
    }

    // ── Arguments and echoes ───────────────────────────────────────────────

    /// <summary>E7, K6: a JSON null is not given; an echoed argument is one line, cut and escaped.</summary>
    [Fact]
    public void E7_K6_NullIsNotGiven_AndEchoesAreCut()
    {
        var args = new AIFunctionArguments { ["days"] = System.Text.Json.JsonDocument.Parse("null").RootElement, ["n"] = 3 };
        Assert.False(AdminArguments.Given(args, "days"));
        Assert.True(AdminArguments.Given(args, "n"));
        string echoed = ArgumentText.NotBoolean("flag", new string('x', 2_000_000));
        Assert.True(echoed.Length < 120);
        Assert.Contains("\\u202E", ServiceText.BadState("x\u202Ey"));
        Assert.Equal(AdminArguments.WholeSecond(DateTimeOffset.UnixEpoch.AddMilliseconds(1300), up: true), DateTimeOffset.UnixEpoch.AddSeconds(2));
    }

    // ── The refusals' wording (DP1) ────────────────────────────────────────

    /// <summary>L4, L5: no refusal names the key that would widen the model's access, nor hands it a cmdkey line.</summary>
    [Fact]
    public void L4_L5_RefusalsNameTheUsersAction_NotTheKey()
    {
        var family = ServiceAccess.Family;
        foreach (string text in new[]
        {
            ComputerText.ReadOnlyComputer("app01", family), ComputerText.NotInControl("Spooler", "app01", family), ComputerText.NoCredential("NeonSidekick/services/app01", family),
            TaskText.Privileged(@"\Job", "SYSTEM"), TaskText.MicrosoftTask(@"\Microsoft\X"), ServiceText.ProtectedByFile("W3SVC"),
            EventLogText.NoCredential("NeonSidekick/eventlog/dc"), DirectoryText.NoCredential("NeonSidekick/ad/lab"), EventLogText.LogNotAllowed("Security", "dc"),
        })
        {
            Assert.DoesNotContain("readwrite", text);
            Assert.DoesNotContain("\"privileged\"", text);
            Assert.DoesNotContain("\"control\"", text);
            Assert.DoesNotContain("cmdkey", text);
            Assert.DoesNotContain(".json", text);
        }
    }
}

/// <summary>A test's look at an answer's rows.</summary>
internal static class DirectoryAnswerRows
{
    public static IReadOnlyList<DirectoryRow> Rows(this DirectoryAnswer answer) => answer.Tables.SelectMany(t => t.Rows).ToList();
}
