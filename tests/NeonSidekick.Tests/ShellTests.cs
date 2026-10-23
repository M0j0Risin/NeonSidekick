using System.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>The pure parts of the shell area (2026-09-21): the words, the prefixes, the allow list, the gate, the probe, the command lines, the child environment, the output ring and every pinned sentence.</summary>
public sealed class ShellTests
{
    // ── The words ────────────────────────────────────────────────────────────

    [WindowsFact]
    public void ShellKinds_AndCommandPolicy_ArePinned()
    {
        Assert.Equal(["powershell", "cmd", "bash"], ShellKinds.Names);
        Assert.Equal("powershell", ShellKinds.Default);
        Assert.True(ShellKinds.TryParse(" CMD ", out var cmd) && cmd == ShellKind.Cmd);
        Assert.True(ShellKinds.TryParse("bash", out var bash) && bash == ShellKind.Bash);
        Assert.False(ShellKinds.TryParse("fish", out var fallback));
        Assert.Equal(ShellKind.PowerShell, fallback);
        Assert.Equal("cmd", ShellKinds.Name(ShellKind.Cmd));
        Assert.Equal("powershell.exe", ShellKinds.FileName(ShellKind.PowerShell));
        Assert.Equal("bash.exe", ShellKinds.FileName(ShellKind.Bash));
        Assert.Equal("pwsh when installed, else Windows PowerShell 5.1", ShellKinds.Describe("powershell"));
        Assert.Equal("cmd.exe: batch syntax", ShellKinds.Describe("cmd"));
        Assert.Equal("Git Bash, when bash.exe is found", ShellKinds.Describe("bash"));
        Assert.Equal("", ShellKinds.Describe("zsh"));
        Assert.Equal(ShellKind.Bash, ShellKinds.Resolve(new AppSettingsData { ShellDefault = "bash" }));
        Assert.Equal(ShellKind.PowerShell, ShellKinds.Resolve(new AppSettingsData { ShellDefault = "zsh" }));   // a hand-edited value: the default, with a warning
        Assert.Equal("Shell", ShellKinds.Category);

        Assert.Equal(["off", "ask", "yolo"], CommandPolicy.Names);
        Assert.Equal("off", CommandPolicy.Default);   // "ask" until 2026-09-29 (the user's call)
        Assert.True(CommandPolicy.TryParse(" Yolo ", out var yolo) && yolo == CommandPolicyMode.Yolo);
        Assert.True(CommandPolicy.TryParse("off", out var off) && off == CommandPolicyMode.Off);
        Assert.False(CommandPolicy.TryParse("maybe", out var unknown));
        Assert.Equal(CommandPolicyMode.Off, unknown);   // the default's, so the toolbar agrees with Resolve
        Assert.Equal("yolo", CommandPolicy.Name(CommandPolicyMode.Yolo));
        Assert.Equal("no shell or script tool is offered", CommandPolicy.Describe("off"));
        Assert.Equal("you approve each command not on the allow list", CommandPolicy.Describe("ask"));
        Assert.Equal("every command runs, nothing is asked", CommandPolicy.Describe("yolo"));
        Assert.Equal(CommandPolicyMode.Yolo, CommandPolicy.Resolve(new AppSettingsData { ShellCommandPolicy = "yolo" }));
        Assert.Equal(CommandPolicyMode.Off, CommandPolicy.Resolve(new AppSettingsData { ShellCommandPolicy = "whatever" }));
        Assert.False(App.ChatScreen.ShellOffered(new AppSettingsData()));   // off by default since 2026-09-29
        Assert.True(App.ChatScreen.ShellOffered(new AppSettingsData { ShellCommandPolicy = "ask" }));
        Assert.False(App.ChatScreen.ShellOffered(new AppSettingsData { ShellCommandPolicy = "off" }));
    }

    // ── The prefixes ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("git push origin main", "git push")]
    [InlineData("git -C x status", "git status")]   // an option and its value skipped on the way to the verb (2026-10-12)
    [InlineData("git -c alias.x=y push", CommandPrefix.Unreadable)]   // -c can set config that runs a program (2026-10-19): the line asks
    [InlineData("git --no-pager -C x log -5", "git log")]
    [InlineData("git --git-dir=.git log", "git log")]   // --name=value is one word
    [InlineData("git --version", "git")]
    [InlineData("git \"push\" origin", "git push")]   // a quoted verb is unquoted and read, never left as a bare git (2026-10-19)
    [InlineData("docker -H tcp://evil rm y", CommandPrefix.Unreadable)]   // -H aims docker at another daemon (2026-10-19)
    [InlineData("make -f /tmp/x test", CommandPrefix.Unreadable)]   // -f runs an arbitrary makefile
    [InlineData("kubectl --kubeconfig /tmp/k get pods", CommandPrefix.Unreadable)]
    // An option the lists don't know may take the next word as its value, so the verb can't be told (2026-10-19, the second review):
    [InlineData("dotnet --roll-forward test x.dll", CommandPrefix.Unreadable)]
    [InlineData("npm --registry test publish", CommandPrefix.Unreadable)]
    [InlineData("npm --script-shell=/tmp/x test", CommandPrefix.Unreadable)]
    [InlineData("git --attr-source test push", CommandPrefix.Unreadable)]
    [InlineData("go -C test build", CommandPrefix.Unreadable)]
    [InlineData("dotnet --info", "dotnet")]   // a last word is only an option
    [InlineData("npm -g install x", "npm install")]   // a known flag
    [InlineData("git --git-dir .git --no-pager log", "git log")]
    [InlineData("kubectl -n prod delete pod x", "kubectl delete")]
    [InlineData("docker --context remote rm -f x", CommandPrefix.Unreadable)]   // another daemon, as -H is (2026-10-20, the code review)
    [InlineData("docker -c remote ps", CommandPrefix.Unreadable)]
    [InlineData("docker --context=remote ps", CommandPrefix.Unreadable)]
    [InlineData("npm --prefix web run build", "npm run")]
    [InlineData("GIT STATUS", "git status")]
    [InlineData("dotnet build -c Release", "dotnet build")]
    [InlineData("python -c \"print(1)\"", "python")]
    // A program named by a path keeps its folder (later on 2026-10-19, the third shell review), so a model's own git.cmd is never git:
    [InlineData("\"C:\\Tools\\Foo.EXE\" -x", "c:/tools/foo")]
    [InlineData("./build.sh --fast", "./build")]
    [InlineData(".\\build.ps1", "./build")]
    [InlineData("C:\\Windows\\System32\\cmd.exe /c dir", "c:/windows/system32/cmd")]
    [InlineData(".\\git.cmd status", "./git status")]
    [InlineData("sub\\git.bat -C x push", "sub/git push")]   // the verb and options read by the file's name
    [InlineData("\"C:\\Program Files\\Git\\cmd\\git.exe\" -c x=y push", CommandPrefix.Unreadable)]
    [InlineData("Get-ChildItem -Recurse", "get-childitem")]
    [InlineData("npm run dev", "npm run")]
    [InlineData("net use Z: \\\\nas\\x", "net use")]   // net a verb program since 2026-10-03
    [InlineData("NET.EXE user bob /add", "net user")]
    [InlineData("   ", "")]
    public void CommandPrefix_Of_IsTheProgram_AndTheVerbForVerbPrograms(string command, string prefix)
    {
        Assert.Equal(prefix, CommandPrefix.Of(command));
    }

    /// <summary>
    /// What a leading <c>NAME=value</c> is, by shell (later on 2026-10-19, the third shell review): an assignment skipped only in bash and
    /// zsh; PowerShell's command name, cmd's up to the <c>=</c>. And a <c>set</c>/<c>export</c> of a variable that redirects what runs asks.
    /// </summary>
    [Theory]
    [InlineData("bash", "FOO=1 BAR=2 make test", "make test")]
    [InlineData("zsh", "FOO=1 git status", "git status")]
    [InlineData("bash", "PATH=/tmp/x make test", CommandPrefix.Unreadable)]   // a leading PATH assignment can redirect which make runs
    [InlineData("powershell", "FOO=1 git status", "foo=1")]
    [InlineData("cmd", "FOO=1 git status", CommandPrefix.Unreadable)]   // cmd runs foo; whether foo sees 1 is foo's (2026-10-20)
    [InlineData("cmd", "git,x status", CommandPrefix.Unreadable)]       // git's verb is status or x, by how git splits its line
    [InlineData("cmd", "git,status", CommandPrefix.Unreadable)]         // read as git until 2026-10-20: an allowed git covered it
    [InlineData("cmd", "git=push", CommandPrefix.Unreadable)]
    [InlineData("cmd", "set,PATH=C:\\x;%PATH%", CommandPrefix.Unreadable)]   // read as set: the PATH never reached SetsDangerous
    [InlineData("cmd", "git, status", "git status")]                    // a delimiter with nothing glued is a blank
    [InlineData("cmd", "set PATH=C:\\x;%PATH%", CommandPrefix.Unreadable)]
    [InlineData("cmd", "set NoDefaultCurrentDirectoryInExePath=", CommandPrefix.Unreadable)]
    [InlineData("cmd", "set \"path=C:\\x\"", CommandPrefix.Unreadable)]
    [InlineData("cmd", "set X=1", "set")]
    [InlineData("bash", "export PATH=.:$PATH", CommandPrefix.Unreadable)]
    [InlineData("bash", "export LANG=C", "export")]
    [InlineData("bash", "/usr/bin/git push", "/usr/bin/git push")]
    public void CommandPrefix_Of_ReadsAssignmentsAsTheShellDoes(string shell, string command, string prefix)
    {
        Assert.True(ShellKinds.TryParse(shell, out var kind));
        Assert.Equal(prefix, CommandPrefix.Of(command, shell: kind));
    }

