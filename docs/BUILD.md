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
| `.\build.ps1 -Package` | The full run, then the release package (a `.zip` on Windows, a `.tar.gz` on a Mac) with the docs and its `.sha256` in `publish\package\`. |
| `.\build.ps1 -Package -Tag v1.2.3` | The same, refusing a tag that isn't the project's `<Version>`. |
| `.\build.ps1 -CoverageFloor 0` | Reports coverage without failing under the floor (80% by default). |
| `.\build.ps1 -Clean` | Deletes `bin\`, `obj\` and `publish\`. |
| `.\build.ps1 -Runtime <rid>` | Picks the runtime: `win-x64` by default, `osx-arm64` on an Apple Silicon Mac. NativeAOT can't publish for another OS. |

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

A preview build for Apple Silicon Macs (`osx-arm64`). Each release since v0.5.0 has its package (`NeonSidekick-v<version>-osx-arm64.tar.gz`, built on GitHub's Mac runner), so building it yourself is only needed for a change of your own. It leaves out the features that have no macOS backend yet: printing, Docker (the tools, `/docker` and Docker servers), UNC shares, `/shortcut` and `/keycheck`. Everything else works; see [The Mac build](#the-mac-build) for what differs.

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
pwsh ./build.ps1 -TestOnly     # build and test: the Windows-only tests skipped
pwsh ./build.ps1 -Publish      # publish/output/NeonSidekick, smoke-tested
pwsh ./build.ps1 -Package      # publish/package/NeonSidekick-v<version>-osx-arm64.tar.gz
```

The publish prints trim and AOT warnings from Oracle, SqlClient, NumSharp and Whisper.net, and `ILC: Method … will always throw` lines for Oracle's optional cloud assemblies. The Windows publish prints them too; they don't fail the build.

`-Runtime` defaults to `osx-arm64` on a Mac. The script marks the bundled `espeak-ng` executable as runnable and gives it and the app an ad-hoc signature. The package is a `.tar.gz` so the executable bits survive. It leaves out what a Mac never runs: the debug symbols (`NeonSidekick.dSYM`, kept in `publish/output` for reading a crash report), the Windows ONNX Runtime dlls and the other platforms' espeak-ng.

A downloaded build isn't notarized, so macOS quarantines it. Clear that once after unpacking: `xattr -dr com.apple.quarantine NeonSidekick-v<version>-osx-arm64`.

## The Mac build

### What works

| Area | On a Mac | Notes |
|---|---|---|
| Chat, tools, MCP, git, sessions, skills, headless | Works | The shell is zsh; `bash` and `pwsh` can be picked. |
| SQL Server, Oracle, MySQL, PostgreSQL and SQLite tools | Works | SQL Server's Windows sign-in is Kerberos only ([below](#sql-server-with-windows-authentication-kerberos)); `runas` needs Windows; a named-pipe (`np:`) server is a connect error. |
| Embedded LLM | Works (Metal) | llama.cpp's macOS build (12 MB); the GPU may use about two thirds of the Mac's memory (10.7 GB of 16 GB). The NVFP4 builds (NVIDIA only) are left out. |
| Pictures: dropped in, pasted, `image_info`, `image_edit` | Works (ImageIO) | Reads HEIC/HEIF, TIFF and AVIF too; writes PNG, JPEG, GIF, BMP, TIFF and HEIF (no JPEG XL); refuses `chroma`, `colors` and `dither`. |
| Picture viewer, thumbnail browser, picture menu, log and process windows | Works (AppKit) | Need the desktop: over SSH a double-clicked picture opens in Preview. |
| Voice: Kokoro, Whisper (on Metal), push-to-talk, wake word, interrupt | Works (Core Audio) | The microphone permission is the terminal app's. |
| Camera | Works (macOS 14+) | The built-in camera, a USB webcam, or an iPhone as Continuity Camera. The permission is the terminal app's. |
| Screen capture | Works (macOS 14+) | Needs the terminal app's Screen Recording permission, applied after the terminal is reopened. A window on another Space, or minimized, isn't listed. |
| YouTube playback | Works (macOS 14+, WebKit) | Its data is kept per home under `~/Library/WebKit/NeonSidekick/WebsiteDataStore/`. |
| Performance bar | Works | VRAM reads as GMEM: the GPU's share of the Mac's memory. |
| `/terminal` | Works | A new Terminal window, or a new tab in iTerm2. |
| Printing, Docker, UNC shares, `/shortcut`, `/keycheck` | Left out | `/tools` shows the groups as *off: it needs Windows*; the UNC and Docker tabs still show their rows. |
| `convert_to_pdf` and `/pdf` | Works, browser only | Edge, Chrome, Brave or Chromium from `/Applications`; the `printer` engine needs Windows. |

