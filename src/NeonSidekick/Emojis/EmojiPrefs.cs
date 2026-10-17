using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Emojis;

/// <summary>
/// What the avatar picker remembers (2026-10-10): the emoji last picked, newest first, at most <see cref="RecentLimit"/>, and the skin tone last
/// chosen (0 none, 1 light … 5 dark), where the tone grid opens. One file per profile since 2026-10-17 (the user's ask: the recent emoji
/// belong to the profile that picked them, the tone with them), <c>&lt;profile&gt;/configs/emoji.json</c> (<see cref="PathFor"/>); one for the
/// home until then (<c>&lt;home&gt;/configs/emoji.json</c>, the home's top until 2026-10-16), no longer read — no migration, the user's call.
/// Best effort both ways: a missing or unreadable file is no history, a failed save a warning in the log.
/// </summary>
public sealed class EmojiPrefs
{
    /// <summary>The file's name in the profile's <c>configs</c>.</summary>
    public const string FileName = "emoji.json";

    /// <summary>How many picks the Recent tab keeps.</summary>
    public const int RecentLimit = 24;

    private const string Category = "Emoji";

    private readonly string _path;

    private EmojiPrefs(string path, List<string> recent, int tone)
    {
        _path = path;
        Recent = recent;
        Tone = tone;
    }

    /// <summary>The emoji picked, newest first.</summary>
    public IReadOnlyList<string> Recent { get; private set; }

    /// <summary>The skin tone last chosen: 0 none, 1 light … 5 dark.</summary>
    public int Tone { get; private set; }

    /// <summary><c>&lt;profile&gt;/configs/emoji.json</c> (2026-10-17, <see cref="Settings.Profiles.ConfigPath"/>; the home's until then).</summary>
    public static string PathFor(string profileDirectory) => Settings.Profiles.ConfigPath(profileDirectory, FileName);

    /// <summary>The prefs of the profile in <paramref name="profileDirectory"/>: empty when there is no file or it does not read.</summary>
    public static EmojiPrefs Load(string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        string path = PathFor(profileDirectory);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), EmojiJsonContext.Default.EmojiPrefsFile) is { } file)
            {
                var recent = (file.Recent ?? []).Select(AvatarRules.Normalize).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).Take(RecentLimit).ToList();
                return new EmojiPrefs(path, recent, Math.Clamp(file.Tone, 0, EmojiCatalog.ToneCount));
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, EmojiText.PrefsUnreadable(path, e.Message));
        }

        return new EmojiPrefs(path, [], 0);
    }

    /// <summary>
    /// <paramref name="emoji"/> picked, in <paramref name="tone"/> when it was chosen on the tone grid (null: the tone kept): first
    /// in <see cref="Recent"/> (moved up when it was there), the list cut to <see cref="RecentLimit"/>, and saved.
    /// </summary>
    public void Picked(string emoji, int? tone = null)
    {
        ArgumentNullException.ThrowIfNull(emoji);
        Recent = [emoji, .. Recent.Where(e => !string.Equals(e, emoji, StringComparison.Ordinal)).Take(RecentLimit - 1)];
        if (tone is int chosen)
        {
            Tone = Math.Clamp(chosen, 0, EmojiCatalog.ToneCount);
        }

        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json = JsonSerializer.Serialize(new EmojiPrefsFile { Recent = [.. Recent], Tone = Tone }, EmojiJsonContext.Default.EmojiPrefsFile);
            Files.WorkingDirectory.WriteAtomically(_path, new System.Text.UTF8Encoding(false).GetBytes(json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, EmojiText.PrefsUnsaved(_path, e.Message));
        }
    }
}

/// <summary><c>&lt;profile&gt;/configs/emoji.json</c> as written.</summary>
public sealed class EmojiPrefsFile
{
    public List<string>? Recent { get; set; }

    public int Tone { get; set; }
}

/// <summary>The source-generated context for <c>&lt;profile&gt;/configs/emoji.json</c> (2026-10-10; the home's until 2026-10-17). Reflection serialisation is off.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(EmojiPrefsFile))]
public sealed partial class EmojiJsonContext : JsonSerializerContext;