    /// <summary>
    /// The programs that run another command, a string as code, or a script (2026-10-20, the code review): read through to the command they
    /// run, or the line asks; never the bare runner, which an allowed <c>env</c>, <c>xargs</c>, <c>eval</c>, <c>iex</c> or <c>source</c> once covered
    /// whatever came after it.
    /// </summary>
    [Theory]
    [InlineData("bash", "env FOO=1 git push", "git push")]
    [InlineData("bash", "env PATH=/tmp/x git status", CommandPrefix.Unreadable)]   // env's assignments judged as a leading one is
    [InlineData("bash", "env -u PATH git status", CommandPrefix.Unreadable)]
    [InlineData("bash", "env \"PATH=/tmp/x\" git status", CommandPrefix.Unreadable)]   // a quoted one asks
    [InlineData("bash", "env -i git status", "git status")]
    [InlineData("bash", "env -S 'rm -rf x'", CommandPrefix.Unreadable)]   // splits a string into the command
    [InlineData("bash", "env -C /tmp ./git status", CommandPrefix.Unreadable)]   // an option the grammar doesn't know
    [InlineData("bash", "env", "env")]   // alone, or with only assignments: lists the environment
    [InlineData("bash", "env FOO=1", "env")]
    [InlineData("zsh", "/usr/bin/env git status", "git status")]
    [InlineData("bash", "./env git status", "./env")]   // a model's own env is a program of its own
    [InlineData("bash", "./script.sh x", "./script")]   // so is its own script, whatever its name
    [InlineData("zsh", "/opt/homebrew/bin/gtimeout 30 rm -rf x", CommandPrefix.Unreadable)]   // read through only from the system's folders
    [InlineData("zsh", "/usr/local/bin/xargs rm", CommandPrefix.Unreadable)]
    [InlineData("zsh", "/usr/bin/xargs", "/usr/bin/xargs")]
    [InlineData("powershell", "C:\\Windows\\System32\\sudo.exe del x", CommandPrefix.Unreadable)]
    [InlineData("powershell", "Microsoft.PowerShell.Management\\Start-Process notepad", CommandPrefix.Unreadable)]
    [InlineData("bash", "nohup git push", "git push")]
    [InlineData("bash", "nohup env PATH=/tmp/x git push", CommandPrefix.Unreadable)]
    [InlineData("bash", "nice -n 5 make test", "make test")]
    [InlineData("bash", "nice -10 make test", "make test")]
    [InlineData("bash", "nice -n 5", "nice")]
    [InlineData("bash", "timeout 30 git fetch", "git fetch")]
    [InlineData("bash", "timeout -s KILL --kill-after=5 30 rm -rf x", "rm")]
    [InlineData("bash", "timeout -x 30 rm x", CommandPrefix.Unreadable)]
    [InlineData("bash", "stdbuf -oL tail -f x", "tail")]
    [InlineData("bash", "setsid -f sleep 10", "sleep")]
    [InlineData("zsh", "caffeinate -dims make test", "make test")]
    [InlineData("zsh", "/usr/bin/time -l git log", "git log")]
    [InlineData("zsh", "time -p git log", "git log")]   // bash's keyword, as before
    [InlineData("bash", "sudo rm -rf x", CommandPrefix.Unreadable)]
    [InlineData("bash", "sudo", "sudo")]
    [InlineData("bash", "xargs rm", CommandPrefix.Unreadable)]
    [InlineData("bash", "env sudo rm x", CommandPrefix.Unreadable)]
    [InlineData("zsh", "doas rm x", CommandPrefix.Unreadable)]
    [InlineData("bash", "eval \"rm -rf x\"", CommandPrefix.Unreadable)]
    [InlineData("zsh", "builtin eval $cmd", CommandPrefix.Unreadable)]
    [InlineData("bash", "eval", "eval")]
    [InlineData("bash", "trap 'rm -rf x' EXIT", CommandPrefix.Unreadable)]
    [InlineData("bash", "trap -p", "trap")]
    [InlineData("zsh", "alias git='rm -rf'", CommandPrefix.Unreadable)]
    [InlineData("zsh", "alias", "alias")]
    [InlineData("bash", "find . -name '*.cs'", "find")]
    [InlineData("bash", "find . -exec rm {} \\;", CommandPrefix.Unreadable)]
    [InlineData("bash", "find / -delete", CommandPrefix.Unreadable)]
    [InlineData("bash", "rg --pre ./x foo", CommandPrefix.Unreadable)]
    [InlineData("bash", "source ./a.sh", "source ./a")]   // each script approved apart
    [InlineData("zsh", ". ./b.sh x", ". ./b")]
    [InlineData("bash", "source $x", CommandPrefix.Unreadable)]
    [InlineData("bash", "source", "source")]
    [InlineData("powershell", ". .\\a.ps1", ". ./a")]
    [InlineData("powershell", "iex $x", CommandPrefix.Unreadable)]
    [InlineData("powershell", "Invoke-Expression 'Remove-Item x'", CommandPrefix.Unreadable)]
    [InlineData("powershell", "Microsoft.PowerShell.Utility\\Invoke-Expression $s", CommandPrefix.Unreadable)]
    [InlineData("powershell", "iex", CommandPrefix.Unreadable)]   // alone, at a pipeline's end, it runs its input
    [InlineData("powershell", "Start-Process notepad", CommandPrefix.Unreadable)]
    [InlineData("powershell", "Set-Alias git Remove-Item", CommandPrefix.Unreadable)]
    [InlineData("powershell", "sudo git push", CommandPrefix.Unreadable)]
    [InlineData("powershell", "env FOO=1 git push", CommandPrefix.Unreadable)]
    [InlineData("powershell", "timeout /t 5", "timeout")]   // Windows' wait
    [InlineData("cmd", "call a.bat", "a")]   // as a.bat itself is
    [InlineData("cmd", "call git push", "git push")]
    [InlineData("cmd", "call %x%", CommandPrefix.Unreadable)]
    [InlineData("cmd", "call set PATH=C:\\x", CommandPrefix.Unreadable)]
    [InlineData("cmd", "start evil.exe", CommandPrefix.Unreadable)]
    [InlineData("cmd", "timeout /t 5 /nobreak", "timeout")]
    [InlineData("cmd", "sudo del x", CommandPrefix.Unreadable)]
    [InlineData("cmd", "git;push", CommandPrefix.Unreadable)]   // cmd ends a name at ; too (2026-10-20)
    [InlineData("cmd", "git; status", "git status")]
    public void CommandPrefix_Of_ReadsThroughWhatRunsAnotherCommand(string shell, string command, string prefix)
    {
        Assert.True(ShellKinds.TryParse(shell, out var kind));
        Assert.Equal(prefix, CommandPrefix.Of(command, shell: kind));
    }

    /// <summary>
    /// The options that run a command (later on 2026-10-20, the code review): a line carrying one asks, in any spelling the program takes —
    /// quoted, escaped, glued to its value, in a bundle, a long one abbreviated, tar's dashless first word, an ssh <c>-o</c> past the safe keys —
    /// as does a value or an unquoted glob among such a program's words; the rest reads as before.
    /// </summary>
    [Theory]
    [InlineData("bash", "tar -xf a.tar --to-command='sh -c x'", CommandPrefix.Unreadable)]
    [InlineData("bash", "tar --use-compress=zstd -xf a.tar", CommandPrefix.Unreadable)]   // GNU's abbreviation
    [InlineData("bash", "tar -xIzstd -f a.tar", CommandPrefix.Unreadable)]
    [InlineData("bash", "tar -cvIzstd -f a.tar x", CommandPrefix.Unreadable)]
    [InlineData("bash", "tar xIf zstd a.tar", CommandPrefix.Unreadable)]   // the dashless bundle
    [InlineData("bash", "tar -cf a.tar --checkpoint=1 --checkpoint-action=exec=sh\\ x.sh x", CommandPrefix.Unreadable)]
    [InlineData("bash", "tar -F ./next.sh -cf a.tar x", CommandPrefix.Unreadable)]
    [InlineData("bash", "tar cf a.tar *", CommandPrefix.Unreadable)]   // a file named --checkpoint-action=… becomes an option
    [InlineData("bash", "tar -xf $archive", CommandPrefix.Unreadable)]
    [InlineData("bash", "tar -xzf a.tgz -C out", "tar")]
    [InlineData("bash", "tar xzf a.tgz", "tar")]
    [InlineData("bash", "tar -xfI.tar", "tar")]   // -f's value
    [InlineData("powershell", "tar.exe -xf a.zip --to-command=calc", CommandPrefix.Unreadable)]
    [InlineData("bash", "rsync -avze 'sh -c x' a host:b", CommandPrefix.Unreadable)]
    [InlineData("bash", "rsync -av -essh a host:b", CommandPrefix.Unreadable)]
    [InlineData("bash", "rsync --rsync-path='sh -c x' a host:b", CommandPrefix.Unreadable)]
    [InlineData("bash", "rsync -avz --exclude '*.o' a/ host:b/", "rsync")]
    [InlineData("bash", "ssh -o ProxyCommand='sh -c x' host", CommandPrefix.Unreadable)]
    [InlineData("bash", "ssh -oProxyCommand=x host", CommandPrefix.Unreadable)]
    [InlineData("bash", "ssh -vo 'LocalCommand x' -o PermitLocalCommand=yes host", CommandPrefix.Unreadable)]
    [InlineData("bash", "ssh -o proxycommand=x host", CommandPrefix.Unreadable)]   // any case
    [InlineData("bash", "ssh -o PKCS11Provider=./x.dylib host", CommandPrefix.Unreadable)]
    [InlineData("bash", "ssh -F ./config host", CommandPrefix.Unreadable)]
    [InlineData("bash", "ssh -o StrictHostKeyChecking=no -o ConnectTimeout=5 -p 22 host uptime", "ssh")]
    [InlineData("bash", "ssh -io host", "ssh")]   // -i's value
    [InlineData("bash", "scp -S ./prog a host:b", CommandPrefix.Unreadable)]
    [InlineData("bash", "scp -rS ./prog a host:b", CommandPrefix.Unreadable)]
    [InlineData("bash", "scp -o 'ProxyCommand x' a host:b", CommandPrefix.Unreadable)]
    [InlineData("bash", "sftp -b batch.txt host", CommandPrefix.Unreadable)]
    [InlineData("bash", "zip -T -TT 'sh -c x' a.zip x", CommandPrefix.Unreadable)]
    [InlineData("bash", "zip --unzip-command=x a.zip x", CommandPrefix.Unreadable)]
    [InlineData("bash", "zip -r a.zip src", "zip")]
    [InlineData("bash", "less '+!rm -rf x' a.txt", CommandPrefix.Unreadable)]
    [InlineData("bash", "less a.txt", "less")]
    [InlineData("bash", "vim -c '!rm -rf x' a.txt", CommandPrefix.Unreadable)]
    [InlineData("bash", "vi +'!rm x' a.txt", CommandPrefix.Unreadable)]
    [InlineData("bash", "nvim --cmd 'lua os.execute(1)' a", CommandPrefix.Unreadable)]
    [InlineData("bash", "vim -es a.txt", CommandPrefix.Unreadable)]
    [InlineData("bash", "vim -R a.txt", "vim")]
    [InlineData("bash", "man -P 'sh -c x' ls", CommandPrefix.Unreadable)]
    [InlineData("bash", "go test -exec ./x ./...", CommandPrefix.Unreadable)]
    [InlineData("bash", "go build -toolexec=./x ./...", CommandPrefix.Unreadable)]
    [InlineData("bash", "go test ./...", "go test")]
    [InlineData("bash", "npm test --script-shell=./x", CommandPrefix.Unreadable)]
    [InlineData("bash", "make test -f ./evil.mk", CommandPrefix.Unreadable)]   // after the verb too
    [InlineData("bash", "make --eval='x:;rm -rf y'", CommandPrefix.Unreadable)]
    [InlineData("bash", "make test -j4Cx", "make test")]   // -j's value
    [InlineData("bash", "find . \"-exec\" rm {} \\;", CommandPrefix.Unreadable)]   // quoted
    [InlineData("bash", "find . \\-exec rm {} \\;", CommandPrefix.Unreadable)]   // escaped
    [InlineData("bash", "find . -name \"*.cs\" -type f", "find")]   // a quoted glob is text
    [InlineData("bash", "find . -name *.cs", CommandPrefix.Unreadable)]
    [InlineData("bash", "fd -Hx rm", CommandPrefix.Unreadable)]
    [InlineData("bash", "git rebase -x 'rm -rf y' main", CommandPrefix.Unreadable)]
    [InlineData("bash", "git rebase --exe=x main", CommandPrefix.Unreadable)]   // git's abbreviation
    [InlineData("bash", "git rebase -i main", "git rebase")]
    [InlineData("bash", "git fetch --upload-pack='sh -c x' origin", CommandPrefix.Unreadable)]
    [InlineData("bash", "git clone -c core.sshCommand=x url", CommandPrefix.Unreadable)]
    [InlineData("bash", "git clone --template=./t url", CommandPrefix.Unreadable)]
    [InlineData("bash", "git push --receive-pack=x origin", CommandPrefix.Unreadable)]
    [InlineData("bash", "git push -u origin main", "git push")]   // push's -u is its upstream
    [InlineData("bash", "git submodule foreach 'rm -rf x'", CommandPrefix.Unreadable)]
    [InlineData("bash", "git submodule update --init", "git submodule")]
    [InlineData("bash", "git bisect run ./x.sh", CommandPrefix.Unreadable)]
    [InlineData("bash", "git difftool -x ./x HEAD", CommandPrefix.Unreadable)]
    [InlineData("bash", "git grep -O./x foo", CommandPrefix.Unreadable)]
    [InlineData("bash", "git -c core.pager=x log", CommandPrefix.Unreadable)]   // before the verb, as before
    [InlineData("bash", "git --config-env=core.pager=X log", CommandPrefix.Unreadable)]
    [InlineData("bash", "GIT_SSH_COMMAND=x git fetch", CommandPrefix.Unreadable)]
    [InlineData("bash", "TAR_OPTIONS=--to-command=sh tar xf a.tar", CommandPrefix.Unreadable)]
    [InlineData("bash", "RSYNC_RSH=x rsync a host:b", CommandPrefix.Unreadable)]
    [InlineData("bash", "env VIMINIT=x vim a", CommandPrefix.Unreadable)]
    [InlineData("bash", "kubectl get pods --kubeconfig ./k", CommandPrefix.Unreadable)]   // its exec plugins
    [InlineData("bash", "kubectl get pods -o json", "kubectl get")]
    [InlineData("bash", "tar -xf a.tar {--to-command=sh,}", CommandPrefix.Unreadable)]   // a brace expansion's words
    [InlineData("zsh", "rsync -av {-e,'sh -c x'} a host:b", CommandPrefix.Unreadable)]
    [InlineData("bash", "tar -xf a.tar x{1..3}", CommandPrefix.Unreadable)]
    [InlineData("bash", "git rebase @{u}", "git rebase")]   // no , or ..: text
    [InlineData("powershell", "tar -xf a.tar @a", CommandPrefix.Unreadable)]   // a splat's items are tar's words
    public void CommandPrefix_Of_AsksForTheOptionsThatRunACommand(string shell, string command, string prefix)
    {
        Assert.True(ShellKinds.TryParse(shell, out var kind));
        Assert.Equal(prefix, CommandPrefix.Of(command, shell: kind));
    }

