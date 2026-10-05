using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Settings;

/// <summary>
/// Loads, holds and persists <see cref="AppSettingsData"/> for the loaded profile.
///
/// <para>Two files. The root <c>settings.json</c> under <see cref="StorageDirectory"/> is the
/// pointer (<see cref="RootSettingsData"/>): only which profile is loaded. The settings themselves
/// are <c>profiles\&lt;name&gt;\profile.json</c> (<see cref="FilePath"/>), and
/// <see cref="SwitchProfileAsync"/> swaps the loaded data for another profile's in place, so every
/// holder of this store sees the switch through <see cref="Current"/>. A root file from before
/// profiles (the full settings, no <c>Profile</c> property) is migrated once into the default
/// profile, together with a root <c>memory.json</c> / <c>persona.md</c>.</para>
///
/// <para><see cref="Current"/> is a snapshot, not the live object. Handing out the live instance
/// would let a caller change a value without <see cref="Changed"/> ever firing, which is the
/// failure that looks like the UI ignoring you. <see cref="Update"/> is the only mutation; saving
/// is debounced and atomic (temp file then <c>File.Move</c>), and the target path is captured when
/// the save is scheduled: a debounced write for the old profile must never land in the new one.</para>
/// </summary>
public sealed class AppSettings : IDisposable
{
    /// <summary>The root pointer file.</summary>
    public const string FileName = "settings.json";
    public const string DefaultDirectoryName = ".neonsidekick";
    /// <summary>The log category of every settings line, <see cref="Profiles"/>' too.</summary>
    public const string Category = "Settings";

    /// <summary>
    /// How long a burst of edits coalesces. Long enough that a menu that touches six fields
    /// writes once; short enough that a user who changes something and immediately kills the
    /// process still keeps it. Settable (2026-09-30) so a test can hold a save in the debounce: the v0.3.6 CI
    /// run's screen started slower than 250 ms, and the fixture's save landed before <c>/profile edit</c> looked.
    /// Read when a save is scheduled.
    /// </summary>
    internal TimeSpan SaveDebounce { get; set; } = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private AppSettingsData _data;
    private string _profileName;

    private CancellationTokenSource? _pendingSave;
    private bool _disposed;

    /// <param name="storageDirectory">The home: <c>settings.json</c>, <c>models</c> and <c>profiles</c> live under it. Created on first save if absent.</param>
    /// <param name="profileOverride">
    /// The profile this launch loads instead of the pointer's (2026-09-26, <c>--profile</c> / <c>NEONSIDEKICK_PROFILE</c>,
    /// the user's ask: a scripted headless run names its profile rather than following the last one clicked into).
    /// Matched as <see cref="Profiles.Resolve"/> matches, loaded under its listed spelling. Unlike the pointer it is
    /// the caller's word, so it is honoured as given: a temporary profile loads (the redirect guards against the
    /// pointer stranding a launch there, not against asking), and an unknown name throws <see cref="ArgumentException"/>
    /// with <see cref="UnknownProfileMessage"/> rather than falling back — a script must not quietly run in the wrong
    /// profile. The pointer is never rewritten for it (only created, as the default, when there is none); a later
    /// <see cref="SwitchProfileAsync"/> rewrites it as always. Null = the pointer decides.
    /// </param>
    public AppSettings(string storageDirectory, string? profileOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        StorageDirectory = storageDirectory;
        PointerPath = Path.Combine(storageDirectory, FileName);

        if (profileOverride is not null)
        {
            _profileName = Profiles.Resolve(storageDirectory, profileOverride)
                ?? throw new ArgumentException(UnknownProfileMessage(profileOverride, Profiles.List(storageDirectory)), nameof(profileOverride));
            _data = Load(FilePath);
            if (!File.Exists(PointerPath))
            {
                TrySavePointer(Profiles.DefaultName);
            }

            DiagnosticLog.Info(Category, OverrideProfileNotice(_profileName));
            EnsureProfileDirectories();
            return;
        }

        var pointer = LoadPointer(PointerPath);
        bool rewritePointer = !File.Exists(PointerPath);

        string? resolved = Profiles.Resolve(storageDirectory, pointer.Profile);
        if (resolved is null)
        {
            DiagnosticLog.Warn(Category, MissingProfileWarning(pointer.Profile));
            resolved = Profiles.DefaultName;
            rewritePointer = true;
        }
        else if (Profiles.IsTemporary(resolved))
        {
            // A temporary profile is never where a launch lands (2026-09-24): default instead, the profile itself kept.
            DiagnosticLog.Info(Category, TemporaryProfileNotice(resolved));
            resolved = Profiles.DefaultName;
            rewritePointer = true;
        }

        _profileName = resolved;
        _data = Load(FilePath);

        if (rewritePointer)
        {
            TrySavePointer(resolved);
        }

        EnsureProfileDirectories();
    }

    /// <summary>The home directory: the pointer, the models and the profiles.</summary>
    public string StorageDirectory { get; }

    /// <summary>The full path of the root pointer file.</summary>
    public string PointerPath { get; }

    /// <summary>The models directory: <c>models</c> under the home, so <c>NEONSIDEKICK_HOME</c> moves both; shared by every profile.</summary>
    public string ModelsDirectory => Path.Combine(StorageDirectory, "models");

    /// <summary>The embedded model's downloads (2026-09-29): <c>models/llm</c> under the home, one folder per catalog model, every profile's.</summary>
    public string EmbeddedModelsDirectory => Path.Combine(ModelsDirectory, "llm");

    /// <summary>The llama.cpp runtimes the embedded model runs on (2026-09-29): <c>llama</c> under the home, one folder per build and backend.</summary>
    public string LlamaDirectory => Path.Combine(StorageDirectory, "llama");

    /// <summary>The user's themes (2026-10-01): <c>themes</c> under the home, every profile's (<see cref="UI.ThemeCatalog"/>).</summary>
    public string ThemesDirectory => Path.Combine(StorageDirectory, UI.ThemeCatalog.DirectoryName);

