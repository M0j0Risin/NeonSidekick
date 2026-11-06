#:project ../src/NeonSidekick/NeonSidekick.csproj
#:property PublishAot=false

// ReleaseSign: the release signing tool (2026-11-04, self-update; the user's pick: the private key stays on their PC, and one local command
// signs each release). A copy of the app refuses a release that isn't signed under a key it embeds (Updates/UpdateKeys).
//
//     dotnet run tools/ReleaseSign.cs --new-key <file>
//         Writes a new P-256 key, encrypted under a password this asks for, and prints its public half for UpdateKeys. Run it twice, for
//         the daily key and the offline backup, and back up both files. Refuses a path inside a git work tree, where the file could be
//         committed, or inside the app's home, where the model's tools work, and never overwrites a file.
//
//     dotnet run tools/ReleaseSign.cs publish vX.Y.Z --key <file> [--key <file>]
//         Signs the draft CI made for that tag and publishes it. It stops on a tag no newer than the latest published and on an archive no copy would
//         ask for, shows the tag's commit and the hashes for you to confirm, checks that CI built that commit, signs each .sha256 (one line per key),
//         checks each key against UpdateKeys.cs at the tag (what the new release will trust) and the .sig against the keys of every version published
//         since the first with an updater, uploads the .sig files, publishes, and then asks GitHub whether this tag is what copies will now be offered.
//         A platform missing from the draft, or a published version the signature doesn't reach, asks for a word typed out.
//         Safe to run again after a failure: nothing is live until the last step.
//
// The password is only ever typed at the terminal, never taken as an argument, so it can't land in a shell's history or a chat. With its
// input redirected this stops rather than read a password from a pipe.
//
// A .NET 10 file-based app: no csproj, not in the solution, not run by build.ps1. The signature itself is UpdateSignature's, shared with the
// app, so what this writes is what the app accepts.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Updates;

const int MinimumPassword = 12;
const string BuildCommitName = "build-commit.txt";
const string KeysPath = "src/NeonSidekick/Updates/UpdateKeys.cs";
const string FirstWithKeys = "0.5.7";
string[] platforms = ["win-x64", "osx-arm64"];   // what build.yml packages

if (args is ["--new-key", var path])
{
    return NewKey(path);
}

if (args is ["publish", var tag, .. var rest] && KeyFiles(rest) is { Count: > 0 } keyFiles)
{
    try
    {
        return Publish(tag, keyFiles, platforms);
    }
    catch (Exception failure) when (failure is StopException or JsonException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
        // A failure past the publish is a release that's live and couldn't be confirmed (2026-11-04, review P4-7): "Nothing was published." sent a rerun into "already published".
        Console.Error.WriteLine(failure.Message);
        Console.Error.WriteLine(PublishState.Done
            ? $"{tag} IS published and live. This couldn't confirm the rest: check what releases/latest answers."
            : PublishState.Unknown ? $"This can't tell whether {tag} went live. Look at the release's page before running this again." : "Nothing was published.");
        return 1;
    }
}

Console.Error.WriteLine("Usage: dotnet run tools/ReleaseSign.cs --new-key <file>");
Console.Error.WriteLine("       dotnet run tools/ReleaseSign.cs publish vX.Y.Z --key <file> [--key <file>]");
return 1;

static List<string>? KeyFiles(string[] rest)
{
    var files = new List<string>();
    for (int i = 0; i < rest.Length; i += 2)
    {
        if (rest[i] != "--key" || i + 1 >= rest.Length)
        {
            return null;
        }

        files.Add(rest[i + 1]);
    }

    return files;
}