    /// <summary>
    /// PowerShell's jobs and remote calls (later on 2026-10-20, the code review): the script file is part of the prefix, as a sourced one is; a
    /// script block, a value path or words whose binding can't be told ask.
    /// </summary>
    [Theory]
    [InlineData("Start-Job -FilePath .\\a.ps1", "start-job ./a")]
    [InlineData("sajb -FilePath .\\a.ps1", "sajb ./a")]
    [InlineData("Start-Job -FilePath:.\\a.ps1", "start-job ./a")]
    [InlineData("Start-Job -fi .\\a.ps1 -Name x", "start-job ./a")]   // a start of the name, another parameter after
    [InlineData("Start-Job -LiteralPath .\\b.ps1", "start-job ./b")]
    [InlineData("Start-Job -PSPath .\\b.ps1", "start-job ./b")]
    [InlineData("Start-Job .\\a.ps1", "start-job ./a")]   // by position
    [InlineData("Start-Job -FilePath \"C:\\x y\\a.ps1\"", "start-job c:/x y/a")]
    [InlineData("Start-ThreadJob -FilePath .\\a.ps1", "start-threadjob ./a")]
    [InlineData("Invoke-Command -FilePath .\\a.ps1 -ComputerName srv", "invoke-command ./a")]
    [InlineData("icm -FilePath .\\a.ps1", "icm ./a")]
    [InlineData("Microsoft.PowerShell.Core\\Start-Job -FilePath .\\a.ps1", "microsoft.powershell.core/start-job ./a")]
    [InlineData("Start-Job", "start-job")]
    [InlineData("Start-Job -FilePath $p", CommandPrefix.Unreadable)]
    [InlineData("Start-Job -FilePath .\\a.ps1 -FilePath .\\b.ps1", CommandPrefix.Unreadable)]
    [InlineData("Start-Job -ScriptBlock { Remove-Item x }", CommandPrefix.Unreadable)]
    [InlineData("Start-Job -s $sb", CommandPrefix.Unreadable)]   // a start of -ScriptBlock (or another): asks
    [InlineData("Start-Job { Remove-Item x }", CommandPrefix.Unreadable)]
    [InlineData("Start-Job $sb", CommandPrefix.Unreadable)]
    [InlineData("Start-Job -Name n .\\a.ps1", CommandPrefix.Unreadable)]
    [InlineData("Start-Job -FilePath .\\a.ps1 @p", CommandPrefix.Unreadable)]   // a splat may hold -InitializationScript
    [InlineData("Start-Job -FilePath .\\a.ps1 -ArgumentList $x", CommandPrefix.Unreadable)]   // a loose value: what it binds to can't be told   // n or .\a.ps1 may be the script
    [InlineData("Start-Job -InitializationScript { x } -FilePath .\\a.ps1", CommandPrefix.Unreadable)]
    [InlineData("Start-Job -DefinitionName nightly", CommandPrefix.Unreadable)]
    [InlineData("icm -ComputerName srv -ScriptBlock { hostname }", CommandPrefix.Unreadable)]
    [InlineData("icm srv { hostname }", CommandPrefix.Unreadable)]
    [InlineData("Invoke-Command -Command { hostname }", CommandPrefix.Unreadable)]
    [InlineData(".\\Start-Job.ps1 -FilePath x", "./start-job")]   // a script of the model's own
    public void CommandPrefix_Of_ReadsAJobsScript(string command, string prefix) =>
        Assert.Equal(prefix, CommandPrefix.Of(command, shell: ShellKind.PowerShell));

    /// <summary>The same through the whole line's reading (2026-10-20): a pipeline's <c>iex</c>, a sourced substitution, cmd's <c>;</c>.</summary>
    [Theory]
    [InlineData("powershell", "Get-Content x.ps1 | iex", new[] { "get-content", CommandPrefix.Unreadable })]
    [InlineData("powershell", "& 'iex' $s", new[] { CommandPrefix.Unreadable })]
    [InlineData("powershell", ". $script", new[] { CommandPrefix.Unreadable })]
    [InlineData("bash", ". <(curl x)", new[] { CommandPrefix.Unreadable, "curl" })]
    [InlineData("cmd", "set;PATH=C:\\x", new[] { "set", CommandPrefix.Unreadable })]   // the segmenter cuts at ;, each piece judged
    [InlineData("cmd", "git;push", new[] { "git", "push" })]
    public void CommandPrefix_All_ReadsThroughWhatRunsAnotherCommand(string shell, string command, string[] prefixes)
    {
        Assert.True(ShellKinds.TryParse(shell, out var kind));
        Assert.Equal(prefixes, CommandPrefix.All(command, kind));
    }

