using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Viewer;

/// <summary>What a clear of the Browser window's data takes (<c>/browser clear history|all</c>, later on 2026-10-13).</summary>
public enum BrowserClear
{
    /// <summary>The browsing and download history, and the window's own list of visits; cookies and sign-ins stay.</summary>
    History,

    /// <summary>Everything the window's profile keeps: history, cookies, sign-ins, autofill, cache, storage, site settings.</summary>
    All,
}

/// <summary>What the Browser window keeps of the pages it went to (<see cref="BrowserPrivacy"/>).</summary>
public enum BrowserPrivacyKind
{
    /// <summary>History kept: Chromium's, and the window's own list in the profile's <c>browser-history.json</c>.</summary>
    KeepHistory,

    /// <summary>History cleared as the window closes (and, as a backstop, as it opens); the list lives only while the window is open.</summary>
    ClearHistory,
}

/// <summary>
/// The setting <c>Browser privacy</c> (2026-10-13, the user's ask and name): <c>keep-history</c> or <c>clear-history</c>, the
/// <see cref="Web.BotcheckWindow"/> shape. Under <c>clear-history</c> (the user's default) the Browser window clears its browsing and
/// download history when it closes; its cookies and sign-ins stay either way (<c>/browser clear all</c> takes those). The window's data
/// is the loaded profile's own (<see cref="BrowserPage.UserDataFolder"/>), so one profile's setting never reaches another's.
/// <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that is neither reads as
/// <see cref="Default"/>, with a warning once. Pure; pinned.
/// </summary>
public static class BrowserPrivacy
{
    public const string KeepHistory = "keep-history";
    public const string ClearHistory = "clear-history";

    /// <summary>History cleared at each close: the compiled default (the user's pick), pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = ClearHistory;

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { KeepHistory, ClearHistory };

    private const string Category = "Browser";

    /// <summary>The unknown value last warned of, so <see cref="Resolve"/> warns once per value (it is read at every close).</summary>
    private static string? s_warned;

    /// <summary>Trims and ignores case; false (and <see cref="BrowserPrivacyKind.ClearHistory"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out BrowserPrivacyKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case KeepHistory: kind = BrowserPrivacyKind.KeepHistory; return true;
            case ClearHistory: kind = BrowserPrivacyKind.ClearHistory; return true;
            default: kind = BrowserPrivacyKind.ClearHistory; return false;
        }
    }

    /// <summary>The saved word for <paramref name="kind"/>.</summary>
    public static string Name(BrowserPrivacyKind kind) => kind == BrowserPrivacyKind.KeepHistory ? KeepHistory : ClearHistory;

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        KeepHistory => "keeps the pages it visited, in its history menu too",
        ClearHistory => "clears its history when the window closes; cookies and sign-ins stay",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns once and uses <see cref="Default"/>.</summary>
    public static BrowserPrivacyKind Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.BrowserPrivacy, out var kind))
        {
            return kind;
        }

        if (!string.Equals(Interlocked.Exchange(ref s_warned, effective.BrowserPrivacy), effective.BrowserPrivacy, StringComparison.Ordinal))
        {
            DiagnosticLog.Warn(Category, $"{nameof(AppSettingsData.BrowserPrivacy)}='{effective.BrowserPrivacy}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return BrowserPrivacyKind.ClearHistory;
    }
}
