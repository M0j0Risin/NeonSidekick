using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>
/// The outside-paths police (2026-09-22): pure over a made-up root, <c>exists</c> a fake that knows a
/// few root folders — every rule of <see cref="PathPolice"/> with a token it refuses and one it lets by. Windows paths
/// throughout, so Windows-only (2026-10-06, the macOS build); <c>PathPoliceUnixTests</c> holds the Unix rules.
/// </summary>
public sealed class PathPoliceTests
{
    private const string Root = @"D:\Repo\Project";
    private const string Sub = @"D:\Repo\Project\sub";

    /// <summary>
    /// The disk as the rules see it: <c>D:\Users</c> and <c>D:\Windows</c> at the drive's root (the single-segment rule), and the
    /// folders a <c>cd</c> in the tests moves to (rule 11 follows a <c>cd</c> only to a folder that is there); nothing else exists.
    /// </summary>
    private static bool Exists(string path) => KnownFolders.Contains(path, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] KnownFolders =
        [@"D:\Users", @"D:\Windows", Root, Sub, Sub + @"\deeper", Root + @"\in", SpacedRoot, SpacedRoot + @"\sub"];

    /// <summary>No links anywhere: the disk the rules before 2026-10-03 assumed.</summary>
    private static string? NoLinks(string path) => null;

    private static string? Command(string text, string baseFolder = Root) => PathPolice.FirstOutside(text, Root, baseFolder, isScript: false, Exists, NoLinks);

    private static string? Script(string text) => PathPolice.FirstOutside(text, Root, Root, isScript: true, Exists, NoLinks);

    [WindowsTheory]
    [InlineData(@"type C:\Windows\win.ini", @"C:\Windows\win.ini")]
    [InlineData(@"type c:/windows/win.ini", @"c:/windows/win.ini")]
    [InlineData(@"copy x D:\Repo\Other\y", @"D:\Repo\Other\y")]
    [InlineData(@"copy x D:\Repo\Project2\y", @"D:\Repo\Project2\y")]   // a sibling that merely starts with the root's spelling
    [InlineData(@"dir ""C:\Program Files""", @"C:\Program")]   // a quote is a cut: the piece the rule reads
    [InlineData(@"\\server\share\x", @"\\server\share\x")]
    [InlineData(@"//server/share/x", @"//server/share/x")]
    [InlineData(@"cat /etc/hosts", "/etc/hosts")]
    [InlineData(@"type \Windows\win.ini", @"\Windows\win.ini")]
    [InlineData(@"ls /c/Users/me", "/c/Users/me")]
    [InlineData(@"cd /", "/")]
    [InlineData(@"ls -la /", "/")]
    [InlineData(@"Get-ChildItem -Path /", "/")]
    [InlineData(@"cd /Users", "/Users")]   // one segment that exists at the drive's root
    [InlineData(@"dir /Windows", "/Windows")]
    [InlineData(@"cd ..", "..")]
    [InlineData(@"type ..\secret.txt", @"..\secret.txt")]
    [InlineData(@"type sub/../../x", "sub/../../x")]
    [InlineData(@"ls ~", "~")]
    [InlineData(@"ls ~/Documents", "~/Documents")]
    [InlineData(@"ls ~\Documents", @"~\Documents")]
    [InlineData(@"dir %USERPROFILE%\Desktop", "%USERPROFILE%")]
    [InlineData(@"dir %ProgramFiles(x86)%", "%ProgramFiles(x86)%")]
    [InlineData(@"Get-ChildItem $env:TEMP", "$env:TEMP")]
    [InlineData(@"Get-ChildItem ${env:APPDATA}\x", "${env:APPDATA}")]
    [InlineData(@"ls $HOME/x", "$HOME")]
    [InlineData(@"ls ${HOME}/x", "${HOME}")]
    [InlineData(@"echo $tmpdir", "$tmpdir")]
    [InlineData(@"[Environment]::GetFolderPath('Desktop')", "GetFolderPath(")]
    public void ACommandLine_NamingAnOutsidePath_IsRefused_AndTheTokenIsNamed(string command, string token) =>
        Assert.Equal(token, Command(command));

