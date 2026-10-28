using System.Text;
using NeonSidekick.App;
using NeonSidekick.Settings;

namespace NeonSidekick.Help;

/// <summary>Where a file of <see cref="HelpFiles"/> lives: the home folder, a profile's folder, or the working folder.</summary>
internal enum HelpFileScope
{
    Home,
    Profile,
    WorkingFolder,
}

/// <summary>
/// One file or folder of the app's own (2026-10-25, the neon_help plan's Step 4): where it lives, its path from there (<c>/</c> between the
/// parts, the system's separator when shown; a folder ends in <c>/</c>), what it holds, what edits it, and the setting that names the folder
/// when one does.
/// </summary>
internal sealed record HelpFile(HelpFileScope Scope, string Path, string Holds, string? EditedBy = null, SettingsField? Setting = null)
{
    /// <summary>The last part, without a folder's slash: <c>sql.json</c>, <c>camera</c>.</summary>
    public string Name => Path.TrimEnd('/')[(Path.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>Whether this is a folder.</summary>
    public bool IsFolder => Path.EndsWith('/');
}

/// <summary>
/// The home layout, a profile's folder and the working folder's <c>.neon</c>, for <c>neon_help</c> (2026-10-25, the neon_help plan's Step 4):
/// each path from its owner's constant or function (the families' <c>FileName</c>, <see cref="Profiles.ConfigsDirectoryName"/>,
/// <see cref="Sessions.SessionStore.FolderName"/>…; <c>models</c> and <c>llama</c> as <see cref="AppSettings"/> spells them, held to it by a
/// test), what holds it written here. The connection files' formats are their owners' <c>EmptyText</c>, this system's own; <c>mcp.json</c>'s a
/// short example here, read back by a test. On a system without services and tasks their files are left out. The temp folders under
/// <c>%TEMP%\NeonSidekick</c> and the retired <c>settings.json</c> get no row. A folder a setting names gives that setting and its default,
/// never this profile's value.
/// </summary>
internal static class HelpFiles
{
    /// <summary>The tree's heading (<c>kind: files</c>, no query). Pinned.</summary>
    public const string TreeHeading = "NeonSidekick's own files (ask about one by name, `sql.json`, or `sql.json format` for its shape):";

    /// <summary>The file answer's heading: <c>File `configs\sql.json` (the home folder): …</c>. Pinned.</summary>
    public static string FileHeading(HelpFile file, bool windows) =>
        $"{(file.IsFolder ? "Folder" : "File")} `{Shown(file.Path, windows)}` ({ScopeWords(file.Scope)}): {file.Holds}";

    /// <summary>The format answer's heading. Pinned.</summary>
    public static string FormatHeading(string fileName) => $"Format of `{fileName}` (JSON with comments; this is what the app writes when the file is missing):";

    /// <summary>
    /// <c>mcp.json</c>'s example (2026-10-25): its own empty text is the bare <c>{"mcpServers": {}}</c>, so the manual shows one stdio server and
    /// one HTTP server. Read back by <c>McpConfigFile.Load</c> in a test (two servers, no problems).
    /// </summary>
    public const string McpExample =
        "{\n" +
        "  \"mcpServers\": {\n" +
        "    \"fetch\": { \"command\": \"uvx\", \"args\": [\"mcp-server-fetch\"] },\n" +
        "    \"docs\": { \"type\": \"http\", \"url\": \"http://localhost:8000/mcp\" }\n" +
        "  }\n" +
        "}\n";

    private const string ConnectionsEdit = "its `/tools {0}` page's wizard, or Enter on its file row there opens it in your editor";

    private static readonly Lazy<IReadOnlyList<HelpFile>> s_windows = new(() => Build(windows: true));
    private static readonly Lazy<IReadOnlyList<HelpFile>> s_elsewhere = new(() => Build(windows: false));

    /// <summary>The rows a system has (services and tasks left out off Windows).</summary>
    public static IReadOnlyList<HelpFile> Rows(bool windows) => windows ? s_windows.Value : s_elsewhere.Value;

    private static List<HelpFile> Build(bool windows)
    {
        string configs = Profiles.ConfigsDirectoryName + "/";
        var families = new List<(string File, string Holds, string Word)>
        {
            (Mcp.McpConfigFile.FileName, "MCP servers", "mcp"),
            (Sql.SqlConfigFile.FileName, "SQL Server connections", "sql"),
            (Oracle.OracleConfigFile.FileName, "Oracle connections", "oracle"),
            (MySql.MySqlConfigFile.FileName, "MySQL and MariaDB connections", "mysql"),
            (Sqlite.SqliteConfigFile.FileName, "named SQLite databases (a relative path counts from this folder)", "sqlite"),
            (DuckDb.DuckDbConfigFile.FileName, "named DuckDB databases (a relative path counts from this folder)", "duckdb"),
            (Postgres.PostgresConfigFile.FileName, "PostgreSQL connections", "postgres"),
            (Unc.UncConfigFile.FileName, "network shares and outside folders for the UNC tools", "unc"),
            (EventLog.EventLogConfigFile.FileName, "computers whose event logs may be read (`local` is built in)", "eventlog"),
        };
        if (windows)
        {
            families.Add((Services.ServiceConfigFile.FileName, "computers whose services may be read or controlled (`local` is built in)", "services"));
            families.Add((Tasks.TaskConfigFile.FileName, "computers whose scheduled tasks may be read or controlled (`local` is built in)", "tasks"));
        }

        families.Add((ActiveDirectory.DirectoryConfigFile.FileName, "Active Directory domains", "directory"));
        string Edit(string word) => word == "mcp" ? "`/mcp` › Servers, or by hand" : string.Format(System.Globalization.CultureInfo.InvariantCulture, ConnectionsEdit, word);

        var rows = new List<HelpFile>
        {
            new(HelpFileScope.Home, Profiles.DirectoryName + "/", "a folder per profile", "`/profile`"),
            new(HelpFileScope.Home, configs, "the home's own JSON files, which every profile reads; a profile's own entry wins on a clash"),
        };
        rows.AddRange(families.Select(f => new HelpFile(HelpFileScope.Home, configs + f.File, "the global " + f.Holds, Edit(f.Word))));
        rows.AddRange(
        [
            new(HelpFileScope.Home, "models/", "the downloaded speech models: Whisper, Silero, Vosk and Kokoro, fetched on first use"),
            new(HelpFileScope.Home, "models/llm/<id>/", "the embedded model's weights, a folder per catalog model", "`/server`"),
            new(HelpFileScope.Home, "models/llm/" + EmbeddedLlm.EmbeddedModelCatalog.SharedFolderName + "/", "the projectors and drafters, one copy each for every model using it"),
            new(HelpFileScope.Home, "llama/<tag>-<backend>/", "the pinned llama.cpp runtime for each backend, downloaded on first use"),
            new(HelpFileScope.Home, UI.ThemeCatalog.DirectoryName + "/", "your own themes, a JSON file each, listed with the built-in ones", "`/theme`"),
            new(HelpFileScope.Home, Skills.SkillRoots.DirectoryName + "/", "the global skills, a folder each with its `SKILL.md`", "`/skills`"),
            new(HelpFileScope.Home, Join(Skills.SkillRecordStore.FolderName, Skills.SkillRecordStore.FileName), "the skills' records: when each was made, changed and last used", "`/skills`"),
            new(HelpFileScope.Home, Comfy.ComfyWorkflowCatalog.DirectoryName + "/", "the global ComfyUI workflows", "`/comfy`"),
            new(HelpFileScope.Home, Diagnostics.CrashReport.FileName, "what the app writes when it crashes"),
            new(HelpFileScope.Home, Viewer.VideoPage.FolderName + "/", "the video window's browser data (YouTube's cookies) and the page it plays from"),
            new(HelpFileScope.Home, Viewer.BrowserPage.FolderName + "/", "no longer used (each profile has its own); you may delete it"),
            new(HelpFileScope.Profile, Profiles.FileName, "the profile's settings", "`/settings`"),
            new(HelpFileScope.Profile, Llm.PersonaFile.FileName, "the persona, when it is not the default", "`/sys persona`"),
            new(HelpFileScope.Profile, Llm.OperataFile.FileName, "the operating rules, when they are not the default", "`/sys operata`"),
            new(HelpFileScope.Profile, Llm.VocaliaFile.FileName, "the spoken-reply directive, when it is not the default", "`/sys vocalia`"),
            new(HelpFileScope.Profile, configs, "the profile's own JSON files"),
            new(HelpFileScope.Profile, configs + Memory.MemoryStore.FileName, "the memories", "`/memory`, `/remember`"),
        ]);
        rows.AddRange(families.Select(f => new HelpFile(HelpFileScope.Profile, configs + f.File, "this profile's own " + f.Holds, Edit(f.Word))));
        rows.AddRange(
        [
            new(HelpFileScope.Profile, configs + Bench.BenchHistory.FileName, "the `/test` runs", "`/test`"),
            new(HelpFileScope.Profile, configs + YouTube.YouTubeLibrary.FileName, "the saved YouTube videos and where each was left", "`/youtube`"),
            new(HelpFileScope.Profile, configs + Prompts.SavedPrompts.FileName, "the saved prompts", "`/prompts`"),
            new(HelpFileScope.Profile, configs + Viewer.BrowserHistory.FileName, "the Browser window's visits, kept while *Web browser privacy* says `keep-history`"),
            new(HelpFileScope.Profile, configs + Viewer.BrowserLinks.FileName, "the Browser window's saved links", "`/browser`"),
            new(HelpFileScope.Profile, configs + Emojis.EmojiPrefs.FileName, "the avatar picker's recent picks and last skin tone", "`/avatar`"),
            new(HelpFileScope.Profile, Join(Sessions.SessionStore.FolderName, Sessions.SessionStore.FileName), "the stored sessions and the input line's history", "`/sessions`"),
            new(HelpFileScope.Profile, Files.CheckpointStore.FolderName + "/", "the file checkpoints: the saved copies `/undo` and `/rewind` put back, one store per working folder", "`/undo`", SettingsField.FileCheckpoints),
            new(HelpFileScope.Profile, Join(Shopping.ShoppingStore.FolderName, Shopping.ShoppingStore.FileName), "the shopping list and the stores' layouts", "`/shopping`"),
            new(HelpFileScope.Profile, Files.WorkingDirectory.DefaultFolderName + "/", "the default working directory: where the file tools read and write unless *Working directory (cwd)* names another"),
            new(HelpFileScope.Profile, AppSettings.ImagesFolderName + "/", "the profile's pictures, kept out of the working directory"),
            new(HelpFileScope.Profile, AppSettings.ImagesFolderName + "/" + AppSettingsData.DefaultCameraOutputFolder + "/", "the camera's photos", null, SettingsField.CameraOutputFolder),
            new(HelpFileScope.Profile, AppSettings.ImagesFolderName + "/" + HomeAssistant.HaCamera.FolderName + "/", "Home Assistant camera stills", "`/ha stills`"),
            new(HelpFileScope.Profile, AppSettings.ImagesFolderName + "/" + AppSettingsData.DefaultScreenOutputFolder + "/", "the screenshots", null, SettingsField.ScreenOutputFolder),
            new(HelpFileScope.Profile, AppSettings.ImagesFolderName + "/" + AppSettingsData.DefaultComfyOutputFolder + "/", "the pictures ComfyUI made", null, SettingsField.ComfyOutputFolder),
            new(HelpFileScope.Profile, AppSettings.ImagesFolderName + "/" + AppSettingsData.DefaultPastedImagesFolder + "/", "the pictures pasted from the clipboard", "`/pasted`", SettingsField.PastedImagesFolder),
            new(HelpFileScope.Profile, AppSettings.ImagesFolderName + "/" + UI.SplashImages.ProfileFolderName + "/", "the splash pictures, in place of the built-in ones", "`/splash`"),
            new(HelpFileScope.Profile, Skills.SkillRoots.DirectoryName + "/", "this profile's skills", "`/skills`"),
            new(HelpFileScope.Profile, Comfy.ComfyWorkflowCatalog.DirectoryName + "/", "this profile's ComfyUI workflows", "`/comfy`"),
            new(HelpFileScope.Profile, Viewer.BrowserPage.FolderName + "/", "the Browser window's browser data (cookies, cache)", "`/browser`"),
            new(HelpFileScope.WorkingFolder, Plans.PlanSlug.Folder + "/", "the plans, a folder each holding `ralph.md`, `ralph.lock` and the reference pictures", "`/plan`"),
            new(HelpFileScope.WorkingFolder, Audits.AuditFiles.Folder + "/", "the code audits' reports, `<name>.md` and `<name>.coverage.txt`", "`/code-audit`"),
            new(HelpFileScope.WorkingFolder, Shell.ShellText.SpillFolderName.Replace('\\', '/') + "/", "a cut command's whole output, `<id>.log`, for `read_file`"),
        ]);
        return rows;
    }

    private static string Join(string folder, string file) => folder.Replace('\\', '/') + "/" + file;

    /// <summary>A path as a system writes it: <c>\</c> on Windows, <c>/</c> elsewhere.</summary>
    public static string Shown(string path, bool windows) => windows ? path.Replace('/', '\\') : path;

    private static string ScopeWords(HelpFileScope scope) => scope switch
    {
        HelpFileScope.Home => "in the home folder",
        HelpFileScope.Profile => "in each profile's folder",
        _ => "in the working folder",
    };

    /// <summary>The home folder's default, as the system spells it.</summary>
    public static string HomeDefault(bool mac) => mac ? "~/" + AppSettings.DefaultDirectoryName : @"%USERPROFILE%\" + AppSettings.DefaultDirectoryName;

    /// <summary>One file or folder in full.</summary>
    public static string Answer(HelpFile file, bool windows, bool mac)
    {
        ArgumentNullException.ThrowIfNull(file);
        var sb = new StringBuilder(FileHeading(file, windows));
        string root = file.Scope switch
        {
            HelpFileScope.Home => $"`{HomeDefault(mac)}`, or the folder `{Settings.EnvironmentOverrides.HomeVariable}` names",
            HelpFileScope.Profile => $"`{Shown("<home>/" + Profiles.DirectoryName + "/<profile>/", windows)}`",
            _ => "the working directory's `" + Plans.PlanSlug.AppFolder + "` folder",
        };
        sb.Append("\n  Under: ").Append(root);
        if (file.Setting is { } field && HelpLocation.Of(field, windows) is { } row)
        {
            string value = HelpLocation.Default(field);
            sb.Append("\n  Setting: ").Append(row.Label).Append(" at ").Append(row.Path);
            if (!value.Contains("neon-help", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" (default `").Append(value).Append("`)");
            }
        }

        if (file.EditedBy is not null)
        {
            sb.Append("\n  Edited by: ").Append(file.EditedBy);
        }

        if (FormatOf(file.Name) is not null)
        {
            sb.Append("\n  Format: ask \"").Append(file.Name).Append(" format\"");
        }

        return sb.ToString();
    }

    /// <summary>The tree: the home's rows, a profile's, the working folder's, a line each.</summary>
    public static string Tree(bool windows, bool mac)
    {
        var sb = new StringBuilder(TreeHeading);
        foreach (var scope in (HelpFileScope[])[HelpFileScope.Home, HelpFileScope.Profile, HelpFileScope.WorkingFolder])
        {
            sb.Append("\n\n").Append(scope switch
            {
                HelpFileScope.Home => $"The home folder (`{HomeDefault(mac)}`, or `{Settings.EnvironmentOverrides.HomeVariable}`):",
                HelpFileScope.Profile => $"A profile's folder (`{Shown("<home>/" + Profiles.DirectoryName + "/<profile>/", windows)}`):",
                _ => "The working folder:",
            });
            foreach (var file in Rows(windows).Where(f => f.Scope == scope))
            {
                sb.Append("\n- `").Append(Shown(file.Path, windows)).Append("`: ").Append(file.Holds);
            }
        }

        return sb.ToString();
    }

    /// <summary>A connection file's format: its owner's empty text (this system's own), <c>mcp.json</c>'s <see cref="McpExample"/>; null for another name.</summary>
    public static string? FormatOf(string fileName) => fileName switch
    {
        Mcp.McpConfigFile.FileName => McpExample,
        Sql.SqlConfigFile.FileName => Sql.SqlConfigFile.EmptyText,
        Oracle.OracleConfigFile.FileName => Oracle.OracleConfigFile.EmptyText,
        MySql.MySqlConfigFile.FileName => MySql.MySqlConfigFile.EmptyText,
        Sqlite.SqliteConfigFile.FileName => Sqlite.SqliteConfigFile.EmptyText,
        DuckDb.DuckDbConfigFile.FileName => DuckDb.DuckDbConfigFile.EmptyText,
        Postgres.PostgresConfigFile.FileName => Postgres.PostgresConfigFile.EmptyText,
        Unc.UncConfigFile.FileName => Unc.UncConfigFile.EmptyText,
        EventLog.EventLogConfigFile.FileName => EventLog.EventLogConfigFile.EmptyText,
        Services.ServiceConfigFile.FileName => Services.ServiceConfigFile.EmptyText,
        Tasks.TaskConfigFile.FileName => Tasks.TaskConfigFile.EmptyText,
        ActiveDirectory.DirectoryConfigFile.FileName => ActiveDirectory.DirectoryConfigFile.EmptyText,
        _ => null,
    };

    /// <summary>A format answer, the text fenced so Markdown leaves its backslashes alone.</summary>
    public static string Format(string fileName, string text) => FormatHeading(fileName) + "\n```jsonc\n" + text.TrimEnd('\n') + "\n```";

    /// <summary>
    /// The files a query's tokens name (2026-10-25, step 12), each once, at most <see cref="HelpText.MaxMatches"/>: a name with its extension
    /// anywhere (<c>sql.json</c>, <c>configs\sql.json</c>), or a name before "file", "folder" or "format" (<c>camera folder</c>); before
    /// "format" a connection file gives its format.
    /// </summary>
    public static List<string> Find(IReadOnlyList<HelpToken> tokens, bool windows, bool mac)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var found = new List<string>();
        var seen = new HashSet<HelpFile>();
        foreach (var token in tokens)
        {
            string text = token.Text.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
            bool named = token.Next is "file" or "files" or "folder" or "folders" or "format";
            if (token.Next == "format" && FormatOf(Last(text)) is { } format && (windows || !IsWindowsOnly(Last(text))))
            {
                found.Add(Format(Last(text), format));
                if (found.Count == HelpText.MaxMatches)
                {
                    break;
                }

                continue;
            }

            foreach (var file in Rows(windows))
            {
                string path = file.Path.TrimEnd('/').ToLowerInvariant();
                bool match = string.Equals(text, path, StringComparison.Ordinal)
                    || (string.Equals(text, file.Name.ToLowerInvariant(), StringComparison.Ordinal) && (named || text.Contains('.', StringComparison.Ordinal)));
                if (match && seen.Add(file))
                {
                    found.Add(Answer(file, windows, mac));
                    if (found.Count == HelpText.MaxMatches)
                    {
                        return found;
                    }
                }
            }
        }

        return found;
    }

    private static string Last(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static bool IsWindowsOnly(string fileName) => fileName is Services.ServiceConfigFile.FileName or Tasks.TaskConfigFile.FileName;

    /// <summary>The rows for the free-word search.</summary>
    public static IEnumerable<HelpItem> Items(bool windows, bool mac) =>
        Rows(windows).Select(f => new HelpItem(f.Path.Replace('/', ' ').Replace('.', ' '), f.Holds, () => Answer(f, windows, mac)));
}
