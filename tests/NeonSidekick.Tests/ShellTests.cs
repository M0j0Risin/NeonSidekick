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
    [InlineData("git -C x status", "git")]
    [InlineData("GIT STATUS", "git status")]
    [InlineData("dotnet build -c Release", "dotnet build")]
    [InlineData("python -c \"print(1)\"", "python")]
    [InlineData("\"C:\\Tools\\Foo.EXE\" -x", "foo")]
    [InlineData("./build.sh --fast", "build")]
    [InlineData(".\\build.ps1", "build")]
    [InlineData("C:\\Windows\\System32\\cmd.exe /c dir", "cmd")]
    [InlineData("FOO=1 BAR=2 make test", "make test")]
    [InlineData("Get-ChildItem -Recurse", "get-childitem")]
    [InlineData("npm run dev", "npm run")]
    [InlineData("net use Z: \\\\nas\\x", "net use")]   // net a verb program since 2026-10-03
    [InlineData("NET.EXE user bob /add", "net user")]
    [InlineData("   ", "")]
    public void CommandPrefix_Of_IsTheProgram_AndTheVerbForVerbPrograms(string command, string prefix)
    {
        Assert.Equal(prefix, CommandPrefix.Of(command));
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
        Assert.Equal(["ping", "exit"], CommandPrefix.All("ping -n 3 127.0.0.1 >nul & exit /b 0"));
        Assert.Empty(CommandPrefix.All(""));
        Assert.Equal(["a", "b"], CommandPrefix.Segments("a || b"));
        Assert.Equal(["\"C:\\x.exe\"", "y"], CommandPrefix.Segments("& \"C:\\x.exe\" & y"));
        Assert.Equal(
            [("a", "&&"), ("b", "||"), ("c", "|"), ("d", "&"), ("e", ";"), ("f", "\n"), ("g 'h && i'", "")],
            CommandPrefix.JoinedSegments("a && b || c | d & e; f\r\ng 'h && i'"));
        Assert.Equal(["tool"], CommandPrefix.All("& 'C:\\x y\\tool.exe' run"));   // PowerShell's call operator: an empty segment, dropped
        Assert.Contains("git", CommandPrefix.VerbPrograms);
        Assert.DoesNotContain("python", CommandPrefix.VerbPrograms);
        Assert.DoesNotContain("powershell", CommandPrefix.VerbPrograms);
        Assert.Equal("foo", CommandPrefix.Program("Foo.exe"));
        Assert.Equal("foo.py", CommandPrefix.Program("foo.py"));   // not an executable extension
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
        var launch = new ProcessLaunch("x.exe", [], null, ".", "x", "cmd", new Dictionary<string, string> { ["TERM"] = "xterm", ["NEONSIDEKICK_BRIDGE_TOKEN"] = "t" });

        ChildEnvironment.Apply(start, launch);

        Assert.Equal("1", start.Environment["NO_COLOR"]);
        Assert.Equal("yes", start.Environment["KEEP"]);
        Assert.Equal("xterm", start.Environment["TERM"]);   // the launch's own last
        Assert.Equal("t", start.Environment["NEONSIDEKICK_BRIDGE_TOKEN"]);
        Assert.Equal("utf-8", start.Environment["PYTHONIOENCODING"]);
        Assert.Equal("cat", start.Environment["GIT_PAGER"]);
        Assert.Equal(["NO_COLOR", "FORCE_COLOR", "CLICOLOR", "TERM", "PYTHONIOENCODING", "PYTHONUTF8", "PYTHONUNBUFFERED", "GIT_TERMINAL_PROMPT", "GIT_PAGER", "PAGER", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT"], ChildEnvironment.Overrides.Select(p => p.Key));
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
        Assert.Equal(".shell", ShellText.SpillFolderName);

        OutputLine[] none = [];
        Assert.Equal("h\n(no output)", ShellText.Result("h", none, 100, null));
        OutputLine[] both = [new("a", false), new("e1", true), new("b", false), new("e2", true)];
        Assert.Equal("a\nb\n\n--- stderr ---\ne1\ne2", ShellText.Body(both));
        Assert.Equal("h\na\nb\n\n--- stderr ---\ne1\ne2", ShellText.Result("h", both, 100, null));
        Assert.Equal("--- stderr ---\ne1", ShellText.Body([new("e1", true)]));
        Assert.Equal("a\nb", ShellText.Body([new("a", false), new("b", false)]));

        string text = string.Join("\n", Enumerable.Range(1, 40).Select(i => "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));   // 40 lines, 8 chars each less the last
        string cut = ShellText.Cut(text, 100, @".shell\run_1.log");
        Assert.StartsWith("line 1\nline 2\n", cut);
        Assert.EndsWith("\nline 39\nline 40", cut);
        Assert.Contains("\n… (", cut);
        Assert.Contains(" characters cut; the whole output is in .shell\\run_1.log) …\n", cut);
        Assert.Equal("… (5 characters cut) …", ShellText.CutLine(5, null));
        Assert.Equal("… (412,345 characters cut; the whole output is in x.log) …", ShellText.CutLine(412345, "x.log"));
        string result = ShellText.Result("h", Enumerable.Range(1, 40).Select(i => new OutputLine("line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), false)).ToList(), 100, null);
        Assert.StartsWith("h — output cut\nline 1\n", result);
        Assert.Equal(0.6, ShellText.HeadShare);
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
    }
}
