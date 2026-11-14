using System.Text.Json.Nodes;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>settings:roundtrip</c> (2026-11-13, plan parsed-yawning-papert): <c>/settings export</c> and <c>import</c> in the trimmed NativeAOT exe,
    /// which <c>dotnet test</c> (JIT) can't prove: the zip written and read, the manifest's source-generated JSON, and the <see cref="JsonNode"/>
    /// edit of <c>profile.json</c>. A scratch home with one profile (a key encrypted on the other system, a missing working folder, a token and a
    /// key a newer version wrote) is exported and imported into a second scratch home; the written file must have the resets and keep the unknown
    /// key. Touches neither the real home nor Downloads; both scratch folders are deleted.
    /// </summary>
    public static SmokeCheck ProbeSettingsRoundtrip()
    {
        const string name = "settings:roundtrip";
        string scratch = Path.Combine(Path.GetTempPath(), "neon-smoke-settings-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string from = Path.Combine(scratch, "from");
            string to = Path.Combine(scratch, "to");
            string downloads = Path.Combine(scratch, "downloads");
            string profile = Profiles.Directory(from, "smoke");
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(to);
            string foreignKey = OperatingSystem.IsWindows() ? "keychain:AAAA" : "dpapi:AAAA";
            string missing = OperatingSystem.IsWindows() ? @"C:\neon-smoke-missing-folder" : "/neon-smoke-missing-folder";
            var seed = new JsonObject
            {
                ["LlmApiKey"] = foreignKey,
                ["WorkingDirectory"] = missing,
                ["WebUiToken"] = "token",
                ["FutureSetting"] = 1,
            };
            File.WriteAllText(Path.Combine(profile, Profiles.FileName), seed.ToJsonString(SettingsImportFixups.Indented));

            var exported = SettingsArchive.ExportToFile(from, null, downloads, SidekickApp.Version, DateTimeOffset.Now, CancellationToken.None);
            if (exported.Error is { } exportError)
            {
                return new SmokeCheck(name, false, "export: " + exportError);
            }

            string zip = Directory.EnumerateFiles(downloads, "*.zip").Single();
            var imported = SettingsArchive.ImportFromFile(to, zip, ImportEnvironment.Live, CancellationToken.None);
            if (imported.Error is { } importError)
            {
                return new SmokeCheck(name, false, "import: " + importError);
            }

            var written = JsonNode.Parse(File.ReadAllText(Profiles.ProfileFile(to, "smoke")))!.AsObject();
            bool good = (string?)written["LlmApiKey"] == Llm.LlmEndpoint.DefaultApiKey
                && (string?)written["WorkingDirectory"] == ""
                && (string?)written["WebUiToken"] == ""
                && written["FutureSetting"] is not null;
            return new SmokeCheck(name, good, good ? "exported, imported, the key, folder and token reset and an unknown key kept" : "the imported profile.json: " + written.ToJsonString());
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }
}
