# Building from source

How to build, test and publish Neon Sidekick yourself. Back to the [README](../README.md).

## What you need

* **Windows x64** for the Windows build, or an **Apple Silicon Mac** for the Mac build: NativeAOT builds only for the OS it runs on. See [Building on a Mac](#building-on-a-mac).
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

`build.ps1` is the whole pipeline in one script. CI runs the same script, so a local run matches it.

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
* **Smoke test.** After publishing, the script runs the native exe with `--smoke` to check that every native library loads and works. `dotnet build` and `dotnet test` can't catch a missing native DLL; this can.

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

The Mac build is for Apple Silicon (`osx-arm64`). Each release has a Mac package (`NeonSidekick-v<version>-osx-arm64.tar.gz`), so you only need to build it for a change of your own. Everything works except Docker servers; see [The Mac build](#the-mac-build) for what differs.

NativeAOT can't build a Mac binary from Windows, so build on the Mac itself. You need:

* **Microsoft's [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** for macOS Arm64, not Homebrew's (see below).
* **The Xcode command line tools** (`xcode-select --install`) for the native link.
* **PowerShell 7** to run `build.ps1`, installed as a .NET tool (see below).

### Setting up the Mac

Homebrew's `dotnet` can't publish NativeAOT: the publish fails at the very end with `ld: library 'ssl' not found`. Homebrew's `powershell` depends on it, so both have to go:

```sh
brew uninstall powershell dotnet          # if they are installed
brew install --cask dotnet-sdk            # Microsoft's SDK, in /usr/local/share/dotnet (asks for your password)
dotnet tool install --global PowerShell   # pwsh, in ~/.dotnet/tools
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.zshrc
```

The `~/.zshrc` line is needed because the SDK's installer adds `~/.dotnet/tools` with a literal `~`, which zsh doesn't expand. In a new shell, check:

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

`-Runtime` defaults to `osx-arm64` on a Mac. The script makes the bundled `espeak-ng` executable and gives it and the app an ad-hoc signature. The package is a `.tar.gz` so the executable bits survive, and it leaves out what a Mac never needs: the debug symbols (`NeonSidekick.dSYM`, kept in `publish/output` for crash reports) and the other platforms' native files.

A downloaded build isn't notarized, so macOS quarantines it. Clear that once after unpacking: `xattr -dr com.apple.quarantine NeonSidekick-v<version>-osx-arm64`.

## The Mac build

### What works

| Area | On a Mac | Notes |
|---|---|---|
| Chat, tools, MCP, git, sessions, skills, headless | Works | The shell is zsh; `bash` and `pwsh` can be picked. |
| SQL Server, Oracle, MySQL, PostgreSQL and SQLite tools | Works | SQL Server's Windows sign-in is Kerberos only ([below](#sql-server-with-windows-authentication-kerberos)); `runas` is a Kerberos sign-in as `name@REALM` with its stored password ([below](#another-account-runas)). A named-pipe (`np:`) server is a connect error. |
| Embedded LLM | Works (Metal) | The GPU may use about two thirds of the Mac's memory (10.7 GB of 16 GB). The NVFP4 models (NVIDIA only) are left out. |
| Pictures: dropped in, pasted, `image_info`, `image_edit` | Works (ImageIO) | Reads HEIC/HEIF, TIFF and AVIF too; writes PNG, JPEG, GIF, BMP, TIFF and HEIF (no JPEG XL); refuses `chroma`, `colors` and `dither`. |
| Picture viewer, thumbnail browser, picture menu, log and process windows | Works (AppKit) | Need the desktop: over SSH a double-clicked picture opens in Preview. |
| Voice: Kokoro, Whisper (on Metal), push-to-talk, wake word, interrupt | Works (Core Audio) | The microphone permission is the terminal app's. |
| Camera | Works (macOS 14+) | The built-in camera, a USB webcam, or an iPhone as Continuity Camera. The permission is the terminal app's. |
| Screen capture | Works (macOS 14+) | Needs the terminal app's Screen Recording permission (takes effect after reopening the terminal). Windows on another Space, or minimized, aren't listed. |
| YouTube playback | Works (macOS 14+, WebKit) | Its data is kept under `~/Library/WebKit/NeonSidekick/WebsiteDataStore/`. |
| Performance bar | Works | VRAM reads as GMEM: the GPU's share of the Mac's memory. |
| `/terminal` | Works | A new Terminal window, or a new tab in iTerm2. |
| `/shortcut` | Works | A `.command` file on the Desktop with the app's icon, opening in iTerm2 or Terminal. |
| Printing | Works (CUPS) | Drawn into a PDF in Helvetica Neue and Menlo on the printer's paper; a PDF goes as it is; an Office file is refused. See [Printing](TOOLS.md#printing). |
| Docker tools and `/docker` | Works (Unix socket) | Docker Desktop, OrbStack, Colima or Rancher Desktop, found the way the `docker` command finds them ([Docker on a Mac](TOOLS.md#docker-on-a-mac)). Stats are the Linux VM's. |
| UNC shares and outside folders | Works (NetFS) | A share is `smb://server/share` (`\\server\share` works too); the app mounts it under `/Volumes`, hidden from Finder, and unmounts it at exit ([UNC shares on a Mac](TOOLS.md#unc-shares-on-a-mac)). |
| Docker servers | Left out | Docker Desktop on a Mac passes no GPU to containers; the embedded model on Metal is the Mac's local server. |
| `/keycheck` | Works (Carbon) | Finds macOS's own shortcuts, apps' exclusive hotkeys and the terminal's Option key; most apps' shared hotkeys never show. |
| `convert_to_pdf` and `/pdf` | Works, browser only | Edge, Chrome, Brave or Chromium from `/Applications`; the `printer` engine (Microsoft Print to PDF) needs Windows. |

### What differs from Windows

* **Secrets go in the Keychain.** API keys and database passwords are encrypted with a key the app keeps in your login Keychain (the *NeonSidekick* item). A `passwordStore: credman` password is a Keychain item you make with `security add-generic-password -s <target> -a <user> -w`. After each new build macOS asks once whether the app may use its key; choose **Always Allow**.
* **Shell and scripts.** `run_command` runs in zsh unless you pick `bash` or `powershell` (pwsh) under *Shell default*. Scripts run with `python3`, `node` or `pwsh`.
* **Paths.** `/cwd` completes a path that starts with `/`; network volumes aren't listed, so typing never waits on a server. `/cwd ~` is the default folder, not the home folder. Dot-files show in listings and searches; only `chflags hidden` items and `.git` are skipped.
* **The embedded LLM.** Pick a model in `/settings` › Servers › Embedded › Embedded models; the app downloads llama.cpp's macOS build and the model and runs it on the GPU. A 16 GB Mac suits the E2B and E4B models best. The server stops when the app ends; one left running by a crash is stopped at the next start.
* **The app's windows** have no Dock icon or menu bar, but take the keyboard when they open. Tab gives it back to the terminal, and so does closing the last window. The viewer adds ⌫ for Del, ⌘W to close and ⌃⌘F for full screen (macOS keeps F11 for Show Desktop); see [Picture viewer](COMMANDS.md#picture-viewer). A right-click (or Control-click) opens the picture menu, with *Show in Finder*. `/log`'s and `/process`'s windows support ⌘A and ⌘C.
* **Printing** goes through CUPS. Pages are laid out as on Windows but in Helvetica Neue and Menlo, so a printout can wrap and paginate a little differently. The paper is the printer's default (Letter or A4 by region if it won't say). A PDF is sent as it is; Word, Excel and other files are refused, since only the program that made them can print them.
* **Thumbnails and the splash** are drawn in half blocks: full colour in iTerm2, 256 colours (banded) in Terminal.app.
* **Permissions belong to the terminal app** (Terminal or iTerm2), not to NeonSidekick. macOS asks the first time the microphone, camera or screen is used, and the answer covers everything run in that terminal. Change a refusal in System Settings › Privacy & Security (Microphone, Camera, or Screen & System Audio Recording). To be asked again, quit the app and reset the terminal's answer: `tccutil reset Microphone com.apple.Terminal` (or `Camera`, `ScreenCapture`; `com.googlecode.iterm2` for iTerm2).
* **A microphone that sends only silence** gets one warning in the transcript. It's either a refused permission or a MacBook's microphone with the lid closed; pick another input in System Settings › Sound, or open the lid. A MacBook's camera is off with the lid closed too.
* **Echo.** The interrupt (saying the wake phrase over a reply) works with headphones and with a MacBook's own speakers and microphone; *STT interrupt echo guard* keeps the reply's own words from counting.
* **YouTube.** ⌃⌘F is full screen (F11 too, if macOS's Show Desktop shortcut is off), Esc or ⌘W closes the window, and a link in the player opens your default browser. Delete the data folder above to clear YouTube's cache and storage.
* **Copy and paste.** Cmd+V is the terminal's paste, text only. The app's own paste is **Ctrl+V**, **Option+V** (with Option as Meta, below) or a right-click on the input line: it takes a picture off the clipboard first (e.g. a screenshot copied with ⌃⇧⌘4), else the text. A file copied in Finder pastes as its path, so a picture file is attached as if dropped.
* **Chords a Mac terminal can't send** have stand-ins: Ctrl+D for `/model`, Ctrl+] for `/terminal`, Ctrl+Option+A for `/memory`, and Option+Delete deletes a word. See [Keyboard shortcuts](SETTINGS.md#keyboard-shortcuts).
* **Editors.** `/sys persona` and similar commands open the file in its default app. For `/draft`, set *Draft editor* to a command that waits, such as `code --wait` or `open -W -t`; a terminal editor like vim can't share the terminal with the app.
* **Terminals.** Terminal.app and iTerm2 both work. For the Ctrl+Alt shortcuts (Ctrl+Option on a Mac), let Option act as Alt: *Use Option as Meta key* in Terminal.app (Settings → Profiles → Keyboard), *Left Option key: Esc+* in iTerm2 (Settings → Profiles → Keys). Option then no longer types characters such as å in that profile (iTerm2's right Option still can). To select text with the mouse while the app has it, hold Fn (Terminal.app) or Option (iTerm2), or press Cmd+R in Terminal.app to toggle the app's mouse.

### SQL Server with Windows Authentication (Kerberos)

A connection with `"auth": "windows"` in `sql.json` signs in with your Active Directory account, with no password in the file. On Windows that's Kerberos with an NTLM fallback. On a Mac it is **Kerberos only**, so you need a Kerberos ticket from `kinit` (built into macOS). It can't work against a SQL Server that isn't in a domain, such as one in a Docker container; use a SQL login (`"auth": "sql"`) there.

1. **Find the realm of the server's domain**, in capitals (e.g. `AD.EXAMPLE.EDU`). On a Windows PC on the domain: `$env:USERDNSDOMAIN` in PowerShell (`echo %USERDNSDOMAIN%` in cmd); on a Mac bound to AD: `dsconfigad -show` (*Active Directory Domain*); or ask IT. Your sign-in name (`whoami /upn`, e.g. `jdoe@example.edu`) may have a different suffix: the realm is the domain's own name. For a server in another domain, see [A server in another domain](#a-server-in-another-domain).
2. **Get a ticket**, on the organization's network or VPN: `kinit jdoe@AD.EXAMPLE.EDU` asks for the domain password and prints nothing when it works. Type the realm in capitals: in lowercase `kinit` may succeed but leave a ticket the connection can't use (`Matching credential … not found`). `kinit --keychain …` keeps the password in your Keychain so later runs don't ask. A plain `kinit` with no name fails with `Configuration file does not specify default realm` unless `/etc/krb5.conf` names one (`[libdefaults]` / `default_realm = AD.EXAMPLE.EDU`).
3. **Check it:** `klist` shows `krbtgt/AD.EXAMPLE.EDU@AD.EXAMPLE.EDU` and its expiry, usually about 10 hours away.
4. **Try the connection:** `./publish/output/NeonSidekick --sql-check <connection>`. It reports the account it signed in as (`AD\jdoe`). Afterwards `klist` also lists the server's ticket, `MSSQLSvc/<server>:<port>`.

Put the server in `sql.json` by its full name, as `host`, `host,port` or `host\instance,port`. The ticket is asked for under that name and port (`MSSQLSvc/sqlhost.ad.example.edu:1451`), so use the name the server is registered under, never an IP address or an alias.

#### A server in another domain

An organization may have several AD domains, such as one for people's accounts and one for test servers. A ticket from your domain only reaches servers in that domain or one that trusts it. Against a server in a domain with no trust, the connection fails with `The target principal name is incorrect. Cannot generate SSPI context.`, even though `kinit` worked. (A Windows PC may still connect, because it falls back to NTLM.)

To see which domain a server is in, and whether you can get a ticket for it:

```sh
dig +short SRV _kerberos._tcp.test.example.edu                               # that domain's domain controllers, if it is one
kgetcred MSSQLSvc/sqlhost.test.example.edu:1451@AD.EXAMPLE.EDU               # a ticket from your own domain
kgetcred MSSQLSvc/sqlhost.test.example.edu:1451@TEST.EXAMPLE.EDU             # a ticket from the server's domain
```

`kgetcred` prints nothing when the ticket is issued (`klist` then lists it). `Server (…) unknown` means that domain has no such name; `Server (krbtgt/TEST.EXAMPLE.EDU@AD.EXAMPLE.EDU) unknown` means your domain has no trust with the server's. Then sign in with an account in the server's domain if you have one (`kinit jdoe-test@TEST.EXAMPLE.EDU`), or ask the server's DBA for a SQL login.

macOS keeps one ticket cache per account; the newest `kinit` becomes the default, and the connection uses the default. `klist -A` lists them all, and `kswitch -p jdoe@AD.EXAMPLE.EDU` makes another the default.

Day to day:
* Tickets expire, and the connection fails until you run `kinit` again; `kinit -R` renews a still-valid ticket without the password, if the domain allows it.
* `kdestroy` throws away the default cache's tickets (`kdestroy -p <principal>` one account's, `kdestroy -A` all).
* The **Ticket Viewer** app (`/System/Library/CoreServices/Ticket Viewer.app`) does the same as `kinit`, `klist` and `kdestroy`.

If it fails:
* **`Cannot find KDC for realm`:** the Mac can't reach a domain controller. Check the network or VPN, and that the realm is spelled right and in capitals. `dig +short SRV _kerberos._tcp.ad.example.edu` should list the domain controllers; if it lists none, the realm is wrong or the domain's DNS is only reachable on its network, and IT can give you an `/etc/krb5.conf` naming the domain controllers.
* **`Clock skew too great`:** the clock is more than 5 minutes off. Turn on *Set time and date automatically* (System Settings → General → Date & Time).
* **`Matching credential … not found`:** the ticket was taken with the realm in lowercase. Run `kdestroy -p <principal>`, then `kinit` again with it in capitals.
* **`The target principal name is incorrect. Cannot generate SSPI context.`:** no ticket could be had for the server's name. Either the server is in a domain yours doesn't trust ([A server in another domain](#a-server-in-another-domain)), or its Kerberos name (SPN) isn't registered for that host and port, which the server's admin must fix. `kgetcred` above tells the two apart.

On the server, `sys.dm_exec_connections.auth_scheme` shows `KERBEROS` for such a connection (`NTLM` for a Windows client that fell back).

#### Another account (runas)

A connection with `"auth": "runas"` signs in as another account, with that account's password kept by the app: in the macOS Keychain (`"passwordStore": "credman"`, set with *SQL set password* or `security add-generic-password -s NeonSidekick/sql/<connection> -a <account> -w`) or encrypted in `sql.json`. On Windows that's a `runas /netonly` sign-in; on a Mac the app gets the account's Kerberos ticket itself at each connect, so no `kinit` is needed and tickets never expire on you.

* **The account is `name@REALM`**, e.g. `svc-reader@AD.EXAMPLE.EDU`. A Windows-style `DOMAIN\name` is refused on a Mac, with the form to use. The app puts the realm in capitals.
* **It is Kerberos only**, with no NTLM fallback; everything above about realms, domain trust and the server's name applies.
* **macOS keeps the ticket** in a cache of its own beside yours: `klist -l` lists it until it expires (`kdestroy -p <account>` removes it). It doesn't become the default, so your own ticket and your `"auth": "windows"` connections are untouched. When the `runas` account *is* your own, each connect renews your ticket.
* **If it fails,** the app says `the Kerberos sign-in as <account> failed (<status>)`. The status doesn't tell a wrong password from an account the server won't take, so check both. A refusal from the server itself (`Login failed for user …`) reads as for any connection.

### Known issues

On a Mac:
* **YouTube may ask for consent again each run.** WebKit blocks the embedded player's third-party cookies, so a consent given inside the player isn't kept.
* **Switching the audio output mid-reply (AirPods) is untested.** The next reply plays on the new default output.
* **With Bluetooth headphones the microphone may open on the last word**, since they play a fifth of a second or so behind.
* **A large picture is decoded whole**: one at the 40-megapixel cap takes about 160 MB for a moment.
* **Screen capture on a 1× screen beside a Retina one, and on macOS 14.0–15.1**, is covered by the tests only.

On both:
* **An unplugged camera freezes `/camera live` silently**: the window stays on its last frame, with nothing in the transcript, until `/camera off`.
* **The camera pane's status line lags a key**: "The camera is opening…" stays until the next key after the camera is on.
* **`seek` and `volume` on a paused video answer with the old value**; the change itself happens, and the next report shows it.

### Notes for contributors

* `pwsh ./build.ps1 -TestOnly` runs the suite with the Windows-only tests skipped. Mark a test whose subject is Windows-only `[WindowsFact]`/`[WindowsTheory]`; a test of a portable feature written with Windows paths or `cmd` lines gets a Unix twin (`…_Unix`, `[UnixFact]`).
* The tests need the Xcode tools' `/usr/bin/python3`.
* No print test reaches paper: the Mac tests and the smoke check draw PDFs and read them back, and the live tests (`NEONSIDEKICK_TEST_PRINTER=<queue>`) send jobs held with `job-hold-until=indefinite` and cancel them.
* The tests use a Keychain item of their own (`NeonSidekick.Tests` / `master-key`), never the app's; you can delete it in Keychain Access at any time.
* To see what Terminal.app or iTerm2 actually drew, `screencapture -l <window id>` captures the terminal's window (the script's terminal needs the Screen Recording permission). To check which app has the keyboard, ask `NSWorkspace.shared.frontmostApplication`; System Events' `frontmost` never names an accessory app like this one.

## Troubleshooting

* **"'vswhere.exe' is not recognized" during the publish:** the linker can't find Visual Studio. Add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` and run it again.
* **"The file is locked" during the publish:** `NeonSidekick.exe` is still running from `publish\output\`. Close it first.
* **The build fails with a warning:** that's the zero-warning rule. Fix the warning; don't silence it project-wide.
* **On a Mac, `ld: library 'ssl' not found` at the end of the publish:** the SDK is Homebrew's. See [Setting up the Mac](#setting-up-the-mac).
* **On a Mac, `pwsh: command not found` after installing it as a .NET tool:** `~/.dotnet/tools` isn't on `PATH`. Add it in `~/.zshrc`.