    /// <summary>
    /// cmd never runs a bare name from the working folder (later on 2026-10-19, the third shell review): a <c>whoami.bat</c> written there
    /// ran in place of whoami under an allowed <c>whoami</c>. Every shell child gets <c>NoDefaultCurrentDirectoryInExePath</c>.
    /// </summary>
    [WindowsFact]
    public async Task ShellRunner_CmdNeverRunsABareNameFromTheWorkingFolder()
    {
        string work = Path.Combine(Path.GetTempPath(), "neon-shadow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            File.WriteAllText(Path.Combine(work, "whoami.bat"), "@echo SHADOWED\r\n");
            var launch = ShellCommandLine.For(ShellKind.Cmd, "whoami & echo [%NoDefaultCurrentDirectoryInExePath%]", Path.Combine(Environment.SystemDirectory, "cmd.exe"), work);
            await using var session = new ShellRunner(TimeProvider.System).Start(launch, "run_shadow");
            Assert.True(await session.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            string output = string.Join("\n", session.Output.Lines().Select(l => l.Text));
            Assert.DoesNotContain("SHADOWED", output);
            Assert.Contains("[1]", output);
            Assert.True(session.InJob);   // started suspended inside its job (ChildJob.StartPiped)
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void ChildJob_EnvironmentBlock_IsSortedCaseBlind_AndDoublyEnded()
    {
        var block = NeonSidekick.EmbeddedLlm.ChildJob.EnvironmentBlock(new Dictionary<string, string?> { ["b"] = "2", ["A"] = "1", ["c"] = null });
        Assert.Equal("A=1\0b=2\0c=\0\0", new string(block));
        Assert.Equal("\0\0", new string(NeonSidekick.EmbeddedLlm.ChildJob.EnvironmentBlock(new Dictionary<string, string?>())));
        Assert.Equal("\"C:\\x y\\cmd.exe\" /d /c dir", ShellRunner.WindowsCommandLine(new ProcessLaunch("C:\\x y\\cmd.exe", null, "/d /c dir", ".", "dir", "cmd")));
        Assert.Equal("\"C:\\p.exe\" a \"b c\"", ShellRunner.WindowsCommandLine(new ProcessLaunch("C:\\p.exe", ["a", "b c"], null, ".", "x", "p")));
    }

    [Fact]
    public void CommandPrefix_All_SplitsOnSeparatorsOutsideQuotes_Distinct_InOrder()
    {
        Assert.Equal(["dotnet build", "rm"], CommandPrefix.All("dotnet build && rm -rf x"));
        Assert.Equal(["git status", "findstr"], CommandPrefix.All("git status | findstr M"));
        Assert.Equal(["echo", "dir"], CommandPrefix.All("echo a; dir\r\ndir /b"));
        Assert.Equal(["echo"], CommandPrefix.All("echo \"a; b && c\""));
        Assert.Equal(["echo"], CommandPrefix.All("echo 'x | y'"));
        Assert.Equal(["ping"], CommandPrefix.All("ping -n 3 127.0.0.1 >nul & exit /b 0").Take(1));
        Assert.Equal(["ping", "/b"], CommandPrefix.All("ping -n 3 127.0.0.1 >nul & exit /b 0"));   // PowerShell runs exit's argument (measured)
        Assert.Empty(CommandPrefix.All(""));
        Assert.Equal(["a", "b"], CommandPrefix.Segments("a || b"));
        Assert.Equal(["\"C:\\x.exe\"", "y"], CommandPrefix.Segments("& \"C:\\x.exe\" & y"));
        Assert.Equal(
            [("a", "&&"), ("b", "||"), ("c", "|"), ("d", "&"), ("e", ";"), ("f", "\n"), ("g 'h && i'", "")],
            CommandPrefix.JoinedSegments("a && b || c | d & e; f\r\ng 'h && i'"));
        Assert.Equal(["c:/x y/tool"], CommandPrefix.All("& 'C:\\x y\\tool.exe' run"));   // PowerShell's call operator: an empty segment, dropped
        Assert.Contains("git", CommandPrefix.VerbPrograms);
        Assert.DoesNotContain("python", CommandPrefix.VerbPrograms);
        Assert.DoesNotContain("powershell", CommandPrefix.VerbPrograms);
        Assert.Equal("foo", CommandPrefix.Program("Foo.exe"));
        Assert.Equal("foo.py", CommandPrefix.Program("foo.py"));   // not an executable extension
    }

    /// <summary>
    /// Substitutions (2026-10-12, the shell review): the commands they run are prefixes of the line too, so an allowed prefix never
    /// carries an unasked command inside it; what cannot be read is <see cref="CommandPrefix.Unreadable"/>.
    /// </summary>
    [Theory]
    [InlineData("powershell", "git log $(Remove-Item -Recurse src)", new[] { "git log", "remove-item" })]
    [InlineData("powershell", "git log \"$(Remove-Item x)\"", new[] { "git log", "remove-item" })]
    [InlineData("powershell", "git log '$(Remove-Item x)'", new[] { "git log" })]   // single quotes: text
    [InlineData("powershell", "git log `$(Remove-Item x)", new[] { "git log", "remove-item" })]   // the $ is text, the (…) after it still runs (checked in 5.1)
    [InlineData("powershell", "git log '$(Remove-Item x)' \"`$(x)\"", new[] { "git log" })]   // in double quotes the escaped $( is text
    [InlineData("powershell", "Write-Output @(Remove-Item x)", new[] { "write-output", "remove-item" })]
    [InlineData("powershell", "Write-Output (Get-Date).Year", new[] { "write-output", "get-date" })]
    [InlineData("powershell", "Get-ChildItem | ForEach-Object { Remove-Item $_ }", new[] { "get-childitem", "foreach-object", "remove-item" })]
    [InlineData("powershell", "Get-ChildItem | Where-Object { $_.Length -gt 1 } | Sort-Object Name", new[] { "get-childitem", "where-object", "sort-object" })]
    [InlineData("powershell", "Write-Output \"n: $($items.Count)\" @{ a = 1 } ${env:X}", new[] { "write-output" })]
    [InlineData("powershell", "Write-Output { $x = Remove-Item y }", new[] { "write-output", "remove-item" })]
    [InlineData("powershell", "[IO.File]::ReadAllText('x')", new[] { "[io.file]::readalltext" })]
    [InlineData("powershell", "Write-Output $(Write-Output $(Remove-Item x))", new[] { "write-output", "remove-item" })]
    [InlineData("powershell", "git log $(Remove-Item x; Stop-Process -Id 1)", new[] { "git log", "remove-item", "stop-process" })]   // the ; inside is the body's
    [InlineData("powershell", "Get-Item x # (an unclosed note", new[] { "get-item" })]   // a comment
    [InlineData("powershell", "git log $(Remove-Item x", new[] { "git log", CommandPrefix.Unreadable })]   // left open
    [InlineData("powershell", "& $tool -x", new[] { "$tool", CommandPrefix.Unreadable })]   // the call operator on a value
    [InlineData("powershell", "git $(Write-Output push)", new[] { CommandPrefix.Unreadable, "write-output" })]   // a verb from a substitution
    [InlineData("bash", "git log `rm -rf src`", new[] { "git log", "rm" })]
    [InlineData("bash", "git log \"$(rm -rf src)\"", new[] { "git log", "rm" })]
    [InlineData("bash", "diff <(sort a) >(tee b)", new[] { "diff", "sort", "tee" })]
    [InlineData("bash", "echo $((1 + 2)) ${HOME}", new[] { "echo" })]   // arithmetic and a variable run nothing
    [InlineData("bash", "x=$(cat f) make test", new[] { "make test", "cat" })]
    [InlineData("bash", "$(echo rm) -rf x", new[] { CommandPrefix.Unreadable, "echo" })]   // the command is the output
    [InlineData("bash", @"echo \$(rm x)", new[] { "echo" })]   // escaped
    [InlineData("zsh", "echo $(echo $(rm x))", new[] { "echo", "rm" })]
    [InlineData("cmd", "echo $(rm x)", new[] { "echo" })]   // cmd has no substitutions
    [InlineData("zsh", "diff =(sort a) b", new[] { "diff", "sort" })]   // zsh's =(…): a temp file of the output
    [InlineData("zsh", "git log =(rm -rf src)", new[] { "git log", "rm" })]
    [InlineData("zsh", "cat < =(rm x) && cat <=(rm y)", new[] { "cat", "rm" })]   // after a redirection
    [InlineData("zsh", "x==(rm y) make test", new[] { "make test", "rm" })]   // an assignment's value
    [InlineData("zsh", "x+==(rm y) make test", new[] { "make test", "rm" })]   // an append is an assignment too (the fourth review); a subscript (not one
    [InlineData("zsh", "a[1]==(rm y) make test", new[] { CommandPrefix.Unreadable, "rm" })]   // IsAssignment knows, so the pane always asks)
    [InlineData("zsh", "export y==(rm z)", new[] { "export", "rm" })]
    [InlineData("zsh", "echo '=(rm x)' \"=(rm y)\" \\=(rm z)", new[] { "echo" })]   // quoted or escaped: text
    [InlineData("zsh", "echo x=(rm y)", new[] { "echo" })]   // inside an argument: a glob qualifier
    [InlineData("zsh", "git log ${:-=(rm -rf src)}", new[] { "git log", "rm" })]   // a parameter expansion's default runs it (the code review)
    [InlineData("zsh", "echo ${x:+=(rm y)} x${x-=(rm z)}", new[] { "echo", "rm" })]
    [InlineData("zsh", "echo \"${x:-=(rm y)}\"", new[] { "echo" })]   // in double quotes: text
    [InlineData("zsh", "echo --opt==(rm z)", new[] { "echo", "rm" })]   // a glob qualifier too, but read: reading more only asks
    [InlineData("zsh", "files=(rm x) && ls", new[] { "x)", "ls" })]   // an array, not a command: read as bash reads it
    [InlineData("bash", "files=(rm x) && ls", new[] { "x)", "ls" })]
    [InlineData("bash", "diff =(sort a) b", new[] { "diff" })]   // bash has no =(…)
    // An escaped quote no longer desyncs the segmenter and hides a separator (2026-10-19, the code review):
    [InlineData("bash", "echo \\\" ; rm x", new[] { "echo", "rm" })]
    [InlineData("powershell", "Write-Output `\" ; Remove-Item x", new[] { "write-output", "remove-item" })]
    // bash/zsh $'…': the \' does not close it, so the $(…) after it is still read:
    [InlineData("bash", "echo $'a\\'b' $(rm x)", new[] { "echo", "rm" })]
    // a # after an escaped blank is part of a word, not a comment, so the $(…) in it is read:
    [InlineData("bash", "echo \\ #$(rm x)", new[] { "echo", "rm" })]
    // a segment of only assignments can set PATH/LD_* for a later command, so it asks:
    [InlineData("bash", "x=$(rm y)", new[] { CommandPrefix.Unreadable, "rm" })]
    // a method call on a $ value runs code, unlike a property read:
    [InlineData("powershell", "Write-Output $($x.Delete())", new[] { "write-output", CommandPrefix.Unreadable })]
    // expansions that evaluate a variable's content as code ask:
    [InlineData("bash", "echo ${x@P}", new[] { "echo", CommandPrefix.Unreadable })]
    [InlineData("zsh", "echo ${(e)x}", new[] { "echo", CommandPrefix.Unreadable })]
    // A comment's quotes are text to the segmenter too, so an apostrophe in one no longer hides the lines after it (the second review):
    [InlineData("powershell", "git log # it's\nRemove-Item x\n# it's", new[] { "git log", "remove-item" })]
    [InlineData("bash", "git log # it's\nrm -rf x\n# it's", new[] { "git log", "rm" })]
    [InlineData("bash", "git log;# it's\nrm -rf x\n#'", new[] { "git log", "rm" })]   // a # after a separator opens one too
    [InlineData("powershell", "git log;# it's\nRemove-Item x\n#'", new[] { "git log", "remove-item" })]
    [InlineData("powershell", "Get-Item x <# it's #> ; Remove-Item y", new[] { "get-item", "remove-item" })]
    [InlineData("powershell", "Write-Output a<#x; Remove-Item y #>b", new[] { "write-output", "remove-item" })]   // mid-word: no comment (measured)
    [InlineData("bash", "echo \"a\"#it's; rm x; '", new[] { "echo" })]   // mid-word: no comment, so the ' is a quote (as bash reads it)
    [InlineData("powershell", "Write-Output \"a\"#it's\nRemove-Item x\n#'", new[] { "write-output", CommandPrefix.Unreadable })]   // in doubt: asks
    [InlineData("bash", "echo $(echo a)#it's\nrm x\n#'", new[] { "echo", CommandPrefix.Unreadable })]
    // PowerShell's typographic quotes, cmd's lack of single ones:
    [InlineData("powershell", "git log 'a’; Remove-Item x; '", new[] { "git log", "remove-item" })]
    [InlineData("powershell", "git log “a\"; Remove-Item x", new[] { "git log", "remove-item" })]
    [InlineData("bash", "git log 'a’; rm x; '", new[] { "git log" })]   // text to bash
    [InlineData("cmd", "git status ' & del x & '", new[] { "git status", "del" })]
    // A statement that opens with a value and calls a method on it runs code:
    [InlineData("powershell", "(Get-Item src).Delete()", new[] { CommandPrefix.Unreadable, "get-item" })]
    [InlineData("powershell", "$(Get-Item src).Delete()", new[] { CommandPrefix.Unreadable, "get-item" })]
    [InlineData("powershell", "@(Get-Item src).'Delete'()", new[] { CommandPrefix.Unreadable, "get-item" })]
    [InlineData("powershell", "(Get-Date).Year", new[] { "get-date" })]   // a property read is a value
    public void CommandPrefix_All_ReadsTheCommandsSubstitutionsRun(string shell, string command, string[] prefixes)
    {
        Assert.True(ShellKinds.TryParse(shell, out var kind));
        Assert.Equal(prefixes, CommandPrefix.All(command, kind));
    }

    /// <summary>A here-string's or a here-document's quotes close only at a line of their own, which no scan follows: the line asks (2026-10-19).</summary>
    [Theory]
    [InlineData("powershell", "git log @'\nit's\n'@; Remove-Item x")]
    [InlineData("powershell", "git log @\"  \r\nit's\n\"@; Remove-Item x")]
    [InlineData("bash", "git log <<EOF\nit's\nEOF\nrm -rf x")]
    [InlineData("zsh", "git log <<-'EOF'\nit's\nEOF\nrm -rf x")]
    public void CommandPrefix_HereStrings_AreUnreadable(string shell, string command)
    {
        Assert.True(ShellKinds.TryParse(shell, out var kind));
        Assert.Contains(CommandPrefix.Unreadable, CommandPrefix.All(command, kind));
        Assert.DoesNotContain(CommandPrefix.Unreadable, CommandPrefix.All("git log <<< 'x'", ShellKind.Bash));   // <<< is a plain string
        Assert.DoesNotContain(CommandPrefix.Unreadable, CommandPrefix.All("git log @'x'@", ShellKind.PowerShell));
    }

    [Fact]
    public void CommandPrefix_NestingPastTheCap_IsUnreadable()
    {
        string deep = string.Concat(Enumerable.Repeat("$(echo ", CommandPrefix.MaxDepth + 2)) + "x" + new string(')', CommandPrefix.MaxDepth + 2);
        Assert.Contains(CommandPrefix.Unreadable, CommandPrefix.All(deep, ShellKind.Bash));
        Assert.Equal("(unreadable substitution)", CommandPrefix.Unreadable);
    }

    [Fact]
    public void CommandAllowList_NeverHolds_TheUnreadablePrefix()
    {
        var saved = new List<string> { CommandPrefix.Unreadable };
        var list = new CommandAllowList(() => saved, merged => saved = [.. merged]);
        Assert.False(list.IsAllowed(CommandPrefix.Unreadable));   // a hand-edited entry counts for nothing
        list.AllowPermanently(["git log", CommandPrefix.Unreadable]);
        Assert.Equal(["git log"], saved);
        Assert.Equal(["git log"], list.SessionSnapshot());
        Assert.False(list.AllowsAll(CommandPrefix.All("git log $(Remove-Item x", ShellKind.PowerShell)));
        Assert.True(list.AllowsAll(CommandPrefix.All("git log $(git log -1)", ShellKind.PowerShell)));
    }

    // ── The allow list ───────────────────────────────────────────────────────

    [Fact]
    public void CommandAllowList_SessionAndPermanent_MergeSortedAndLowerCase()
    {
        var saved = new List<string> { "Git Push", " dotnet build " };
        var persisted = new List<IReadOnlyList<string>>();
        var list = new CommandAllowList(() => saved, persisted.Add);

        Assert.True(list.IsAllowed("git push"));
        Assert.True(list.IsAllowed("dotnet build"));
        Assert.False(list.IsAllowed("rm"));
        Assert.True(list.AllowsAll(["git push", "dotnet build"]));
        Assert.False(list.AllowsAll(["git push", "rm"]));
        Assert.True(list.AllowsAll([]));

        list.AllowSession(["RM", "git push"]);
        Assert.True(list.IsAllowed("rm"));
        Assert.Equal(["git push", "rm"], list.SessionSnapshot());
        Assert.Empty(persisted);

        list.AllowPermanently(["python"]);
        Assert.True(list.IsAllowed("python"));
        Assert.Equal(["dotnet build", "git push", "python"], Assert.Single(persisted));
        saved = [.. Assert.Single(persisted)];
        Assert.Equal(["dotnet build", "git push", "python", "rm"], list.Snapshot());

        Assert.Equal(["a", "b"], CommandAllowList.Merge(["b", "B", " "], ["a"]));
        Assert.Equal(["a"], CommandAllowList.Without(["a", "B"], "b"));
        Assert.True(CommandAllowList.Contains(["Git Push"], "git push"));
        Assert.False(CommandAllowList.Contains([], "git push"));
    }

    // ── The gate ─────────────────────────────────────────────────────────────

    private static CommandRequest Request(string command = "git push origin", string kind = "powershell") => new(kind, command, CommandPrefix.All(command));

    [Fact]
    public async Task CommandGate_Yolo_RunsEverything_Off_RefusesEverything()
    {
        var settings = new AppSettingsData { ShellCommandPolicy = "yolo" };
        var list = new CommandAllowList(() => [], _ => { });
        int asked = 0;
        var gate = new CommandGate(() => settings, list, (_, _) => { asked++; return Task.FromResult<CommandChoice?>(CommandChoice.Deny); });

        Assert.True((await gate.JudgeAsync(Request(), CancellationToken.None)).Allowed);
        Assert.Equal(0, asked);
        Assert.Equal(CommandPolicyMode.Yolo, gate.Policy);
        Assert.True(gate.CanAsk);

        settings.ShellCommandPolicy = "off";
        var verdict = await gate.JudgeAsync(Request(), CancellationToken.None);
        Assert.False(verdict.Allowed);
        Assert.Equal("Error: Shell command policy is off: no command runs", verdict.Error);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task CommandGate_Ask_RunsTheAllowed_AsksTheRest_AndRecordsThePick()
    {
        var settings = new AppSettingsData { ShellCommandPolicy = "ask" };   // off by default since 2026-09-29
        var saved = new List<string>();
        var list = new CommandAllowList(() => saved, merged => saved = [.. merged]);
        var answers = new Queue<CommandChoice?>();
        var seen = new List<CommandRequest>();
        var gate = new CommandGate(() => settings, list, (request, _) => { seen.Add(request); return Task.FromResult(answers.Dequeue()); });

        answers.Enqueue(CommandChoice.Deny);
        var denied = await gate.JudgeAsync(Request(), CancellationToken.None);
        Assert.False(denied.Allowed);
        Assert.Equal("Error: the command was denied by the user: git push origin; do not retry it or work around the refusal", denied.Error);
        Assert.Equal(["git push"], Assert.Single(seen).Prefixes);

        answers.Enqueue(CommandChoice.Once);
        Assert.True((await gate.JudgeAsync(Request(), CancellationToken.None)).Allowed);
        answers.Enqueue(CommandChoice.Once);
        Assert.True((await gate.JudgeAsync(Request(), CancellationToken.None)).Allowed);   // once is once: asked again
        Assert.Equal(3, seen.Count);

        answers.Enqueue(CommandChoice.Session);
        Assert.True((await gate.JudgeAsync(Request(), CancellationToken.None)).Allowed);
        Assert.True((await gate.JudgeAsync(Request("git push --force"), CancellationToken.None)).Allowed);   // the prefix, not the line
        Assert.Equal(4, seen.Count);
        Assert.Empty(saved);

        answers.Enqueue(CommandChoice.Permanent);
        Assert.True((await gate.JudgeAsync(Request("dotnet build && git push"), CancellationToken.None)).Allowed);
        Assert.Equal(["dotnet build", "git push"], saved);   // the pick names both prefixes; the file takes both
        Assert.Equal(["dotnet build", "git push"], seen[^1].Prefixes);

        answers.Enqueue(null);   // never asked (no watcher)
        var unasked = await gate.JudgeAsync(Request("rm -rf x"), CancellationToken.None);
        Assert.False(unasked.Allowed);
        Assert.Equal("Error: the command was not run: there was no screen to ask the user on (Shell command policy is ask; --yolo, NEONSIDEKICK_COMMAND_POLICY=yolo or the profile's Shell allowed commands would let it run). Allowed prefixes: dotnet build, git push. Do not retry it or work around the refusal; tell the user what could not run", unasked.Error);

        // The record (2026-09-26): the denial and the never-asked, not the approvals.
        Assert.Equal(["git push origin", "rm -rf x"], gate.Refusals);
    }

    /// <summary>What a gate refused (2026-09-26): not approved without an asker, noted by a tool, never an approval, never the off policy's refusal.</summary>
    [Fact]
    public async Task CommandGate_Refusals_RecordWhatDidNotRun()
    {
        var settings = new AppSettingsData { ShellCommandAllowed = ["dir"], ShellCommandPolicy = "ask" };
        var gate = new CommandGate(() => settings, new CommandAllowList(() => settings.ShellCommandAllowed, _ => { }), null);

        Assert.Empty(gate.Refusals);
        await gate.JudgeAsync(Request("dir /b", "cmd"), CancellationToken.None);
        await gate.JudgeAsync(Request("del x", "cmd"), CancellationToken.None);
        gate.NoteRefused(new CommandRequest("python", "print(1)", [ShellText.ScriptPrefix("python")], IsScript: true));
        Assert.Equal(["del x", "python script"], gate.Refusals);

        settings.ShellCommandPolicy = "yolo";
        Assert.True((await gate.JudgeAsync(Request("del y", "cmd"), CancellationToken.None)).Allowed);
        settings.ShellCommandPolicy = "off";
        Assert.False((await gate.JudgeAsync(Request("del z", "cmd"), CancellationToken.None)).Allowed);
        Assert.Equal(["del x", "python script"], gate.Refusals);
    }

    [Fact]
    public void RefusedSummary_CountsEachCommandOnce()
    {
        Assert.Equal("1 command was not run: \"whoami\"", ShellText.RefusedSummary(["whoami", "whoami"]));
        Assert.Equal("2 commands were not run: \"npm install\", \"python script\"", ShellText.RefusedSummary(["npm install", "python script", "npm install"]));
    }

    [Fact]
    public async Task CommandGate_WithoutAnAsker_TheListAloneDecides()
    {
        var settings = new AppSettingsData { ShellCommandAllowed = ["dir"], ShellCommandPolicy = "ask" };
        var gate = new CommandGate(() => settings, new CommandAllowList(() => settings.ShellCommandAllowed, _ => { }), null);

        Assert.False(gate.CanAsk);
        Assert.True((await gate.JudgeAsync(Request("dir /b", "cmd"), CancellationToken.None)).Allowed);
        var refused = await gate.JudgeAsync(Request("del x", "cmd"), CancellationToken.None);
        Assert.False(refused.Allowed);
        Assert.StartsWith("Error: the command was not run: there was no screen to ask the user on", refused.Error);
        Assert.Contains("Allowed prefixes: dir. ", refused.Error);
        Assert.Contains("Allowed prefixes: none. ", (await new CommandGate(() => new AppSettingsData { ShellCommandPolicy = "ask" }, new CommandAllowList(() => [], _ => { }), null).JudgeAsync(Request(), CancellationToken.None)).Error);
    }

    // ── The probe ────────────────────────────────────────────────────────────

    [WindowsFact]
    public void InterpreterProbe_WalksPathAndPathExt_SkipsWhatItIsToldTo()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\python.exe",
            @"C:\Python313\python.exe",
            @"C:\Windows\System32\bash.exe",
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Tools\build.cmd",
        };
        string path = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps;C:\Python313;C:\Windows\System32;""C:\Program Files\Git\bin"";C:\Tools;;not a path?:";
        bool Exists(string p) => files.Contains(p);

        Assert.Equal(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\python.exe", InterpreterProbe.Find("python", path, ".COM;.EXE;.BAT;.CMD", Exists));
        Assert.Equal(@"C:\Python313\python.exe", InterpreterProbe.Find("python", path, ".COM;.EXE;.BAT;.CMD", Exists, InterpreterProbe.IsStoreStub));
        Assert.Equal(@"C:\Windows\System32\bash.exe", InterpreterProbe.Find("bash", path, null, Exists));
        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", InterpreterProbe.Find("bash", path, null, Exists, InterpreterProbe.IsWslLauncher));
        Assert.Equal(@"C:\Tools\build.cmd", InterpreterProbe.Find("build", path, ".EXE;.CMD", Exists));
        Assert.Null(InterpreterProbe.Find("build", path, ".EXE", Exists));
        Assert.Equal(@"C:\Tools\build.cmd", InterpreterProbe.Find("build.cmd", path, ".EXE", Exists));   // a name with its extension takes it as is
        Assert.Null(InterpreterProbe.Find("node", path, null, Exists));
        Assert.Null(InterpreterProbe.Find("python", null, null, Exists));
        Assert.Equal(".COM;.EXE;.BAT;.CMD", InterpreterProbe.DefaultPathExt);
    }

    [WindowsFact]
    public void Interpreters_FindTheTwoWindowsShells_WithoutAPath_AndBashOnlyWhenThere()
    {
        var none = new Interpreters(_ => null);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), none.Locate(ShellKind.Cmd));
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), none.Locate(ShellKind.PowerShell));
        Assert.Null(none.Locate(ShellKind.Bash));
        Assert.Equal([ShellKind.PowerShell, ShellKind.Cmd], none.AvailableShells());

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\Git\bin\bash.exe", @"C:\pwsh\pwsh.exe", Path.Combine(Environment.SystemDirectory, "cmd.exe") };
        var found = new Interpreters(name => name switch { "PATH" => @"C:\pwsh", "ProgramFiles" => @"C:\Program Files", _ => null }, files.Contains);
        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", found.Locate(ShellKind.Bash));
        Assert.Equal(@"C:\pwsh\pwsh.exe", found.Locate(ShellKind.PowerShell));   // pwsh on the PATH wins over Windows PowerShell
        Assert.Equal([ShellKind.PowerShell, ShellKind.Cmd, ShellKind.Bash], found.AvailableShells());

