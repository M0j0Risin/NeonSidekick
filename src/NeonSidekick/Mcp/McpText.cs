using System.Globalization;
using System.Text;

namespace NeonSidekick.Mcp;

/// <summary>
/// Every sentence the MCP side shows or logs (2026-09-20), pinned: the status lines the screen and
/// headless print after a connect, the pane's labels, rows and notices (<c>App/McpMenu</c> builds the
/// markup over these words), the tool adapter's placeholders, the config problems and the <c>Mcp</c> log
/// lines. Pure.
/// </summary>
public static class McpText
{
    /// <summary>The glyph every MCP status line leads with (U+1F50C, two cells, no variation selector — the strip's rule).</summary>
    public const string Glyph = "🔌";

    /// <summary>The pane's title and the pickers' root: <c>🔌 MCP › MCP servers</c> (the glyph since later on 2026-09-21).</summary>
    public const string Label = Glyph + " MCP";

    // ── The status lines (the TTS: / STT: shape) ────────────────────────────

    /// <summary><c>🔌 MCP: 2 servers, 14 tools</c> after a connect; <c>🔌 MCP: no server connected</c> when every server failed or is off.</summary>
    public static string StatusLine(int servers, int tools) =>
        servers == 0 ? Glyph + " MCP: no server connected" : Glyph + " MCP: " + Servers(servers) + ", " + Tools(tools);

    /// <summary><c>🔌 MCP: docker failed: &lt;detail&gt;</c>, one warning per failed server.</summary>
    public static string FailedLine(string name, string detail) => Glyph + " MCP: " + name + " failed: " + detail;

    /// <summary>The spinner's label while the servers connect.</summary>
    public const string ConnectingLabel = "connecting MCP servers";

    /// <summary>The label as servers come up: <c>connecting MCP servers (1 of 3)</c>.</summary>
    public static string ConnectingProgress(int done, int total) =>
        ConnectingLabel + " (" + done.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>A server that did not finish its handshake and tool list inside <c>MCP connect timeout (s)</c>.</summary>
    public static string TimedOut(int seconds) => "timed out after " + seconds.ToString(CultureInfo.InvariantCulture) + " s";

    /// <summary>A connect the user cancelled (Ctrl+C under the spinner); the same word the speech session uses.</summary>
    public const string Cancelled = "cancelled";

    /// <summary><c>1 server</c> / <c>2 servers</c>.</summary>
    public static string Servers(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " server" : " servers");

    /// <summary><c>1 tool</c> / <c>14 tools</c>.</summary>
    public static string Tools(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " tool" : " tools");

    // ── The tool adapter ────────────────────────────────────────────────────

    /// <summary>What an image content block becomes in the model's result (pictures are not carried, 2026-09-20).</summary>
    public const string ImageDropped = "(an image block was dropped)";

    /// <summary>What any other non-text block becomes: <c>(an audio block was dropped)</c>.</summary>
    public static string BlockDropped(string type) => "(" + Article(type) + " " + type + " block was dropped)";

    /// <summary>A result with no content at all.</summary>
    public const string NoContent = "(no content)";

    /// <summary>A tool the server described with nothing.</summary>
    public const string NoDescription = "(no description)";

    /// <summary>What an <c>isError</c> result is prefixed with, so every tool error starts <c>Error:</c>.</summary>
    public const string ErrorPrefix = "Error: ";

    /// <summary>The text on one line with its whitespace collapsed (a server's description may be a paragraph; the grids are one row each). Empty stays empty.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    // ── The config problems ─────────────────────────────────────────────────

    public const string NeitherCommandNorUrl = "names neither a command nor a url";
    public const string BothCommandAndUrl = "names both a command and a url; one or the other";
    public const string BlankName = "a server with a blank name was skipped";
    public static string BadUrl(string url) => "the url '" + url + "' is not an absolute http(s) address";
    public static string UnreadableFile(string detail) => "could not be read: " + detail;

    /// <summary>A problem's source for one server: <c>&lt;path&gt; › docker</c>.</summary>
    public static string ServerSource(string path, string name) => path + " › " + name;

    /// <summary>The pane's line under <c>Skipped:</c>: <c>&lt;source&gt;: &lt;reason&gt;</c> (the skills' shape).</summary>
    public static string ProblemLine(McpConfigProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return problem.Source + ": " + problem.Reason;
    }

    /// <summary>The transport in a word and its address: <c>stdio: docker mcp gateway run</c>, <c>http: http://localhost:8811/mcp</c>.</summary>
    public static string Transport(McpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.IsHttp)
        {
            return "http: " + config.Url!.Trim();
        }