### What differs from Windows

* **Secrets go in the Keychain.** API keys and database passwords are encrypted with a key the app keeps in your login Keychain (the *NeonSidekick* item), and a `passwordStore: credman` password is a Keychain item you make with `security add-generic-password -s <target> -a <user> -w`. After each new build macOS asks once whether it may use that key; choose **Always Allow**.
* **Shell and scripts.** `run_command` runs in zsh unless you pick `bash` or `powershell` (pwsh) under *Shell default*. Scripts run with `python3`, `node` or `pwsh`.
* **Paths.** `/cwd` completes a path that starts with `/`; network volumes list nothing, so typing never waits on a server. `/cwd ~` is the default folder, not the home folder. Dot-files show in listings and searches; only `chflags hidden` items and `.git` are skipped.
* **The embedded LLM.** Pick a model in `/settings` › Embedded › Embedded models; the app downloads llama.cpp's macOS build and the model and runs it on the GPU. A 16 GB Mac suits the E2B and E4B models best. The server stops when the app ends, however it ends; one a crash left running is stopped at the next start.
* **The app's windows** show no Dock icon and no menu bar, yet take the keyboard when they open. Tab gives it back to the terminal app that started NeonSidekick, and closing the last window does too. The viewer's keys are Windows' plus ⌫ for Del, ⌘W to close and ⌃⌘F for full screen (macOS keeps F11 for Show Desktop); see [Picture viewer](COMMANDS.md#picture-viewer). A right-click (or Control-click) opens the picture menu as the Mac's own menu, with *Show in Finder* and no *Print*. `/log`'s and `/process`'s windows are the system's text view, with ⌘A and ⌘C.
* **Thumbnails and the splash** are half blocks: iTerm2 draws them in full colour, Terminal.app (macOS 15) in 256 colours, so they look banded there.
* **Permissions belong to the terminal app** (Terminal or iTerm2), not to NeonSidekick: macOS asks the first time the microphone, the camera or the screen is used, and the answer covers everything run in that terminal. A refusal is changed in System Settings › Privacy & Security (Microphone, Camera, or Screen & System Audio Recording). To be asked again, quit the app and reset that terminal's answer: `tccutil reset Microphone com.apple.Terminal` (or `Camera`, `ScreenCapture`; `com.googlecode.iterm2` for iTerm2).
* **A microphone that sends only silence** gets one warning in the transcript: a refused permission, or a MacBook's own microphone with the lid closed. Pick another input in System Settings › Sound, or open the lid. A MacBook's own camera is off with the lid closed too.
* **Echo.** The interrupt (saying the wake phrase over a reply) works with headphones and on a MacBook's own speakers and microphone; the echo guard (*STT interrupt echo guard*) keeps the reply's own words from counting.
* **YouTube.** ⌃⌘F is full screen (F11 too, if macOS's Show Desktop shortcut is off), Esc or ⌘W closes the window, and a link in the player opens your default browser. Delete the data folder above to forget YouTube's cache and storage.
* **Copy and paste.** Cmd+V is the terminal's paste: text only, so a copied picture pastes nothing there. The app's own paste is **Ctrl+V**, **Option+V** (with Option as Meta, below) or a right-click on the input line: it takes a picture off the clipboard first (a screenshot copied with ⌃⇧⌘4, a picture copied in Preview or a browser), else the text. A file copied in Finder pastes as its path, so a picture file is attached by its name, as if dropped.
* **Chords a Mac terminal can't send** have stand-ins: Ctrl+D for `/model`, Ctrl+] for `/terminal`, Ctrl+Option+A for `/memory`, and Option+Delete deletes a word. See [Keyboard shortcuts](SETTINGS.md#keyboard-shortcuts).
* **Editors.** `/persona` and friends open the file in its default app. For `/draft`, set *Draft editor* to a command that waits, such as `code --wait` or `open -W -t`; a terminal editor like vim can't share the terminal with the app.
* **Terminals.** Terminal.app and iTerm2 both work. For the Ctrl+Alt shortcuts (Ctrl+Option on a Mac), let Option act as Alt: *Use Option as Meta key* in Terminal.app (Settings → Profiles → Keyboard), *Left Option key: Esc+* in iTerm2 (Settings → Profiles → Keys); Option then no longer types characters such as å in that profile (iTerm2's right Option still can). Hold Fn (Terminal.app) or Option (iTerm2) to select text with the mouse while the app has it, or press Cmd+R in Terminal.app to turn the app's mouse off and on.

### SQL Server with Windows Authentication (Kerberos)

A connection with `"auth": "windows"` in `sql.json` uses Windows Authentication, also called integrated security: an Active Directory account and no password in the file. On Windows that sign-in is Kerberos, falling back to NTLM when Kerberos fails. On a Mac it is **Kerberos only**: SqlClient has no NTLM fallback there, so the Mac needs a Kerberos ticket from `kinit`, which comes with macOS. It can't work against a SQL Server that isn't in a domain, such as one in a Docker container; use a SQL login (`"auth": "sql"`) there.

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

### Known issues

On a Mac:
* **YouTube may ask for consent again each run.** WebKit blocks third-party cookies, and the embedded player is a third party to the app's page, so a consent asked inside the player isn't kept.
* **Switching the output mid-reply (AirPods) is untested.** The next reply plays on the new default output; what happens when headphones connect or disconnect in the middle of one is unknown.
* **With Bluetooth headphones the microphone may open on the last word**, since they play a fifth of a second or so behind.
* **A large picture is decoded whole**: one at the 40-megapixel cap takes about 160 MB for a moment.
* **Screen capture on a 1× screen beside a Retina one, and on macOS 14.0–15.1**, is covered by the tests only.

On both:
* **An unplugged camera freezes `/camera live` silently**: the window stays on its last frame, with nothing in the transcript, until `/camera off`.
* **The camera pane's status line lags a key**: "The camera is opening…" stays until the next key after the camera is on.
* **`seek` and `volume` on a paused video answer with the old value**; the change itself happens, and the next report shows it.

### Notes for contributors

* `pwsh ./build.ps1 -TestOnly` runs the suite with the Windows-only tests skipped. A test whose subject is Windows-only is marked `[WindowsFact]`/`[WindowsTheory]`; a test of a portable feature spelled with Windows paths or `cmd` lines gets a Unix twin (`…_Unix`, `[UnixFact]`).
* The tests need the Xcode tools' `/usr/bin/python3`.
* The tests use a Keychain item of their own (`NeonSidekick.Tests` / `master-key`), never the app's; it can be deleted in Keychain Access at any time.
* To see what Terminal.app or iTerm2 actually drew, `screencapture -l <window id>` captures the terminal's window (the script's terminal needs the Screen Recording permission). To check which app has the keyboard, ask `NSWorkspace.shared.frontmostApplication`: System Events' `frontmost` never names an accessory app like this one.

## Troubleshooting

* **"'vswhere.exe' is not recognized" during the publish:** the linker can't find Visual Studio. Add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` and run it again.
* **"The file is locked" during the publish:** `NeonSidekick.exe` is still running from `publish\output\`. Close it first.
* **The build fails with a warning:** that's the zero-warning rule. Fix the warning; don't silence it project-wide.
* **On a Mac, `ld: library 'ssl' not found` at the end of the publish:** the SDK is Homebrew's. See [Setting up the Mac](#setting-up-the-mac).
* **On a Mac, `pwsh: command not found` after installing it as a .NET tool:** `~/.dotnet/tools` isn't on `PATH`. Add it in `~/.zshrc`.