    [WindowsTheory]
    [InlineData(@"dir")]
    [InlineData(@"type D:\Repo\Project\notes.txt")]
    [InlineData(@"type d:/repo/project/sub/x")]
    [InlineData(@"type D:\\Repo\\Project\\x")]   // an escaped spelling of the same path
    [InlineData(@"ls /d/Repo/Project/sub")]   // Git Bash's drive form, under the root
    [InlineData(@"type sub\..\notes.txt")]
    [InlineData(@"type sub/deeper/../x")]
    [InlineData(@"dir /s /b")]
    [InlineData(@"msbuild /t:Build /p:Configuration=Release /nologo")]
    [InlineData(@"robocopy src dst /MIR /XD .git")]
    [InlineData(@"findstr /i /c:""foo"" *.cs")]
    [InlineData(@"robocopy a b /purge")]   // one segment, nothing by that name at the drive's root: a switch as far as the text says
    [InlineData(@"$x -replace '\\', '/'")]   // a bare / that is not the first argument, and \\ alone
    [InlineData(@"echo a / b")]
    [InlineData(@"git log --format=%H")]
    [InlineData(@"echo %PATH% $env:CI $HOMEBREW ${env:PROCESSOR_LEVEL}")]
    [InlineData(@"curl https://example.invalid/api/users")]
    [InlineData(@"echo 1..10 ... x[a:b] ~x")]
    [InlineData(@"echo //TODO /* comment */")]
    [InlineData(@"python -c ""print('\\d+')""")]
    public void ACommandLine_UnderTheRoot_Passes(string command) =>
        Assert.Null(Command(command));

    [WindowsFact]
    public void ARelativePath_ResolvesFromTheWorkdir()
    {
        Assert.Equal("..", Command("cd ..", Root));
        Assert.Null(Command("cd ..", Sub));   // sub\.. is the root
        Assert.Equal(@"..\..", Command(@"cd ..\..", Sub));
        Assert.Null(Command(@"type ..\notes.txt", Sub));
    }

    [WindowsFact]
    public void ACompoundLine_IsJudgedSegmentBySegment()
    {
        Assert.Equal("/", Command("dotnet build && cd / && dir"));   // the first argument of its own segment
        Assert.Null(Command("dotnet build && dir | findstr x"));
        Assert.Equal(@"C:\x", Command("echo a; type C:\\x"));
    }

    [WindowsTheory]
    [InlineData("open(r'C:\\Users\\x.txt').read()", @"C:\Users\x.txt")]
    [InlineData("open(\"C:\\\\Users\\\\x.txt\")", @"C:\\Users\\x.txt")]
    [InlineData("fs.readFileSync('/etc/passwd')", "/etc/passwd")]
    [InlineData("fs.readFileSync('//server/share/x')", "//server/share/x")]
    [InlineData("open('../secret')", "../secret")]
    [InlineData("os.listdir('~')", "~")]
    [InlineData("os.path.expanduser('~/x')", "expanduser(")]
    [InlineData("from pathlib import Path\nprint(Path.home())", "Path.home(")]
    [InlineData("require('os').homedir()", "homedir(")]
    [InlineData("os.tmpdir()", "tmpdir(")]
    [InlineData("tempfile.gettempdir()", "gettempdir(")]
    [InlineData("[IO.Path]::GetTempPath()", "GetTempPath(")]
    [InlineData("Get-ChildItem $env:LOCALAPPDATA", "$env:LOCALAPPDATA")]
    [InlineData("x = '%SystemRoot%'", "%SystemRoot%")]
    [InlineData("shutil.copy('a', '/Users/me/b')", "/Users/me/b")]
    public void AScript_NamingAnOutsidePath_IsRefused(string code, string token) =>
        Assert.Equal(token, Script(code));

