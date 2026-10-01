using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NeonSidekick.Llm;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

/// <summary>
/// What the About tab is built from: the version, the runtime the process runs on, the machine,
/// and the folders the app writes to. Read once per show by <see cref="Runtime"/>; tests build one by hand.
/// </summary>
/// <param name="Version">The app version, <c>major.minor.patch</c>.</param>
/// <param name="Framework">The runtime's own description (<c>.NET 10.0.0</c>).</param>
/// <param name="NativeAot">True in the published binary, false under the JIT (<c>dotnet run</c>).</param>
/// <param name="Architecture">The process architecture, lowercased (<c>x64</c>).</param>
/// <param name="Os">The OS description (<c>Microsoft Windows 10.0.26200</c>).</param>
/// <param name="ExecutablePath">The running executable, null when the runtime cannot tell.</param>
/// <param name="HomeDirectory">The settings home: the pointer, the models and the profiles.</param>
/// <param name="ProfileDirectory">The loaded profile's directory.</param>
/// <param name="ModelsDirectory">Where the Whisper, Silero, Vosk and Kokoro models live.</param>
public sealed record AboutFacts(
    string Version,
    string Framework,
    bool NativeAot,
    string Architecture,
    string Os,
    string? ExecutablePath,
    string HomeDirectory,
    string ProfileDirectory,
    string ModelsDirectory)
{
    /// <summary>
    /// The facts of this process. <see cref="RuntimeFeature.IsDynamicCodeSupported"/> is false only
    /// under NativeAOT, so it names the build without a define; nothing here is reflective.
    /// </summary>
    public static AboutFacts Runtime(string version, string homeDirectory, string profileDirectory, string modelsDirectory) =>
        new(
            version,
            RuntimeInformation.FrameworkDescription,
            !RuntimeFeature.IsDynamicCodeSupported,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.OSDescription,
            Environment.ProcessPath,
            homeDirectory,
            profileDirectory,
            modelsDirectory);
}

/// <summary>One third-party part the app ships with or talks to: what it is, which version, under which licence, and what it does here.</summary>
public sealed record Component(string Name, string Version, string License, string Role);

/// <summary>
/// The words for <c>/about</c>: an info pane with an About tab (the app, the runtime, the folders,
/// the servers it talks to), a Components tab (every third-party package and model with its
/// version and licence — a pinned list a test checks against the project file) and a License tab
/// (the repository's <c>LICENSE</c> — GPL-3.0 since 2026-09-20, MIT before — embedded in the exe), the same as
/// plain lines for a console without the pane. Pure statics, every string pinned. <c>Text</c> cells, never <c>Markup</c>: a path may hold brackets.
/// </summary>
public static class AboutText
{
    /// <summary>The info pane's strip label.</summary>
    public const string Label = "About";

    /// <summary>The tab titles.</summary>
    public const string AboutTabTitle = "General";
    public const string ComponentsTabTitle = "Components";
    public const string LicenseTabTitle = "License";

    /// <summary>The second line of the About tab.</summary>
    public const string Copyright = "© 2026 Christopher Nelson";
    public const string LicenseName = "GNU GPL v3";
    public const string LicenseNote = "(the License tab)";

    /// <summary>The About tab's row labels, in order.</summary>
    public const string RuntimeLabel = "Runtime";
    public const string OsLabel = "OS";
    public const string ExecutableLabel = "Executable";
    public const string HomeLabel = "Home";
    public const string ProfileLabel = "Profile";
    public const string ModelsLabel = "Models";
    public const string ServersLabel = "LLM servers";
    public const string EmbeddedLabel = "Embedded";
    public const string SpeechLabel = "Speech";

    /// <summary>The Executable row when the runtime cannot name the process.</summary>
    public const string UnknownExecutable = "(unknown)";

    /// <summary>The build, on the Runtime row.</summary>
    public const string NativeAotBuild = "native AOT";
    public const string JitBuild = "JIT";

    /// <summary>The Speech row. Pinned.</summary>
    public const string SpeechLine = "Kokoro-FastAPI over HTTP or KokoroSharp in-process (speech output) · Whisper + Silero VAD in-process (voice input) · Vosk in-process (wake word)";

    /// <summary>Ahead of the port list on the LLM servers row.</summary>
    public const string ServersLead = "any OpenAI-compatible /v1 endpoint; /server looks on 127.0.0.1 for ";

    /// <summary>The Components tab's column headings.</summary>
    public const string ComponentHeading = "Component";
    public const string VersionHeading = "Version";
    public const string LicenseHeading = "Licence";
    public const string RoleHeading = "Role";

    /// <summary>The version cell of a part that is not versioned here: the Whisper and Vosk models (one file per size or name).</summary>
    public const string Unversioned = "—";

    private const string Sep = " · ";

