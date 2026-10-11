using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonSidekick.Web;

/// <summary>Which Chromium a request claims to be: the brand its client hints name besides Chromium.</summary>
public enum BrowserBrand
{
    Chrome,
    Edge,
    Brave,
}

/// <summary>The operating system a request claims, in the user agent and <c>Sec-CH-UA-Platform</c>.</summary>
public enum BrowserPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// The browser the web requests claim to be (2026-10-11, the user's ask: get past more bot checks): a brand, a Chrome major
/// version and a platform, from which the user agent and the <c>Sec-CH-UA</c> brand list are worked out exactly as Chromium does.
/// The HTTP client's headers (<see cref="BrowserHeaders"/>) and the headless browser's <c>--user-agent</c> both come from here.
/// Until then the headers were pinned to Chrome 140 with a brand list Chromium never sends for 140, while the installed Edge was
/// 155 — a browser fourteen releases old is a cheap bot signal — and the headless browser said <c>HeadlessChrome</c> in its own
/// user agent. The version is read from the browser <see cref="HeadlessBrowser.Locate(string)"/> finds (<see cref="ReadVersion"/>,
/// once per path), so it stays current with the machine; without one it is <see cref="FallbackMajor"/>.
/// </summary>
public sealed partial record BrowserIdentity(BrowserBrand Brand, int Major, BrowserPlatform Platform)
{
    /// <summary>The Chrome major version claimed when no browser is found, or its version cannot be read; bump it now and then.</summary>
    public const int FallbackMajor = 154;

    /// <summary>The lowest version read from a file that is taken as a Chromium major (Chrome 100 shipped in 2022); a lower one is someone else's numbering.</summary>
    public const int LowestMajor = 100;

    /// <summary>Chrome at <see cref="FallbackMajor"/> on this machine's platform: what the HTTP client claims with no browser to read.</summary>
    public static BrowserIdentity Fallback { get; } = new(BrowserBrand.Chrome, FallbackMajor, CurrentPlatform);

    /// <summary>The platform this process runs on.</summary>
    public static BrowserPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? BrowserPlatform.Windows : OperatingSystem.IsMacOS() ? BrowserPlatform.MacOS : BrowserPlatform.Linux;

    /// <summary>
    /// The user agent Chromium sends with its reduced (frozen) form: the platform token, <c>Chrome/N.0.0.0</c>, and Edge's
    /// <c>Edg/N.0.0.0</c> after. Brave sends Chrome's exactly. Pinned.
    /// </summary>
    public string UserAgent
    {
        get
        {
            string platform = Platform switch
            {
                BrowserPlatform.MacOS => "Macintosh; Intel Mac OS X 10_15_7",
                BrowserPlatform.Linux => "X11; Linux x86_64",
                _ => "Windows NT 10.0; Win64; x64",
            };
            string major = Major.ToString(CultureInfo.InvariantCulture);
            string agent = "Mozilla/5.0 (" + platform + ") AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + major + ".0.0.0 Safari/537.36";
            return Brand == BrowserBrand.Edge ? agent + " Edg/" + major + ".0.0.0" : agent;
        }
    }

    /// <summary>The brand's name in the client hints.</summary>
    public string BrandName => Brand switch
    {
        BrowserBrand.Edge => "Microsoft Edge",
        BrowserBrand.Brave => "Brave",
        _ => "Google Chrome",
    };

    /// <summary><c>Sec-CH-UA-Platform</c>'s value, quotes included.</summary>
    public string PlatformHint => Platform switch
    {
        BrowserPlatform.MacOS => "\"macOS\"",
        BrowserPlatform.Linux => "\"Linux\"",
        _ => "\"Windows\"",
    };