        // The cache: a probe once, then Refresh probes again.
        int probes = 0;
        var counted = new Interpreters(name => { probes++; return null; });
        counted.Locate(ShellKind.Bash);
        int once = probes;
        counted.Locate(ShellKind.Cmd);
        Assert.Equal(once, probes);   // one probe serves every kind
        counted.Refresh();
        counted.Locate(ShellKind.Bash);
        Assert.Equal(once * 2, probes);

        // The turn's revalidation (2026-10-12): no walk while every found path is still there; the whole cache forgotten when one is gone.
        files.Add(@"C:\Program Files\Git\bin\bash.exe");
        int walks = 0;
        var watched = new Interpreters(name => { walks++; return name switch { "PATH" => @"C:\pwsh", "ProgramFiles" => @"C:\Program Files", _ => null }; }, files.Contains);
        Assert.NotNull(watched.Locate(ShellKind.Bash));
        int first = walks;
        watched.Revalidate();
        watched.Locate(ShellKind.Bash);
        Assert.Equal(first, walks);
        files.Remove(@"C:\Program Files\Git\bin\bash.exe");
        watched.Revalidate();
        Assert.Null(watched.Locate(ShellKind.Bash));   // uninstalled: walked again, not found
        Assert.True(walks > first);

        Assert.Equal([@"C:\PF64\Git\bin\bash.exe", @"C:\PF64\Git\usr\bin\bash.exe", @"C:\PF\Git\bin\bash.exe", @"C:\PF\Git\usr\bin\bash.exe", @"C:\Local\Programs\Git\bin\bash.exe", @"C:\Local\Programs\Git\usr\bin\bash.exe"],
            Interpreters.GitBashCandidates(@"C:\PF", @"C:\PF64", @"C:\Local"));
    }

    // ── The command lines ────────────────────────────────────────────────────

    [Fact]
    public void ShellCommandLine_PerShell_IsPinned()
    {
        var ps = ShellCommandLine.For(ShellKind.PowerShell, "git status", @"C:\pwsh.exe", @"D:\files");
        Assert.Equal(@"C:\pwsh.exe", ps.Executable);
        Assert.Null(ps.Arguments);
        Assert.Equal(["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", ShellCommandLine.Encode(ShellCommandLine.PowerShellScript("git status"))], ps.ArgumentList);
        string script = ShellCommandLine.PowerShellScript("git status");
        Assert.StartsWith("$ProgressPreference = 'SilentlyContinue'\n[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding\n$__ok = $true\ntry {\n& {\ngit status\n} *>&1 | ForEach-Object {\n", script);
        Assert.Contains("[System.Management.Automation.ErrorRecord]) { $script:__ok = $false; [Console]::Error.WriteLine(($_ | Out-String).TrimEnd()) }\n", script);
        Assert.Contains("| Out-String -Stream -Width 200 | ForEach-Object { [Console]::Out.WriteLine($_) }\n} catch { [Console]::Error.WriteLine(($_ | Out-String).TrimEnd()); exit 1 }\n", script);
        Assert.EndsWith("if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\nif (-not $__ok) { exit 1 }\nexit 0", script);
        Assert.Equal(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("x")), ShellCommandLine.Encode("x"));
        Assert.Equal(@"D:\files", ps.WorkingDirectory);
        Assert.Equal("git status", ps.Label);
        Assert.Equal("powershell", ps.Kind);
        Assert.Null(ps.Environment);

        var cmd = ShellCommandLine.For(ShellKind.Cmd, "echo \"a b\" & dir", @"C:\Windows\System32\cmd.exe", @"D:\files");
        Assert.Null(cmd.ArgumentList);
        Assert.Equal("/d /s /c \"chcp 65001>nul & cmd /d /s /c \"echo \"a b\" & dir\"\"", cmd.Arguments);
        Assert.Equal("cmd", cmd.Kind);

        var bash = ShellCommandLine.For(ShellKind.Bash, "ls -la | head", @"C:\Git\bin\bash.exe", @"D:\files");
        Assert.Equal(["-lc", "ls -la | head"], bash.ArgumentList);
        Assert.Equal("bash", bash.Kind);
        Assert.Equal(8000, ShellCommandLine.MaxCommandChars);
    }

    [Fact]
    public void ChildEnvironment_Applies_TheOverrides_ThenTheLaunchesOwn()
    {
        var start = new ProcessStartInfo("x.exe");
        start.Environment["NO_COLOR"] = "0";
        start.Environment["KEEP"] = "yes";
        start.Environment["NEONSIDEKICK_LLM_API_KEY"] = "secret";
        start.Environment["NEONSIDEKICK_HA_TOKEN"] = "secret";
        start.Environment["NEONSIDEKICK_TEST_LLM_URL"] = "http://x";
        start.Environment["NEONSIDEKICK_TEST_OPENAI_API_KEY"] = "secret";
        start.Environment["NEONSIDEKICK_TEST_SQL_CONNECTION"] = "Password=secret";
        start.Environment["NEONSIDEKICK_TEST_UNC_PASSWORD"] = "secret";
        start.Environment["NEONSIDEKICK_WEBUI_TOKEN"] = "secret";
        start.Environment["NEONSIDEKICK_TEST_AD_DOMAIN"] = "Domain=x;User=y;Password=secret";
        var launch = new ProcessLaunch("x.exe", [], null, ".", "x", "cmd", new Dictionary<string, string> { ["TERM"] = "xterm", ["NEONSIDEKICK_EXAMPLE_TOKEN"] = "t" });

        ChildEnvironment.Apply(start, launch);

        Assert.Equal("1", start.Environment["NO_COLOR"]);
        Assert.Equal("yes", start.Environment["KEEP"]);
        Assert.Equal("xterm", start.Environment["TERM"]);   // the launch's own last
        Assert.Equal("t", start.Environment["NEONSIDEKICK_EXAMPLE_TOKEN"]);
        Assert.Equal("utf-8", start.Environment["PYTHONIOENCODING"]);
        Assert.Equal("cat", start.Environment["GIT_PAGER"]);
        // The app's secrets are withheld (2026-10-12), and since 2026-10-19 every secret-named one, and later that day every test variable.
        Assert.False(start.Environment.ContainsKey("NEONSIDEKICK_LLM_API_KEY"));
        Assert.False(start.Environment.ContainsKey("NEONSIDEKICK_HA_TOKEN"));
        Assert.False(start.Environment.ContainsKey("NEONSIDEKICK_TEST_OPENAI_API_KEY"));
        Assert.False(start.Environment.ContainsKey("NEONSIDEKICK_TEST_SQL_CONNECTION"));
        Assert.False(start.Environment.ContainsKey("NEONSIDEKICK_TEST_UNC_PASSWORD"));
        Assert.False(start.Environment.ContainsKey("NEONSIDEKICK_WEBUI_TOKEN"));
        Assert.False(start.Environment.ContainsKey("NEONSIDEKICK_TEST_AD_DOMAIN"));
        Assert.False(start.Environment.ContainsKey("NEONSIDEKICK_TEST_LLM_URL"));
        Assert.Equal("1", start.Environment["NoDefaultCurrentDirectoryInExePath"]);
        Assert.True(ChildEnvironment.IsWithheld("neonsidekick_test_ha_token"));
        Assert.False(ChildEnvironment.IsWithheld("GITHUB_TOKEN"));   // the user's own, not the app's
        Assert.True(ChildEnvironment.IsWithheld("NEONSIDEKICK_TEST_UNC_SHARE"));
        Assert.False(ChildEnvironment.IsWithheld("NEONSIDEKICK_HOME"));
        Assert.Equal("NEONSIDEKICK_TEST_", ChildEnvironment.TestPrefix);
        var withheld = new Dictionary<string, string?> { ["NEONSIDEKICK_OPENAI_API_KEY"] = "k", ["PATH"] = "p" };
        ChildEnvironment.Withhold(withheld);
        Assert.Equal(["PATH"], withheld.Keys);
        Assert.DoesNotContain(ChildEnvironment.WithoutSecrets().Keys, ChildEnvironment.IsWithheld);
        Assert.Equal(["KEY", "TOKEN", "SECRET", "PASSWORD", "CONNECTION", "CREDENTIAL"], ChildEnvironment.SecretWords);
        // An MCP stdio server: every withheld name in the app's environment removed (a null), its own env entries on top.
        var mcp = NeonSidekick.App.McpSession.McpEnvironment(new Dictionary<string, string> { ["NEONSIDEKICK_HA_TOKEN"] = "mine", ["X"] = "1" });
        Assert.Equal("mine", mcp["NEONSIDEKICK_HA_TOKEN"]);
        Assert.Equal("1", mcp["X"]);
        Assert.All(mcp.Where(p => p.Key is not ("NEONSIDEKICK_HA_TOKEN" or "X")), p => Assert.True(p.Value is null && ChildEnvironment.IsWithheld(p.Key)));
        Assert.Equal(["NEONSIDEKICK_LLM_API_KEY", "NEONSIDEKICK_ANTHROPIC_API_KEY", "NEONSIDEKICK_OPENAI_API_KEY", "NEONSIDEKICK_HA_TOKEN", "NEONSIDEKICK_YOUTUBE_API_KEY"], ChildEnvironment.Withheld);
        Assert.Equal(["NO_COLOR", "FORCE_COLOR", "CLICOLOR", "TERM", "PYTHONIOENCODING", "PYTHONUTF8", "PYTHONUNBUFFERED", "GIT_TERMINAL_PROMPT", "GIT_PAGER", "PAGER", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT", "NoDefaultCurrentDirectoryInExePath"], ChildEnvironment.Overrides.Select(p => p.Key));
    }

    // ── The output ring ──────────────────────────────────────────────────────

    [Fact]
    public void OutputBuffer_KeepsTheLastLines_NumbersThemFromOne_AndCutsALongOne()
    {
        var buffer = new OutputBuffer(maxLines: 3, maxBytes: OutputBuffer.MaxLineChars);
        Assert.Equal(0, buffer.TotalLines);
        Assert.Equal(1, buffer.FirstKeptLine);
        Assert.Empty(buffer.Lines());

        buffer.Append("one", false);
        buffer.Append("two", true);
        buffer.Append("three", false);
        buffer.Append("four", false);

        Assert.Equal(4, buffer.TotalLines);
        Assert.Equal(2, buffer.FirstKeptLine);
        Assert.Equal(["two", "three", "four"], buffer.Lines().Select(l => l.Text));
        Assert.True(buffer.Lines()[0].IsError);
        Assert.Equal(3 + 3 + 5 + 4 + 4, buffer.TotalChars);

        var since = buffer.Since(2, out long next);
        Assert.Equal(["three", "four"], since.Select(l => l.Text));
        Assert.Equal(4, next);
        Assert.Empty(buffer.Since(4, out _));
        Assert.Equal(["two", "three", "four"], buffer.Since(0, out _).Select(l => l.Text));   // the dropped one is gone

        Assert.Equal(["three", "four"], buffer.Slice(3, 10, out long first, out long last).Select(l => l.Text));
        Assert.Equal((3, 4), (first, last));
        Assert.Equal(["two"], buffer.Slice(1, 1, out first, out last).Select(l => l.Text));   // before the kept range: from the first kept
        Assert.Equal((2, 2), (first, last));
        Assert.Empty(buffer.Slice(9, 1, out first, out last));
        Assert.Equal((0, 0), (first, last));
        Assert.Equal(["four"], buffer.Tail(1, out first, out last).Select(l => l.Text));
        Assert.Equal((4, 4), (first, last));

        var wide = new OutputBuffer();
        wide.Append(new string('x', OutputBuffer.MaxLineChars + 5), false);
        Assert.Equal(OutputBuffer.MaxLineChars, wide.Lines()[0].Text.Length);
        Assert.EndsWith(OutputBuffer.CutMark, wide.Lines()[0].Text);

        var bytes = new OutputBuffer(maxLines: 100, maxBytes: OutputBuffer.MaxLineChars);
        bytes.Append(new string('a', 5000), false);
        bytes.Append(new string('b', 5000), false);
        Assert.Equal(["b"], bytes.Lines().Select(l => l.Text[..1]));   // the byte cap dropped the first
    }

    // ── The sentences ────────────────────────────────────────────────────────

    [Fact]
    public void ShellText_Headers_Result_AndCut_ArePinned()
    {
        Assert.Equal("exit 0 in 1.2 s (powershell): git status", ShellText.ExitHeader("powershell", 0, TimeSpan.FromSeconds(1.24), "git status"));
        Assert.Equal("exit 3 in 3 m 12 s (cmd): build", ShellText.ExitHeader("cmd", 3, TimeSpan.FromSeconds(192), "build"));
        Assert.Equal("timed out after 3 m 0 s (bash, killed): sleep 999", ShellText.TimedOutHeader("bash", TimeSpan.FromSeconds(180), "sleep 999"));
        Assert.Equal("timed out after 59.9 s (cmd, killed): x", ShellText.TimedOutHeader("cmd", TimeSpan.FromSeconds(59.9), "x"));
        Assert.Equal("1 h 2 m", ShellText.Elapsed(TimeSpan.FromMinutes(62.5)));
        Assert.Equal("0.0 s", ShellText.Elapsed(TimeSpan.FromSeconds(-1)));
        Assert.Equal("12,345", ShellText.Count(12345));
        Assert.Equal("exit 0 in 0.0 s (cmd): x", ShellText.Note("exit 0 in 0.0 s (cmd): x\nline"));
        Assert.Equal(Path.Combine(".neon", "shell"), ShellText.SpillFolderName);   // the app's own folder since 2026-10-21 (.shell until then)

        OutputLine[] none = [];
        Assert.Equal("h\n(no output)", ShellText.Result("h", none, 100, null));
        OutputLine[] both = [new("a", false), new("e1", true), new("b", false), new("e2", true)];
        Assert.Equal("a\nb\n\n--- stderr ---\ne1\ne2", ShellText.Body(both));
        Assert.Equal("h\na\nb\n\n--- stderr ---\ne1\ne2", ShellText.Result("h", both, 100, null));
        Assert.Equal("--- stderr ---\ne1", ShellText.Body([new("e1", true)]));
        Assert.Equal("a\nb", ShellText.Body([new("a", false), new("b", false)]));

        string text = string.Join("\n", Enumerable.Range(1, 40).Select(i => "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));   // 40 lines, 8 chars each less the last
        string cut = ShellText.Cut(text, 100, @".neon\shell\run_1.log");
        Assert.StartsWith("line 1\nline 2\n", cut);
        Assert.EndsWith("\nline 39\nline 40", cut);
        Assert.Contains("\n… (", cut);
        Assert.Contains(" characters cut; the whole output is in .neon\\shell\\run_1.log) …\n", cut);
        Assert.Equal("… (5 characters cut) …", ShellText.CutLine(5, null));
        Assert.Equal("… (412,345 characters cut; the whole output is in x.log) …", ShellText.CutLine(412345, "x.log"));
        string result = ShellText.Result("h", Enumerable.Range(1, 40).Select(i => new OutputLine("line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), false)).ToList(), 100, null);
        Assert.StartsWith("h — output cut\nline 1\n", result);
        Assert.Equal(0.6, ShellText.HeadShare);

        // A buffer that dropped its oldest lines (2026-10-11, the review): the output says so first, and a cut names the spill as the output kept.
        Assert.Equal("… (the first 15,000 lines were not kept) …", ShellText.DroppedLine(15000));
        Assert.Equal("… (the first 3 lines were not kept) …\na\nb", ShellText.Body([new("a", false), new("b", false)], 3));
        Assert.Equal("a\nb", ShellText.Body([new("a", false), new("b", false)], 0));
        Assert.Equal("… (9 characters cut; the output kept is in x.log) …", ShellText.CutLine(9, "x.log", whole: false));
        string dropped = ShellText.Result("h", Enumerable.Range(1, 40).Select(i => new OutputLine("line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), false)).ToList(), 100, "x.log", dropped: 7);
        Assert.StartsWith("h — output cut\n… (the first 7 lines were not kept) …\n", dropped);
        Assert.Contains("; the output kept is in x.log) …", dropped);
    }

    /// <summary>The spill folder (2026-10-12, the shell review): the newest 50 logs kept, and a .gitignore so git never lists it.</summary>
    [Fact]
    public void Spill_KeepsTheNewest_AndIgnoresItselfForGit()
    {
        string root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string folder = Path.Combine(root, ShellText.SpillFolderName);
            for (int i = 0; i < ShellPoliceChecks.SpillKept + 3; i++)
            {
                string id = "run_" + i.ToString("x6", System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(Path.Combine(".neon", "shell", id + ".log"), ShellPoliceChecks.Spill(root, id, "body " + i));
                File.SetLastWriteTimeUtc(Path.Combine(folder, id + ".log"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i));
            }

            var logs = Directory.GetFiles(folder, "*.log").Select(Path.GetFileName).ToList();
            Assert.Equal(ShellPoliceChecks.SpillKept, logs.Count);
            Assert.DoesNotContain("run_000002.log", logs);   // the three oldest went
            Assert.Contains("run_000003.log", logs);
            ShellPoliceChecks.Spill(root, "run_ffffff", "last");
            logs = Directory.GetFiles(folder, "*.log").Select(Path.GetFileName).ToList();
            Assert.Equal(ShellPoliceChecks.SpillKept, logs.Count);
            Assert.DoesNotContain("run_000003.log", logs);
            Assert.Contains("run_ffffff.log", logs);
            Assert.Equal("*\n", File.ReadAllText(Path.Combine(folder, ".gitignore")));
            Assert.False(File.Exists(Path.Combine(root, ".neon", ".gitignore")));   // only the logs are hidden: the rest of .neon may be committed
        }
        finally
        {
            GitAccessTests.DeleteTree(root);
        }
    }

    /// <summary>
    /// A spill folder the app didn't make (2026-10-19, the second shell review, a repository's own .shell then): its files are never counted or
    /// deleted, and it gets no .gitignore. An old .shell folder, from before the logs moved into .neon (2026-10-21), is left as it is.
    /// </summary>
    [Fact]
    public void Spill_LeavesAFolderOfTheUsersAlone()
    {
        string root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string folder = Path.Combine(root, ShellText.SpillFolderName);
            Directory.CreateDirectory(folder);
            for (int i = 0; i < ShellPoliceChecks.SpillKept + 3; i++)
            {
                File.WriteAllText(Path.Combine(folder, "mine" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".log"), "x");
            }

            File.WriteAllText(Path.Combine(folder, "run_00000.log"), "five digits: not ours");
            string old = Path.Combine(root, ".shell");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "run_abcdef.log"), "an old log");
            ShellPoliceChecks.Spill(root, "run_abcdef", "body");
            Assert.Equal(["run_abcdef.log"], Directory.GetFiles(old).Select(Path.GetFileName));   // the old folder: untouched, no .gitignore
            Assert.Equal("an old log", File.ReadAllText(Path.Combine(old, "run_abcdef.log")));
            Assert.Equal(ShellPoliceChecks.SpillKept + 5, Directory.GetFiles(folder, "*.log").Length);
            Assert.False(File.Exists(Path.Combine(folder, ".gitignore")));
            Assert.True(ShellPoliceChecks.IsSpillName(ShellRunner.NewId(new Random(1), NeonSidekick.Llm.Tools.RunCommandTool.IdPrefix) + ".log"));
            Assert.True(ShellPoliceChecks.IsSpillName(ShellRunner.NewId(new Random(1), ProcessRegistry.IdPrefix) + ".log"));
            Assert.True(ShellPoliceChecks.IsSpillName(ShellRunner.NewId(new Random(1), NeonSidekick.Llm.Tools.ExecuteCodeTool.IdPrefix) + ".log"));
            Assert.True(ShellPoliceChecks.IsSpillName(ShellRunner.NewId(new Random(1), NeonSidekick.App.ChatScreen.CmdIdPrefix) + ".log"));
        }
        finally
        {
            GitAccessTests.DeleteTree(root);
        }
    }

    /// <summary>The review of 2026-10-11: a /cmd --bg report shows the end of the output, the earlier lines counted and the spill named.</summary>
    [Fact]
    public void ACmdReport_ShowsTheTail()
    {
        OutputLine[] lines = [new("aaaa", false), new("bbbb", false), new("cccc", false)];   // five characters each with the newline
        Assert.Equal(0, NeonSidekick.App.ChatScreen.CmdTailStart(lines, 15));
        Assert.Equal(1, NeonSidekick.App.ChatScreen.CmdTailStart(lines, 14));
        Assert.Equal(2, NeonSidekick.App.ChatScreen.CmdTailStart(lines, 5));
        Assert.Equal(2, NeonSidekick.App.ChatScreen.CmdTailStart(lines, 1));   // the last line always shown
        Assert.Equal(0, NeonSidekick.App.ChatScreen.CmdTailStart([], 10));
        Assert.Equal(@"(the first 14,880 lines are not shown here; the whole output is in .neon\shell\proc_1.log)", ShellText.CmdEarlierHidden(14880, @".neon\shell\proc_1.log", whole: true));
        Assert.Equal("(the first 1 line is not shown here; the output kept is in x.log)", ShellText.CmdEarlierHidden(1, "x.log", whole: false));
        Assert.Equal("(the first 2 lines are not shown here)", ShellText.CmdEarlierHidden(2, null, whole: true));
        Assert.Equal("(started in an earlier conversation: shown here, not added to this one)", ShellText.CmdOtherConversation);
    }

    [WindowsFact]
    public void ShellText_Errors_LogLines_AndThePane_ArePinned()
    {
        Assert.Equal("Error: the script was denied by the user (python); do not retry it or work around the refusal", ShellText.Denied(new CommandRequest("python", "print(1)", ["code:python"], IsScript: true)));
        Assert.Equal("Error: bash is not installed (no bash.exe found)", ShellText.ShellNotInstalled(ShellKind.Bash));
        Assert.Equal("Error: workdir '../x' is outside the working directory", ShellText.WorkdirOutside("../x"));
        Assert.Equal("Error: workdir 'x' is not a folder", ShellText.WorkdirNotFolder("x"));
        Assert.Equal("Error: timeout must be 1 to 600", ShellText.BadTimeout(1, 600));
        Assert.Equal("Error: the command is longer than 8,000 characters; put it in a script file and run that", ShellText.CommandTooLong(8000));
        Assert.Equal("Error: could not start bash.exe (boom)", ShellText.CouldNotStart("bash.exe", "boom"));
        Assert.Equal("approval: allowed once — cmd \"dir a\"", ShellText.ApprovedLogLine(new CommandRequest("cmd", "dir\na", ["dir"]), "allowed once"));
        Assert.Equal("approval: refused (denied by the user) — cmd \"dir\"", ShellText.RefusedLogLine(new CommandRequest("cmd", "dir", ["dir"]), "denied by the user"));
        Assert.Equal("run_command: cmd \"dir\" → exit 0 in 0.1 s (2,340 chars)", ShellText.RunLogLine("cmd", "dir", "exit 0 in 0.1 s", 2340));
        // The police (2026-09-22): the sentence names the token and the rule, never the setting; the transcript keys 👮 on its head.
        Assert.Equal(@"Error: outside the working directory: 'C:\Windows\win.ini' — a command or a script may only name paths under it", ShellText.OutsidePath(@"C:\Windows\win.ini"));
        Assert.True(ShellText.IsOutside(ShellText.OutsidePath("~")));
        Assert.False(ShellText.IsOutside(ShellText.WorkdirOutside("..")));
        Assert.False(ShellText.IsOutside("exit 0 in 0.0 s (cmd): dir\n"));
        Assert.DoesNotContain("police", ShellText.OutsidePath("~"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("police: refused ('C:\\') — cmd \"cd C:\\ dir\"", ShellText.PolicedLogLine(new CommandRequest("cmd", "cd C:\\\ndir", ["cd", "dir"]), "C:\\"));

        var request = new CommandRequest("powershell", "git push origin main && rm x", ["git push", "rm"]);
        Assert.Equal("powershell › git push origin main && rm x", ShellText.Caption(request));
        Assert.Equal("Allow \"git push\", \"rm\" for this session", ShellText.SessionRow(request));
        Assert.Equal("Allow \"git push\", \"rm\" always (saved to the profile)", ShellText.PermanentRow(request));
        var script = new CommandRequest("python", "import os\nprint(os.getcwd())\n", ["code:python"], IsScript: true);
        Assert.Equal("python · 3 lines · first line: import os", ShellText.Caption(script));
        Assert.Equal("Allow python scripts for this session", ShellText.SessionRow(script));
        Assert.Equal("(🔓 allowed for this session: git push, rm)", ShellText.SessionAllowedNotice(["git push", "rm"]));
        Assert.Equal("(🔓 allowed always: git push — the Shell tab of /tools)", ShellText.PermanentAllowedNotice(["git push"]));

        var page = App.CommandApprovalMenu.Page(request);
        Assert.Equal("Run this command?", page.Title);
        Assert.Equal("powershell › git push origin main && rm x", page.Caption);
        Assert.Equal(["Deny", "Allow once", "Allow \"git push\", \"rm\" for this session", "Allow \"git push\", \"rm\" always (saved to the profile)"], page.Rows);
        Assert.Equal(App.CommandApprovalMenu.Hotkeys, page.Hotkeys);
        Assert.Equal(new Dictionary<char, int> { ['d'] = 0, ['o'] = 1, ['s'] = 2, ['a'] = 3 }, App.CommandApprovalMenu.Hotkeys);
        Assert.Equal([CommandChoice.Deny, CommandChoice.Once, CommandChoice.Session, CommandChoice.Permanent], App.CommandApprovalMenu.Choices);
        Assert.Equal("Run this script?", App.CommandApprovalMenu.Page(script).Title);

        // The whole command (2026-10-07): the view's label and lines, a script's numbered, and the button on the title row.
        Assert.Equal([ShellText.ViewButton], App.CommandApprovalMenu.Page(script).Buttons!.Select(b => b.Title));
        var long12 = new CommandRequest("python", string.Join("\r\n", Enumerable.Range(1, 12).Select(i => "print(" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")")), [], IsScript: true);
        Assert.Equal("Run this script? › python · 12 lines", ShellText.WholeCommandLabel(long12));
        var numbered = ShellText.WholeCommandLines(long12);
        Assert.Equal(" 1  print(1)", numbered[0]);
        Assert.Equal("12  print(12)", numbered[11]);
        Assert.Equal("Run this command? › cmd", ShellText.WholeCommandLabel(new CommandRequest("cmd", "echo hi", ["echo"])));
        Assert.Equal(["echo hi"], ShellText.WholeCommandLines(new CommandRequest("cmd", "echo hi", ["echo"])));
    }

    /// <summary>
    /// Leftovers (2026-10-19, the second shell review): a job counts as holding them only while a process is still in it, so an emptied job
    /// is no kill to report. A foreground command's job too since the third review: its server holding the output is killed at the timeout.
    /// </summary>
    [WindowsFact]
    public void ProcessChild_HoldsLeftovers_WhileItsJobStillRunsSomething()
    {
        static Process Ping() => Process.Start(new ProcessStartInfo("ping", "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;

        // Both jobs are kill-on-close since 2026-10-22 (review F1, the fix plan's D9); a foreground one is kept until the app ends.
        using var foreground = new ProcessChild(Ping(), keepUntilExit: true);
        using var background = new ProcessChild(Ping(), keepUntilExit: false);
        try
        {
            Assert.True(foreground.InJob && background.InJob);
            Assert.True(foreground.HoldsLeftovers);
            Assert.True(background.HoldsLeftovers);
        }
        finally
        {
            foreground.Kill();
            background.Kill();
        }

        Assert.True(background.WaitForExit(10_000) && foreground.WaitForExit(10_000));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((background.HoldsLeftovers || foreground.HoldsLeftovers) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.False(background.HoldsLeftovers);   // the job emptied: nothing left to kill
        Assert.False(foreground.HoldsLeftovers);
        Assert.False(NeonSidekick.EmbeddedLlm.ChildJob.HoldsProcesses(0));
    }
}
