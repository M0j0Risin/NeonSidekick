namespace NeonSidekick.Files;

/// <summary>
/// What the model's <c>open</c> tool may hand to the shell (2026-10-22, review G1 and P6, the fix plan's D1, the user's call: an allow-list,
/// not a deny-list). <c>open</c> shell-executed any file in the sandbox, so <c>write_file x.bat</c> then <c>open x.bat</c> ran a program past
/// <c>Shell command policy</c>, the approval pane and the shell police alike (a <c>.vbs</c>, <c>.js</c>, <c>.hta</c>, <c>.lnk</c>, <c>.msi</c>
/// the same; on a Mac a <c>.command</c> or an <c>.app</c>). Now only folders and files whose extension is on one list — documents,
/// pictures, PDFs, audio and video, plain text and data, source code a double-click opens in an editor — open; everything else, an
/// extension nobody listed or none at all included, is refused with a sentence naming its kind and pointing at the user's own
/// <c>/open</c>. A folder named like a package (<c>x.app</c>, a Mac bundle) is a file for this rule. Only the tool: <c>/open</c>, the
/// viewer and the user's own commands open what they always did. Pure.
/// </summary>
public static class OpenRules
{
    /// <summary>The extensions <c>open</c> hands to the shell. Source code is here only where a double-click opens an editor, never a runtime.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // documents
        ".pdf", ".rtf", ".doc", ".docx", ".odt", ".xls", ".xlsx", ".ods", ".ppt", ".pptx", ".odp", ".pages", ".numbers", ".key", ".epub",
        ".html", ".htm", ".xps", ".oxps",
        // pictures
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl", ".ico", ".svg",
        ".psd", ".raw", ".dng", ".cr2", ".nef", ".arw",
        // audio and video
        ".mp3", ".wav", ".flac", ".ogg", ".oga", ".m4a", ".aac", ".opus", ".wma", ".aiff", ".aif", ".mid", ".midi",
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".mpg", ".mpeg", ".3gp",
        // plain text and data
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg",
        ".conf", ".diff", ".patch", ".srt", ".vtt", ".tex", ".rst", ".adoc", ".org", ".nfo", ".properties",
        // source code an editor opens
        ".cs", ".java", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".go", ".rs", ".ts", ".tsx", ".jsx", ".css", ".scss", ".less", ".sql",
        ".kt", ".swift", ".fs", ".dart", ".lua", ".r", ".m", ".scala", ".zig",
    };

    /// <summary>Programs and the things that start one.</summary>
    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msc", ".dll", ".sys", ".drv", ".ocx", ".app", ".appex", ".xpc", ".bundle", ".plugin",
        ".prefpane", ".kext", ".framework", ".saver", ".jar", ".gadget", ".application", ".xbap",
    };

    /// <summary>Scripts, which a double-click runs on one system or the other.</summary>
    private static readonly HashSet<string> Scripts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".py", ".pyw", ".pyz", ".sh",
        ".bash", ".zsh", ".command", ".tool", ".pl", ".rb", ".php", ".applescript", ".scpt", ".scptd", ".workflow", ".action", ".reg", ".chm",
        ".inf", ".ins", ".isp", ".sct", ".ws", ".mshxml", ".docm", ".xlsm", ".pptm", ".xlam", ".ppam", ".dotm",
    };

    /// <summary>Shortcuts and links, which open whatever they name.</summary>
    private static readonly HashSet<string> Shortcuts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".webloc", ".inetloc", ".appref-ms", ".settingcontent-ms", ".desktop", ".library-ms", ".searchconnector-ms", ".scf",
        ".website", ".fileloc", ".alias",
    };

    /// <summary>Installers and packages.</summary>
    private static readonly HashSet<string> Installers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msi", ".msp", ".mst", ".appx", ".appxbundle", ".msix", ".msixbundle", ".appinstaller", ".pkg", ".mpkg", ".dmg", ".deb", ".rpm",
        ".vsix", ".crx", ".xpi",
    };

    /// <summary>The folder names a Mac (or the shell) treats as one file that runs: a bundle opens as a program, not as a folder.</summary>
    private static readonly HashSet<string> PackageFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".app", ".appex", ".xpc", ".bundle", ".plugin", ".prefpane", ".kext", ".framework", ".saver", ".workflow", ".action", ".pkg",
        ".mpkg", ".scptd",
    };

    /// <summary>Why <c>open</c> refuses <paramref name="full"/> (<see cref="FileText.OpenRefused"/> words it), or null to open it.</summary>
    public static OpenRefusal? Refusal(string full, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(full);
        string name = Path.GetFileName(full.TrimEnd('\\', '/'));
        string extension = Path.GetExtension(name);
        if (isDirectory)
        {
            return PackageFolders.Contains(extension) ? OpenRefusal.Program : null;
        }

        if (Allowed.Contains(extension))
        {
            return null;
        }

        if (Programs.Contains(extension))
        {
            return OpenRefusal.Program;
        }

        if (Scripts.Contains(extension))
        {
            return OpenRefusal.Script;
        }

        if (Shortcuts.Contains(extension))
        {
            return OpenRefusal.Shortcut;
        }

        return Installers.Contains(extension) ? OpenRefusal.Installer : OpenRefusal.Unlisted;
    }
}

/// <summary>The kind of thing <see cref="OpenRules"/> keeps <c>open</c> from starting.</summary>
public enum OpenRefusal
{
    Program,
    Script,
    Shortcut,
    Installer,
    Unlisted,
}
