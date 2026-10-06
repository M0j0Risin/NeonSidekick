using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>
/// The outside-paths police on macOS (2026-10-06, the macOS build): a Unix root, zsh and bash command lines, the rules that
/// change off Windows — every absolute path outside the root refused, Git Bash's <c>/d/…</c> an absolute path, <c>C:</c> a name,
/// backslashes undone as the shell undoes them, <c>~name</c> another account's home. Skipped on Windows, whose paths
/// <see cref="PathPoliceTests"/> covers; the pure parts run there too.
/// </summary>
public sealed class PathPoliceUnixTests
{
    private const string Root = "/Users/me/Project";
    private const string Sub = "/Users/me/Project/sub";

    private static readonly string[] KnownFolders = ["/Users", "/etc", "/tmp", "/private", Root, Sub];

    private static bool Exists(string path) => KnownFolders.Contains(path, StringComparer.OrdinalIgnoreCase);

    private static string? NoLinks(string path) => null;

    private static string? Command(string text, ShellKind shell = ShellKind.Zsh) =>
        PathPolice.FirstOutside(text, Root, Root, isScript: false, Exists, NoLinks, shell);

    private static string? Script(string text) => PathPolice.FirstOutside(text, Root, Root, isScript: true, Exists, NoLinks);

    [UnixTheory]
    [InlineData("cat /etc/passwd", "/etc/passwd")]
    [InlineData("cat /Users/other/secret", "/Users/other/secret")]
    [InlineData("cp x /Users/me/Project2/y", "/Users/me/Project2/y")]   // a sibling that merely starts with the root's spelling
    [InlineData("ls /d/Repo", "/d/Repo")]                               // no Git Bash drives: an absolute path
    [InlineData("cd /", "/")]
    [InlineData("ls -la /", "/")]
    [InlineData("cd /etc", "/etc")]
    [InlineData("ls /tmp", "/tmp")]                                     // one segment that exists at /
    [InlineData("cat \\/etc/passwd", "\\/etc/passwd")]                  // zsh reads \/ as /: judged as /etc/passwd, named as typed
    [InlineData("cat /e\\tc/passwd", "/e\\tc/passwd")]
    [InlineData("ls ~", "~")]
    [InlineData("ls ~/Documents", "~/Documents")]
    [InlineData("ls ~root/x", "~root/x")]                               // another account's home
    [InlineData("ls \\~root", "\\~root")]
    [InlineData("cd ..", "..")]
    [InlineData("cat sub/../../x", "sub/../../x")]
    [InlineData("ls $HOME/x", "$HOME")]
    [InlineData("ls $TMPDIR", "$TMPDIR")]
    [InlineData("cd $OLDPWD", "$OLDPWD")]
    [InlineData("cd", "cd")]                                            // bare cd goes home
    public void ACommandLine_NamingAnOutsidePath_IsRefused(string command, string token) =>
        Assert.Equal(token, Command(command));

    [UnixTheory]
    [InlineData("ls")]
    [InlineData("ls -la")]
    [InlineData("cat /Users/me/Project/notes.txt")]
    [InlineData("cat sub/x.txt")]
    [InlineData("cd sub && cat ../x.txt")]
    [InlineData("ls /s")]                                               // nothing named s at /: not refused by the one-segment rule
    [InlineData("echo hi > /dev/null")]
    [InlineData("head -c 16 /dev/urandom")]
    [InlineData("scp notes.txt host:")]                                 // no drives: a bare x: is a name
    [InlineData("grep 'a\\nb' sub/x.txt")]
    [InlineData("git log --oneline")]
    [InlineData("curl https://example.com/a/b")]
    public void ACommandLine_StayingUnderTheRoot_Passes(string command) =>
        Assert.Null(Command(command));

    [UnixFact]
    public void Bash_ReadsTheSameAsZsh_OffWindows()
    {
        Assert.Equal("/c", Command("cd /c", ShellKind.Bash));
        Assert.Equal("/c/Users", Command("ls /c/Users", ShellKind.Bash));
    }

    [UnixFact]
    public void AScript_StillLeavesBackslashTokensAlone_ButNotAbsolutePaths()
    {
        Assert.Null(Script("print('\\t'.join(x))"));
        Assert.Equal("/etc/passwd", Script("open('/etc/passwd')"));
    }

    [Fact]
    public void Unescape_UndoesEachBackslash_AndDropsALastOneAlone()
    {
        Assert.Equal("/etc/passwd", PathPolice.Unescape("\\/etc/passwd"));
        Assert.Equal("a\\b", PathPolice.Unescape("a\\\\b"));
        Assert.Equal("ab", PathPolice.Unescape("ab\\"));
    }
}