    /// <summary>
    /// Every third-party part, in the order the tab lists them (the user's order, 2026-09-20): the
    /// packages with the models beside the package that runs them — the versions the project file
    /// names (<c>AboutTextTests</c> reads it and fails when a bump leaves this list behind), the
    /// models voice input, the wake word and the in-process TTS download. The TTS server the user
    /// runs and the generated timezone table are not listed (the user's call). Pinned.
    /// </summary>
    public static readonly IReadOnlyList<Component> Components =
    [
        new("Spectre.Console", "0.57.2", "MIT", "the terminal UI: the transcript, the panes, the thumbnails"),
        new("Microsoft.Extensions.AI (+ .OpenAI)", "10.8.3", "MIT", "the chat client and the tool loop over the OpenAI SDK"),
        new("OpenAI (.NET SDK)", "2.12.0", "MIT", "the OpenAI-compatible wire format"),
        new("Microsoft.ML.OnnxRuntime", "1.22.0", "MIT", "the CPU inference runtime KokoroSharp runs on"),
        new("Microsoft.Data.Sqlite", "10.0.12", "MIT", "the session store; bundles SQLite (public domain) with FTS5 through SQLitePCLRaw (Apache-2.0)"),
        new("ModelContextProtocol.Core", "2.2.0", "Apache-2.0", "MCP client"),
        new("LibGit2Sharp", "0.32.0", "MIT", "the git tools over the repository under the working directory"),
        new("LibGit2Sharp.NativeBinaries", "2.0.324", "MIT", "libgit2 prebuilt for win-x64 (GPL-2.0 with the linking exception)"),
        new("Microsoft.Data.SqlClient", "7.1.0", "MIT", "the SQL tools' SQL Server client, with its native SNI network layer"),
        new("SqlServer.TransactSql.ScriptDom", "180.107.0", "MIT", "the T-SQL parser behind the SQL tools' read-only gate"),
        new("Oracle.ManagedDataAccess.Core", "23.26.301", "Oracle Free Use Terms", "the Oracle tools' fully managed Oracle client (ODP.NET Core)"),
        new("KokoroSharp", "0.8.0", "MIT", "Kokoro in-process; bundles espeak-ng (GPL-3.0) for the non-English voices"),
        new("Kokoro-82M in-process", "1.0", "Apache-2.0", "the TTS model in-process, downloaded on first use"),
        new("Whisper.net", "1.9.1", "MIT", "whisper.cpp bindings: speech-to-text and the Silero VAD in-process"),
        new("Whisper ggml models", Unversioned, "MIT", "OpenAI's Whisper weights, downloaded on first use"),
        new("Vosk", "0.3.38", "Apache-2.0", "the wake-word recogniser in-process"),
        new("Vosk models", Unversioned, "Apache-2.0", "wake-word models, downloaded on first use"),
        new("Silero VAD", "6.2.0", "MIT", "the voice-activity model, downloaded on first use"),
        new("PhotoSauce.MagicScaler", "0.15.0", "MIT", "image decode and downscale through Windows' WIC codecs"),
        new("Markdig", "1.3.2", "BSD-2-Clause", "the Markdown reader behind the styled transcript"),
        new("llama.cpp (llama-server)", EmbeddedLlm.LlamaRelease.Tag, "MIT", "the embedded model's server, downloaded on first use (2026-09-29)"),
        new("Gemma 4 (GGUF)", Unversioned, "Apache-2.0 / Gemma", "the embedded models (Unsloth's and HauhauCS's quantisations), downloaded when picked"),
    ];

    /// <summary>The manifest resource the project file embeds the repository's <c>LICENSE</c> as.</summary>
    public const string LicenseResourceName = "LICENSE";

    /// <summary>
    /// The repository's <c>LICENSE</c>, verbatim (the GPL-3.0 text since 2026-09-20, MIT before): embedded
    /// by the project file and read once from the manifest, LF-normalised with one trailing line break;
    /// a test compares it with the file. Empty when the resource is missing (a build without it), never a throw.
    /// </summary>
    public static string LicenseText => _licenseText ??= ReadLicense();

    private static string? _licenseText;

