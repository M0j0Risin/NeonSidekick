using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.Shell;

/// <summary>
/// The package-manager commands the database police let through (2026-10-10, the user's call, after <c>dotnet add package
/// Microsoft.Data.Sqlite</c> was refused for holding the word <c>sqlite</c> and the model added the reference by editing the
/// <c>.csproj</c> instead): installing, removing, restoring or listing a package declares a dependency and reaches no database.
/// <see cref="Strip"/> blanks every such segment of a line — the line split on <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c> and
/// line breaks — so the police judge only the rest: <c>dotnet add package X &amp;&amp; sqlite3 a.db</c> is still refused at
/// <c>sqlite3</c>, and a script that later uses the package is still read. A segment is cut only when it starts with one of
/// <see cref="Forms"/> and holds no shell operator or substitution outside quotes (<c>$(</c>, <c>${</c>, a backtick, <c>&lt;</c>,
/// <c>&gt;</c>, <c>@(</c>, a <c>&amp;</c> past a leading call operator, and since 2026-10-11 any bracket of <c>( ) { }</c>: PowerShell
/// runs a parenthesised argument). Pure.
/// </summary>
public static partial class PackageCommands
{
    /// <summary>The commands that pass, as the docs list them. Pinned.</summary>
    public static readonly IReadOnlyList<string> Forms =
    [
        "dotnet add|remove|list [<project>] package", "dotnet restore", "dotnet tool install|update|uninstall|restore",
        "nuget install|restore", "Install-Package", "Update-Package", "Uninstall-Package",
        "pip|pip3 install|uninstall|download|show", "python|python3|py -m pip install|uninstall|download|show",
        "uv add|remove", "uv pip install|uninstall", "poetry add|remove",
        "npm install|i|add|uninstall|remove|rm", "pnpm add|install|i|remove|rm", "yarn add|remove",
        "cargo add|remove", "gem install|uninstall", "go get",
    ];

    /// <summary><paramref name="line"/> with every package-manager segment blanked (its separators kept); the line itself when there is none.</summary>
    public static string Strip(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string[] parts = Separator().Split(line);
        bool cut = false;
        var sb = new StringBuilder(line.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            // Split with a capturing group: the even parts are segments, the odd ones the separators between them.
            if (i % 2 == 0 && IsPackageCommand(parts[i]))
            {
                cut = true;
                continue;
            }

            sb.Append(parts[i]);
        }

        return cut ? sb.ToString() : line;
    }

    /// <summary>Whether <paramref name="segment"/> (one command, no separators) only installs, removes, restores or lists packages. Pure.</summary>
    public static bool IsPackageCommand(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        string text = segment.Trim();
        if (text.StartsWith('&'))
        {
            text = text[1..].TrimStart();   // PowerShell's call operator
        }

        if (text.Length > 1 && text[0] is '"' or '\'' && text.IndexOf(text[0], 1) is int close and > 0)
        {
            text = text[1..close] + text[(close + 1)..];   // a quoted program path: "C:\tools\nuget.exe" install …
        }

        return text.Length > 0 && Command().IsMatch(text) && !HasOperator(text);
    }

    /// <summary>Whether <paramref name="text"/> holds a shell operator or substitution outside quotes.</summary>
    private static bool HasOperator(string text)
    {
        string bare = Quoted().Replace(text, "\"\"");
        return bare.Contains("$(", StringComparison.Ordinal) || bare.Contains("${", StringComparison.Ordinal) || bare.Contains("@(", StringComparison.Ordinal)
            // A bare ( or { too (2026-10-11, the review): PowerShell runs a parenthesised argument, so `pip install (sqlite3 a.db …)`
            // would run sqlite3 past the police; a script block's braces never belong in a package spec either.
            || bare.IndexOfAny(['`', '<', '>', '&', '(', ')', '{', '}']) >= 0
            // A double-quoted run expands $( and backticks in bash: never cut one holding them.
            || DoubleQuoted().Matches(text).Any(m => m.Value.Contains("$(", StringComparison.Ordinal) || m.Value.Contains('`'));
    }

    [GeneratedRegex(@"(&&|\|\||;|\||\r?\n)", RegexOptions.CultureInvariant)]
    private static partial Regex Separator();

    [GeneratedRegex(@"""[^""]*""|'[^']*'", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"""[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleQuoted();

    /// <summary>A package command at the start: an optional path to the program, its name (with <c>.exe</c> or <c>.cmd</c>), the verb.</summary>
    [GeneratedRegex(
        @"^(?:[\w.:\\/-]*[\\/])?(?:" +
        @"dotnet(?:\.exe)?\s+(?:(?:add|remove|list)\s+(?:\S+\s+)?package|restore|tool\s+(?:install|update|uninstall|restore))" +
        @"|nuget(?:\.exe)?\s+(?:install|restore)" +
        @"|(?:install|update|uninstall)-package" +
        @"|pip3?(?:\.exe)?\s+(?:install|uninstall|download|show)" +
        @"|(?:python3?|py)(?:\.exe)?\s+-m\s+pip\s+(?:install|uninstall|download|show)" +
        @"|uv(?:\.exe)?\s+(?:add|remove|pip\s+(?:install|uninstall))" +
        @"|poetry(?:\.exe)?\s+(?:add|remove)" +
        @"|npm(?:\.cmd)?\s+(?:install|i|add|uninstall|remove|rm)" +
        @"|pnpm(?:\.cmd)?\s+(?:add|install|i|remove|rm)" +
        @"|yarn(?:\.cmd)?\s+(?:add|remove)" +
        @"|cargo(?:\.exe)?\s+(?:add|remove)" +
        @"|gem(?:\.cmd)?\s+(?:install|uninstall)" +
        @"|go(?:\.exe)?\s+get" +
        @")(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Command();
}