    /// <summary>The global skills folder: <c>skills</c> under the home, every profile's (<c>Skills.SkillRoots</c>).</summary>
    public string GlobalSkillsDirectory => Path.Combine(StorageDirectory, Skills.SkillRoots.DirectoryName);

    /// <summary>The loaded profile's own skills folder: <c>skills</c> under <see cref="ProfileDirectory"/>.</summary>
    public string ProfileSkillsDirectory => Path.Combine(ProfileDirectory, Skills.SkillRoots.DirectoryName);

    /// <summary>The loaded profile's own splash folder: <c>splash</c> under <see cref="ProfileDirectory"/>, whose pictures stand in for the embedded set (<see cref="UI.SplashImages.FromDirectory"/>).</summary>
    public string ProfileSplashDirectory => Path.Combine(ProfileDirectory, UI.SplashImages.ProfileFolderName);

    /// <summary>The loaded profile's ComfyUI workflows folder: <c>comfy</c> under <see cref="ProfileDirectory"/> (2026-09-24), read ahead of <see cref="GlobalComfyDirectory"/> (<see cref="Comfy.ComfyWorkflowCatalog"/>).</summary>
    public string ProfileComfyDirectory => Path.Combine(ProfileDirectory, Comfy.ComfyWorkflowCatalog.DirectoryName);

    /// <summary>The global ComfyUI workflows folder: <c>comfy</c> under the home, every profile's (2026-09-24).</summary>
    public string GlobalComfyDirectory => Path.Combine(StorageDirectory, Comfy.ComfyWorkflowCatalog.DirectoryName);

    /// <summary>The loaded profile's name, spelt as its directory is.</summary>
    public string ProfileName
    {
        get { lock (_gate) { return _profileName; } }
    }

    /// <summary>The loaded profile's directory: where its <c>profile.json</c>, <c>memory.json</c>, <c>persona.md</c>, <c>operata.md</c> and <c>vocalia.md</c> live.</summary>
    public string ProfileDirectory => Profiles.Directory(StorageDirectory, ProfileName);

    /// <summary>The full path of the loaded profile's settings file.</summary>
    public string FilePath => Profiles.ProfileFile(StorageDirectory, ProfileName);

    /// <summary>Raised after a change is applied (or a profile switch), on the calling thread.</summary>
    public event Action<AppSettingsData>? Changed;

    /// <summary>An immutable-by-convention snapshot of the saved values.</summary>
    public AppSettingsData Current
    {
        get { lock (_gate) { return Copy(_data); } }
    }

    /// <summary>The warning when the pointer names a profile that is not there. Pinned.</summary>
    public static string MissingProfileWarning(string name) =>
        $"Profile \"{name}\" does not exist; loading {Profiles.DefaultName}.";

    /// <summary>
    /// What an unknown <c>--profile</c> / <c>NEONSIDEKICK_PROFILE</c> ends the launch with (2026-09-26), the profiles
    /// there are named so the script's author can fix the name. Pinned.
    /// </summary>
    public static string UnknownProfileMessage(string name, IReadOnlyList<string> available) =>
        $"Profile \"{name}\" does not exist. Profiles: {string.Join(", ", available)}.";

    /// <summary>The log line when a launch loads a named profile over the pointer (2026-09-26). Pinned.</summary>
    public static string OverrideProfileNotice(string name) =>
        $"Loading profile \"{name}\" for this launch; {FileName} left as it is.";

    /// <summary>The note when the pointer names a temporary profile (<see cref="Profiles.IsTemporary"/>, 2026-09-24). Pinned.</summary>
    public static string TemporaryProfileNotice(string name) =>
        $"Profile \"{name}\" is temporary (starts with {Profiles.TemporaryPrefix}); loading {Profiles.DefaultName}.";

    /// <summary>The warning when a skills folder could not be created (2026-09-18); the app runs on, the catalog reads an absent root as empty. Pinned.</summary>
    public static string SkillsFolderWarning(string path, string detail) =>
        $"Could not create the skills folder {path}: {detail}";

    /// <summary>The warning when the profile's splash folder could not be created (2026-09-22); the app runs on, and an absent folder is the embedded set. Pinned.</summary>
    public static string SplashFolderWarning(string path, string detail) =>
        $"Could not create the splash folder {path}: {detail}";

    /// <summary>The warning when the profile's ComfyUI workflows folder could not be created (2026-09-24); the app runs on, and an absent folder is no workflow. Pinned.</summary>
    public static string ComfyFolderWarning(string path, string detail) =>
        $"Could not create the ComfyUI workflows folder {path}: {detail}";

