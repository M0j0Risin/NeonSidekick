# Building from source

How to build, test and publish Neon Sidekick yourself. Back to the [README](../README.md).

## What you need

* **Windows x64** for the Windows build. Each release also has a preview package for Apple Silicon Macs, built on a Mac; see [Building on a Mac](#building-on-a-mac).
* **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).**
* **For the native build (NativeAOT):** Visual Studio 2022 or later, or its Build Tools, with the **Desktop development with C++** workload. A plain `dotnet build` doesn't need it.
* **PowerShell** to run `build.ps1`, not Git Bash: the smoke step hangs under MSYS.

## Quick start

```powershell
git clone https://github.com/M0j0Risin/NeonSidekick.git
cd NeonSidekick
dotnet build NeonSidekick.slnx -c Release
dotnet run --project src/NeonSidekick -c Release
```

`dotnet run` starts the app on the .NET runtime, which is fine for trying a change. The native, self-contained `NeonSidekick.exe` that releases ship comes from `build.ps1`.

## build.ps1

`build.ps1` is the whole pipeline in one script; CI runs the same script, so a local run matches it.

| Command | What it does |
|---|---|
| `.\build.ps1` | Restore, build, run the tests with coverage, publish the native exe and smoke-test it. |
| `.\build.ps1 -TestOnly` | Restore, build and test; no publish. The quickest full check. |
| `.\build.ps1 -Publish` | Restore, build, publish the native exe and smoke-test it; no tests. |
| `.\build.ps1 -Package` | The full run, then the release zip and its `.sha256` in `publish\package\`. |
| `.\build.ps1 -Package -Tag v1.2.3` | The same, refusing a tag that isn't the project's `<Version>`. |
| `.\build.ps1 -CoverageFloor 0` | Reports coverage without failing under the floor (80% by default). |
| `.\build.ps1 -Clean` | Deletes `bin\`, `obj\` and `publish\`. |

The native exe lands in `publish\output\NeonSidekick.exe`.

The build is strict on purpose:
* **Zero warnings.** Any compiler warning fails the build, and so does any NativeAOT warning about the app's own code.
* **Coverage.** The tests must cover at least 80% of the lines.
* **Smoke test.** After publishing, the script runs the native exe with `--smoke`, which checks that every native library loads and works. `dotnet build` and `dotnet test` can't catch a missing native DLL; this can.

## Tests

```powershell
dotnet test tests/NeonSidekick.Tests -c Release
dotnet test tests/NeonSidekick.Tests -c Release --filter "FullyQualifiedName~AssistantTests"
```

* The tests run one at a time, by design.
* **Live tests** (a real LLM server, database, camera, microphone, speech model…) are skipped unless their resource is there. [ENVIRONMENT.md](ENVIRONMENT.md#test-suite) lists the variables that turn each one on.
* Two tests load an embedded model onto the GPU. `build.ps1` leaves them out; run them with `dotnet test tests/NeonSidekick.Tests -c Release --filter Category=LoadsEmbeddedModel`.

## Checking the native exe

Besides `--smoke`, the published exe has checks for real hardware and servers: `--audio-check`, `--voice-check`, `--camera-check`, and one per database family (`--sql-check`, `--postgres-check`…). [COMMANDLINE.md](COMMANDLINE.md) lists them all.

## Building on a Mac

A preview build for Apple Silicon Macs (`osx-arm64`). Each release since v0.5.0 has its package (`NeonSidekick-v<version>-osx-arm64.tar.gz`, built on GitHub's Mac runner), so building it yourself is only needed for a change of your own. It leaves out the features that have no macOS backend yet: voice, pictures, the camera, screen capture, the viewer windows, YouTube playback, printing, Docker and UNC shares. The chat, the tools, MCP, git, the database tools, sessions, headless mode and the embedded LLM (on Metal) are all there.

NativeAOT can't build a Mac binary from Windows, so build on the Mac itself. You need:

* **Microsoft's [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** for macOS Arm64, not Homebrew's (see below).
* **The Xcode command line tools** (`xcode-select --install`) for the native link.
* **PowerShell 7** to run `build.ps1`, installed as a .NET tool (see below).

### Setting up the Mac

Homebrew's `dotnet` formula is built from source. Its NativeAOT pack is marked non-portable, so the publish links against OpenSSL and fails at the very end with `ld: library 'ssl' not found`. Homebrew's `powershell` formula depends on that same `dotnet`, so both have to go:

```sh
brew uninstall powershell dotnet          # if they are installed
brew install --cask dotnet-sdk            # Microsoft's SDK, in /usr/local/share/dotnet (asks for your password)
dotnet tool install --global PowerShell   # pwsh, in ~/.dotnet/tools
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.zshrc
```

Homebrew no longer has a stable `powershell` cask, only `powershell@preview`. The SDK's installer also adds `~/.dotnet/tools` through `/etc/paths.d`, but as a literal `~`, which zsh doesn't expand; hence the `~/.zshrc` line. In a new shell, check:

```sh
which pwsh dotnet      # ~/.dotnet/tools/pwsh, and /usr/local/share/dotnet/dotnet (or a link to it)
dotnet --list-sdks     # 10.0.x [/usr/local/share/dotnet/sdk]
```

Run `pwsh ./build.ps1 -Clean` once after switching SDKs, so nothing built by the old one is reused.

### Building

```sh
pwsh ./build.ps1 -TestOnly     # build and test: green on a Mac, the Windows-only tests skipped
pwsh ./build.ps1 -Publish      # publish/output/NeonSidekick, smoke-tested
pwsh ./build.ps1 -Package      # publish/package/NeonSidekick-v<version>-osx-arm64.tar.gz
```

The publish prints trim and AOT warnings from Oracle, SqlClient, NumSharp and Whisper.net, and `ILC: Method … will always throw` lines for Oracle's optional cloud assemblies. The Windows publish prints them too; they don't fail the build.

`-Runtime` defaults to `osx-arm64` on a Mac. The script marks the bundled `espeak-ng` executable as runnable and gives it and the app an ad-hoc signature. The package is a `.tar.gz` so the executable bits survive. It leaves out what a Mac never runs: the debug symbols (`NeonSidekick.dSYM`, kept in `publish/output` for reading a crash report), the Windows ONNX Runtime dlls and the other platforms' espeak-ng.

A downloaded build isn't notarized, so macOS quarantines it. Clear that once after unpacking: `xattr -dr com.apple.quarantine NeonSidekick-v<version>-osx-arm64`.

Good to know on a Mac:
* **Secrets go in the Keychain.** API keys and database passwords are encrypted with a key the app keeps in your login Keychain (the *NeonSidekick* item), and a `passwordStore: credman` password is a Keychain item you make with `security add-generic-password -s <target> -a <user> -w`. After each new build macOS asks once whether it may use that key; choose **Always Allow**.
* **The shell is `zsh`.** `run_command` runs in zsh unless you pick `bash` or `powershell` (pwsh) under *Shell default*. Scripts run with `python3`, `node` or `pwsh`.
* **The embedded LLM runs on Metal.** Pick a model in `/settings` › Embedded › Embedded models; the app downloads llama.cpp's macOS build (12 MB) and the model, and runs it on the Apple Silicon GPU. The GPU shares the Mac's memory, and macOS lets it use about two thirds of it (10.7 GB on a 16 GB Mac), so a 16 GB Mac suits the E2B and E4B models best. The NVFP4 builds (NVIDIA only) are left out, and the vision projector is downloaded but not loaded while pictures need Windows. The server runs under a small guard (the app's own executable, `--llama-guard`), which stops it when the app ends however it ends; a server a crash still left running is stopped at the next start.
* **Copy and paste.** Cmd+V pastes through the terminal. `/copy` and the input line's copy use `pbcopy`.
* **Editors.** `/persona` and friends open the file in its default app. For `/draft`, set *Draft editor* to a command that waits, such as `code --wait` or `open -W -t`; a terminal editor like vim can't share the terminal with the app.
* **Terminals.** Terminal.app and iTerm2 both work. For the Ctrl+Alt shortcuts (Ctrl+Option on a Mac), let Option act as Alt: *Use Option as Meta key* in Terminal.app (Settings → Profiles → Keyboard), *Left Option key: Esc+* in iTerm2 (Settings → Profiles → Keys); Option then no longer types characters such as å in that profile (iTerm2's right Option still can). Hold Fn (Terminal.app) or Option (iTerm2) to select text with the mouse while the app has it, or press Cmd+R in Terminal.app to turn the app's mouse off and on. iTerm2 also honours synchronized output, so a full redraw never shows half drawn.

### SQL Server with Windows Authentication (Kerberos)

A connection with `"auth": "windows"` in `sql.json` uses Windows Authentication, also called integrated security: an Active Directory account and no password in the file. On Windows that sign-in is Kerberos, falling back to NTLM when Kerberos fails. On a Mac it is **Kerberos only**: SqlClient has no NTLM fallback there, so the Mac needs a Kerberos ticket from `kinit`, which comes with macOS. It can't work against a SQL Server that isn't in a domain, such as one in a Docker container; use a SQL login (`"auth": "sql"`) there. It has been checked from the Mac build against SQL Server 2022 (`--sql-check`, all checks passed).

1. **Find the realm of the domain the server is in**, in capitals (e.g. `AD.EXAMPLE.EDU`). For your own account: `$env:USERDNSDOMAIN` in PowerShell (`echo %USERDNSDOMAIN%` in cmd) on a Windows PC on the domain, `dsconfigad -show` (*Active Directory Domain*) on a Mac bound to AD, or ask IT. Your sign-in name (`whoami /upn`, e.g. `jdoe@example.edu`) can carry a different suffix from the realm: the realm is the domain's own name. If the server is in another domain, see [A server in another domain](#a-server-in-another-domain).
2. **Get a ticket**, on the organization's network or VPN: `kinit jdoe@AD.EXAMPLE.EDU` asks for the domain password and prints nothing when it works. Type the realm in capitals: with it in lowercase, `kinit` may still succeed but leave a ticket (`krbtgt/ad.example.edu@AD.EXAMPLE.EDU`) the Kerberos library won't use, and the connection fails with `Matching credential … not found`. `kinit --keychain …` keeps the password in your Keychain, so later runs don't ask. A plain `kinit` with no name fails with `Configuration file does not specify default realm` until `/etc/krb5.conf` names one (`[libdefaults]` / `default_realm = AD.EXAMPLE.EDU`).
3. **Check it:** `klist` shows `krbtgt/AD.EXAMPLE.EDU@AD.EXAMPLE.EDU` and its expiry, usually about 10 hours away.
4. **Try the connection:** `./publish/output/NeonSidekick --sql-check <connection>`. It reports the account it signed in as (`AD\jdoe`). Afterwards `klist` also lists the server's ticket, `MSSQLSvc/<server>:<port>`.

The server goes in `sql.json` by its full name, as `host`, `host,port` or `host\instance,port` (the instance before the port; with the port given, SqlClient connects to it directly). SqlClient asks for the server's ticket under its name and port, `MSSQLSvc/sqlhost.ad.example.edu:1451`, so use the name the server is registered under, never an IP address or an alias.

#### A server in another domain

An organization can have more than one AD domain: a production one for people's accounts and a separate one for test servers, say. A ticket from your account's domain only reaches servers in that domain or in one that trusts it. Against a server in a domain with no trust, the connection fails with `The target principal name is incorrect. Cannot generate SSPI context.`, even though `kinit` worked. A Windows PC may still connect to the same server, because it falls back to NTLM.

To see which domain a server is in, and whether a ticket for it can be had:

```sh
dig +short SRV _kerberos._tcp.test.example.edu                               # that domain's domain controllers, if it is one
kgetcred MSSQLSvc/sqlhost.test.example.edu:1451@AD.EXAMPLE.EDU               # a ticket from your own domain
kgetcred MSSQLSvc/sqlhost.test.example.edu:1451@TEST.EXAMPLE.EDU             # a ticket from the server's domain
```

`kgetcred` prints nothing when the ticket is issued (`klist` then lists it). `Server (…) unknown` means that domain has no such name; `Server (krbtgt/TEST.EXAMPLE.EDU@AD.EXAMPLE.EDU) unknown` means your domain has no trust with the server's. Then sign in with an account in the server's domain, if you have one (`kinit jdoe-test@TEST.EXAMPLE.EDU`), or ask the server's DBA for a SQL login.

macOS keeps a ticket cache per account, side by side; the newest `kinit` becomes the default, and SqlClient uses the default. `klist -A` lists them all, and `kswitch -p jdoe@AD.EXAMPLE.EDU` makes another the default.

Day to day:
* Tickets expire, and the connection fails until you run `kinit` again; `kinit -R` renews a ticket that's still valid without the password, if the domain allows it.
* `kdestroy` throws the default cache's tickets away (`kdestroy -p <principal>` one account's, `kdestroy -A` all).
* The **Ticket Viewer** app (`/System/Library/CoreServices/Ticket Viewer.app`) does the same as `kinit`, `klist` and `kdestroy`.

If it fails:
* **`Cannot find KDC for realm`:** the Mac can't reach a domain controller. Check the network or VPN, and that the realm is spelled right and in capitals. `dig +short SRV _kerberos._tcp.ad.example.edu` should list the domain controllers; if it lists none, the realm is wrong or the domain's DNS is only reachable on its network, and IT can give you an `/etc/krb5.conf` naming the domain controllers.
* **`Clock skew too great`:** the clock is more than 5 minutes off. Turn on *Set time and date automatically* (System Settings → General → Date & Time).
* **`Matching credential … not found`:** the ticket was taken with the realm in lowercase. `kdestroy -p <principal>`, then `kinit` again with it in capitals.
* **`The target principal name is incorrect. Cannot generate SSPI context.`:** no ticket could be had for the server's name. Either the server is in a domain your ticket's doesn't trust ([A server in another domain](#a-server-in-another-domain)), or its Kerberos name (SPN) isn't registered for that host and port, which the server's admin fixes. `kgetcred` above tells the two apart.

When writing it down for others, "Windows Authentication (Kerberos)" says both what the server sees (a Windows login) and what the Mac needs (a Kerberos ticket). On the server, `sys.dm_exec_connections.auth_scheme` shows `KERBEROS` for such a connection (`NTLM` for a Windows client that fell back).

### Status of the Mac port

The first build and run on a Mac was on 2026-10-06 (Apple Silicon, Terminal.app). What came up, and where each item stands:

**Fixed** (on the `macos-preview` branch):
* **The publish failed to link** (`library 'ssl' not found`): Homebrew's SDK. Use Microsoft's; see [Setting up the Mac](#setting-up-the-mac).
* **The smoke test crashed at exit** (exit code 134, `mutex lock failed: Invalid argument`, with a ggml backtrace). The ggml name is misleading: ggml installs the process-wide crash handler, but the crash is ONNX Runtime 1.22, which aborts at exit on macOS while its environment is still alive. `Program.cs` now releases that environment as its last step on macOS and Linux, when one was made.
* **`git:roundtrip` failed with `AboveSandbox`.** On macOS `/var` and `/tmp` are links into `/private`, and libgit2 reports the resolved path (`/private/var/…`), so a repository in the working directory looked like one above it. Any project under `/tmp` hit the same thing. `GitAccess` now resolves the root's real path (`Files/RealPath.cs`, libc's `realpath`) and maps libgit2's paths back to the root's spelling.
* **`pdf:browser` timed out after 60 s.** Chrome on macOS writes the PDF, or prints the page for `--dump-dom`, and then never exits; every browser-based web fetch would have waited out its timeout too. `HeadlessBrowser` now stops Chrome once the output is whole (the PDF ends with `%%EOF`, the page with `</html>`) and has stayed unchanged for 1.5 s. On Windows Chrome exits by itself within that time.
* **The screen blanked while typing.** Each full redraw (opening the `/` menu, a draft wrapping) erases the pane, asks the terminal where the cursor is, and draws the pane again. The question went through the held frame writer (`FrameWriter`) and stayed in the buffer until the 500 ms timeout, so Terminal.app, which shows output as it arrives, showed a blank pane for half a second. The late answer could also be taken for the next question's, putting the screen out of step. `UnixConsoleInput.QueryCursor` now flushes the held frame with the question; a redraw takes milliseconds.

* **Paths spelled through `/private` were refused.** With the working directory at `/tmp/x`, the file tools and the shell's path check refused `/private/tmp/x/a.txt` as outside it, though it is the same file (`pwd` prints that spelling). `WorkingDirectory.InRootSpelling`, the git fix's mapping moved there, now serves `WorkingDirectory.Resolve`, `PathPolice` and `GitAccess` alike; the link rules still judge the path after it.
* **The model was told the shells were "powershell, cmd or bash"** and never which computer it was on, so it ran `powershell -Command "Get-Acl …"` in zsh. On a Mac the shell rule now names the Mac, zsh, bash and pwsh (`Assistant.ShellRuleFor`, `ShellPickMac`); Windows' wording is unchanged.
* **`/cwd` listed no folders.** Its list took only a drive path (`C:\…`). On a Mac a path starting with `/` lists its folders (`FolderCompleter.IsMachinePath`); network and optical volumes and the automounter's `/net` and `/Network` list nothing, so typing never waits on a server. `/cwd ~` is still the default folder, not the home folder.
* **The toolbar's ⚙️ and 🛠️ ran together and clicks missed them.** Measured with cursor reports, Terminal.app, and iTerm2 on the alternate screen the app runs in, move one cell for a text-default emoji with its selector (⚙️ 🛠️ 🖥️ ✂️ 🗑️) but paint it two wide; Windows Terminal moves two. The app counted two, so every toolbar click after them landed two to four cells off, mostly on a blank, which opens `/settings`. When `TERM_PROGRAM` is `Apple_Terminal` or `iTerm.app`, `TextCells.NarrowSelectorSequences` counts them one cell, the toolbar writes a space after each so the gaps match, and a click on the cell such a glyph paints over still lands on it. The hint row does the same for its notes and a pane's hint (`⚙️ Settings · double-click to open`), and the transcript for every line it writes (`🛠️ read_file`), before the scrollback keeps it.
* **The test suite failed on a Mac** (635 of about 10,100 tests). The tests were written on Windows. A test whose subject is Windows-only (WinMM, DPAPI, WIC pictures, the viewer windows, UNC, Docker, the camera, the embedded LLM (until it came to the Mac on 2026-10-07; only its CUDA/Vulkan detection and job object stay Windows-only), `cmd.exe`) is marked `[WindowsFact]`/`[WindowsTheory]` and skipped elsewhere; a test of a portable feature spelled with Windows paths or `cmd` lines has a Unix twin beside it (`…_Unix`, `[UnixFact]`), and `ProcessUnixTests`, `RunCommandUnixTests` and `WorkingDirectoryUnixTests` hold the larger sets. `pwsh ./build.ps1 -TestOnly` is green on a Mac, about 85% line coverage. The run needs the Xcode tools' `/usr/bin/python3`: the screen and app fixtures give the interpreter probe a PATH of `/usr/bin:/bin` off Windows (`Fakes/TestPath`). The triage found three bugs:
  * **A named-pipe SQL Server crashed the SQL tools.** An `np:\\.\pipe\…` server in `sql.json` threw `PlatformNotSupportedException` out of the tool; off Windows it is now a connect error like any unreachable server (`SqlAccess.IsUnsupportedHere`).
  * **The sandbox hid every dot-name.** .NET reads a dot-name as Hidden on Unix, and the walks skip Hidden, so `.gitignore`, `.github` and `.env` were missing from listings and searches and dropped from a folder's copy. Off Windows a walk now skips only the macOS hidden flag (`chflags hidden`) and `.git`, as Windows skips its Hidden attribute and Git for Windows' hidden `.git` (`WorkingDirectory.IsHiddenOffWindows`).
  * **A city named no time zone.** "Tokyo" was matched against Windows display names, which macOS zones don't have; the city is now read from the IANA id's last segment too (`ClockText.NamesCity`).

* **Windows wording on a Mac** (the tidy-up before the first Mac release, 2026-10-06). On a Mac, the app now uses Mac wording in these places:
  * The connection files' templates (`sql.json`, `oracle.json`, `mysql.json`, `postgres.json`, `unc.json`) name the macOS Keychain, `security add-generic-password -s <target> -a <user> -w` and `keychain:…` (`SqlText.CredentialStore`/`CredentialCommand`).
  * The wizards' store picks, the set-password rows and the saved/undecryptable password sentences name the Keychain too.
  * `open` says a folder opened in Finder.
  * The skills row reads `.agents/skills`, and its value reads `~/.agents/skills`.
  * `/terminal`, the Claude CLI's not-found error, the skill installer's path refusal and the SQLite wizard's example path have Mac sentences.
  * `convert_to_pdf` and the PDF errors drop Microsoft Print to PDF.
  * The perf bar's GPU note, `/camera list`'s note and `/about`'s WIC and WebView2 lines have Mac wording.
  * `neon_help` and `/help` read a Mac layer (`Help/HelpMac`) for the settings and command forms whose text named Windows. Windows' own sentences are unchanged.
  * SQL Server's Windows sign-in stays on a Mac: it works there through Kerberos (a `kinit` ticket). The SQL wizard offers `sql` and `windows`, and the Mac `sql.json` template keeps the `windows` example and leaves out the `runas` ones. `runas` needs Windows' `LogonUser`.
* **UNC and printing were offered on a Mac.**
  * The UNC catalog reads only `\\server\share` and `X:\` paths, so `UncAccess.IsOffered` is now false on a Mac.
  * Printing had only the null spooler there, so `PrintOffered` is false and `/print` says it needs Windows.
  * `/tools` shows both groups, and Docker's, as *off: it needs Windows*.
* **Smaller differences.**
  * The prefer-native rule no longer sends zsh's or bash's `type` (which describes a command) to `read_file` on a Mac, and the prompt's rule no longer lists `type` or `dir` there (`NativeRedirect.For(command, offered, kind)`).
  * A skill resource spelled with `\` is read through its folders off Windows (`SkillCatalog.ReadResource`).
  * **A zip's entries are written sorted by path (ordinal)**, so the same folder makes the same zip on both systems. This changes Windows' zips too, on purpose.
  * The theme and welcome-splash tests that only need the splash to stay away run on a Mac: the fixture gives no splash off Windows. Four that draw a picture stay Windows-only.

* **The embedded LLM on Metal** (Stage 2, 2026-10-07). llama.cpp's macOS arm64 build (`b11258`, a 12 MB `.tar.gz` with its executable bits and `.dylib` links) is a Mac's one backend, `metal`. It arrives with an ad-hoc signature and no quarantine flag, so it runs as unpacked. The VRAM budget measures Metal's working set (10,922 MiB of 16 GiB on an M4). Checked on the published app on an M4 with 16 GB: Gemma 4 E2B UD-Q4_K_XL downloaded and connected, every layer on the GPU (41 of 41 with the drafter), a cold load in 21 s and a warm one in 2–4 s, replies at about 31 tokens/s (89 on a short one with the drafter), the prompt read at about 615–695 tokens/s, and a tool call answered. `/exit`, SIGTERM and `kill -9` of the app mid-reply each left no `llama-server` running; `pkill -9` of the app and its guard together left one, which the next start stopped.

**Open:**
* **Windows-only groups still have their rows on a Mac.** The UNC, Docker and camera tabs show their settings: the UNC wizard's `D:\Data` and runas wording, and Docker's pipe. Their groups are off, and `/tools` and the help say they need Windows.
* **The performance bar has no Mac source.** Only PROC has a reading; CPU, RAM, GPU and network need a macOS `IPerfSource`.
* **`/terminal` has no Mac opener.** One would be a new process-start site (`open -a Terminal`), a design call.
* **The tests use a Keychain key of their own** on a Mac (`NeonSidekick.Tests` / `master-key`), so a test run never makes or reads the app's (`NeonSidekick` / `master-key`). Before that change a run made the app's key when there was none, trusting `dotnet`; the app still reads such a key after its Keychain prompt. The tests' item can be deleted in Keychain Access at any time.
* **Sound.** Speech and the microphone need Windows for now (`AudioSupport.Available`: WinMM is the only backend), so Kokoro never speaks on a Mac. When a macOS sound backend comes, check that quitting after Kokoro has spoken still exits cleanly: the exit-crash fix releases ONNX Runtime's environment, but has only been seen with no session alive.

**Checking a change in Terminal.app or iTerm2.** A small pseudo-terminal relay can run the app in a terminal window, type and click into it from a script (an SGR mouse report is just bytes), and log every write and the terminal's answers with timestamps; `screencapture -l <window id>` (or `-R` with the window's bounds) then shows what the terminal actually drew. That is how the blank-screen bug was found: the timestamps showed the cursor question leaving 500 ms late. A glyph's width is measured the same way: print it, ask for the cursor (`ESC[6n`) and compare columns — on the alternate screen (`ESC[?1049h`) when it is the app's width that matters, since iTerm2 answers differently there. The capture needs the screen-recording permission for the terminal running the script.

## Troubleshooting

* **"'vswhere.exe' is not recognized" during the publish:** the linker can't find Visual Studio. Add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` and run it again.
* **"The file is locked" during the publish:** `NeonSidekick.exe` is still running from `publish\output\`. Close it first.
* **The build fails with a warning:** that's the zero-warning rule. Fix the warning; don't silence it project-wide.
* **On a Mac, `ld: library 'ssl' not found` at the end of the publish:** the SDK is Homebrew's. See [Setting up the Mac](#setting-up-the-mac).
* **On a Mac, `pwsh: command not found` after installing it as a .NET tool:** `~/.dotnet/tools` isn't on `PATH`. Add it in `~/.zshrc`.