    [WindowsTheory]
    [InlineData("print('hi')")]
    [InlineData("open('notes.txt').read()")]
    [InlineData("open(r'D:\\Repo\\Project\\notes.txt')")]
    [InlineData("open('D:\\\\Repo\\\\Project\\\\sub\\\\x')")]
    [InlineData("open('sub/../notes.txt')")]
    [InlineData("print(\"\\t\\n\")")]   // escapes, not rooted paths
    [InlineData("re.match(r'\\d+\\s', x)")]   // a regex
    [InlineData("re.match('\\\\d+\\\\s', x)")]   // \\d+\\s is not \\server\\share
    [InlineData("const n = a / b; // a comment\n/* block */")]
    [InlineData("fetch('https://host/api/users')")]
    [InlineData("for i in range(1, 10): x[a:b]")]
    [InlineData("y = ~x; z = ...")]
    [InlineData("1..10 | ForEach-Object { $_ }")]
    [InlineData("Write-Output $env:NEONSIDEKICK_BRIDGE_ADDRESS $env:CI")]
    [InlineData("os.environ['PATH']")]
    [InlineData("route('/api')")]   // one rooted segment, nothing at the drive's root by that name
    [InlineData("x = 10/2")]
    public void AScript_UnderTheRoot_Passes(string code) =>
        Assert.Null(Script(code));

    [WindowsFact]
    public void TheFirstOffender_IsTheOneNamed()
    {
        Assert.Equal(@"C:\a", Command(@"type C:\a D:\b ~"));
        Assert.Equal("%TEMP%", Command(@"copy C:\a %TEMP%"));   // a folder variable anywhere in the text comes first: it is read over the whole line
    }

    [WindowsFact]
    public void Tokens_CutOnWhitespaceQuotesAndPunctuation_AndTrimTrailingMarks()
    {
        Assert.Equal(["dir", @"C:\x", "y", "--out", @"C:\z", "a", "b", "."], PathPolice.Tokens(@"dir ""C:\x"" 'y' --out=C:\z (a, b)."));
        Assert.Equal(["..", "...", "x..", "..", @"..\..", @"C:\x", "y"], PathPolice.Tokens(@".. ... x.. ..; ..\.. C:\x. y.,"));   // one sentence-ending dot goes, the dots of .. never
        Assert.Equal(["cd", "/d", "C:", "Note", "ab"], PathPolice.Tokens("cd /d C: Note: ab:"));   // a bare drive keeps its colon, a word's goes
        Assert.Empty(PathPolice.Tokens("  \n\t "));
        Assert.Equal("\"'`(),;=<>|&[]{}", PathPolice.Delimiters);
    }

    // ---- 2026-10-03: paths inside a token, bare drives, bare cd, quoted paths with a space, cd earlier in the line, links ----