    /// <summary>
    /// The folders a loaded profile is given exist: the two skills roots (2026-09-18, the user's
    /// call) — <see cref="GlobalSkillsDirectory"/> and <see cref="ProfileSkillsDirectory"/> — and
    /// <see cref="ProfileSplashDirectory"/> (2026-09-22, the user's ask: nothing created the splash
    /// folder before, so using it meant making it by hand; empty, it is no splash at all, since
    /// <see cref="UI.SplashImages.FromDirectory"/> reads a folder with no picture as none and the
    /// embedded set stands). Made whenever a profile is loaded — the constructor, a switch (the one
    /// after <c>/profile add</c> included), a reset of the loaded profile, a reload. Never for a
    /// profile that is not loaded (nothing is created for a look). A failure is a warning, never an
    /// exception. Named <c>EnsureSkillsDirectories</c> until the splash folder joined it. <see cref="ProfileComfyDirectory"/>
    /// joined on 2026-09-24, so the place a ComfyUI workflow goes is there to be found.
    /// </summary>
    private void EnsureProfileDirectories()
    {
        foreach ((string path, Func<string, string, string> warning) in new (string, Func<string, string, string>)[]
        {
            (GlobalSkillsDirectory, SkillsFolderWarning),
            (ProfileSkillsDirectory, SkillsFolderWarning),
            (ProfileSplashDirectory, SplashFolderWarning),
            (ProfileComfyDirectory, ComfyFolderWarning),
        })
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(Category, warning(path, ex.Message));
            }
        }
    }

    /// <summary>
    /// Resolves the settings directory: an explicit override (<c>NEONSIDEKICK_HOME</c>), else
    /// <c>%USERPROFILE%\.neonsidekick</c>, else next to the binary when no profile exists.
    /// </summary>
    public static string ResolveStorageDirectory(string? overrideDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return Path.Combine(profile, DefaultDirectoryName);
        }

        return Path.Combine(AppContext.BaseDirectory, DefaultDirectoryName);
    }

    /// <summary>
    /// Applies a mutation, raises <see cref="Changed"/> and schedules a save.
    ///
    /// <para>The mutation runs under the lock; the event is raised outside it. Raising while
    /// holding a lock runs arbitrary subscriber code with it on the stack, which is the shape
    /// that makes deadlocks.</para>
    /// </summary>
    public void Update(Action<AppSettingsData> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        AppSettingsData before;
        AppSettingsData snapshot;
        string path;
        lock (_gate)
        {
            before = Copy(_data);
            mutate(_data);
            snapshot = Copy(_data);
            path = Profiles.ProfileFile(StorageDirectory, _profileName);
        }

        // Every writer comes through here (the pickers, /tts off, /cwd, a /tools flip), so this is
        // the one place a change is logged: one line per property, none for a value picked again.
        foreach (string change in SettingsDiff.Changes(before, snapshot))
        {
            DiagnosticLog.Info(Category, ChangedLogLine(change));
        }

        ScheduleSave(snapshot, path);
        RaiseChanged(snapshot);
    }

    /// <summary>The log line for one changed property: <c>Changed TtsSpeed: 1 → 1.2</c>.</summary>
    public static string ChangedLogLine(string change) => "Changed " + change;

    /// <summary>
    /// The log line naming the values that are not the compiled defaults (<see cref="SettingsDiff.NotDefault"/>):
    /// <c>Not default: TtsSpeed=1.2, WorkingDirectory=D:\x</c>, or <see cref="AllDefaultLogLine"/>.
    /// </summary>
    public static string NotDefaultLogLine(IReadOnlyList<string> notDefault) =>
        notDefault.Count == 0 ? AllDefaultLogLine : "Not default: " + string.Join(", ", notDefault);

    public const string AllDefaultLogLine = "Every setting is at its default.";

    /// <summary>
    /// Loads another profile in place: the pending save of the loaded one is written first, the
    /// other's <c>profile.json</c> (or the compiled defaults, when it has none yet) replaces the
    /// data, the pointer is rewritten, and <see cref="Changed"/> fires with the new snapshot.
    /// <paramref name="name"/> must be a listed profile (<see cref="Profiles.Resolve"/>); an
    /// unknown one is the caller's mistake and throws <see cref="ArgumentException"/>.
    /// </summary>
    public async Task SwitchProfileAsync(string name)
    {
        string resolved = Profiles.Resolve(StorageDirectory, name)
            ?? throw new ArgumentException($"No profile named \"{name}\".", nameof(name));

        await FlushAsync().ConfigureAwait(false);

        AppSettingsData snapshot;
        lock (_gate)
        {
            _profileName = resolved;
            _data = Load(Profiles.ProfileFile(StorageDirectory, resolved));
            snapshot = Copy(_data);
        }

        DiagnosticLog.Info(Category, $"Switched to profile \"{resolved}\".");
        TrySavePointer(resolved);
        EnsureProfileDirectories();
        RaiseChanged(snapshot);
    }

    /// <summary>
    /// Takes a profile's settings back to the compiled defaults (<see cref="Profiles.Reset"/>: <c>profile.json</c>
    /// rewritten, the sidekick's files untouched since 2026-09-20). For the loaded
    /// profile the pending save is written first — a debounced write landing after the reset would
    /// bring the old values back — and the defaults then replace the data in place, with
    /// <see cref="Changed"/> firing on the new snapshot; another profile is disk only. The pointer
    /// is untouched. <paramref name="name"/> must be a listed profile (<see cref="Profiles.Resolve"/>);
    /// an unknown one throws <see cref="ArgumentException"/>. <paramref name="all"/> resets
    /// <see cref="Profiles.ResetKeptSettings"/> too (2026-09-27; the flush first is what lets a plain
    /// reset keep the loaded profile's current values).
    /// </summary>
    public async Task ResetProfileAsync(string name, bool all = false)
    {
        string resolved = Profiles.Resolve(StorageDirectory, name)
            ?? throw new ArgumentException($"No profile named \"{name}\".", nameof(name));

        bool loaded = Profiles.NameEquals(resolved, ProfileName);
        if (loaded)
        {
            await FlushAsync().ConfigureAwait(false);
        }

        Profiles.Reset(StorageDirectory, resolved, all);
        if (!loaded)
        {
            DiagnosticLog.Info(Category, $"Reset profile \"{resolved}\".");
            return;
        }

        AppSettingsData snapshot;
        lock (_gate)
        {
            _data = Load(Profiles.ProfileFile(StorageDirectory, resolved));
            snapshot = Copy(_data);
        }

        DiagnosticLog.Info(Category, $"Reset profile \"{resolved}\" (loaded).");
        EnsureProfileDirectories();
        RaiseChanged(snapshot);
    }

    /// <summary>
    /// Copies <paramref name="from"/>'s settings over <paramref name="to"/>'s (2026-09-28, <c>/profile push</c> and
    /// <c>/profile pull</c>; <see cref="Profiles.CopySettings"/>: every setting but the target's working directory). When
    /// either is the loaded profile the pending save is written first — a push then copies the values on screen, and a
    /// pull leaves no debounced write to bring the old values back over the copy. When <paramref name="to"/> is the loaded
    /// one the copy replaces the data in place and <see cref="Changed"/> fires on the new snapshot, as a reset does;
    /// otherwise it is disk only. The pointer is untouched. Both must be listed profiles (<see cref="Profiles.Resolve"/>);
    /// an unknown one throws <see cref="ArgumentException"/>. A corrupt source throws <see cref="JsonException"/>, nothing written.
    /// </summary>
    public async Task CopyProfileSettingsAsync(string from, string to)
    {
        string source = Profiles.Resolve(StorageDirectory, from)
            ?? throw new ArgumentException($"No profile named \"{from}\".", nameof(from));
        string target = Profiles.Resolve(StorageDirectory, to)
            ?? throw new ArgumentException($"No profile named \"{to}\".", nameof(to));

        string loadedName = ProfileName;
        bool intoLoaded = Profiles.NameEquals(target, loadedName);
        if (intoLoaded || Profiles.NameEquals(source, loadedName))
        {
            await FlushAsync().ConfigureAwait(false);
        }

        Profiles.CopySettings(StorageDirectory, source, target);
        if (!intoLoaded)
        {
            return;
        }

        AppSettingsData snapshot;
        lock (_gate)
        {
            _data = Load(Profiles.ProfileFile(StorageDirectory, target));
            snapshot = Copy(_data);
        }

        EnsureProfileDirectories();
        RaiseChanged(snapshot);
    }

    /// <summary>
    /// Re-reads the loaded profile's <c>profile.json</c> from disk (2026-09-21, <c>/profile reload</c>,
    /// the way back in after <c>/profile edit</c>): the pending debounced save is cancelled — the
    /// hand-edited file is what the user wants, not what the menu last saved —, the data replaced in
    /// place (the compiled defaults when the file is unreadable, as <see cref="Load"/> says), the
    /// profile folders made sure of, and <see cref="Changed"/> raised with the new snapshot. The
    /// pointer is untouched. A save already past its debounce and inside its write at that instant
    /// still lands; the window is milliseconds, and the next reload reads it.
    /// </summary>
    public void Reload()
    {
        AppSettingsData snapshot;
        string name;
        lock (_gate)
        {
            _pendingSave?.Cancel();
            _pendingSave?.Dispose();
            _pendingSave = null;
            name = _profileName;
            _data = Load(Profiles.ProfileFile(StorageDirectory, name));
            snapshot = Copy(_data);
        }

        DiagnosticLog.Info(Category, $"Reloaded profile \"{name}\" from disk.");
        EnsureProfileDirectories();
        RaiseChanged(snapshot);
    }

    private void RaiseChanged(AppSettingsData snapshot)
    {
        try
        {
            Changed?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            // A subscriber that throws must not take out the caller: losing the rest of a
            // settings menu's work would apply half a page.
            DiagnosticLog.Error(Category, $"A settings subscriber threw: {ex.Message}", ex);
        }
    }

    /// <summary>Writes now, skipping the debounce. Called on the way out and before a profile switch.</summary>
    public Task FlushAsync()
    {
        AppSettingsData snapshot;
        string path;
        lock (_gate)
        {
            _pendingSave?.Cancel();
            _pendingSave?.Dispose();
            _pendingSave = null;
            snapshot = Copy(_data);
            path = Profiles.ProfileFile(StorageDirectory, _profileName);
        }

        return SaveAsync(snapshot, path);
    }

    private void ScheduleSave(AppSettingsData snapshot, string path)
    {
        CancellationTokenSource cts;
        TimeSpan debounce;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingSave?.Cancel();
            _pendingSave?.Dispose();
            _pendingSave = cts = new CancellationTokenSource();
            debounce = SaveDebounce;
        }

        // Fire-and-forget, deliberately: a menu submission must not block on the disk. The
        // snapshot and the path are captured at schedule time so a cancelled save cannot
        // resurrect stale values and a switch cannot redirect an older profile's write.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounce, cts.Token).ConfigureAwait(false);
                await SaveAsync(snapshot, path).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later edit, a flush, or shutdown.
            }
        });
    }

    private static async Task SaveAsync(AppSettingsData snapshot, string path)
    {
        // Temp file then File.Move: a half-written settings file is indistinguishable from a
        // corrupt one on the next launch. The GUID suffix keeps two writers apart.
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(snapshot, SettingsJsonContext.Default.AppSettingsData);
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            DiagnosticLog.Error(Category, $"Failed to save settings to {path}: {ex.Message}", ex);
        }
    }

    /// <summary>The pointer: written synchronously (once per launch, once per switch), never throws.</summary>
    private void TrySavePointer(string profile)
    {
        var tempPath = $"{PointerPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            var json = JsonSerializer.Serialize(new RootSettingsData { Profile = profile }, SettingsJsonContext.Default.RootSettingsData);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, PointerPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            DiagnosticLog.Error(Category, $"Failed to save {FileName} to {PointerPath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads the root file. Missing or unreadable is a pointer to the default, logged as the
    /// settings file always was. The one-time move of a pre-profile root file (the full settings,
    /// no <c>Profile</c> property) into <c>profiles\default</c> went on 2026-09-24, the user's call:
    /// such a file now reads as a pointer to the default profile, its settings not carried over.
    /// </summary>
    private static RootSettingsData LoadPointer(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new RootSettingsData();
            }

            string json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.RootSettingsData);
            if (loaded is not null)
            {
                return loaded;
            }

            DiagnosticLog.Warn(Category, $"{FileName} parsed to null. Loading the {Profiles.DefaultName} profile.");
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Error(Category, $"{FileName} is corrupt ({ex.Message}). Loading the {Profiles.DefaultName} profile.", ex);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(Category, $"Could not read {FileName} ({ex.Message}). Loading the {Profiles.DefaultName} profile.", ex);
        }

        return new RootSettingsData();
    }

    /// <summary>
    /// Reads a profile file, falling back to defaults on anything unreadable. Refusing to launch
    /// over a bad value in a preferences file would be absurd, so it is logged loudly instead.
    /// </summary>
    private static AppSettingsData Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize(json, ReadInfo);
                if (loaded is not null)
                {
                    // The interrupt needs the wake word (2026-09-13); a profile saved before that
                    // rule loads with it off, and the file follows at the next save.
                    if (loaded.SttInterrupt && !loaded.SttWake)
                    {
                        loaded.SttInterrupt = false;
                        DiagnosticLog.Info(Category, "Interrupt switched off: it needs the wake word on.");
                    }

                    // A plain key in the file — a hand edit, a file from before, the LLM API key before 2026-09-28 — is
                    // encrypted as it loads and the file written back at once (the user's call: all three keys, no
                    // retyping). A failed write keeps the encrypted values in memory for the next save to write.
                    if (SettingsSecrets.ProtectAtRest(loaded))
                    {
                        try
                        {
                            Profiles.WriteProfileFile(filePath, loaded);
                            DiagnosticLog.Info(Category, "Encrypted the profile's plain API keys (DPAPI).");
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            DiagnosticLog.Warn(Category, $"Could not write the encrypted API keys back ({ex.Message}); the next save will.");
                        }
                    }

                    return loaded;
                }

                DiagnosticLog.Warn(Category, "Settings file parsed to null. Using defaults.");
            }
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Error(Category, $"Settings file is corrupt ({ex.Message}). Using defaults.", ex);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(Category, $"Could not read settings ({ex.Message}). Using defaults.", ex);
        }

        return new AppSettingsData();
    }

    /// <summary>
    /// How a profile file is read: <see cref="SettingsJsonContext"/>'s metadata with a JSON <c>null</c> skipped for every property
    /// that is never null in code, so its default stands (the third 2026-10-04 review). System.Text.Json writes the null into a
    /// non-nullable <c>string</c> or <c>List&lt;string&gt;</c> all the same: <c>"LlmModel": null</c> threw at <c>/settings</c>'
    /// <c>.Trim()</c>, a null list in <see cref="Copy"/>'s spread and <c>ForbiddenStrings.Find</c>. The review before patched five
    /// lists by hand; this covers every field, a new one included. The nullable ones (<c>ToolbarItems</c> and the like) still read
    /// null as null. <c>RespectNullableAnnotations</c> was not the way: it throws, and the whole file would fall back to defaults.
    /// </summary>
    internal static JsonTypeInfo<AppSettingsData> ReadInfo { get; } = BuildReadInfo();

    private static JsonTypeInfo<AppSettingsData> BuildReadInfo()
    {
        var options = new JsonSerializerOptions(SettingsJsonContext.Default.Options)
        {
            TypeInfoResolver = SettingsJsonContext.Default.WithAddedModifier(KeepDefaultForNull),
        };
        return (JsonTypeInfo<AppSettingsData>)options.GetTypeInfo(typeof(AppSettingsData));
    }

    /// <summary>A non-nullable reference property's setter wrapped to pass a null by: the property keeps its initial value.</summary>
    private static void KeepDefaultForNull(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in info.Properties)
        {
            if (property.Set is { } set && !property.IsSetNullable && !property.PropertyType.IsValueType)
            {
                property.Set = (target, value) =>
                {
                    if (value is not null)
                    {
                        set(target, value);
                    }
                };
            }
        }
    }

    /// <summary>
    /// A hand-written copy, because the alternative is reflection.
    ///
    /// <para>Every new field must be added here as well as to <see cref="AppSettingsData"/>. A
    /// missed field loses its value on the next <see cref="Update"/> rather than failing loudly,
    /// so <c>AppSettingsTests</c> round-trips a fully non-default object to turn the omission
    /// into a test failure.</para>
    /// </summary>
    internal static AppSettingsData Copy(AppSettingsData source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        CommandTypoIntercept = source.CommandTypoIntercept,
        KeepCommandHistory = source.KeepCommandHistory,
        CopyUserPrompt = source.CopyUserPrompt,
        UserLineStyle = source.UserLineStyle,
        DraftEditor = source.DraftEditor,
        ImageEditor = source.ImageEditor,
        ThemedBackground = source.ThemedBackground,
        ThemedExternalWindows = source.ThemedExternalWindows,
        ViewerLeft = source.ViewerLeft,
        ViewerTop = source.ViewerTop,
        CameraWindowLeft = source.CameraWindowLeft,
        CameraWindowTop = source.CameraWindowTop,
        LogWindowLeft = source.LogWindowLeft,
        LogWindowTop = source.LogWindowTop,
        ProcessWindowLeft = source.ProcessWindowLeft,
        ProcessWindowTop = source.ProcessWindowTop,
        ThumbsWindowLeft = source.ThumbsWindowLeft,
        ThumbsWindowTop = source.ThumbsWindowTop,
        HideExitAutocomplete = source.HideExitAutocomplete,
        ImageThumbnailSize = source.ImageThumbnailSize,
        MemoryMode = source.MemoryMode,
        NewProfileMode = source.NewProfileMode,
        PastePreviewLines = source.PastePreviewLines,
        QueueCancelMode = source.QueueCancelMode,
        QueueMessages = source.QueueMessages,
        ShowImageThumbnails = source.ShowImageThumbnails,
        ShowWorkingDirectory = source.ShowWorkingDirectory,
        ShowHeader = source.ShowHeader,
        ToolbarItems = source.ToolbarItems is null ? null : [.. source.ToolbarItems],
        ToolbarLastItems = source.ToolbarLastItems is null ? null : [.. source.ToolbarLastItems],
        PerformanceBarItems = source.PerformanceBarItems is null ? null : [.. source.PerformanceBarItems],
        PerformanceBarLastItems = source.PerformanceBarLastItems is null ? null : [.. source.PerformanceBarLastItems],
        PerformanceBarLook = source.PerformanceBarLook,
        Theme = source.Theme,
        TranscriptMarkdown = source.TranscriptMarkdown,
        WelcomeSplashMode = source.WelcomeSplashMode,
        MenuMaxHeight = source.MenuMaxHeight,
        WorkingDirectory = source.WorkingDirectory,
        SessionLogging = source.SessionLogging,
        SessionNamingMode = source.SessionNamingMode,
        SessionRetentionDays = source.SessionRetentionDays,
        SessionSearchMaxResults = source.SessionSearchMaxResults,
        SessionShowName = source.SessionShowName,
        SessionSaveThinking = source.SessionSaveThinking,
        SessionTool = source.SessionTool,
        LlmApiKey = source.LlmApiKey,
        LlmAutoCompactPercent = source.LlmAutoCompactPercent,
        LlmMaxTurns = source.LlmMaxTurns,
        LlmCompactKeepRecent = source.LlmCompactKeepRecent,
        LlmPictureKeep = source.LlmPictureKeep,
        LlmPictureMegabytes = source.LlmPictureMegabytes,
        LlmCompactShowSummary = source.LlmCompactShowSummary,
        LlmCompactType = source.LlmCompactType,
        LlmContextLength = source.LlmContextLength,
        LlmMidTurnUsage = source.LlmMidTurnUsage,
        LlmMaxToolIterations = source.LlmMaxToolIterations,
        LlmModel = source.LlmModel,
        LlmOfferTools = source.LlmOfferTools,
        LlmReasoning = source.LlmReasoning,
        LlmRequestTimeoutSeconds = source.LlmRequestTimeoutSeconds,
        LlmScanMode = source.LlmScanMode,
        LlmToolCompactType = source.LlmToolCompactType,
        LlmTurnTimeoutSeconds = source.LlmTurnTimeoutSeconds,
        LlmUrl = source.LlmUrl,
        LlmUseFunVerbs = source.LlmUseFunVerbs,
        LlmShowThinking = source.LlmShowThinking,
        LlmPreserveThinking = source.LlmPreserveThinking,
        LlmReasoningEstimate = source.LlmReasoningEstimate,
        LlmSampling = LlmSamplingEntry.CopyAll(source.LlmSampling),
        LlmSamplingFromHuggingFace = source.LlmSamplingFromHuggingFace,
        TtsHttpUrl = source.TtsHttpUrl,
        TtsOutput = source.TtsOutput,
        TtsSource = source.TtsSource,
        TtsSpeed = source.TtsSpeed,
        TtsVoice = source.TtsVoice,
        TtsVoice2 = source.TtsVoice2,
        TtsVoiceMix = source.TtsVoiceMix,
        TtsVoicePreview = source.TtsVoicePreview,
        SttInput = source.SttInput,
        SttDestination = source.SttDestination,
        SttInterrupt = source.SttInterrupt,
        SttInterruptConfirmMs = source.SttInterruptConfirmMs,
        SttInterruptEchoGuard = source.SttInterruptEchoGuard,
        SttPushToTalkKey = source.SttPushToTalkKey,
        SttVoskModel = source.SttVoskModel,
        SttWake = source.SttWake,
        SttWakePhrase = source.SttWakePhrase,
        SttWhisperModel = source.SttWhisperModel,
        AgentSkills = source.AgentSkills,
        ExternalSkills = source.ExternalSkills,
        ProjectFile = source.ProjectFile,
        ReflectionAutoLearn = source.ReflectionAutoLearn,
        ReflectionCooldownMinutes = source.ReflectionCooldownMinutes,
        ReflectionCooldownMode = source.ReflectionCooldownMode,
        ReflectionIncludesSessions = source.ReflectionIncludesSessions,
        ReflectionMaxRequests = source.ReflectionMaxRequests,
        ReflectionMinToolCalls = source.ReflectionMinToolCalls,
        ReflectionReasoning = source.ReflectionReasoning,
        ReflectionWindow = source.ReflectionWindow,
        ReflectionYieldsToTurns = source.ReflectionYieldsToTurns,
        ReflectionEditsSupportingFiles = source.ReflectionEditsSupportingFiles,
        ReflectionInstalledSkills = source.ReflectionInstalledSkills,
        SkillCompactMode = source.SkillCompactMode,
        SkillHashMention = source.SkillHashMention,
        ToolsDisabled = [.. source.ToolsDisabled],
        ToolsDollarMention = source.ToolsDollarMention,
        ToolCollapseCount = source.ToolCollapseCount,
        CodeCollapseCount = source.CodeCollapseCount,
        ShowFileDiffs = source.ShowFileDiffs,
        DiffMaxLines = source.DiffMaxLines,
        DiffCollapseCount = source.DiffCollapseCount,
        AskMaxChoices = source.AskMaxChoices,
        AskMaxQuestions = source.AskMaxQuestions,
        AskUser = source.AskUser,
        CameraTools = source.CameraTools,
        CameraShutter = source.CameraShutter,
        CameraPreview = source.CameraPreview,
        CameraDevice = source.CameraDevice,
        CameraResolution = source.CameraResolution,
        CameraKeepInSessions = source.CameraKeepInSessions,
        CameraOutputFolder = source.CameraOutputFolder,
        CameraWatchSeconds = source.CameraWatchSeconds,
        CameraWatchThreshold = source.CameraWatchThreshold,
        CameraWatchUnprompted = source.CameraWatchUnprompted,
        CameraWatchMinGapSeconds = source.CameraWatchMinGapSeconds,
        ScreenTools = source.ScreenTools,
        ScreenAsk = source.ScreenAsk,
        ScreenPreview = source.ScreenPreview,
        ScreenKeepInSessions = source.ScreenKeepInSessions,
        ScreenOutputFolder = source.ScreenOutputFolder,
        FileMentionFolderMode = source.FileMentionFolderMode,
        FileBrowserMode = source.FileBrowserMode,
        FileTools = source.FileTools,
        FileTreeMaxLength = source.FileTreeMaxLength,
        FileTreeShowSizes = source.FileTreeShowSizes,
        FileViewImageMaxPerCall = source.FileViewImageMaxPerCall,
        FileSearchMaxResults = source.FileSearchMaxResults,
        ImageEditQuality = source.ImageEditQuality,
        ImageEditMetadata = source.ImageEditMetadata,
        ImageEditMode = source.ImageEditMode,
        ImageEditOutputFolder = source.ImageEditOutputFolder,
        GitLibDiffMaxLines = source.GitLibDiffMaxLines,
        GitLibLogMaxCommits = source.GitLibLogMaxCommits,
        GitLibTools = source.GitLibTools,
        GitLibEmail = source.GitLibEmail,
        GitLibName = source.GitLibName,
        ObsidianTools = source.ObsidianTools,
        ObsidianAllowDelete = source.ObsidianAllowDelete,
        ObsidianVault = source.ObsidianVault,
        SqlTools = source.SqlTools,
        SqlMode = source.SqlMode,
        SqlStatementsAllowed = source.SqlStatementsAllowed is null ? null : [.. source.SqlStatementsAllowed],
        SqlDefaultConnection = source.SqlDefaultConnection,
        SqlConnectionsOffered = source.SqlConnectionsOffered is null ? null : [.. source.SqlConnectionsOffered],
        SqlPercentMention = source.SqlPercentMention,
        SqlQueryMaxRows = source.SqlQueryMaxRows,
        SqlQueryTimeoutSeconds = source.SqlQueryTimeoutSeconds,
        QueryResultMaxChars = source.QueryResultMaxChars,
        OracleTools = source.OracleTools,
        OracleMode = source.OracleMode,
        OracleStatementsAllowed = source.OracleStatementsAllowed is null ? null : [.. source.OracleStatementsAllowed],
        OracleDefaultConnection = source.OracleDefaultConnection,
        OracleConnectionsOffered = source.OracleConnectionsOffered is null ? null : [.. source.OracleConnectionsOffered],
        OraclePercentMention = source.OraclePercentMention,
        OracleQueryMaxRows = source.OracleQueryMaxRows,
        OracleQueryTimeoutSeconds = source.OracleQueryTimeoutSeconds,
        MySqlTools = source.MySqlTools,
        MySqlMode = source.MySqlMode,
        MySqlStatementsAllowed = source.MySqlStatementsAllowed is null ? null : [.. source.MySqlStatementsAllowed],
        MySqlDefaultConnection = source.MySqlDefaultConnection,
        MySqlConnectionsOffered = source.MySqlConnectionsOffered is null ? null : [.. source.MySqlConnectionsOffered],
        MySqlPercentMention = source.MySqlPercentMention,
        MySqlQueryMaxRows = source.MySqlQueryMaxRows,
        MySqlQueryTimeoutSeconds = source.MySqlQueryTimeoutSeconds,
        SqliteTools = source.SqliteTools,
        SqliteMode = source.SqliteMode,
        SqliteStatementsAllowed = source.SqliteStatementsAllowed is null ? null : [.. source.SqliteStatementsAllowed],
        SqliteDefaultDatabase = source.SqliteDefaultDatabase,
        SqliteDatabasesOffered = source.SqliteDatabasesOffered is null ? null : [.. source.SqliteDatabasesOffered],
        SqliteSandboxFiles = source.SqliteSandboxFiles,
        SqlitePercentMention = source.SqlitePercentMention,
        SqliteQueryMaxRows = source.SqliteQueryMaxRows,
        SqliteQueryTimeoutSeconds = source.SqliteQueryTimeoutSeconds,
        PostgresTools = source.PostgresTools,
        PostgresMode = source.PostgresMode,
        PostgresStatementsAllowed = source.PostgresStatementsAllowed is null ? null : [.. source.PostgresStatementsAllowed],
        PostgresDefaultConnection = source.PostgresDefaultConnection,
        PostgresConnectionsOffered = source.PostgresConnectionsOffered is null ? null : [.. source.PostgresConnectionsOffered],
        PostgresPercentMention = source.PostgresPercentMention,
        PostgresQueryMaxRows = source.PostgresQueryMaxRows,
        PostgresQueryTimeoutSeconds = source.PostgresQueryTimeoutSeconds,
        UncTools = source.UncTools,
        UncWrites = source.UncWrites,
        DockerTools = source.DockerTools,
        DockerWrites = source.DockerWrites,
        DockerEnginePipe = source.DockerEnginePipe,
        DockerServers = source.DockerServers,
        DockerServerContainers = source.DockerServerContainers is null ? null : [.. source.DockerServerContainers],
        DockerServerStopTimeoutSeconds = source.DockerServerStopTimeoutSeconds,
        DockerServerPostStopDelaySeconds = source.DockerServerPostStopDelaySeconds,
        DockerServerReadyTimeoutSeconds = source.DockerServerReadyTimeoutSeconds,
        DockerServerStopOnExit = source.DockerServerStopOnExit,
        UncDefaultShare = source.UncDefaultShare,
        UncSharesOffered = source.UncSharesOffered is null ? null : [.. source.UncSharesOffered],
        UncStarMention = source.UncStarMention,
        ComfyTools = source.ComfyTools,
        ComfyUrl = source.ComfyUrl,
        ComfyWorkflowsOffered = source.ComfyWorkflowsOffered is null ? null : [.. source.ComfyWorkflowsOffered],
        ComfyCaretMention = source.ComfyCaretMention,
        ComfyTimeoutSeconds = source.ComfyTimeoutSeconds,
        ComfyMaxPicturesPerCall = source.ComfyMaxPicturesPerCall,
        ComfyReinforceNegatives = source.ComfyReinforceNegatives,
        ComfyShowPrompts = source.ComfyShowPrompts,
        ComfyPictureStrip = source.ComfyPictureStrip,
        ComfyOutputFolder = source.ComfyOutputFolder,
        BotChatLlmMode = source.BotChatLlmMode,
        BotChatMultiEmbedded = source.BotChatMultiEmbedded,
        BotChatMultiEmbeddedKill = source.BotChatMultiEmbeddedKill,
        BotChatComfy = source.BotChatComfy,
        BotChatLimitedComfyWorkflows = source.BotChatLimitedComfyWorkflows is null ? null : [.. source.BotChatLimitedComfyWorkflows],
        BotChatImageMode = source.BotChatImageMode,
        BotChatImg2ImgMode = source.BotChatImg2ImgMode,
        BotChatImageAsync = source.BotChatImageAsync,
        BotChatNonTtsDelaySeconds = source.BotChatNonTtsDelaySeconds,
        BotChatTools = source.BotChatTools,
        BotChatLimitedTools = source.BotChatLimitedTools is null ? null : [.. source.BotChatLimitedTools],
        BotChatSkills = source.BotChatSkills,
        BotChatLimitedSkills = source.BotChatLimitedSkills is null ? null : [.. source.BotChatLimitedSkills],
        BotChatMemory = source.BotChatMemory,
        BotChatMemoryMode = source.BotChatMemoryMode,
        BotChatVision = source.BotChatVision,
        BotChatCamera = source.BotChatCamera,
        ClaudeCliExecutable = source.ClaudeCliExecutable,
        ClaudeCliPermissions = source.ClaudeCliPermissions,
        ClaudeCliModel = source.ClaudeCliModel,
        ClaudeCliEffort = source.ClaudeCliEffort,
        ClaudeCliAdvisor = source.ClaudeCliAdvisor,
        ClaudeCliAdvisorContext = source.ClaudeCliAdvisorContext,
        ClaudeCliAdvisorCallsPerTurn = source.ClaudeCliAdvisorCallsPerTurn,
        ClaudeCliAdvisorModel = source.ClaudeCliAdvisorModel,
        ClaudeCliAdvisorEffort = source.ClaudeCliAdvisorEffort,
        ClaudeCliAdvisorConfirm = source.ClaudeCliAdvisorConfirm,
        AnthropicApi = source.AnthropicApi,
        AnthropicApiKey = source.AnthropicApiKey,
        AnthropicApiMaxTokens = source.AnthropicApiMaxTokens,
        AnthropicApiPromptCaching = source.AnthropicApiPromptCaching,
        ClaudeCliServer = source.ClaudeCliServer,
        OpenAIApi = source.OpenAIApi,
        OpenAIApiKey = source.OpenAIApiKey,
        OpenAIApiMaxTokens = source.OpenAIApiMaxTokens,
        OpenAIApiOrganization = source.OpenAIApiOrganization,
        OpenAIApiProject = source.OpenAIApiProject,
        EmbeddedBackend = source.EmbeddedBackend,
        EmbeddedContextSize = source.EmbeddedContextSize,
        EmbeddedVramBudget = source.EmbeddedVramBudget,
        EmbeddedHfDownloadType = source.EmbeddedHfDownloadType,
        EmbeddedGpuLayers = source.EmbeddedGpuLayers,
        EmbeddedVision = source.EmbeddedVision,
        EmbeddedVramOnly = source.EmbeddedVramOnly,
        EmbeddedLlmServer = source.EmbeddedLlmServer,
        EmbeddedDrafter = source.EmbeddedDrafter,
        HomeAssistantTools = source.HomeAssistantTools,
        HomeAssistantUrl = source.HomeAssistantUrl,
        HomeAssistantToken = source.HomeAssistantToken,
        HomeAssistantActionPolicy = source.HomeAssistantActionPolicy,
        HomeAssistantSafeServices = source.HomeAssistantSafeServices is null ? null : [.. source.HomeAssistantSafeServices],
        HomeAssistantAssistAgent = source.HomeAssistantAssistAgent,
        HomeAssistantTimeoutSeconds = source.HomeAssistantTimeoutSeconds,
        PrintTools = source.PrintTools,
        PrintActionPolicy = source.PrintActionPolicy,
        PrintDefaultPrinter = source.PrintDefaultPrinter,
        PrintFontSize = source.PrintFontSize,
        PdfEngine = source.PdfEngine,
        ShellCodeLanguages = [.. source.ShellCodeLanguages],
        ShellCodeMaxToolCalls = source.ShellCodeMaxToolCalls,
        ShellCodeTimeoutSeconds = source.ShellCodeTimeoutSeconds,
        ShellCommandAllowed = [.. source.ShellCommandAllowed],
        ShellCommandPolicy = source.ShellCommandPolicy,
        ShellDefault = source.ShellDefault,
        ShellForegroundCapSeconds = source.ShellForegroundCapSeconds,
        ShellOutputMaxChars = source.ShellOutputMaxChars,
        ShellPolice = source.ShellPolice,
        ShellPoliceForbiddenStrings = [.. source.ShellPoliceForbiddenStrings],
        ShellPreferNative = source.ShellPreferNative,
        ShellTimeoutSeconds = source.ShellTimeoutSeconds,
        ShellToolBridge = source.ShellToolBridge,
        WebBrowserMode = source.WebBrowserMode,
        WebBrowserNetworkMode = source.WebBrowserNetworkMode,
        WebBrowserPath = source.WebBrowserPath,
        WebSearchMaxResults = source.WebSearchMaxResults,
        WebDownloadMaxMegabytes = source.WebDownloadMaxMegabytes,
        WebSearchMethod = source.WebSearchMethod,
        WebSearxngUrl = source.WebSearxngUrl,
        WebTools = source.WebTools,
        McpConnectTimeoutSeconds = source.McpConnectTimeoutSeconds,
        McpServers = source.McpServers,
        McpServersDisabled = [.. source.McpServersDisabled],
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingSave?.Cancel();
            _pendingSave?.Dispose();
            _pendingSave = null;
        }
    }
}