static int Publish(string tag, List<string> keyFiles, string[] platforms)
{
    if (!UpdateVersion.TryParseTag(tag, out var tagVersion))
    {
        throw new StopException($"{tag} isn't a plain vX.Y.Z tag, which is all a copy of the app would accept.");
    }

    if (Console.IsInputRedirected)
    {
        throw new StopException("This asks you to confirm and for each key's password at the terminal, and its input is redirected. Run it in your own terminal.");
    }

    foreach (string file in keyFiles)
    {
        if (!File.Exists(file))
        {
            throw new StopException($"{file} doesn't exist.");
        }
    }

    string repository = new Uri(AboutText.RepositoryUrl).AbsolutePath.Trim('/');

    // Nothing stops two drafts sharing one tag, and a live release's .sig files must never be replaced, so it stops unless exactly one has the tag and that one is still a draft.
    using var list = JsonDocument.Parse(Gh("api", $"repos/{repository}/releases?per_page=100"));
    var matches = list.RootElement.EnumerateArray().Where(r => r.GetProperty("tag_name").GetString() == tag).ToList();
    if (matches.Count == 0)
    {
        throw new StopException($"No release has the tag {tag} yet. CI makes the draft when the tag's build finishes, so check that run and the tag's spelling.");
    }

    if (matches.Count != 1)
    {
        throw new StopException($"{matches.Count} releases have the tag {tag}. Delete the extras, so there's one draft.");
    }

    var release = matches[0];
    if (!release.GetProperty("draft").GetBoolean())
    {
        throw new StopException($"{tag} is already published, and its .sig files are never replaced.");
    }

    // Publishing always passes --latest, so an older draft signed now would move releases/latest backward and tell every copy nothing's newer (2026-11-04, review P17-1).
    var published = list.RootElement.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean()).Select(r => r.GetProperty("tag_name").GetString() ?? "").ToList();
    foreach (string other in published)
    {
        if (UpdateVersion.TryParseTag(other, out var version) && version >= tagVersion)
        {
            throw new StopException($"{other} is already published, and {tag} is no newer. Copies would be told nothing's newer.");
        }
    }

    var assets = release.GetProperty("assets").EnumerateArray().ToList();
    var shaAssets = assets.Where(a => a.GetProperty("name").GetString()!.EndsWith(".sha256", StringComparison.Ordinal)).ToList();
    if (shaAssets.Count == 0)
    {
        throw new StopException($"The draft for {tag} holds no .sha256 file.");
    }

    if (!assets.Any(a => a.GetProperty("name").GetString() == BuildCommitName))
    {
        throw new StopException($"The draft for {tag} holds no {BuildCommitName}, so nothing says which commit CI built. Delete the draft and run CI on the tag again.");
    }

    // The tag's commit comes from the commits API: a release's targetCommitish only says main.
    using var commit = JsonDocument.Parse(Gh("api", $"repos/{repository}/commits/{tag}"));
    string commitHash = commit.RootElement.GetProperty("sha").GetString()!;
    string commitLine = commit.RootElement.GetProperty("commit").GetProperty("message").GetString()!.Split('\n')[0];
    var publishedKeys = PublishedKeys(repository, published);

    // What the new release will trust, from the tag and not this checkout (2026-11-04, review P10-2): a wrong key pasted at the tag and fixed here afterwards signed cleanly, and that release could never update again.
    var tagKeys = KeysIn(GhOrNotFound("api", "-H", "Accept: application/vnd.github.raw", $"repos/{repository}/contents/{KeysPath}?ref={tag}") ?? "");
    if (tagKeys.Count == 0)
    {
        throw new StopException($"{KeysPath} at {tag} isn't there or holds no key, so that release could never update again.");
    }

    string folder = Path.Combine(Path.GetTempPath(), "neonsidekick-release-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        Gh("release", "download", tag, "--repo", repository, "--pattern", "*.sha256", "--pattern", BuildCommitName, "--dir", folder);

        // The confirmation below shows the tag's commit, and nothing else ties the draft to it (2026-11-04, review P3-15): CI skips a release that already exists, so a moved tag kept the old draft, and this would sign the old build under the new commit's subject.
        string built = File.ReadAllText(Path.Combine(folder, BuildCommitName)).Trim();
        if (!string.Equals(built, commitHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new StopException($"This draft was built from {built}, and {tag} is at {commitHash}. Delete the draft and run CI on the tag again.");
        }

        var signing = new List<(string ShaPath, byte[] Bytes, string SigPath)>();
        Console.WriteLine($"{tag}, commit {commitHash}");
        Console.WriteLine($"  {commitLine}");
        Console.WriteLine();
        foreach (var asset in shaAssets)
        {
            string name = asset.GetProperty("name").GetString()!;
            string shaPath = Path.Combine(folder, name);
            byte[] bytes = File.ReadAllBytes(shaPath);
            string archive = name[..^".sha256".Length];

            // The exact name a copy asks for under this tag (2026-11-04, reviews P3-16, P10-7): a v0.5.8 draft holding a v0.5.7 archive, or a -win64 one, would publish, and every copy would report no package.
            if (!platforms.Any(platform => archive == UpdateCheck.ArchiveName(tagVersion, platform)))
            {
                throw new StopException($"{archive} isn't a name a copy asks for under {tag} ({string.Join(" or ", platforms.Select(platform => UpdateCheck.ArchiveName(tagVersion, platform)))}).");
            }

            if (UpdateShaFile.TryParse(Encoding.ASCII.GetString(bytes)) is not { } sha)
            {
                throw new StopException($"{name} doesn't read as a release's two lines (the archive's hash, then the manifest's).");
            }

            if (sha.ArchiveName != archive)
            {
                throw new StopException($"{name} names {sha.ArchiveName}, so every copy would refuse it.");
            }

            var archiveAsset = assets.FirstOrDefault(a => a.GetProperty("name").GetString() == archive);
            if (archiveAsset.ValueKind == JsonValueKind.Undefined)
            {
                throw new StopException($"The draft holds {name} but no {archive}.");
            }

            // Signing a pair every user would refuse is the mistake this catches, without downloading 210 MB. GitHub may leave a draft's digest out.
            if (archiveAsset.TryGetProperty("digest", out var digest) && digest.GetString() is { Length: > 0 } text)
            {
                string github = text.StartsWith("sha256:", StringComparison.Ordinal) ? text["sha256:".Length..] : text;
                if (!UpdateHash.Same(github, sha.ArchiveHash))
                {
                    throw new StopException($"GitHub's digest for {archive} is {github}, and {name} says {sha.ArchiveHash}. They must match, or every copy refuses it.");
                }

                Console.WriteLine($"  {archive}");
                Console.WriteLine($"    {sha.ArchiveHash} (matches GitHub's digest)");
            }
            else
            {
                Console.WriteLine($"  {archive}");
                Console.WriteLine($"    {sha.ArchiveHash} (GitHub gave no digest to compare)");
            }

            Console.WriteLine($"    manifest {sha.ManifestHash}");
            signing.Add((shaPath, bytes, shaPath + ".sig"));
        }

        Console.WriteLine();
        if (LooksGenerated(release.TryGetProperty("body", out var body) ? body.GetString() : null))
        {
            Console.WriteLine("Warning: the notes are still only the generated changelog, and the Install pane shows them. gh release edit --notes-file sets them.");
            Console.WriteLine();
        }

        // A pair deleted by hand while the notes were being fixed left every copy on that platform with "no package" (review P10-7). It asks, since a platform's build can fail while the other's release still matters.
        foreach (string platform in platforms)
        {
            string asked = UpdateCheck.ArchiveName(tagVersion, platform);
            if (!shaAssets.Any(asset => asset.GetProperty("name").GetString() == asked + ".sha256"))
            {
                Confirm($"*** The draft holds no {asked}. EVERY COPY ON {platform} WILL BE TOLD THERE'S NO PACKAGE. ***", "publish-without");
            }
        }

        Console.Write("Sign and publish this? Type yes: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.Ordinal))
        {
            throw new StopException("Not confirmed.");
        }

        // One .sig line per key, so a new key can sign beside the old one until old copies have moved on. A wrong key file stops here, at a draft, before it can publish a release every copy refuses.
        var keys = new List<ECDsa>();
        try
        {
            foreach (string file in keyFiles)
            {
                string password = ReadPassword($"Password for {file}: ");
                try
                {
                    keys.Add(UpdateSignature.LoadKeyFile(File.ReadAllText(file), password));
                }
                catch (CryptographicException)
                {
                    throw new StopException($"{file} didn't open with that password.");
                }
                catch (ArgumentException)
                {
                    throw new StopException($"{file} holds no key.");   // review P2-15: this was a stack trace
                }
            }

            var stranded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, bytes, sigPath) in signing)
            {
                // Each key's own line (2026-11-04, review P1-15): any one line passing let a wrong second key through, against BUILD.md's "A wrong key stops at the draft".
                var lines = keys.Select(key => UpdateSignature.SignatureLine(bytes, key)).ToList();
                for (int i = 0; i < lines.Count; i++)
                {
                    if (!UpdateSignature.Verify(bytes, lines[i], tagKeys))
                    {
                        throw new StopException($"{keyFiles[i]} isn't a key {tag} embeds ({KeysPath}), so it can't sign {Path.GetFileName(sigPath)}.");
                    }
                }

                string text = string.Concat(lines.Select(line => line + "\n"));
                File.WriteAllText(sigPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                foreach (var (version, trusted) in publishedKeys)
                {
                    if (!UpdateSignature.Verify(bytes, text, trusted))
                    {
                        stranded.Add(version);
                    }
                }
            }

            // What installed copies embed, not this source (review P2-13): after a key change, signing with the new key alone passed here, and every copy refused the release. Every version since the first with an updater (review P10-4), as copies stay on any of them. BUILD.md allows one case that can't pass, losing both keys, so this asks rather than stopping for good.
            if (stranded.Count > 0)
            {
                Confirm($"*** No signature here verifies under the keys of {string.Join(", ", stranded)}. EVERY COPY STILL ON THAT WILL REFUSE THIS RELEASE, and has to update by hand. ***", "publish-anyway");
            }
        }
        finally
        {
            keys.ForEach(key => key.Dispose());
        }

        // --clobber: without it, a rerun after a run that failed partway is refused for the assets already there.
        Gh(["release", "upload", tag, "--repo", repository, "--clobber", .. signing.Select(s => s.SigPath)]);
        Console.WriteLine("Signatures uploaded.");

        // A failure can come after GitHub applied the edit, a dropped connection on gh's follow-up read, so it asks (2026-11-04, review P10-8): "Nothing was published." over a live release sent a rerun into "already published".
        string[] edit = ["release", "edit", tag, "--repo", repository, "--draft=false", "--latest"];
        if (GhRun(edit, out string editFailure) is null)
        {
            switch (IsLive(repository, tag))
            {
                case true:
                    Console.Error.WriteLine($"gh reported a failure, but {tag} is live: {editFailure}");
                    break;
                case false:
                    throw new StopException($"gh {string.Join(' ', edit)} failed: {editFailure}");
                default:
                    PublishState.Unknown = true;
                    throw new StopException($"gh {string.Join(' ', edit)} failed: {editFailure}");
            }
        }

        PublishState.Done = true;
        Console.WriteLine($"{tag} is published.");
    }
    finally
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Couldn't delete {folder}: {ex.Message}");
        }
    }

    // GitHub's docs don't say a published draft becomes latest, and they sort it by the commit's date. If this isn't the tag, no copy is offered it.
    using var latest = JsonDocument.Parse(Gh("api", $"repos/{repository}/releases/latest"));
    string? offered = latest.RootElement.GetProperty("tag_name").GetString();
    if (offered != tag)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"*** releases/latest answers {offered}, not {tag}. NO COPY WILL BE OFFERED THIS RELEASE until it does. ***");
        return 1;
    }

    Console.WriteLine($"releases/latest answers {tag}, so copies will be offered it.");
    return 0;
}