    /// <summary>
    /// <c>Sec-CH-UA</c> as Chromium builds it (<c>GenerateBrandVersionList</c>): three entries — a GREASE brand, Chromium and
    /// the browser's own — in an order and with a GREASE name and version that all follow from the major version alone, so
    /// one version always sends one list. Checked against Chrome 154 (<c>Chromium, Google Chrome, Not A(Brand/99</c>) and
    /// Edge 155 (<c>Microsoft Edge, Chromium, Not(A:Brand/24</c>). Pinned.
    /// </summary>
    public string SecChUa
    {
        get
        {
            string[] greaseyChars = [" ", "(", ":", "-", ".", "/", ")", ";", "=", "?", "_"];
            string[] greasedVersions = ["8", "99", "24"];
            int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
            int seed = Major;
            string major = Major.ToString(CultureInfo.InvariantCulture);
            string grease = "Not" + greaseyChars[seed % greaseyChars.Length] + "A" + greaseyChars[(seed + 1) % greaseyChars.Length] + "Brand";
            var order = orders[seed % orders.Length];
            var list = new string[3];
            list[order[0]] = Entry(grease, greasedVersions[seed % greasedVersions.Length]);
            list[order[1]] = Entry("Chromium", major);
            list[order[2]] = Entry(BrandName, major);
            return string.Join(", ", list);

            static string Entry(string brand, string version) => "\"" + brand + "\";v=\"" + version + "\"";
        }
    }

    /// <summary>The brand an executable is, by its name: <c>msedge</c> or Microsoft Edge → Edge, <c>brave</c> → Brave, anything else Chrome. Pure; pinned.</summary>
    public static BrowserBrand BrandOf(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        string name = Path.GetFileNameWithoutExtension(executable.Replace('\\', '/').Split('/')[^1]);
        if (name.Contains("edge", StringComparison.OrdinalIgnoreCase))
        {
            return BrowserBrand.Edge;
        }

        return name.Contains("brave", StringComparison.OrdinalIgnoreCase) ? BrowserBrand.Brave : BrowserBrand.Chrome;
    }

    /// <summary>A version string's major, when it is a Chromium one (at least <see cref="LowestMajor"/>); null else. Pure; pinned.</summary>
    public static int? MajorOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        string first = version.Trim().Split('.')[0];
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out int major) && major >= LowestMajor && major < 10_000 ? major : null;
    }

    /// <summary>The identity of <paramref name="executable"/>, its version read by <paramref name="version"/>; <see cref="FallbackMajor"/> when that gives none. Pure.</summary>
    public static BrowserIdentity Of(string executable, Func<string, string?> version, BrowserPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(version);
        return new BrowserIdentity(BrandOf(executable), MajorOf(version(executable)) ?? FallbackMajor, platform);
    }

    private static readonly ConcurrentDictionary<string, BrowserIdentity> s_read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The identity of an installed browser, its version read from the file once per path for the run (an update lands at the next start).</summary>
    public static BrowserIdentity For(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return s_read.GetOrAdd(executable, path => Of(path, ReadVersion, CurrentPlatform));
    }

    /// <summary>
    /// An installed browser's version: on Windows the executable's product version (<c>155.0.4283.45</c>), on macOS the bundle's
    /// <c>CFBundleShortVersionString</c> from <c>Contents/Info.plist</c> beside <c>Contents/MacOS</c>; null where neither reads.
    /// </summary>
    public static string? ReadVersion(string executable)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).ProductVersion : null;
            }

            if (OperatingSystem.IsMacOS() && Path.GetDirectoryName(Path.GetDirectoryName(executable)) is { } contents)
            {
                string plist = Path.Combine(contents, "Info.plist");
                return File.Exists(plist) ? BundleVersion(File.ReadAllText(plist)) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Diagnostics.DiagnosticLog.Debug(WebFetcher.Category, $"No version for {executable}: {ex.Message}");
        }

        return null;
    }

    /// <summary>The <c>CFBundleShortVersionString</c> of an XML <c>Info.plist</c>, null without one. Pure; pinned.</summary>
    public static string? BundleVersion(string plist)
    {
        ArgumentNullException.ThrowIfNull(plist);
        var match = BundleVersionPattern().Match(plist);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"<key>\s*CFBundleShortVersionString\s*</key>\s*<string>([^<]*)</string>", RegexOptions.CultureInvariant)]
    private static partial Regex BundleVersionPattern();
}