    [WindowsTheory]
    [InlineData(@"csc -out:C:\x\a.exe a.cs", @"-out:C:\x\a.exe")]
    [InlineData(@"csc /out:C:\x\a.exe a.cs", @"/out:C:\x\a.exe")]
    [InlineData(@"cmd @C:\x\args.rsp", @"@C:\x\args.rsp")]
    [InlineData(@"curl file:///C:/Windows/win.ini", "file:///C:/Windows/win.ini")]
    [InlineData(@"curl file://server/share/x", "file://server/share/x")]
    [InlineData(@"curl file:///etc/passwd", "file:///etc/passwd")]
    [InlineData(@"Get-Content FileSystem::C:\x", @"FileSystem::C:\x")]
    [InlineData(@"Get-Content Microsoft.PowerShell.Core\FileSystem::\\server\share\x", @"Microsoft.PowerShell.Core\FileSystem::\\server\share\x")]
    [InlineData(@"tool -o:..\x", @"-o:..\x")]
    [InlineData(@"C: && dir", "C:")]
    [InlineData(@"cd /d C:", "C:")]
    [InlineData(@"Set-Location E:", "E:")]
    [InlineData(@"echo a:", "a:")]   // the price of the bare-drive rule, pinned
    [InlineData(@"cd \", @"\")]   // the drive root, as cd / is
    [InlineData(@"cd /d \", @"\")]   // a one-letter switch before it is an option
    [InlineData(@"cd", "cd")]   // PowerShell 7 and bash go home
    [InlineData(@"dotnet build && Set-Location", "Set-Location")]
    [InlineData(@"SL", "SL")]
    public void ACommandLine_NamingAnOutsidePath_TheNewWays_IsRefused(string command, string token) =>
        Assert.Equal(token, Command(command));

    [WindowsTheory]
    [InlineData(@"csc /out:D:\Repo\Project\bin\a.exe a.cs")]   // refused by the rooted rule until 2026-10-03
    [InlineData(@"csc -out:sub\a.exe a.cs")]
    [InlineData(@"curl file:///D:/Repo/Project/x")]
    [InlineData(@"D: && dir")]   // the root's own drive
    [InlineData(@"cd sub && type ..\notes.txt")]
    [InlineData(@"pushd sub && type ..\x")]
    [InlineData(@"Set-Location -Path sub; Get-Content ..\x")]
    [InlineData(@"cd /d D:\Repo\Project\sub && type ..\x")]
    [InlineData(@"cd sub && cd deeper && type ..\..\x")]
    [InlineData(@"git diff main..feature HEAD@{1}..HEAD")]
    [InlineData(@"docker run -v .\data:/data image")]
    [InlineData(@"scp a.txt user@host:/tmp")]
    [InlineData(@"Get-ChildItem HKLM:\Software")]
    [InlineData(@"cd -")]
    [InlineData(@"ping -n 30 127.0.0.1 >nul")]   // a device, not a path: made full it is \\.\nul
    [InlineData(@"dir x 2>NUL")]
    [InlineData(@"echo x > nul.txt")]   // Windows reads any extension as the device too
    [InlineData(@"type con")]
    [InlineData(@"ls > /dev/null 2>&1")]   // refused by the rooted rule until 2026-10-03
    [InlineData(@"cat /dev/stdin | sort")]
    public void ACommandLine_UnderTheRoot_TheNewWays_Passes(string command) =>
        Assert.Null(Command(command));

    [WindowsFact]
    public void ABareCd_InCmd_OnlyPrintsTheFolder_AndPasses()
    {
        Assert.Null(In(ShellKind.Cmd, "cd"));
        Assert.Null(In(ShellKind.Cmd, "cd /d"));
        Assert.Equal("cd", In(ShellKind.PowerShell, "cd"));
        Assert.Equal("cd", In(ShellKind.Bash, "cd"));
        Assert.Equal("..", In(ShellKind.Cmd, "cd .."));   // only the bare one
    }

    private static string? In(ShellKind shell, string text, Func<string, string?>? links = null) =>
        PathPolice.FirstOutside(text, Root, Root, isScript: false, Exists, links ?? NoLinks, shell);

    // ---- 2026-10-03, the review of the day's rules: each way out it found, refused, and its near neighbour still passing ----

    [WindowsTheory]
    // A cd that may not have moved what follows it: a pipe, ||, a subshell, bash's & (cmd's runs in turn), a folder that is not there.
    [InlineData(ShellKind.Cmd, @"cd sub | type ..\secret.txt", @"..\secret.txt")]
    [InlineData(ShellKind.Bash, @"cd sub || cat ../x", "../x")]
    [InlineData(ShellKind.Bash, @"(cd sub) && cat ../x", "../x")]
    [InlineData(ShellKind.Bash, @"cd sub & cat ../x", "../x")]
    [InlineData(ShellKind.Bash, @"cd nosuch; cat ../x", "../x")]
    [InlineData(ShellKind.Cmd, @"cd nosuch & type ..\x", @"..\x")]
    [InlineData(ShellKind.PowerShell, @"Set-Location sub | Get-Content ..\x", @"..\x")]
    [InlineData(ShellKind.Bash, @"cd sub | echo; cat ../x", "../x")]   // once in doubt, in doubt for the rest of the line
    // A bare cd behind options or a redirect still goes home.
    [InlineData(ShellKind.Bash, @"cd -- && cat .ssh/id_rsa", "cd")]
    [InlineData(ShellKind.Bash, @"cd -P; cat .bashrc", "cd")]
    [InlineData(ShellKind.Bash, @"cd >/dev/null && cat .bashrc", "cd")]
    [InlineData(ShellKind.Bash, @"cd 2>/dev/null; cat .bashrc", "cd")]
    [InlineData(ShellKind.PowerShell, @"Set-Location -PassThru; gc .ssh\config", "Set-Location")]
    // A device's name in front does not hide the .. after it.
    [InlineData(ShellKind.Cmd, @"type con.x\..\..\secret.txt", @"con.x\..\..\secret.txt")]
    [InlineData(ShellKind.Bash, @"cat aux.a/../../etc/x", "aux.a/../../etc/x")]
    [InlineData(ShellKind.Cmd, @"type nul\..\..\x", @"nul\..\..\x")]
    // A switch's letter glued to the path.
    [InlineData(ShellKind.Cmd, @"7z x a.zip -oC:\Windows\Temp", @"-oC:\Windows\Temp")]
    [InlineData(ShellKind.Cmd, @"cl -IC:\secret a.c", @"-IC:\secret")]
    [InlineData(ShellKind.Cmd, @"7z x a.zip -o\\server\share\x", @"-o\\server\share\x")]
    [InlineData(ShellKind.Bash, @"gcc -I../../inc a.c", "-I../../inc")]
    // Git Bash's drive mounts, and a cd's argument, which is a folder and never a switch.
    [InlineData(ShellKind.Bash, @"cd /c && cat Windows/win.ini", "/c")]
    [InlineData(ShellKind.Bash, @"ls /c", "/c")]
    [InlineData(ShellKind.Bash, @"cd /etc && cat passwd", "/etc")]
    [InlineData(ShellKind.PowerShell, @"cd \tools; gc x", @"\tools")]
    [InlineData(ShellKind.Cmd, @"cd /tools", "/tools")]
    public void TheReviewsWaysOut_AreRefused(ShellKind shell, string command, string token) =>
        Assert.Equal(token, In(shell, command));

    [WindowsTheory]
    [InlineData(ShellKind.Bash, @"cd sub && cat ../notes.txt")]
    [InlineData(ShellKind.Bash, @"cd sub; cat ../notes.txt")]
    [InlineData(ShellKind.PowerShell, @"Set-Location sub; Get-Content ..\x")]
    [InlineData(ShellKind.Cmd, @"cd sub & type ..\x")]   // cmd's & runs the next command after it, in the folder it left
    [InlineData(ShellKind.Cmd, @"cd /d sub && type ..\x")]
    [InlineData(ShellKind.Bash, @"cd sub && echo x | cat ../x")]   // a pipe after a cd that moved runs where it moved to
    [InlineData(ShellKind.Bash, @"cd - && ls")]   // the previous folder: not followed, not home
    [InlineData(ShellKind.Bash, @"cd sub 2>/dev/null && ls")]
    [InlineData(ShellKind.Cmd, @"type nul.txt")]
    [InlineData(ShellKind.Cmd, @"7z x a.zip -oD:\Repo\Project\out")]
    [InlineData(ShellKind.Cmd, @"cl -Iinclude a.c")]
    [InlineData(ShellKind.Cmd, @"dir /c")]   // a switch in cmd
    [InlineData(ShellKind.PowerShell, @"cmd /c dir")]
    [InlineData(ShellKind.Bash, @"cmd //c dir")]   // Git Bash's own spelling of a switch
    public void TheReviewsNeighbours_StillPass(ShellKind shell, string command) =>
        Assert.Null(In(shell, command));

    [WindowsFact]
    public void AQuotedStringWithASpace_IsNotJoined_WhenAWordOfItLeadsOutByALink()
    {
        // bash -c runs the words as separate arguments: link/secret on its own goes through the link, so the join may not hide it.
        Assert.Equal("out/secret", In(ShellKind.Bash, @"bash -c ""./tool.sh out/secret""", Links));
        Assert.Null(In(ShellKind.Bash, @"bash -c ""./tool.sh in/secret""", Links));
    }

    [Fact]
    public void TheDevices_ArePinned() =>
        Assert.Equal(
            ["nul", "con", "prn", "aux", "conin$", "conout$", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
             "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9", "/dev/null", "/dev/stdin", "/dev/stdout", "/dev/stderr", "/dev/tty",
             "/dev/zero", "/dev/random", "/dev/urandom"],
            PathPolice.Devices);

    [WindowsFact]
    public void ACdEarlierInTheLine_MovesWhereLaterPathsResolveFrom_OnlyInsideTheRoot()
    {
        Assert.Equal(@"..\..\x", Command(@"cd sub && type ..\..\x"));   // sub\..\.. is the root's parent
        Assert.Equal(@"..\x", Command(@"cd sub; cd ..; type ..\x"));   // back at the root, .. leaves it
        Assert.Equal(@"..\x", Command(@"cd $env:CI && type ..\x"));   // a variable is not followed: the root stays the base
        Assert.Equal(@"..\x", Command(@"type ..\x && cd sub"));   // a cd counts only for what comes after it
        Assert.Null(Command(@"cd sub && type ..\x", Sub));   // from the workdir: sub\sub\..\x is sub\x
    }

    [WindowsTheory]
    [InlineData(@"type ""D:\Repo\My Project\a.txt""")]
    [InlineData(@"type 'D:\Repo\My Project\sub dir\a.txt'")]
    [InlineData(@"cd ""D:\Repo\My Project\sub"" && type ..\a.txt")]   // joined, then followed by the cd
    [InlineData(@"type ""..\My Project\a.txt""")]   // relative too: ..\My alone would be D:\Repo\My
    public void AQuotedPathWithASpace_UnderARootWithASpace_Passes(string command) =>
        Assert.Null(PathPolice.FirstOutside(command, SpacedRoot, SpacedRoot, isScript: false, Exists, NoLinks));

    [WindowsTheory]
    [InlineData(@"type ""D:\Repo\My Project 2\a.txt""", @"D:\Repo\My")]   // outside: cut as before, the first piece named
    [InlineData(@"type ""D:\Repo\My Project\a C:\x""", @"D:\Repo\My")]   // a later word that is a path is never hidden in a join: cut, as before
    [InlineData(@"type ""D:\Repo\My Project\a ..\..\..\x""", @"D:\Repo\My")]
    [InlineData(@"type ""C:\Program Files\x""", @"C:\Program")]
    public void AQuotedPathWithASpace_Outside_IsStillRefused(string command, string token) =>
        Assert.Equal(token, PathPolice.FirstOutside(command, SpacedRoot, SpacedRoot, isScript: false, Exists, NoLinks));

    [WindowsFact]
    public void AQuotedPathWithASpace_InAScript_Passes()
    {
        Assert.Null(PathPolice.FirstOutside("open(\"D:\\\\Repo\\\\My Project\\\\a.txt\").read()", SpacedRoot, SpacedRoot, isScript: true, Exists, NoLinks));
        Assert.Equal(@"D:\\Repo\\My", PathPolice.FirstOutside("open(\"D:\\\\Repo\\\\My Projects\\\\a.txt\")", SpacedRoot, SpacedRoot, isScript: true, Exists, NoLinks));
    }

    private const string SpacedRoot = @"D:\Repo\My Project";

    /// <summary>The links of the link tests: <c>out</c> leads to <c>C:\</c>, <c>in</c> to the root's <c>sub</c>, <c>loop</c> to itself, <c>hop</c> to <c>in</c>'s sibling link <c>out</c>.</summary>
    private static string? Links(string path) => path switch
    {
        @"D:\Repo\Project\out" => @"C:\",
        @"D:\Repo\Project\in" => Sub,
        @"D:\Repo\Project\loop" => @"D:\Repo\Project\loop",
        @"D:\Repo\Project\hop" => @"D:\Repo\Project\out",
        _ => null,
    };

    [WindowsTheory]
    [InlineData(@"type out\x", @"out\x")]
    [InlineData(@"dir out", "out")]
    [InlineData(@"type D:\Repo\Project\out\x", @"D:\Repo\Project\out\x")]
    [InlineData(@"type hop\x", @"hop\x")]   // a link to a link that leads out
    [InlineData(@"type loop\x", @"loop\x")]   // a loop is given up on, and refused
    [InlineData(@"cd in && type ..\..\x", @"..\..\x")]   // in is sub: two up from it leaves the root
    public void ALinkLeadingOutside_IsRefused(string command, string token) =>
        Assert.Equal(token, PathPolice.FirstOutside(command, Root, Root, isScript: false, Exists, Links));

    [WindowsTheory]
    [InlineData(@"type in\x")]
    [InlineData(@"type in\..\notes.txt")]   // .. is spelling, as Win32 reads it: the root's notes.txt
    [InlineData(@"cd in && type ..\notes.txt")]
    [InlineData(@"dir outer")]   // a name that merely starts like a link's
    public void ALinkLeadingInside_Passes(string command) =>
        Assert.Null(PathPolice.FirstOutside(command, Root, Root, isScript: false, Exists, Links));

    [WindowsFact]
    public void ALinkLeadingOutside_InAScript_IsRefused() =>
        Assert.Equal(@"out/x.txt", PathPolice.FirstOutside("open('out/x.txt').read()", Root, Root, isScript: true, Exists, Links));

    [WindowsFact]
    public void TheCdLists_ArePinned()
    {
        Assert.Equal(["cd", "chdir", "pushd", "Set-Location", "sl", "Push-Location"], PathPolice.CdCommands);
        Assert.Equal(["cd", "chdir", "Set-Location", "sl"], PathPolice.BareCdCommands);
    }

    [WindowsFact]
    public void Judge_SeesARealJunctionLeadingOutside()
    {
        string dir = Path.Combine(Path.GetTempPath(), "neon-police-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(dir, "root");
        string outside = Path.Combine(dir, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(outside);
        try
        {
            Junction.Make(Path.Combine(root, "link"), outside);
            Junction.Make(Path.Combine(root, "near"), Path.Combine(root, "sub"));
            var files = new Files.WorkingDirectory(() => root, TimeProvider.System);
            Assert.Equal(@"link\x", PathPolice.Judge(@"type link\x", files, root, isScript: false));
            Assert.Null(PathPolice.Judge(@"type near\x", files, root, isScript: false));
            Assert.Equal(Files.FileOutcome.OutsideRoot, files.Resolve(@"link\x", forWrite: false, out _));
            Assert.Equal(Files.FileOutcome.Ok, files.Resolve(@"near\x", forWrite: false, out _));
        }
        finally
        {
            Junction.DeleteTree(dir);
        }
    }

    [Fact]
    public void TheLists_ArePinned()
    {
        Assert.Equal(["USERPROFILE", "HOMEPATH", "HOMEDRIVE", "HOME", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432", "PROGRAMDATA", "ALLUSERSPROFILE", "PUBLIC", "ONEDRIVE", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "OLDPWD"], PathPolice.FolderVariables);
        Assert.Equal(["expanduser(", "Path.home(", "homedir(", "tmpdir(", "gettempdir(", "GetFolderPath(", "GetTempPath("], PathPolice.FolderCalls);
        foreach (string name in PathPolice.FolderVariables)
        {
            Assert.Equal("%" + name + "%", Command("echo %" + name + "%"));
            if (!name.Contains('('))
            {
                Assert.Equal("$env:" + name, Command("echo $env:" + name));
                Assert.Equal("$" + name.ToLowerInvariant(), Command("echo $" + name.ToLowerInvariant()));
            }
        }
    }

    [WindowsFact]
    public void Judge_ReadsTheRealDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "neon-police-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = new Files.WorkingDirectory(() => dir, TimeProvider.System);
            Assert.Null(PathPolice.Judge("dir " + dir, files, dir, isScript: false));
            Assert.Equal("..", PathPolice.Judge("cd ..", files, dir, isScript: false));
            Assert.Equal("/Windows", PathPolice.Judge("dir /Windows", files, dir, isScript: false));   // exists on every Windows system drive; the temp folder lives there
            Assert.Null(PathPolice.Judge("dir /nothing-here-" + Guid.NewGuid().ToString("N"), files, dir, isScript: false));
            Assert.Equal("/c", PathPolice.Judge("cd /c", files, dir, isScript: false, "bash"));   // the shell's name reaches the rules
            Assert.Null(PathPolice.Judge("cd /c", files, dir, isScript: false, "cmd"));   // a switch there, and a bare cd only prints
            Assert.Equal("cd", PathPolice.Judge("cd /c", files, dir, isScript: false, "powershell"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