    private static string ReadLicense()
    {
        using var stream = typeof(AboutText).Assembly.GetManifestResourceStream(LicenseResourceName);
        if (stream is null)
        {
            return "";
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n").TrimEnd('\n') + "\n";
    }

    // ── The lines ───────────────────────────────────────────────────────────

    /// <summary><c>NeonSidekick 0.2.0</c>.</summary>
    public static string TitleLine(AboutFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return SidekickApp.Name + " " + facts.Version;
    }

    /// <summary><c>© 2026 Christopher Nelson · GNU GPL v3 (the License tab)</c>.</summary>
    public static string CopyrightLine => Copyright + Sep + LicenseName + " " + LicenseNote;

    /// <summary><c>.NET 10.0.0 · native AOT · x64</c>, or <c>· JIT ·</c> under <c>dotnet run</c>.</summary>
    public static string RuntimeLine(AboutFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Framework + Sep + (facts.NativeAot ? NativeAotBuild : JitBuild) + Sep + facts.Architecture;
    }

    /// <summary>
    /// The LLM servers row: the lead, then every candidate port with its conventional owner, in
    /// probe order — <c>LM Studio :1234, vLLM :8000, …</c> — from the probe's own list, so the row
    /// and <c>/server</c> can never disagree.
    /// </summary>
    public static string ServersLine()
    {
        var parts = new List<string>(LlmEndpointProbe.CandidatePorts.Length);
        foreach (int port in LlmEndpointProbe.CandidatePorts)
        {
            string name = LlmServer.PortNames.TryGetValue(port, out var owner) ? owner : "port";
            parts.Add(name + " :" + port.ToString(CultureInfo.InvariantCulture));
        }

        return ServersLead + string.Join(", ", parts);
    }

    /// <summary>
    /// The Embedded row (2026-09-29): the pinned llama.cpp build and where its runtimes and the models live —
    /// <c>llama.cpp b11258 · runtimes in &lt;home&gt;\llama · models in &lt;models&gt;\llm</c>.
    /// </summary>
    public static string EmbeddedLine(AboutFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return "llama.cpp " + EmbeddedLlm.LlamaRelease.Tag + Sep + "runtimes in " + Path.Combine(facts.HomeDirectory, "llama") + Sep + "models in " + Path.Combine(facts.ModelsDirectory, "llm");
    }

    /// <summary>The About tab's labelled rows, in order: the label and its value.</summary>
    public static IReadOnlyList<(string Label, string Value)> AboutRows(AboutFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return
        [
            (RuntimeLabel, RuntimeLine(facts)),
            (OsLabel, facts.Os),
            (ExecutableLabel, facts.ExecutablePath ?? UnknownExecutable),
            (HomeLabel, facts.HomeDirectory),
            (ProfileLabel, facts.ProfileDirectory),
            (ModelsLabel, facts.ModelsDirectory),
            (ServersLabel, ServersLine()),
            (EmbeddedLabel, EmbeddedLine(facts)),
            (SpeechLabel, SpeechLine),
        ];
    }

    // ── The tabs ────────────────────────────────────────────────────────────

    /// <summary>The About tab: the title and the copyright line, then the rows as two columns, the folders and the servers set apart by a blank row.</summary>
    public static IRenderable AboutTab(AboutFacts facts)
    {
        var rows = AboutRows(facts);
        var grid = ChatScreen.TwoColumns();
        for (int i = 0; i < rows.Count; i++)
        {
            // Three blocks: the runtime and the machine, the folders, the servers.
            if (rows[i].Label is HomeLabel or ServersLabel)
            {
                grid.AddRow(new Text(" "), new Text(""));
            }

            grid.AddRow(new Text(rows[i].Label, Theme.AccentSecondary), new Text(rows[i].Value, Theme.Body));
        }

        return new Rows(
            new Text(TitleLine(facts), Theme.Label),
            new Text(CopyrightLine, Theme.DimText),
            new Text(" "),
            grid);
    }

    /// <summary>The Components tab: a heading row, then one row per part — name, version, licence, role.</summary>
    public static IRenderable ComponentsTab()
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn(new GridColumn().PadRight(0));
        grid.AddRow(
            new Text(ComponentHeading, Theme.Label),
            new Text(VersionHeading, Theme.Label),
            new Text(LicenseHeading, Theme.Label),
            new Text(RoleHeading, Theme.Label));
        foreach (var part in Components)
        {
            grid.AddRow(
                new Text(part.Name, Theme.AccentSecondary),
                new Text(part.Version, Theme.Body),
                new Text(part.License, Theme.Body),
                new Text(part.Role, Theme.DimText));
        }

        return grid;
    }

    /// <summary>The License tab: the text, verbatim.</summary>
    public static IRenderable LicenseTab() => new Text(LicenseText.TrimEnd('\n'), Theme.Body);

    // ── Plain lines ─────────────────────────────────────────────────────────

    /// <summary>The three tabs as plain lines, for a console without the pane: each tab's title as a heading, its content indented.</summary>
    public static IEnumerable<string> Lines(AboutFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        yield return AboutTabTitle;
        yield return "  " + TitleLine(facts);
        yield return "  " + CopyrightLine;
        foreach (var (label, value) in AboutRows(facts))
        {
            yield return "  " + label.PadRight(13) + value;
        }

        yield return ComponentsTabTitle;
        foreach (var part in Components)
        {
            yield return "  " + part.Name + " " + part.Version + Sep + part.License + Sep + part.Role;
        }

        yield return LicenseTabTitle;
        foreach (var line in LicenseText.TrimEnd('\n').Split('\n'))
        {
            yield return "  " + line;
        }
    }
}