        string command = config.Command?.Trim() ?? "";
        return "stdio: " + (config.Args is { Count: > 0 } args ? command + " " + string.Join(' ', args) : command);
    }

    // ── The pane ────────────────────────────────────────────────────────────

    public const string ServersTabTitle = "Servers";
    public const string ToolsTabTitle = "Tools";
    public const string OptionsTabTitle = "Options";
    public static readonly IReadOnlyList<string> TabTitles = [ServersTabTitle, ToolsTabTitle, OptionsTabTitle];

    /// <summary>The Servers tab's hint.</summary>
    public const string ServersKeys = "Enter / Space = on or off · Enter on failed = retry · ←/→ tabs · ESC = close";

    /// <summary>The Servers and Tools tabs' first line while the master switch is off (shortened later on 2026-09-20: the server rows are inert then).</summary>
    public const string OffLine = "MCP servers is off (the Options tab)";

    /// <summary>The status line's answer to Enter or Space on a server row while the master switch is off (later on 2026-09-20, the user's ask): nothing saved, nothing started.</summary>
    public const string OffNotice = Glyph + " MCP servers is off: switch it on under the Options tab first";

    /// <summary>The Servers tab with nothing configured in either file.</summary>
    public const string NoServersLine = "no MCP server is configured — the edit rows below open mcp.json";

    /// <summary>The Tools tab with no server connected.</summary>
    public const string NoToolsLine = "no MCP server is connected";

    /// <summary>The heading over the problems (the skills' word).</summary>
    public const string SkippedHeading = "Skipped:";

    /// <summary>The Servers tab's three headings (2026-10-10, the user's ask: the <c>/settings</c> look): the servers, the files' rows and what was skipped. Pinned.</summary>
    public const string ServersHeading = "Servers";
    public const string FilesHeading = "Files";
    public const string ProblemsHeading = "Skipped";

    public const string EditProfileRow = "edit profile mcp.json";
    public const string EditGlobalRow = "edit global mcp.json";
    public const string ReloadRow = "reload";

    /// <summary>The dim status after a server's name and state.</summary>
    public static string StatusConnected(int tools) => "connected · " + Tools(tools);
    public const string StatusConnecting = "connecting";
    public static string StatusFailed(string detail) => "failed: " + detail;
    public const string StatusOff = "off";
    public const string StatusShadowed = "shadowed by the profile's";
    public const string GlobalMark = "(global)";

    /// <summary><c>docker: on</c> on the status line after a flip (the /tools shape).</summary>
    public static string ServerFlippedNotice(string name, bool on) => Glyph + " " + name + ": " + (on ? "on" : "off");
    public static string ConnectingNotice(string name) => Glyph + " connecting " + name + "…";
    public static string ConnectedNotice(string name, int tools) => Glyph + " " + name + ": connected, " + Tools(tools);
    public static string FailedNotice(string name, string detail) => name + " failed: " + detail;
    public static string StoppedNotice(string name) => Glyph + " " + name + ": stopped";
    public static string ReloadedNotice(int added, int removed, int kept) =>
        Glyph + " reloaded: " + added.ToString(CultureInfo.InvariantCulture) + " added, " + removed.ToString(CultureInfo.InvariantCulture) + " removed, " + kept.ToString(CultureInfo.InvariantCulture) + " kept";
    /// <summary>After an edit row: <c>opened the profile's mcp.json</c> / <c>opened the global mcp.json</c> (the path is long; the row says which).</summary>
    public static string EditingNotice(bool profile) => Glyph + " opened the " + (profile ? "profile's" : "global") + " " + McpConfigFile.FileName;
    public static string EditFailedError(string path, string detail) => "could not open " + path + ": " + detail;

    // ── The Mcp log lines ───────────────────────────────────────────────────

    public static string ConnectedLogLine(string name, int tools, TimeSpan elapsed) =>
        "Connected " + name + ": " + Tools(tools) + " in " + ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
    public static string FailedLogLine(string name, string detail) => "Failed " + name + ": " + detail;
    public static string StoppedLogLine(string name) => "Stopped " + name;
    public static string ConfigProblemLogLine(string path, string detail) => "Could not read " + path + ": " + detail;
    public static string StderrLogLine(string name, string line) => name + " stderr: " + line;

    private static string Article(string word) =>
        word.Length > 0 && "aeiouAEIOU".Contains(word[0]) ? "an" : "a";
}