// Whether the tag's release is live, or null if GitHub can't say.
static bool? IsLive(string repository, string tag)
{
    if (GhRun(["api", $"repos/{repository}/releases?per_page=100"], out _) is not { } text)
    {
        return null;
    }

    try
    {
        using var list = JsonDocument.Parse(text);
        var matches = list.RootElement.EnumerateArray().Where(r => r.GetProperty("tag_name").GetString() == tag).ToList();
        return matches.Count == 1 ? !matches[0].GetProperty("draft").GetBoolean() : null;
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        return null;
    }
}

// The keys of every version published since the first with an updater, as copies stay on any of them (2026-11-04, review P10-4: only the latest's were read, so a release that stranded an older version passed quietly). A version from before the updater has no such file and isn't asked; a later tag whose file has moved asks, since the check couldn't run. A network failure stops, since running this again is fine.
static List<(string Tag, List<string> Keys)> PublishedKeys(string repository, IEnumerable<string> published)
{
    var found = new List<(string, List<string>)>();
    foreach (string tag in published)
    {
        if (!UpdateVersion.TryParseTag(tag, out var version) || !UpdateVersion.TryParse(FirstWithKeys, out var first) || version < first)
        {
            continue;
        }

        var keys = KeysIn(GhOrNotFound("api", "-H", "Accept: application/vnd.github.raw", $"repos/{repository}/contents/{KeysPath}?ref={tag}") ?? "");
        if (keys.Count > 0)
        {
            found.Add((tag, keys));
        }
        else
        {
            Confirm($"*** {KeysPath} at {tag} isn't there or holds no key, so the keys copies on it trust WEREN'T CHECKED. ***", "publish-unchecked");
        }
    }

    return found;
}

