using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonSidekick.Web;

/// <summary>
/// Which browser a request claims to be: a Chromium, by the brand its client hints name besides Chromium, or Safari (2026-10-18, the Mac's
/// Browser window: WebKit, which sends no client hints).
/// </summary>
public enum BrowserBrand
{
    Chrome,
    Edge,
    Brave,

    /// <summary>
    /// Safari (2026-10-18): what the Mac's Browser window is, a WKWebView claiming Safari's own user agent, so the cookies a person earns
    /// there are replayed under the same name (<see cref="BrowserHeaders.Apply"/> sends Safari's header set for it).
    /// </summary>
    Safari,
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
/// again whenever the file it comes from changes, <see cref="For(string)"/>), so it stays current with the machine; without one it is
/// <see cref="FallbackMajor"/>.
/// </summary>
public sealed partial record BrowserIdentity(BrowserBrand Brand, int Major, BrowserPlatform Platform)
{
    /// <summary>The Chrome major version claimed when no browser is found, or its version cannot be read; bump it now and then.</summary>
    public const int FallbackMajor = 154;

    /// <summary>The lowest version read from a file that is taken as a Chromium major (Chrome 100 shipped in 2022); a lower one is someone else's numbering.</summary>
    public const int LowestMajor = 100;

    /// <summary>The Safari version claimed when Safari's own cannot be read (2026-10-18): the release the Mac pass was built against.</summary>
    public const string SafariFallbackVersion = "26.6";

    /// <summary>
    /// The minor version (2026-10-18, Safari: its user agent carries <c>26.6</c>, not a Chromium major alone); 0 for the Chromiums, whose
    /// reduced user agent says <c>N.0.0.0</c>.
    /// </summary>
    public int Minor { get; init; }

    /// <summary>Whether this is a Chromium (Chrome, Edge, Brave), which sends client hints; Safari sends none.</summary>
    public bool IsChromium => Brand != BrowserBrand.Safari;

    /// <summary>Chrome at <see cref="FallbackMajor"/> on this machine's platform: what the HTTP client claims with no browser to read.</summary>
    public static BrowserIdentity Fallback { get; } = new(BrowserBrand.Chrome, FallbackMajor, CurrentPlatform);

    /// <summary>The platform this process runs on.</summary>
    public static BrowserPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? BrowserPlatform.Windows : OperatingSystem.IsMacOS() ? BrowserPlatform.MacOS : BrowserPlatform.Linux;

    /// <summary>
    /// Safari at <paramref name="version"/> (<c>CFBundleShortVersionString</c>, <c>26.6</c>) on macOS (2026-10-18, the Mac's Browser window);
    /// <see cref="SafariFallbackVersion"/> when the version is missing or no <c>major[.minor[.patch]]</c>. Pure; pinned.
    /// </summary>
    public static BrowserIdentity Safari(string? version)
    {
        if (!TryReadSafariVersion(version, out int major, out int minor))
        {
            TryReadSafariVersion(SafariFallbackVersion, out major, out minor);
        }

        return new BrowserIdentity(BrowserBrand.Safari, major, BrowserPlatform.MacOS) { Minor = minor };
    }

    private static bool TryReadSafariVersion(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        string[] parts = (version ?? "").Trim().Split('.');
        return parts.Length is >= 1 and <= 3
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) && major is >= 10 and < 1000
            && (parts.Length == 1 || int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor) && minor < 1000)
            && (parts.Length < 3 || int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    /// <summary>
    /// The user agent Chromium sends with its reduced (frozen) form: the platform token, <c>Chrome/N.0.0.0</c>, and Edge's
    /// <c>Edg/N.0.0.0</c> after. Brave sends Chrome's exactly. Safari's (2026-10-18) is its own, on macOS's frozen platform token:
    /// <c>… AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.6 Safari/605.1.15</c>, the string the Mac's Browser window sets as its
    /// <c>customUserAgent</c> (a WKWebView's own leaves out <c>Version/…</c> and <c>Safari/…</c>). Pinned.
    /// </summary>
    public string UserAgent
    {
        get
        {
            if (Brand == BrowserBrand.Safari)
            {
                return "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/"
                    + Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture) + " Safari/605.1.15";
            }

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
        BrowserBrand.Safari => "Safari",
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

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, BrowserIdentity Identity)> s_read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The identity of an installed browser: its version read once per path and again when the file it comes from (<see cref="VersionSource"/>)
    /// has a new write time — 2026-10-11, the user's ask: until then a browser that updated while the app ran was claimed at its old version
    /// to the next start, while its own client hints said the new one. On Windows the stamp is the executable's, which is also the one a run
    /// starts: Chrome and Edge stage an update as <c>new_chrome.exe</c>/<c>new_msedge.exe</c> and rename it over the executable when they
    /// restart, so the claim moves with the browser that actually runs.
    /// </summary>
    public static BrowserIdentity For(string executable) => For(executable, VersionStamp, ReadVersion, CurrentPlatform);

    /// <summary><see cref="For(string)"/> with the stamp and the version read by the caller (tests). Shares the run's cache, keyed by path.</summary>
    internal static BrowserIdentity For(string executable, Func<string, DateTime> stamp, Func<string, string?> version, BrowserPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(version);
        DateTime now = stamp(executable);
        if (s_read.TryGetValue(executable, out var held) && held.Stamp == now)
        {
            return held.Identity;
        }

        var identity = Of(executable, version, platform);
        s_read[executable] = (now, identity);
        return identity;
    }

    /// <summary>The file a browser's version is read from: the executable on Windows, the bundle's <c>Contents/Info.plist</c> on macOS (beside <c>Contents/MacOS</c>), null elsewhere.</summary>
    public static string? VersionSource(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        if (OperatingSystem.IsWindows())
        {
            return executable;
        }

        return OperatingSystem.IsMacOS() && Path.GetDirectoryName(Path.GetDirectoryName(executable)) is { } contents ? Path.Combine(contents, "Info.plist") : null;
    }

    /// <summary>The write time of <see cref="VersionSource"/> (UTC), <see cref="DateTime.MinValue"/> without one or when it cannot be read.</summary>
    public static DateTime VersionStamp(string executable)
    {
        try
        {
            return VersionSource(executable) is { } source && File.Exists(source) ? File.GetLastWriteTimeUtc(source) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// An installed browser's version: on Windows the executable's product version (<c>155.0.4283.45</c>), on macOS the bundle's
    /// <c>CFBundleShortVersionString</c> from <see cref="VersionSource"/>; null where neither reads.
    /// </summary>
    public static string? ReadVersion(string executable)
    {
        try
        {
            if (VersionSource(executable) is not { } source || !File.Exists(source))
            {
                return null;
            }

            return OperatingSystem.IsWindows() ? FileVersionInfo.GetVersionInfo(source).ProductVersion : BundleVersion(File.ReadAllText(source));
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