// The keys a source file holds. Read left to right, strings and comments alike, so a key-shaped string in a comment doesn't count (2026-11-04, review P10-3: the house style keeps a retired key in its why-comment, and a release signed with only that passed) and a key's own base64 can hold // with no harm.
static List<string> KeysIn(string source) =>
    Regex.Matches(source, "\"(?<text>[^\"\\r\\n]*)\"|//[^\\r\\n]*|/\\*.*?\\*/", RegexOptions.Singleline)
        .Where(match => match.Groups["text"].Success)
        .Select(match => match.Groups["text"].Value)
        .Where(text => text.Length >= 40 && UpdateSignature.IsPublicKey(text))
        .ToList();

// A word of its own to type, so a yes meant for the question above can't answer this one.
static void Confirm(string warning, string word)
{
    Console.WriteLine();
    Console.WriteLine(warning);
    Console.Write($"Go on anyway? Type {word}: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), word, StringComparison.Ordinal))
    {
        throw new StopException("Not confirmed.");
    }
}

static bool LooksGenerated(string? notes)
{
    if (string.IsNullOrWhiteSpace(notes))
    {
        return true;
    }

    // --generate-notes writes a "What's Changed" list and a "Full Changelog" link. Text of your own has lines of neither.
    return notes.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).All(line =>
        line.StartsWith("## What's Changed", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("**Full Changelog**", StringComparison.Ordinal));
}

static string Gh(params string[] arguments) =>
    GhRun(arguments, out string failure) ?? throw new StopException($"gh {string.Join(' ', arguments)} failed: {failure}");

// Null only for a 404: anything else that failed stops.
static string? GhOrNotFound(params string[] arguments)
{
    if (GhRun(arguments, out string failure) is { } output)
    {
        return output;
    }

    return failure.Contains("HTTP 404", StringComparison.Ordinal) ? null : throw new StopException($"gh {string.Join(' ', arguments)} failed: {failure}");
}

static string? GhRun(string[] arguments, out string failure)
{
    var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    Process? started;
    try
    {
        started = Process.Start(start);
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
        throw new StopException($"gh didn't start, so GitHub's command line may not be installed: {ex.Message}");   // review P2-15: this was a stack trace
    }

    using var process = started ?? throw new StopException("gh didn't start.");
    var error = process.StandardError.ReadToEndAsync();
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    failure = error.GetAwaiter().GetResult().Trim();
    return process.ExitCode == 0 ? output : null;
}

static int NewKey(string path)
{
    string full = Path.GetFullPath(path);
    if (Refusal(full) is { } refusal)
    {
        Console.Error.WriteLine(refusal);
        return 1;
    }

    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("This asks for the key's password at the terminal, and its input is redirected. Run it in your own terminal.");
        return 1;
    }

    string password = ReadPassword("New key password: ");
    if (password.Length < MinimumPassword)
    {
        Console.Error.WriteLine($"Use at least {MinimumPassword} characters: this password is what stands in the way if the file is ever taken.");
        return 1;
    }

    if (ReadPassword("Again: ") != password)
    {
        Console.Error.WriteLine("Those didn't match. Nothing was written.");
        return 1;
    }

    using ECDsa key = UpdateSignature.NewKey();
    string text = UpdateSignature.EncryptedKeyFile(key, password);
    Write(full, text);

    // Read back what's on the disk, so a key that can't be opened is found now and not at a release.
    using ECDsa opened = UpdateSignature.LoadKeyFile(File.ReadAllText(full), password);
    byte[] probe = Encoding.ASCII.GetBytes("ReleaseSign's own check");
    string publicKey = UpdateSignature.PublicKeyText(key);
    if (!UpdateSignature.Verify(probe, UpdateSignature.SignatureLine(probe, opened), [publicKey]))
    {
        Console.Error.WriteLine($"{full} doesn't open to the key just made. Delete it and try again.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"Wrote {full}");
    Console.WriteLine("Back this file up: a lost key can't be made again. Its public half, for UpdateKeys:");
    Console.WriteLine();
    Console.WriteLine(publicKey);
    return 0;
}

// Asked before the password, so a bad path costs no typing. A .git anywhere above is what makes a file committable, and it reads the same
// through a subst drive or a link in the path, which comparing with one repository's path wouldn't.
static string? Refusal(string full)
{
    if (File.Exists(full) || Directory.Exists(full))
    {
        return $"{full} already exists, and this never overwrites a key.";
    }

    string? folder = Path.GetDirectoryName(full);
    if (folder is null || !Directory.Exists(folder))
    {
        return $"The folder for {full} doesn't exist.";
    }

    for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
    {
        if (Directory.Exists(Path.Combine(at, ".git")) || File.Exists(Path.Combine(at, ".git")))
        {
            return $"{full} is inside the git work tree at {at}, where it could be committed. Choose a folder outside it.";
        }
    }

    string home = AppSettings.ResolveStorageDirectory(new EnvironmentOverrides(Environment.GetEnvironmentVariable).Home);
    var comparison = CaseRule.Comparison(home);
    if (full.StartsWith(Path.TrimEndingDirectorySeparator(home) + Path.DirectorySeparatorChar, comparison))
    {
        return $"{full} is inside the app's home at {home}, where the model's tools work. Choose a folder outside it.";
    }

    return null;
}

static void Write(string full, string text)
{
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    using var writer = new StreamWriter(full, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), options);
    writer.Write(text);
    writer.Write('\n');
}

static string ReadPassword(string prompt)
{
    Console.Write(prompt);
    var typed = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return typed.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            typed.Length = Math.Max(0, typed.Length - 1);
        }
        else if (!char.IsControl(key.KeyChar))
        {
            typed.Append(key.KeyChar);
        }
    }
}

/// <summary>A reason to stop.</summary>
sealed class StopException(string message) : Exception(message);

/// <summary>Whether the release went live, for what a failure says.</summary>
static class PublishState
{
    public static bool Done { get; set; }

    public static bool Unknown { get; set; }
}
